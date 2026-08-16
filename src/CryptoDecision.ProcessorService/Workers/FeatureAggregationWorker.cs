using CryptoDecision.ProcessorService.Persistence;
using CryptoDecision.ProcessorService.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CryptoDecision.ProcessorService.Workers;

/// <summary>
/// Two responsibilities:
///   1. Compute daily features (every 5 minutes during the day, so the
///      dashboard always has fresh data without waiting until midnight)
///   2. Ensure tomorrow's trade partition exists (runs daily at midnight)
/// </summary>
public sealed class FeatureAggregationWorker(
    FeatureRepository featureRepo,
    DatabaseInitializer dbInit,
    NpgsqlDataSource dataSource,
    ProcessorMetrics metrics,
    IOptions<FeatureSettings> settings,
    ILogger<FeatureAggregationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("FeatureAggregationWorker starting");

        // Run once at startup (catch up if service was down)
        await RunCycleAsync(stoppingToken);

        using var timer = new PeriodicTimer(settings.Value.AggregationInterval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            await RunCycleAsync(stoppingToken);
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var today    = DateOnly.FromDateTime(DateTime.UtcNow);
        var symbols  = settings.Value.Symbols;

        foreach (var symbol in symbols)
        {
            try
            {
                // Compute today's features
                await featureRepo.UpsertDailyFeatureAsync(symbol, today, ct);
                metrics.FeaturesComputed.Add(1, new KeyValuePair<string, object?>("symbol", symbol));

                // Also refresh yesterday to catch late-arriving trades
                await featureRepo.UpsertDailyFeatureAsync(symbol, today.AddDays(-1), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Feature aggregation failed for {Symbol} {Date}", symbol, today);
            }
        }

        // Ensure upcoming partitions exist
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await dbInit.EnsureDailyPartitionAsync(conn, DateTime.UtcNow.Date.AddDays(1), ct);
            await dbInit.EnsureDailyPartitionAsync(conn, DateTime.UtcNow.Date.AddDays(2), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Partition maintenance failed");
        }
    }
}

public sealed class FeatureSettings
{
    public const string Section   = "Feature";
    public string[] Symbols       { get; set; } = ["BTCUSDT", "ETHUSDT"];
    public TimeSpan AggregationInterval { get; set; } = TimeSpan.FromMinutes(5);
}
