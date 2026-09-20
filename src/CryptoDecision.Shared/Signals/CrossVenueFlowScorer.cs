namespace CryptoDecision.Shared.Signals;

/// <summary>
/// Which rule turns flow into an entry.
///
/// NUMBERING STARTS AT 2, ON PURPOSE. ZScore (0) and OfiMagnitude (1) were removed on
/// 2026-09-18 and the survivors keep the values they had. Renumbering would silently
/// change what an existing `entry_mode` string or a stored integer resolves to, and this
/// repository has already been bitten twice by a configured mode differing from the
/// running one. The gap is cheaper than the reassignment.
///
/// Why they went: z tracks the past — aggregate z correlates +0.467 with the PRECEDING
/// hour's return and −0.116 with the following one — and OfiMagnitude was never promoted
/// past the experiment. What justified keeping them implemented was the ability to
/// compare rules over the same history, and the backtester that did the comparing was
/// deleted on 2026-09-11. Keeping ~660 lines reachable only through a config typo is not
/// optionality, it is surface. `git log` has them if they are ever wanted back.
/// </summary>
public enum FlowEntryMode
{
    /// <summary>
    /// Fade the last move: go long once price has fallen at least
    /// <see cref="FlowSignalOptions.ReversalDropPct"/>, or short once it has risen at
    /// least <see cref="FlowSignalOptions.ReversalRisePct"/>, over the last
    /// <see cref="FlowSignalOptions.ReversalBars"/> closed candles. No order flow in
    /// it at all — only price.
    ///
    /// The two thresholds are different numbers because the two sides are not
    /// symmetric: the dip pays from 0.60% and the rally does not pay until 1.00%.
    /// The table for each is on the option it belongs to.
    ///
    /// It is here because the flow data does not contain direction. Over 1,496 buckets
    /// aggregate OFI correlates −0.015 with the next hour's signed return, and four
    /// rules built on flow (z-score, OFI sign flip, volume burst, prior-return
    /// momentum) all landed within noise of going long at random. Price's own recent
    /// shape carries more than the flow does.
    ///
    /// Measured on the 30-minute change into the entry, return from the next candle's
    /// open:
    ///
    ///     prior 30m       n       +30m     +1h      +2h    hit@1h
    ///     fell >=0.60%   IN  78  +0.237  +0.249  +0.181   61.5%
    ///                    OOS 40  +0.115  +0.208  +0.026   65.0%
    ///     fell 0.35-0.60 IN  75  +0.026  -0.044  -0.197   42.7%
    ///                    OOS 61  +0.013  +0.062  +0.014   52.5%
    ///     ROSE           IN 291  -0.021  -0.066  -0.049   43.0%
    ///                    OOS 228 -0.023  -0.068  -0.036   43.9%
    ///
    /// The 0.60% threshold is where it starts working and is not a tuned value — the
    /// band below it flips sign between halves, and the band above is positive at
    /// every horizon in both halves at better than 60% hit. At one hour it returns
    /// three times the 7 bps round trip.
    ///
    /// The bottom row is the same finding from the other side: buying after a RISE
    /// loses, in both halves, at every horizon, on n=519. Refusing those is half of
    /// what this mode does, and it costs nothing because it prevents trades rather
    /// than creating them.
    ///
    /// Long only. Shorting after a rise is the mirror image and measures +0.066/+0.068
    /// at one hour — real, but sitting exactly on the 0.070% round trip, so it pays
    /// the exchange rather than the account.
    ///
    /// The honest discount: SOL rose 12.65% over the sample, about 0.044% of drift per
    /// hour. Perhaps a fifth of the one-hour figure is the market going up rather than
    /// the pattern working, and none of this has been seen in a falling market.
    /// </summary>
    CandleReversal = 2,

    /// <summary>
    /// Enter WITH the side that dominated the last closed bucket: buy volume at least
    /// <see cref="FlowSignalOptions.RatioMinimum"/> times sell volume (or the reverse
    /// for a short), on at least <see cref="FlowSignalOptions.RatioMinVolumeUsd"/> of
    /// notional. Price is not consulted at all.
    ///
    /// The opposite sign to every rule above it, and the first to enter with the tape
    /// rather than against it. See <see cref="CrossVenueFlowScorer.ScoreFlowRatio"/>
    /// for the measurement, including why it does not contradict the finding that
    /// aggregate OFI carries no direction: that figure averages the whole
    /// distribution, and this rule only reads its extreme tail.
    ///
    /// Wants the ATR geometry, which since 2026-09-19 is the only one there is. Entering
    /// with the dominant side puts price at the edge of its own range, so the range
    /// geometry's boundary target sat almost on top of the entry and failed MinRewardRisk
    /// — that is why it was deleted despite measuring better on its own terms, and the
    /// tables are in HYPOTHESES.md under "Removed features". A 2.00% stop floor with
    /// TargetRiskMultiple 2.0 gives the 2%/4% pair this was measured on.
    /// </summary>
    FlowRatio = 3,
}

// The block below documents FlowSignalOptions and lived at the top of this file, above
// `public enum FlowEntryMode` -- so every <param> tag described a parameter the enum
// does not have, and the summary called the enum "tunable thresholds for
// CrossVenueFlowScorer". Tooling showed nothing on the record and the wrong text on the
// enum. Moved 2026-09-12; no wording changed.

