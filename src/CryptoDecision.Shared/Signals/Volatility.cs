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
/// <param name="RangePct">
/// High-to-low span of the whole lookback, as a percentage of the last close. The
/// figure a stop has to survive if the position is held across the window.
/// </param>
public sealed record VolatilityRead(double AtrPct, double RangePct, int Samples)
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
            return new VolatilityRead(0.0, 0.0, bars.Count);

        var last = bars[^1].Close;
        if (last <= 0m) return new VolatilityRead(0.0, 0.0, bars.Count);

        var ranges = new List<double>(bars.Count - 1);
        var high   = bars[0].High;
        var low    = bars[0].Low;

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

            if (c.High > high) high = c.High;
            if (c.Low  < low)  low  = c.Low;
        }

        var typical  = Median(ranges);
        var atrPct   = typical / (double)last * 100.0;
        var rangePct = low > 0m ? (double)((high - low) / last) * 100.0 : 0.0;

        return new VolatilityRead(atrPct, rangePct, bars.Count);
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

    public static StopGeometry Resolve(
        decimal entryPrice,
        string  side,
        VolatilityRead volatility,
        decimal roundTripFeeRate,
        double  stopAtrMultiple   = 1.5,
        double  targetRiskMultiple = 2.0,
        decimal? maxStopPct       = null)
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

        var feeFloor = roundTripFeeRate * MinStopAsFeeMultiple;
        if (stopPct < feeFloor)
        {
            stopPct = feeFloor;
            basis   = $"{basis}, raised to fee floor";
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
