using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CryptoDecision.ProcessorService.Health;

/// <summary>
/// Validates PostgreSQL connectivity. The ProcessorService lives and dies by
/// its database connection — if the DB is unreachable, no processing happens.
/// </summary>
public sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd  = new NpgsqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy("PostgreSQL reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL unreachable", ex);
        }
    }
}

/// <summary>
/// Minimal HTTP server exposing GET /health on port 8080.
/// Worker services don't have Kestrel, so we use raw HttpListener.
/// Pattern mirrors IngestionService.Health.HealthCheckHttpServer.
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
        logger.LogInformation("ProcessorService health endpoint listening on :8080/health");

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
                        duration = e.Value.Duration.TotalMilliseconds
                    })
                });

                ctx.Response.StatusCode  = report.Status == HealthStatus.Healthy ? 200 : 503;
                ctx.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(json);
                await ctx.Response.OutputStream.WriteAsync(bytes, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "ProcessorService health endpoint error"); }
            finally { ctx?.Response.Close(); }
        }

        listener.Stop();
        logger.LogInformation("ProcessorService health endpoint stopped");
    }
}
