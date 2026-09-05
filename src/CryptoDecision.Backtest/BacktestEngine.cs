using CryptoDecision.Shared.Signals;

namespace CryptoDecision.Backtest;

/// <summary>
/// One fully specified policy: what to trade on, and what to do once in.
/// </summary>
/// <param name="AllInCostRate">
/// Round-trip cost as a fraction of notional, covering fees and slippage together.
///
/// Deliberately one number and deliberately pessimistic by default. Published
/// audits of candle-based crypto timing strategies found configurations that looked
/// viable at an optimistic 10 bps and were solidly negative at a realistic 21+ bps,
/// and separately that order-flow-imbalance strategies on crypto perpetuals were
/// net negative at an assumed 4 bps round-trip taker cost. The cost assumption is
/// not a detail of the simulation, it is usually the finding.
/// </param>
/// <param name="FundingRatePerHour">
/// Drag applied per hour held, as a fraction of notional. Perpetual funding settles
/// every eight hours and a target hold measured in hours crosses it, yet no part of
/// the existing bot accounted for it anywhere — not the expectancy arithmetic, not
/// the recorded P&amp;L. Signed: positive costs a long and pays a short, but this
/// applies it symmetrically as a cost because the sign is not knowable in advance.
/// </param>
/// <param name="MaxHoldHours">
/// Target holding period. Order-flow imbalance measured on the quarter-hour grid has
/// its documented predictive content over the following 4-12 hours, peaking near
/// 8-12h; the same imbalance says nothing about the next few minutes. The strategy
/// this replaces held for minutes and re-evaluated every 30 seconds, which paid the
/// round-trip cost dozens of times per day against a signal with an hours-long
/// horizon.
/// </param>
public sealed record PolicyConfig(
    FlowSignalOptions Signal,
    double  StopAtrMultiple    = FlowGeometryDefaults.StopAtrMultiple,
    double  TargetRiskMultiple = FlowGeometryDefaults.TargetRiskMultiple,
    int     AtrLookbackMinutes = FlowGeometryDefaults.AtrLookbackMinutes,
    int     AtrBarMinutes      = FlowGeometryDefaults.AtrBarMinutes,
    double  MaxHoldHours       = FlowGeometryDefaults.MaxHoldHours,
    decimal AllInCostRate      = 0.0021m,
    decimal FundingRatePerHour = 0.00005m)
{
    public string Describe() =>
        $"z≥{Signal.EnterZ:F2} venues≥{Signal.MinAgreeingVenues} " +
        $"win={Signal.SignalBars}b stop={StopAtrMultiple:F1}×ATR " +
        $"rr={TargetRiskMultiple:F1} hold≤{MaxHoldHours:F0}h " +
        $"cost={AllInCostRate * 10_000m:F0}bps";
}

/// <summary>One simulated trade, with everything needed to audit it afterwards.</summary>
public sealed record SimTrade(
    DateTime SignalAt,
    DateTime EntryAt,
    DateTime ExitAt,
    string   Side,
    decimal  EntryPrice,
    decimal  ExitPrice,
    string   ExitReason,
    decimal  StopPct,
    decimal  TargetPct,
    double   AggregateZ,
    int      AgreeingVenues,
    double   GrossReturn,
    double   FundingDrag,
    double   NetReturn,
    double   HoursHeld)
{
    /// <summary>
    /// Net return expressed in units of the risk taken. Comparable across trades
    /// whose stops were different widths, which a plain percentage is not once the
    /// stop is volatility-scaled.
    /// </summary>
    public double RMultiple => StopPct > 0m ? NetReturn / (double)StopPct : 0.0;
}

