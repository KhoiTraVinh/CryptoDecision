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
    /// What the bot actually pays: 7 bps.
    ///
    /// <see cref="OkxOrderEngine"/> rests a post-only entry — maker — and exits through
    /// the OCO, which is taker. So it is maker in, taker out, not taker both ways. This
    /// is the figure to model a real round trip with, and it matches the 7 bps measured
    /// on short holds against the account bills.
    ///
    /// It excludes funding, which is charged every eight hours against the position and
    /// settles in the bills rather than on either order. Funding is lumpy and much larger
    /// than this over a long hold — a measured 12-hour hold cost 63 bps all-in — so any
    /// horizon past a few hours needs it added separately rather than folded in here.
    /// </summary>
    public const decimal PostOnlyRoundTrip = OkxMakerFeeRate + OkxTakerFeeRate;

    /// <summary>
    /// 10 bps: taker in and taker out.
    ///
    /// Used where the cost has to be assumed rather than known — placing the stop and
    /// target at entry, before it is settled whether the resting order will fill as maker
    /// or be crossed. Deliberately above <see cref="PostOnlyRoundTrip"/>: erring high puts
    /// the target slightly further out and rejects marginal reward:risk, which fails
    /// toward not trading.
    /// </summary>
    public const decimal TakerRoundTrip = OkxTakerFeeRate * 2m;

    /// <summary>
    /// 21 bps, the backtester's default. Deliberately far above what the venue charges.
    ///
    /// Not a fee estimate — a stress level. Published audits of this class of strategy
    /// found policies that looked viable at an optimistic 10 bps and were solidly negative
    /// at a realistic 21+, once slippage and adverse selection on the resting order are
    /// counted rather than assumed away. A policy that only survives at
    /// <see cref="PostOnlyRoundTrip"/> has not survived.
    /// </summary>
    public const decimal BacktestStressRoundTrip = 0.0021m;
}
