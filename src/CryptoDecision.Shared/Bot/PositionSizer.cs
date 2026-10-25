namespace CryptoDecision.Shared.Bot;

/// <summary>The size an order should be, and the reasoning that produced it.</summary>
/// <param name="NotionalUsd">USD to commit, after every adjustment.</param>
/// <param name="AdjustedPct">Fraction of capital this represents.</param>
/// <param name="VolatilityScalar">How far volatility shrank the position (1.0 = untouched).</param>
public sealed record PositionSize(
    decimal NotionalUsd,
    decimal AdjustedPct,
    double  VolatilityScalar
);

/// <summary>
/// How large a position should be, given capital and volatility.
///
/// Shared by the paper and the live engine on purpose. Two copies of this
/// arithmetic would drift, and the copy that drifts silently is the one nobody
/// watches — which is exactly the wrong way round, because a sizing bug in paper
/// mode costs a misleading backtest while the same bug live costs money. One
/// implementation means the numbers an operator validated on paper are the
/// numbers the exchange is asked for.
///
/// Confidence scaling used to live here, gated by bot_config.use_ai_sizing: it
/// multiplied the position by 0.5 + confidence, clamped to [0.5, 1.5]. Both
/// inputs were dead. The flag was false throughout, and the only strategy that
/// sets a confidence sets a literal 0 — "Confidence: there isn't one", because
/// the expression behind it (|AggregateZ| / (EnterZ * 2)) lost its numerator
/// when the venue-scoring rule was deleted on 2026-09-11. Removed 2026-10-25.
/// </summary>
public static class PositionSizer
{
    /// <summary>Volatility the base position size is calibrated for, in percent.</summary>
    public const double BaseVolatilityPct = 2.0;

    public static PositionSize Resolve(
        decimal capitalUsd,
        decimal positionPct,
        double  currentVolatilityPct)
    {
        // Volatility-adjusted sizing: above the calibration point the position
        // shrinks in proportion, floored at half size. Never scaled up — a quiet
        // market is not a reason to bet more.
        var volScalar   = Math.Clamp(BaseVolatilityPct / Math.Max(currentVolatilityPct, 0.5), 0.5, 1.0);
        var adjustedPct = positionPct * (decimal)volScalar;

        return new PositionSize(
            NotionalUsd:      Math.Round(capitalUsd * adjustedPct, 2),
            AdjustedPct:      adjustedPct,
            VolatilityScalar: volScalar);
    }

    /// <summary>
    /// Size so that hitting the stop costs a fixed fraction of capital.
    ///
    /// <code>notional = (capital × riskPct) / stopPct</code>
    ///
    /// Why this replaces <see cref="Resolve"/> wherever a stop distance is known
    /// -------------------------------------------------------------------------
    /// The older method sizes a fraction of capital and then shrinks it when
    /// volatility is high. That was coherent while the stop was a constant. It is not
    /// coherent now that the stop is scaled to measured volatility, because the two
    /// adjustments compound in the same direction: a volatile day gives a smaller
    /// position <em>and</em> a wider stop, so risk per trade collapses, while a quiet
    /// day gives a full position with a tight stop. Nobody chose that schedule — it
    /// falls out of two independent knobs both reading volatility.
    ///
    /// Measured on this account: with daily volatility at 15.9% the scalar pinned at
    /// its 0.5 floor and halved every order, while the 15-minute ATR that sets the stop
    /// was 1.07%. Two different volatility measures adjusting the same decision.
    ///
    /// Fixed fractional risk removes the interaction. The stop distance already carries
    /// the volatility information, so the position is simply whatever size makes that
    /// distance cost the intended amount. Every trade then risks the same money
    /// regardless of regime — which is what discipline means once it is written as
    /// arithmetic rather than intent.
    /// </summary>
    /// <param name="riskPctOfCapital">
    /// Fraction of capital to lose if the stop is hit. This is now the only risk knob
    /// that matters; position_pct no longer participates.
    /// </param>
    /// <param name="stopPct">
    /// Distance from entry to the stop, as a fraction. Must be positive — a zero stop
    /// implies infinite size, so it is refused rather than clamped.
    /// </param>
    public static PositionSize ResolveByRisk(
        decimal capitalUsd,
        decimal riskPctOfCapital,
        decimal stopPct)
    {
        if (stopPct <= 0m)
            throw new ArgumentOutOfRangeException(
                nameof(stopPct), stopPct,
                "Stop distance must be positive; sizing off a zero stop implies an unbounded position.");

        var riskUsd  = capitalUsd * Math.Max(0m, riskPctOfCapital);
        var notional = riskUsd / stopPct;

        return new PositionSize(
            NotionalUsd:      Math.Round(notional, 2),
            AdjustedPct:      capitalUsd > 0m ? notional / capitalUsd : 0m,
            // Reported as 1.0 because nothing here scales on volatility any more. The
            // volatility adjustment lives in the stop distance, which is the input.
            VolatilityScalar: 1.0);
    }
}
