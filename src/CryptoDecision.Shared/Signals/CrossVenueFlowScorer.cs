namespace CryptoDecision.Shared.Signals;

/// <summary>
/// Which rule turns flow into an entry.
/// </summary>
public enum FlowEntryMode
{
    /// <summary>
    /// The original rule: the aggregate imbalance must be statistically unusual
    /// against its own trailing distribution, and venues must independently agree.
    ///
    /// Its measured defect is latency, not direction. The statistic is a sum over
    /// <see cref="FlowSignalOptions.SignalBars"/> buckets — an hour at the default 4 —
    /// so it reports what happened over the past hour, not what is happening. Measured
    /// on 440 buckets: aggregate z correlates +0.467 with the PRECEDING hour's return
    /// and −0.116 with the following one. Observed live on 2026-09-07: price fell 1.5%
    /// in 45 minutes while z read +1.01, because the one-hour window still held the
    /// buying from before the fall.
    /// </summary>
    ZScore = 0,

    /// <summary>
    /// Enter on the RAW magnitude of the aggregate imbalance in the single bucket
    /// that just closed: <c>|OFI| ≥ MinAbsOfi</c>, direction from its sign.
    ///
    /// Two things it deliberately drops, and why
    /// -----------------------------------------
    /// <b>The trailing sum.</b> ZScore adds up SignalBars buckets — an hour at the
    /// default — so its centre of mass is 30-45 minutes behind the market. This reads
    /// one closed bucket, so the lag is 0-15 minutes and nothing else.
    ///
    /// <b>The standardisation.</b> Dividing by a trailing MAD converts "the imbalance
    /// is large" into "the imbalance is unusual for recently", and those are not the
    /// same claim. A 0.30 imbalance during a volatile stretch has a large MAD under
    /// it and scores a small z, so ZScore rejects exactly the readings that are big in
    /// absolute terms. That is the mechanism behind the thing this bot kept doing:
    /// standing aside through real moves and entering on quiet-period noise.
    ///
    /// What the measurement actually says — read this before trusting a result
    /// ---------------------------------------------------------------------
    /// Selected on out-of-sample performance across a 22-cell grid of |OFI| against
    /// volume-ratio thresholds. At the chosen 0.30, forward return in the signal's
    /// direction at 4 hours:
    ///
    ///     all        n=141   +0.174%
    ///     out-of-s.  n=82    +0.134%   hit 50.0%   t=1.08
    ///
    /// and the finer grid rises monotonically with the threshold out-of-sample
    /// (+0.044 at 0.20 → +0.181 at 0.35), which is the shape a real dose-response has
    /// and is why this was picked over the alternatives.
    ///
    /// But it is NOT significant and the caveats are load-bearing. The hit rate is
    /// ~50% at every threshold — below the 52.6% you get from going long at random —
    /// so the positive mean comes from magnitude asymmetry, not from being right more
    /// often. t never exceeds 1.32, and even that is inflated because 15-minute
    /// signals against a 4-hour horizon share up to 16 overlapping observations. And
    /// splitting by direction at 0.30, SHORT ran +0.409% (71.4% hit) in-sample and
    /// −0.214% (32.4%) out-of-sample while LONG did the reverse — the two sides swap
    /// which half they work in, which is what noise looks like.
    ///
    /// It is being paper-traded because the operator judged 7 days of information a
    /// fair price for finding out, and paper risks none. Treat the result as the
    /// experiment, not the expectation.
    /// </summary>
    OfiMagnitude = 1,

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
    /// Wants the ATR geometry, not the range geometry: entering with the dominant side
    /// puts price at the edge of its own range, so a range-boundary target sits almost
    /// on top of the entry and fails MinRewardRisk. Set UseRangeGeometry false, which
    /// with a 2.00% stop floor and TargetRiskMultiple 2.0 gives the 2%/4% pair this
    /// was measured on.
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
/// none had a derivation. The backtester sweeps this record and reports a
/// coverage-risk curve, so a threshold in production is one that survived a sweep
/// rather than one that sounded reasonable.
///
/// The defaults below are starting points for that sweep, not recommendations.
/// </summary>
/// <param name="SignalBars">
/// 15-minute buckets in the decision window. 4 = one hour. Order-flow imbalance
/// measured on the quarter-hour grid carries its documented predictive content
/// over the following 4-12 hours; the window that measures it wants to be long
/// enough to average out single participants and short enough to still be current.
/// </param>
/// <param name="BaselineBars">
/// Buckets of trailing history used to judge whether the signal window is unusual.
/// 44 = 11 hours. This is the piece the previous strategy had no way to express:
/// it compared a raw buy ratio against a hardcoded 62, so the same imbalance meant
/// the same thing in a dead market and a panic.
///
/// THIS DEFAULT IS THE ONE SOURCE OF TRUTH — do not add another. It was 96 here
/// while appsettings.json ran 44, and the backtester carried its own literal 96,
/// so any backtest run without an explicit --baseline-bars was certifying a
/// strategy nobody was running: 0 signals over 134 buckets against 6 signals in
/// 16 live hours, with the discrepancy invisible because both numbers looked
/// plausible. A validation tool that picks its own parameters cannot validate
/// anything.
///
/// 44 is not a researched value. It was chosen under time pressure to make the
/// readiness bar reachable within a day, and it has never been tested out of
/// sample. It is here because it is what runs, and agreement between the live
/// path and the offline path matters more than the number until there is enough
/// history to choose one properly.
/// </param>
/// <param name="EnterZ">
/// How many robust standard deviations from its own median a venue's imbalance
/// must reach before it counts as leaning. Judged per venue against that venue's
/// own history, not against 50%, because venues have structural biases — a
/// retail-heavy book sits above 50% buys most of the time and that is its normal,
/// not a signal.
///
/// ONE KNOB, TWO GATES. This value is the aggregate threshold AND the bar a single
/// venue must clear to count as agreeing. Lowering it loosens both, so "2 of 3
/// venues agree" is a weaker statement at 1.0 than it was at 1.5.
///
/// Lowered 1.5 → 1.0 on 2026-08-27 to BUY OBSERVATIONS, not because 1.0 was shown
/// to be better. At 1.5 coverage was 8.2% of buckets: six days of live running
/// produced four in-sample and four out-of-sample trades, which the backtester
/// prints as "—" because it cannot compute a win rate from that. A threshold that
/// cannot accumulate evidence inside two months cannot be evaluated at all. At 1.0
/// with venue agreement held at 2, coverage is 20% — roughly 2.4x the signals, so
/// thirty R-multiples arrive in about three weeks instead of eight.
///
/// It was also the only cell in a 36-configuration sweep that was not negative in
/// both halves (break-even 9.2 bps in-sample, 17.2 bps out-of-sample, against the
/// 7 bps this bot actually pays). That is a weak reason and it is stated as one:
/// n was 9 and 7. Nothing in that sweep cleared the tool's own bar of 20
/// in-sample and 10 out-of-sample trades — the honest summary was "nothing
/// survived". See HYPOTHESES.md for the pre-registered decision rule, which
/// exists so that a negative result three weeks from now reads as a result rather
/// than as a reason to try the next cell.
/// </param>
/// <param name="MinAgreeingVenues">
/// How many venues must independently lean the same way, for venues that are not
/// <see cref="FlowSignalOptions.SufficientVenue"/>. This is the actual cross-venue
/// corroboration, and it is the thing the old ensemble claimed to do and did not:
/// three models reading one identical four-number daily feature row is one opinion
/// counted three times, whereas Binance, Bybit and OKX have different participants
/// and their books can genuinely disagree.
///
/// Stays at 2. It briefly went to 1 on 2026-08-28 and was reverted the same day, in
/// favour of the sufficient-venue rule below, which says the same thing more
/// precisely: 1 was never wanted for Bybit, only for Binance.
///
/// Also the minimum number of venues that must PARTICIPATE — except when the
/// sufficient venue is among them, which is the exception that makes the rule work
/// at all. Never set it to 0.
/// </param>
/// <param name="VenueAgreementZ">
/// The bar an individual venue must clear to count as agreeing, separate from
/// <see cref="FlowSignalOptions.EnterZ"/>, which is the bar for the aggregate.
///
/// They were one number until 2026-08-28 and should not have been. The aggregate is
/// volume-weighted across venues and standardised against its own history, so it is
/// already a quieter series than any single venue's; asking both to clear 1.0 asks
/// much more of the aggregate than of a venue. 1.5 for a venue against 1.0 for the
/// aggregate says: the aggregate has to be unusual, and whichever venue vouches for
/// it has to be *clearly* unusual, not marginally.
/// </param>
/// <param name="SufficientVenue">
/// A venue whose agreement alone satisfies the consensus requirement. Null disables
/// the exception and restores plain N-of-M agreement.
///
/// BINANCE, because the venues are not interchangeable. Measured median volume in a
/// 15-minute bucket: Binance $7.0M, OKX $2.07M, Bybit $0.89M. An imbalance that is
/// statistically unusual on Binance is unusual across most of the traded market;
/// the same z on Bybit is unusual across a tenth of it. Requiring Bybit to be
/// corroborated while letting Binance stand alone is not favouritism, it is the
/// eightfold volume spread expressed as a rule.
///
/// The honest caveat: this is a judgement about market structure, not a result read
/// off data. Nothing in the fifteen signals with known outcomes tests it, because
/// per-venue z was never recorded for them. signal_outcomes.venue_votes records it
/// from now on, which is what makes the rule falsifiable later.
/// </param>
/// <param name="MinVenueVolumeFractionOfMedian">
/// Share of a venue's <em>own</em> trailing median window volume that it must print
/// before its imbalance counts.
///
/// Relative rather than absolute, and that is the point. Measured on real SOL data,
/// median volume in a 15-minute bucket was $7.0M on Binance, $2.07M on OKX and
/// $0.89M on Bybit — an eightfold spread, so any single USD floor is either
/// meaningless on the deepest venue or permanently excludes the thinnest. The
/// hardcoded whale threshold that this codebase already shipped is the same mistake
/// in the other direction: `quote_qty &gt; 100000` was calibrated for BTC, and on SOL
/// it was never once true, so the whale term silently contributed nothing while
/// appearing to carry 15% of the score. A fraction of each venue's own normal
/// travels across venues and across symbols without recalibration.
/// </param>
/// <param name="MinVenueVolumeUsd">
/// Absolute backstop, applied alongside the relative floor. Only bites for a venue
/// with no usable history to take a median from.
/// </param>
/// <param name="MaxConcentration">
/// Reject a venue's vote when this fraction or more of its dominant side's volume
/// came from a single print. One order is not a crowd.
/// </param>
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
    int     SignalBars                    = 4,
    int     BaselineBars                  = 44,
    double  EnterZ                        = 1.0,
    int     MinAgreeingVenues             = 2,
    double  MinVenueVolumeFractionOfMedian = 0.20,
    decimal MinVenueVolumeUsd             = 25_000m,
    int     MinVenueTrades                = 50,
    double  MaxConcentration              = 0.35,
    // 0 disables the check. Kept identical to appsettings'
    // FlowStrategy:Signal:MaxDispersionBps — the backtester takes this default rather
    // than a CLI flag, so a change made only in appsettings would leave the tool
    // measuring a strategy the bot is not running, which is the drift that has
    // already cost this repository three parameters.
    double  MaxDispersionBps              = 0.0,
    double  VenueAgreementZ               = 1.5,
    string? SufficientVenue               = "BINANCE",

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
    /// Minimum absolute aggregate OFI for <see cref="FlowEntryMode.OfiMagnitude"/>,
    /// in OFI units, which run [-1, +1].
    ///
    /// 0.30 was selected on out-of-sample performance, not in-sample — see the mode's
    /// own documentation for the numbers and for why they do not amount to proof. At
    /// 0.30 the rule fires on about 8 buckets a day, which the one-position limit will
    /// bind well before the daily entry cap does.
    ///
    /// Below ~0.20 it degrades toward the unconditional base rate; above ~0.40 the
    /// sample thins to nothing (10 buckets in 18 days at 0.50) and the sign reverses,
    /// which is a sample-size artifact rather than a ceiling.
    /// </summary>
    double  MinAbsOfi                     = 0.30,

