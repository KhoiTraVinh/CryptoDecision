using CryptoDecision.BotService.Infrastructure;
using CryptoDecision.Shared.Bot;
using CryptoDecision.Shared.Signals;

namespace CryptoDecision.BotService.Bot;

/// <summary>
/// How large an entry should be, before the venue's contract grid is applied.
/// </summary>
/// <param name="Size">The raw sizing decision, with the scalars that produced it.</param>
/// <param name="NotionalUsd">USD to commit, after the per-order ceiling.</param>
/// <param name="AskedNotionalUsd">
/// What sizing wanted before the ceiling. Equal to <paramref name="NotionalUsd"/> when the
/// ceiling did not bind.
/// </param>
/// <param name="RiskBased">
/// True when the strategy supplied a stop distance and fixed-fractional sizing was used.
/// False means the volatility-scaled percentage-of-capital fallback.
/// </param>
/// <param name="VolatilityPct">
/// The daily range fed to the fallback rule, or null on the risk-based path where no such
/// reading is taken. Only there to be logged.
/// </param>
public sealed record SizedEntry(
    PositionSize Size,
    decimal      NotionalUsd,
    decimal      AskedNotionalUsd,
    bool         RiskBased,
    decimal?     VolatilityPct)
{
    /// <summary>Whether the per-order ceiling shrank this order.</summary>
    public bool CapBound => AskedNotionalUsd > NotionalUsd;
}

/// <summary>
/// The sizing decision, in one place, for both the paper and the live engine.
///
/// Both engines used to carry their own copy of this: the same two-rule branch, the same
/// per-order ceiling, the same arithmetic. The comment above each copy said the two had to
/// stay in step — which is a note that a reader must do by hand what the compiler would do
/// for free, and it is exactly the arrangement that had paper P&amp;L running 4.5x live
/// before the ceiling was added to the paper side.
///
/// Only the arithmetic moved here. Each engine still logs in its own voice, because what a
/// simulated fill and a real order need to say about the same number is not the same thing:
/// the live one restates the risk after the cap, since once the ceiling binds
/// <c>risk_pct_per_trade</c> stops describing anything.
/// </summary>
public static class EntrySizing
{
    /// <summary>
    /// Volatility assumed when no feature row exists for today. Matches
    /// <see cref="PositionSizer.BaseVolatilityPct"/>, so an absent reading sizes as though
    /// the market were at the calibration point rather than shrinking or inflating blindly.
    /// </summary>
    private const decimal FallbackVolatilityPct = 2.0m;

    /// <summary>
    /// Two sizing rules, chosen by whether the strategy supplied a stop distance.
    ///
    /// With one, size is whatever makes that stop cost <c>risk_pct_per_trade</c> of capital,
    /// so every trade risks the same money whatever the regime. Without one, fall back to
    /// the older percentage-of-capital rule, which is all a strategy that does not express a
    /// stop distance can support.
    ///
    /// They must not be combined. Sizing a fraction of capital and <em>then</em> shrinking it
    /// for volatility, while the stop separately widens for volatility, adjusts one decision
    /// with two readings of the same thing: on this account a 15.9% daily volatility pinned
    /// the scalar at its 0.5 floor and halved every order, while the 15-minute ATR that sets
    /// the stop was 1.07%.
    /// </summary>
    public static async Task<SizedEntry> ResolveAsync(
        IFeatureRepository featureRepo,
        string             symbol,
        decimal            capitalUsd,
        decimal            positionPct,
        decimal            riskPctPerTrade,
        decimal            maxOrderNotionalUsd,
        StopGeometry?      geometry,
        decimal            confidence,
        bool               useAiSizing,
        CancellationToken  ct)
    {
        PositionSize size;
        decimal?     volatilityPct = null;
        bool         riskBased;

        if (geometry is { StopPct: > 0m } g)
        {
            size      = PositionSizer.ResolveByRisk(
                            capitalUsd, riskPctPerTrade, g.StopPct, confidence, useAiSizing);
            riskBased = true;
        }
        else
        {
            var feature = await featureRepo.GetTodayAsync(symbol, ct);
            volatilityPct = feature?.Volatility ?? FallbackVolatilityPct;
            size          = PositionSizer.Resolve(
                                capitalUsd, positionPct, (double)volatilityPct.Value,
                                confidence, useAiSizing);
            riskBased     = false;
        }

        // The per-order ceiling, applied identically on both paths. It only ever shrinks the
        // order, so it can lower the realised risk below risk_pct_per_trade but never raise
        // it — see the caller's log line for what that costs in interpretability.
        //
        // A non-positive ceiling means NO ceiling, not a ceiling of zero. The unguarded
        // Math.Min read it as the latter and sized every order at $0, which the live engine
        // catches at startup (OkxOptions.DescribeRefusal refuses a non-positive value) but
        // paper mode never validates — so a misconfigured deployment would have paper-traded
        // zero-size positions and reported them as fills. TradingBotService already treats
        // it this way when it builds the gate's brief; these two now agree.
        var asked    = size.NotionalUsd;
        var notional = maxOrderNotionalUsd > 0m ? Math.Min(asked, maxOrderNotionalUsd) : asked;

        return new SizedEntry(size, notional, asked, riskBased, volatilityPct);
    }
}
