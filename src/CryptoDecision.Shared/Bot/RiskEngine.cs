namespace CryptoDecision.Shared.Bot;

/// <summary>
/// Pure risk arithmetic over a bot configuration and its realised trade history.
///
/// This exists because the bot shipped with TakeProfitPct = 0.3% against
/// StopLossPct = 5%. With a 0.2% round-trip fee that is a net win of 0.1% against
/// a net loss of 5.2% — a configuration that needs a 98% win rate to break even
/// and will drain an account regardless of how good the entry signal is.
///
/// Nothing here reads the database or mutates state, so every rule is directly
/// testable and the same arithmetic runs in the live bot, the backtester and the
/// API's config validation.
/// </summary>
public static class RiskEngine
{
    /// <summary>Round-trip taker fee: 0.1% in, 0.1% out. Binance spot without BNB discount.</summary>
    public const decimal DefaultRoundTripFeeRate = 0.002m;

    /// <summary>
    /// Above this required win rate a configuration is treated as unachievable.
    /// Sustained edges beyond ~65% on liquid crypto pairs are vanishingly rare, so
    /// anything needing more than 70% just to break even is a losing setup.
    /// </summary>
    public const decimal ImplausibleWinRate = 0.70m;

    /// <summary>Below this reward:risk ratio a configuration is flagged as inverted.</summary>
    public const decimal MinHealthyRewardRisk = 0.8m;

    // ── Expectancy ────────────────────────────────────────────────────────────

    /// <summary>
    /// What a configuration mathematically requires in order to make money,
    /// before any judgement about whether the strategy can deliver it.
    /// </summary>
    public static ExpectancyProfile Expectancy(
        decimal takeProfitPct,
        decimal stopLossPct,
        decimal roundTripFeeRate = DefaultRoundTripFeeRate)
    {
        // Fees are paid on both legs whichever way the trade goes, so they shrink
        // the win and widen the loss. Ignoring them is what makes a 0.3% target
        // look viable when two thirds of it is eaten before the trade resolves.
        var netWinPct  = takeProfitPct - roundTripFeeRate;
        var netLossPct = stopLossPct  + roundTripFeeRate;

        var denominator = netWinPct + netLossPct;

        // Breakeven win rate: p·netWin = (1-p)·netLoss  →  p = netLoss / (netWin + netLoss)
        var breakevenWinRate = denominator > 0m
            ? netLossPct / denominator
            : 1m;

        var rewardRisk = netLossPct > 0m ? netWinPct / netLossPct : 0m;

        return new ExpectancyProfile(
            TakeProfitPct:    takeProfitPct,
            StopLossPct:      stopLossPct,
            RoundTripFeeRate: roundTripFeeRate,
            NetWinPct:        netWinPct,
            NetLossPct:       netLossPct,
            RewardRiskRatio:  rewardRisk,
            BreakevenWinRate: Math.Clamp(breakevenWinRate, 0m, 1m));
    }

    /// <summary>
    /// Expected value per trade in currency, given an assumed win rate.
    /// Positive means the configuration makes money at that hit rate.
    /// </summary>
    public static decimal ExpectedValuePerTrade(
        ExpectancyProfile profile, decimal assumedWinRate, decimal notionalUsd)
    {
        var p = Math.Clamp(assumedWinRate, 0m, 1m);
        return notionalUsd * (p * profile.NetWinPct - (1m - p) * profile.NetLossPct);
    }

    // ── Configuration validation ──────────────────────────────────────────────