/// <summary>
/// Tunable thresholds for <see cref="CrossVenueFlowScorer"/>.
///
/// Every field here is a parameter rather than a constant because every one of
/// them used to be a hand-picked literal deployed against real funds without ever
/// being tested — 62 and 38 for the entry thresholds, 0.15 for the dead zone,
/// 25/25/20/15/15 for the composite weights. None of those numbers had units and
/// none had a derivation. Binding them as one record is what makes a threshold in
/// production traceable to the measurement that chose it rather than to something that
/// sounded reasonable; the sweeps themselves are in HYPOTHESES.md, each with its
/// decision rule fixed in advance.
///
/// The defaults below are starting points, not recommendations.
/// </summary>
/// <param name="MaxDispersionBps">
/// Ceiling on cross-venue VWAP dispersion. Wide dispersion means thin books or a
/// move already in progress — entering into it is paying for information the
/// market has already priced. Set to 0 to disable the check.
///
/// **Disabled (0) on 2026-08-28 by operator decision — see H3.** What is known about
/// it, so that turning it back on is a decision rather than a guess:
///
///   • It did bind, but rarely: one `VENUE_DISPERSION_TOO_WIDE` abstention in the
///     24 hours audited, against 23 other abstentions for other reasons.
///   • It carries no visible information about outcomes in the data that exists. On
///     the fifteen signals with known results the two winners sat at 4.3 and 7.5 bps
///     while losers spanned 2.2 to 13.2 bps.
///   • Every actionable signal on record was between 2.2 and 13.2 bps, so at 25 the
///     ceiling sat at roughly double the observed maximum.
///
/// Disabling it also removes the entry gate's most-used excuse as a side effect:
/// AiEntryGate permits the "late entry" ground only at 80% of the ceiling or above,
/// and with no ceiling the brief states plainly that the ground is unavailable. The
/// gate refused eight entries in one day on that ground at 2.8-13.2 bps.
/// </param>
public sealed record FlowSignalOptions(
    // 0 disables the check, and it IS 0 — so nothing reads this as a ceiling anywhere.
    //
    // NO SCORER CONSUMES IT. ScoreFlowRatio and ScoreReversal both leave
    // FlowVerdict.DispersionBps at 0, and the dispersion check the two deleted modes
    // performed went with them. The value survives only as CONTEXT in the gate brief,
    // where it is explicitly labelled "not a ground" — see AiEntryGate. It is left in
    // place rather than deleted because removing it would edit the gate's prompt while
    // H17 is being measured on that prompt; remove it once H17 closes.
    //
    // The comment here used to say "the backtester takes this default rather than a CLI
    // flag". There has been no backtester since 2026-09-11.
    double  MaxDispersionBps              = 0.0,

    /// <summary>
    /// Which rule decides an entry. See <see cref="FlowEntryMode"/>.
    ///
    /// FlowRatio, because that is what production runs. It defaulted to ZScore on the
    /// argument that "nothing changes unless it is configured" — which was right while
    /// ZScore was the deployed rule and became the drift it was written to prevent the
    /// moment appsettings moved to FlowRatio: a backtest taken without an explicit mode
    /// measured the retired rule and reported it as the strategy.
    ///
    /// The other three modes stay implemented and one config edit away. Keeping them
    /// costs nothing and is what lets the rules be compared on the same history
    /// rather than one silently replacing another.
    /// </summary>
    FlowEntryMode EntryMode               = FlowEntryMode.FlowRatio,


    /// <summary>
    /// Minimum fall, in percent, over <see cref="ReversalBars"/> closed candles before
    /// <see cref="FlowEntryMode.CandleReversal"/> will go long.
    ///
    /// 0.60 is where the effect starts rather than where it is largest: the 0.35-0.60
    /// band flips sign between sample halves while everything at or beyond 0.60 is
    /// positive at every horizon in both. Lowering it to catch more trades reaches
    /// straight into the band that does not work.
    /// </summary>
    double  ReversalDropPct               = 0.60,

    /// <summary>
    /// Closed 15-minute candles the FALL is measured over, for the long side.
    /// 2 = 30 minutes. See <see cref="ReversalBarsShort"/> for the rise.
    /// </summary>
    int     ReversalBars                  = 2,

    /// <summary>
    /// Closed 15-minute candles the RISE is measured over, for the short side.
    /// 1 = a single bar.
    ///
    /// Separate from <see cref="ReversalBars"/> because the two sides responded in
    /// opposite directions to the same change. Measured over the 19-day production
    /// window, varying only the window the trigger move is measured on:
    ///
    ///     window    SHORT n   mean R   1st half   2nd half     LONG n   mean R
    ///       15m        17     +1.168    +1.467     +0.619        53     -0.213
    ///       30m        46     +0.055    -0.014     +0.272       117     -0.151
    ///        1h        91     -0.027    +0.009     -0.127       191     -0.195
    ///        2h       166     -0.222    -0.154     -0.337       273     -0.215
    ///
    /// The short gradient is monotone and has a mechanism behind it: a 1% rise inside
    /// a SINGLE 15-minute bar is a spike, usually a liquidation cascade, and fading a
    /// spike is a different trade from fading a 1% rise that took two hours to build,
    /// which is a trend. One window could not tell those apart. The long side does not
    /// share the gradient and stays at 2.
    ///
    /// WHAT THE +1.168 IS NOT. Two trades carry it. A single +13.32R on 2026-08-22,
    /// the day SOL ranged 87.72-102.74 (17% in one day), is 67% of the net; adding the
    /// next largest reaches 96%, and the remaining fifteen trades total +0.83R — a mean
    /// of +0.055R, which is exactly what the 30-minute window already produces. This is
    /// a fat-tailed, low-frequency setup: roughly one trade a day, eight of seventeen
    /// stopped out, and its expectancy sits in rare large wins. That shape needs far
    /// more evidence than a normal one before the number means anything, and it is
    /// shipped on the operator's decision with that stated. H7 in HYPOTHESES.md carries
    /// a decision rule that discards the largest win before judging, for this reason.
    /// </summary>
    int     ReversalBarsShort             = 1,

    /// <summary>
    /// Minimum RISE, in percent, over <see cref="ReversalBars"/> closed candles before
    /// <see cref="FlowEntryMode.CandleReversal"/> will go short. Read only when
    /// <see cref="ReversalLongOnly"/> is false.
    ///
    /// Deliberately NOT the mirror of <see cref="ReversalDropPct"/>, and that is the
    /// whole finding. Fading a rally at the same 0.60% that works for buying a dip
    /// hands the entire edge to the exchange; the rally has to be bigger before the
    /// fade pays for its own round trip. Measured on 1,562 closed 15-minute buckets of
    /// SOLUSDT from production (2026-08-21 to 2026-09-09), short return over the two
    /// hours after the signal bucket:
    ///
    ///     rise &gt;=   n    2h return   win %   avg favourable   avg adverse
    ///       0.60%   126     +0.086%    59.5%       1.359%          1.200%
    ///       0.80%    79     +0.133%    64.6%       1.541%          1.269%
    ///       1.00%    49     +0.348%    67.3%       1.994%          1.460%
    ///       1.25%    27     +0.495%    74.1%       2.283%          1.589%
    ///       1.50%    20     +0.471%    70.0%       2.291%          1.537%
    ///
    /// Against a 0.070% round trip, 0.60% returns 1.2x its cost and 1.00% returns 5x.
    /// 1.00 is taken rather than the higher-scoring 1.25 because 1.25 rests on 27
    /// observations and the curve is flat past it — the extra return sits inside the
    /// noise of the extra thinning, and picking the argmax of a curve this short is
    /// how this repository has manufactured edges before.
    ///
    /// Split across sample halves, both of which are positive:
    ///
    ///     rise &gt;= 1.00%   first half  n 38  +0.419%  68.4% win
    ///                     second half n 11  +0.103%  63.6% win
    ///
    /// The second half is thin and its magnitudes are smaller across every rule
    /// including the long one, because that stretch was quieter (average favourable
    /// excursion 0.88% against 2.32%). The sign holds; the size is not established.
    /// Registered as H5 in HYPOTHESES.md.
    /// </summary>
    double  ReversalRisePct               = 1.00,

    /// <summary>
    /// Refuse the short side.
    ///
    /// Was true, on a measurement that only ever tested the mirrored threshold:
    /// shorting after a 0.60% rise measures +0.066 in-sample and +0.068 out-of-sample
    /// at one hour against a 0.070% round trip, which is a real effect that hands its
    /// entire value to the exchange. That number is still correct, and it is exactly
    /// why <see cref="ReversalRisePct"/> is a separate parameter rather than a sign
    /// flip on <see cref="ReversalDropPct"/>.
    ///
    /// Now false: the same market gives the short side +0.348% over two hours once the
    /// rise threshold is raised to 1.00%. Turning it back on is a config edit.
    /// </summary>
    bool    ReversalLongOnly              = false,

    /// <summary>
    /// For <see cref="FlowEntryMode.FlowRatio"/>: how far the dominant side must
    /// outweigh the other, as a plain volume ratio. 2.5 means buy volume at least 2.5x
    /// sell volume for a long.
    ///
    /// **2.1 -> 2.5 on 2026-09-20 (H23), to match the short side.** This is the one
    /// threshold change in the H20-H23 sequence that lands INSIDE its own measurement
    /// rather than outside it: the original sweep found a three-cell plateau at 1.8, 2.1
    /// and 2.5, all positive overall after discarding the largest winner and in both
    /// sample halves, with 3.0 failing the outlier check on 12 observations. 2.1 was
    /// picked from the middle of that plateau; 2.5 is its top edge, still on the plateau.
    ///
    /// It also moves toward the evidence rather than away: the RATIO path is negative on
    /// both sides so far — LONG -0.248R over 9, SHORT -0.749R over 3 — so raising the bar
    /// on the long side is not tightening a rule that was working.
    ///
    /// Cost in coverage, measured on 30 days: buy-dominated buckets clearing the floor and
    /// the ratio fall from 34 to 16, about 0.53/day.
    /// </summary>
    decimal RatioMinimum                  = 2.5m,

    /// <summary>
    /// For <see cref="FlowEntryMode.FlowRatio"/>: minimum notional in the qualifying
    /// bucket, in USD.
    ///
    /// A separate condition from the ratio, not a refinement of it. Below $3M a 2:1
    /// imbalance measured -0.012 mean R once the largest winner was removed and -0.082
    /// in the first sample half; above it, +0.332 and +0.521. A 2:1 lean on two million
    /// dollars is what a quiet hour looks like, and it predicts nothing.
    ///
    /// **$3M -> $2.5M on 2026-09-20 (H23), to match the short side. THIS ONE GOES AGAINST
    /// THE MEASUREMENT ABOVE** and the paragraph is left intact rather than softened: $2.5M
    /// reaches half a million dollars into the band that measured -0.012R. The argument for
    /// it is that the band was measured at a **2:1** imbalance and the ratio is now 2.5:1,
    /// so the thin-volume cases it describes are largely excluded by the ratio instead —
    /// but that is a reason to expect it to be survivable, not evidence that it is. H23
    /// carries it as the weaker half of a symmetry change.
    /// </summary>
    decimal RatioMinVolumeUsd             = 2_500_000m,

    /// <summary>
    /// For <see cref="FlowEntryMode.FlowRatio"/>: bucket notional at or above which the
    /// ratio test is SKIPPED and the entry is taken with whichever side traded more,
    /// however narrow its lead. 0 disables the exception and leaves the ratio in charge.
    ///
    /// The idea is to catch a news print — a bucket so large that the event itself is
    /// the signal — and ride it rather than waiting for a 2.1:1 lean that a stampede
    /// never produces. The mechanism is real and visible in the data: ratio FALLS as
    /// volume rises (mean 1.40 under $5M against 1.19-1.31 over $30M), so the two rules
    /// are nearly disjoint and this one does reach buckets the ratio never will —
    /// only 1 of the 42 buckets over $20M also cleared 2.1:1.
    ///
    /// IT FAILED ITS PRE-TEST. Measured on 1,770 buckets, 2026-08-21 to 2026-09-11,
    /// entering 18 minutes after the bucket opens (close plus the settle wait) and
    /// holding 12 hours, signed to the dominant side:
    ///
    ///     threshold    n     +1h      +12h    1st half   2nd half   less top 1
    ///       12M      112   -0.162   -0.035    +0.013     -0.182      -0.104
    ///       15M       76   +0.062   -0.032    +0.076     -0.330      -0.137
    ///       20M       41   +0.160   +0.141    +0.276     -0.341      -0.054
    ///       25M       25   +0.004   -0.174    -0.166     -0.197      -0.527
    ///       30M       18   -0.048   -0.239    -0.252     -0.201      -0.823
    ///
    /// 20M is a single positive cell between negative neighbours, which is the shape
    /// this repository has taught itself to distrust — 15M and 25M are both negative and
    /// each is further from 20M than 20M is from zero. The last column is the decisive
    /// one: at EVERY threshold, including the chosen one, removing the single best trade
    /// turns the mean negative. And the second half is negative everywhere.
    ///
    /// For comparison, on the same rows and the same arithmetic, the ratio rule that is
    /// already running: n 45, +0.838% at 12h, 56.8% hit, +1.631 / +0.385 across halves,
    /// +0.712 after removing its best trade. It passes all three checks; this passes one.
    ///
    /// A weaker ratio filter on top does not rescue it. Within the 20M buckets, by ratio
    /// band: &lt;1.2 gives +0.202, 1.2-1.4 +0.200, 1.4-1.6 +0.866, and 1.6+ gives
    /// **-1.004** — the wrong way round, on 6 to 17 observations a band, with every band
    /// flipping sign between halves.
    ///
    /// The one number that is not bad is +0.160% at ONE hour, which is better than the
    /// ratio rule's +0.139% at the same horizon. If this rule has anything in it, it is
    /// a fast trade, and the machinery around it is built for a 12-hour hold that costs
    /// roughly 63 bps in funding — which is most of what a 12-hour edge of 14 bps would
    /// ever earn. Judging it on the shipped exits is judging it on the wrong exits.
    ///
    /// Shipped on the operator's decision with all of the above stated, in paper mode,
    /// as H11 in HYPOTHESES.md. Set to 0 to remove it.
    /// </summary>
    decimal RatioHighVolumeUsd            = 20_000_000m,


    /// <summary>
    /// For <see cref="FlowEntryMode.FlowRatio"/>: minutes to wait after a bucket closes
    /// before trusting its numbers.
    ///
    /// Closed is not settled. Trades arrive late and the aggregation worker folds them
    /// in on a two-minute cycle, so a just-closed bucket is still moving. Measured cost
    /// of the wait, as entry price given up: 0.001R at one minute, 0.016R at three,
    /// 0.020R at five — against a measured edge of 0.379R. Three minutes buys a settled
    /// number for 4% of the edge.
    /// </summary>
    int     RatioSettleMinutes            = 3,

    /// <summary>
    /// For <see cref="FlowEntryMode.FlowRatio"/>: how far SELL volume must outweigh buy
    /// before a SHORT is allowed, in place of <see cref="RatioMinimum"/>. 0 makes the two
    /// sides symmetric again.
    ///
    /// Applied AFTER the high-volume waiver, so the news-print path keeps taking shorts at
    /// any ratio. H20 put this ahead of the waiver and thereby changed two rules on one
    /// decision; keeping them separate means each can be judged on its own.
    /// </summary>
    decimal ShortRatioMinimum             = 2.5m,

    /// <summary>
    /// For <see cref="FlowEntryMode.FlowRatio"/>: the notional floor a SHORT must clear,
    /// in USD, in place of <see cref="RatioMinVolumeUsd"/>. 0 falls back to that one.
    ///
    /// **NOTE IT IS BELOW THE LONG SIDE'S $3M**, deliberately: under H22 the ratio does the
    /// work on the short side and the notional is only a sanity floor. The -0.012 mean R
    /// measured below $3M is a pooled figure over both sides at 2:1, not a short-side
    /// measurement at 2.5:1, so it does not directly argue against this.
    ///
    /// **This pair is the third setting of the short rule in one day**, after H20 (3.0x +
    /// |OFI| 0.60, unreachable) and H21 ($10M + 2.1x, one matching bucket in 30 days).
    /// Unlike both, it produces a testable population: over 30 days to 2026-09-20,
    /// **11 sell-dominated buckets clear $2.5M AND 2.5x**, spread from 08-27 to 09-20
    /// rather than clustered, roughly 0.37/day. See H22, including the count of how many
    /// times this parameter has now moved.
    /// </summary>
    decimal ShortMinVolumeUsd             = 2_500_000m)
{
    /// <summary>
    /// How many closed buckets to load before scoring.
    ///
    /// This was <c>SignalBars + BaselineBars</c> — 4 + 44 — because ZScore standardised a
    /// 4-bucket window against 44 buckets of trailing history. Neither rule that survives
    /// reads a baseline: FlowRatio needs exactly one closed bucket and CandleReversal
    /// reads candles, not flow.
    ///
    /// It stays at 48 all the same, and the number is now honest about what it is: the
    /// depth of history the repository fetch asks for, not a statistical window. Two
    /// things still depend on it — the OFI exit sums <c>FlowOfiBars</c> buckets, and
    /// sql/021's readiness view calls a symbol ready at 48 — so shrinking it is a change
    /// to those, not a tidy-up, and wants its own reason.
    /// </summary>
    public int MinimumBars => 48;
}


