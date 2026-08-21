using CryptoDecision.BotService.Strategies;
using CryptoDecision.Shared.Bot;

namespace CryptoDecision.BotService.Bot;

/// <summary>
/// Thin coordinator that resolves ITradingStrategy by name from DI.
/// No strategy logic lives here — each strategy is a separate class (Strategy Pattern / OCP).
/// Also supplies the live price, from whichever venue orders are going to.
/// </summary>
public sealed class StrategyEvaluator
{
    private readonly IReadOnlyDictionary<string, ITradingStrategy> _strategies;
    private readonly PriceFeedResolver _prices;
    private readonly IOrderEngine _orderEngine;
    private readonly ILogger<StrategyEvaluator> _log;

    public StrategyEvaluator(
        IEnumerable<ITradingStrategy> strategies,
        PriceFeedResolver             prices,
        IOrderEngine                  orderEngine,
        ILogger<StrategyEvaluator>    log)
    {
        // Build a lookup dictionary keyed by strategy name for O(1) resolution
        _strategies  = strategies.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        _prices      = prices;
        _orderEngine = orderEngine;
        _log         = log;

        _log.LogInformation("[StrategyEvaluator] Registered strategies: [{Names}]",
            string.Join(", ", _strategies.Keys));
    }

    // ── Live price from the execution venue ───────────────────────────────────

    public Task<decimal?> GetCurrentPriceAsync(BotOptions opts, CancellationToken ct)
        => _prices.GetPriceAsync(opts, ct);

    // ── Entry Evaluation (delegates to resolved strategy) ─────────────────────

    public async Task<EntryDecision> ShouldEnterAsync(
        string strategy,
        BotOptions opts,
        IReadOnlyList<BotTrade> openTrades,
        decimal currentPrice,
        CancellationToken ct)
    {
        if (!_strategies.TryGetValue(strategy, out var impl))
        {
            _log.LogWarning("[StrategyEvaluator] Unknown strategy '{Strategy}', skipping", strategy);
            return new EntryDecision(false);
        }

        var ctx      = new StrategyContext(opts, openTrades, currentPrice);
        var decision = await impl.EvaluateEntryAsync(ctx, ct);

        // ── Drop signals the execution venue cannot fill ──
        //
        // MomentumStrategy's thresholds are symmetric (LONG >= 62, SHORT <= 38), so
        // on a spot account roughly half its actionable signals are unfillable. The
        // order engine refuses them correctly, but refusing there turns a known
        // structural constraint into a stream of errors that buries the real ones.
        // Filtered here instead, at Information level, because a long-only run is a
        // valid configuration rather than a fault.
        if (decision.Pass && decision.Side == "SHORT" && !_orderEngine.SupportsShort(opts))
        {
            _log.LogInformation(
                "[StrategyEvaluator] {Strategy} signalled SHORT but {Exchange} cannot short in this " +
                "mode (spot is long-only). Skipping the entry. [{Rationale}]",
                strategy, opts.Exchange, decision.Rationale ?? "no rationale");

            return new EntryDecision(false, Rationale: $"SHORT not executable on {opts.Exchange} spot");
        }

        return decision;
    }

    // ── Exit Evaluation (delegates to resolved strategy) ──────────────────────

    public ExitDecision EvaluateExit(BotTrade trade, decimal currentPrice, BotOptions opts)
    {
        var rawChange = (currentPrice - trade.EntryPrice) / trade.EntryPrice;
        var changePct = trade.Side == "SHORT" ? -rawChange : rawChange;
        var held = DateTime.UtcNow - trade.OpenedAt;

        // Timeout is universal across all strategies
        if (held.TotalMinutes >= opts.MaxHoldMinutes)
            return new ExitDecision(true, "TIMEOUT", currentPrice, changePct);

        // ── Breakeven stop (universal) ──────────────────────────────────────
        // Once trade reaches BreakevenTriggerPct profit, treat entry price as floor.
        // If price retraces back to entry → close at breakeven (risk-free exit).
        if (opts.UseBreakevenStop && changePct < 0m && trade.PeakPrice.HasValue)
        {
            var peakChange = trade.Side == "SHORT"
                ? -(trade.PeakPrice.Value - trade.EntryPrice) / trade.EntryPrice
                : (trade.PeakPrice.Value - trade.EntryPrice) / trade.EntryPrice;

            // Peak was above breakeven trigger, but current price reverted to entry
            if (peakChange >= opts.BreakevenTriggerPct)
            {
                _log.LogInformation(
                    "[Breakeven] Trade {Id} peak was +{Peak:P2} but now {Cur:P2} → closing at breakeven",
                    trade.Id, peakChange, changePct);
                return new ExitDecision(true, "BREAKEVEN", currentPrice, changePct);
            }
        }

        if (!_strategies.TryGetValue(trade.Strategy, out var impl))
            return new ExitDecision(false, null, currentPrice, changePct);

        // ── Dynamic TP/SL scaling (universal) ───────────────────────────────
        // Adjust thresholds based on volatility: high vol → wider TP/SL
        var effectiveOpts = opts;
        if (opts.UseDynamicTpSl && trade.PeakPrice.HasValue)
        {
            // Use peak-to-entry ratio as a volatility proxy for the trade
            var volatilityFactor = Math.Abs(
                (trade.PeakPrice.Value - trade.EntryPrice) / trade.EntryPrice);
            var scale = Math.Max(1m, 1m + volatilityFactor * 10m); // scale 1x-2x
            scale = Math.Min(scale, 2m);

            effectiveOpts = new BotOptions
            {
                TakeProfitPct  = opts.TakeProfitPct * scale,
                StopLossPct    = opts.StopLossPct * scale,
                UseTrailingStop = opts.UseTrailingStop,
                TrailingStopPct = opts.TrailingStopPct * scale,
                UseBreakevenStop    = opts.UseBreakevenStop,
                BreakevenTriggerPct = opts.BreakevenTriggerPct,
            };
        }

        return impl.EvaluateExit(trade, currentPrice, effectiveOpts);
    }
}

public sealed record ExitDecision(bool ShouldExit, string? Reason, decimal CurrentPrice, decimal ChangePct);
