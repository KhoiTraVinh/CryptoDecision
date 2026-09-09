namespace CryptoDecision.Shared.Signals;

/// <summary>One OHLC candle. Mirrors a klines_1m row, minus what nothing here reads.</summary>
public sealed record Candle(
    DateTime OpenTime,
    decimal  Open,
    decimal  High,
    decimal  Low,
    decimal  Close);

/// <summary>
/// Realised-volatility measures over a candle series, as percentages of price.
/// </summary>
/// <param name="AtrPct">
/// Average true range over the lookback, as a percentage of the last close.
/// </param>
/// <remarks>
/// A RangePct sat here — the lookback's high-to-low span — computed on every
/// Measure call and read by nothing, in either the bot or the backtester. The stop
/// is scaled off AtrPct; the span was only ever going to be the figure someone
/// checked it against by eye.
/// </remarks>
public sealed record VolatilityRead(double AtrPct, int Samples)
{
    public bool IsUsable => Samples > 0 && AtrPct > 0.0;
}

/// <summary>
/// True-range volatility, used to size stops against the market rather than
/// against a fixed percentage.
///
/// Why this exists
/// ---------------
/// The bot's stops were constants: take profit 2.00%, stop loss 1.50%, trailing
/// 1.20%. On 2026-08-22 that 1.20% trailing stop sat inside a 15.76% daily range,
/// and four consecutive entries were stopped out having moved at most +0.29% in
/// their favour. The stop was not being hit because the trades were wrong; it was
/// being hit because ordinary intraday movement on SOL is several times its width.
/// A stop expressed as a multiple of measured range cannot drift out of scale with
/// the market the way a literal can.
/// </summary>
public static class Volatility
{
    /// <summary>
    /// Typical true range over the supplied candles, oldest first, measured on bars of
    /// <paramref name="barMinutes"/> resampled from the input.
    ///
    /// Two decisions here, and both were wrong in the obvious implementation:
    ///
    /// <b>The bar has to match the holding period.</b> Measured on SOL's real
    /// 1-minute candles, ATR is 0.30% — so a "1.5× ATR" stop would be 0.45% on a
    /// position held eight to twelve hours, inside a window whose realised range was
    /// 15.9%. That is precisely the failure this class exists to prevent, arrived at
    /// from the opposite direction: not a hardcoded percentage that was too tight, but
    /// a correctly-scaled multiple of the wrong timeframe. Per-minute volatility says
    /// nothing about how far price wanders over half a day.
    ///
    /// <b>The median, not the mean.</b> On the same data, 15-minute true range has a
    /// mean of 1.62% and a median of 1.07% — the mean is 51% higher because one bar
    /// in the sample ranged 13%. Using the mean lets a single violent bar widen every
    /// stop placed for the next day, and true-range distributions always have that
    /// bar in them. Same reasoning as the robust dispersion in
    /// <see cref="CrossVenueFlowScorer"/>: fat tails make the mean a statement about
    /// the outlier rather than about the typical case.
    /// </summary>
    /// <param name="candles">1-minute candles, oldest first.</param>
    /// <param name="barMinutes">
    /// Bar size to resample onto before measuring. Must be at least 1; 1 measures the
    /// input as given.
    /// </param>
    public static VolatilityRead Measure(IReadOnlyList<Candle> candles, int barMinutes = 15)
    {
        var bars = barMinutes <= 1 ? candles : Resample(candles, barMinutes);

        if (bars.Count < 3)
            return new VolatilityRead(0.0, bars.Count);

        var last = bars[^1].Close;
        if (last <= 0m) return new VolatilityRead(0.0, bars.Count);

        var ranges = new List<double>(bars.Count - 1);

        for (var i = 1; i < bars.Count; i++)
        {
            var c         = bars[i];
            var prevClose = bars[i - 1].Close;

            // True range rather than close-to-close: it includes the gap from the
            // previous close, which is where an intrabar stop actually gets hit.
            // Close-to-close systematically understates what a stop must tolerate.
            ranges.Add(Math.Max(
                (double)(c.High - c.Low),
                Math.Max(
                    Math.Abs((double)(c.High - prevClose)),
                    Math.Abs((double)(c.Low  - prevClose)))));
        }

        var typical = Median(ranges);
        var atrPct  = typical / (double)last * 100.0;

        return new VolatilityRead(atrPct, bars.Count);
    }

