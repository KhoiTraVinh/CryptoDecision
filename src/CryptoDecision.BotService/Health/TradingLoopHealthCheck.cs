using CryptoDecision.BotService.Bot;
using CryptoDecision.Shared.Bot;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CryptoDecision.BotService.Health;

/// <summary>
/// Reports unhealthy when the trading loop has stopped turning.
///
/// On 2026-08-22 the loop stopped for 64 minutes: no exception, no restart, exit code
/// zero, and the container reported healthy for every one of those minutes. The health
/// endpoint checked one thing — that postgres was reachable — which proves the process
/// is alive and says nothing about whether it is doing the job it exists to do. A bot
/// that can query the database and never places an order is indistinguishable, to that
/// check, from one trading normally.
///
/// The information was never missing. <see cref="BotStateService.LastEvalAt"/> holds it
/// in memory and bot_config.last_heartbeat holds it on disk; the dashboard even renders
/// STOPPED from the same fact. Nothing turned it into a signal, so the only detection
/// path was a human noticing on the exchange that nothing had traded.
///
/// This asks the in-process clock rather than the database on purpose: the failure to
/// catch is the loop not running, and a check that depends on the loop's own writes
/// having succeeded is testing two things at once. It also keeps the endpoint free of a
/// database round trip every thirty seconds.
/// </summary>
public sealed class TradingLoopHealthCheck(BotStateService state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // A bot the operator stopped is not a sick bot. Failing here would make
        // "stopped on purpose" and "stalled" the same colour, which is the mistake
        // this check exists to undo.
        if (!state.IsRunning)
            return Task.FromResult(HealthCheckResult.Healthy("Bot is stopped."));

        var window  = BotLiveness.StaleAfter(state.Options.EvalIntervalSeconds);
        var lastEval = state.LastEvalAt;

        // Running but no cycle has begun yet. Measured from when it started running,
        // because "never evaluated" must not be a permanent free pass — a loop that
        // hangs before its first cycle is exactly the case a null check would excuse
        // forever.
        if (lastEval is null)
        {
            var since = state.RunningSince;
            var waiting = since.HasValue ? DateTime.UtcNow - since.Value : TimeSpan.Zero;

            return Task.FromResult(waiting <= window
                ? HealthCheckResult.Healthy(
                    "Started; first evaluation cycle has not begun yet.",
                    Data(waiting, window, lastEval))
                : HealthCheckResult.Unhealthy(
                    $"Running for {waiting.TotalSeconds:F0}s without beginning a single " +
                    $"evaluation cycle (allowed {window.TotalSeconds:F0}s).",
                    data: Data(waiting, window, lastEval)));
        }

        var stale = DateTime.UtcNow - lastEval.Value;

        if (stale <= window)
            return Task.FromResult(HealthCheckResult.Healthy(
                "Trading loop is turning.", Data(stale, window, lastEval)));

        // 503 from the endpoint, so the container's wget healthcheck fails and docker
        // marks it unhealthy. Note that docker does not restart an unhealthy container
        // on its own — this makes the stall visible, it does not repair it.
        return Task.FromResult(HealthCheckResult.Unhealthy(
            $"Trading loop last began a cycle {stale.TotalSeconds:F0}s ago, over the " +
            $"{window.TotalSeconds:F0}s allowed for an evaluation interval of " +
            $"{state.Options.EvalIntervalSeconds}s. The process is alive but not trading.",
            data: Data(stale, window, lastEval)));
    }

    private static Dictionary<string, object> Data(
        TimeSpan elapsed, TimeSpan window, DateTime? lastEval) => new()
    {
        ["secondsSinceLastCycle"] = Math.Round(elapsed.TotalSeconds),
        ["allowedSeconds"]        = Math.Round(window.TotalSeconds),
        ["lastEvalAt"]            = lastEval?.ToString("O") ?? "never",
    };
}
