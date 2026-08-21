using CryptoDecision.ApiService.Application;
using CryptoDecision.Shared.Bot;
using CryptoDecision.ApiService.Infrastructure.Hubs;
using CryptoDecision.ApiService.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;

namespace CryptoDecision.ApiService.Infrastructure.Services;

/// <summary>
/// Background worker that periodically polls REST application logic (via MediatR)
/// and pushes updates to all connected SignalR clients. This replaces HTTP polling from UI.
/// </summary>
public sealed class DashboardBroadcastService(
    IServiceScopeFactory scopeFactory,
    BotConfigRepository configRepo,
    BotRepository botRepo,
    IHubContext<MarketHub, IMarketClient> hub,
    ILogger<DashboardBroadcastService> logger) : BackgroundService
{
    // The symbols we actively broadcast
    private static readonly string[] ActiveSymbols = { "SOLUSDT" };
    
    // Default config values as used in web dashboards
    private const string Exchange = "ALL";
    private const string BinanceExchange = "BINANCE";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[SignalR] Dashboard broadcast started.");
        
        // Different polling loops running concurrently
        var tasks = new[]
        {
            LoopMarketStatus(stoppingToken),
            LoopBotStatus(stoppingToken)
        };
        
        await Task.WhenAll(tasks);
    }

    private async Task LoopMarketStatus(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var queries = scope.ServiceProvider.GetRequiredService<MarketQueries>();

                foreach (var symbol in ActiveSymbols)
                {
                    // AI Prediction & Today's metrics (every 20s)
                    var status = await queries.GetMarketStatusAsync(symbol, ct);
                    if (status != null)
                    {
                        var group = MarketHub.GroupName(symbol, BinanceExchange);
                        await hub.Clients.Group(group).ReceiveMarketStatus(status);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[SignalR] Error broadcasting MarketStatus");
            }
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
        }
    }


    private async Task LoopBotStatus(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // ── Read bot status from DB (written by Bot Worker heartbeat) ──
                var cfg = await configRepo.GetStatusAsync(ct);
                var cap = cfg.CapitalUsd;

                // 1. Status
                var status = new BotStatus(
                    IsRunning      : cfg.Enabled && cfg.IsWorkerAlive,
                    PaperMode      : cfg.PaperMode,
                    Symbol         : cfg.Symbol,
                    CapitalUsd     : cap,
                    TotalPnlUsd    : cfg.TotalPnlUsd,
                    TotalPnlPct    : cap > 0 ? cfg.TotalPnlUsd / cap : 0m,
                    TotalTrades    : cfg.TotalTrades,
                    WinCount       : cfg.WinCount,
                    LossCount      : cfg.LossCount,
                    OpenTradeCount : cfg.OpenTradeCount,
                    LastEvalAt     : cfg.LastEvalAt);

                // 2. Pnl
                var recentTrades = await botRepo.GetRecentTradesAsync(1000, ct);
                var closed = recentTrades.Where(t => t.Status is "CLOSED" or "STOPPED").ToList();
                object pnl = null!;
                if (!closed.Any())
                {
                    pnl = new { totalTrades = 0, winRate = 0, totalPnlUsd = 0, totalPnlPct = 0 };
                }
                else
                {
                    var totalPnl = closed.Sum(t => t.PnlUsd ?? 0m);
                    var wins = closed.Count(t => (t.PnlUsd ?? 0m) >= 0);
                    pnl = new
                    {
                        totalTrades = closed.Count,
                        winCount = wins,
                        lossCount = closed.Count - wins,
                        winRate = Math.Round((decimal)wins / closed.Count, 4),
                        totalPnlUsd = Math.Round(totalPnl, 4),
                        totalPnlPct = cap > 0 ? Math.Round(totalPnl / cap, 6) : 0
                    };
                }

                // 3. Debug (from DB — no in-memory state)
                object debug;
                if (cfg.Enabled && cfg.IsWorkerAlive)
                {
                    var openTrades = recentTrades.Where(t => t.Status == "OPEN").ToList();

                    // Get current price for unrealized PnL
                    var currentPrice = await botRepo.GetLatestPriceAsync(cfg.Symbol, ct);

                    var checks = new List<object>();
                    foreach (var strat in cfg.ActiveStrategies)
                    {
                        var stratTrades = openTrades.Where(t => t.Strategy == strat).ToList();
                        var slotsUsed = stratTrades.Count;
                        var slotsMax = cfg.MaxOpenTradesPerStrategy;
                        var hasFreeSlot = slotsUsed < slotsMax;

                        // Compute cooldown from the most recent entry in this strategy
                        var lastEntry = stratTrades.OrderByDescending(t => t.OpenedAt).FirstOrDefault();
                        string cooldownText = "Ready";
                        bool cooldownOk = true;
                        if (lastEntry != null)
                        {
                            var elapsed = (DateTime.UtcNow - lastEntry.OpenedAt).TotalSeconds;
                            var remaining = cfg.CooldownSeconds - elapsed;
                            if (remaining > 0)
                            {
                                cooldownOk = false;
                                cooldownText = remaining >= 60
                                    ? $"{Math.Floor(remaining / 60)}m {(int)(remaining % 60)}s"
                                    : $"{(int)remaining}s";
                            }
                        }

                        checks.Add(new
                        {
                            strategy  = strat,
                            slots     = $"{slotsUsed}/{slotsMax}",
                            willEnter = hasFreeSlot && cooldownOk,
                            cooldown  = cooldownText,
                        });
                    }

                    var positions = openTrades.Select(t =>
                    {
                        string pnlText = "—";
                        if (currentPrice.HasValue)
                        {
                            var rawChange = t.Side == "SHORT"
                                ? (t.EntryPrice - currentPrice.Value) / t.EntryPrice
                                : (currentPrice.Value - t.EntryPrice) / t.EntryPrice;
                            var pnlUsd = rawChange * t.NotionalUsd;
                            pnlText = (pnlUsd >= 0 ? "+" : "") + "$" + pnlUsd.ToString("F4");
                        }

                        return new
                        {
                            id       = t.Id.ToString(),
                            strategy = t.Strategy ?? "N/A",
                            side     = t.Side,
                            age      = Math.Floor((DateTime.UtcNow - t.OpenedAt).TotalMinutes) + "m",
                            pnl      = pnlText,
                        };
                    });

                    debug = new
                    {
                        botRunning    = true,
                        workerAlive   = cfg.IsWorkerAlive,
                        currentPrice  = currentPrice,
                        openPositions = positions,
                        conditions    = checks,
                        options = new
                        {
                            Symbol = cfg.Symbol,
                            ActiveStrategies = cfg.ActiveStrategies,
                            MaxOpenTradesPerStrategy = cfg.MaxOpenTradesPerStrategy,
                            PositionPctOfCapital = cfg.PositionPctOfCapital,
                            CooldownSeconds = cfg.CooldownSeconds
                        }
                    };
                }
                else
                {
                    debug = new { error = "Bot is not running.", workerAlive = cfg.IsWorkerAlive };
                }

                // 4. Trades
                var tradesList = await botRepo.GetRecentTradesAsync(20, ct);
                var dtos = tradesList.Select(t => new BotTradeDto(
                    t.Id, t.Symbol, t.Side, t.Strategy, t.EntryPrice, t.ExitPrice, t.Quantity,
                    t.NotionalUsd, t.PnlUsd, t.PnlPct, t.Status,
                    t.OpenedAt, t.ClosedAt, t.CloseReason, t.Mode, t.Exchange));

                await hub.Clients.All.ReceiveBotStatus(new
                {
                    Status = status,
                    Pnl = pnl,
                    Debug = debug,
                    Trades = dtos
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[SignalR] Error broadcasting BotStatus");
            }
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }

}
