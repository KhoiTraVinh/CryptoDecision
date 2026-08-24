namespace CryptoDecision.Shared.Signals;

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
/// 96 = 24 hours. This is the piece the previous strategy had no way to express:
/// it compared a raw buy ratio against a hardcoded 62, so the same imbalance meant
/// the same thing in a dead market and a panic.
/// </param>
/// <param name="EnterZ">
/// How many robust standard deviations from its own median a venue's imbalance
/// must reach before it counts as leaning. Judged per venue against that venue's
/// own history, not against 50%, because venues have structural biases — a
/// retail-heavy book sits above 50% buys most of the time and that is its normal,
/// not a signal.
/// </param>
/// <param name="MinAgreeingVenues">
/// How many venues must independently lean the same way. This is the actual
/// cross-venue corroboration, and it is the thing the old ensemble claimed to do
/// and did not: three models reading one identical four-number daily feature row
/// is one opinion counted three times, whereas Binance, Bybit and OKX have
/// different participants and their books can genuinely disagree.
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
/// </param>
public sealed record FlowSignalOptions(
    int     SignalBars                    = 4,
    int     BaselineBars                  = 96,
    double  EnterZ                        = 1.5,
    int     MinAgreeingVenues             = 2,
    double  MinVenueVolumeFractionOfMedian = 0.20,
    decimal MinVenueVolumeUsd             = 25_000m,
    int     MinVenueTrades                = 50,
    double  MaxConcentration              = 0.35,
    double  MaxDispersionBps              = 25.0)
{
    /// <summary>Buckets the scorer needs before it can produce anything at all.</summary>
    public int MinimumBars => SignalBars + BaselineBars;
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
    IReadOnlyList<VenueVote> Votes)
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
    public static FlowVerdict Score(
        IReadOnlyDictionary<string, IReadOnlyList<FlowBar>> barsByVenue,
        FlowSignalOptions options)
    {
        if (barsByVenue.Count == 0)
            return FlowVerdict.Abstain("NO_VENUES", "No venue supplied any flow buckets.");

        var votes = new List<VenueVote>(barsByVenue.Count);

        // ── Per-venue: summarise the signal window and standardise it ─────────
        foreach (var (exchange, bars) in barsByVenue.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            votes.Add(EvaluateVenue(exchange, bars, options));
        }

        var participating = votes.Where(v => v.Participated).ToList();

        if (participating.Count == 0)
            return FlowVerdict.Abstain(
                "NO_VENUE_QUALIFIED",
                "No venue met the volume, print-count and concentration floors. " +
                $"[{DescribeExclusions(votes)}]",
                votes);

        // A single venue cannot corroborate itself. Checked before the arithmetic
        // because the whole premise of this signal is agreement between independent
        // books, and one book agreeing with itself is the failure mode the old
        // ensemble shipped with.
        if (participating.Count < options.MinAgreeingVenues)
            return FlowVerdict.Abstain(
                "TOO_FEW_VENUES",
                $"Only {participating.Count} venue(s) qualified but " +
                $"{options.MinAgreeingVenues} must agree. [{DescribeExclusions(votes)}]",
                votes,
                participating: participating.Count);

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
                      && Math.Abs(vote.Z) >= options.EnterZ
                      && Math.Sign(vote.Z) == direction;

            var updated = vote with { Agreed = agreed };
            finalVotes.Add(updated);
            if (agreed) agreeing.Add(updated);
        }

        if (agreeing.Count < options.MinAgreeingVenues)
            return FlowVerdict.Abstain(
                "NO_CROSS_VENUE_CONSENSUS",
                $"Aggregate leans {(direction > 0 ? "buy" : "sell")} (z={aggregateZ:F2}) but only " +
                $"{agreeing.Count} of {participating.Count} venue(s) independently agree at " +
                $"z≥{options.EnterZ:F2}. [{DescribeVotes(finalVotes)}]",
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
