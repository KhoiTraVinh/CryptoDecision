using System.Net;
using System.Text;
using System.Text.Json;
using CryptoDecision.IngestionService.Channels;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace CryptoDecision.IngestionService.Health;

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
