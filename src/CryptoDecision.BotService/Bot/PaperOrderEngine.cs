using CryptoDecision.BotService.Infrastructure;
using CryptoDecision.Shared.Bot;
using CryptoDecision.Shared.Signals;

namespace CryptoDecision.BotService.Bot;

/// <summary>
/// How the bot places and closes orders. Implemented by <see cref="PaperOrderEngine"/>
/// for simulation and by OkxOrderEngine for real spot orders;
/// RoutingOrderEngine picks between them per trade.
/// </summary>
public interface IOrderEngine
{
    /// <param name="geometry">
    /// Volatility-scaled stop and target for this entry, when the strategy produced
    /// them. Null falls back to the configured percentages.
    ///
    /// Threaded through the engine rather than attached to the trade afterwards
    /// because the exchange-side OCO is placed here, inside OpenPositionAsync, and it
    /// is the protection that survives the bot process dying. Recording the geometry
    /// on the row after the fact left the OCO on the configured 2%/1.5% while the bot
    /// watched a 3.2%/1.6% pair — two different stops on one position, with the
    /// exchange holding the one that actually fires. That is not bookkeeping drifting
    /// out of step, it is the strategy being silently overridden by whatever
    /// bot_config happens to say.
    /// </param>
    Task<BotTrade> OpenPositionAsync(string symbol, string strategy, string side, decimal price, decimal capitalUsd, decimal positionPct, CancellationToken ct, decimal confidence = 1.0m, bool useAiSizing = false, StopGeometry? geometry = null, string? entryPath = null);
    Task<BotTrade> CloseTradeAsync(BotTrade trade, decimal exitPrice, string reason, CancellationToken ct);

    /// <summary>
    /// Null when this engine can trade the given configuration; otherwise the
    /// reason it cannot, phrased for an operator reading a log line.
    ///
    /// This exists so the bot can refuse to <em>start</em> on a configuration it
    /// cannot honour, rather than discovering it one order at a time. The failure
    /// it guards against is specific: an operator asks for live trading, the
    /// deployment has no credentials, and the bot quietly paper-trades while
    /// bot_config still says paper_mode = false. Simulated P&amp;L recorded as real
    /// is worse than a bot that plainly refuses to run.
    /// </summary>
    string? DescribeRefusal(BotOptions opts);

    /// <summary>
    /// Whether a SHORT entry can actually be executed under this configuration.
    ///
    /// Asked before a signal becomes an order, not after. A spot cash account
    /// cannot short, and the retired MOMENTUM strategy's thresholds were symmetric — roughly
    /// half of its actionable signals are SHORT. Discovering that one order at a
    /// time turns a predictable constraint into a stream of errors, and buries the
    /// real failures among them.
    /// </summary>
    bool SupportsShort(BotOptions opts);

    /// <summary>
    /// Check whether the venue has already closed this position on its own, and if
    /// so record the close from the exchange's own fill.
    ///
    /// Returns the closed trade, or null when the position is still open and
    /// should be managed normally. This is the counterpart to placing a protective
    /// order at the exchange: once the exchange can close a position without being
    /// asked, something has to notice. Otherwise the bot keeps evaluating stops for
    /// a position that no longer exists, and its recorded P&amp;L never learns what
    /// the trade actually made.
    /// </summary>
    Task<BotTrade?> ReconcileAsync(BotTrade trade, CancellationToken ct);
}