    /// <summary>
    /// Buckets summed for the magnitude reading. 1 is the single closed bucket, which
    /// is the entire point of the mode: every extra bucket adds 15 minutes of lag back.
    /// </summary>
    int     MagnitudeBars                 = 1,

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
    /// outweigh the other, as a plain volume ratio. 2.1 means buy volume at least 2.1x
    /// sell volume for a long.
    ///
    /// Chosen by the operator from the middle of a three-cell plateau — 1.8, 2.1 and
    /// 2.5 are all positive overall, after discarding the largest winner, and in both
    /// sample halves — rather than at its argmax. 3.0 fails the outlier check on 12
    /// observations.
    /// </summary>
    decimal RatioMinimum                  = 2.1m,

    /// <summary>
    /// For <see cref="FlowEntryMode.FlowRatio"/>: minimum notional in the qualifying
    /// bucket, in USD.
    ///
    /// A separate condition from the ratio, not a refinement of it. Below $3M a 2:1
    /// imbalance measured -0.012 mean R once the largest winner was removed and -0.082
    /// in the first sample half; above it, +0.332 and +0.521. A 2:1 lean on two million
    /// dollars is what a quiet hour looks like, and it predicts nothing.
    /// </summary>
    decimal RatioMinVolumeUsd             = 3_000_000m,

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
    /// For <see cref="FlowEntryMode.FlowRatio"/>: reject a bucket when this fraction or
    /// more of the dominant side's notional came from a single print. 0 disables.
    ///
    /// THE GAP THIS CLOSES. `MaxConcentration` (0.35, "one order is not a crowd") lives
    /// in <see cref="Prepare"/>, and FlowRatio is dispatched before Prepare and never
    /// calls it — deliberately, because the per-venue quality gates are not what this
    /// rule was measured with. The side effect was that the concentration guard, which is
    /// about data quality rather than about venue selection, silently did not apply to
    /// the rule that actually trades. Observed 2026-09-11: a single $1.04M print on
    /// BINANCE inside one bucket. On a $3-4M bucket that one order can carry the entire
    /// 2.1:1 imbalance by itself, and nothing would have rejected it.
    ///
    /// DEFAULT 0, WHICH CHANGES NOTHING. Turning it on is an entry-rule change, and H9
    /// and H11 are both open on the current rule — shipping a silent tightening would
    /// invalidate the very measurements they exist to collect. This makes the capability
    /// exist and the gap visible; enabling it wants its own entry in HYPOTHESES.md.
    ///
    /// Note the aggregate is across venues while max_buy_usd/max_sell_usd are per venue,
    /// so this compares the largest single print on any venue against the market-wide
    /// dominant side. That is the conservative direction: it can only under-report
    /// concentration, never invent it.
    /// </summary>
    double  RatioMaxConcentration         = 0.0,

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
    int     RatioSettleMinutes            = 3)
{
    /// <summary>Buckets the scorer needs before it can produce anything at all.</summary>
    public int MinimumBars => SignalBars + BaselineBars;

    /// <summary>
    /// Whether one venue is allowed to corroborate a signal on its own.
    ///
    /// Whitespace counts as absent, not as a venue named " ". Configuration binders
    /// produce empty strings for a key that was written and left blank, and an empty
    /// SufficientVenue that silently matched nothing would turn the rule off without
    /// anything saying so — the class of failure this repository keeps paying for.
    /// </summary>
    public bool HasSufficientVenue => !string.IsNullOrWhiteSpace(SufficientVenue);
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

/// <summary>One venue's contribution to a verdict, and whether it counted.</summary>
public sealed record VenueVote(
    string  Exchange,
    double  Ofi,
    double  OfiMedian,
    double  Z,
    decimal VolumeUsd,
    int     TradeCount,
    double  Concentration,
    bool    Participated,
    bool    Agreed,
    string  ExclusionReason);

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
    IReadOnlyList<VenueVote> Votes,

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
        IReadOnlyList<VenueVote>? votes = null,
        double aggregateOfi = 0.0,
        double aggregateZ = 0.0,
        int agreeing = 0,
        int participating = 0,
        double dispersionBps = 0.0)
        => new(false, null, aggregateOfi, aggregateZ, agreeing, participating,
               dispersionBps, code, reason, votes ?? []);
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
    /// <summary>Scales a median absolute deviation to a normal-consistent sigma.</summary>
    private const double MadToSigma = 1.4826;

    /// <summary>
    /// Below this robust sigma a venue's imbalance is treated as having no usable
    /// dispersion — dividing by it manufactures enormous z-scores out of a flat
    /// series. Expressed in OFI units, which run [-1, +1].
    /// </summary>
    private const double MinSigma = 0.005;

    /// <summary>
    /// Score one decision point.
    /// </summary>
    /// <param name="barsByVenue">
    /// Per venue, that venue's buckets in ascending time order, ending with the
    /// bucket being decided on. Venues may have different lengths — a venue that
    /// started ingesting later simply has fewer, and is excluded rather than
    /// silently treated as balanced.
    /// </param>
    /// <param name="options">Thresholds to apply.</param>
    /// <param name="nowUtc">
    /// The moment the decision is being made, which <see cref="FlowEntryMode.FlowRatio"/>
    /// needs in order to tell a closed bucket from a settled one. Null derives it from
    /// the newest visible bucket — its close plus the settle wait — which is the instant
    /// the live bot would act on that bucket. Supply it explicitly from a live caller;
    /// the derivation exists for the backtester, whose clock is the data.
    /// </param>
    public static FlowVerdict Score(
        IReadOnlyDictionary<string, IReadOnlyList<FlowBar>> barsByVenue,
        FlowSignalOptions options,
        DateTime? nowUtc = null)
    {
        // Dispatched here rather than at the call sites, so the live strategy and the
        // backtester cannot end up on different rules. There is exactly one place that
        // decides what an entry is, and both read it.
        //
        // THAT CLAIM WAS FALSE FOR TWO OF THE FOUR MODES until 2026-09-11. The switch
        // below handled OfiMagnitude and sent everything else to ScoreZ, so a caller
        // configured for FlowRatio or CandleReversal was silently scored on the z-rule.
        // It went unnoticed because the only caller reaching this method was the
        // backtester and the default mode was ZScore, so the wrong branch and the
        // intended one were the same branch — the defect could not show itself until the
        // default moved. It was one of the two findings that got the backtester deleted.
        //
        // The live strategy dispatches FlowRatio and CandleReversal itself and only
        // reaches this method for ZScore and OfiMagnitude, so the branches below are
        // defensive rather than load-bearing today. They stay because the failure they
        // prevent is silent: the next caller to arrive gets the rule it asked for, or an
        // explicit refusal, and never a different rule scored without comment.
        //
        // FlowRatio is dispatched BEFORE Prepare, matching the live strategy: it reads
        // one aggregate bucket and does not use the per-venue quality gates, so running
        // them here would abstain on conditions the deployed rule never applies.
        if (options.EntryMode == FlowEntryMode.FlowRatio)
            return ScoreFlowRatio(barsByVenue, nowUtc ?? DecisionInstant(barsByVenue, options), options);

        // CandleReversal scores on price and this method has no candles — a FlowBar
        // carries VWAP, which is the bucket's average rather than its close, and
        // substituting one for the other would not be the rule that was measured. Said
        // out loud rather than falling through to ScoreZ, because a tool that quietly
        // measures a different rule than the one it was asked for is the failure this
        // whole comment is about. Call ScoreReversal directly, with candles.
        if (options.EntryMode == FlowEntryMode.CandleReversal)
            return FlowVerdict.Abstain(
                "MODE_NEEDS_CANDLES",
                "CandleReversal reads price, not flow, so it cannot be scored from flow " +
                "buckets. Call ScoreReversal with 1-minute candles instead.");

        var prepared = Prepare(barsByVenue, options);
        if (prepared.Abstained is { } early) return early;

        return options.EntryMode switch
        {
            FlowEntryMode.OfiMagnitude => ScoreMagnitude(barsByVenue, options, prepared),
            _                          => ScoreZ(barsByVenue, options, prepared),
        };
    }

    /// <summary>
    /// The instant a caller with no clock would be deciding: the newest visible bucket's
    /// close, plus the settle wait FlowRatio requires before it trusts the numbers.
    ///
    /// This reproduces the live timing rather than bypassing it. ScoreFlowRatio drops
    /// any bucket at or after the quarter-hour containing <c>nowUtc</c> and then refuses
    /// one that closed less than RatioSettleMinutes ago; feeding it the newest bucket's
    /// own start would fail both tests and abstain on every bar, while feeding it
    /// something far in the future would let a bucket through that the bot would still
    /// have been waiting on.
    /// </summary>
    private static DateTime DecisionInstant(
        IReadOnlyDictionary<string, IReadOnlyList<FlowBar>> barsByVenue,
        FlowSignalOptions options)
    {
        var newest = DateTime.MinValue;

        foreach (var bars in barsByVenue.Values)
            if (bars.Count > 0 && bars[^1].BucketStart > newest)
                newest = bars[^1].BucketStart;

        return newest == DateTime.MinValue
            ? DateTime.UtcNow
            : newest.AddMinutes(15 + Math.Max(0, options.RatioSettleMinutes));
    }

    // ── Mode: CandleReversal ──────────────────────────────────────────────────

    /// <summary>
    /// Score the buy-the-dip rule. See <see cref="FlowEntryMode.CandleReversal"/>.
    ///
    /// Takes candles rather than flow bars because it reads price and nothing else —
    /// a FlowBar carries VWAP, which is the bucket's average rather than its close, and
    /// substituting one for the other would not be the rule that was measured. So this
    /// is called directly by the strategy instead of going through
    /// <see cref="Score"/>'s dispatch.
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
        var openBar = new DateTime(
            nowUtc.Ticks - nowUtc.Ticks % TimeSpan.FromMinutes(15).Ticks, DateTimeKind.Utc);

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
                     : ""),
            Votes:               []);
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

        var openBar = new DateTime(
            nowUtc.Ticks - nowUtc.Ticks % TimeSpan.FromMinutes(15).Ticks, DateTimeKind.Utc);

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
    /// Measured over 1,562 production buckets, entering at the close of the qualifying
    /// bucket, stop 2.00%, target 4.00%, 12-hour cap, timeouts priced at the exit rather
    /// than at zero:
    ///
    ///     ratio    n     mean R   less top 1   1st half   2nd half
    ///      1.8    200    +0.043     +0.034      +0.045     +0.042
    ///      2.1     92    +0.185     +0.166      +0.457     +0.083
    ///      2.5     31    +0.278     +0.225      +0.380     +0.258
    ///      3.0     12    +0.027     -0.140      +0.011     +0.034
    ///
    /// Three adjacent ratios positive on every column. 2.1 is the operator's choice and
    /// sits in the middle of that plateau rather than at its argmax.
    ///
    /// The volume floor is a separate finding, not a refinement of the ratio. At a fixed
    /// 2.1 ratio, split by the bucket's total notional:
    ///
    ///     total      n    mean R   less top 1   1st half   2nd half
    ///     under $3M  50   +0.025     -0.012      -0.082     +0.048
    ///     $3-6M      30   +0.383     +0.332      +0.521     +0.303
    ///     $6-12M     10   +0.212     +0.030      +1.143     -0.408
    ///     over $12M   2   +1.065        —           —          —
    ///
    /// Below $3M a 2:1 imbalance is what thin trading looks like, and it carries
    /// nothing. The floor is doing real work: it removes 50 of 93 signals and every one
    /// of the failing columns.
    ///
    /// Hold time, at ratio 2.1 with the volume floor, is a plateau rather than a peak:
    /// +0.032 / +0.082 / +0.273 / +0.346 / +0.379 / +0.330 at 1, 2, 4, 6, 12 and 24
    /// hours, all positive in both halves and after discarding the largest winner. The
    /// shipped MaxHoldMinutes of 720 is the peak, and the rule is not sensitive to it.
    ///
    /// NOT PROVEN. 43 signals over the whole sample once the volume floor applies, the
    /// second half is much weaker than the first (+0.137 against +0.751), and it is the
    /// same 19-day window that roughly eighty configurations have now been measured
    /// against. H9 in HYPOTHESES.md carries the decision rule.
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

        if (recent.Count == 0)
            return FlowVerdict.Abstain(
                "NO_CLOSED_BUCKET",
                "flow_bars_15m has no closed bucket for this symbol yet.");

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
                $"{options.RatioSettleMinutes} min.");

        if (bucket.VolumeUsd < options.RatioMinVolumeUsd)
            return FlowVerdict.Abstain(
                "VOLUME_TOO_THIN",
                $"The {bucket.Bucket:HH:mm} bucket traded " +
                $"${bucket.VolumeUsd / 1_000_000m:F2}M, under the " +
                $"${options.RatioMinVolumeUsd / 1_000_000m:F1}M floor. A 2:1 imbalance on thin " +
                "volume is what thin volume looks like: below the floor those signals measured " +
                "-0.012 mean R once the largest winner is removed, against +0.332 above it.");

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
                "data fault rather than a market state.");

        var ratio = (1.0 + Math.Abs(ofi)) / (1.0 - Math.Abs(ofi));

        // ── One print is not a crowd ──────────────────────────────────────────
        //
        // Off unless RatioMaxConcentration is set; see that option for why the default
        // leaves behaviour unchanged. Applied before both entry paths, because a bucket
        // whose imbalance is one order is a data-quality problem under either of them —
        // and the high-volume waiver is if anything more exposed, since it takes whichever
        // side is heavier however narrow the lead.
        if (options.RatioMaxConcentration > 0.0)
        {
            decimal dominant = 0m, largestPrint = 0m;

            foreach (var bars in barsByVenue.Values)
                foreach (var bar in bars)
                {
                    if (bar.BucketStart != bucket.Bucket) continue;

                    dominant     += ofi > 0 ? bar.BuyVolumeUsd : bar.SellVolumeUsd;
                    largestPrint  = Math.Max(largestPrint, ofi > 0 ? bar.MaxBuyUsd : bar.MaxSellUsd);
                }

            var concentration = dominant > 0m ? (double)(largestPrint / dominant) : 0.0;

            if (concentration >= options.RatioMaxConcentration)
                return FlowVerdict.Abstain(
                    "PRINT_CONCENTRATION_TOO_HIGH",
                    $"The {bucket.Bucket:HH:mm} bucket is {ratio:F2}:1 " +
                    $"{(ofi > 0 ? "buy" : "sell")}-dominated, but a single " +
                    $"${largestPrint:N0} print is {concentration:P0} of that side's " +
                    $"${dominant / 1_000_000m:F2}M — over the " +
                    $"{options.RatioMaxConcentration:P0} cap. One order is not a crowd.",
                    [], ofi, 0.0, 0, barsByVenue.Count, 0.0);
        }

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
                    [], ofi, 0.0, 0, 0, 0.0);

            return new FlowVerdict(
                Actionable:          true,
                Side:                ofi > 0 ? "LONG" : "SHORT",
                AggregateOfi:        ofi,
                AggregateZ:          0.0,
                AgreeingVenues:      0,
                ParticipatingVenues: barsByVenue.Count,
                DispersionBps:       0.0,
                AbstainCode:         "",
                Reason:              $"The {bucket.Bucket:HH:mm} bucket traded " +
                                     $"${bucket.VolumeUsd / 1_000_000m:F2}M, at or above the " +
                                     $"${options.RatioHighVolumeUsd / 1_000_000m:F1}M news-print " +
                                     $"threshold, so the {options.RatioMinimum:F2}:1 ratio test is " +
                                     $"skipped. Entering WITH the heavier side at {ratio:F2}:1 " +
                                     $"{(ofi > 0 ? "buy" : "sell")} (OFI {ofi:+0.000;-0.000}) — a " +
                                     "lead of {Math.Abs(ofi) * 100:F1}% of the bucket's notional. " +
                                     "Volume is the whole signal here; price is not consulted.",
                Votes:               [],
                EntryPath:           EntryPaths.HighVolume);
        }

        if (ratio < (double)options.RatioMinimum)
            return FlowVerdict.Abstain(
                "RATIO_TOO_LOW",
                $"The {bucket.Bucket:HH:mm} bucket is {ratio:F2}:1 " +
                $"{(ofi > 0 ? "buy" : "sell")}-dominated on " +
                $"${bucket.VolumeUsd / 1_000_000m:F2}M, under the " +
                $"{options.RatioMinimum:F2}:1 minimum" +
                (options.RatioHighVolumeUsd > 0m
                    ? $", and under the ${options.RatioHighVolumeUsd / 1_000_000m:F1}M that " +
                      "would have waived it."
                    : "."),
                [], ofi, 0.0, 0, 0, 0.0);

        var side = ofi > 0 ? "LONG" : "SHORT";

        return new FlowVerdict(
            Actionable:          true,
            Side:                side,
            AggregateOfi:        ofi,
            AggregateZ:          0.0,
            AgreeingVenues:      0,
            ParticipatingVenues: barsByVenue.Count,
            DispersionBps:       0.0,
            AbstainCode:         "",
            Reason:              $"The {bucket.Bucket:HH:mm} bucket closed {ratio:F2}:1 " +
                                 $"{(ofi > 0 ? "buy" : "sell")}-dominated on " +
                                 $"${bucket.VolumeUsd / 1_000_000m:F2}M (OFI {ofi:+0.000;-0.000}), " +
                                 $"past the {options.RatioMinimum:F2}:1 and " +
                                 $"${options.RatioMinVolumeUsd / 1_000_000m:F1}M floors. Entering " +
                                 "WITH the dominant side; price is not consulted.",
            Votes:               [],
            EntryPath:           EntryPaths.Ratio);
    }

    // ── Mode: OfiMagnitude ────────────────────────────────────────────────────

    /// <summary>
    /// The raw-magnitude rule. See <see cref="FlowEntryMode.OfiMagnitude"/>.
    ///
    /// Recomputes the aggregate over the last <see cref="FlowSignalOptions.MagnitudeBars"/>
    /// buckets rather than reusing <see cref="Prepared.AggregateOfi"/>, because that one
    /// is summed over SignalBars — an hour — and the whole purpose here is to not do
    /// that. The z from Prepared is still carried onto the verdict: it costs nothing,
    /// it lands in signal_outcomes, and it is what lets the two modes be compared
    /// afterwards on the same rows rather than on separate runs.
    /// </summary>
    private static FlowVerdict ScoreMagnitude(
        IReadOnlyDictionary<string, IReadOnlyList<FlowBar>> barsByVenue,
        FlowSignalOptions options,
        Prepared p)
    {
        var participating = p.Participating;
        var bars          = Math.Max(1, options.MagnitudeBars);

        // Volume-weighted across participating venues, on the recent buckets only.
        // Summing raw volumes rather than averaging ratios is what makes this the
        // market's imbalance and not the mean of three venues' opinions of it.
        //
        // Each venue's own imbalance is kept over THE SAME window, which is the whole
        // point of doing it here rather than reusing VenueVote.Ofi. That field is
        // summed over SignalBars — four buckets — and comparing its sign against a
        // one-bucket direction is comparing two different hours. It produced
        // signal_outcomes rows reading "OFI +0.314, 0/3 venues agree", which is not
        // merely wrong but arithmetically impossible: a volume-weighted sum cannot be
        // positive when every component is negative. The entry decision never used
        // the agreement count in this mode, so nothing was mistraded — but the column
        // is the one the experiment is judged on.
        decimal buy = 0m, sell = 0m;
        var counted = 0;
        var venueOfi = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var vote in participating)
        {
            if (!barsByVenue.TryGetValue(vote.Exchange, out var venueBars)) continue;
            if (venueBars.Count < bars) continue;

            decimal vBuy = 0m, vSell = 0m;
            foreach (var bar in venueBars.Skip(venueBars.Count - bars))
            {
                vBuy  += bar.BuyVolumeUsd;
                vSell += bar.SellVolumeUsd;
            }

            var vTotal = vBuy + vSell;
            if (vTotal > 0m) venueOfi[vote.Exchange] = (double)((vBuy - vSell) / vTotal);

            buy  += vBuy;
            sell += vSell;
            counted++;
        }

        var total = buy + sell;

        if (counted == 0 || total <= 0m)
            return FlowVerdict.Abstain(
                "NO_RECENT_VOLUME",
                $"No participating venue had {bars} closed bucket(s) with any volume, so " +
                "there is no imbalance to measure.",
                p.Votes, p.AggregateOfi, p.AggregateZ, 0, participating.Count, p.DispersionBps);

        var ofi       = (double)((buy - sell) / total);
        var direction = Math.Sign(ofi);

        if (direction == 0 || Math.Abs(ofi) < options.MinAbsOfi)
            return FlowVerdict.Abstain(
                "OFI_BELOW_MAGNITUDE",
                $"Aggregate OFI {ofi:+0.000;-0.000} over the last {bars} bucket(s) is inside " +
                $"±{options.MinAbsOfi:F2}. ${total / 1_000_000m:F2}M traded, " +
                $"{(double)(buy / total):P1} of it buying.",
                p.Votes, ofi, p.AggregateZ, 0, participating.Count, p.DispersionBps);

        // Which venues leaned the same way, over the magnitude window. Not a gate —
        // this rule is on the aggregate, deliberately, because requiring per-venue
        // agreement is what made ZScore fire on 26% of buckets and enter on almost
        // none of them. Recorded so "did the venues actually agree?" stays answerable
        // from signal_outcomes.
        //
        // Ofi is overwritten with the magnitude-window value so the vote describes the
        // reading this mode actually used. Z and OfiMedian are left as they came from
        // EvaluateVenue — they are the ZScore statistic over SignalBars, carried along
        // as the reference that lets the two modes be compared on the same rows.
        var finalVotes = p.Votes
            .Select(v =>
            {
                if (!venueOfi.TryGetValue(v.Exchange, out var o))
                    return v with { Agreed = false };

                return v with { Ofi = o, Agreed = v.Participated && Math.Sign(o) == direction };
            })
            .ToList();

        var agreeing = finalVotes.Count(v => v.Agreed);

        // Dispersion keeps its veto. It is off by default (MaxDispersionBps = 0) but
        // it is a statement about the venues disagreeing on PRICE, which is orthogonal
        // to how the direction was chosen and stays valid under either mode.
        if (options.MaxDispersionBps > 0.0 && p.DispersionBps > options.MaxDispersionBps)
            return FlowVerdict.Abstain(
                "VENUE_DISPERSION_TOO_WIDE",
                $"OFI {ofi:+0.000;-0.000} cleared ±{options.MinAbsOfi:F2} but cross-venue VWAP " +
                $"dispersion is {p.DispersionBps:F1} bps, over the {options.MaxDispersionBps:F1} " +
                "bps ceiling — thin books or a move already underway.",
                finalVotes, ofi, p.AggregateZ, agreeing, participating.Count, p.DispersionBps);

        return new FlowVerdict(
            Actionable:          true,
            Side:                direction > 0 ? "LONG" : "SHORT",
            AggregateOfi:        ofi,
            AggregateZ:          p.AggregateZ,
            AgreeingVenues:      agreeing,
            ParticipatingVenues: participating.Count,
            DispersionBps:       p.DispersionBps,
            AbstainCode:         "",
            Reason:              $"OFI {ofi:+0.000;-0.000} over {bars} bucket(s) past " +
                                 $"±{options.MinAbsOfi:F2} on ${total / 1_000_000m:F2}M " +
                                 $"({agreeing}/{participating.Count} venues leaning the same way, " +
                                 $"z={p.AggregateZ:F2} for reference).",
            Votes:               finalVotes);
    }

    /// <summary>
    /// Everything both modes need: per-venue quality gates, the participating set,
    /// dispersion, and the aggregate.
    ///
    /// Shared because the *quality* floors are not what the two modes disagree about.
    /// A venue that printed $8k in fifteen minutes, or whose imbalance is one order,
    /// has nothing to say under either rule — and duplicating those checks per mode is
    /// how the two would drift into admitting different venues.
    /// </summary>
    private readonly record struct Prepared(
        FlowVerdict?           Abstained,
        List<VenueVote>        Votes,
        List<VenueVote>        Participating,
        double                 DispersionBps,
        double                 AggregateOfi,
        double                 AggregateZ);

    private static Prepared Prepare(
        IReadOnlyDictionary<string, IReadOnlyList<FlowBar>> barsByVenue,
        FlowSignalOptions options)
    {
        if (barsByVenue.Count == 0)
            return new Prepared(
                FlowVerdict.Abstain("NO_VENUES", "No venue supplied any flow buckets."),
                [], [], 0.0, 0.0, 0.0);

        var votes = new List<VenueVote>(barsByVenue.Count);

        // ── Per-venue: summarise the signal window and standardise it ─────────
        foreach (var (exchange, bars) in barsByVenue.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            votes.Add(EvaluateVenue(exchange, bars, options));
        }

        var participating = votes.Where(v => v.Participated).ToList();

        if (participating.Count == 0)
            return new Prepared(FlowVerdict.Abstain(
                "NO_VENUE_QUALIFIED",
                "No venue met the volume, print-count and concentration floors. " +
                $"[{DescribeExclusions(votes)}]",
                votes), votes, participating, 0.0, 0.0, 0.0);

        // A single venue cannot corroborate itself — unless it is the venue the
        // operator has designated as sufficient on its own. That exception is what
        // SufficientVenue exists for, so the participation floor has to know about
        // it: requiring two participants would abstain on a bucket where Binance
        // qualified and the two thin venues did not, which is precisely the bucket
        // the rule is meant to admit.
        var sufficientParticipates = options.HasSufficientVenue
            && participating.Any(v => string.Equals(
                   v.Exchange, options.SufficientVenue, StringComparison.OrdinalIgnoreCase));

        if (!sufficientParticipates && participating.Count < options.MinAgreeingVenues)
            return new Prepared(FlowVerdict.Abstain(
                "TOO_FEW_VENUES",
                $"Only {participating.Count} venue(s) qualified but " +
                $"{options.MinAgreeingVenues} must agree without " +
                $"{options.SufficientVenue ?? "a sufficient venue"}. " +
                $"[{DescribeExclusions(votes)}]",
                votes,
                participating: participating.Count), votes, participating, 0.0, 0.0, 0.0);

        // ── Cross-venue price dispersion ──────────────────────────────────────
        var dispersionBps = DispersionBps(participating.Select(v => v.Exchange), barsByVenue, options);

        // ── Aggregate imbalance, volume-weighted across participating venues ──
        //
        // Weighted by notional rather than equally: a venue printing ten times the
        // volume of another is ten times as much of the market, and an equal-weight
        // mean would let the thinnest book move the aggregate as much as the deepest.
        decimal totalVolume = participating.Sum(v => v.VolumeUsd);
        var aggregateOfi = totalVolume > 0m
            ? participating.Sum(v => v.Ofi * (double)(v.VolumeUsd / totalVolume))
            : 0.0;

        // The aggregate's own z, against the same aggregate statistic computed over
        // the baseline region. Standardising the aggregate separately matters: the
        // mean of three z-scores is not the z-score of the mean, because the venues
        // are correlated and that correlation is exactly what the aggregate's own
        // dispersion already contains.
        var aggregateZ = AggregateZ(
            participating.Select(v => v.Exchange).ToList(), barsByVenue, options, aggregateOfi);

        return new Prepared(
            null, votes, participating, dispersionBps, aggregateOfi, aggregateZ);
    }

    // ── Mode: ZScore ──────────────────────────────────────────────────────────

    private static FlowVerdict ScoreZ(
        IReadOnlyDictionary<string, IReadOnlyList<FlowBar>> barsByVenue,
        FlowSignalOptions options,
        Prepared p)
    {
        var votes         = p.Votes;
        var participating = p.Participating;
        var dispersionBps = p.DispersionBps;
        var aggregateOfi  = p.AggregateOfi;
        var aggregateZ    = p.AggregateZ;

        var direction = Math.Sign(aggregateZ);

        if (direction == 0 || Math.Abs(aggregateZ) < options.EnterZ)
            return FlowVerdict.Abstain(
                "AGGREGATE_BELOW_THRESHOLD",
                $"Aggregate flow z={aggregateZ:F2} is inside the ±{options.EnterZ:F2} band " +
                $"(OFI {aggregateOfi:+0.000;-0.000}). Nothing unusual is happening.",
                votes, aggregateOfi, aggregateZ, 0, participating.Count, dispersionBps);

        // ── Do the venues independently agree? ────────────────────────────────
        //
        // "Agree" means: this venue is itself past the threshold, and leaning the
        // same way as the aggregate. A venue that is merely not contradicting does
        // not count — that weaker reading is how the old agent prompt justified
        // entries ("1h does not strongly lean the other way") and it turns two
        // correlated windows into an apparent confirmation.
        var agreeing = new List<VenueVote>();
        var finalVotes = new List<VenueVote>(votes.Count);

        foreach (var vote in votes)
        {
            var agreed = vote.Participated
                      && Math.Abs(vote.Z) >= options.VenueAgreementZ
                      && Math.Sign(vote.Z) == direction;

            var updated = vote with { Agreed = agreed };
            finalVotes.Add(updated);
            if (agreed) agreeing.Add(updated);
        }

        // ── One designated venue may stand alone ──────────────────────────────
        //
        // The operator's rule, and the reason this is not simply MinAgreeingVenues=1:
        // the venues are not interchangeable. Binance prints roughly $7.0M in a
        // 15-minute bucket against OKX's $2.07M and Bybit's $0.89M, so a move that
        // shows up as unusual there is a move in most of the market, while the same
        // z on Bybit is a move in a tenth of it. So Binance clearing the bar counts
        // as corroboration on its own; the thinner two have to agree with each other.
        //
        // Deliberately NOT expressed as a weight or a score. A venue is either
        // sufficient or it is not, and which one it is sits in configuration where it
        // can be read, argued with and reverted — not buried in a coefficient.
        var sufficientAgreed = options.HasSufficientVenue
            && agreeing.Any(v => string.Equals(
                   v.Exchange, options.SufficientVenue, StringComparison.OrdinalIgnoreCase));

        if (!sufficientAgreed && agreeing.Count < options.MinAgreeingVenues)
            return FlowVerdict.Abstain(
                "NO_CROSS_VENUE_CONSENSUS",
                $"Aggregate leans {(direction > 0 ? "buy" : "sell")} (z={aggregateZ:F2}) but " +
                $"{(options.HasSufficientVenue ? $"{options.SufficientVenue} did not clear " +
                    $"z≥{options.VenueAgreementZ:F2} on its own and " : "")}" +
                $"only {agreeing.Count} of {participating.Count} venue(s) independently agree at " +
                $"z≥{options.VenueAgreementZ:F2}. [{DescribeVotes(finalVotes)}]",
                finalVotes, aggregateOfi, aggregateZ, agreeing.Count, participating.Count, dispersionBps);

        // Dispersion is checked last of the vetoes so its message can report a
        // signal that was otherwise good — "we had consensus and skipped it because
        // the venues disagreed on price" is a different operational fact from "there
        // was no consensus", and collapsing them loses the one worth acting on.
        if (options.MaxDispersionBps > 0.0 && dispersionBps > options.MaxDispersionBps)
            return FlowVerdict.Abstain(
                "VENUE_DISPERSION_TOO_WIDE",
                $"Consensus present ({agreeing.Count} venues, z={aggregateZ:F2}) but cross-venue " +
                $"VWAP dispersion is {dispersionBps:F1} bps, over the {options.MaxDispersionBps:F1} " +
                "bps ceiling — thin books or a move already underway.",
                finalVotes, aggregateOfi, aggregateZ, agreeing.Count, participating.Count, dispersionBps);

        var side = direction > 0 ? "LONG" : "SHORT";

        return new FlowVerdict(
            Actionable:          true,
            Side:                side,
            AggregateOfi:        aggregateOfi,
            AggregateZ:          aggregateZ,
            AgreeingVenues:      agreeing.Count,
            ParticipatingVenues: participating.Count,
            DispersionBps:       dispersionBps,
            AbstainCode:         "",
            Reason:              $"{side}: {agreeing.Count}/{participating.Count} venues agree, " +
                                 $"aggregate z={aggregateZ:F2} (OFI {aggregateOfi:+0.000;-0.000}), " +
                                 $"dispersion {dispersionBps:F1} bps. [{DescribeVotes(finalVotes)}]",
            Votes:               finalVotes);
    }

    // ── Per venue ─────────────────────────────────────────────────────────────

    private static VenueVote EvaluateVenue(
        string exchange, IReadOnlyList<FlowBar> bars, FlowSignalOptions options)
    {
        VenueVote Excluded(string reason, VenueWindow? w = null) => new(
            Exchange:        exchange,
            Ofi:             w?.Ofi ?? 0.0,
            OfiMedian:       0.0,
            Z:               0.0,
            VolumeUsd:       w?.TotalVolumeUsd ?? 0m,
            TradeCount:      w?.TotalCount ?? 0,
            Concentration:   w?.Concentration ?? 0.0,
            Participated:    false,
            Agreed:          false,
            ExclusionReason: reason);

        if (bars.Count < options.MinimumBars)
            return Excluded($"only {bars.Count} bars, needs {options.MinimumBars}");

        // The signal window is the tail; the baseline is everything before it.
        //
        // Split rather than overlapping on purpose: including the signal window in
        // its own reference distribution pulls the median toward the value being
        // tested and shrinks its own z-score. That is a small leak, and small leaks
        // in the direction of "looks less unusual than it is" are the ones that
        // survive review.
        var signalBars   = bars.Skip(bars.Count - options.SignalBars).ToList();
        var baselineBars = bars.Take(bars.Count - options.SignalBars).ToList();

        var window = VenueWindow.Sum(exchange, signalBars);

        // Absolute floors first: they need no history, and a venue that fails them is
        // not printing at all.
        if (window.TotalVolumeUsd < options.MinVenueVolumeUsd)
            return Excluded(
                $"${window.TotalVolumeUsd:N0} volume, under the ${options.MinVenueVolumeUsd:N0} " +
                "absolute floor", window);

        if (window.TotalCount < options.MinVenueTrades)
            return Excluded(
                $"{window.TotalCount} prints, under {options.MinVenueTrades}", window);

        if (window.Concentration >= options.MaxConcentration)
            return Excluded(
                $"one print is {window.Concentration:P0} of the leading side " +
                $"(cap {options.MaxConcentration:P0})", window);

        // Baseline distribution of the same statistic: rolling SignalBars-wide sums
        // across the baseline region. Overlapping positions are used because the
        // question is the dispersion of *this* statistic and disjoint sampling would
        // leave too few points to estimate it from — 24 samples against 93.
        var samples = RollingWindows(baselineBars, options.SignalBars);

        if (samples.Count < 8)
            return Excluded($"baseline has only {samples.Count} samples", window);

        // Relative volume floor, against this venue's own normal.
        //
        // Checked here rather than with the absolute floors above because it needs the
        // baseline to exist. This is the check that actually catches a venue whose
        // feed has gone quiet: on real SOL data the venues' median bucket volumes
        // span eightfold, so no single USD number can distinguish "Bybit having a
        // normal quarter hour" from "Binance having stopped".
        var medianVolume = (decimal)Median(samples.Select(s => (double)s.Volume).ToList());
        var volumeFloor  = medianVolume * (decimal)options.MinVenueVolumeFractionOfMedian;

        if (medianVolume > 0m && window.TotalVolumeUsd < volumeFloor)
            return Excluded(
                $"${window.TotalVolumeUsd:N0} is {window.TotalVolumeUsd / medianVolume:P0} of this " +
                $"venue's ${medianVolume:N0} median window — under the " +
                $"{options.MinVenueVolumeFractionOfMedian:P0} floor", window);

        var ofiSamples = samples.Select(s => s.Ofi).ToList();

        var median = Median(ofiSamples);
        var sigma  = Median(ofiSamples.Select(s => Math.Abs(s - median)).ToList()) * MadToSigma;

        // Robust dispersion rather than a standard deviation: OFI series are
        // fat-tailed, and a handful of extreme buckets inflate a plain stdev enough
        // to hide the next extreme bucket behind it.
        if (sigma < MinSigma)
            return Excluded($"baseline dispersion {sigma:F4} is degenerate", window);

        var z = (window.Ofi - median) / sigma;

        return new VenueVote(
            Exchange:        exchange,
            Ofi:             window.Ofi,
            OfiMedian:       median,
            Z:               z,
            VolumeUsd:       window.TotalVolumeUsd,
            TradeCount:      window.TotalCount,
            Concentration:   window.Concentration,
            Participated:    true,
            Agreed:          false,
            ExclusionReason: "");
    }

    /// <summary>
    /// Rolling <paramref name="width"/>-bar windows over a bar series, oldest first:
    /// one (OFI, notional) pair per valid position.
    ///
    /// Both are returned together because the two callers want the same windows —
    /// the OFI baseline and the volume baseline — and walking the series twice to get
    /// them was the sort of duplication that ends with the two disagreeing about
    /// which positions were valid.
    /// </summary>
    private static List<(double Ofi, decimal Volume)> RollingWindows(
        IReadOnlyList<FlowBar> bars, int width)
    {
        var result = new List<(double, decimal)>(Math.Max(0, bars.Count - width + 1));
        if (bars.Count < width) return result;

        // Running sums rather than re-summing each window: the baseline is walked
        // once per venue per decision, and the backtester does this for every bucket
        // in the history.
        decimal buy = 0m, sell = 0m;
        for (var i = 0; i < bars.Count; i++)
        {
            buy  += bars[i].BuyVolumeUsd;
            sell += bars[i].SellVolumeUsd;

            if (i >= width)
            {
                buy  -= bars[i - width].BuyVolumeUsd;
                sell -= bars[i - width].SellVolumeUsd;
            }

            if (i >= width - 1)
            {
                var total = buy + sell;
                result.Add((total > 0m ? (double)((buy - sell) / total) : 0.0, total));
            }
        }

        return result;
    }

    // ── Aggregate ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Standardise the cross-venue aggregate against its own trailing distribution.
    ///
    /// Built by summing the participating venues' buckets position by position, so
    /// the historical aggregate is computed the same way as the live one — including
    /// the notional weighting, which falls out of summing raw volumes rather than
    /// averaging ratios.
    /// </summary>
    private static double AggregateZ(
        IReadOnlyList<string> venues,
        IReadOnlyDictionary<string, IReadOnlyList<FlowBar>> barsByVenue,
        FlowSignalOptions options,
        double currentOfi)
    {
        // Align venues on bucket timestamps. Venues can be missing buckets — a
        // reconnect, a quiet minute — and indexing by position rather than by time
        // would silently compare one venue's 14:00 against another's 14:15.
        var byTime = new SortedDictionary<DateTime, (decimal Buy, decimal Sell)>();

        foreach (var venue in venues)
        {
            if (!barsByVenue.TryGetValue(venue, out var bars)) continue;

            // Exclude the signal window from its own baseline, as per venue.
            var baseline = bars.Take(Math.Max(0, bars.Count - options.SignalBars));

            foreach (var bar in baseline)
            {
                byTime.TryGetValue(bar.BucketStart, out var acc);
                byTime[bar.BucketStart] =
                    (acc.Buy + bar.BuyVolumeUsd, acc.Sell + bar.SellVolumeUsd);
            }
        }

        if (byTime.Count < options.SignalBars + 8) return 0.0;

        var merged = byTime
            .Select(kv => new FlowBar("AGGREGATE", kv.Key, kv.Value.Buy, kv.Value.Sell,
                                      0, 0, 0m, 0m, 0m))
            .ToList();

        var samples = RollingWindows(merged, options.SignalBars)
            .Select(s => s.Ofi)
            .ToList();

        if (samples.Count < 8) return 0.0;

        var median = Median(samples);
        var sigma  = Median(samples.Select(s => Math.Abs(s - median)).ToList()) * MadToSigma;

        return sigma < MinSigma ? 0.0 : (currentOfi - median) / sigma;
    }

    private static double DispersionBps(
        IEnumerable<string> venues,
        IReadOnlyDictionary<string, IReadOnlyList<FlowBar>> barsByVenue,
        FlowSignalOptions options)
    {
        var vwaps = new List<decimal>();

        foreach (var venue in venues)
        {
            if (!barsByVenue.TryGetValue(venue, out var bars) || bars.Count < options.SignalBars)
                continue;

            var window = VenueWindow.Sum(
                venue, bars.Skip(bars.Count - options.SignalBars).ToList());

            if (window.Vwap > 0m) vwaps.Add(window.Vwap);
        }

        if (vwaps.Count < 2) return 0.0;

        var low  = vwaps.Min();
        var high = vwaps.Max();
        return low > 0m ? (double)((high - low) / low) * 10_000.0 : 0.0;
    }

    // ── Small statistics helpers ──────────────────────────────────────────────

    /// <summary>
    /// Median of a sample. Copies before sorting so the caller's list is untouched —
    /// the MAD computation calls this twice over related sequences and an in-place
    /// sort there would reorder data still being read.
    /// </summary>
    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0.0;

        var sorted = values.ToArray();
        Array.Sort(sorted);

        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static string DescribeVotes(IEnumerable<VenueVote> votes) =>
        string.Join(", ", votes.Select(v => v.Participated
            ? $"{v.Exchange} z={v.Z:+0.00;-0.00}{(v.Agreed ? "*" : "")}"
            : $"{v.Exchange} out({v.ExclusionReason})"));

    private static string DescribeExclusions(IEnumerable<VenueVote> votes) =>
        string.Join("; ", votes.Where(v => !v.Participated)
            .Select(v => $"{v.Exchange}: {v.ExclusionReason}"));
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
    /// this is the same fact as a constant, and <see cref="BotOptions.MaxHoldMinutes"/>
    /// derives its default from it so the two cannot drift. They did drift — 1440 there
    /// against 12.0 here — for as long as the backtester was the only thing reading this.
    /// </summary>
    public const double MaxHoldHours = 12.0;

    /// <summary>
    /// Floor under the stop distance, as a fraction of entry — the market's noise, not
    /// the exchange's fees. This is H8, and the full measurement is on
    /// FlowStrategyOptions.MinStopPct, which now reads it from here.
    ///
    /// It lives in this class for the reason the class exists: the backtester cannot
    /// see BotService, so a geometry value defined only on FlowStrategyOptions is one
    /// the validation tool structurally cannot apply. It did not apply this one — the
    /// engine simply never passed a minStopPct — so every stop-width result the tool
    /// has produced since H8 shipped was measured on a ~1.6% stop against a deployed
    /// 2.00% one. Of the four drifts this class has now recorded, this is the only one
    /// that was invisible rather than merely inconsistent: there was no second value
    /// to disagree with, just an absent argument.
    /// </summary>
    public const decimal MinStopPct = 0.020m;

    /// <summary>
    /// ATR multiples price must give back before an actionable signal is taken.
    /// 0 enters at market. See CrossVenueFlowStrategy, where it is applied, and H4 in
    /// HYPOTHESES.md — it was read off 28 paper trades and is not proven.
    ///
    /// 0 because that is what production runs, and it has since this file started
    /// claiming to be what production runs. The value was 0.75 here while
    /// appsettings ran 0.0, so every backtest taken without an explicit flag measured
    /// a pullback rule the bot does not apply — and H4 is registered against trades the
    /// live path never waited for. The rule stays implemented and one config edit away;
    /// what is corrected is the claim about which version is deployed.
    /// </summary>
    public const double EntryPullbackAtr = 0.0;

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
    /// any measurement taken so far. It would bind again the moment UseRangeGeometry
    /// goes back on, where reward:risk falls out of where the entry sits in the range
    /// and MinRewardRisk becomes a positional filter; see VolatilityStops.ResolveFromRange.
    /// </summary>
    public const decimal MinRewardRisk = 0.5m;
}
