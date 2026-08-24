using System.Diagnostics;
using CryptoDecision.ProcessorService.Health;
using CryptoDecision.ProcessorService.Kafka;
using CryptoDecision.ProcessorService.Models;
using CryptoDecision.ProcessorService.Persistence;
using CryptoDecision.ProcessorService.Telemetry;
using CryptoDecision.ProcessorService.Workers;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);


    // ─── Serilog ─────────────────────────────────────────────────────────────
    builder.Services.AddSerilog((svc, cfg) =>
        cfg.ReadFrom.Configuration(builder.Configuration)
           .ReadFrom.Services(svc)
           .Enrich.FromLogContext()
           .Enrich.WithMachineName());

    // ─── Settings ─────────────────────────────────────────────────────────────
    builder.Services.Configure<ConsumerSettings>(
        builder.Configuration.GetSection(ConsumerSettings.Section));
    builder.Services.Configure<FeatureSettings>(
        builder.Configuration.GetSection(FeatureSettings.Section));
    builder.Services.Configure<FlowBarSettings>(
        builder.Configuration.GetSection(FlowBarSettings.Section));

    // Validated at startup, before any worker runs. Both collections default to empty
    // to dodge the configuration binder's append behaviour, which means a missing
    // section is now indistinguishable from an empty one — and an empty one makes the
    // whole pipeline idle while reporting healthy. Failing here is the only version of
    // that an operator can see.
    builder.Configuration.GetSection(FeatureSettings.Section).Get<FeatureSettings>()
        ?.Validate();
    builder.Configuration.GetSection(FlowBarSettings.Section).Get<FlowBarSettings>()
        ?.Validate();

    // ─── PostgreSQL ───────────────────────────────────────────────────────────
    var pgConnStr = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres required");

    var ds = new NpgsqlDataSourceBuilder(pgConnStr).Build();
    builder.Services.AddSingleton(ds);
    builder.Services.AddSingleton<NpgsqlDataSource>(ds);

    // ─── ActivitySource (distributed tracing) ────────────────────────────────
    builder.Services.AddSingleton(new ActivitySource("CryptoDecision.Processor"));

    // ─── Custom Metrics ───────────────────────────────────────────────────────
    builder.Services.AddSingleton<ProcessorMetrics>();


    // ─── Repositories ─────────────────────────────────────────────────────────
    builder.Services.AddSingleton<DatabaseInitializer>();
    builder.Services.AddSingleton<TradeRepository>();
    builder.Services.AddSingleton<FeatureRepository>();

    // ─── Health checks ─────────────────────────────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddCheck<PostgresHealthCheck>("postgres");

    // ─── Message Deserializers (OCP — pluggable deserialization) ──────────────
    builder.Services.AddSingleton<IMessageDeserializer<TradeBatch>, TradeBatchDeserializer>();
    builder.Services.AddSingleton<IMessageDeserializer<KlineBatch>, KlineBatchDeserializer>();

    // ─── Workers ─────────────────────────────────────────────────────────────
    builder.Services.AddHostedService<TradeProcessorWorker>();
    builder.Services.AddHostedService<KlineProcessorWorker>();
    builder.Services.AddHostedService<FeatureAggregationWorker>();
    builder.Services.AddHostedService<FlowBarAggregationWorker>();
    builder.Services.AddHostedService<HealthCheckHttpServer>();

    var host = builder.Build();

    // Run schema initialization before workers start consuming
    using (var scope = host.Services.CreateScope())
    {
        var init = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        await init.InitializeAsync();
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "ProcessorService crashed on startup");
}
finally
{
    await Log.CloseAndFlushAsync();
}