/// <summary>
/// Paper trading engine — simulates fills without placing any order.
///
/// Fills are assumed to happen at the requested price with no slippage and a flat
/// fee. That is optimistic by construction, so paper results are an upper bound on
/// what the same configuration would have done live, not a forecast of it.
/// </summary>
public sealed class PaperOrderEngine(
    BotRepository      repo,
    IFeatureRepository featureRepo,
    BotStateService    state,
    // Injected only for MaxOrderNotionalUsd. The ceiling is a property of this
    // deployment risk appetite rather than of OKX, but OkxOptions is where it lives
    // today and one copy beats two that drift.
    CryptoDecision.BotService.Exchanges.OkxOptions okxOptions,
    ILogger<PaperOrderEngine> log) : IOrderEngine
{
    // Per leg, as a fraction of notional. 0.035% in and 0.035% out is ~7 bps round
    // trip, which is what this bot ACTUALLY PAID over eight live OKX trades:
    //
    //     held <6h   -6.93, -6.94, -6.95, -6.96, -7.07, -7.11 bps
    //
    // measured as (exchange realised P&L - price-derived P&L) over notional, so it
    // includes the maker rebate on entry and the taker fee on exit.
    //
    // Was 0.001 (20 bps round trip), described as "Binance spot without the BNB
    // discount" — a venue and product this bot has not traded since it moved to OKX
    // perpetual swaps with post-only entries. That figure was 3x reality, and on the
    // tight stops this strategy places it was not a rounding error: at a 0.6% stop,
    // 13 bps of phantom cost is 0.22R on every single trade. Across the first 28
    // paper trades it manufactured 5.7R of losses that were never paid.
    //
    // What this still does NOT model, and both flatter a long hold:
    //   • funding, which settles every 8h on OKX and cost 56 bps on the one live
    //     trade that ran the full 12 hours — 9x the rate the backtester assumes
    //   • slippage past the stop, which cost 3.9R on paper trade 47 when SOL fell
    //     2.6% inside one minute
    // Neither bites at the ~2h median hold, and both would if the horizon grew.
    // Per leg, so two legs come to TradingCosts.PostOnlyRoundTrip — maker in, taker
    // out, which is what OkxOrderEngine actually does. Derived rather than written as
    // 0.00035m so it cannot drift away from the live engine's cost model.
    private const decimal FeeRate = TradingCosts.PostOnlyRoundTrip / 2m;

    /// <summary>Simulation can honour any configuration, so it never refuses.</summary>
    public string? DescribeRefusal(BotOptions opts) => null;

    /// <summary>
    /// Simulation shorts freely — there is no borrow to arrange. Note this means
    /// paper results include SHORT trades that the live spot engine would refuse,
    /// so a paper run is not directly comparable to a live long-only one.
    /// </summary>
    public bool SupportsShort(BotOptions opts) => true;

    /// <summary>Nothing outside this process can close a simulated position.</summary>
    public Task<BotTrade?> ReconcileAsync(BotTrade trade, CancellationToken ct)
        => Task.FromResult<BotTrade?>(null);

    public async Task<BotTrade> OpenPositionAsync(
        string symbol, string strategy, string side, decimal price, decimal capitalUsd,
        decimal positionPct, CancellationToken ct, decimal confidence = 1.0m, bool useAiSizing = false,
        StopGeometry? geometry = null,
        string? entryPath = null)
    {
        // Paper mode has no exchange-side OCO to place, so the geometry is carried
        // onto the row for the exit evaluation to read — the same field the live
        // engine populates, so a simulated run exits where a real one would.
        //
        // Sizing is the live engine's, literally — the same EntrySizing call, including
        // the per-order ceiling. A paper run whose position sizing differs from the live
        // one is not a rehearsal of anything: risk-based sizing once asked for $44.54
        // here while OkxOrderEngine would have capped the same entry at $10, so paper
        // P&L ran about 4.5x live and the two were not comparable. It flattered the
        // simulation, since the cap only ever shrinks the order.
        var sized = await EntrySizing.ResolveAsync(
            featureRepo, symbol, capitalUsd, positionPct, state.Options.RiskPctPerTrade,
            okxOptions.MaxOrderNotionalUsd, geometry, confidence, useAiSizing, ct);

        var size     = sized.Size;
        var notional = sized.NotionalUsd;

        if (sized.CapBound)
            log.LogInformation(
                "[PaperBot] Sizing asked for {Asked} but the per-order ceiling is {Cap} — capping, " +
                "so this simulated fill matches what the live engine would have placed.",
                sized.AskedNotionalUsd, okxOptions.MaxOrderNotionalUsd);

        var qty = Math.Round(notional / price, 6);

        // Notional is what the position is worth, and the fee is a cost beside it — not
        // a haircut on the size. This used to record `notional - fee`, which made the
        // recorded notional disagree with `qty x price` by the entry fee and quietly
        // became the denominator of pnl_pct. OkxOrderEngine records the filled notional
        // and carries the fee separately; the two modes now agree.
        var filledNotional = Math.Round(qty * price, 4);
        var entryFee       = Math.Round(filledNotional * FeeRate, 8);

        if (useAiSizing && size.ConfidenceScalar != 1.0m)
            log.LogInformation(
                "[PaperBot] AI-sized: confidence={Conf:P0} scalar={Scalar:F2} adjustedPct={Pct:P1}",
                confidence, size.ConfidenceScalar, size.AdjustedPct);

        // Risk-based sizing reports a scalar of 1.0 — the volatility adjustment lives
        // in the stop distance there, not in a haircut on the notional — so this only
        // ever fires on the fallback path, which is where it is meaningful.
        if (size.VolatilityScalar < 1.0)
            log.LogInformation(
                "[PaperBot] Vol-adjusted size: scalar={Scalar:P0} notional={Notional}",
                size.VolatilityScalar, notional);

        var trade = new BotTrade
        {
            Symbol      = symbol,
            Side        = side,
            Strategy    = strategy,
            EntryPrice  = price,
            Quantity    = qty,
            NotionalUsd = filledNotional,
            // Recorded, not merely deducted. Every paper row had fee_usd NULL while the
            // live rows carried it, so any query that compared cost across modes
            // silently read paper as free.
            FeeUsd      = entryFee,
            Status      = "OPEN",
            OpenedAt    = DateTime.UtcNow,
            Mode        = "PAPER",
            EntryPath   = entryPath,
            // The venue whose prices drove this simulated fill, so a paper row can
            // still be compared against the live rows it was meant to predict.
            Exchange    = state.Options.Exchange,

            // Same levels the live engine would arm at the exchange. Carried here so a
            // paper run exits where a live run would — otherwise the simulation being
            // used to validate the strategy is validating a different exit policy,
            // which is the one thing a paper run must not quietly do.
            StopPrice     = geometry?.RebaseTo(price, side).StopPrice,
            TargetPrice   = geometry?.RebaseTo(price, side).TargetPrice,
            AtrPctAtEntry = geometry is null ? null : (decimal)geometry.AtrPctUsed,
        };

        trade = trade with { Id = await repo.InsertTradeAsync(trade, ct) };

        log.LogInformation(
            "[PaperBot] OPEN {Side} {Symbol} @ ${Price} qty={Qty} notional=${Notional} fee=${Fee}",
            side, symbol, price, qty, filledNotional, entryFee);

        return trade;
    }

    /// <summary>
    /// Settle a simulated position, charging BOTH legs.
    ///
    /// The entry fee used to be charged nowhere. It was subtracted from the recorded
    /// notional at open and then never appeared again, so P&amp;L was
    /// <c>gross - exitFee</c> and every paper trade came out ~3.5 bps better than the
    /// same trade live. On a rule whose whole measured edge is single-digit basis points
    /// that is not a rounding difference: it is most of the margin the hypotheses in
    /// HYPOTHESES.md are being judged on.
    ///
    /// The arithmetic is now the same shape as OkxOrderEngine.ApplyExit — gross, less
    /// the sum of both legs — so a paper row and a live row mean the same thing.
    ///
    /// NOTE FOR ANYONE COMPARING HISTORY: rows closed before this fix carry the old,
    /// flattering figure. A series that spans the change is not homogeneous; subtract
    /// fee_usd/2 from the older rows before pooling them.
    ///
    /// Funding is still not modelled, and at a 12-hour hold it is the larger omission —
    /// a measured 63 bps against these 7. See <see cref="TradingCosts"/>.
    /// </summary>
    public async Task<BotTrade> CloseTradeAsync(
        BotTrade trade, decimal exitPrice, string reason, CancellationToken ct)
    {
        // Charged on the value being closed, not on the entry notional: that is what a
        // percentage fee is, and it is what the exchange bills.
        var exitFee = Math.Round(trade.Quantity * exitPrice * FeeRate, 8);

        var rawPnl = trade.Side == "SHORT"
            ? (trade.EntryPrice - exitPrice) * trade.Quantity
            : (exitPrice - trade.EntryPrice) * trade.Quantity;

        // Both legs, matching how OkxOrderEngine accumulates it. FeeUsd already holds
        // the entry leg from OpenPositionAsync.
        var totalFee = Math.Round((trade.FeeUsd ?? 0m) + exitFee, 8);
        var pnlUsd   = Math.Round(rawPnl - totalFee, 4);

        trade.FeeUsd = totalFee;
        var pnlPct = trade.NotionalUsd > 0m ? Math.Round(pnlUsd / trade.NotionalUsd, 6) : 0m;

        trade.ExitPrice   = exitPrice;
        trade.PnlUsd      = pnlUsd;
        trade.PnlPct      = pnlPct;
        trade.Status      = "CLOSED";
        trade.ClosedAt    = DateTime.UtcNow;
        trade.CloseReason = reason;

        await repo.CloseTradeAsync(trade, ct);

        log.LogInformation(
            "[PaperBot] CLOSE {Symbol} @ ${Exit} reason={Reason} PnL={Pnl:+0.0000;-0.0000} USD ({PnlPct:P2})",
            trade.Symbol, exitPrice, reason, pnlUsd, pnlPct);

        return trade;
    }
}
