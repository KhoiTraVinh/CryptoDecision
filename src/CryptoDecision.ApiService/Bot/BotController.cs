using CryptoDecision.ApiService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using CryptoDecision.Shared.Bot;

namespace CryptoDecision.ApiService.Bot;

[ApiController]
[Route("api/bot")]
public sealed class BotController(
    BotConfigRepository  configRepo,
    BotRepository        repo) : ControllerBase
{
    // GET /api/bot/status
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct = default)
    {
        var cfg = await configRepo.GetStatusAsync(ct);
        var cap = cfg.CapitalUsd;
        return Ok(new BotStatus(
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
            LastEvalAt     : cfg.LastEvalAt));
    }

    // GET /api/bot/config
    /// <summary>
    /// The configuration the bot is actually running, straight from bot_config.
    ///
    /// The dashboard needs this because its form was static HTML defaults —
    /// capital 1000, position 10%, three slots — while the bot ran on 40, 25% and
    /// two. That is worse than a wrong display: pressing Start posted those
    /// defaults back, so reading the screen and clicking one button silently
    /// rewrote the live configuration.
    /// </summary>
    [HttpGet("config")]
    public async Task<IActionResult> GetConfig(CancellationToken ct = default)
    {
        var cfg = await configRepo.GetConfigAsync(ct);
        return cfg is null
            ? NotFound(new { message = "bot_config row 1 does not exist." })
            : Ok(cfg);
    }

    // POST /api/bot/start
    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] BotStartRequest req, CancellationToken ct = default)
    {
        var cfg = await configRepo.GetStatusAsync(ct);
        if (cfg.Enabled && cfg.IsWorkerAlive)
            return Conflict(new { message = "Bot is already running." });

        var opts = new BotOptions
        {
            Enabled                  = true,
            PaperMode                = req.PaperMode,
            Symbol                   = req.Symbol?.ToUpperInvariant() ?? "SOLUSDT",
            // Defaults to BINANCE, the price feed the strategies read. Live trading
            // requires setting this to OKX explicitly — the only venue with an order
            // engine — so going live is never something a default does for you. The
            // bot worker refuses to start on any other live combination rather than
            // falling back to simulation; see IOrderEngine.DescribeRefusal.
            Exchange                 = req.Exchange?.ToUpperInvariant() ?? "BINANCE",
            ActiveStrategies         = req.ActiveStrategies ?? new List<string> { "MOMENTUM" },
            CapitalUsd               = req.CapitalUsd > 0 ? req.CapitalUsd : 100m,
            MaxOpenTradesPerStrategy = req.MaxOpenTradesPerStrategy > 0 ? req.MaxOpenTradesPerStrategy : 5,
            PositionPctOfCapital     = req.PositionPct > 0 ? req.PositionPct : 0.10m,
            // Fallbacks must form a viable pair — RiskEngine rejects the old
            // 0.3%/5% combination outright (98% breakeven win rate).
            TakeProfitPct            = req.TakeProfitPct > 0 ? req.TakeProfitPct : 0.02m,
            StopLossPct              = req.StopLossPct > 0 ? req.StopLossPct : 0.015m,
            CooldownSeconds          = req.CooldownSeconds > 0 ? req.CooldownSeconds : 120,
            MaxHoldMinutes           = 1440,
            UseTrailingStop          = req.UseTrailingStop,
            TrailingStopPct          = req.TrailingStopPct > 0 ? req.TrailingStopPct : 0.012m,
            UseBreakevenStop         = req.UseBreakevenStop,
            BreakevenTriggerPct      = req.BreakevenTriggerPct > 0 ? req.BreakevenTriggerPct : 0.008m,
            UseDynamicTpSl           = req.UseDynamicTpSl,
            UseAiFilter              = req.UseAiFilter,
            MinAiConfidence          = req.MinAiConfidence > 0 ? req.MinAiConfidence : 0.50m,
            UseAiSizing              = req.UseAiSizing,
            UseAiAgent               = req.UseAiAgent
        };

        await configRepo.StartBotAsync(opts, ct);

        return Ok(new 
        { 
            message = $"Bot started [{string.Join(", ", opts.ActiveStrategies)}] - {opts.MaxOpenTradesPerStrategy} slots/each.", 
            options = opts 
        });
    }

    // POST /api/bot/stop
    [HttpPost("stop")]
    public async Task<IActionResult> Stop(CancellationToken ct = default)
    {
        var cfg = await configRepo.GetStatusAsync(ct);
        if (!cfg.Enabled)
            return BadRequest(new { message = "Bot is not running." });

        await configRepo.StopBotAsync(ct);
        return Ok(new { message = "Bot stopped." });
    }

    // GET /api/bot/trades?limit=50
    [HttpGet("trades")]
    public async Task<IActionResult> GetTrades(
        [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var trades = await repo.GetRecentTradesAsync(limit, ct);
        var dtos = trades.Select(t => new BotTradeDto(
            t.Id, t.Symbol, t.Side, t.Strategy, t.EntryPrice, t.ExitPrice, t.Quantity,
            t.NotionalUsd, t.PnlUsd, t.PnlPct, t.Status,
            t.OpenedAt, t.ClosedAt, t.CloseReason, t.Mode, t.Exchange));
        return Ok(dtos);
    }

    // GET /api/bot/pnl
    [HttpGet("pnl")]
    public async Task<IActionResult> GetPnl(CancellationToken ct = default)
    {
        var trades = await repo.GetRecentTradesAsync(1000, ct);
        var closed = trades.Where(t => t.Status is "CLOSED" or "STOPPED").ToList();

        if (!closed.Any())
            return Ok(new { totalTrades = 0, winRate = 0, totalPnlUsd = 0, totalPnlPct = 0 });

        var totalPnl = closed.Sum(t => t.PnlUsd ?? 0m);
        var wins     = closed.Count(t => (t.PnlUsd ?? 0m) >= 0);

        var cfg = await configRepo.GetStatusAsync(ct);
        var cap = cfg.CapitalUsd;

        return Ok(new
        {
            totalTrades = closed.Count,
            winCount    = wins,
            lossCount   = closed.Count - wins,
            winRate     = Math.Round((decimal)wins / closed.Count, 4),
            totalPnlUsd = Math.Round(totalPnl, 4),
            totalPnlPct = cap > 0 ? Math.Round(totalPnl / cap, 6) : 0,
            avgPnlUsd   = Math.Round(totalPnl / closed.Count, 4),
            byStrategy  = closed
                .GroupBy(t => t.Strategy ?? "UNKNOWN")
                .Select(g => new { strategy = g.Key, count = g.Count(), pnl = g.Sum(t => t.PnlUsd ?? 0m) }),
            byReason    = closed
                .GroupBy(t => t.CloseReason ?? "?")
                .Select(g => new { reason = g.Key, count = g.Count(), pnl = g.Sum(t => t.PnlUsd ?? 0m) })
        });
    }

    // GET /api/bot/debug — show current multi-position status from DB
    [HttpGet("debug")]
    public async Task<IActionResult> Debug(CancellationToken ct = default)
    {
        var cfg = await configRepo.GetStatusAsync(ct);

        if (!cfg.Enabled || !cfg.IsWorkerAlive)
            return Ok(new { error = "Bot is not running.", workerAlive = cfg.IsWorkerAlive, lastHeartbeat = cfg.LastHeartbeat });

        var openTrades = (await repo.GetRecentTradesAsync(100, ct))
            .Where(t => t.Status == "OPEN").ToList();

        // Get current price for unrealized PnL
        var currentPrice = await repo.GetLatestPriceAsync(cfg.Symbol, ct);

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
            // Compute unrealized PnL
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
                id         = t.Id.ToString(),
                strategy   = t.Strategy ?? "N/A",
                side       = t.Side,
                entryPrice = t.EntryPrice,
                age        = Math.Floor((DateTime.UtcNow - t.OpenedAt).TotalMinutes) + "m",
                pnl        = pnlText,
            };
        });

        return Ok(new
        {
            botRunning    = true,
            workerAlive   = cfg.IsWorkerAlive,
            lastHeartbeat = cfg.LastHeartbeat,
            currentPrice  = currentPrice,
            openPositions = positions,
            conditions    = checks,
            options       = new
            {
                cfg.Symbol,
                cfg.ActiveStrategies,
                cfg.MaxOpenTradesPerStrategy,
                cfg.PositionPctOfCapital,
                cfg.CooldownSeconds
            }
        });
    }
}

// ── Request body ──────────────────────────────────────────────────────────────

public sealed record BotStartRequest(
    bool         PaperMode                = true,
    string?      Symbol                   = "SOLUSDT",
    // Venue for live orders. Only OKX is implemented; ignored in paper mode.
    string?      Exchange                 = "BINANCE",
    List<string>? ActiveStrategies        = null,
    decimal      CapitalUsd               = 100m,
    decimal      PositionPct              = 0.10m,
    decimal      TakeProfitPct            = 0.003m,
    decimal      StopLossPct              = 0.05m,
    int          CooldownSeconds          = 120,
    int          MaxOpenTradesPerStrategy = 5,
    bool         UseTrailingStop          = true,
    decimal      TrailingStopPct          = 0.015m,
    bool         UseBreakevenStop         = true,
    decimal      BreakevenTriggerPct      = 0.005m,
    bool         UseDynamicTpSl           = false,
    bool         UseAiFilter              = false,
    decimal      MinAiConfidence          = 0.50m,
    bool         UseAiSizing              = false,
    bool         UseAiAgent               = false
);
