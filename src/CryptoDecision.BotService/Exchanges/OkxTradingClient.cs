namespace CryptoDecision.BotService.Exchanges;

/// <summary>
/// What an order actually did, once the exchange has settled it. Every figure
/// here is read back from OKX rather than assumed from the request — a market
/// order's price and its fee are both unknown until it fills.
/// </summary>
public sealed record OkxFill(
    string   OrdId,
    decimal  FilledBase,
    decimal  AveragePrice,
    decimal  FeeAbs,
    string?  FeeCcy,
    bool     FullyFilled
);

/// <summary>
/// Order placement and account reads against OKX spot.
///
/// Spot only, tdMode=cash: no leverage, no borrowing, nothing that can go below
/// zero. Sizes are always expressed in the base currency (tgtCcy=base_ccy) on
/// both legs, so the amount bought is the amount later sold and the two legs are
/// directly comparable. Quoting the buy in USDT and the sell in BTC would leave
/// the fee haircut to be reconciled across two different units, on the one code
/// path where a mistake spends real money.
/// </summary>
public sealed class OkxTradingClient(
    OkxSignedClient client,
    OkxOptions      opts,
    ILogger<OkxTradingClient> log)
{
    // ── Account ───────────────────────────────────────────────────────────────

    /// <summary>Free balance of one currency — what a new order may actually commit.</summary>
    public async Task<decimal> GetAvailableAsync(string currency, CancellationToken ct)
    {
        var accounts = await client.GetPrivateAsync<OkxBalanceResponse>(
            $"/api/v5/account/balance?ccy={currency}", ct);

        var detail = accounts
            .FirstOrDefault()?.Details?
            .FirstOrDefault(d => string.Equals(d.Ccy, currency, StringComparison.OrdinalIgnoreCase));

        return detail?.Available ?? 0m;
    }

    // ── Order placement ───────────────────────────────────────────────────────

    /// <summary>
    /// Place a spot market order sized in the base currency and return its
    /// exchange order id. Placement only — the fill is read back separately.
    /// </summary>
    public async Task<string> PlaceSpotMarketOrderAsync(
        string instId, string side, decimal baseQuantity, CancellationToken ct)
    {
        // clOrdId is OKX-constrained to alphanumerics, 1-32 characters. A hex GUID
        // fits, and giving the order an id of ours is what makes a timed-out
        // placement recoverable: the order can be looked up by client id instead of
        // being re-sent blind.
        var clientOrderId = "cd" + Guid.NewGuid().ToString("N")[..24];

        var payload = new
        {
            instId,
            tdMode  = "cash",
            side,                          // "buy" | "sell"
            ordType = "market",
            sz      = OkxNum.Format(baseQuantity),
            tgtCcy  = "base_ccy",
            clOrdId = clientOrderId,
        };

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
            "[OKX] Placed {Side} market order {OrdId} on {InstId} sz={Qty} (clOrdId={ClOrdId}, demo={Demo})",
            side, ack.OrdId, instId, OkxNum.Format(baseQuantity), clientOrderId, opts.DemoTrading);

        return ack.OrdId!;
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
                "[OKX] Order {OrdId} on {InstId} filled {Filled} of {Requested} requested.",
                ordId, instId, filled, requested);

        return new OkxFill(
            OrdId:        ordId,
            FilledBase:   filled,
            AveragePrice: price,
            FeeAbs:       detail.FeeAbs,
            FeeCcy:       detail.FeeCcy,
            FullyFilled:  complete);
    }

    /// <summary>Read one order's current state. Public so reconciliation can inspect a triggered OCO's fill.</summary>
    public async Task<OkxOrderDetail?> ReadOrderAsync(string instId, string ordId, CancellationToken ct)
    {
        var found = await client.GetPrivateAsync<OkxOrderDetail>(
            $"/api/v5/trade/order?instId={instId}&ordId={ordId}", ct);
        return found.FirstOrDefault();
    }

    // ── Exchange-side protective orders ───────────────────────────────────────

    /// <summary>
    /// Place an OCO sell that takes profit or stops out, whichever triggers first.
    ///
    /// This is the only protection that survives this process dying. The bot's own
    /// evaluation loop still runs trailing stops, breakeven and timeouts — those
    /// need judgement the exchange cannot make — but the hard floor lives at OKX,
    /// where it does not depend on a container being up or a price poll succeeding.
    ///
    /// Both legs use ordPx = -1, meaning "fill at market once triggered". A limit
    /// exit at a fixed price is the classic way a stop loss fails to stop
    /// anything: in the move that makes you want out, the limit never fills.
    /// </summary>
    public async Task<string> PlaceOcoExitAsync(
        string instId, decimal baseQuantity, decimal takeProfitTrigger, decimal stopLossTrigger,
        CancellationToken ct)
    {
        var clientOrderId = "cd" + Guid.NewGuid().ToString("N")[..24];

        var payload = new
        {
            instId,
            tdMode      = "cash",
            side        = "sell",
            ordType     = "oco",
            sz          = OkxNum.Format(baseQuantity),
            tgtCcy      = "base_ccy",
            algoClOrdId = clientOrderId,
            tpTriggerPx = OkxNum.Format(takeProfitTrigger),
            tpOrdPx     = "-1",
            slTriggerPx = OkxNum.Format(stopLossTrigger),
            slOrdPx     = "-1",
        };

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
            "[OKX] OCO exit {AlgoId} on {InstId}: sz={Qty} TP={Tp} SL={Sl}",
            ack.AlgoId, instId, OkxNum.Format(baseQuantity),
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
    /// sell the position by hand, and doing that while the OCO is still armed
    /// leaves an order queued to sell coins that will no longer exist.
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
}