    /// <summary>
    /// Check a configuration for setups that cannot profit, or that risk more of
    /// the account than intended. Returns findings; the caller decides whether to
    /// warn or refuse to start.
    /// </summary>
    /// <param name="realizedVolatilityPct">
    /// The day's high-low range as a percentage of its open, if known — the same
    /// figure PositionSizer already uses to shrink positions. Optional so existing
    /// callers keep working; without it the volatility check is skipped rather than
    /// guessed at.
    /// </param>
    public static RiskAssessment Validate(
        BotOptions opts,
        decimal roundTripFeeRate = DefaultRoundTripFeeRate,
        decimal? realizedVolatilityPct = null)
    {
        var findings = new List<RiskFinding>();
        var profile  = Expectancy(opts.TakeProfitPct, opts.StopLossPct, roundTripFeeRate);

        // ── Fees swallowing the target ──
        if (profile.NetWinPct <= 0m)
        {
            findings.Add(new RiskFinding(
                RiskSeverity.Critical,
                "TAKE_PROFIT_BELOW_FEES",
                $"Take profit {opts.TakeProfitPct:P2} does not cover the {roundTripFeeRate:P2} " +
                "round-trip fee. Every winning trade still loses money."));
        }
        else if (roundTripFeeRate / opts.TakeProfitPct > 0.33m)
        {
            findings.Add(new RiskFinding(
                RiskSeverity.Warning,
                "FEES_DOMINATE_TARGET",
                $"Fees consume {roundTripFeeRate / opts.TakeProfitPct:P0} of the {opts.TakeProfitPct:P2} " +
                "target. Widen the target or trade less often."));
        }

        // ── Inverted reward:risk ──
        if (profile.BreakevenWinRate >= ImplausibleWinRate)
        {
            findings.Add(new RiskFinding(
                RiskSeverity.Critical,
                "IMPLAUSIBLE_WIN_RATE",
                $"This TP/SL needs a {profile.BreakevenWinRate:P1} win rate just to break even " +
                $"(reward:risk {profile.RewardRiskRatio:F2}:1). One loss undoes " +
                $"{(profile.NetWinPct > 0m ? Math.Round(profile.NetLossPct / profile.NetWinPct, 1) : 0m)} wins."));
        }
        else if (profile.RewardRiskRatio < MinHealthyRewardRisk)
        {
            findings.Add(new RiskFinding(
                RiskSeverity.Warning,
                "LOW_REWARD_RISK",
                $"Reward:risk is {profile.RewardRiskRatio:F2}:1, so a {profile.BreakevenWinRate:P1} " +
                "win rate is required to break even."));
        }

        // ── Aggregate exposure ──
        // Every strategy can hold MaxOpenTradesPerStrategy positions at once, so
        // worst-case exposure multiplies across the whole active set.
        var strategyCount  = Math.Max(1, opts.ActiveStrategies?.Count ?? 1);
        var maxConcurrent  = strategyCount * Math.Max(1, opts.MaxOpenTradesPerStrategy);
        var maxExposurePct = maxConcurrent * opts.PositionPctOfCapital;

        if (maxExposurePct > 1m)
        {
            findings.Add(new RiskFinding(
                RiskSeverity.Critical,
                "OVER_ALLOCATED",
                $"{strategyCount} strategies × {opts.MaxOpenTradesPerStrategy} positions × " +
                $"{opts.PositionPctOfCapital:P0} = {maxExposurePct:P0} of capital committed at once. " +
                "This exceeds the account."));
        }
        else if (maxExposurePct > 0.60m)
        {
            findings.Add(new RiskFinding(
                RiskSeverity.Warning,
                "HIGH_EXPOSURE",
                $"Worst-case exposure is {maxExposurePct:P0} of capital across {maxConcurrent} positions."));
        }

        // ── Worst-case drawdown vs the daily loss limit ──
        // If every open position stops out together, does the daily limit even bind?
        var worstCaseLossPct = maxExposurePct * profile.NetLossPct;
        if (worstCaseLossPct > opts.DailyLossLimitPct)
        {
            findings.Add(new RiskFinding(
                RiskSeverity.Warning,
                "DAILY_LIMIT_UNREACHABLE_IN_TIME",
                $"All positions stopping out at once costs {worstCaseLossPct:P1} of capital, " +
                $"past the {opts.DailyLossLimitPct:P1} daily limit. The limit is only checked " +
                "between evaluation cycles, so it cannot prevent this."));
        }

        // ── Trailing stop tighter than the noise it sits in ──
        if (opts.UseTrailingStop && opts.TrailingStopPct <= roundTripFeeRate)
        {
            findings.Add(new RiskFinding(
                RiskSeverity.Warning,
                "TRAILING_STOP_TOO_TIGHT",
                $"A {opts.TrailingStopPct:P2} trailing stop is inside the {roundTripFeeRate:P2} " +
                "fee band and will exit on noise."));
        }

        // ── Trailing stop small against the day's actual range ──
        //
        // The fee check above compares the trailing stop to a fixed cost and passes
        // anything above 0.2%. It therefore said nothing on 2026-08-22, when a 1.20%
        // trailing stop sat inside a 15.76% daily range and four consecutive entries
        // were stopped out having moved at most +0.29% in their favour — the stop was
        // being hit by ordinary intraday movement, not by the trade being wrong.
        //
        // A quarter of the daily range is a rule of thumb, not a derivation: intraday
        // retracements routinely run a quarter to a third of the day's span, so a stop
        // inside that is expected to be caught by noise. It is a warning, never a
        // block — a tight stop can be a deliberate choice, and on those same four
        // trades it did beat the exchange's wider stop.
        if (opts.UseTrailingStop && realizedVolatilityPct is > 0m)
        {
            var trailingPct = opts.TrailingStopPct * 100m;
            var quarterRange = realizedVolatilityPct.Value / 4m;

            if (trailingPct < quarterRange)
            {
                findings.Add(new RiskFinding(
                    RiskSeverity.Warning,
                    "TRAILING_STOP_INSIDE_VOLATILITY",
                    $"A {opts.TrailingStopPct:P2} trailing stop is well inside today's " +
                    $"{realizedVolatilityPct.Value:F2}% range — under a quarter of it " +
                    $"({quarterRange:F2}%). Expect exits on ordinary movement rather than " +
                    "on the trade being wrong."));
            }
        }

        // ── Stop loss the bot itself can never reach ──
        //
        // MomentumStrategy.EvaluateExit tests the trailing stop before the stop loss,
        // so a trailing stop tighter than the stop loss always fires first and the
        // bot's own stop-loss branch is unreachable.
        //
        // Worth saying, and worth being precise about: the stop loss is NOT disabled.
        // It is armed at the exchange as the OCO's slTriggerPx, which is what catches
        // a gap between the bot's 30-second polls and what protects the position when
        // the bot is not running at all. What this describes is dead code and a
        // misleading configuration reading, not an unprotected position: the typical
        // exit is the trailing stop, while the worst case remains the stop loss.
        if (opts.UseTrailingStop && opts.TrailingStopPct < opts.StopLossPct)
        {
            findings.Add(new RiskFinding(
                RiskSeverity.Warning,
                "BOT_STOP_LOSS_UNREACHABLE",
                $"The {opts.TrailingStopPct:P2} trailing stop is checked before the " +
                $"{opts.StopLossPct:P2} stop loss and is tighter, so the bot's stop-loss " +
                $"branch never fires. Typical risk per trade is {opts.TrailingStopPct:P2}; " +
                $"{opts.StopLossPct:P2} remains the worst case, enforced by the exchange OCO."));
        }

        // ── Breakeven trigger unreachable before the target ──
        if (opts.UseBreakevenStop && opts.BreakevenTriggerPct >= opts.TakeProfitPct)
        {
            findings.Add(new RiskFinding(
                RiskSeverity.Warning,
                "BREAKEVEN_AFTER_TARGET",
                $"Breakeven arms at {opts.BreakevenTriggerPct:P2} but the trade closes at " +
                $"{opts.TakeProfitPct:P2}, so it never engages."));
        }

        return new RiskAssessment(profile, findings);
    }

