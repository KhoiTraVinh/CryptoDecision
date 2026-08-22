namespace CryptoDecision.Shared.Bot;

/// <summary>The size an order should be, and the reasoning that produced it.</summary>
/// <param name="NotionalUsd">USD to commit, after every adjustment.</param>
/// <param name="AdjustedPct">Fraction of capital this represents.</param>
/// <param name="VolatilityScalar">How far volatility shrank the position (1.0 = untouched).</param>
/// <param name="ConfidenceScalar">How far AI confidence scaled it (1.0 = untouched).</param>
public sealed record PositionSize(
    decimal NotionalUsd,
    decimal AdjustedPct,
    double  VolatilityScalar,
    decimal ConfidenceScalar
);

/// <summary>
/// How large a position should be, given capital, volatility and confidence.
///
/// Shared by the paper and the live engine on purpose. Two copies of this
/// arithmetic would drift, and the copy that drifts silently is the one nobody
/// watches — which is exactly the wrong way round, because a sizing bug in paper
/// mode costs a misleading backtest while the same bug live costs money. One
/// implementation means the numbers an operator validated on paper are the
/// numbers the exchange is asked for.
/// </summary>
public static class PositionSizer
{
    /// <summary>Volatility the base position size is calibrated for, in percent.</summary>
    public const double BaseVolatilityPct = 2.0;

    public static PositionSize Resolve(
        decimal capitalUsd,
        decimal positionPct,
        double  currentVolatilityPct,
        decimal confidence,
        bool    useAiSizing)
    {
        // Volatility-adjusted sizing: above the calibration point the position
        // shrinks in proportion, floored at half size. Never scaled up — a quiet
        // market is not a reason to bet more.
        var volScalar   = Math.Clamp(BaseVolatilityPct / Math.Max(currentVolatilityPct, 0.5), 0.5, 1.0);
        var adjustedPct = positionPct * (decimal)volScalar;

        // AI confidence sizing: confidence 0.0 → 0.5x, 0.5 → 1.0x, 1.0 → 1.5x.
        var confidenceScalar = 1.0m;
        if (useAiSizing && confidence > 0m)
        {
            confidenceScalar = Math.Clamp(0.5m + confidence, 0.5m, 1.5m);
            adjustedPct     *= confidenceScalar;
        }

        return new PositionSize(
            NotionalUsd:      Math.Round(capitalUsd * adjustedPct, 2),
            AdjustedPct:      adjustedPct,
            VolatilityScalar: volScalar,
            ConfidenceScalar: confidenceScalar);
    }
}
