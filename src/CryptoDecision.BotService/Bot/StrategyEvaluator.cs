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
    public async Task<ExitDecision> EvaluateExitAsync(
        BotTrade trade, decimal currentPrice, BotOptions opts, bool clockTrusted = true,
        CancellationToken ct = default)
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

        // Dynamic TP/SL was scaled here too, and that copy is gone.
        //
        // It multiplied opts.TakeProfitPct and opts.StopLossPct by 1 + 10x excursion,
        // capped at 2x — the same formula CrossVenueFlowStrategy applies to the STORED
        // geometry. Two implementations of one rule, and this was the one that could not
        // do anything: those two percentages are read only by the no-geometry fallback,
        // and every trade that carries geometry — which is every trade the live strategy
        // opens — never reaches them.
        //
        // Deleting it rather than wiring it up, for two reasons. The duplicate formula is
        // a drift waiting to happen: H12 is open on the scaling rule and a second copy
        // with the same magic numbers would have to be found and changed alongside it.
        // And the fallback is a fallback — it exists for a trade whose geometry write
        // failed, and scaling an emergency stop by a volatility proxy is not a thing to
        // do to a position that is already in trouble.
        return await impl.EvaluateExitAsync(trade, currentPrice, opts, ct);
    }
}

public sealed record ExitDecision(bool ShouldExit, string? Reason, decimal CurrentPrice, decimal ChangePct);