/// <summary>
/// One closed 15-minute bucket's market-wide aggressive-flow imbalance.
/// </summary>
/// <param name="VolumeUsd">
/// Total notional behind the reading. Carried so a caller can tell a genuine
/// market-wide lean from an imbalance measured on a nearly empty bucket, which on a
/// quiet night looks identical in the <paramref name="Ofi"/> alone.
/// </param>
public readonly record struct BucketOfi(DateTime Bucket, double Ofi, decimal VolumeUsd);

/// <summary>
/// The entry rules FlowRatio can fire on, as constants rather than loose strings.
///
/// These values are written to bot_trades.entry_path and are read back by the position
/// limit, so a typo here is a limit that silently never binds. Naming them once is the
/// difference between a constraint and a string comparison that happens to work.
/// </summary>
public static class EntryPaths
{
    /// <summary>The imbalance cleared RatioMinimum on at least RatioMinVolumeUsd.</summary>
    public const string Ratio = "RATIO";

    /// <summary>
    /// The bucket cleared RatioHighVolumeUsd, so the ratio test was waived and the entry
    /// took whichever side was heavier. See H11 — measured, and shipped knowing it.
    /// </summary>
    public const string HighVolume = "HIGH_VOLUME";
}

/// <summary>
/// The scorer's answer for one decision bucket.
///
/// <see cref="AbstainCode"/> is always populated when not actionable, and it is
/// deliberately a small enumerable vocabulary rather than free text. A bot that
/// refused every entry for hours looked, from the outside, identical to one waiting
/// for a signal — RUNNING, healthy, silent. Counting abstain codes turns "why is
/// nothing happening" into a query.
/// </summary>
public sealed record FlowVerdict(
    bool     Actionable,
    string?  Side,
    double   AggregateOfi,
    double   AggregateZ,
    int      AgreeingVenues,
    int      ParticipatingVenues,
    double   DispersionBps,
    string   AbstainCode,
    string   Reason,

    /// <summary>
    /// Which rule produced an actionable verdict: <see cref="EntryPaths.Ratio"/>,
    /// <see cref="EntryPaths.HighVolume"/>, or empty when the mode has only one way in.
    ///
    /// A field rather than something a caller recovers from <see cref="Reason"/>. The
    /// last time a branch in this codebase was driven by matching prose — the gate's
    /// "unavailable" state, recognised by the first two words of its reason string — it
    /// silently missed four of six paths and blocked a live entry by a rule the operator
    /// had switched off. Callers branch on this, and it is persisted to
    /// bot_trades.entry_path so a position can still be attributed to its rule hours
    /// later, after the sentence that produced it is gone.
    /// </summary>
    string   EntryPath = "")
{
    public static FlowVerdict Abstain(
        string code,
        string reason,
        double aggregateOfi = 0.0,
        double aggregateZ = 0.0,
        int agreeing = 0,
        int participating = 0,
        double dispersionBps = 0.0)
        => new(false, null, aggregateOfi, aggregateZ, agreeing, participating,
               dispersionBps, code, reason);
}

