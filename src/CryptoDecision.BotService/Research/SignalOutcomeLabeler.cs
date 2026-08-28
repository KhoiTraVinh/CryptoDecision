using CryptoDecision.Shared.Signals;
using Npgsql;

namespace CryptoDecision.BotService.Research;

/// <summary>
/// Resolves every recorded signal against the tick stream: what the market did after
/// each one, including — especially — the ones the gate refused.
///
/// Why this job exists at all
/// --------------------------
/// The gate's refusals were auditable only from `docker logs bot`, which holds one
/// container's lifetime and is destroyed on every push to main. So the only evidence
/// that could say whether refusing was right was being deleted several times a week.
/// signal_outcomes keeps the signals; this keeps their results.
///
/// Why it runs in BotService and not ProcessorService
/// -------------------------------------------------
/// ProcessorService is the better home on paper — it owns the data plane and this is
/// a data job. It is not reachable today: that service builds from its own directory
/// with no reference to CryptoDecision.Shared, so moving this there means changing
/// its csproj, its Dockerfile, the compose build context and the CI matrix together.
/// Four coordinated changes to the path that deploys a bot holding real positions, to
/// relocate a research query, is the wrong trade. Revisit it if ProcessorService ever
/// needs Shared for another reason.
///
/// What running here does and does not cost
/// ---------------------------------------
/// It is a separate hosted service, so it never runs inside the trading loop and
/// cannot consume any part of that loop's 90-second cycle deadline. It takes one
/// connection from a pool sized at 20 and holds it for the length of one statement.
/// The scan itself is Postgres-side work on the same host either way, so which .NET
/// process asks for it changes nothing about the load — only about what a bug in it
/// can reach. Every failure here is caught and logged; nothing it does can refuse,
/// delay or alter a trade.
///
/// Idempotency
/// -----------
/// One UPDATE that only touches rows which are unresolved or below the current label
/// version. A second run changes nothing; a run after a crash resumes exactly where
/// it stopped, because the rows already written no longer match. No state is carried
/// between passes.
///
/// Cadence against the retention clock
/// -----------------------------------
/// Ticks live for RawTradeRetentionDays (7) and a signal needs its full 12-hour hold
/// horizon of ticks to resolve, so it must be labelled within about six days or the
/// evidence is gone. Thirty minutes leaves days of margin — which matters, because
/// the retention purge runs on its own schedule in another service and a labeler that
/// only just beat it would be a race nobody would notice losing.
/// </summary>
public sealed class SignalOutcomeLabeler(
    NpgsqlDataSource              dataSource,
    SignalOutcomeRepository       repository,
    SignalLabelOptions            options,
    ILogger<SignalOutcomeLabeler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            log.LogInformation(
                "[Labeler] Disabled. signal_outcomes will keep collecting signals with no " +
                "outcome attached — statistics built on it will be empty rather than wrong.");
            return;
        }

        log.LogInformation(
            "[Labeler] Starting — symbol {Symbol}, every {Interval}, label version {Version}.",
            options.Symbol, options.Interval, options.LabelVersion);

        using var timer = new PeriodicTimer(options.Interval);

        do
        {
            await RunPassAsync(stoppingToken);
        }
        while (!stoppingToken.IsCancellationRequested
               && await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunPassAsync(CancellationToken ct)
    {
        try
        {
            var horizon  = await ResolveHorizonAsync(ct);
            var labelled = await repository.LabelAsync(
                options.Symbol, horizon, options.LabelVersion, ct);
            var coverage = await repository.GetCoverageAsync(options.Symbol, ct);

            if (labelled > 0)
                log.LogInformation(
                    "[Labeler] Resolved {Count} signal(s) against a {Horizon}-minute horizon. " +
                    "{Decided} decided, {Pending} still open.",
                    labelled, horizon, coverage.Decided, coverage.Pending);

            // Warned every pass, deliberately. This table exists to support claims
            // like "the gate's dispersion refusals lose money", and the first audit
            // that produced such a claim had 15 signals from a single 23-hour window.
            // The sample size belongs next to the numbers, not in a document nobody
            // opens.
            if (!coverage.Sufficient)
                log.LogWarning(
                    "[Labeler] {Decided} decided signal(s) spanning {Days:F1} day(s) — below the " +
                    "{Minimum}-signal / 7-day floor. Nothing in signal_gate_report is evidence " +
                    "yet; it describes one market regime. Do not tune thresholds from it.",
                    coverage.Decided,
                    coverage is { First: { } f, Last: { } l } ? (l - f).TotalDays : 0.0,
                    OutcomeCoverage.MinimumForStatistics);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never fatal, and never propagated. A failed pass costs a delay in
            // labelling; the next pass finds the same unresolved rows. Taking the bot
            // down over a research query would trade a real outage for a statistic.
            log.LogError(ex,
                "[Labeler] Pass failed. The unresolved rows stay unresolved and the next " +
                "pass retries them.");
        }
    }

    /// <summary>
    /// The hold horizon to score against, read from bot_config rather than configured
    /// separately.
    ///
    /// An outcome only means something against the limit the bot would really have
    /// held to. The audited window contains a signal whose target arrived 348 minutes
    /// later: a win under the live 720-minute limit, and nothing at all under a
    /// 240-minute one. Two copies of that number would eventually disagree, and the
    /// disagreement would surface as a table that quietly measures a strategy nobody
    /// is running.
    /// </summary>
    private async Task<int> ResolveHorizonAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var cmd  = new NpgsqlCommand(
                "SELECT max_hold_minutes FROM bot_config WHERE id = 1", conn);

            if (await cmd.ExecuteScalarAsync(ct) is int minutes && minutes > 0) return minutes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(
                "[Labeler] Could not read bot_config.max_hold_minutes ({Err}); scoring this pass " +
                "against the {Fallback}-minute fallback instead.", ex.Message, options.HorizonMinutes);
        }

        return options.HorizonMinutes;
    }
}

public sealed class SignalLabelOptions
{
    public const string Section = "SignalLabeling";

    /// <summary>
    /// Off leaves the table filling with unlabelled rows — visibly empty statistics
    /// rather than silently wrong ones. A switch exists because this reads the whole
    /// tick table, and an operator debugging a loaded host should be able to stop it
    /// without stopping the bot.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// One symbol, not a collection. The bot trades one instrument at a time
    /// (bot_config.symbol) and a collection property here would inherit the
    /// configuration binder's append-don't-replace behaviour, which has already cost
    /// this repository a double-counted backfill once.
    /// </summary>
    public string Symbol { get; set; } = "SOLUSDT";

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Used only when bot_config cannot be read; 12h matches the live config.</summary>
    public int HorizonMinutes { get; set; } = 720;

    /// <summary>
    /// Raise to re-label every existing row under corrected rules. Rows carry the
    /// version that produced them, so a table half-labelled by two generations of
    /// this job shows up in a query instead of being averaged together.
    /// </summary>
    public int LabelVersion { get; set; } = 1;
}
