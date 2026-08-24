using CryptoDecision.Backtest;
using CryptoDecision.Shared.Signals;

// ─────────────────────────────────────────────────────────────────────────────
// Backtester for the cross-venue order-flow policy.
//
// This tool exists because the bot went live on real funds with no backtest of any
// kind. Every threshold it traded on — composite 62/38, the 25/25/20/15/15 weights,
// take profit 2.0% against a 1.20% trailing stop, the ensemble's 0.15 dead zone —
// was chosen by hand and never measured against history.
//
// The output is deliberately shaped to resist the reading that produced that
// situation. It leads with data coverage, reports the break-even cost rather than a
// return, splits in-sample from out-of-sample, and prints the whole sweep instead of
// the best row. A sweep's best row over a few hundred buckets is a description of
// the noise in that window.
//
// Usage
//   dotnet run --project src/CryptoDecision.Backtest -- \
//       --conn "Host=localhost;Port=5432;Database=crypto;Username=crypto;Password=crypto" \
//       --symbol SOLUSDT --cost-bps 21 --sweep
// ─────────────────────────────────────────────────────────────────────────────

var opts = CliOptions.Parse(args);
if (opts is null) return 2;

Console.WriteLine();
Console.WriteLine($"Cross-venue order-flow backtest — {opts.Symbol}");
Console.WriteLine(new string('=', 78));

MarketHistory history;
try
{
    history = await MarketHistory.LoadAsync(
        opts.ConnectionString, opts.Symbol, opts.From, opts.To, CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Could not load history: {ex.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(
        "If flow_bars_15m does not exist, apply sql/017_flow_bars.sql first, then " +
        "backfill it: SELECT upsert_flow_bars_15m('SOLUSDT', '2026-01-01', now());");
    return 1;
}

// ── Coverage first, always ───────────────────────────────────────────────────
//
// Printed before any result and never suppressed. Cross-venue consensus is
// meaningless over a period when one venue was not ingesting, and a Sharpe ratio
// computed over four days of one exchange reads exactly like one computed over four
// months of three.
Console.WriteLine();
Console.WriteLine("DATA COVERAGE");
Console.WriteLine($"  {"venue",-10} {"from",-17} {"to",-17} {"bars",8} {"complete",10}");

foreach (var c in history.Coverage)
    Console.WriteLine(
        $"  {c.Exchange,-10} {c.FirstBucket,-17:yyyy-MM-dd HH:mm} {c.LastBucket,-17:yyyy-MM-dd HH:mm} " +
        $"{c.Bars,8} {c.CompletenessPct,9:F1}%");

Console.WriteLine($"  candles (1m): {history.Candles.Count:N0}");
Console.WriteLine($"  timeline buckets: {history.Timeline.Count:N0}");

if (history.Timeline.Count == 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("No flow bars in range. Backfill flow_bars_15m before backtesting.");
    return 1;
}

var span = history.Timeline[^1] - history.Timeline[0];
Console.WriteLine($"  span: {span.TotalDays:F1} days");

var venuesWithData = history.Coverage.Count(c => c.Bars > 0);
if (venuesWithData < 2)
    Warn($"Only {venuesWithData} venue(s) have flow bars. Cross-venue consensus " +
         "cannot be tested — every result below is single-venue.");

if (span.TotalDays < 30)
    Warn($"{span.TotalDays:F1} days of history. Treat every number below as a smoke " +
         "test of the plumbing, not as evidence about the strategy. A parameter " +
         "chosen on this much data is a parameter fitted to a week of weather.");

// ── In-sample / out-of-sample split ──────────────────────────────────────────
var splitIndex = (int)(history.Timeline.Count * (1.0 - opts.OosFraction));
splitIndex = Math.Clamp(splitIndex, 1, history.Timeline.Count - 1);
var splitAt = history.Timeline[splitIndex];

Console.WriteLine();
Console.WriteLine($"SPLIT  in-sample < {splitAt:yyyy-MM-dd HH:mm} ≤ out-of-sample " +
                  $"({(1.0 - opts.OosFraction) * 100:F0}/{opts.OosFraction * 100:F0})");

var baseSignal = new FlowSignalOptions(
    SignalBars:   opts.SignalBars,
    BaselineBars: opts.BaselineBars);

if (opts.BaselineBars < 96)
    Warn($"Baseline shortened to {opts.BaselineBars} buckets " +
         $"({opts.BaselineBars * 15 / 60.0:F1}h). That exercises the scorer on a short " +
         "history; it does not measure whether an imbalance was unusual, so nothing " +
         "below is evidence about the strategy.");

if (!opts.Sweep)
{
    var config = new PolicyConfig(
        Signal:             baseSignal with { EnterZ = opts.EnterZ, MinAgreeingVenues = opts.MinVenues },
        StopAtrMultiple:    opts.StopAtr,
        TargetRiskMultiple: opts.TargetRr,
        MaxHoldHours:       opts.MaxHoldHours,
        AllInCostRate:      opts.CostRate,
        FundingRatePerHour: opts.FundingPerHour);

    Report.Single(BacktestEngine.Run(history, config, null, splitAt),  "IN-SAMPLE");
    Report.Single(BacktestEngine.Run(history, config, splitAt, null), "OUT-OF-SAMPLE");
    Report.Full(BacktestEngine.Run(history, config), opts.DumpTrades);
    return 0;
}

// ── Sweep ────────────────────────────────────────────────────────────────────
//
// The whole grid is printed, in a fixed order, with no sorting by result. Sorting a
// sweep by return and reading the top row is how a threshold gets chosen by the
// noise it was fitted to; the coverage column next to it is what shows that the top
// row is usually the one that traded four times.
double[] zGrid       = [1.0, 1.5, 2.0, 2.5];
int[]    venueGrid   = [1, 2, 3];
double[] rrGrid      = [1.5, 2.0, 3.0];

Console.WriteLine();
Console.WriteLine($"SWEEP — cost {opts.CostRate * 10_000m:F0} bps all-in, funding " +
                  $"{opts.FundingPerHour * 10_000m:F2} bps/h, hold ≤ {opts.MaxHoldHours:F0}h");
Console.WriteLine();
Report.SweepHeader();

var rows = new List<(PolicyConfig Config, BacktestResult InSample, BacktestResult Oos)>();

foreach (var z in zGrid)
foreach (var v in venueGrid)
foreach (var rr in rrGrid)
{
    var config = new PolicyConfig(
        Signal:             baseSignal with { EnterZ = z, MinAgreeingVenues = v },
        StopAtrMultiple:    opts.StopAtr,
        TargetRiskMultiple: rr,
        MaxHoldHours:       opts.MaxHoldHours,
        AllInCostRate:      opts.CostRate,
        FundingRatePerHour: opts.FundingPerHour);

    var inSample = BacktestEngine.Run(history, config, null, splitAt);
    var oos      = BacktestEngine.Run(history, config, splitAt, null);

    rows.Add((config, inSample, oos));
    Report.SweepRow(z, v, rr, inSample, oos);
}

Report.SweepFooter(rows);
return 0;

static void Warn(string message)
{
    Console.WriteLine();
    Console.WriteLine($"  !! {message}");
}