/// <summary>
/// Turns per-venue 15-minute flow buckets into an entry verdict, or into an
/// explicit refusal to have a view.
///
/// This is a pure function over its inputs. It holds no state, touches no clock and
/// performs no I/O, which is what lets the live bot and the backtester call the
/// identical code — the single most important property here, because the previous
/// signal existed only as a live database scan and therefore could not be tested at
/// all.
///
/// What it measures
/// ----------------
/// Aggressive (taker) order-flow imbalance, volume-weighted, per venue, on disjoint
/// clock-aligned buckets, standardised against each venue's own trailing
/// distribution, and then required to agree across venues.
///
/// What it deliberately does not do
/// --------------------------------
/// It does not blend a price-return term, a daily aggregate or a model forecast into
/// a single composite score. The strategy this replaces averaged five components of
/// different units and horizons into one number and compared it to a threshold,
/// which meant no individual condition was ever actually required: a strong enough
/// reading on two components carried an entry past components that were absent,
/// stale or contradicting. Conditions here are conjunctive and each one can veto.
///
/// Abstaining is the expected outcome
/// ----------------------------------
/// Most buckets produce no verdict, and that is the design. Published audits of
/// candle-based crypto timing models found strong predictive discrimination
/// (ROC AUC 0.73-0.97) coexisting with returns of -1.79% to -44.30%, and identified
/// mandatory coverage — being obliged to pick a trade every period — as one of the
/// direct causes. Optional participation is a first-class feature of the policy,
/// not a failure of the signal.
/// </summary>
public static class CrossVenueFlowScorer
{
    // ── Mode: CandleReversal ──────────────────────────────────────────────────

