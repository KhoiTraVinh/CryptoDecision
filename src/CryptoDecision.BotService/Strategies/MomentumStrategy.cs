using CryptoDecision.BotService.Bot;
using CryptoDecision.BotService.Domain;
using CryptoDecision.BotService.Infrastructure;
using CryptoDecision.Shared.Bot;

namespace CryptoDecision.BotService.Strategies;

/// <summary>
/// Enhanced Momentum Strategy v2 — Multi-timeframe composite scoring + AI prediction integration.
///
/// Signal components (weighted composite score 0-100):
///   1. 5-min  momentum  buy ratio  (weight 25%)  — immediate pressure
///   2. 15-min momentum  buy ratio  (weight 25%)  — short-term trend confirmation
///   3. 1h     volume    buy ratio  (weight 20%)  — hourly trend confirmation
///   4. Whale  flow      pressure   (weight 15%)  — smart money direction
///   5. AI     prediction alignment (weight 15%)  — ML/heuristic direction + confidence
///
/// LONG entry:  compositeScore >= 65 (bullish bias across timeframes)
/// SHORT entry: compositeScore <= 35 (bearish bias)
/// Dead zone:   35-65 (mixed signals, no entry)
///
/// Exit: TP, trailing stop (momentum-aware tightening), SL.
/// </summary>
public sealed class MomentumStrategy(
    IMomentumRepository        momentumRepo,
    IVolumeRepository          volumeRepo,
    IPredictionRepository      predictionRepo,
    ILogger<MomentumStrategy>  log) : ITradingStrategy
{
    public string Name => "MOMENTUM";

    // ── Composite weights ──
    private const decimal W_5m    = 0.25m;
    private const decimal W_15m   = 0.25m;
    private const decimal W_1h    = 0.20m;
    private const decimal W_Whale = 0.15m;
    private const decimal W_AI    = 0.15m;

    // ── Entry thresholds ──
    private const decimal LongThreshold  = 62m;   // composite score >= 62 → LONG
    private const decimal ShortThreshold = 38m;    // composite score <= 38 → SHORT
    private const int     MinTrades5m    = 5;      // minimum trades in 5m to have signal

    public async Task<EntryDecision> EvaluateEntryAsync(StrategyContext ctx, CancellationToken ct)
    {
        var opts = ctx.Options;

        try
        {
            // ── 1. Multi-timeframe momentum ─────────────────────────────────
            var mtf = await momentumRepo.GetMultiTimeframeAsync(opts.Symbol, ct);

            if (mtf.M5.TotalTrades < MinTrades5m)
                return new EntryDecision(false);

            // Normalize buy ratios to 0-100 scale
            var score5m  = mtf.M5.BuyRatio * 100m;   // 0=all sells, 100=all buys
            var score15m = mtf.M15.TotalTrades > 0 ? mtf.M15.BuyRatio * 100m : 50m;
            var score1h  = mtf.M1h.TotalTrades > 0 ? mtf.M1h.VolBuyRatio * 100m : 50m;

            // ── 2. Whale flow pressure ──────────────────────────────────────
            // WhalePressure is [-0.5, +0.5], normalize to [0, 100]
            var totalWhales = mtf.M5.WhaleBuyCount + mtf.M5.WhaleSellCount
                            + mtf.M15.WhaleBuyCount + mtf.M15.WhaleSellCount
                            + mtf.M1h.WhaleBuyCount + mtf.M1h.WhaleSellCount;
            var whaleBuys   = mtf.M5.WhaleBuyCount + mtf.M15.WhaleBuyCount + mtf.M1h.WhaleBuyCount;

            decimal whaleScore;
            if (totalWhales > 0)
            {
                whaleScore = ((decimal)whaleBuys / totalWhales) * 100m;
            }
            else
            {
                whaleScore = 50m; // no whale data → neutral
            }

            // ── 3. AI prediction ────────────────────────────────────────────
            decimal aiScore = 50m; // default neutral
            PredictionSnapshot? prediction = null;
            try
            {
                prediction = await predictionRepo.GetLatestAsync(opts.Symbol, ct);
                if (prediction != null)
                {
                    // Map direction + confidence to 0-100 score
                    var conf = prediction.Confidence;
                    aiScore = prediction.Direction switch
                    {
                        "UP"   => 50m + (conf * 50m),   // 50-95 range
                        "DOWN" => 50m - (conf * 50m),   // 5-50 range
                        _      => 50m                     // NEUTRAL
                    };
                }
            }
            catch (Exception ex)
            {
                log.LogDebug("[MomentumV2] Failed to read prediction: {Err}", ex.Message);
            }

            // ── 4. Weighted composite score ─────────────────────────────────
            var composite = (score5m  * W_5m)
                          + (score15m * W_15m)
                          + (score1h  * W_1h)
                          + (whaleScore * W_Whale)
                          + (aiScore  * W_AI);

            composite = Math.Clamp(composite, 0m, 100m);

            // Build rationale string
            var rationale = $"5m:{score5m:F0} 15m:{score15m:F0} 1h:{score1h:F0} whale:{whaleScore:F0} ai:{aiScore:F0} → {composite:F1}";

            log.LogInformation(
                "[MomentumV2] {Symbol} composite={Score:F1} [{Detail}] whale_trades={WhaleTotal} ai={AiDir}({AiConf:P0})",
                opts.Symbol, composite, rationale, totalWhales,
                prediction?.Direction ?? "N/A", prediction?.Confidence ?? 0);

            // ── 5. Entry decision ───────────────────────────────────────────

            // AI filter: if enabled, block entries that conflict with AI prediction
            if (opts.UseAiFilter && prediction != null && prediction.Confidence >= opts.MinAiConfidence)
            {
                if (composite >= LongThreshold && prediction.Direction == "DOWN")
                {
                    log.LogInformation("[MomentumV2] LONG blocked by AI filter (AI says DOWN with {Conf:P0})", prediction.Confidence);
                    return new EntryDecision(false, Rationale: $"LONG blocked by AI: {rationale}");
                }
                if (composite <= ShortThreshold && prediction.Direction == "UP")
                {
                    log.LogInformation("[MomentumV2] SHORT blocked by AI filter (AI says UP with {Conf:P0})", prediction.Confidence);
                    return new EntryDecision(false, Rationale: $"SHORT blocked by AI: {rationale}");
                }
            }

            // Determine confidence for position sizing
            var entryConfidence = Math.Abs(composite - 50m) / 50m; // 0.0 = dead center, 1.0 = extreme

            // AI alignment bonus: if AI agrees, boost confidence
            if (prediction != null && prediction.Confidence >= 0.5m)
            {
                if ((composite >= LongThreshold && prediction.Direction == "UP") ||
                    (composite <= ShortThreshold && prediction.Direction == "DOWN"))
                {
                    entryConfidence = Math.Min(1m, entryConfidence + prediction.Confidence * 0.2m);
                }
            }

            if (composite >= LongThreshold)
            {
                log.LogInformation("[MomentumV2] LONG signal: composite={Score:F1} confidence={Conf:P0}", composite, entryConfidence);
                return new EntryDecision(true, "LONG", entryConfidence, rationale);
            }

            if (composite <= ShortThreshold)
            {
                log.LogInformation("[MomentumV2] SHORT signal: composite={Score:F1} confidence={Conf:P0}", composite, entryConfidence);
                return new EntryDecision(true, "SHORT", entryConfidence, rationale);
            }

            return new EntryDecision(false, Rationale: $"Dead zone ({composite:F1}): {rationale}");
        }
        catch (Exception ex)
        {
            log.LogWarning("[MomentumV2] Error evaluating entry: {Err}", ex.Message);
            return new EntryDecision(false);
        }
    }

    public ExitDecision EvaluateExit(BotTrade trade, decimal currentPrice, BotOptions opts)
    {
        var rawChange = (currentPrice - trade.EntryPrice) / trade.EntryPrice;
        var changePct = trade.Side == "SHORT" ? -rawChange : rawChange;

        if (changePct >= opts.TakeProfitPct)
            return new ExitDecision(true, "TP", currentPrice, changePct);

        if (opts.UseTrailingStop && trade.PeakPrice.HasValue)
        {
            var peak = trade.PeakPrice.Value;
            var retracePct = trade.Side == "SHORT"
                ? (currentPrice - peak) / peak
                : (peak - currentPrice) / peak;

            if (retracePct >= opts.TrailingStopPct)
                return new ExitDecision(true, "TRAILING_STOP", currentPrice, changePct);
        }

        if (changePct <= -opts.StopLossPct)
            return new ExitDecision(true, "SL", currentPrice, changePct);

        return new ExitDecision(false, null, currentPrice, changePct);
    }
}
