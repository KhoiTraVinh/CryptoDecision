using CryptoDecision.PolymarketService.Polymarket;
using Microsoft.Extensions.Options;

namespace CryptoDecision.PolymarketService;

/// <summary>
/// The whole strategy: on an esports match market with real volume, leave a resting buy
/// order at 1c (and 3c) on one side, and wait. If the match goes badly for that side the
/// price collapses through the order and fills it; if it does not, the order expires
/// worthless when the market resolves. There is no exit leg.
///
/// Why resting orders and not market buys. Measured on @greenworld's 36-day history:
/// 99% of positions have a ROUND total share count (100, 80, 250, 200, 150) and 26% of
/// them filled in several pieces, median 12 seconds apart and up to 4.6 minutes. That is
/// a limit order being eaten by sellers, not a taker lifting an offer. Confirming it from
/// the other side: a live snapshot of 272 esports sides found 199 priced above 30c, 21
/// below half a cent, and exactly ONE anywhere between 1c and 5c. The price gaps straight
/// past the entry band, so a taker would almost never fill at 1c - and when it did it
/// would be buying the already-dead population, every one of which lost in the sample.
///
/// Why no exit. The 23 positions that were sold mid-match returned $1,693.75; held to
/// resolution the same positions would have returned $1,660.40. A 2% difference is not
/// worth an exit engine.
///
/// What the entry rests on. 2,187 entries at 1c hit 1.74% against a 1.00% break-even
/// (z = 3.36, p = 0.0004). The 3c level has no such measurement: it needs 3.00% to break
/// even and nothing in the sample says it gets there. Every order is logged with its
/// level so this bot's own fills can settle the question later.
/// </summary>
public sealed class EsportsEntryWorker(
    GammaClient gamma,
    ClobClient clob,
    ChainClient chain,
    DataApiClient dataApi,
    IOptions<PolymarketOptions> options,
    ILogger<EsportsEntryWorker> log) : BackgroundService
{
    private readonly PolymarketOptions _opt = options.Value;

    /// <summary>"tokenId|level" for every level we have already acted on - resting or filled.</summary>
    private readonly HashSet<string> _placed = [];

    /// <summary>conditionId to the token we committed to. Blocks the other side of the same match.</summary>
    private readonly Dictionary<string, string> _committedSide = [];

    /// <summary>orderId to the token it rests on, so orders on finished matches can be found and cancelled.</summary>
    private readonly Dictionary<string, string> _restingOrders = [];


    private IReadOnlyList<EsportsMarket> _markets = [];
    private DateTimeOffset _marketsAt = DateTimeOffset.MinValue;
    private DateTimeOffset _syncedAt = DateTimeOffset.MinValue;

    /// <summary>Orders actually sent to the exchange this run.</summary>
    private int _ordersSent;

    private DateOnly _spendDay = DateOnly.FromDateTime(DateTime.UtcNow);
    private decimal _committedToday;

    /// <summary>
    /// Polymarket rejects orders under 5 shares. $0.20 buys 20 whole shares at 1c and 6 at
    /// 3c, so the default stake clears it at both levels - a smaller stake would not.
    /// </summary>
    private const decimal MinOrderShares = 5m;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Also fills the array defaults; see the binding note on PolymarketOptions.
        _opt.Validate();

        log.LogInformation(
            "Esports entry bot starting. Mode={Mode} Stake=${Stake} Levels=[{Levels}] MinVolume=${Volume} Games=[{Games}] DailyCap=${Cap} BothSides={Both}",
            _opt.DryRun ? "DRY RUN (no orders sent)" : "LIVE (real money)",
            _opt.StakeUsd, string.Join(", ", _opt.EntryPrices), _opt.MinVolumeUsd,
            string.Join(", ", _opt.Games), _opt.MaxDailyUsd, _opt.RestBothSides);

        // Throws rather than returning quietly. A BackgroundService that just returns
        // leaves the host running with nothing to do, so the container still reports
        // "Up" while the bot is dead — the one failure that could go unnoticed for weeks.
        // Throwing stops the host, and restart: unless-stopped brings it back: a
        // transient network failure at boot then retries by itself, and a real
        // misconfiguration crash-loops loudly instead of hiding.
        await PreflightAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Scan cycle failed; retrying next tick");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_opt.ScanIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // _placed is the de-duplication set and counts levels seeded from existing
        // positions and open orders too, so it is not the number of orders this run sent.
        log.LogInformation("Esports entry bot stopped. {Sent} orders sent this run, ${Committed} committed today",
            _ordersSent, _committedToday);
    }

    /// <summary>
    /// Proves the wallet before the first order rather than after it. In dry-run with no
    /// key configured this is skipped entirely, so the scanner can be watched on its own.
    /// </summary>
    private async Task PreflightAsync(CancellationToken ct)
    {
        if (_opt.DryRun)
        {
            log.LogWarning("DRY RUN: scanning and deciding, but no order will ever be sent. " +
                           "Set Polymarket__DryRun=false once the preflight numbers below look right.");

            if (string.IsNullOrWhiteSpace(_opt.PrivateKey)) return;
        }

        try
        {
            await clob.ConnectAsync(ct);
        }
        catch (Exception ex) when (_opt.DryRun)
        {
            // Dry-run is allowed to carry on without a working wallet: the scanner is
            // the thing being watched, and there is nothing to sign.
            log.LogError(ex, "Could not connect to the CLOB with this wallet; continuing because this is a dry run");
            return;
        }

        // Two sources, because they disagree and only one of them decides anything.
        // The CLOB figure is the legacy USDC.e view and reads zero for an account funded
        // in pUSD; the chain figure is read against the exchange this bot actually signs
        // for, so that is the one the go-live gate uses.
        var (clobBalance, clobAllowance) = await clob.GetBalanceAllowanceAsync(ct);
        var onChain = await chain.ReadAccountStateAsync(_opt.ExchangeAddress, clob.HolderAddress, ct);

        var balance = onChain.Balance ?? clobBalance;
        var allowance = onChain.Allowance ?? clobAllowance;

        log.LogInformation(
            "Preflight: signer={Signer} holder={Holder} signatureType={SigType} exchange={Exchange} " +
            "collateral={Collateral} ({Symbol}) balance={Balance} allowance={Allowance} [clob reported {ClobBalance}/{ClobAllowance}]",
            clob.SignerAddress, clob.HolderAddress, _opt.SignatureType, _opt.ExchangeAddress,
            onChain.CollateralToken ?? "unknown", onChain.Symbol ?? "?",
            balance?.ToString("0.00") ?? "unknown",
            allowance is null ? "unknown" : allowance > 1_000_000_000m ? "unlimited" : allowance.Value.ToString("0.00"),
            clobBalance?.ToString("0.00") ?? "?", clobAllowance?.ToString("0.00") ?? "?");

        // Signature types 1 and 2 mean the money lives in a proxy that Polymarket created,
        // which is never the wallet address itself. The two being equal means
        // FunderAddress was filled in with the wallet address instead of the one from the
        // Deposit page - an easy mistake, and one whose only other symptom is a zero
        // balance that looks like "no funds".
        if (_opt.SignatureType != 0 &&
            string.Equals(clob.SignerAddress, clob.HolderAddress, StringComparison.OrdinalIgnoreCase))
        {
            log.LogError(
                "Polymarket__FunderAddress ({Holder}) is the same as the wallet address. For signature " +
                "type {SigType} it must be the Polymarket address from the Deposit page, which is a " +
                "different address. If this wallet has never been connected to polymarket.com it does " +
                "not have one yet - sign in there once to create it.",
                clob.HolderAddress, _opt.SignatureType);

            if (!_opt.DryRun) throw new InvalidOperationException("FunderAddress is the wallet address; see the error above");
        }

        if (balance is 0)
        {
            // Zero usually means one of two things, and both are worth stopping for: the
            // bot is looking at the wrong address, or there is genuinely no cash to trade
            // with because it is all tied up in open positions.
            log.LogError(
                "Holder {Holder} holds no collateral that exchange {Exchange} can spend. Either the " +
                "account is genuinely empty, or it is funded on different rails than this exchange " +
                "settles in — Polymarket runs USDC.e and pUSD exchanges side by side. Check " +
                "Polymarket__ExchangeAddress against the token the account actually holds.",
                clob.HolderAddress, _opt.ExchangeAddress);

            if (!_opt.DryRun) throw new InvalidOperationException("no spendable collateral; see the error above");
        }

        if (allowance is 0)
        {
            // Orders will be accepted and will simply never settle. Make one manual trade
            // on polymarket.com with this wallet; that sets the approvals, and a bot
            // cannot set them for you because it needs an on-chain transaction.
            log.LogError(
                "Holder {Holder} has not approved exchange {Exchange} to move its collateral. Orders " +
                "would rest and never fill. Either make one manual trade on polymarket.com with this " +
                "wallet, or point Polymarket__ExchangeAddress at the exchange the account IS approved for.",
                clob.HolderAddress, _opt.ExchangeAddress);

            if (!_opt.DryRun) throw new InvalidOperationException("exchange not approved to move collateral; see the error above");
        }

        await SyncWithExchangeAsync(ct);
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        RollDailyBudget();

        var now = DateTimeOffset.UtcNow;

        if (now - _marketsAt > TimeSpan.FromSeconds(_opt.MarketRefreshSeconds))
        {
            _markets = await gamma.FetchEsportsMarketsAsync(ct);
            _marketsAt = now;

            // Markets that just left the eligible set are matches that are over. Anything
            // we left resting on them is exposure on a game nobody is watching.
            await CancelOrdersOnFinishedMarketsAsync(ct);
        }

        if (now - _syncedAt > TimeSpan.FromSeconds(_opt.PositionRefreshSeconds))
            await SyncWithExchangeAsync(ct);

        if (_markets.Count == 0) return;


        // Only markets where at least one level is still unplaced can produce an order.
        var open = _markets.Where(HasWorkLeft).ToList();
        if (open.Count == 0) return;

        var books = await clob.GetBooksAsync(open.SelectMany(m => m.TokenIds).Distinct().ToList(), ct);
        var placed = 0;
        var armed = 0;
        var nearest = 1m;
        var nearestOn = "";

        // Why an armed side produced no order. Without these the cycle line says
        // "3 collapsing, 0 placed" and gives the operator no way to tell a bot that is
        // waiting correctly from one that is broken.
        var tooLate = 0;
        var alreadyDone = 0;
        var released = 0;

        foreach (var market in open)
        {
            // How close the market is to arming. Without this a quiet bot and a broken
            // one print the same line, and most cycles are quiet by design.
            foreach (var t in market.TokenIds)
                if (books.TryGetValue(t, out var b) && b.BestAsk is { } a && a < nearest)
                {
                    nearest = a;
                    nearestOn = market.Question;
                }

            foreach (var (tokenId, outcome) in ChooseSides(market, books))
            {
                armed++;

                // If an earlier, still-unfilled order sits on the other side and that
                // side has climbed back out of the band, let it go and take this one.
                // If it cannot be released, skip the market rather than rest on both.
                var (clear, didRelease) = await TryClearOtherSideAsync(market, tokenId, books, ct);
                if (didRelease) released++;
                if (!clear) continue;
                foreach (var level in _opt.EntryPrices)
                {
                    if (!books.TryGetValue(tokenId, out var book)) continue;

                    if (_placed.Contains(Key(tokenId, level))) { alreadyDone++; continue; }

                    // The order must REST, not cross. If the ask is already at or below the
                    // level, a buy there executes immediately as a taker - and that is the
                    // already-collapsed population the sample says loses. Strictly above.
                    var ask = book.BestAsk;
                    if (ask is null) continue;
                    if (ask.Value <= level) { tooLate++; continue; }

                    if (await TryPlaceAsync(market, tokenId, outcome, level, ask.Value, ct)) placed++;
                }
            }
        }

        log.LogInformation(
            "Cycle: {Markets} in play, {Open} watched, {Armed} collapsing past {Arm} -> {Placed} placed " +
            "({TooLate} already below the level, {Done} done earlier, {Released} stale sides released). " +
            "{Resting} resting, ${Committed} today. Lowest ask {Nearest} on {Where}",
            _markets.Count, open.Count, armed, _opt.ArmBelowAsk, placed, tooLate, alreadyDone, released,
            _restingOrders.Count, _committedToday,
            nearest < 1m ? nearest.ToString("0.###") : "none",
            nearest < 1m ? nearestOn : "-");
    }

    /// <summary>
    /// Only the side we could still act on counts. Once a market is committed to one
    /// outcome the other side will never be ordered, so letting it keep the market "open"
    /// would have us re-fetching its book every cycle for nothing.
    /// </summary>
    private bool HasWorkLeft(EsportsMarket m)
    {
        var candidates = !_opt.RestBothSides && _committedSide.TryGetValue(m.ConditionId, out var side)
            ? m.TokenIds.Where(t => t == side)
            : m.TokenIds;

        return candidates.Any(t => _opt.EntryPrices.Any(l => !_placed.Contains(Key(t, l))));
    }

    /// <summary>
    /// Which side of the match to arm, if any.
    ///
    /// A side only qualifies once its ask has fallen to <see cref="PolymarketOptions.ArmBelowAsk"/>:
    /// the bet is on a team that is being beaten badly right now, not on whoever happens
    /// to be the underdog at kickoff. Until then the market is watched and nothing is
    /// placed. That gate is also what picks the side — the collapsing one selects itself,
    /// so no "cheaper side" heuristic is needed.
    ///
    /// The measured account averaged 1.01 positions per market across 2,169 markets and
    /// held both sides on only 24 of them, which is what arming one collapsing side per
    /// match looks like. <see cref="PolymarketOptions.RestBothSides"/> allows both; it is
    /// not a hedge, since if both ever fill you paid $0.40 for a ticket that pays out
    /// whichever team wins.
    /// </summary>
    private IEnumerable<(string TokenId, string Outcome)> ChooseSides(EsportsMarket market, IReadOnlyDictionary<string, OrderBook> books)
    {
        var sides = new List<(string TokenId, string Outcome, decimal Ask)>();
        for (var i = 0; i < market.TokenIds.Count; i++)
        {
            var token = market.TokenIds[i];
            if (!books.TryGetValue(token, out var book) || book.BestAsk is null) continue;
            sides.Add((token, i < market.Outcomes.Count ? market.Outcomes[i] : $"#{i}", book.BestAsk.Value));
        }

        if (sides.Count == 0) yield break;

        // Stay on a side already committed to, whatever its price does now - switching
        // mid-match is how you end up owning both by accident.
        if (!_opt.RestBothSides && _committedSide.TryGetValue(market.ConditionId, out var committed))
        {
            var held = sides.FirstOrDefault(s => s.TokenId == committed);
            if (held.TokenId is not null) yield return (held.TokenId, held.Outcome);
            yield break;
        }

        var collapsing = sides.Where(s => s.Ask <= _opt.ArmBelowAsk).OrderBy(s => s.Ask).ToList();
        if (collapsing.Count == 0) yield break;

        foreach (var s in _opt.RestBothSides ? collapsing : collapsing.Take(1))
            yield return (s.TokenId, s.Outcome);
    }

    private async Task<bool> TryPlaceAsync(
        EsportsMarket market, string tokenId, string outcome, decimal level, decimal ask, CancellationToken ct)
    {
        if (_placed.Contains(Key(tokenId, level))) return false;

        // Whole shares, rounded down. Polymarket accepts two decimals, but every position
        // in the measured history is a round number - 100, 80, 250, 200, 150 - and a
        // ragged 6.66 buys nothing a clean 6 does not. The stake is therefore a ceiling,
        // not a target: $0.20 at 3c is 6 shares for $0.18.
        var shares = Math.Floor(_opt.StakeUsd / level);
        if (shares < MinOrderShares)
        {
            log.LogWarning("Stake ${Stake} at {Level} is only {Shares} whole shares, under Polymarket's {Min}-share minimum; skipping",
                _opt.StakeUsd, level, shares, MinOrderShares);
            return false;
        }

        // Budget against what the order actually costs, not the nominal stake.
        var notional = shares * level;

        // 0 disables the cap. Worth knowing what that removes: the account balance
        // becomes the only limit on a day's spending, because an unfilled order costs
        // nothing and a filled one is already paid for out of collateral.
        if (_opt.MaxDailyUsd > 0 && _committedToday + notional > _opt.MaxDailyUsd)
        {
            log.LogWarning("Daily cap of ${Cap} reached (${Committed} committed); skipping {Question}",
                _opt.MaxDailyUsd, _committedToday, market.Question);
            return false;
        }

        if (_opt.DryRun)
        {
            log.LogInformation(
                "WOULD REST {Shares} x {Outcome} @ {Level} (ask now {Ask}, costs ${Notional}) | {Game} | vol ${Volume} | {Question}",
                shares, outcome, level, ask, notional, market.Game, market.VolumeUsd.ToString("0"), market.Question);

            Commit(market, tokenId, level, orderId: null);
            _committedToday += notional;
            return true;
        }

        var result = await clob.PlaceRestingBuyAsync(tokenId, level, shares, market.NegRisk, ct);

        if (!result.Ok)
        {
            log.LogWarning("Order rejected for {Outcome} @ {Level} on {Question}: {Error}",
                outcome, level, market.Question, result.Error);
            return false;
        }

        _committedToday += notional;
        _ordersSent++;
        Commit(market, tokenId, level, result.OrderId);

        log.LogInformation(
            "RESTING {Shares} x {Outcome} @ {Level} (costs ${Notional}) | ask now {Ask} | {Game} | vol ${Volume} | order {OrderId} | {Question}",
            shares, outcome, level, notional, ask, market.Game, market.VolumeUsd.ToString("0"), result.OrderId, market.Question);

        if (result.FilledShares > 0)
            log.LogInformation("  ...and {Filled} shares filled immediately for ${Notional}",
                result.FilledShares, result.NotionalUsd.ToString("0.0000"));

        return true;
    }

    /// <summary>
    /// Records a placement. Deliberately does NOT commit the market to this side —
    /// <see cref="_committedSide"/> is populated from held positions only, in
    /// SyncWithExchangeAsync. A resting order is not a commitment: no money has moved,
    /// and if the price recovers past the arming threshold it never will.
    ///
    /// Measured on 171 markets: whichever side reaches 0.15 first is NOT the side that
    /// ends up at 1c in 7.6% of them. Committing on placement loses those outright —
    /// the order sits unfillable while the other side collapses with the market locked.
    /// Seen live on Team WE vs FlyQuest Game 4: armed on WE at 0.15, WE recovered to
    /// 0.64, FlyQuest became the falling side, and the WE order could never fill.
    /// </summary>
    private void Commit(EsportsMarket market, string tokenId, decimal level, string? orderId)
    {
        _placed.Add(Key(tokenId, level));
        if (!string.IsNullOrEmpty(orderId)) _restingOrders[orderId] = tokenId;
    }

    /// <summary>
    /// Releases a market still held by an unfilled order when the other side has become
    /// the one collapsing. Cancels the stale order and forgets the placement so the new
    /// side can be armed this cycle.
    ///
    /// Only ever runs when the market has no position: once an entry has filled, the
    /// side is settled and nothing here touches it.
    /// </summary>
    /// <summary>
    /// Answers whether it is safe to place on <paramref name="wantedToken"/>, releasing
    /// an unfilled order on the other side first if one is in the way.
    ///
    /// Returns false rather than throwing when the old order cannot be cancelled: the
    /// alternative is to place anyway and end up resting on both sides, which is the one
    /// outcome the "one side per market" rule exists to prevent.
    /// </summary>
    private async Task<(bool Clear, bool Released)> TryClearOtherSideAsync(
        EsportsMarket market, string wantedToken, IReadOnlyDictionary<string, OrderBook> books, CancellationToken ct)
    {
        if (_committedSide.ContainsKey(market.ConditionId)) return (true, false);   // we hold shares; ChooseSides already kept us on them

        var stale = _restingOrders
            .Where(kv => market.TokenIds.Contains(kv.Value) && kv.Value != wantedToken)
            .ToList();
        if (stale.Count == 0) return (true, false);

        // Only let go once the old side has actually climbed back out of the arming band.
        // Both sides cannot be below the threshold at once, but prices move between
        // cycles and this stops a market flipping back and forth on noise.
        foreach (var (_, token) in stale)
            if (books.TryGetValue(token, out var b) && b.BestAsk is { } ask && ask <= _opt.ArmBelowAsk)
                return (false, false);

        var ids = stale.Select(kv => kv.Key).ToList();
        var cancelled = _opt.DryRun ? ids.Count : await clob.CancelOrdersAsync(ids, ct);
        if (cancelled == 0)
        {
            log.LogWarning("Could not cancel {Count} order(s) on the other side of {Question}; not placing, to avoid resting on both sides",
                ids.Count, market.Question);
            return (false, false);
        }

        foreach (var (id, token) in stale)
        {
            _restingOrders.Remove(id);
            foreach (var level in _opt.EntryPrices) _placed.Remove(Key(token, level));
        }

        log.LogInformation(
            "Released {Count} unfilled order(s) on {Question}: that side recovered and {Wanted} is the one collapsing now",
            cancelled, market.Question, wantedToken[..Math.Min(12, wantedToken.Length)]);

        return (true, true);
    }

    /// <summary>
    /// Rebuilds what we hold and what we have resting, from the exchange. This is the
    /// entire duplicate-entry defence and the entire reason this service needs no
    /// database: positions are on-chain and open orders live on the CLOB, so both survive
    /// a restart that an in-process set would not.
    /// </summary>
    private async Task SyncWithExchangeAsync(CancellationToken ct)
    {
        var address = clob.HolderAddress;
        if (string.IsNullOrWhiteSpace(address)) return;

        var ok = true;

        try
        {
            foreach (var p in await dataApi.GetPositionsAsync(address, ct))
            {
                _committedSide[p.ConditionId] = p.TokenId;

                // Block every level on a token we already hold. A position does not say
                // which level bought it, and re-buying after a restart is a far worse
                // failure than missing one follow-up order.
                foreach (var level in _opt.EntryPrices) _placed.Add(Key(p.TokenId, level));
            }
        }
        catch (Exception ex)
        {
            // Crucially we do NOT clear what we already know: a failed read must never
            // look like "I hold nothing", or the bot would buy it all a second time.
            log.LogWarning(ex, "Position sync failed; keeping the previous picture");
            ok = false;
        }

        try
        {
            var orders = await clob.GetOpenOrdersAsync(ct);
            _restingOrders.Clear();

            foreach (var o in orders)
            {
                if (o.Side != "BUY") continue;
                _restingOrders[o.Id] = o.TokenId;
                _placed.Add(Key(o.TokenId, o.Price));
            }

            log.LogInformation("Exchange sync: {Resting} resting orders, {Markets} markets blocked from re-entry",
                _restingOrders.Count, _committedSide.Count);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Open order sync failed; keeping the previous picture");
            ok = false;
        }

        // Only advance the clock on a clean sync, so a failure retries next cycle.
        if (ok) _syncedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Cancels anything resting on a token that is no longer in the eligible market set.
    /// A resting 1c bid on a finished match is a live order on a game whose result is
    /// already known to everyone watching it.
    /// </summary>
    private async Task CancelOrdersOnFinishedMarketsAsync(CancellationToken ct)
    {
        if (_restingOrders.Count == 0 || _opt.DryRun) return;

        var live = _markets.SelectMany(m => m.TokenIds).ToHashSet();
        var stale = _restingOrders.Where(kv => !live.Contains(kv.Value)).Select(kv => kv.Key).ToList();
        if (stale.Count == 0) return;

        var cancelled = await clob.CancelOrdersAsync(stale, ct);
        if (cancelled > 0)
        {
            foreach (var id in stale) _restingOrders.Remove(id);
            log.LogInformation("Cancelled {Count} orders left on matches that are over", cancelled);
        }
    }

    private void RollDailyBudget()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today == _spendDay) return;

        log.LogInformation("New UTC day; ${Committed} was committed on {Day}", _committedToday, _spendDay);
        _spendDay = today;
        _committedToday = 0m;
    }

    private static string Key(string tokenId, decimal level) =>
        tokenId + "|" + level.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