    /// <summary>
    /// Score the buy-the-dip rule. See <see cref="FlowEntryMode.CandleReversal"/>.
    ///
    /// Takes candles rather than flow bars because it reads price and nothing else —
    /// a FlowBar carries VWAP, which is the bucket's average rather than its close, and
    /// substituting one for the other would not be the rule that was measured. That is
    /// also why it takes a different argument type from <see cref="ScoreFlowRatio"/>:
    /// the strategy switches on EntryMode and calls one or the other directly. The
    /// <c>Score</c> dispatch this used to go through went with the ZScore rule on
    /// 2026-09-18.
    ///
    /// The verdict it returns is shaped like any other so everything downstream — the
    /// gate brief, signal_outcomes, the geometry — keeps working unchanged. The flow
    /// fields are zero because no flow was consulted, and that is the honest value: a
    /// row with AggregateZ = 0 under this mode means "not measured", not "measured as
    /// nothing".
    /// </summary>
    /// <param name="candles">1-minute candles, oldest first.</param>
    /// <param name="nowUtc">Used to exclude the 15-minute bar still in progress.</param>
    public static FlowVerdict ScoreReversal(
        IReadOnlyList<Candle> candles, DateTime nowUtc, FlowSignalOptions options)
    {
        // Two windows, one per side. The dip is measured over ReversalBars and the
        // rise over ReversalBarsShort, because the two sides are not the same trade:
        // see ReversalBarsShort for the gradient that separated them.
        var dipBars  = Math.Max(1, options.ReversalBars);
        var riseBars = Math.Max(1, options.ReversalBarsShort);
        var needed   = Math.Max(dipBars, riseBars) + 1;

        if (candles.Count == 0)
            return FlowVerdict.Abstain("NO_CANDLES", "No 1-minute candles available.");

        // Resample to the 15-minute grid, then drop the bar still filling. Reading a
        // live bar is what turned a +0.17 OFI into -0.081 an hour earlier in this same
        // session, and price is no different: the 16:30 bar on 2026-09-08 showed +0.92%
        // at minute ten and closed at +0.48%.
        var openBar = Buckets.OpenBucketUtc(nowUtc);

        var closed = Volatility.Resample(candles, 15).Where(c => c.OpenTime < openBar).ToList();

        if (closed.Count < needed)
            return FlowVerdict.Abstain(
                "NOT_ENOUGH_CANDLES",
                $"Only {closed.Count} closed 15-minute bar(s); {needed} needed to measure a " +
                $"{dipBars}-bar fall and a {riseBars}-bar rise.");

        var now      = closed[^1].Close;
        var thenDip  = closed[^(dipBars  + 1)].Close;
        var thenRise = closed[^(riseBars + 1)].Close;

        if (thenDip <= 0m || thenRise <= 0m)
            return FlowVerdict.Abstain("NO_CANDLES", "Reference close is not positive.");

        var dipMovePct  = (double)((now - thenDip)  / thenDip)  * 100.0;
        var riseMovePct = (double)((now - thenRise) / thenRise) * 100.0;

        // ── Which side, if any ────────────────────────────────────────────────
        //
        // Two independent thresholds, not one with a sign flip. The asymmetry is the
        // measurement, not a preference: buying a dip pays from 0.60% while fading a
        // rally does not pay until 1.00% — see ReversalRisePct for the table. A single
        // mirrored threshold is what made the short side look worthless, and it is the
        // reason this branch sat unimplemented.
        //
        // The rise arm is checked first so that the two are visibly parallel and
        // neither can be reached by falling through the other. The previous version
        // put the short test AFTER the dip guard had already returned for every
        // non-falling move, which made it unreachable: by that point movePct was at
        // most -0.60, so `movePct > 0` could never hold. Setting ReversalLongOnly to
        // false therefore did nothing at all, silently — the config knob was inert and
        // said so nowhere. Same class of defect as reading a stale prediction: a
        // switch that reports success and changes no behaviour.
        var isDip  = dipMovePct  <= -options.ReversalDropPct;
        var isRise = riseMovePct >=  options.ReversalRisePct;

        // Both can now be true at once, which was impossible while one window served
        // both sides. A 30-minute fall of 0.60% whose most recent 15-minute bar rose
        // 1.00% satisfies each rule on its own evidence — the dip happened, and then
        // it was bought back.
        //
        // The shorter window wins, and not arbitrarily: the entire reason the short
        // side moved to one bar is that recency is what distinguishes a spike worth
        // fading from a trend worth leaving alone. A dip whose last bar has already
        // been reclaimed is a dip that has finished; entering long on it buys the top
        // of the bounce. Logged by the caller through the reason string rather than
        // resolved in silence, because a tie-break nobody can see is how a rule ends
        // up doing something its author never chose.
        var contested = isDip && isRise;
        if (contested) isDip = false;

        if (isRise && options.ReversalLongOnly)
            return FlowVerdict.Abstain(
                "SHORT_DISABLED",
                $"Price rose {riseMovePct:F2}% over the last {riseBars} closed bar(s), past the " +
                $"{options.ReversalRisePct:F2}% short threshold, but ReversalLongOnly is on. This " +
                "is a configured refusal, not an absent signal — the distinction matters because " +
                "the two are the same silence from outside.");

        if (!isDip && !isRise)
            return FlowVerdict.Abstain(
                "NO_SETUP",
                $"Over {dipBars} closed bar(s) price moved {dipMovePct:+0.00;-0.00}% against a " +
                $"-{options.ReversalDropPct:F2}% long threshold" +
                (options.ReversalLongOnly
                    ? "."
                    : $", and over {riseBars} bar(s) {riseMovePct:+0.00;-0.00}% against a " +
                      $"+{options.ReversalRisePct:F2}% short threshold.") +
                " Entering inside this band measured negative at every horizon in both " +
                "sample halves.");

        var side = isDip ? "LONG" : "SHORT";

        return new FlowVerdict(
            Actionable:          true,
            Side:                side,
            AggregateOfi:        0.0,
            AggregateZ:          0.0,
            AgreeingVenues:      0,
            ParticipatingVenues: 0,
            DispersionBps:       0.0,
            AbstainCode:         "",
            Reason:              isDip
                ? $"Price fell {dipMovePct:F2}% over {dipBars} closed 15m bar(s), past the " +
                  $"{options.ReversalDropPct:F2}% threshold. Buying the dip; no order flow consulted."
                : $"Price rose {riseMovePct:F2}% over {riseBars} closed 15m bar(s), past the " +
                  $"{options.ReversalRisePct:F2}% threshold. Fading the spike; no order flow " +
                  "consulted." +
                  (contested
                     ? $" The {dipBars}-bar window also showed a {dipMovePct:F2}% fall, which the " +
                       "shorter window overrides: the dip has already been bought back."
                     : ""));
    }

