using CryptoDecision.PolymarketService;
using CryptoDecision.PolymarketService.Polymarket;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((services, cfg) =>
        cfg.ReadFrom.Configuration(builder.Configuration)
           .ReadFrom.Services(services)
           .Enrich.FromLogContext()
           .Enrich.WithMachineName()
           .Enrich.WithEnvironmentName());

    builder.Services.Configure<PolymarketOptions>(builder.Configuration.GetSection(PolymarketOptions.Section));

    // Fail at startup rather than on the first order: a bad stake or an unreachable
    // price level is a configuration bug, and discovering it mid-match is worse.
    var settings = builder.Configuration.GetSection(PolymarketOptions.Section).Get<PolymarketOptions>() ?? new PolymarketOptions();
    settings.Validate();

    // Polymarket rate-limits by IP and all three hosts are read on every cycle, so each
    // client gets a timeout short enough that a hung call cannot stall the scan loop.
    builder.Services.AddHttpClient<GammaClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
    builder.Services.AddHttpClient<ClobClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
    builder.Services.AddHttpClient<DataApiClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
    builder.Services.AddHttpClient<ChainClient>(c => c.Timeout = TimeSpan.FromSeconds(15));

    builder.Services.AddHostedService<EsportsEntryWorker>();

    await builder.Build().RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "PolymarketService terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
