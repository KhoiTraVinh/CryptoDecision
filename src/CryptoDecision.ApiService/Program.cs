using CryptoDecision.ApiService.Application;
using CryptoDecision.Shared.Bot;
using CryptoDecision.ApiService.Bot; // Some things like BotController might still be there
using CryptoDecision.ApiService.Domain.Interfaces;
using CryptoDecision.ApiService.Infrastructure.Hubs;
using CryptoDecision.ApiService.Infrastructure.Persistence;
using CryptoDecision.ApiService.Infrastructure.Services;
using CryptoDecision.ApiService.Middleware;
// Note: Bot engine (TradingBotService, StrategyEvaluator, PaperOrderEngine, BotStateService)
// has been moved to CryptoDecision.BotService (separate Worker container).
using Npgsql;
using Serilog;

var builder = WebApplication.CreateBuilder(args);


// ─── Serilog ─────────────────────────────────────────────────────────────────
builder.Host.UseSerilog((ctx, cfg) =>
    cfg.ReadFrom.Configuration(ctx.Configuration)
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
builder.Services.AddSingleton<IKlineRepository, KlineRepository>();
builder.Services.AddSingleton<IVolumeRepository, VolumeRepository>();
builder.Services.AddSingleton<ITradeQueryRepository, TradeQueryRepository>();

// ─── Queries ─────────────────────────────────────────────────────────────────
// Scoped so the SignalR broadcasters can resolve it per-tick from a scope.
builder.Services.AddScoped<MarketQueries>();

// ─── SignalR ──────────────────────────────────────────────────────────────────
builder.Services.AddSignalR();
builder.Services.AddHostedService<VolumeAnalysisBroadcastService>();
builder.Services.AddHostedService<WhaleAlertBroadcastService>();
builder.Services.AddHostedService<DashboardBroadcastService>();

// ─── Trading Bot (API-side: read/write config via DB, engine runs in BotService) ──
builder.Services.AddSingleton<BotRepository>();
builder.Services.AddSingleton<BotConfigRepository>();


// ─── API ──────────────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddResponseCaching();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
    c.SwaggerDoc("v1", new() { Title = "CryptoDecision API", Version = "v1" }));

// ─── Health Checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddNpgSql(pgConnStr, name: "postgres", tags: ["db"]);

// ─── CORS ─────────────────────────────────────────────────────────────────────
// SetIsOriginAllowed(_ => true) + AllowCredentials() — allows any origin while
// still satisfying SignalR's credential requirement.
// This is safe for a private home server; tighten for public production.
builder.Services.AddCors(opts =>
    opts.AddPolicy("dashboard", p =>
        p.SetIsOriginAllowed(_ => true)   // any origin (LAN, WAN, Capacitor, browser)
         .AllowAnyHeader()
         .AllowAnyMethod()
         .AllowCredentials()));            // required by SignalR

var app = builder.Build();

// ─── Middleware pipeline ──────────────────────────────────────────────────────
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseSerilogRequestLogging(opts =>
    opts.MessageTemplate = "HTTP {RequestMethod} {RequestPath} → {StatusCode} ({Elapsed:0.000}ms)");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("dashboard");
app.UseResponseCaching();
app.MapControllers();
app.MapHub<MarketHub>("/hubs/market");

app.MapHealthChecks("/health", new()
{
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name     = e.Key,
                status   = e.Value.Status.ToString(),
                duration = e.Value.Duration.TotalMilliseconds
            })
        }));
    }
});

app.Run();
