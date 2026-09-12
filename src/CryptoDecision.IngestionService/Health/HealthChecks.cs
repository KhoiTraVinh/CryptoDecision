using System.Net;
using System.Text;
using System.Text.Json;
using CryptoDecision.IngestionService.Channels;
using CryptoDecision.IngestionService.Telemetry;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace CryptoDecision.IngestionService.Health;

/// <summary>
/// Reports unhealthy when a feed has stopped delivering data.
///
/// The check this service had measured channel depth, which asks whether Kafka is
/// keeping up and answers the OPPOSITE of the truth for the failure that actually
/// happens: a dead feed leaves the channel EMPTY, so a service ingesting nothing
/// reported Healthy indefinitely. The container stayed green, the bot downstream
/// abstained on FLOW_BARS_STALE, and both a stopped feed and a quiet market looked the
/// same from every surface.
///
/// Both checks are kept because they fail in opposite directions and neither implies the
/// other: a backed-up channel with data still arriving is a different problem from a
/// silent socket, and a service can be in either state alone.
/// </summary>
public sealed class FeedLivenessHealthCheck(FeedLiveness liveness) : IHealthCheck
{
    /// <summary>
    /// How long a feed may deliver no trades before it is called dead.
    ///
    /// SOL-USDT prints continuously on all three venues — the thinnest of them, Bybit,
    /// still medians roughly $0.89M per 15-minute bucket — so five minutes of complete
    /// silence is not a quiet market. It is generous on purpose: the cost of being wrong
    /// here is an alert on a genuinely idle overnight stretch, and the cost of being too
    /// generous is the failure this check was written for.
    /// </summary>
    private static readonly TimeSpan DataIdleLimit = TimeSpan.FromMinutes(5);

    /// <summary>Grace after a connection before its silence counts. One idle limit.</summary>
    private static readonly TimeSpan StartupGrace = DataIdleLimit;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var feeds = liveness.Snapshot();

        var data = feeds.ToDictionary(
            f => $"{f.Exchange.ToLowerInvariant()}_seconds_since_data",
            object (f) => f.SinceData is { } d ? Math.Round(d.TotalSeconds) : "never");

        foreach (var f in feeds)
            data[$"{f.Exchange.ToLowerInvariant()}_connections"] = f.Connections;

        if (feeds.Count == 0)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "No exchange feed has even been registered. No WebSocket client started.",
                data: data));

        var dead = new List<string>();

        foreach (var f in feeds)
        {
            // Never connected at all. Distinguished from "connected and then went quiet"
            // because the fix is different and the operator should not have to guess.
            if (f.SinceConnected is null)
            {
                dead.Add($"{f.Exchange} has never connected");
                continue;
            }

            // Connected recently enough that silence is not yet evidence.
            if (f.SinceConnected <= StartupGrace && f.SinceData is null) continue;

            if (f.SinceData is not { } since)
            {
                dead.Add(
                    $"{f.Exchange} connected {f.SinceConnected!.Value.TotalSeconds:F0}s ago and has " +
                    "delivered no trades at all — most likely subscribed to nothing");
                continue;
            }

            if (since > DataIdleLimit)
                dead.Add($"{f.Exchange} last delivered a trade {since.TotalMinutes:F1} min ago");
        }

        if (dead.Count == 0)
            return Task.FromResult(HealthCheckResult.Healthy("All feeds delivering.", data));

        return Task.FromResult(HealthCheckResult.Unhealthy(
            $"{dead.Count} of {feeds.Count} feed(s) are not delivering data " +
            $"(limit {DataIdleLimit.TotalMinutes:F0} min): {string.Join("; ", dead)}.",
            data: data));
    }
}

/// <summary>
/// Reports degraded status if channel buffers are filling up — an early
/// warning that the Kafka producer can't keep up with the WebSocket.
/// </summary>
public sealed class ChannelHealthCheck(
    TradeChannel tradeChannel,
    KlineChannel klineChannel) : IHealthCheck
{
    private const int DegradedThreshold  = 30_000;   // 60% of capacity
    private const int UnhealthyThreshold = 45_000;   // 90% of capacity

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var tradePending = tradeChannel.PendingCount;
        var data = new Dictionary<string, object>
        {
            ["trade_channel_depth"] = tradePending,
            ["kline_channel_depth"] = klineChannel.PendingCount
        };

        if (tradePending >= UnhealthyThreshold)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Trade channel critically full: {tradePending}", data: data));

        if (tradePending >= DegradedThreshold)
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Trade channel filling up: {tradePending}", data: data));

        return Task.FromResult(HealthCheckResult.Healthy(data: data));
    }
}

/// <summary>
/// Minimal HTTP server exposing GET /health on port 8080.
/// Worker services don't have Kestrel, so we use raw HttpListener.
/// </summary>
public sealed class HealthCheckHttpServer(
    HealthCheckService healthCheckService,
    ILogger<HealthCheckHttpServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://+:8080/");
        listener.Start();
        logger.LogInformation("Health HTTP endpoint listening on :8080/health");

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext? ctx = null;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(stoppingToken);

                if (ctx.Request.Url?.AbsolutePath is not ("/health" or "/health/"))
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    continue;
                }

                var report = await healthCheckService.CheckHealthAsync(stoppingToken);
                var json   = JsonSerializer.Serialize(new
                {
                    status = report.Status.ToString(),
                    checks = report.Entries.Select(e => new
                    {
                        name     = e.Key,
                        status   = e.Value.Status.ToString(),
                        data     = e.Value.Data
                    })
                });

                ctx.Response.StatusCode  = report.Status == HealthStatus.Healthy ? 200 : 503;
                ctx.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(json);
                await ctx.Response.OutputStream.WriteAsync(bytes, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Health endpoint error"); }
            finally { ctx?.Response.Close(); }
        }

        listener.Stop();
    }
}