    /// <summary>
    /// Aggregate OFI for each of the last <paramref name="count"/> CLOSED buckets,
    /// volume-weighted across every venue, oldest first.
    ///
    /// The bucket still filling is dropped here rather than by the caller, because
    /// forgetting to drop it is the single most repeated defect in this file's
    /// history: a live bucket turned a +0.17 OFI into −0.081 an hour later in one
    /// session, and the aggregation worker deliberately rewrites the current bucket
    /// every two minutes, so whatever it currently says is a partial slice that will
    /// change. A rule that closes real positions must not read it.
    ///
    /// Volume-weighted across venues rather than an average of per-venue ratios, for
    /// the reason given on <see cref="FlowBar.Ofi"/>: the mean of three venues' buy
    /// ratios is not the market's buy ratio unless they carry equal volume, which
    /// they never do.
    ///
    /// Returns fewer than <paramref name="count"/> entries — possibly none — when
    /// the history is short. Callers must treat a short list as "I do not know",
    /// never as agreement.
    /// </summary>
    public static IReadOnlyList<BucketOfi> OfiByClosedBucket(
        IReadOnlyDictionary<string, IReadOnlyList<FlowBar>> barsByVenue,
        DateTime nowUtc,
        int      count)
    {
        if (count <= 0 || barsByVenue.Count == 0) return [];

        var openBar = Buckets.OpenBucketUtc(nowUtc);

        var byBucket = new Dictionary<DateTime, (decimal Buy, decimal Sell)>();

        foreach (var bars in barsByVenue.Values)
        foreach (var bar in bars)
        {
            if (bar.BucketStart >= openBar) continue;

            byBucket.TryGetValue(bar.BucketStart, out var acc);
            byBucket[bar.BucketStart] =
                (acc.Buy + bar.BuyVolumeUsd, acc.Sell + bar.SellVolumeUsd);
        }

        return byBucket
            .OrderByDescending(kv => kv.Key)
            .Take(count)
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                var total = kv.Value.Buy + kv.Value.Sell;
                return new BucketOfi(
                    Bucket:     kv.Key,
                    Ofi:        total > 0m ? (double)((kv.Value.Buy - kv.Value.Sell) / total) : 0.0,
                    VolumeUsd:  total);
            })
            .ToList();
    }


    /// <summary>
    /// The rule for <see cref="FlowEntryMode.FlowRatio"/>: enter WITH the side that
    /// dominated the last closed bucket, when it dominated by enough and on enough
    /// volume.
    ///
    /// This is the opposite sign to every entry rule that came before it. CandleReversal
    /// buys a fall and sells a spike, which means buying while the tape is selling: 138
    /// of its 155 signals entered against the last closed bucket's flow. This one enters
    /// with it.
    ///
    /// Why that is not a contradiction of "flow has no direction"
    /// ---------------------------------------------------------
    /// Aggregate OFI correlates -0.015 with the next hour's signed return over 1,496
    /// buckets, which is why entries moved to price alone. That figure is an average
    /// over the whole distribution. This rule only ever looks at the extreme tail —
    /// a 2.1:1 imbalance occurs in about 5% of buckets, and with the volume floor in
    /// about 3%. An average of zero says nothing about a tail, and nobody had measured
    /// the tail separately.
    ///
    /// **H9 carries the measurement.** Over 1,562 production buckets: ratios 1.8 / 2.1 / 2.5
    /// are all positive on every column, and 2.1 sits in the middle of that plateau rather
    /// than at its argmax. The $3M volume floor is a separate finding, not a refinement of
    /// the ratio — below it a 2:1 imbalance is what thin trading looks like, and the floor
    /// removes 50 of 93 signals and every failing column. Hold time is a plateau too, with
    /// the shipped 720 minutes at its peak.
    ///
    /// NOT PROVEN, and the sample is thin: 43 signals once the volume floor applies, second
    /// half much weaker than the first. Switched OFF as H16 on 2026-09-18 after 12 traded
    /// signals at -4.477R, and back ON as H18 on 2026-09-19 by operator override.
    /// </summary>
    public static FlowVerdict ScoreFlowRatio(
        IReadOnlyDictionary<string, IReadOnlyList<FlowBar>> barsByVenue,
        DateTime nowUtc,
        FlowSignalOptions options)
    {
        // One bucket, and it must be closed. OfiByClosedBucket owns that guard for the
        // same reason the exit path uses it: the aggregation worker rewrites the
        // current bucket every two minutes, so its reading is a partial slice that
        // will change.
        var recent = OfiByClosedBucket(barsByVenue, nowUtc, 1);

        // Every verdict below reports the same venue count, abstention or not. It was 0
        // on four abstentions and barsByVenue.Count on the fifth and on the actionable
        // ones, so strategy_verdicts.participating_venues meant "how many venues have
        // bars" on some rows and "nothing was measured" on others, with no way to tell
        // which from the row. The count is a fact about the input and does not depend on
        // which test the bucket failed.
        var venues = barsByVenue.Count;

        if (recent.Count == 0)
            return FlowVerdict.Abstain(
                "NO_CLOSED_BUCKET",
                "flow_bars_15m has no closed bucket for this symbol yet.",
                0.0, 0.0, 0, venues, 0.0);

        var bucket = recent[0];

        // Closed is not the same as settled. Late trades keep arriving after a bucket
        // ends and the worker folds them in on its next pass, so the reading is still
        // moving for the first couple of minutes. Waiting costs a measured 0.016R at
        // three minutes -- 4% of the edge -- which is the cheaper side of the trade
        // against acting on a number that has not stopped changing.
        var closedAt = bucket.Bucket.AddMinutes(15);
        var settled  = nowUtc - closedAt;

        if (settled < TimeSpan.FromMinutes(options.RatioSettleMinutes))
            return FlowVerdict.Abstain(
                "BUCKET_NOT_SETTLED",
                $"The {bucket.Bucket:HH:mm} bucket closed {settled.TotalMinutes:F1} min ago and " +
                $"the aggregation worker is still folding late trades into it. Waiting for " +
                $"{options.RatioSettleMinutes} min.",
                bucket.Ofi, 0.0, 0, venues, 0.0);

        // The floor is side-dependent. A sell-dominated bucket has to be bigger before
        // this rule will act on it -- H21, replacing H20's ratio and |OFI| gates, which
        // were out of reach and stopped the short side outright rather than raising its
        // bar. The ratio test itself is back to symmetric at RatioMinimum.
        //
        // Checked here rather than after the ratio, so a thin bucket is refused for being
        // thin whichever side it leans. The sign is read straight off bucket.Ofi, which is
        // the same number `ratio` is derived from a few lines below.
        var sellSide   = bucket.Ofi < 0.0;
        var volumeFloor = sellSide && options.ShortMinVolumeUsd > 0m
            ? options.ShortMinVolumeUsd
            : options.RatioMinVolumeUsd;

        if (bucket.VolumeUsd < volumeFloor)
            return FlowVerdict.Abstain(
                sellSide && volumeFloor != options.RatioMinVolumeUsd
                    ? "SHORT_VOLUME_TOO_THIN"
                    : "VOLUME_TOO_THIN",
                $"The {bucket.Bucket:HH:mm} bucket traded " +
                $"${bucket.VolumeUsd / 1_000_000m:F2}M, under the " +
                $"${volumeFloor / 1_000_000m:F1}M floor" +
                (sellSide && volumeFloor != options.RatioMinVolumeUsd
                    ? $" the SHORT side requires — the long side needs only " +
                      $"${options.RatioMinVolumeUsd / 1_000_000m:F1}M. Shorts are held to more " +
                      "because this rule has never produced a winning one (H21)."
                    : ". A 2:1 imbalance on thin volume is what thin volume looks like: below " +
                      "the floor those signals measured -0.012 mean R once the largest winner " +
                      "is removed, against +0.332 above it."),
                bucket.Ofi, 0.0, 0, venues, 0.0);

        // ratio = buy/sell, recovered from the imbalance:
        //   ofi = (b-s)/(b+s)  =>  b/s = (1+ofi)/(1-ofi)
        // Expressed this way so the rule reads in the operator's terms -- "one side
        // must be 2.1 times the other" -- while reusing the aggregation that already
        // drops the live bucket.
        var ofi = bucket.Ofi;

        if (Math.Abs(ofi) >= 1.0)
            return FlowVerdict.Abstain(
                "ONE_SIDED_BUCKET",
                $"The {bucket.Bucket:HH:mm} bucket has no volume on one side at all, which is a " +
                "data fault rather than a market state.",
                ofi, 0.0, 0, venues, 0.0);

        var ratio = (1.0 + Math.Abs(ofi)) / (1.0 - Math.Abs(ofi));

        // ── The news-print exception ──────────────────────────────────────────
        //
        // Above RatioHighVolumeUsd the ratio test is skipped and the side is simply
        // whichever traded more. See that option for the measurement, which did not
        // support it: 20M is an isolated positive cell between negative neighbours and
        // its mean goes negative once the best single trade is removed.
        //
        // Placed AFTER the one-sided guard so it inherits that protection, and after
        // `ratio` is computed so the reason can state how narrow the lead actually was
        // — which is the number an operator will want when this loses. At these volumes
        // the lead is usually narrow by construction: ratio falls as volume rises, so a
        // typical qualifying bucket leans about 1.2:1 and the side is being chosen by
        // roughly a tenth of the notional.
        //
        // An exactly balanced bucket has no dominant side and is refused rather than
        // defaulted. Without this, `ofi > 0 ? LONG : SHORT` silently resolves a zero to
        // SHORT — impossible at 2.1:1, and reachable here, which is exactly the kind of
        // edge a bypass rule opens up.
        if (options.RatioHighVolumeUsd > 0m && bucket.VolumeUsd >= options.RatioHighVolumeUsd)
        {
            if (ofi == 0.0)
                return FlowVerdict.Abstain(
                    "BUCKET_PERFECTLY_BALANCED",
                    $"The {bucket.Bucket:HH:mm} bucket traded " +
                    $"${bucket.VolumeUsd / 1_000_000m:F2}M with buy and sell exactly equal, so " +
                    "there is no dominant side to enter with.",
                    ofi, 0.0, 0, venues, 0.0);

            return new FlowVerdict(
                Actionable:          true,
                Side:                ofi > 0 ? "LONG" : "SHORT",
                AggregateOfi:        ofi,
                AggregateZ:          0.0,
                AgreeingVenues:      0,
                ParticipatingVenues: venues,
                DispersionBps:       0.0,
                AbstainCode:         "",
                Reason:              $"The {bucket.Bucket:HH:mm} bucket traded " +
                                     $"${bucket.VolumeUsd / 1_000_000m:F2}M, at or above the " +
                                     $"${options.RatioHighVolumeUsd / 1_000_000m:F1}M news-print " +
                                     $"threshold, so the {options.RatioMinimum:F2}:1 ratio test is " +
                                     $"skipped. Entering WITH the heavier side at {ratio:F2}:1 " +
                                     $"{(ofi > 0 ? "buy" : "sell")} (OFI {ofi:+0.000;-0.000}) — a " +
                                     // The $ on this segment is load-bearing. Without it the
                                     // brief printed the expression source verbatim, and this
                                     // string is the gate's evidence: a system prompt that
                                     // demands "every claim must be true of a number in the
                                     // brief" was being handed a claim with no number in it.
                                     $"lead of {Math.Abs(ofi) * 100:F1}% of the bucket's notional. " +
                                     "Volume is the whole signal here; price is not consulted.",
                EntryPath:           EntryPaths.HighVolume);
        }

        // The ratio floor is side-dependent too, and sits AFTER the waiver so the
        // news-print path keeps its documented "any ratio" behaviour. H20 put the short
        // ratio ahead of the waiver and that also changed what the waiver does, which is a
        // second change riding on one decision; H22 keeps them separate.
        var ratioFloor = sellSide && options.ShortRatioMinimum > 0m
            ? options.ShortRatioMinimum
            : options.RatioMinimum;

        if (ratio < (double)ratioFloor)
            return FlowVerdict.Abstain(
                sellSide && ratioFloor != options.RatioMinimum
                    ? "SHORT_RATIO_TOO_LOW"
                    : "RATIO_TOO_LOW",
                $"The {bucket.Bucket:HH:mm} bucket is {ratio:F2}:1 " +
                $"{(ofi > 0 ? "buy" : "sell")}-dominated on " +
                $"${bucket.VolumeUsd / 1_000_000m:F2}M, under the " +
                $"{ratioFloor:F2}:1 minimum" +
                (sellSide && ratioFloor != options.RatioMinimum
                    ? $" the SHORT side requires — the long side needs only " +
                      $"{options.RatioMinimum:F2}:1 (H22)"
                    : "") +
                (options.RatioHighVolumeUsd > 0m
                    ? $", and under the ${options.RatioHighVolumeUsd / 1_000_000m:F1}M that " +
                      "would have waived it."
                    : "."),
                ofi, 0.0, 0, venues, 0.0);

        var side = ofi > 0 ? "LONG" : "SHORT";

        return new FlowVerdict(
            Actionable:          true,
            Side:                side,
            AggregateOfi:        ofi,
            AggregateZ:          0.0,
            AgreeingVenues:      0,
            ParticipatingVenues: venues,
            DispersionBps:       0.0,
            AbstainCode:         "",
            Reason:              $"The {bucket.Bucket:HH:mm} bucket closed {ratio:F2}:1 " +
                                 $"{(ofi > 0 ? "buy" : "sell")}-dominated on " +
                                 $"${bucket.VolumeUsd / 1_000_000m:F2}M (OFI {ofi:+0.000;-0.000}), " +
                                 $"past the {options.RatioMinimum:F2}:1 and " +
                                 $"${options.RatioMinVolumeUsd / 1_000_000m:F1}M floors. Entering " +
                                 "WITH the dominant side; price is not consulted.",
            EntryPath:           EntryPaths.Ratio);
    }
}

