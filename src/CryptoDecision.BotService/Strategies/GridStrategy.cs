using CryptoDecision.BotService.Bot;
using CryptoDecision.Shared.Bot;

namespace CryptoDecision.BotService.Strategies;

/// <summary>
/// Grid Strategy: DCA (Dollar Cost Averaging) with grid-step entries.
/// Opens a LONG when price drops GridStepPct below the lowest open entry.
/// Exits on TP or SL — no trailing stop.
/// </summary>
public sealed class GridStrategy(ILogger<GridStrategy> log) : ITradingStrategy
{
    public string Name => "GRID";

    public Task<EntryDecision> EvaluateEntryAsync(StrategyContext ctx, CancellationToken ct)
    {
        var opts       = ctx.Options;
        var openTrades = ctx.OpenTrades;
        var price      = ctx.CurrentPrice;

        if (openTrades.Count == 0)
            return Task.FromResult(new EntryDecision(true, "LONG"));

        var lowestEntry      = openTrades.Min(t => t.EntryPrice);
        var requiredDropPrice = lowestEntry * (1m - opts.GridStepPct);

        if (price <= requiredDropPrice)
        {
            log.LogInformation(
                "[GridStrategy] Price dropped to {Curr:F2} (Lowest: {Low:F2} -> Target: {Tar:F2}). Buying slot {Slot}.",
                price, lowestEntry, requiredDropPrice, openTrades.Count + 1);
            return Task.FromResult(new EntryDecision(true, "LONG"));
        }

        return Task.FromResult(new EntryDecision(false));
    }

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
