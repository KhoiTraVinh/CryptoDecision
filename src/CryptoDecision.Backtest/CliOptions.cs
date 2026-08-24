namespace CryptoDecision.Backtest;

/// <summary>
/// Command-line arguments, parsed by hand to keep the tool dependency-free.
///
/// The cost default is 21 bps rather than the exchange's headline taker fee. That is
/// a stress level, chosen because published audits of this exact class of strategy
/// found policies that looked viable at an optimistic 10 bps and were solidly
/// negative at a realistic 21+, and because the bot's own risk arithmetic was still
/// assuming a Binance *spot* fee schedule while placing orders on OKX perpetuals.
/// A policy that only survives at the optimistic number has not survived.
/// </summary>
public sealed record CliOptions(
    string   ConnectionString,
    string   Symbol,
    DateTime? From,
    DateTime? To,
    decimal  CostRate,
    decimal  FundingPerHour,
    double   EnterZ,
    int      MinVenues,
    double   StopAtr,
    double   TargetRr,
    double   MaxHoldHours,
    double   OosFraction,
    int      SignalBars,
    int      BaselineBars,
    bool     Sweep,
    bool     DumpTrades)
{
    private const string DefaultConn =
        "Host=localhost;Port=5432;Database=crypto;Username=crypto;Password=crypto";

    public static CliOptions? Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"Unexpected argument '{arg}'.");
                Usage();
                return null;
            }

            var key = arg[2..];

            // A flag is a switch with no value: either it is last, or the next token
            // is another switch.
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                flags.Add(key);
                continue;
            }

            map[key] = args[++i];
        }

        if (flags.Contains("help") || flags.Contains("h"))
        {
            Usage();
            return null;
        }

        try
        {
            return new CliOptions(
                ConnectionString: map.GetValueOrDefault("conn")
                                  ?? Environment.GetEnvironmentVariable("POSTGRES_CONN")
                                  ?? DefaultConn,
                Symbol:           (map.GetValueOrDefault("symbol") ?? "SOLUSDT").ToUpperInvariant(),
                From:             ParseDate(map.GetValueOrDefault("from")),
                To:               ParseDate(map.GetValueOrDefault("to")),
                CostRate:         ParseDecimal(map.GetValueOrDefault("cost-bps"), 21m) / 10_000m,
                FundingPerHour:   ParseDecimal(map.GetValueOrDefault("funding-bps-per-hour"), 0.5m) / 10_000m,
                EnterZ:           ParseDouble(map.GetValueOrDefault("z"), 1.5),
                MinVenues:        (int)ParseDecimal(map.GetValueOrDefault("min-venues"), 2m),
                StopAtr:          ParseDouble(map.GetValueOrDefault("stop-atr"), 1.5),
                TargetRr:         ParseDouble(map.GetValueOrDefault("target-rr"), 2.0),
                MaxHoldHours:     ParseDouble(map.GetValueOrDefault("max-hold-hours"), 12.0),
                OosFraction:      Math.Clamp(ParseDouble(map.GetValueOrDefault("oos"), 0.4), 0.1, 0.9),
                SignalBars:       (int)ParseDecimal(map.GetValueOrDefault("signal-bars"), 4m),
                BaselineBars:     (int)ParseDecimal(map.GetValueOrDefault("baseline-bars"), 96m),
                Sweep:            flags.Contains("sweep"),
                DumpTrades:       flags.Contains("trades"));
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"Bad argument: {ex.Message}");
            Usage();
            return null;
        }
    }

    private static DateTime? ParseDate(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? null
            : DateTime.Parse(raw, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                                      | System.Globalization.DateTimeStyles.AssumeUniversal);

    private static decimal ParseDecimal(string? raw, decimal fallback) =>
        string.IsNullOrWhiteSpace(raw) ? fallback : decimal.Parse(raw);

    private static double ParseDouble(string? raw, double fallback) =>
        string.IsNullOrWhiteSpace(raw) ? fallback : double.Parse(raw);

    private static void Usage()
    {
        Console.Error.WriteLine("""

            Cross-venue order-flow backtester.

              --conn <str>                 Postgres connection string (or $POSTGRES_CONN)
              --symbol <SYM>               default SOLUSDT
              --from / --to <date>         restrict the window, UTC
              --cost-bps <n>               all-in round-trip cost, default 21
              --funding-bps-per-hour <n>   perpetual funding drag, default 0.5
              --z <n>                      venue z threshold, default 1.5
              --min-venues <n>             venues that must agree, default 2
              --stop-atr <n>               stop as a multiple of ATR, default 1.5
              --target-rr <n>              target as a multiple of the stop, default 2.0
              --max-hold-hours <n>         default 12
              --oos <frac>                 out-of-sample tail fraction, default 0.4
              --signal-bars <n>            buckets in the decision window, default 4 (=1h)
              --baseline-bars <n>          trailing buckets for the baseline, default 96 (=24h)

            Shrink --baseline-bars only to exercise the machinery on a short history.
            A baseline of a few hours cannot tell an unusual imbalance from an ordinary
            one, so signals produced that way are a plumbing check, not evidence.
              --sweep                      run the parameter grid instead of one config
              --trades                     dump the trade log

            Before the first run, apply sql/017_flow_bars.sql and backfill:
              SELECT upsert_flow_bars_15m('SOLUSDT', '2026-01-01'::timestamptz, now());

            """);
    }
}
