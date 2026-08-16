using CryptoDecision.BotService.Bot;
using CryptoDecision.BotService.Infrastructure;
using CryptoDecision.Shared.Bot;

namespace CryptoDecision.BotService.Strategies;

/// <summary>
/// RSI Strategy: computes a flow-based RSI from buy/sell volume pressure across
/// multiple time windows (5m, 1h). RSI below 30 → oversold → LONG,
/// RSI above 70 → overbought → SHORT. Combines with 1h volume trend confirmation.
/// Uses trailing stop for exits.
/// </summary>
public sealed class RsiStrategy(
    IMomentumRepository       momentumRepo,
    IVolumeRepository         volumeRepo,
    ILogger<RsiStrategy>      log) : ITradingStrategy
{
    public string Name => "RSI";

    // RSI thresholds
    private const decimal OversoldThreshold  = 30m;
    private const decimal OverboughtThreshold = 70m;

    public async Task<EntryDecision> EvaluateEntryAsync(StrategyContext ctx, CancellationToken ct)
    {
        var opts = ctx.Options;

        try
        {
            // ── 1. Get 5-min momentum data ───────────────────────────────────
            var mom = await momentumRepo.GetAsync(opts.Symbol, "ALL", ct);
            if (mom is null || (mom.BuyCount + mom.SellCount) <= 10)
                return new EntryDecision(false);

            // ── 2. Get 1h volume data ────────────────────────────────────────
            var windows = await volumeRepo.GetWindowsAsync(opts.Symbol, "BINANCE", ct);
            var w1h = windows.FirstOrDefault(w => w.Window == "1h");
            if (w1h is null || (w1h.BuyVolumeUsd + w1h.SellVolumeUsd) <= 0)
                return new EntryDecision(false);

            // ── 3. Compute flow-based RSI ────────────────────────────────────
            // RSI = 100 - (100 / (1 + RS))
            // RS = avg_gain / avg_loss ≈ buyVolume / sellVolume (flow-based proxy)
            var buyVol  = w1h.BuyVolumeUsd;
            var sellVol = w1h.SellVolumeUsd;

            // Avoid division by zero
            if (sellVol <= 0) sellVol = 1m;
            if (buyVol <= 0)  buyVol  = 1m;

            var rs  = buyVol / sellVol;
            var rsi = 100m - (100m / (1m + rs));

            // ── 4. 5-min momentum confirmation ──────────────────────────────
            var totalMom  = mom.BuyCount + mom.SellCount;
            var momBuyPct = (decimal)mom.BuyCount / totalMom;

            log.LogInformation("[RsiStrategy] RSI={Rsi:F1} momentum={MomBuy:P1} buyVol={Buy:F0} sellVol={Sell:F0}",
                rsi, momBuyPct, buyVol, sellVol);

            // ── 5. Entry conditions ─────────────────────────────────────────
            // LONG: RSI oversold + short-term momentum starting to favor buys
            if (rsi <= OversoldThreshold && momBuyPct >= 0.50m)
            {
                log.LogInformation("[RsiStrategy] LONG signal: RSI={Rsi:F1} (oversold), momentum confirming", rsi);
                return new EntryDecision(true, "LONG");
            }

            // SHORT: RSI overbought + short-term momentum starting to favor sells
            if (rsi >= OverboughtThreshold && momBuyPct <= 0.50m)
            {
                log.LogInformation("[RsiStrategy] SHORT signal: RSI={Rsi:F1} (overbought), momentum confirming", rsi);
                return new EntryDecision(true, "SHORT");
            }

            return new EntryDecision(false);
        }
        catch (Exception ex)
        {
            log.LogWarning("[RsiStrategy] Error evaluating entry: {Err}", ex.Message);
            return new EntryDecision(false);
        }
    }

    public ExitDecision EvaluateExit(BotTrade trade, decimal currentPrice, BotOptions opts)
    {
        var rawChange = (currentPrice - trade.EntryPrice) / trade.EntryPrice;
        var changePct = trade.Side == "SHORT" ? -rawChange : rawChange;

        // Take Profit
        if (changePct >= opts.TakeProfitPct)
            return new ExitDecision(true, "TP", currentPrice, changePct);

        // Trailing Stop
        if (opts.UseTrailingStop && trade.PeakPrice.HasValue)
        {
            var peak = trade.PeakPrice.Value;
            var retracePct = trade.Side == "SHORT"
                ? (currentPrice - peak) / peak
                : (peak - currentPrice) / peak;

            if (retracePct >= opts.TrailingStopPct)
                return new ExitDecision(true, "TRAILING_STOP", currentPrice, changePct);
        }

        // Stop Loss
        if (changePct <= -opts.StopLossPct)
            return new ExitDecision(true, "SL", currentPrice, changePct);

        return new ExitDecision(false, null, currentPrice, changePct);
    }
}
