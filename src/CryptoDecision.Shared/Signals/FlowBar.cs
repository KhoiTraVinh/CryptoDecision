namespace CryptoDecision.Shared.Signals;

/// <summary>
/// One venue's aggressive-flow activity over one clock-aligned 15-minute bucket.
///
/// Mirrors a row of flow_bars_15m. Sums rather than ratios, because a ratio cannot
/// be re-aggregated: the mean of four buckets' buy ratios is not the hour's buy
/// ratio unless the buckets carry equal volume, which they never do. Every longer
/// horizon in this namespace is therefore an explicit sum of these.
/// </summary>
/// <param name="MaxBuyUsd">
/// Largest single aggressive buy in the bucket. Kept so concentration can be
/// measured as a fraction of the side's own volume rather than against a fixed
/// USD threshold — the previous whale flag was hardcoded at 100k USDT, which for
/// SOL requires a single print above ~1,087 SOL and was therefore always false.
/// </param>
public sealed record FlowBar(
    string   Exchange,
    DateTime BucketStart,
    decimal  BuyVolumeUsd,
    decimal  SellVolumeUsd,
    int      BuyCount,
    int      SellCount,
    decimal  MaxBuyUsd,
    decimal  MaxSellUsd,
    decimal  Vwap)
{
    public decimal TotalVolumeUsd => BuyVolumeUsd + SellVolumeUsd;
    public int     TotalCount     => BuyCount + SellCount;

    /// <summary>
    /// Volume-weighted order-flow imbalance in [-1, +1]. Zero for an empty bucket.
    ///
    /// Volume-weighted, not count-weighted, on purpose. The strategy this replaces
    /// scored its 5m and 15m components off trade *counts* — treating a $5 print
    /// and a $50,000 print as one vote each — while scoring its 1h component off
    /// volume, then averaged the two as though they were the same quantity. Volume
    /// is the one that measures pressure.
    /// </summary>
    public double Ofi
    {
        get
        {
            var total = TotalVolumeUsd;
            return total > 0m ? (double)((BuyVolumeUsd - SellVolumeUsd) / total) : 0.0;
        }
    }
}

/// <summary>
/// One venue's flow summed over a contiguous run of buckets — the unit the signal
/// is actually computed on.
/// </summary>
public sealed record VenueWindow(
    string   Exchange,
    DateTime WindowStart,
    DateTime WindowEnd,
    int      BarCount,
    decimal  BuyVolumeUsd,
    decimal  SellVolumeUsd,
    int      BuyCount,
    int      SellCount,
    decimal  MaxBuyUsd,
    decimal  MaxSellUsd,
    decimal  Vwap)
{
    public decimal TotalVolumeUsd => BuyVolumeUsd + SellVolumeUsd;
    public int     TotalCount     => BuyCount + SellCount;

    public double Ofi
    {
        get
        {
            var total = TotalVolumeUsd;
            return total > 0m ? (double)((BuyVolumeUsd - SellVolumeUsd) / total) : 0.0;
        }
    }

    // FlowBar.Concentration lived here and was deleted on 2026-09-19 with the last thing
    // that read it — the RatioMaxConcentration guard in ScoreFlowRatio, which was never
    // enabled. It returned the dominant side's largest single print over that side's
    // volume: the guard against "one participant, not a crowd".
    //
    // MaxBuyUsd and MaxSellUsd are deliberately KEPT. They are real columns on
    // flow_bars_15m, still written by the aggregation worker and still merged by Sum and
    // Merge below, so the guard can be rebuilt without a schema change. See
    // HYPOTHESES.md, "Removed features".

    /// <summary>Sum a contiguous run of one venue's buckets into a single window.</summary>
    /// <remarks>
    /// Requires all bars to be from the same venue; mixing venues here would
    /// produce an aggregate that no single order book ever showed, which is the
    /// opposite of what cross-venue corroboration needs.
    /// </remarks>
    public static VenueWindow Sum(string exchange, IReadOnlyList<FlowBar> bars)
    {
        if (bars.Count == 0)
            return new VenueWindow(exchange, default, default, 0, 0m, 0m, 0, 0, 0m, 0m, 0m);

        decimal buyVol = 0m, sellVol = 0m, maxBuy = 0m, maxSell = 0m;
        decimal vwapNumerator = 0m, vwapWeight = 0m;
        int buyCount = 0, sellCount = 0;
        var start = DateTime.MaxValue;
        var end   = DateTime.MinValue;

        foreach (var bar in bars)
        {
            buyVol    += bar.BuyVolumeUsd;
            sellVol   += bar.SellVolumeUsd;
            buyCount  += bar.BuyCount;
            sellCount += bar.SellCount;
            if (bar.MaxBuyUsd  > maxBuy)  maxBuy  = bar.MaxBuyUsd;
            if (bar.MaxSellUsd > maxSell) maxSell = bar.MaxSellUsd;

            // Weight each bucket's VWAP by that bucket's notional, so the window
            // VWAP is the notional-weighted mean rather than the mean of means.
            vwapNumerator += bar.Vwap * bar.TotalVolumeUsd;
            vwapWeight    += bar.TotalVolumeUsd;

            if (bar.BucketStart < start) start = bar.BucketStart;
            if (bar.BucketStart > end)   end   = bar.BucketStart;
        }

        return new VenueWindow(
            Exchange:       exchange,
            WindowStart:    start,
            WindowEnd:      end.AddMinutes(15),
            BarCount:       bars.Count,
            BuyVolumeUsd:   buyVol,
            SellVolumeUsd:  sellVol,
            BuyCount:       buyCount,
            SellCount:      sellCount,
            MaxBuyUsd:      maxBuy,
            MaxSellUsd:     maxSell,
            Vwap:           vwapWeight > 0m ? vwapNumerator / vwapWeight : 0m);
    }
}
