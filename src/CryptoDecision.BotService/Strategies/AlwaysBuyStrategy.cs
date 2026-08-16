using CryptoDecision.BotService.Bot;
using CryptoDecision.Shared.Bot;

namespace CryptoDecision.BotService.Strategies;

/// <summary>
/// Always-Buy Strategy: unconditionally opens LONG positions.
/// Useful for testing and as a baseline benchmark.
/// Exits on simple TP/SL thresholds.
/// </summary>
public sealed class AlwaysBuyStrategy : ITradingStrategy
{
    public string Name => "ALWAYS_BUY";

    public Task<EntryDecision> EvaluateEntryAsync(StrategyContext ctx, CancellationToken ct)
        => Task.FromResult(new EntryDecision(true, "LONG"));

    public ExitDecision EvaluateExit(BotTrade trade, decimal currentPrice, BotOptions opts)
    {
        var rawChange = (currentPrice - trade.EntryPrice) / trade.EntryPrice;
        var changePct = trade.Side == "SHORT" ? -rawChange : rawChange;

        if (changePct >= opts.TakeProfitPct)
            return new ExitDecision(true, "TP", currentPrice, changePct);
        if (changePct <= -opts.StopLossPct)
            return new ExitDecision(true, "SL", currentPrice, changePct);

        return new ExitDecision(false, null, currentPrice, changePct);
    }
}
