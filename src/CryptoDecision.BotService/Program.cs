using CryptoDecision.Shared.Bot;
using CryptoDecision.BotService.Bot;
using CryptoDecision.BotService.Health;
using CryptoDecision.BotService.Infrastructure;
using CryptoDecision.BotService.Strategies;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

var tempoEndpoint = builder.Configuration["Telemetry:TempoEndpoint"] ?? "http://tempo:4317";

// ─── Serilog ─────────────────────────────────────────────────────────────────
builder.Services.AddSerilog((_, cfg) =>
    cfg.ReadFrom.Configuration(builder.Configuration)
       .Enrich.FromLogContext()
       .Enrich.WithMachineName()
       .Enrich.WithEnvironmentName());

// ─── PostgreSQL ───────────────────────────────────────────────────────────────
var pgConnStr = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres required");

var ds = new NpgsqlDataSourceBuilder(pgConnStr).Build();
builder.Services.AddSingleton(ds);

// ─── Repositories ─────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IFeatureRepository, FeatureRepository>();
builder.Services.AddSingleton<IMomentumRepository, MomentumRepository>();
builder.Services.AddSingleton<IVolumeRepository, VolumeRepository>();
builder.Services.AddSingleton<IPredictionRepository, PredictionRepository>();

// ─── Trading Strategies (Strategy Pattern — OCP) ─────────────────────────────
builder.Services.AddSingleton<ITradingStrategy, GridStrategy>();
builder.Services.AddSingleton<ITradingStrategy, MomentumStrategy>();
builder.Services.AddSingleton<ITradingStrategy, AlwaysBuyStrategy>();
builder.Services.AddSingleton<ITradingStrategy, RsiStrategy>();

// ─── Trading Bot ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton<BotStateService>();
builder.Services.AddSingleton<BotRepository>();
builder.Services.AddSingleton<BotConfigRepository>();
builder.Services.AddSingleton<IOrderEngine, PaperOrderEngine>();
builder.Services.AddSingleton<StrategyEvaluator>();
builder.Services.AddHostedService<TradingBotService>();
builder.Services.AddHttpClient("binance-public", c =>
{
    c.BaseAddress = new Uri("https://api.binance.com");
    c.Timeout = TimeSpan.FromSeconds(5);
});

// ─── OpenTelemetry (tracing only — no Prometheus since no metrics port exposed) ─
var resource = ResourceBuilder.CreateDefault().AddService("bot-service");
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .SetResourceBuilder(resource)
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(o => o.Endpoint = new Uri(tempoEndpoint)));

// ─── Health Checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddNpgSql(pgConnStr, name: "postgres", tags: ["db"]);
builder.Services.AddHostedService<HealthCheckHttpServer>();

var host = builder.Build();
host.Run();
