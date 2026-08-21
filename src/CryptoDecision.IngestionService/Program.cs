using System.Diagnostics;
using CryptoDecision.IngestionService.Binance;
using CryptoDecision.IngestionService.Bybit;
using CryptoDecision.IngestionService.Channels;
using CryptoDecision.IngestionService.Health;
using CryptoDecision.IngestionService.Kafka;
using CryptoDecision.IngestionService.OKX;
using CryptoDecision.IngestionService.Telemetry;
using CryptoDecision.IngestionService.Workers;
using Microsoft.Extensions.Hosting;
using Serilog;

// Bootstrap Serilog early to capture startup errors
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);


    // ─── Serilog ──────────────────────────────────────────────────────────────
    builder.Services.AddSerilog((services, cfg) =>
        cfg.ReadFrom.Configuration(builder.Configuration)
           .ReadFrom.Services(services)
           .Enrich.FromLogContext()
           .Enrich.WithMachineName()
           .Enrich.WithEnvironmentName());

    // ─── Settings ─────────────────────────────────────────────────────────────
    builder.Services.Configure<BinanceSettings>(
        builder.Configuration.GetSection(BinanceSettings.Section));
    builder.Services.Configure<KafkaProducerSettings>(
        builder.Configuration.GetSection(KafkaProducerSettings.Section));
    builder.Services.Configure<BatchSettings>(
        builder.Configuration.GetSection(BatchSettings.Section));

    // ─── Channels ─────────────────────────────────────────────────────────────
    builder.Services.AddSingleton<TradeChannel>();
    builder.Services.AddSingleton<KlineChannel>();
    builder.Services.AddSingleton<OkxTradeChannel>();
    builder.Services.AddSingleton<BybitTradeChannel>();

    // ─── ActivitySource (distributed tracing) ────────────────────────────────
    builder.Services.AddSingleton(new ActivitySource("CryptoDecision.Ingestion"));

    // ─── Custom Metrics (System.Diagnostics.Metrics) ──────────────────────────
    // Register singleton first so channels can be resolved for the depth gauges
    builder.Services.AddSingleton<IngestionMetrics>(sp =>
    {
        var m = new IngestionMetrics();
        m.RegisterChannelDepth("trades", () => sp.GetRequiredService<TradeChannel>().PendingCount);
        m.RegisterChannelDepth("klines", () => sp.GetRequiredService<KlineChannel>().PendingCount);
        return m;
    });


    // ─── Kafka ────────────────────────────────────────────────────────────────
    builder.Services.AddSingleton<KafkaProducerService>();

    // ─── Normalizers (Adapter Pattern — Anti-Corruption Layer) ────────────────
    builder.Services.AddSingleton<BinanceNormalizer>();
    builder.Services.AddSingleton<OkxNormalizer>();
    builder.Services.AddSingleton<BybitNormalizer>();

    // ─── Exchange WebSocket clients ───────────────────────────────────────────
    builder.Services.AddSingleton<BinanceWebSocketClient>();
    builder.Services.AddSingleton<OkxWebSocketClient>();
    builder.Services.AddSingleton<BybitWebSocketClient>();

    // ─── Workers ──────────────────────────────────────────────────────────────
    // Binance: WebSocket → TradeChannel → Kafka
    builder.Services.AddHostedService<BinanceIngestionWorker>();
    builder.Services.AddHostedService<KafkaBatchPublisherWorker>();
    builder.Services.AddHostedService<KlinePublisherWorker>();

    // OKX: WebSocket → OkxTradeChannel → Kafka
    builder.Services.AddHostedService<OkxIngestionWorker>();
    builder.Services.AddHostedService<OkxKafkaBatchPublisherWorker>();

    // Bybit: WebSocket → BybitTradeChannel → Kafka
    builder.Services.AddHostedService<BybitIngestionWorker>();
    builder.Services.AddHostedService<BybitKafkaBatchPublisherWorker>();



    // ─── Health checks ─────────────────────────────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddCheck<ChannelHealthCheck>("channels");

    // Minimal HTTP listener for /health — workers don't have Kestrel by default
    builder.Services.AddHostedService<HealthCheckHttpServer>();

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "IngestionService crashed on startup");
}
finally
{
    await Log.CloseAndFlushAsync();
}
