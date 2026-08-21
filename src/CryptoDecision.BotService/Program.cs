using CryptoDecision.Shared.Bot;
using CryptoDecision.BotService.Agent;
using CryptoDecision.BotService.Bot;
using CryptoDecision.BotService.Exchanges;
using CryptoDecision.BotService.Health;
using CryptoDecision.BotService.Infrastructure;
using CryptoDecision.BotService.Strategies;
using Npgsql;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);


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
builder.Services.AddSingleton<IPredictionRepository, PredictionRepository>();

// ─── Trading Strategies (Strategy Pattern — OCP) ─────────────────────────────
builder.Services.AddSingleton<ITradingStrategy, MomentumStrategy>();

// ─── Trading Bot ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton<BotStateService>();
builder.Services.AddSingleton<BotRepository>();
builder.Services.AddSingleton<BotConfigRepository>();
builder.Services.AddSingleton<StrategyEvaluator>();
builder.Services.AddHostedService<TradingBotService>();
builder.Services.AddHttpClient("binance-public", c =>
{
    c.BaseAddress = new Uri("https://api.binance.com");
    c.Timeout = TimeSpan.FromSeconds(5);
});

// ─── Order execution ──────────────────────────────────────────────────────────
// Both engines are always constructed; RoutingOrderEngine decides per order which
// one applies. Registering only the "active" engine would make the choice a
// startup decision, and it is not — bot_config.paper_mode can change at runtime,
// and an exit has to reach the venue its entry filled on regardless.
var okxOptions = builder.Configuration.GetSection("Okx").Get<OkxOptions>() ?? new OkxOptions();
builder.Services.AddSingleton(okxOptions);

builder.Services.AddHttpClient(OkxSignedClient.HttpClientName, c =>
{
    c.BaseAddress = new Uri(okxOptions.BaseUrl);
    c.Timeout     = TimeSpan.FromSeconds(okxOptions.TimeoutSeconds);
});

builder.Services.AddSingleton<OkxSignedClient>();
builder.Services.AddSingleton<OkxTradingClient>();
builder.Services.AddSingleton<OkxInstrumentCache>();

builder.Services.AddSingleton<PaperOrderEngine>();
builder.Services.AddSingleton<OkxOrderEngine>();
builder.Services.AddSingleton<IOrderEngine, RoutingOrderEngine>();

// ─── Price feeds ──────────────────────────────────────────────────────────────
// Resolved per cycle from bot_config.exchange so the price the strategies and the
// exit thresholds work on comes from the same order book the orders fill on.
// Registered via factory delegates so each feed is one instance, shared between
// its concrete registration and the IPriceFeed set the resolver enumerates.
builder.Services.AddSingleton<BinancePriceFeed>();
builder.Services.AddSingleton<OkxPriceFeed>();
builder.Services.AddSingleton<IPriceFeed>(sp => sp.GetRequiredService<BinancePriceFeed>());
builder.Services.AddSingleton<IPriceFeed>(sp => sp.GetRequiredService<OkxPriceFeed>());
builder.Services.AddSingleton<PriceFeedResolver>();

// ─── AI Agent (tool-calling over Ollama) ──────────────────────────────────────
// Active only when bot_config.use_ai_agent is set. Tools are singletons so their
// schemas are built once; AgentContext carries the per-cycle facts they read.
var agentOptions = new AgentOptions
{
    Model          = builder.Configuration["Agent:Model"]   ?? "qwen2.5:7b",
    BaseUrl        = builder.Configuration["Agent:BaseUrl"] ?? "http://ollama:11434",
    MaxIterations  = int.TryParse(builder.Configuration["Agent:MaxIterations"], out var mi) ? mi : 8,
    Temperature    = double.TryParse(builder.Configuration["Agent:Temperature"], out var tp) ? tp : 0.1,
    TimeoutSeconds = int.TryParse(builder.Configuration["Agent:TimeoutSeconds"], out var ts) ? ts : 180,
};
builder.Services.AddSingleton(agentOptions);

builder.Services.AddHttpClient("ollama", c =>
{
    c.BaseAddress = new Uri(agentOptions.BaseUrl);
    // Generous: a tool-calling round trip against a 7B on CPU is tens of seconds.
    c.Timeout = TimeSpan.FromSeconds(agentOptions.TimeoutSeconds);
});

builder.Services.AddSingleton<AgentContext>();
builder.Services.AddSingleton<OllamaAgentClient>();
builder.Services.AddSingleton<ITradingTool, GetMarketSnapshotTool>();
builder.Services.AddSingleton<ITradingTool, GetOpenPositionsTool>();
builder.Services.AddSingleton<ITradingTool, GetAccountStateTool>();
builder.Services.AddSingleton<ITradingTool, OpenPositionTool>();
builder.Services.AddSingleton<ITradingTool, ClosePositionTool>();
builder.Services.AddSingleton<TradingAgent>();


