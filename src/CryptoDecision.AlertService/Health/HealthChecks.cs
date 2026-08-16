using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace CryptoDecision.AlertService.Health;

public sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(ct);
            return HealthCheckResult.Healthy("PostgreSQL reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL unreachable", ex);
        }
    }
}

public sealed class HealthCheckHttpServer(
    HealthCheckService healthCheckService,
    ILogger<HealthCheckHttpServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://+:8080/");
        listener.Start();
        logger.LogInformation("AlertService health endpoint listening on :8080/health");

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
                var json = JsonSerializer.Serialize(new
                {
                    status = report.Status.ToString(),
                    checks = report.Entries.Select(e => new
                    {
                        name = e.Key,
                        status = e.Value.Status.ToString(),
                        duration = e.Value.Duration.TotalMilliseconds
                    })
                });

                ctx.Response.StatusCode = report.Status == HealthStatus.Healthy ? 200 : 503;
                ctx.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(json);
                await ctx.Response.OutputStream.WriteAsync(bytes, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "AlertService health endpoint error"); }
            finally { ctx?.Response.Close(); }
        }

        listener.Stop();
    }
}