    // ── Circuit breakers over realised history ────────────────────────────────

    /// <summary>
    /// Evaluate stop conditions against closed trades, newest first.
    /// Returns the first breach found, or null when trading may continue.
    /// </summary>
    public static CircuitBreak? CheckCircuitBreakers(
        IReadOnlyList<BotTrade> closedTradesNewestFirst,
        BotOptions opts,
        decimal todayPnlUsd,
        int maxConsecutiveLosses = 5,
        decimal maxDrawdownPct = 0.20m)
    {
        // ── Daily loss limit ──
        var dailyLimitUsd = opts.CapitalUsd * opts.DailyLossLimitPct;
        if (todayPnlUsd <= -dailyLimitUsd)
        {
            return new CircuitBreak(
                "DAILY_LOSS_LIMIT",
                $"Today's P&L {todayPnlUsd:C2} breached the {dailyLimitUsd:C2} daily limit.");
        }

        if (closedTradesNewestFirst.Count == 0) return null;

        // ── Consecutive losses ──
        var streak = 0;
        foreach (var trade in closedTradesNewestFirst)
        {
            if ((trade.PnlUsd ?? 0m) >= 0m) break;
            streak++;
        }
        if (streak >= maxConsecutiveLosses)
        {
            return new CircuitBreak(
                "CONSECUTIVE_LOSSES",
                $"{streak} losing trades in a row (limit {maxConsecutiveLosses}). " +
                "The signal is likely out of regime.");
        }

        // ── Peak-to-trough drawdown on the realised equity curve ──
        var drawdown = MaxDrawdownPct(closedTradesNewestFirst, opts.CapitalUsd);
        if (drawdown >= maxDrawdownPct)
        {
            return new CircuitBreak(
                "MAX_DRAWDOWN",
                $"Realised drawdown {drawdown:P1} reached the {maxDrawdownPct:P1} limit.");
        }

        return null;
    }