/// <summary>Aggregate outcome of one policy over one period.</summary>
public sealed record BacktestResult(
    PolicyConfig Config,
    DateTime     From,
    DateTime     To,
    int          BucketsEvaluated,
    int          Signals,
    int          Trades,
    IReadOnlyList<SimTrade> TradeLog,
    IReadOnlyDictionary<string, int> AbstainCounts)
{
    public double Coverage => BucketsEvaluated > 0 ? (double)Signals / BucketsEvaluated : 0.0;

    public int Wins   => TradeLog.Count(t => t.NetReturn > 0);
    public int Losses => TradeLog.Count(t => t.NetReturn <= 0);

    public double WinRate => Trades > 0 ? (double)Wins / Trades : 0.0;

    public double MeanR => Trades > 0 ? TradeLog.Average(t => t.RMultiple) : 0.0;

    /// <summary>Sum of per-trade net returns. Fixed notional, so this is additive.</summary>
    public double TotalReturn => TradeLog.Sum(t => t.NetReturn);

    public double MeanHoursHeld => Trades > 0 ? TradeLog.Average(t => t.HoursHeld) : 0.0;

    /// <summary>
    /// The all-in round-trip cost at which this policy breaks exactly even.
    ///
    /// This is the single most decision-relevant number the backtester produces, and
    /// it is reported rather than a Sharpe ratio because it answers the question that
    /// actually decides whether the strategy can exist: is the gross edge bigger than
    /// what the exchange charges? A policy whose breakeven cost is below the fee
    /// schedule cannot be fixed by better entries.
    /// </summary>
    public double BreakevenCostBps => Trades > 0
        ? TradeLog.Average(t => t.GrossReturn - t.FundingDrag) * 10_000.0
        : 0.0;

    /// <summary>Largest peak-to-trough decline of the cumulative net-return curve.</summary>
    public double MaxDrawdown
    {
        get
        {
            double equity = 0, peak = 0, worst = 0;
            foreach (var t in TradeLog.OrderBy(t => t.ExitAt))
            {
                equity += t.NetReturn;
                if (equity > peak) peak = equity;
                var drop = peak - equity;
                if (drop > worst) worst = drop;
            }
            return worst;
        }
    }

    /// <summary>
    /// Mean R divided by the standard deviation of R. Not annualised, because
    /// annualising a handful of trades over a few days produces a number whose
    /// magnitude invites exactly the confidence the sample cannot support.
    /// </summary>
    public double RSharpe
    {
        get
        {
            if (Trades < 2) return 0.0;
            var rs = TradeLog.Select(t => t.RMultiple).ToArray();
            var mean = rs.Average();
            var variance = rs.Sum(r => (r - mean) * (r - mean)) / (rs.Length - 1);
            var sd = Math.Sqrt(variance);
            return sd > 0 ? mean / sd : 0.0;
        }
    }
}

/// <summary>
/// Replays a policy over stored history.
///
/// Three rules make this a test rather than a demonstration, and each one exists
/// because its absence is a known way to produce a profitable-looking backtest that
/// loses money live:
///
///   1. <b>No lookahead.</b> A decision at bucket t sees only buckets up to and
///      including t, enforced in one place (<see cref="MarketHistory.VisibleAt"/>).
///   2. <b>Next-executable pricing.</b> Entry fills at the open of the first minute
///      of the *following* bucket, never at the price that produced the signal.
///   3. <b>Pessimistic intrabar resolution.</b> When a single minute's range spans
///      both the stop and the target, the stop is taken. 1-minute OHLC does not say
///      which came first, and assuming the favourable one is how a losing policy
///      reports a win rate.
/// </summary>
public static class BacktestEngine
{
    public static BacktestResult Run(
        MarketHistory history, PolicyConfig config, DateTime? from = null, DateTime? to = null)
    {
        var trades  = new List<SimTrade>();
        var abstain = new Dictionary<string, int>(StringComparer.Ordinal);

        var buckets = history.Timeline
            .Where(t => (from is null || t >= from) && (to is null || t < to))
            .ToList();

        var evaluated = 0;
        var signals   = 0;

        // Wall the simulation off from overlapping positions rather than modelling a
        // portfolio: with one instrument and a signal that persists across adjacent
        // buckets, unrestricted stacking would open near-identical trades minutes
        // apart and report their correlated outcome as independent evidence.
        var openUntil = DateTime.MinValue;

        // How many bars the scorer needs, plus headroom so the baseline is full
        // rather than minimal at the start of the window.
        var maxBars = config.Signal.MinimumBars + config.Signal.SignalBars;

        foreach (var bucket in buckets)
        {
            evaluated++;

            var visible = history.VisibleAt(bucket, maxBars);
            var verdict = CrossVenueFlowScorer.Score(visible, config.Signal);

            if (!verdict.Actionable)
            {
                abstain[verdict.AbstainCode] = abstain.GetValueOrDefault(verdict.AbstainCode) + 1;
                continue;
            }

            signals++;

            // Counted as a signal before this check, so coverage measures how often
            // the *signal* fires rather than how often the position book happened to
            // be free. Conflating the two makes a busy period look like a quiet one.
            if (bucket < openUntil) continue;

            var trade = Simulate(history, config, bucket, verdict);
            if (trade is null) continue;

            trades.Add(trade);
            openUntil = trade.ExitAt;
        }

        return new BacktestResult(
            Config:           config,
            From:             buckets.Count > 0 ? buckets[0]  : default,
            To:               buckets.Count > 0 ? buckets[^1] : default,
            BucketsEvaluated: evaluated,
            Signals:          signals,
            Trades:           trades.Count,
            TradeLog:         trades,
            AbstainCounts:    abstain);
    }