/// <summary>
/// Exit-geometry defaults, in one place because they have now drifted three times.
///
/// The pattern each time: a code default here, a different value in
/// appsettings.json, and the backtester carrying its own literal copy of the code
/// default. The tool then validates a strategy nobody runs, and nothing errors
/// because every number looks plausible on its own.
///
///   BaselineBars        record said 96, production ran 44, backtester used 96
///                       — 0 signals in backtest against 6 in 16 live hours
///   EnterZ              record said 1.5, production moved to 1.0, backtester
///                       still defaulted 1.5
///   AtrLookbackMinutes  both code defaults said 1440, appsettings ran 240, so
///                       every sweep sized stops from a 24-hour ATR while
///                       production sized them from a 4-hour one
///   EntryPullbackAtr    this file said 0.75, appsettings ran 0.0 — the backtester
///                       waited for a pullback the bot never waits for
///   MinRewardRisk       this file said 1.2, appsettings ran 0.5, so the tool
///                       rejected candidates production would have traded
///
/// The last two were found on 2026-09-11 by diffing this class against the deployed
/// appsettings, which is the check the paragraph below had been asserting rather
/// than performing. Writing "these values ARE what production runs" does not make
/// them so; the audit is what does, and it is worth repeating whenever either side
/// of the pair is edited.
///
/// These values ARE what production runs. appsettings may still override them, but
/// an override that changes behaviour should now also change this file, and a
/// backtest run with no flags validates what is deployed.
/// </summary>
public static class FlowGeometryDefaults
{
    /// <summary>Minutes of 1-minute candles the ATR is measured over. 240 = 4 hours.</summary>
    public const int AtrLookbackMinutes = 240;

