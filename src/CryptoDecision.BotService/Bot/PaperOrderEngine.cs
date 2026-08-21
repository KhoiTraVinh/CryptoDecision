using CryptoDecision.BotService.Infrastructure;
using CryptoDecision.Shared.Bot;

namespace CryptoDecision.BotService.Bot;

/// <summary>
/// How the bot places and closes orders. Implemented by <see cref="PaperOrderEngine"/>
/// for simulation and by OkxOrderEngine for real spot orders;
/// RoutingOrderEngine picks between them per trade.
/// </summary>
public interface IOrderEngine
{
    Task<BotTrade> OpenPositionAsync(string symbol, string strategy, string side, decimal price, decimal capitalUsd, decimal positionPct, CancellationToken ct, decimal confidence = 1.0m, bool useAiSizing = false);
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
        string symbol, string strategy, string side, decimal price, decimal capitalUsd, decimal positionPct, CancellationToken ct, decimal confidence = 1.0m, bool useAiSizing = false)
    {
        var feature = await featureRepo.GetTodayAsync(symbol, ct);
        var size    = PositionSizer.Resolve(
            capitalUsd, positionPct, (double)(feature?.Volatility ?? 2.0m), confidence, useAiSizing);

        var notional = size.NotionalUsd;
        var qty      = Math.Round(notional / price, 6);
        var fee      = Math.Round(notional * FeeRate, 4);

        if (useAiSizing && size.ConfidenceScalar != 1.0m)
            log.LogInformation(
                "[PaperBot] AI-sized: confidence={Conf:P0} scalar={Scalar:F2} adjustedPct={Pct:P1}",
                confidence, size.ConfidenceScalar, size.AdjustedPct);

        if (size.VolatilityScalar < 1.0)
            log.LogInformation(
                "[PaperBot] Vol-adjusted size: vol={Vol:F1}% scalar={Scalar:P0} notional=${Notional}",
                feature?.Volatility ?? 2.0m, size.VolatilityScalar, notional);

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