    /// <summary>
    /// Aggregate 1-minute candles onto a coarser clock-aligned grid.
    ///
    /// Aligned to the epoch rather than to the first candle in the list, so the bars a
    /// live read produces are the same bars the backtester produces over the same
    /// period — otherwise the two disagree about where every boundary falls, and a
    /// stop validated offline is not the stop placed live.
    ///
    /// Incomplete trailing bars are kept. Dropping the newest bar would discard the
    /// most recent information at exactly the moment a decision is being made, and a
    /// partial bar understates range rather than overstating it, which errs toward a
    /// tighter stop being rejected by the fee floor rather than toward a false wide one.
    /// </summary>
    internal static List<Candle> Resample(IReadOnlyList<Candle> candles, int barMinutes)
    {
        var bars = new List<Candle>();
        if (candles.Count == 0) return bars;

        var width = TimeSpan.FromMinutes(Math.Max(1, barMinutes)).Ticks;

        DateTime bucket = default;
        decimal open = 0m, high = 0m, low = 0m, close = 0m;
        var started = false;

        foreach (var c in candles)
        {
            var start = new DateTime(c.OpenTime.Ticks - c.OpenTime.Ticks % width, c.OpenTime.Kind);

            if (!started || start != bucket)
            {
                if (started) bars.Add(new Candle(bucket, open, high, low, close));

                bucket  = start;
                open    = c.Open;
                high    = c.High;
                low     = c.Low;
                started = true;
            }
            else
            {
                if (c.High > high) high = c.High;
                if (c.Low  < low)  low  = c.Low;
            }

            close = c.Close;
        }

        if (started) bars.Add(new Candle(bucket, open, high, low, close));

        return bars;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0.0;

        var sorted = values.ToArray();
        Array.Sort(sorted);

        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}

/// <summary>
/// Where the stop and the target go, for one entry, given measured volatility.
/// </summary>
/// <param name="StopPct">Adverse move that closes the position, as a fraction.</param>
/// <param name="TargetPct">Favourable move that closes it, as a fraction.</param>
/// <param name="RewardRisk">Target divided by stop, gross of fees.</param>
public sealed record StopGeometry(
    decimal StopPct,
    decimal TargetPct,
    decimal StopPrice,
    decimal TargetPrice,
    double  AtrPctUsed,
    decimal RewardRisk,
    string  Basis)
{
    /// <summary>
    /// Re-anchor the same distances onto an actual fill price.
    ///
    /// The percentages are the decision — how far the stop should sit from entry,
    /// given the volatility measured when the signal fired. The absolute prices are
    /// just that decision applied to a number, and the number that matters is where
    /// the order actually filled, not where the market was when the signal was
    /// scored.
    ///
    /// This exists because a resting maker order fills minutes later and a few basis
    /// points away. Keeping the signal-time prices would put the stop at a distance
    /// nobody chose: on a $92 SOL a 5 bps drift is small, but it is applied to a stop
    /// that may only be 40 bps wide, so it moves the risk on the trade by over a
    /// tenth. Recomputing the distances from a fresh volatility read instead would be
    /// worse — that is a different decision, made after the position already exists.
    /// </summary>
    public StopGeometry RebaseTo(decimal entryPrice, string side)
    {
        if (entryPrice <= 0m) return this;

        var isLong = string.Equals(side, "LONG", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(side, "BUY",  StringComparison.OrdinalIgnoreCase);

        return this with
        {
            StopPrice = isLong
                ? entryPrice * (1m - StopPct)
                : entryPrice * (1m + StopPct),
            TargetPrice = isLong
                ? entryPrice * (1m + TargetPct)
                : entryPrice * (1m - TargetPct),
            Basis = $"{Basis}, rebased to fill {entryPrice}",
        };
    }
}

/// <summary>
/// Volatility-scaled stop and target placement.
///
/// Two rules, and the tension between them is the whole point:
///
///   • The stop must sit outside the noise. Expressed as a multiple of ATR so it
///     widens in a fast market and tightens in a quiet one, which a literal cannot.
///   • The stop must sit inside what the account can afford. A stop wide enough to
///     survive a violent market can still be wider than the position should risk,
///     and the answer is a smaller position, not a tighter stop — so this reports
///     the distance and lets sizing shrink to fit rather than clipping the stop.
///
/// The target is a multiple of the stop rather than an independent number, which
/// makes the reward:risk ratio a property of the configuration instead of an
/// emergent accident. The pair it replaces — take profit 2.0% against a 1.2%
/// trailing stop — had an effective reward:risk that depended on the price path and
/// was frequently inverted: a trade peaking at +0.29% before retracing exited at
/// roughly -0.9%, so the realised ratio bore no relation to the configured 1.33:1.
/// </summary>
public static class VolatilityStops
{
    /// <summary>
    /// Smallest stop this will ever return, as a multiple of the round-trip fee.
    ///
    /// A stop inside the fee band exits on cost rather than on price, and no
    /// volatility reading is low enough to justify one. Four times the round trip
    /// is the point at which the fee is a fifth of the risk rather than most of it.
    /// </summary>
    public const decimal MinStopAsFeeMultiple = 4m;

    /// <summary>
    /// Stop at the low of the recent range, target at its high — the levels price
    /// actually turns at, rather than a multiple of average movement.
    ///
    /// Why this exists alongside <see cref="Resolve"/>
    /// -----------------------------------------------
    /// The ATR geometry places both barriers at a fixed multiple of typical movement,
    /// which says nothing about where this particular market has been rejecting. It
    /// was measured on 1,486 decision points over 18 days of SOL, first-touch within
    /// 12 hours, stop taken when a single minute spans both:
    ///
    ///     geometry                    stop%   win%    mean R
    ///     1.5x ATR, 2:1 target        0.672   33.9%   +0.015
    ///     range boundary, 2h look     0.79    52.9%   +0.107
    ///     range boundary, 4h look     1.11    52.2%   +0.030
    ///
    /// The ATR pair sits on its own break-even line — 33.9% against the 33.3% that a
    /// 2:1 needs — which is the whole of why two weeks of live trading produced 28%.
    /// Two hours beats four clearly, so the lookback is short by measurement rather
    /// than by preference.
    ///
    /// What the reward:risk means here, and why it is not fixed
    /// -------------------------------------------------------
    /// It falls out of where the entry sits inside the range, so it is information
    /// rather than a parameter. Measured by quartile of that position, entering long:
    ///
    ///     position in range   avg R:R   win%    mean R
    ///     0-25%   (near low)   13.37    14.2%   +0.390
    ///     25-50%                1.72    38.8%   +0.025
    ///     50-75%                0.63    68.9%   +0.105
    ///     75-100% (near high)   0.17    83.0%   -0.033
    ///
    /// So <c>MinRewardRisk</c> becomes a positional filter by the back door: at 1.2 it
    /// admits only the bottom ~42% of the range, which is the 14-39% win-rate
    /// territory where a five-loss streak is routine and the consecutive-loss breaker
    /// would halt the bot. That interaction is the reason the threshold was lowered
    /// when this shipped, and it is not obvious from either setting alone.
    ///
    /// The fee floor and the optional cap apply exactly as they do to the ATR path.
    /// </summary>
    /// <param name="rangeHigh">Highest high over the lookback, excluding the entry bar.</param>
    /// <param name="rangeLow">Lowest low over the lookback, excluding the entry bar.</param>
    public static StopGeometry ResolveFromRange(
        decimal entryPrice,
        string  side,
        decimal rangeHigh,
        decimal rangeLow,
        VolatilityRead volatility,
        decimal roundTripFeeRate,
        decimal? maxStopPct = null,
        decimal? minStopPct = null)
    {
        if (entryPrice <= 0m)
            throw new ArgumentOutOfRangeException(
                nameof(entryPrice), entryPrice, "Entry price must be positive.");

        var isLong = string.Equals(side, "LONG", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(side, "BUY",  StringComparison.OrdinalIgnoreCase);

        // Distances from the entry to each boundary. A boundary the entry has already
        // passed gives a non-positive distance — price broke out of the range between
        // the reading and the fill — and there is no sensible barrier to place there,
        // so the caller is told rather than handed a negative stop.
        var stopDistance   = isLong ? entryPrice - rangeLow  : rangeHigh - entryPrice;
        var targetDistance = isLong ? rangeHigh  - entryPrice : entryPrice - rangeLow;

        if (stopDistance <= 0m || targetDistance <= 0m)
            return new StopGeometry(
                StopPct: 0m, TargetPct: 0m, StopPrice: 0m, TargetPrice: 0m,
                AtrPctUsed: volatility.IsUsable ? volatility.AtrPct : 0.0,
                RewardRisk: 0m,
                Basis: $"range unusable: entry {entryPrice} is outside [{rangeLow}, {rangeHigh}]");

        var stopPct   = stopDistance   / entryPrice;
        var targetPct = targetDistance / entryPrice;
        var basis     = "range";

        // Same fee floor as the ATR path. A stop inside the fee band exits on cost
        // rather than on price, and a narrow range does not change that. Widening the
        // stop without widening the target deliberately worsens the ratio — the
        // alternative is a barrier that cannot pay for itself.
        // Two floors, for two different reasons, and kept apart on purpose.
        //
        // The FEE floor says a stop must sit outside the cost of the round trip, or
        // the trade exits on cost rather than on price. It is a property of the
        // exchange and it scales with fees.
        //
        // The NOISE floor says a stop must sit outside the market's ordinary
        // movement, or it is hit by nothing happening. It is a property of the
        // instrument and it has nothing to do with fees. Collapsing the two into one
        // multiple of the fee rate is what hid this for so long: the constant was
        // named for fees, so nobody read 4x fees as a claim about SOL's volatility --
        // which is what it silently was, and it was wrong. SOL's median 15-minute
        // true range is 1.07%; the fee floor placed every stop at 0.40%, well inside
        // it, so 87 of 115 long signals were stopped out by ordinary noise.
        var feeFloor   = roundTripFeeRate * MinStopAsFeeMultiple;
        var noiseFloor = minStopPct ?? 0m;
        var floor      = Math.Max(feeFloor, noiseFloor);

        if (stopPct < floor)
        {
            stopPct = floor;
            basis   = floor == noiseFloor && noiseFloor > feeFloor
                ? $"range, stop raised to the {floor:P2} noise floor"
                : "range, stop raised to fee floor";
        }

        if (maxStopPct is { } cap && stopPct > cap)
        {
            stopPct = cap;
            basis   = $"{basis}, capped at {cap:P2}";
        }

        var stopPrice = isLong
            ? entryPrice * (1m - stopPct)
            : entryPrice * (1m + stopPct);

        var targetPrice = isLong
            ? entryPrice * (1m + targetPct)
            : entryPrice * (1m - targetPct);

        var netWin  = targetPct - roundTripFeeRate;
        var netLoss = stopPct   + roundTripFeeRate;

        return new StopGeometry(
            StopPct:     stopPct,
            TargetPct:   targetPct,
            StopPrice:   stopPrice,
            TargetPrice: targetPrice,
            // Reported for the record even though nothing here scales on it, so a
            // losing trade can still be attributed to the volatility of its moment.
            AtrPctUsed:  volatility.IsUsable ? volatility.AtrPct : 0.0,
            RewardRisk:  netLoss > 0m ? netWin / netLoss : 0m,
            Basis:       basis);
    }

    public static StopGeometry Resolve(
        decimal entryPrice,
        string  side,
        VolatilityRead volatility,
        decimal roundTripFeeRate,
        double  stopAtrMultiple   = 1.5,
        double  targetRiskMultiple = 2.0,
        decimal? maxStopPct       = null,
        decimal? minStopPct       = null)
    {
        if (entryPrice <= 0m)
            throw new ArgumentOutOfRangeException(
                nameof(entryPrice), entryPrice, "Entry price must be positive.");

        var basis = "atr";

        // Fall back to a fee-anchored floor rather than to another literal: with no
        // volatility reading there is nothing to scale against, and the honest
        // minimum is "wide enough that fees are not the risk".
        var atrPct = volatility.IsUsable ? volatility.AtrPct : 0.0;
        if (!volatility.IsUsable) basis = "fee-floor (no volatility reading)";

        var stopPct = (decimal)(atrPct * stopAtrMultiple) / 100m;

        // Same two floors as ResolveFromRange, and the same reason for keeping them
        // apart: fees are a property of the exchange, noise is a property of SOL.
        var feeFloor   = roundTripFeeRate * MinStopAsFeeMultiple;
        var noiseFloor = minStopPct ?? 0m;
        var floor      = Math.Max(feeFloor, noiseFloor);

        if (stopPct < floor)
        {
            stopPct = floor;
            basis   = floor == noiseFloor && noiseFloor > feeFloor
                ? $"{basis}, raised to the {floor:P2} noise floor"
                : $"{basis}, raised to fee floor";
        }

        // Capping the stop is a deliberate choice with a cost worth naming: it
        // reintroduces the failure this class exists to remove, a stop narrower than
        // the market's own movement. It is offered because a hard per-trade risk
        // limit is sometimes non-negotiable, but sizing down is the better lever and
        // the caller should reach for that first.
        if (maxStopPct is { } cap && stopPct > cap)
        {
            stopPct = cap;
            basis   = $"{basis}, capped at {cap:P2}";
        }

        var targetPct = stopPct * (decimal)targetRiskMultiple;

        var isLong = string.Equals(side, "LONG", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(side, "BUY",  StringComparison.OrdinalIgnoreCase);

        var stopPrice = isLong
            ? entryPrice * (1m - stopPct)
            : entryPrice * (1m + stopPct);

        var targetPrice = isLong
            ? entryPrice * (1m + targetPct)
            : entryPrice * (1m - targetPct);

        // Net of fees, because gross reward:risk is the number that made the
        // original 0.3%/5% configuration look survivable.
        var netWin  = targetPct - roundTripFeeRate;
        var netLoss = stopPct   + roundTripFeeRate;

        return new StopGeometry(
            StopPct:     stopPct,
            TargetPct:   targetPct,
            StopPrice:   stopPrice,
            TargetPrice: targetPrice,
            AtrPctUsed:  atrPct,
            RewardRisk:  netLoss > 0m ? netWin / netLoss : 0m,
            Basis:       basis);
    }
}
