namespace CryptoDecision.Shared.Bot;

/// <summary>
/// What a round trip on this account actually costs, in one place.
///
/// This exists because four different numbers were in force at once, none of them
/// obviously wrong on its own, and no two of them agreeing:
///
///     PaperOrderEngine.FeeRate               3.5 bps a leg  →  7 bps round trip
///     FlowStrategyOptions.RoundTripFeeRate                     10 bps
///     RiskEngine.DefaultRoundTripFeeRate                       20 bps
///     Backtest --cost-bps                                      21 bps
///
/// So a paper fill was charged 7 bps, the stop and target guarding it were placed
/// assuming 10, the startup risk report judged the configuration at 20 — against a
/// documented "Binance spot without BNB discount", a venue and product this bot has not
/// traded in months — and the backtester certifying the whole thing used 21. On a
/// strategy whose measured break-even margin is single-digit basis points, a 3 bps
/// disagreement between what paper charges and what the geometry assumes is not rounding.
///
/// Config drift across tools has now cost this repo three parameters and one certified-
/// but-never-run configuration. The rule that came out of that is the one applied here:
/// one named constant per real-world fact, and every consumer either uses it or documents
/// why it deliberately differs.
/// </summary>
public static class TradingCosts
{
    /// <summary>OKX perpetual swap maker fee, one leg. Lv1: 0.02%.</summary>
    public const decimal OkxMakerFeeRate = 0.0002m;

    /// <summary>OKX perpetual swap taker fee, one leg. Lv1: 0.05%.</summary>
    public const decimal OkxTakerFeeRate = 0.0005m;

    /// <summary>
    /// 10 bps: taker in and taker out.
    ///
    /// Used where the cost has to be assumed rather than known — placing the stop and
    /// target at entry, before it is settled whether the resting order will fill as maker
    /// or be crossed. Deliberately above the 7 bps the bot actually pays on a post-only
    /// entry: erring high puts the target slightly further out and rejects marginal
    /// reward:risk, which fails toward not trading.
    ///
    /// The two composite constants that used to sit here — the 7 bps maker-in/taker-out
    /// round trip and the 21 bps backtest stress level — were deleted on 2026-09-20 once
    /// nothing read either of them: PaperOrderEngine charges the two leg rates above
    /// separately, and the backtester was deleted on 2026-09-11. Both numbers, and the
    /// reasoning that makes them worth knowing, are in HYPOTHESES.md under "Removed
    /// features".
    /// </summary>
    public const decimal TakerRoundTrip = OkxTakerFeeRate * 2m;
}
