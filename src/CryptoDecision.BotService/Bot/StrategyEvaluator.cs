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
        // The retired MOMENTUM strategy's thresholds were symmetric (LONG >= 62, SHORT <= 38), so
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

    /// <param name="clockTrusted">
    /// False when the caller has reason to believe the wall clock moved between
    /// cycles by something other than the passage of time. The timeout branch is
    /// skipped while it is false; every other exit compares prices and is unaffected.
    ///
    /// This parameter exists because of a real loss of control on a live account: two
    /// positions 13 and 16 minutes old were closed in the same instant with reason
    /// TIMEOUT against a 1440-minute threshold. See
    /// <see cref="BotStateService.TouchEval"/> for how the condition is detected.
    /// </param>
    public ExitDecision EvaluateExit(
        BotTrade trade, decimal currentPrice, BotOptions opts, bool clockTrusted = true)
    {
        var rawChange = (currentPrice - trade.EntryPrice) / trade.EntryPrice;
        var changePct = trade.Side == "SHORT" ? -rawChange : rawChange;
        var held = DateTime.UtcNow - trade.OpenedAt;

        // Timeout is universal across all strategies, and is the only exit here that
        // is decided by the clock rather than by a price.
        //
        // A negative hold is checked separately from the untrusted-clock case because
        // it is unambiguous: a position cannot have been opened in the future, so the
        // timestamp or the clock is wrong and neither is a reason to close a position.
        if (held < TimeSpan.Zero)
        {
            _log.LogError(
                "[Exit] Trade {Id} reports a negative hold time ({Held:F1} min): opened_at is " +
                "{Opened:O} but now is {Now:O}. Not applying the timeout — the clock or the row is " +
                "wrong, and neither is a reason to close a real position. Price-based exits still apply.",
                trade.Id, held.TotalMinutes, trade.OpenedAt, DateTime.UtcNow);
        }
        else if (held.TotalMinutes >= opts.MaxHoldMinutes)
        {
            if (clockTrusted)
                return new ExitDecision(true, "TIMEOUT", currentPrice, changePct);

            _log.LogError(
                "[Exit] Trade {Id} would time out at {Held:F1} min against a {Max} min limit, but " +
                "the eval clock jumped this cycle so that figure is not trustworthy. Holding. If the " +
                "position really is this old it will time out next cycle, once the clock is sane.",
                trade.Id, held.TotalMinutes, opts.MaxHoldMinutes);
        }

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