    private static SimTrade? Simulate(
        MarketHistory history, PolicyConfig config, DateTime signalAt, FlowVerdict verdict)
    {
        // Rule 2: entry is at the open of the next bucket's first minute.
        var entryMinute = signalAt.AddMinutes(15);
        var entryIndex  = history.CandleIndexAtOrAfter(entryMinute);

        if (entryIndex < 0) return null;   // history ends before the trade could open

        var entryCandle = history.Candles[entryIndex];
        var entryPrice  = entryCandle.Open;
        if (entryPrice <= 0m) return null;

        // Volatility is measured from candles strictly *before* entry. Including the
        // entry candle would size the stop using a bar the trade is already inside.
        var atrFrom = entryIndex - config.AtrLookbackMinutes;
        var lookback = history.Candles
            .GetRange(Math.Max(0, atrFrom), entryIndex - Math.Max(0, atrFrom));

        var vol = Volatility.Measure(lookback, config.AtrBarMinutes);

        var geometry = VolatilityStops.Resolve(
            entryPrice:         entryPrice,
            side:               verdict.Side!,
            volatility:         vol,
            roundTripFeeRate:   config.AllInCostRate,
            stopAtrMultiple:    config.StopAtrMultiple,
            targetRiskMultiple: config.TargetRiskMultiple);

        var isLong  = verdict.Side == "LONG";
        var deadline = entryCandle.OpenTime.AddHours(config.MaxHoldHours);

        decimal exitPrice = entryCandle.Close;
        var exitAt     = entryCandle.OpenTime;
        var exitReason = "TIMEOUT";

        // The entry candle is included from here on: the fill happened at its open,
        // so the remainder of its range can take out the stop before the next minute
        // begins.
        var previousCandleAt = entryCandle.OpenTime;

        for (var i = entryIndex; i < history.Candles.Count; i++)
        {
            var c = history.Candles[i];

            // Rule 4. A gap in the candle series makes the outcome unknowable.
            //
            // The barriers are checked candle by candle, so minutes that are not in
            // the series are minutes in which the stop cannot be seen being hit. Walk
            // straight through a gap and the trade is scored on the first price after
            // it, which is unbounded in the favourable direction and silently
            // optimistic in the unfavourable one.
            //
            // This produced the only outlier in the run that made this strategy look
            // profitable: a LONG entered 08-23 10:30 exited "TIMEOUT" after 30.2 hours
            // against a 12-hour limit, at +4.31R, because the deadline fell inside a
            // gap and the next available candle was 18 hours the other side of it.
            // That single trade carried 90% of the measured edge — without it meanR
            // fell from +0.26 to +0.03. A validation tool that turns missing data into
            // profit is worse than no validation tool.
            //
            // Reported distinctly rather than dropped, so the count of unknowable
            // trades is visible instead of quietly shrinking the sample.
            var gapMinutes = (c.OpenTime - previousCandleAt).TotalMinutes;
            if (gapMinutes > 2.0)
            {
                exitPrice  = history.Candles[Math.Max(entryIndex, i - 1)].Close;
                exitAt     = previousCandleAt;
                exitReason = "GAP_UNRESOLVED";
                break;
            }

            previousCandleAt = c.OpenTime;

            if (c.OpenTime >= deadline)
            {
                exitPrice  = c.Open;
                exitAt     = c.OpenTime;
                exitReason = "TIMEOUT";
                break;
            }

            var hitStop   = isLong ? c.Low  <= geometry.StopPrice   : c.High >= geometry.StopPrice;
            var hitTarget = isLong ? c.High >= geometry.TargetPrice : c.Low  <= geometry.TargetPrice;

            // Rule 3. Stop first, always, when both are inside one minute's range.
            if (hitStop)
            {
                exitPrice  = geometry.StopPrice;
                exitAt     = c.OpenTime;
                exitReason = hitTarget ? "STOP_AMBIGUOUS" : "STOP";
                break;
            }

            if (hitTarget)
            {
                exitPrice  = geometry.TargetPrice;
                exitAt     = c.OpenTime;
                exitReason = "TARGET";
                break;
            }

            // Ran out of candles before either barrier or the deadline. Marked
            // distinctly so a result padded with unresolved tail trades is visible
            // rather than being counted as a set of timeouts.
            if (i == history.Candles.Count - 1)
            {
                exitPrice  = c.Close;
                exitAt     = c.OpenTime;
                exitReason = "TRUNCATED";
            }
        }

        var hoursHeld = Math.Max(0.0, (exitAt - entryCandle.OpenTime).TotalHours);

        var raw = (double)((exitPrice - entryPrice) / entryPrice);
        var gross = isLong ? raw : -raw;

        var funding = (double)config.FundingRatePerHour * hoursHeld;
        var net     = gross - (double)config.AllInCostRate - funding;

        return new SimTrade(
            SignalAt:       signalAt,
            EntryAt:        entryCandle.OpenTime,
            ExitAt:         exitAt,
            Side:           verdict.Side!,
            EntryPrice:     entryPrice,
            ExitPrice:      exitPrice,
            ExitReason:     exitReason,
            StopPct:        geometry.StopPct,
            TargetPct:      geometry.TargetPct,
            AggregateZ:     verdict.AggregateZ,
            AgreeingVenues: verdict.AgreeingVenues,
            GrossReturn:    gross,
            FundingDrag:    funding,
            NetReturn:      net,
            HoursHeld:      hoursHeld);
    }
}
