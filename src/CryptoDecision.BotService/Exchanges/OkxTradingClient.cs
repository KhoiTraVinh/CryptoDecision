namespace CryptoDecision.BotService.Exchanges;

/// <summary>
/// What an order actually did, once the exchange has settled it. Every figure
/// here is read back from OKX rather than assumed from the request — a market
/// order's price and its fee are both unknown until it fills.
/// </summary>
/// <param name="FilledContracts">Contracts filled. Multiply by ctVal for base units.</param>
/// <param name="FeeAbs">Fee charged, unsigned. Quote currency (USDT) on a linear perp.</param>
public sealed record OkxFill(
    string   OrdId,
    decimal  FilledContracts,
    decimal  AveragePrice,
    decimal  FeeAbs,
    string?  FeeCcy,
    bool     FullyFilled
);

/// <summary>
/// Order placement and account reads against OKX USDT-margined perpetual swaps.
///
/// Three things differ from spot in ways that reach the money, and every method
/// here is shaped by them:
///
///  - <b>Sizes are contracts, not coins.</b> One BTC-USDT-SWAP contract is 0.01
///    BTC. Sending a base quantity where OKX expects contracts is off by a factor
///    of a hundred, in the direction of a position a hundred times too large.
///  - <b>Closing is reduceOnly, not selling.</b> There is no coin balance to sell;
///    a plain opposite-side order would open a position the other way instead of
///    closing this one. reduceOnly is what makes an exit an exit.
///  - <b>posSide depends on account configuration.</b> Hedge mode requires it,
///    net mode rejects it. It is read once from the account rather than assumed,
///    because guessing wrong has every order refused.
/// </summary>
public sealed class OkxTradingClient(
    OkxSignedClient client,
    OkxOptions      opts,
    ILogger<OkxTradingClient> log)
{
    private OkxAccountConfig? _accountConfig;
    private readonly SemaphoreSlim _configGate = new(1, 1);
    private readonly HashSet<string> _leverageSet = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Null until the account config has been read; then whether this key may place
    /// orders. Exposed so the engine's synchronous refusal check can consult it
    /// without an API call, which is what lets the bot refuse to start on a
    /// read-only key rather than discovering it one signal at a time.
    /// </summary>
    public bool? CanTrade => _accountConfig?.CanTrade;

    // ── Account ───────────────────────────────────────────────────────────────

    /// <summary>Free margin balance of one currency — what a new position may commit.</summary>
    public async Task<decimal> GetAvailableAsync(string currency, CancellationToken ct)
    {
        var accounts = await client.GetPrivateAsync<OkxBalanceResponse>(
            $"/api/v5/account/balance?ccy={currency}", ct);

        var detail = accounts
            .FirstOrDefault()?.Details?
            .FirstOrDefault(d => string.Equals(d.Ccy, currency, StringComparison.OrdinalIgnoreCase));

        return detail?.Available ?? 0m;
    }

    /// <summary>
    /// Account trading configuration, fetched once. Read for posMode, which decides
    /// whether orders must name a position side.
    /// </summary>
    public async Task<OkxAccountConfig> GetAccountConfigAsync(CancellationToken ct)
    {
        if (_accountConfig is not null) return _accountConfig;

        await _configGate.WaitAsync(ct);
        try
        {
            if (_accountConfig is not null) return _accountConfig;

            var configs = await client.GetPrivateAsync<OkxAccountConfig>("/api/v5/account/config", ct);
            _accountConfig = configs.FirstOrDefault()
                ?? throw new OkxApiException("NO_ACCOUNT_CONFIG",
                    "OKX returned no account configuration, so position mode is unknown.");

            log.LogInformation(
                "[OKX] Account config: posMode={PosMode} acctLv={Level} perm={Perm} " +
                "(hedge mode: {Hedge}, can trade: {CanTrade})",
                _accountConfig.PosMode, _accountConfig.AccountLevel, _accountConfig.Permissions,
                _accountConfig.IsHedgeMode, _accountConfig.CanTrade);

            return _accountConfig;
        }
        finally
        {
            _configGate.Release();
        }
    }

    /// <summary>
    /// Set leverage for an instrument, once per process.
    ///
    /// OKX keeps leverage as instrument state, not an order parameter, so it
    /// persists between sessions and from whatever was set last — possibly by hand,
    /// possibly much higher. Setting it explicitly before the first order is what
    /// makes the configured value the one actually in force.
    /// </summary>
    public async Task EnsureLeverageAsync(string instId, CancellationToken ct)
    {
        lock (_leverageSet)
            if (_leverageSet.Contains(instId)) return;

        var config = await GetAccountConfigAsync(ct);

        // Isolated margin in hedge mode keeps leverage per position side, so the
        // request is rejected outright without posSide — verified against the live
        // account: HTTP 400 without it, code 0 with it. Both sides are set, because
        // the strategy trades both and discovering the gap at the first SHORT signal
        // means losing that signal.
        //
        // Cross margin and net mode keep one leverage per instrument, and reject
        // posSide, so the field has to be absent rather than empty there.
        var needsPositionSide = config.IsHedgeMode
            && string.Equals(opts.MarginMode, "isolated", StringComparison.OrdinalIgnoreCase);

        var sides = needsPositionSide ? new[] { "long", "short" } : [null];

        foreach (var side in sides)
        {
            var payload = new Dictionary<string, object>
            {
                ["instId"]  = instId,
                ["lever"]   = OkxNum.Format(opts.Leverage),
                ["mgnMode"] = opts.MarginMode,
            };

            if (side is not null) payload["posSide"] = side;

            await client.PostPrivateAsync<OkxLeverageAck>("/api/v5/account/set-leverage", payload, ct);
        }

        lock (_leverageSet)
            _leverageSet.Add(instId);

        log.LogInformation(
            "[OKX] Leverage for {InstId} set to {Lever}x {Mode} margin{Sides}.",
            instId, opts.Leverage, opts.MarginMode,
            needsPositionSide ? " (long and short)" : "");
    }

    /// <summary>
    /// The open position on an instrument, or null when flat.
    ///
    /// For futures this — not a coin balance — is the answer to "do I still hold
    /// this". A position closed by an exchange-side stop leaves no trace in any
    /// balance the bot would otherwise check.
    /// </summary>
    /// <param name="positionSide">
    /// "long" or "short". Required, not optional: in hedge mode both sides exist on
    /// the same instrument at once, so "the position on SOL-USDT-SWAP" is not a
    /// question with one answer. Taking the first non-flat row meant closing a long
    /// could size itself from the short's contract count, or find the short and
    /// conclude the long was still open.
    /// </param>
    public async Task<OkxPosition?> GetPositionAsync(
        string instId, string positionSide, CancellationToken ct)
    {
        var positions = await client.GetPrivateAsync<OkxPosition>(
            $"/api/v5/account/positions?instType=SWAP&instId={instId}", ct);

        var config = await GetAccountConfigAsync(ct);

        // Net mode reports a single row with posSide "net" and a signed size, so the
        // side is carried by the sign rather than the field.
        if (!config.IsHedgeMode)
        {
            var net = positions.FirstOrDefault(p => !p.IsFlat);
            if (net is null) return null;

            var netIsLong = net.Contracts > 0m;
            return netIsLong == (positionSide == "long") ? net : null;
        }

        return positions.FirstOrDefault(p =>
            !p.IsFlat && string.Equals(p.PosSide, positionSide, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Realised P&amp;L of the most recent closed position on an instrument, as the
    /// exchange computed it, or null when it has no record.
    ///
    /// This exists because a position can leave without the bot placing the order
    /// that removed it — a liquidation, or a manual close. Settling that row from a
    /// reference price invents a number, and the invented one is always smaller than
    /// a liquidation's real loss, which is the direction that matters: the daily-loss
    /// circuit breaker sums these values, so understating them keeps a bot trading
    /// through exactly the run of losses the breaker exists to stop.
    /// </summary>
    public async Task<OkxPositionHistory?> GetLastClosedPositionAsync(
        string instId, string positionSide, CancellationToken ct)
    {
        var history = await client.GetPrivateAsync<OkxPositionHistory>(
            $"/api/v5/account/positions-history?instType=SWAP&instId={instId}&limit=10", ct);

        var config = await GetAccountConfigAsync(ct);

        return config.IsHedgeMode
            ? history.FirstOrDefault(h =>
                  string.Equals(h.PosSide, positionSide, StringComparison.OrdinalIgnoreCase))
            : history.FirstOrDefault();
    }

    // ── Order placement ───────────────────────────────────────────────────────

    /// <summary>
    /// Place a market order on a perpetual swap, sized in contracts, and return its
    /// exchange order id. Placement only — the fill is read back separately.
    /// </summary>
    /// <param name="side">"buy" or "sell" — the direction of the order, not of the position.</param>
    /// <param name="positionSide">"long"/"short" in hedge mode; null in net mode.</param>
    /// <param name="reduceOnly">True for an exit: the order may only shrink a position, never open one.</param>
    public async Task<string> PlaceSwapMarketOrderAsync(
        string instId, string side, string? positionSide, decimal contracts, bool reduceOnly,
        CancellationToken ct)
    {
        // clOrdId is OKX-constrained to alphanumerics, 1-32 characters. A hex GUID
        // fits, and giving the order an id of ours is what makes a timed-out
        // placement recoverable: the order can be looked up by client id instead of
        // being re-sent blind.
        var clientOrderId = "cd" + Guid.NewGuid().ToString("N")[..24];

        // Built as a dictionary because posSide must be absent in net mode, not
        // present-and-null — OKX rejects the field outright when posMode is net.
        var payload = new Dictionary<string, object>
        {
            ["instId"]  = instId,
            ["tdMode"]  = opts.MarginMode,
            ["side"]    = side,
            ["ordType"] = "market",
            ["sz"]      = OkxNum.Format(contracts),
            ["clOrdId"] = clientOrderId,
        };

        if (positionSide is not null) payload["posSide"]    = positionSide;
        if (reduceOnly)               payload["reduceOnly"] = true;

        var acks = await client.PostPrivateAsync<OkxOrderAck>("/api/v5/trade/order", payload, ct);

        var ack = acks.FirstOrDefault()
            ?? throw new OkxApiException("NO_ACK",
                $"OKX accepted the {side} request for {instId} but returned no order acknowledgement. " +
                $"The order may exist under clOrdId {clientOrderId} — check the exchange before retrying.");

        if (!ack.Accepted)
            throw new OkxApiException(ack.SCode ?? "UNKNOWN",
                $"OKX refused the {side} order on {instId}: sCode={ack.SCode} sMsg={ack.SMsg ?? "(none)"}");

        if (string.IsNullOrWhiteSpace(ack.OrdId))
            throw new OkxApiException("NO_ORDER_ID",
                $"OKX accepted the {side} order on {instId} without returning an ordId " +
                $"(clOrdId {clientOrderId}). Check the exchange before retrying.");

        log.LogInformation(
            "[OKX] Placed {Side} market order {OrdId} on {InstId} sz={Contracts} contracts " +
            "posSide={PosSide} reduceOnly={ReduceOnly} (clOrdId={ClOrdId}, demo={Demo})",
            side, ack.OrdId, instId, OkxNum.Format(contracts), positionSide ?? "(net)",
            reduceOnly, clientOrderId, opts.DemoTrading);

        return ack.OrdId!;
    }

    /// <summary>
    /// Outcome of trying to rest a post-only order on the book.
    ///
    /// A result rather than an exception, because the interesting failure is an
    /// ordinary one: a post-only order that would cross the spread is refused by the
    /// exchange by design, and that means "the book moved, try again next cycle" — not
    /// "something is broken". Throwing here would turn the normal case into an error
    /// log and bury the placements that genuinely failed.
    /// </summary>
    public sealed record PostOnlyPlacement(
        bool Placed, string? OrdId, string? RejectCode, string? RejectMessage)
    {
        public static PostOnlyPlacement Rested(string ordId) => new(true, ordId, null, null);
        public static PostOnlyPlacement Rejected(string? code, string? message) =>
            new(false, null, code ?? "UNKNOWN", message);
    }

    /// <summary>
    /// Rest a post-only limit order on the book at <paramref name="limitPrice"/>.
    ///
    /// Why post-only and not a plain limit
    /// -----------------------------------
    /// A plain limit order priced at the touch can still cross and pay the taker fee
    /// if the book moves between the quote and the placement. post_only is refused
    /// outright in that case, which is the guarantee being bought: either this order
    /// is a maker order or it does not exist. That matters because the whole point is
    /// the fee — taker is 5 bps a side on OKX perpetual swaps against 2 bps for maker,
    /// so a round trip goes from about 10 bps to about 4. Against a signal whose gross
    /// edge is small, that difference is most of the decision about whether the
    /// strategy can exist at all.
    ///
    /// The cost of it is fill probability: a resting order is not an entry until
    /// someone trades against it. That is an acceptable trade for a signal held for
    /// hours — missing an entry costs an opportunity, while paying 6 extra bps costs
    /// money on every trade taken.
    /// </summary>
    public async Task<PostOnlyPlacement> PlaceSwapPostOnlyOrderAsync(
        string instId, string side, string? positionSide, decimal contracts,
        decimal limitPrice, bool reduceOnly, CancellationToken ct)
    {
        var clientOrderId = "cd" + Guid.NewGuid().ToString("N")[..24];

        var payload = new Dictionary<string, object>
        {
            ["instId"]  = instId,
            ["tdMode"]  = opts.MarginMode,
            ["side"]    = side,
            ["ordType"] = "post_only",
            ["px"]      = OkxNum.Format(limitPrice),
            ["sz"]      = OkxNum.Format(contracts),
            ["clOrdId"] = clientOrderId,
        };

        if (positionSide is not null) payload["posSide"]    = positionSide;
        if (reduceOnly)               payload["reduceOnly"] = true;

        List<OkxOrderAck> acks;
        try
        {
            acks = await client.PostPrivateAsync<OkxOrderAck>("/api/v5/trade/order", payload, ct);
        }
        catch (OkxApiException ex)
        {
            // A transport-level or API-level refusal. Reported rather than rethrown
            // for the same reason as an sCode rejection below: the caller's response
            // is identical either way — abandon this entry, keep the bot running.
            log.LogInformation(
                "[OKX] Post-only {Side} on {InstId} at {Px} was refused: {Message}",
                side, instId, OkxNum.Format(limitPrice), ex.Message);

            return PostOnlyPlacement.Rejected(ex.Code, ex.Message);
        }

        var ack = acks.FirstOrDefault();

        if (ack is null)
            // No acknowledgement at all is genuinely ambiguous — the order may exist.
            // This one throws, because silently abandoning an order that might be
            // resting is how an unmanaged position appears.
            throw new OkxApiException("NO_ACK",
                $"OKX accepted the post-only {side} request for {instId} but returned no " +
                $"acknowledgement. The order may exist under clOrdId {clientOrderId} — check the " +
                "exchange before retrying.");

        if (!ack.Accepted || string.IsNullOrWhiteSpace(ack.OrdId))
        {
            log.LogInformation(
                "[OKX] Post-only {Side} on {InstId} at {Px} was not accepted: sCode={Code} sMsg={Msg}. " +
                "Most often this means the book moved and the order would have crossed — nothing is " +
                "resting, and nothing needs cleaning up.",
                side, instId, OkxNum.Format(limitPrice), ack.SCode, ack.SMsg ?? "(none)");

            return PostOnlyPlacement.Rejected(ack.SCode, ack.SMsg);
        }

        log.LogInformation(
            "[OKX] Resting post-only {Side} order {OrdId} on {InstId} at {Px} sz={Contracts} " +
            "posSide={PosSide} (clOrdId={ClOrdId}, demo={Demo})",
            side, ack.OrdId, instId, OkxNum.Format(limitPrice), OkxNum.Format(contracts),
            positionSide ?? "(net)", clientOrderId, opts.DemoTrading);

        return PostOnlyPlacement.Rested(ack.OrdId!);
    }

    /// <summary>
    /// Wait for a resting order for up to <paramref name="attempts"/> polls, then
    /// cancel whatever is left and report what filled.
    ///
    /// Separate from <see cref="WaitForFillAsync"/> because the two have opposite
    /// expectations. A market order that fills nothing is a fault; a resting order
    /// that fills nothing is the ordinary outcome of a quiet book, and the caller
    /// needs to tell the difference. So this returns null for "nothing filled"
    /// instead of throwing.
    /// </summary>
    public async Task<OkxFill?> WaitForRestingFillAsync(
        string instId, string ordId, int attempts, int delayMs, CancellationToken ct)
    {
        OkxOrderDetail? detail = null;
        var polls = Math.Max(1, attempts);

        for (var attempt = 1; attempt <= polls; attempt++)
        {
            detail = await ReadOrderAsync(instId, ordId, ct);

            if (detail is not null && detail.IsTerminal)
                break;

            if (attempt < polls)
                await Task.Delay(Math.Max(50, delayMs), ct);
        }

        if (detail is null)
            throw new OkxApiException("ORDER_NOT_FOUND",
                $"OKX order {ordId} on {instId} could not be read back. Check the exchange: " +
                "the order may be resting and unmanaged.");

        if (!detail.IsTerminal)
        {
            // Cancelled rather than left to rest. An order the bot has stopped
            // watching can still fill, and a position the bot does not know it holds
            // has no stop loss — which on a leveraged perp is the one state worth
            // spending a cancel request to avoid.
            await TryCancelAsync(instId, ordId, ct);

            // Re-read: cancelling is what settles accFillSz on a partial fill.
            detail = await ReadOrderAsync(instId, ordId, ct) ?? detail;
        }

        var filled = detail.FilledSize;
        var price  = detail.AverageFillPrice ?? 0m;

        if (filled <= 0m || price <= 0m)
        {
            log.LogInformation(
                "[OKX] Resting order {OrdId} on {InstId} ended '{State}' with nothing filled. " +
                "No position was opened.", ordId, instId, detail.State);

            return null;
        }

        var requested = OkxNum.Parse(detail.SzRaw);

        if (requested > 0m && filled < requested)
            log.LogWarning(
                "[OKX] Resting order {OrdId} on {InstId} filled {Filled} of {Requested} contracts " +
                "before being cancelled.", ordId, instId, filled, requested);

        return new OkxFill(
            OrdId:           ordId,
            FilledContracts: filled,
            AveragePrice:    price,
            FeeAbs:          detail.FeeAbs,
            FeeCcy:          detail.FeeCcy,
            FullyFilled:     requested <= 0m || filled >= requested);
    }

    // ── Fill resolution ───────────────────────────────────────────────────────

    /// <summary>
    /// Poll an order until it reaches a terminal state, then report what it did.
    ///
    /// A market order still resting when the poll window closes is cancelled
    /// rather than left alone. An order the bot has stopped watching can still
    /// fill, and a position the bot does not know it holds has no stop loss.
    /// </summary>
    public async Task<OkxFill> WaitForFillAsync(string instId, string ordId, CancellationToken ct)
    {
        var attempts = Math.Max(1, opts.FillPollAttempts);
        OkxOrderDetail? detail = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            detail = await ReadOrderAsync(instId, ordId, ct);

            if (detail is not null && detail.IsTerminal)
                break;

            if (attempt < attempts)
                await Task.Delay(Math.Max(50, opts.FillPollDelayMs), ct);
        }

        if (detail is null)
            throw new OkxApiException("ORDER_NOT_FOUND",
                $"OKX order {ordId} on {instId} could not be read back after placement. " +
                "Check the exchange: the order may be live and unmanaged.");

        if (!detail.IsTerminal)
        {
            log.LogWarning(
                "[OKX] Order {OrdId} on {InstId} still {State} after {Attempts} polls — cancelling the remainder.",
                ordId, instId, detail.State, attempts);

            await TryCancelAsync(instId, ordId, ct);

            // Re-read: cancelling is what settles accFillSz on a partial fill.
            detail = await ReadOrderAsync(instId, ordId, ct) ?? detail;
        }

        var filled = detail.FilledSize;
        var price  = detail.AverageFillPrice ?? 0m;

        if (filled <= 0m || price <= 0m)
            throw new OkxApiException("NOT_FILLED",
                $"OKX order {ordId} on {instId} ended '{detail.State}' with nothing filled " +
                $"(accFillSz='{detail.AccFillSzRaw}', avgPx='{detail.AvgPxRaw}').");

        var requested = OkxNum.Parse(detail.SzRaw);
        var complete  = requested <= 0m || filled >= requested;

        if (!complete)
            log.LogWarning(
                "[OKX] Order {OrdId} on {InstId} filled {Filled} of {Requested} contracts.",
                ordId, instId, filled, requested);

        return new OkxFill(
            OrdId:           ordId,
            FilledContracts: filled,
            AveragePrice:    price,
            FeeAbs:          detail.FeeAbs,
            FeeCcy:          detail.FeeCcy,
            FullyFilled:     complete);
    }

    /// <summary>Read one order's current state. Public so reconciliation can inspect a triggered OCO's fill.</summary>
    public async Task<OkxOrderDetail?> ReadOrderAsync(string instId, string ordId, CancellationToken ct)
    {
        var found = await client.GetPrivateAsync<OkxOrderDetail>(
            $"/api/v5/trade/order?instId={instId}&ordId={ordId}", ct);
        return found.FirstOrDefault();
    }

    /// <summary>
    /// Best-effort cancel. A cancel that fails because the order just filled is
    /// the expected race, not a problem, so the error is logged and swallowed —
    /// the caller re-reads the order either way and works from what it says.
    /// </summary>
    private async Task TryCancelAsync(string instId, string ordId, CancellationToken ct)
    {
        try
        {
            await client.PostPrivateAsync<OkxOrderAck>(
                "/api/v5/trade/cancel-order", new { instId, ordId }, ct);
        }
        catch (OkxApiException ex)
        {
            log.LogInformation(
                "[OKX] Cancel of {OrdId} on {InstId} did not apply ({Code}: {Message}) — " +
                "re-reading the order instead.", ordId, instId, ex.Code, ex.Message);
        }
    }

    // ── Exchange-side protective orders ───────────────────────────────────────

    /// <summary>
    /// Place a reduce-only OCO that takes profit or stops out, whichever triggers
    /// first.
    ///
    /// This is the only protection that survives this process dying, and on a
    /// leveraged position that matters more than it does on spot: an unattended
    /// spot position can only lose its own value, while an unattended perp can be
    /// liquidated. The stop's job is to close the trade a long way before the
    /// exchange closes it for you.
    ///
    /// Both legs use ordPx = -1, meaning "fill at market once triggered". A limit
    /// exit at a fixed price is the classic way a stop loss fails to stop
    /// anything: in the move that makes you want out, the limit never fills.
    /// </summary>
    public async Task<string> PlaceOcoExitAsync(
        string instId, string? positionSide, decimal contracts,
        decimal takeProfitTrigger, decimal stopLossTrigger, CancellationToken ct)
    {
        var clientOrderId = "cd" + Guid.NewGuid().ToString("N")[..24];

        // Exit side is the opposite of the position: a long is closed by selling.
        // In net mode posSide is absent and reduceOnly carries the intent instead.
        var exitSide = positionSide switch
        {
            "long"  => "sell",
            "short" => "buy",
            _       => throw new ArgumentException(
                           $"positionSide must be 'long' or 'short' to size an exit, got '{positionSide}'.",
                           nameof(positionSide)),
        };

        var payload = new Dictionary<string, object>
        {
            ["instId"]      = instId,
            ["tdMode"]      = opts.MarginMode,
            ["side"]        = exitSide,
            ["ordType"]     = "oco",
            ["sz"]          = OkxNum.Format(contracts),
            ["reduceOnly"]  = true,
            ["algoClOrdId"] = clientOrderId,
            ["tpTriggerPx"] = OkxNum.Format(takeProfitTrigger),
            ["tpOrdPx"]     = "-1",
            ["slTriggerPx"] = OkxNum.Format(stopLossTrigger),
            ["slOrdPx"]     = "-1",
        };

        // Only meaningful in hedge mode; the caller passes null for net accounts.
        var config = await GetAccountConfigAsync(ct);
        if (config.IsHedgeMode) payload["posSide"] = positionSide!;

        var acks = await client.PostPrivateAsync<OkxAlgoAck>("/api/v5/trade/order-algo", payload, ct);

        var ack = acks.FirstOrDefault()
            ?? throw new OkxApiException("NO_ALGO_ACK",
                $"OKX returned no acknowledgement for the OCO exit on {instId} " +
                $"(algoClOrdId {clientOrderId}).");

        if (!ack.Accepted)
            throw new OkxApiException(ack.SCode ?? "UNKNOWN",
                $"OKX refused the OCO exit on {instId}: sCode={ack.SCode} sMsg={ack.SMsg ?? "(none)"}");

        if (string.IsNullOrWhiteSpace(ack.AlgoId))
            throw new OkxApiException("NO_ALGO_ID",
                $"OKX accepted the OCO exit on {instId} without returning an algoId " +
                $"(algoClOrdId {clientOrderId}).");

        log.LogInformation(
            "[OKX] OCO exit {AlgoId} on {InstId}: {Side} {Contracts} contracts TP={Tp} SL={Sl}",
            ack.AlgoId, instId, exitSide, OkxNum.Format(contracts),
            OkxNum.Format(takeProfitTrigger), OkxNum.Format(stopLossTrigger));

        return ack.AlgoId!;
    }

    /// <summary>Read an OCO order's state, or null if OKX no longer lists it.</summary>
    public async Task<OkxAlgoOrder?> ReadAlgoOrderAsync(string algoId, CancellationToken ct)
    {
        var found = await client.GetPrivateAsync<OkxAlgoOrder>(
            $"/api/v5/trade/order-algo?algoId={algoId}&ordType=oco", ct);
        return found.FirstOrDefault();
    }

    /// <summary>
    /// Cancel an OCO order, reporting whether it is now gone.
    ///
    /// Returns true when the order is cancelled or was already not live. A failure
    /// here is not swallowed the way an order cancel is: the caller is about to
    /// close the position by hand, and doing that while the OCO is still armed
    /// leaves an order queued against a position that will no longer exist.
    /// </summary>
    public async Task<bool> TryCancelAlgoAsync(string instId, string algoId, CancellationToken ct)
    {
        try
        {
            // This endpoint takes an array, not an object — one entry per order.
            var acks = await client.PostPrivateAsync<OkxAlgoAck>(
                "/api/v5/trade/cancel-algos", new[] { new { algoId, instId } }, ct);

            var ack = acks.FirstOrDefault();
            if (ack is null || ack.Accepted)
            {
                log.LogInformation("[OKX] Cancelled OCO {AlgoId} on {InstId}.", algoId, instId);
                return true;
            }

            log.LogWarning(
                "[OKX] Cancel of OCO {AlgoId} on {InstId} was refused: sCode={Code} sMsg={Msg}",
                algoId, instId, ack.SCode, ack.SMsg);
            return false;
        }
        catch (OkxApiException ex)
        {
            log.LogWarning(
                "[OKX] Cancel of OCO {AlgoId} on {InstId} failed ({Code}: {Message}).",
                algoId, instId, ex.Code, ex.Message);
            return false;
        }
    }

    // ── Market data ───────────────────────────────────────────────────────────

    /// <summary>Last traded price on OKX, or null when the ticker is unavailable.</summary>
    public async Task<decimal?> GetLastPriceAsync(string instId, CancellationToken ct)
    {
        var tickers = await client.GetPublicAsync<OkxTicker>(
            $"/api/v5/market/ticker?instId={instId}", ct);
        return tickers.FirstOrDefault()?.Last;
    }

    /// <summary>
    /// Top of book, for deciding where a resting order goes. Null when the venue
    /// returns no ticker at all.
    /// </summary>
    public async Task<OkxTicker?> GetQuoteAsync(string instId, CancellationToken ct)
    {
        var tickers = await client.GetPublicAsync<OkxTicker>(
            $"/api/v5/market/ticker?instId={instId}", ct);
        return tickers.FirstOrDefault();
    }
}
