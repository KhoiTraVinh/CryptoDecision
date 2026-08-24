namespace CryptoDecision.Backtest;

/// <summary>
/// Console rendering of backtest results.
///
/// The shape of this output is an argument, not a formatting preference. It leads
/// with break-even cost rather than return, because for this class of strategy the
/// cost comparison is usually the whole finding: order-flow-imbalance policies on
/// crypto perpetuals have been measured net-negative at an assumed 4 bps round trip,
/// and the live bot pays around 10 before slippage. It prints trade counts next to
/// every ratio, because a 2.4 Sharpe over six trades is a coincidence with a decimal
/// point. And it never sorts a sweep by result.
/// </summary>
public static class Report
{
    public static void Single(BacktestResult r, string label)
    {
        Console.WriteLine();
        Console.WriteLine($"{label}  ({r.From:MM-dd HH:mm} → {r.To:MM-dd HH:mm})");

        if (r.Trades == 0)
        {
            Console.WriteLine($"  no trades — {r.Signals} signal(s) over {r.BucketsEvaluated} buckets");
            AbstainBreakdown(r);
            return;
        }

        Console.WriteLine(
            $"  trades {r.Trades,4}   coverage {r.Coverage,6:P1}   " +
            $"win {r.WinRate,6:P1}   meanR {r.MeanR,6:F2}");
        Console.WriteLine(
            $"  net    {r.TotalReturn,6:P2}   maxDD {r.MaxDrawdown,7:P2}   " +
            $"R-sharpe {r.RSharpe,5:F2}   hold {r.MeanHoursHeld,4:F1}h");

        BreakevenLine(r);
    }

    public static void Full(BacktestResult r, bool dumpTrades)
    {
        Console.WriteLine();
        Console.WriteLine("FULL PERIOD");
        Console.WriteLine(
            $"  buckets {r.BucketsEvaluated,5}   signals {r.Signals,4}   trades {r.Trades,4}   " +
            $"coverage {r.Coverage:P1}");

        if (r.Trades > 0)
        {
            Console.WriteLine(
                $"  net {r.TotalReturn:P2}   win {r.WinRate:P1}   meanR {r.MeanR:+0.00;-0.00}   " +
                $"maxDD {r.MaxDrawdown:P2}");
            BreakevenLine(r);

            Console.WriteLine();
            Console.WriteLine("  exits:");
            foreach (var g in r.TradeLog.GroupBy(t => t.ExitReason).OrderByDescending(g => g.Count()))
                Console.WriteLine(
                    $"    {g.Key,-16} {g.Count(),4}   meanR {g.Average(t => t.RMultiple),6:F2}");

            // STOP_AMBIGUOUS is the count of trades where one minute's range held both
            // barriers and the stop was assumed. A large share means the result rests
            // heavily on that assumption, and 1-minute OHLC cannot settle it — the
            // honest response is a wider target or tick data, not a coin flip.
            var ambiguous = r.TradeLog.Count(t => t.ExitReason == "STOP_AMBIGUOUS");
            if (ambiguous > 0)
                Console.WriteLine(
                    $"    ({ambiguous} of {r.Trades} resolved by the pessimistic intrabar rule)");

            var truncated = r.TradeLog.Count(t => t.ExitReason == "TRUNCATED");
            if (truncated > 0)
                Console.WriteLine(
                    $"    !! {truncated} trade(s) ran past the end of the candle series and were " +
                    "marked to the last close. Exclude them or extend the data.");
        }

        AbstainBreakdown(r);

        if (dumpTrades && r.Trades > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  TRADES");
            Console.WriteLine(
                $"    {"entry",-16} {"side",-5} {"z",6} {"vn",3} {"entry$",10} {"exit$",10} " +
                $"{"reason",-16} {"h",5} {"net",9} {"R",7}");

            foreach (var t in r.TradeLog)
                Console.WriteLine(
                    $"    {t.EntryAt,-16:MM-dd HH:mm} {t.Side,-5} {t.AggregateZ,6:F2} " +
                    $"{t.AgreeingVenues,3} {t.EntryPrice,10:F4} {t.ExitPrice,10:F4} " +
                    $"{t.ExitReason,-16} {t.HoursHeld,5:F1} {t.NetReturn,9:P2} {t.RMultiple,7:F2}");
        }
    }

    private static void BreakevenLine(BacktestResult r)
    {
        var breakeven = r.BreakevenCostBps;
        var paying    = (double)r.Config.AllInCostRate * 10_000.0;

        Console.WriteLine(
            $"  break-even cost {breakeven,6:F1} bps  vs  {paying:F0} bps assumed" +
            (breakeven < paying
                ? "   ← the gross edge does not cover the cost"
                : "   ← gross edge exceeds the assumed cost"));

        if (breakeven < paying)
            Console.WriteLine(
                "     No entry threshold fixes this. Either the cost has to come down " +
                "(maker entries, fewer round trips, longer holds) or the signal is not tradeable.");
    }

