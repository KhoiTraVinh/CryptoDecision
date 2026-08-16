using CryptoDecision.AlertService.Engine;
using CryptoDecision.AlertService.Health;
using CryptoDecision.AlertService.Kafka;
using CryptoDecision.AlertService.Repository;
using CryptoDecision.AlertService.Telemetry;
using CryptoDecision.AlertService.Workers;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    var tempoEndpoint = builder.Configuration["Telemetry:TempoEndpoint"] ?? "http://tempo:4317";

    // ─── Serilog ─────────────────────────────────────────────────────────────
    builder.Services.AddSerilog((svc, cfg) =>
        cfg.ReadFrom.Configuration(builder.Configuration)
           .ReadFrom.Services(svc)
           .Enrich.FromLogContext()
           .Enrich.WithMachineName());

    // ─── PostgreSQL ───────────────────────────────────────────────────────────
    var pgConnStr = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres required");

    var ds = new NpgsqlDataSourceBuilder(pgConnStr).Build();
    builder.Services.AddSingleton(ds);
    builder.Services.AddSingleton<NpgsqlDataSource>(ds);

    // ─── Custom Metrics ───────────────────────────────────────────────────────
    builder.Services.AddSingleton<AlertMetrics>();

    // ─── OpenTelemetry ────────────────────────────────────────────────────────
    var resource = ResourceBuilder.CreateDefault().AddService("alert-service");
    builder.Services.AddOpenTelemetry()
        .WithTracing(t => t
            .SetResourceBuilder(resource)
            .AddOtlpExporter(o => o.Endpoint = new Uri(tempoEndpoint)))
        .WithMetrics(m => m
            .SetResourceBuilder(resource)
            .AddMeter(AlertMetrics.MeterName)
            .AddRuntimeInstrumentation()
            .AddPrometheusHttpListener(o => o.UriPrefixes = ["http://+:9090/"]));

    // ─── Repository & Engine ─────────────────────────────────────────────────
    builder.Services.AddSingleton<AlertRepository>();
    builder.Services.AddSingleton<AlertEngine>();
    builder.Services.AddSingleton<AlertNotificationProducer>();

    // ─── Health checks ───────────────────────────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddCheck<PostgresHealthCheck>("postgres");

    // ─── Workers ─────────────────────────────────────────────────────────────
    builder.Services.AddHostedService<AlertConsumerWorker>();
    builder.Services.AddHostedService<HealthCheckHttpServer>();

    var host = builder.Build();

    // Register active alert count provider for metrics gauge
    var metrics = host.Services.GetRequiredService<AlertMetrics>();
    var engine = host.Services.GetRequiredService<AlertEngine>();
    metrics.RegisterActiveAlertProvider(() => engine.ActiveAlertCount);

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "AlertService crashed on startup");
}
finally
{
    await Log.CloseAndFlushAsync();
}
