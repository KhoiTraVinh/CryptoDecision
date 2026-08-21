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
///   5. AI     prediction alignment (weight 15%)  — ensemble direction + confidence
///
/// The three flow windows are cumulative — 15m contains 5m, 1h contains both — so
/// they are correlated by construction and the composite moves smoothly rather
/// than snapping. Weighting overlapping windows is the point: agreement across
/// nested horizons is what distinguishes a trend from a single burst.
///
/// LONG entry:  compositeScore >= 65 (bullish bias across timeframes)
/// SHORT entry: compositeScore <= 35 (bearish bias)
/// Dead zone:   35-65 (mixed signals, no entry)
///
/// Exit: TP, trailing stop (momentum-aware tightening), SL.
/// </summary>
public sealed class MomentumStrategy(
    IMomentumRepository        momentumRepo,
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

    /// <summary>
    /// Whale trades needed in the hour before their buy/sell split is treated as a
    /// signal rather than as noise. Below this the component is dropped and its
    /// weight redistributed, which is the honest handling of a sample too small to
    /// read.
    /// </summary>
    private const int     MinWhaleTrades = 3;

    public async Task<EntryDecision> EvaluateEntryAsync(StrategyContext ctx, CancellationToken ct)
    {
        var opts = ctx.Options;

        try
        {
            // ── 1. Multi-timeframe momentum ─────────────────────────────────
            var mtf = await momentumRepo.GetMultiTimeframeAsync(opts.Symbol, ct);

            if (mtf.M5.TotalTrades < MinTrades5m)
                return new EntryDecision(false);

            // Normalize buy ratios to 0-100 scale. 0 = all sells, 100 = all buys.
            //
            // Components are collected with their weights and only included when they
            // actually carry information, then renormalised over the weight present.
            //
            // The alternative — which this replaced — was to substitute a neutral 50
            // for a missing input. That looks harmless and is not: a neutral vote is
            // not an abstention, it is a vote for the dead zone. On SOL the whale
            // term is structurally absent (the 100k USDT threshold is calibrated for
            // BTC and exceeds the largest SOL trade on record) and the AI term is
            // absent until predictions accumulate, so 30% of the score sat pinned at
            // 50 and the flow components had to reach 67 for a composite of 62. The
            // bot was biased toward inaction by its own missing data rather than by
            // the market.
            var components = new List<(string Name, decimal Score, decimal Weight)>
            {
                ("5m", mtf.M5.BuyRatio * 100m, W_5m),
            };

            if (mtf.M15.TotalTrades > 0)
                components.Add(("15m", mtf.M15.BuyRatio * 100m, W_15m));

            if (mtf.M1h.TotalTrades > 0)
                components.Add(("1h", mtf.M1h.VolBuyRatio * 100m, W_1h));

            // ── 2. Whale flow pressure ──────────────────────────────────────
            //
            // Read from the 1h window alone, which now covers the full hour on its
            // own. Summing the three windows would triple-count the last five
            // minutes and double-count the 5-15 minute slice, because the windows
            // are cumulative rather than disjoint — see
            // MomentumRepository.GetMultiTimeframeAsync. Recency is already
            // expressed through the separate 5m/15m/1h flow weights; layering an
            // implicit second recency bias into the whale term would double it.
            // A ratio over one or two trades is not a reading, it is a coin flip that
            // lands on 0 or 100 — the most extreme value the component can take, at
            // full weight. Observed live on SOL: a single whale buy moved the
            // composite from 52.6 to 60.9 while the flow components were leaning
            // *sell*, which is most of the distance to a real-money entry on the
            // evidence of one trade. The flow term has had MinTrades5m guarding it
            // for the same reason; this is that rule applied where it was missing.
            var totalWhales = mtf.M1h.WhaleBuyCount + mtf.M1h.WhaleSellCount;
            var whaleBuys   = mtf.M1h.WhaleBuyCount;

            if (totalWhales >= MinWhaleTrades)
                components.Add(("whale", ((decimal)whaleBuys / totalWhales) * 100m, W_Whale));
            else if (totalWhales > 0)
                log.LogDebug(
                    "[MomentumV2] Ignoring whale flow: only {Count} whale trade(s) in the hour, " +
                    "below the {Min} needed for a ratio to mean anything.",
                    totalWhales, MinWhaleTrades);

            // ── 3. AI prediction ────────────────────────────────────────────
            //
            // A NEUTRAL verdict is excluded along with a missing one. The model
            // saying "no view" carries the same information as having no model, and
            // scoring it as 50 would pull the composite toward the dead zone on the
            // model's indecision rather than on the market's.
            PredictionSnapshot? prediction = null;
            try
            {
                prediction = await predictionRepo.GetLatestAsync(opts.Symbol, ct);
            }
            catch (Exception ex)
            {
                log.LogDebug("[MomentumV2] Failed to read prediction: {Err}", ex.Message);
            }

            if (prediction is not null && prediction.Direction is "UP" or "DOWN")
            {
                var conf = prediction.Confidence;
                var aiScore = prediction.Direction == "UP"
                    ? 50m + (conf * 50m)    // 50-95
                    : 50m - (conf * 50m);   // 5-50

                components.Add(("ai", aiScore, W_AI));
            }

            // ── 4. Weighted composite over the components that have data ─────
            //
            // Renormalised by the weight actually present, so the thresholds keep
            // meaning "the evidence leans this far" regardless of how many sources
            // are reporting. Fewer sources makes the score noisier, not smaller —
            // which is the honest representation of a thinner read.
            var presentWeight = components.Sum(c => c.Weight);
            var composite     = Math.Clamp(
                components.Sum(c => c.Score * c.Weight) / presentWeight, 0m, 100m);

            var rationale = string.Join(" ", components.Select(c => $"{c.Name}:{c.Score:F0}"))
                          + $" → {composite:F1}";

            log.LogInformation(
                "[MomentumV2] {Symbol} composite={Score:F1} [{Detail}] weight={Weight:P0} of full " +
                "(whale_trades={WhaleTotal} ai={AiDir})",
                opts.Symbol, composite, rationale, presentWeight, totalWhales,
                prediction?.Direction ?? "N/A");

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