    /// <summary>
    /// Largest peak-to-trough decline of the realised equity curve, as a fraction
    /// of peak equity. Input is newest-first; the curve is walked chronologically.
    /// </summary>
    public static decimal MaxDrawdownPct(
        IReadOnlyList<BotTrade> closedTradesNewestFirst, decimal startingCapital)
    {
        if (startingCapital <= 0m || closedTradesNewestFirst.Count == 0) return 0m;

        var equity   = startingCapital;
        var peak     = startingCapital;
        var maxDrop  = 0m;

        for (var i = closedTradesNewestFirst.Count - 1; i >= 0; i--)
        {
            equity += closedTradesNewestFirst[i].PnlUsd ?? 0m;
            if (equity > peak) peak = equity;
            if (peak <= 0m) continue;

            var drop = (peak - equity) / peak;
            if (drop > maxDrop) maxDrop = drop;
        }

        return maxDrop;
    }
}

// ── Result types ──────────────────────────────────────────────────────────────

public enum RiskSeverity
{
    /// <summary>Worth surfacing, but trading may proceed.</summary>
    Warning,
    /// <summary>The configuration cannot profit, or risks more than the account.</summary>
    Critical,
}

public sealed record RiskFinding(RiskSeverity Severity, string Code, string Message);

/// <summary>What a TP/SL pair requires arithmetically, net of fees.</summary>
public sealed record ExpectancyProfile(
    decimal TakeProfitPct,
    decimal StopLossPct,
    decimal RoundTripFeeRate,
    decimal NetWinPct,
    decimal NetLossPct,
    decimal RewardRiskRatio,
    decimal BreakevenWinRate
)
{
    /// <summary>How many winning trades a single loss cancels out.</summary>
    public decimal WinsPerLoss => NetWinPct > 0m ? NetLossPct / NetWinPct : decimal.MaxValue;
}

public sealed record RiskAssessment(
    ExpectancyProfile Expectancy,
    IReadOnlyList<RiskFinding> Findings
)
{
    public bool HasCritical => Findings.Any(f => f.Severity == RiskSeverity.Critical);
    public bool IsClean     => Findings.Count == 0;

    public IEnumerable<RiskFinding> Critical => Findings.Where(f => f.Severity == RiskSeverity.Critical);
    public IEnumerable<RiskFinding> Warnings => Findings.Where(f => f.Severity == RiskSeverity.Warning);
}

public sealed record CircuitBreak(string Code, string Message);
