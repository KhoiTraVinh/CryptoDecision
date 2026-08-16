using CryptoDecision.BotService.Infrastructure;
using CryptoDecision.Shared.Bot;

namespace CryptoDecision.BotService.Bot;

/// <summary>
/// Paper trading engine — simulates orders without real money.
/// Interface designed to be swapped with RealOrderEngine in the future.
/// </summary>
public interface IOrderEngine
{
    Task<BotTrade> OpenPositionAsync(string symbol, string strategy, string side, decimal price, decimal capitalUsd, decimal positionPct, CancellationToken ct, decimal confidence = 1.0m, bool useAiSizing = false);
    Task<BotTrade> CloseTradeAsync(BotTrade trade, decimal exitPrice, string reason, CancellationToken ct);
}

public sealed class PaperOrderEngine(
    BotRepository      repo,
    IFeatureRepository featureRepo,
    ILogger<PaperOrderEngine> log) : IOrderEngine
{
    // Binance spot fee = 0.1% (BNB discount gives 0.075%, use conservative 0.1%)
    private const decimal FeeRate = 0.001m;
    // Base volatility target: reduce size proportionally above this (%)
    private const double  BaseVolPct = 2.0;

    public async Task<BotTrade> OpenPositionAsync(
        string symbol, string strategy, string side, decimal price, decimal capitalUsd, decimal positionPct, CancellationToken ct, decimal confidence = 1.0m, bool useAiSizing = false)
    {
        // Volatility-adjusted sizing
        var feature     = await featureRepo.GetTodayAsync(symbol, ct);
        var currentVol  = (double)(feature?.Volatility ?? 2.0m);
        var volScalar   = Math.Clamp(BaseVolPct / Math.Max(currentVol, 0.5), 0.5, 1.0);
        var adjustedPct = positionPct * (decimal)volScalar;

        // AI confidence-based sizing: scale position 0.5x-1.5x based on confidence
        if (useAiSizing && confidence > 0)
        {
            var aiScalar = 0.5m + confidence; // confidence 0.0→0.5x, 0.5→1.0x, 1.0→1.5x
            aiScalar = Math.Clamp(aiScalar, 0.5m, 1.5m);
            adjustedPct *= aiScalar;
            log.LogInformation(
                "[PaperBot] AI-sized: confidence={Conf:P0} scalar={Scalar:F2} adjustedPct={Pct:P1}",
                confidence, aiScalar, adjustedPct);
        }

        var notional = Math.Round(capitalUsd * adjustedPct, 2);
        var qty      = Math.Round(notional / price, 6);
        var fee      = Math.Round(notional * FeeRate, 4);

        if (volScalar < 1.0)
            log.LogInformation(
                "[PaperBot] Vol-adjusted size: vol={Vol:F1}% scalar={Scalar:P0} notional=${Notional}",
                currentVol, volScalar, notional);

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