    private static void AbstainBreakdown(BacktestResult r)
    {
        if (r.AbstainCounts.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine("  abstentions:");
        foreach (var (code, count) in r.AbstainCounts.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"    {code,-28} {count,5}  ({(double)count / r.BucketsEvaluated:P1})");
    }

    // ── Sweep ─────────────────────────────────────────────────────────────────

    public static void SweepHeader()
    {
        Console.WriteLine(
            $"  {"z",4} {"vn",3} {"rr",4} │ {"n",4} {"cov",6} {"win",6} {"meanR",6} {"net",8} {"be",6} │ " +
            $"{"n",4} {"win",6} {"meanR",6} {"net",8} {"be",6}");
        Console.WriteLine(
            $"  {new string(' ', 12)} │ {"────── in-sample ──────────────────────",-38} │ " +
            "───── out-of-sample ─────────────");
    }

    public static void SweepRow(double z, int venues, double rr, BacktestResult ins, BacktestResult oos)
    {
        Console.WriteLine(
            $"  {z,4:F1} {venues,3} {rr,4:F1} │ " +
            $"{ins.Trades,4} {ins.Coverage,6:P1} {Fmt(ins.WinRate, ins.Trades),6} " +
            $"{Fmt2(ins.MeanR, ins.Trades),6} {Fmt3(ins.TotalReturn, ins.Trades),8} " +
            $"{Fmt2(ins.BreakevenCostBps, ins.Trades),6} │ " +
            $"{oos.Trades,4} {Fmt(oos.WinRate, oos.Trades),6} " +
            $"{Fmt2(oos.MeanR, oos.Trades),6} {Fmt3(oos.TotalReturn, oos.Trades),8} " +
            $"{Fmt2(oos.BreakevenCostBps, oos.Trades),6}");
    }

    public static void SweepFooter(
        IReadOnlyList<(PolicyConfig Config, BacktestResult InSample, BacktestResult Oos)> rows)
    {
        Console.WriteLine();
        Console.WriteLine("  z=venue z threshold, vn=venues that must agree, rr=target/stop, " +
                          "cov=share of buckets signalling,");
        Console.WriteLine("  be=break-even round-trip cost in bps.");
        Console.WriteLine();

        var assumed = rows.Count > 0 ? (double)rows[0].Config.AllInCostRate * 10_000.0 : 0.0;

        // The only summary offered, and it is a count rather than a ranking. Naming a
        // winner from a grid this size over this little data is the mistake the tool
        // exists to prevent — a sweep of 36 cells will always contain a good-looking
        // one whether or not an edge exists.
        var viable = rows.Count(r =>
            r.InSample.Trades >= 20 && r.Oos.Trades >= 10 &&
            r.InSample.BreakevenCostBps > assumed && r.Oos.BreakevenCostBps > assumed);

        Console.WriteLine(
            $"  {viable} of {rows.Count} configurations cleared {assumed:F0} bps break-even in BOTH " +
            "halves with ≥20 in-sample and ≥10 out-of-sample trades.");

        if (viable == 0)
            Console.WriteLine("""

                  Nothing survived. That is a result, and the correct response is not a
                  finer grid — a finer grid over the same window finds the same noise at
                  higher resolution. In order of expected effect:

                    1. Cut the cost. Post-only maker entries roughly halve the fee, and
                       holding for hours instead of minutes cuts the number of round
                       trips by one to two orders of magnitude.
                    2. Collect more history before deciding anything. A week cannot
                       distinguish a real edge from a quiet market.
                    3. Accept that taker order-flow imbalance may not clear its own
                       execution cost at this size, and look at a signal whose documented
                       edge is larger than the fee — funding carry rather than direction.
                """);
        else
            Console.WriteLine("""

                  Configurations clearing break-even in both halves is necessary, not
                  sufficient. Before committing funds: re-run on a later window that no
                  parameter was chosen on, check the surviving cells are adjacent rather
                  than scattered (an isolated winner is noise), and confirm the exit mix
                  is not dominated by STOP_AMBIGUOUS.
                """);
    }

    // Ratios are suppressed below a sample size at which they mean anything, rather
    // than printed with a caveat elsewhere that a reader has to remember to apply.
    private static string Fmt(double value, int n)  => n >= 5 ? $"{value:P0}"  : "—";
    private static string Fmt2(double value, int n) => n >= 5 ? $"{value:F2}"  : "—";
    private static string Fmt3(double value, int n) => n >= 5 ? $"{value:P2}"  : "—";
}
