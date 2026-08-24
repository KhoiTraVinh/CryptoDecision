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
    Task<BotTrade> OpenPositionAsync(string symbol, string strategy, string side, decimal price, decimal capitalUsd, decimal positionPct, CancellationToken ct, decimal confidence = 1.0m, bool useAiSizing = false, StopGeometry? geometry = null);
    Task<BotTrade> CloseTradeAsync(BotTrade trade, decimal exitPrice, string reason, CancellationToken ct);

    /// <summary>
    /// Null when this engine can trade the given configuration; otherwise the
    /// reason it cannot, phrased for an operator reading a log line.
    ///
    /// This exists so the bot can refuse to <em>start</em> on a configuration it
    /// cannot honour, rather than discovering it one order at a time. The failure
    /// it guards against is specific: an operator asks for live trading, the
    /// deployment has no credentials, and the bot quietly paper-trades while the
    /// dashboard says LIVE. Simulated P&amp;L presented as real is worse than a
    /// bot that plainly refuses to run.
    /// </summary>
    string? DescribeRefusal(BotOptions opts);

    /// <summary>
    /// Whether a SHORT entry can actually be executed under this configuration.
    ///
    /// Asked before a signal becomes an order, not after. A spot cash account
    /// cannot short, and MomentumStrategy's thresholds are symmetric — roughly
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
    ILogger<PaperOrderEngine> log) : IOrderEngine
{
    // Conservative taker fee, applied per leg. Binance spot without the BNB
    // discount; OKX spot taker is 0.08-0.10%, so this stays on the safe side of
    // both venues.
    private const decimal FeeRate = 0.001m;

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
        string symbol, string strategy, string side, decimal price, decimal capitalUsd, decimal positionPct, CancellationToken ct, decimal confidence = 1.0m, bool useAiSizing = false, StopGeometry? geometry = null)
    {
        // Paper mode has no exchange-side OCO to place, so the geometry is carried
        // onto the row for the exit evaluation to read — the same field the live
        // engine populates, so a simulated run exits where a real one would.
        //
        // Sizing follows the same two rules as the live engine, by design: a paper run
        // whose position sizing differs from the live one is not a rehearsal of
        // anything. See PositionSizer.ResolveByRisk.
        PositionSize size;

        if (geometry is { StopPct: > 0m } g)
        {
            size = PositionSizer.ResolveByRisk(
                capitalUsd, state.Options.RiskPctPerTrade, g.StopPct, confidence, useAiSizing);
        }
        else
        {
            var feature = await featureRepo.GetTodayAsync(symbol, ct);
            size = PositionSizer.Resolve(
                capitalUsd, positionPct, (double)(feature?.Volatility ?? 2.0m), confidence, useAiSizing);
        }

        var notional = size.NotionalUsd;
        var qty      = Math.Round(notional / price, 6);
        var fee      = Math.Round(notional * FeeRate, 4);

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
            NotionalUsd = notional - fee,
            Status      = "OPEN",
            OpenedAt    = DateTime.UtcNow,
            Mode        = "PAPER",
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
            "[PaperBot] OPEN {Side} {Symbol} @ ${Price} qty={Qty} notional=${Notional}",
            side, symbol, price, qty, notional);

        return trade;
    }

    public async Task<BotTrade> CloseTradeAsync(
        BotTrade trade, decimal exitPrice, string reason, CancellationToken ct)
    {
        var fee    = Math.Round(trade.NotionalUsd * FeeRate, 4);
        var rawPnl = trade.Side == "SHORT"
            ? (trade.EntryPrice - exitPrice) * trade.Quantity
            : (exitPrice - trade.EntryPrice) * trade.Quantity;

        var pnlUsd = Math.Round(rawPnl - fee, 4);
        var pnlPct = Math.Round(pnlUsd / trade.NotionalUsd, 6);

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