// ─── Health Checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddNpgSql(pgConnStr, name: "postgres", tags: ["db"]);
builder.Services.AddHostedService<HealthCheckHttpServer>();

var host = builder.Build();

// ─── State the execution posture once, at startup ─────────────────────────────
// Whether this process can spend real money is the single most important thing
// about it, and it should be answerable from the first line of the log rather
// than inferred from the absence of order messages later.
var startupLog = host.Services.GetRequiredService<ILogger<Program>>();
var liveRefusal = okxOptions.DescribeRefusal();

if (liveRefusal is not null)
    startupLog.LogInformation(
        "[Startup] Live order placement is DISABLED — {Reason} The bot can only paper trade.",
        liveRefusal);
else
{
    startupLog.LogWarning(
        "[Startup] Live order placement is ARMED on OKX {Mode}. Per-order ceiling ${Cap}. " +
        "Orders are placed whenever bot_config.paper_mode is false.",
        okxOptions.DemoTrading ? "demo trading (simulated funds)" : "REAL FUNDS",
        okxOptions.MaxOrderNotionalUsd);

    // ─── Credential self-check ────────────────────────────────────────────────
    //
    // One signed read-only call, before the loop starts. Being armed only means
    // credentials are *present*; whether they authenticate — and whether they
    // belong to the environment the demo flag selects — is a different question,
    // and the only other place it gets answered is halfway through placing a real
    // order. Failing here costs a log line; failing there means a signal was acted
    // on, refused by the exchange, and the opportunity is gone.
    //
    // Never fatal: a network blip at startup should not stop a bot that may be
    // perfectly able to paper trade.
    try
    {
        var probe  = host.Services.GetRequiredService<OkxTradingClient>();
        var config = await probe.GetAccountConfigAsync(CancellationToken.None);

        startupLog.LogInformation(
            "[Startup] OKX credentials authenticated. Account level {Level}, position mode {PosMode}.",
            config.AccountLevel, config.PosMode);

        // Account level 1 is spot-only and cannot hold a swap position, so every
        // order this bot places would be refused. Worth saying now, in the words
        // of the setting the operator has to change.
        if (config.AccountLevel == "1")
            startupLog.LogError(
                "[Startup] OKX account is in Simple mode (acctLv=1), which cannot trade perpetual " +
                "swaps. Switch the account to Single-currency or Multi-currency margin in the OKX " +
                "app, or every order will be refused.");
    }
    catch (OkxApiException ex)
    {
        startupLog.LogCritical(
            "[Startup] OKX rejected the credential check — code {Code}: {Message} {Hint}",
            ex.Code, ex.Message, DescribeAuthFailure(ex.Code, okxOptions.DemoTrading));
    }
    catch (Exception ex)
    {
        startupLog.LogError(ex,
            "[Startup] Could not reach OKX to verify credentials. Trading will retry on demand.");
    }
}

// Maps the OKX auth error codes to the thing an operator actually has to change.
// The codes are not self-explanatory and the difference between them is the
// difference between a wrong passphrase and a key from the wrong environment.
static string DescribeAuthFailure(string code, bool demoTrading) => code switch
{
    "50101" => demoTrading
        ? "This key was created for live trading, but Okx:DemoTrading is true. Demo trading " +
          "needs an API key created inside OKX's Demo Trading environment — a live key will " +
          "not authenticate there. Either create a demo key, or set OKX_DEMO_TRADING=false."
        : "This key belongs to OKX's demo environment, but Okx:DemoTrading is false. Set " +
          "OKX_DEMO_TRADING=true, or use a live API key.",
    "50102" => "The request timestamp was outside OKX's accepted window. The container clock has " +
               "drifted — check the host clock and Docker's time sync.",
    "50103" => "Request header OK-ACCESS-KEY is missing or empty.",
    "50104" => "Request header OK-ACCESS-PASSPHRASE is missing or empty.",
    "50105" => "Wrong passphrase. This is the phrase chosen when the API key was created, " +
               "not the account login password.",
    "50111" => "Invalid API key.",
    "50113" => "Invalid signature. The key and passphrase may be fine while the secret is wrong " +
               "or truncated — check for a trailing space or newline in the .env value.",
    "50110" => "The caller IP is not on this key's allowlist. Add the host's public IP to the " +
               "key in the OKX API settings, or remove the IP restriction.",
    "50119" => "API key does not exist.",
    _       => "See the OKX API error code reference for this code.",
};

host.Run();
