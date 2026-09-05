using CryptoDecision.Shared.Bot;
using CryptoDecision.BotService.Agent;
using CryptoDecision.BotService.Bot;
using CryptoDecision.BotService.Exchanges;
using CryptoDecision.BotService.Health;
using CryptoDecision.BotService.Infrastructure;
using CryptoDecision.BotService.Research;
using CryptoDecision.BotService.Strategies;
using CryptoDecision.Shared.Signals;
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
builder.Services.AddSingleton<IFlowBarRepository, FlowBarRepository>();

// Every signal the strategy produces, what the gate did with it, and what the market
// did next. The refused signals are the point: until this table existed, the only
// record of a refusal was a container log that a deploy destroys, so the question
// "was the gate right to refuse" had no data behind it at all.
builder.Services.AddSingleton<SignalOutcomeRepository>();

// ─── Trading Strategies (Strategy Pattern — OCP) ─────────────────────────────

// The only strategy this build registers. MOMENTUM was deleted; which strategies run
// is still bot_config.active_strategies — a database edit, not a redeploy — so a name
// that no longer resolves reaches StrategyEvaluator, logs "Unknown strategy" once per
// cycle and silently trades nothing. Keep this list and the seeded config in step.
builder.Services.AddSingleton<FlowStrategyOptions>(sp =>
{
    var options = new FlowStrategyOptions();
    builder.Configuration.GetSection(FlowStrategyOptions.Section).Bind(options);
    return options;
});
builder.Services.AddSingleton<ITradingStrategy, CrossVenueFlowStrategy>();

// The entry gate. This is the only place a language model can affect whether real
// funds move, and it can only ever prevent a trade — see AiEntryGate.
//
// GateRetrieval controls whether the gate is shown resolved past signals near the one
// it is judging. Its own section, so switching it off is one config line: it is the
// newest input to a live veto and therefore the first thing to disable if the gate
// starts behaving oddly.
builder.Services.AddSingleton<GateRetrievalOptions>(sp =>
{
    var options = new GateRetrievalOptions();
    builder.Configuration.GetSection(GateRetrievalOptions.Section).Bind(options);
    return options;
});
builder.Services.AddSingleton<IEntryGate, AiEntryGate>();

// ─── Trading Bot ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton<BotStateService>();
builder.Services.AddSingleton<BotRepository>();
builder.Services.AddSingleton<BotConfigRepository>();
builder.Services.AddSingleton<StrategyEvaluator>();
builder.Services.AddHostedService<TradingBotService>();

// ─── Outcome labelling (research, never in the trading path) ─────────────────
//
// Resolves recorded signals against the tick stream on its own timer. A separate
// hosted service, so it cannot consume any part of the trading loop's cycle
// deadline, and every failure inside it is caught and logged rather than raised.
builder.Services.AddSingleton<SignalLabelOptions>(sp =>
{
    var options = new SignalLabelOptions();
    builder.Configuration.GetSection(SignalLabelOptions.Section).Bind(options);
    return options;
});
builder.Services.AddHostedService<SignalOutcomeLabeler>();
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

// ─── Ollama, for the entry gate only ──────────────────────────────────────────
//
// The tool-calling agent that used to live here is gone. It handed the model
// get_market_snapshot / get_open_positions / get_account_state / open_position /
// close_position and let it decide entries when bot_config.use_ai_agent was set.
// That flag was false throughout, XVENUE_FLOW owns the entry decision, and 853
// lines of tool plumbing nothing reached is 853 lines that can still break a
// build and still has to be read before every change.
//
// What remains is one call with no tools array: AiEntryGate hands the model a
// finished proposal and takes APPROVE or SKIP. Giving it tools would give it a
// way to act, which is the property being deliberately withheld.
var agentOptions = new AgentOptions
{
    Model          = builder.Configuration["Agent:Model"]   ?? new AgentOptions().Model,
    BaseUrl        = builder.Configuration["Agent:BaseUrl"] ?? new AgentOptions().BaseUrl,
    Temperature    = double.TryParse(builder.Configuration["Agent:Temperature"], out var tp) ? tp : 0.1,
    TimeoutSeconds = int.TryParse(builder.Configuration["Agent:TimeoutSeconds"], out var ts) ? ts : 60,
};
builder.Services.AddSingleton(agentOptions);

builder.Services.AddHttpClient("ollama", c =>
{
    c.BaseAddress = new Uri(agentOptions.BaseUrl);
    c.Timeout = TimeSpan.FromSeconds(agentOptions.TimeoutSeconds);
});

builder.Services.AddSingleton<OllamaAgentClient>();


// ─── Health Checks ─────────────────────────────────────────────────────────────
// Two checks, because "can this process reach the database" and "is this process
// trading" are different questions and only the first was ever asked. See
// TradingLoopHealthCheck for the 64 minutes that bought.
builder.Services.AddHealthChecks()
    .AddNpgSql(pgConnStr, name: "postgres", tags: ["db"])
    .AddCheck<TradingLoopHealthCheck>("trading_loop", tags: ["bot"]);
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

        if (!config.CanTrade)
            startupLog.LogCritical(
                "[Startup] This OKX API key is READ-ONLY (perm={Perm}). It authenticates and reads " +
                "balances fine, but every order will be refused with code 50123. Enable the Trade " +
                "permission on the key in the OKX API settings, or create one that has it. The bot " +
                "will refuse to start in live mode until then.",
                config.Permissions);

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
    "50123" => "This key is read-only. Enable the Trade permission on it in the OKX API settings, " +
               "or create a key that has it.",
    _       => "See the OKX API error code reference for this code.",
};

host.Run();