    /// <summary>Bar size the true range is resampled to before taking a median.</summary>
    public const int AtrBarMinutes = 15;

    /// <summary>Stop distance as a multiple of that ATR.</summary>
    public const double StopAtrMultiple = 1.5;

    /// <summary>Target distance as a multiple of the stop distance.</summary>
    public const double TargetRiskMultiple = 2.0;

    /// <summary>
    /// Hours a position may be held before it is closed regardless.
    ///
    /// The live cap is <c>bot_config.max_hold_minutes</c>, read by StrategyEvaluator;
    /// this is the same fact as a constant, and <see cref="CryptoDecision.Shared.Bot.BotOptions.MaxHoldMinutes"/>
    /// derives its default from it so the two cannot drift. They did drift — 1440 there
    /// against 12.0 here — for as long as the backtester was the only thing reading this.
    /// </summary>
    public const double MaxHoldHours = 12.0;

    /// <summary>
    /// Floor under the stop distance, as a fraction of entry — the market's noise, not
    /// the exchange's fees. This is H8, and the full measurement is on
    /// FlowStrategyOptions.MinStopPct, which now reads it from here.
    ///
    /// It lives in this class for the reason the class exists: nothing outside BotService
    /// can see FlowStrategyOptions, so a geometry value defined only there is one no other
    /// caller can structurally apply. The backtester did not apply this one — the
    /// engine simply never passed a minStopPct — so every stop-width result the tool
    /// has produced since H8 shipped was measured on a ~1.6% stop against a deployed
    /// 2.00% one. Of the four drifts this class has now recorded, this is the only one
    /// that was invisible rather than merely inconsistent: there was no second value
    /// to disagree with, just an absent argument.
    /// </summary>
    public const decimal MinStopPct = 0.020m;


    /// <summary>
    /// Minimum post-fee reward:risk for a signal to become a trade.
    ///
    /// Here rather than only on FlowStrategyOptions because the backtester needs the
    /// same number and cannot see BotService. Every other parameter in this class is in
    /// it for that reason: a literal duplicated into the backtester is how a
    /// configuration gets certified that nobody was running.
    ///
    /// 0.5 because that is the deployed value. It read 1.2 here against 0.5 in
    /// appsettings, which made the backtester STRICTER than the bot: a cell the tool
    /// rejected for thin reward:risk is one production would have traded. Neither
    /// number binds under the shipped geometry — a 2.00% stop with TargetRiskMultiple
    /// 2.0 clears both at 1.86:1 after fees — so this corrects the record rather than
    /// any measurement taken so far. It would bind again only under a geometry whose
    /// reward:risk falls out of where the entry sits in a range, which would make
    /// MinRewardRisk a positional filter rather than a floor. The range geometry that did
    /// that was deleted on 2026-09-19; HYPOTHESES.md, "Removed features", carries the
    /// reward:risk-by-range-position table under it.
    /// </summary>
    public const decimal MinRewardRisk = 0.5m;
}
