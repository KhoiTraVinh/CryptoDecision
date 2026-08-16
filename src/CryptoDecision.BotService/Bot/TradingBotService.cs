using CryptoDecision.Shared.Bot;

namespace CryptoDecision.BotService.Bot;

/// <summary>
/// BackgroundService that drives the trading bot evaluation loop.
/// Polls bot_config from PostgreSQL to receive start/stop commands from the API.
/// Writes heartbeat + stats back to bot_config so Dashboard can display status.
/// </summary>
public sealed class TradingBotService(
    BotStateService       state,
    StrategyEvaluator     strategy,
    IOrderEngine          orderEngine,
    BotRepository         repo,
    BotConfigRepository   configRepo,
    ILogger<TradingBotService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("[TradingBot] Worker Service started. Polling bot_config for commands...");

        // Seed in-memory stats from DB on startup
        var history = await repo.GetRecentTradesAsync(500, stoppingToken);
        state.SeedStats(history);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // ── Poll config from DB (API writes start/stop here) ──────────
                var dbConfig = await configRepo.GetConfigAsync(stoppingToken);
                if (dbConfig is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                if (dbConfig.Enabled && !state.IsRunning)
                {
                    // API sent start command → validate the risk profile before
                    // committing capital to it. A configuration that cannot profit
                    // arithmetically will not be rescued by a good entry signal.
                    if (!PassesRiskGate(dbConfig))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                        continue;
                    }

                    state.Start(dbConfig);
                    log.LogInformation("[TradingBot] Received START command from API. Strategies: [{Strats}]",
                        string.Join(", ", dbConfig.ActiveStrategies));
                }
                else if (!dbConfig.Enabled && state.IsRunning)
                {
                    // API sent stop command → stop bot
                    state.Stop();
                    log.LogInformation("[TradingBot] Received STOP command from API.");
                }
                else if (dbConfig.Enabled && state.IsRunning)
                {
                    // Update options if changed while running
                    state.Start(dbConfig);
                }

                var opts = state.Options;
                await Task.Delay(TimeSpan.FromSeconds(opts.EvalIntervalSeconds), stoppingToken);

                if (!state.IsRunning) continue;

                await EvalCycleAsync(opts, stoppingToken);

                // ── Write heartbeat back to DB ────────────────────────────────
                var status = state.GetStatus();
                await configRepo.UpdateHeartbeatAsync(
                    status.LastEvalAt ?? DateTime.UtcNow,
                    status.OpenTradeCount,
                    status.TotalTrades,
                    status.TotalPnlUsd,
                    status.WinCount,
                    status.LossCount,
                    stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "[TradingBot] Unhandled error in main loop");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    /// <summary>
    /// Refuse to start on a configuration that cannot make money.
    ///
    /// Warnings are logged and trading proceeds; a critical finding (fees exceeding
    /// the target, an inverted reward:risk, exposure beyond the account) blocks the
    /// start. The config stays Enabled in the database, so this re-evaluates every
    /// 30 seconds and the bot starts on its own once the operator fixes the setup.
    /// </summary>
    private bool PassesRiskGate(BotOptions opts)
    {
        var assessment = RiskEngine.Validate(opts);
        var profile    = assessment.Expectancy;

        log.LogInformation(
            "[Risk] TP {Tp:P2} / SL {Sl:P2} → net {NetWin:P2} vs {NetLoss:P2}, " +
            "reward:risk {Rr:F2}:1, breakeven win rate {Be:P1} (one loss undoes {Wins:F1} wins)",
            profile.TakeProfitPct, profile.StopLossPct, profile.NetWinPct, profile.NetLossPct,
            profile.RewardRiskRatio, profile.BreakevenWinRate, profile.WinsPerLoss);

        foreach (var finding in assessment.Warnings)
            log.LogWarning("[Risk] {Code}: {Message}", finding.Code, finding.Message);

        if (!assessment.HasCritical) return true;

        foreach (var finding in assessment.Critical)
            log.LogError("[Risk] BLOCKING — {Code}: {Message}", finding.Code, finding.Message);

        log.LogError(
            "[TradingBot] Refusing to start: the configuration cannot profit as set. " +
            "Adjust take profit, stop loss or position sizing and the bot will start automatically.");

        return false;
    }

    private async Task EvalCycleAsync(BotOptions opts, CancellationToken ct)
    {
        state.TouchEval();

        // ── 1. Circuit breakers ───────────────────────────────────────────────
        // Daily loss limit, consecutive-loss streak and realised drawdown. Any
        // breach stops the bot rather than letting a losing regime compound.
        var todayPnl     = await repo.GetTodayPnlAsync(ct);
        var closedTrades = (await repo.GetRecentTradesAsync(500, ct))
            .Where(t => t.Status == "CLOSED")
            .ToList();

        var breach = RiskEngine.CheckCircuitBreakers(closedTrades, opts, todayPnl);
        if (breach is not null)
        {
            log.LogWarning("[TradingBot] Circuit breaker {Code} tripped: {Message} Stopping bot.",
                breach.Code, breach.Message);
            state.Stop();
            return;
        }

        var openTrades = state.GetOpenTrades();
        var currentPrice = await strategy.GetCurrentPriceAsync(opts.Symbol, ct);
        if (currentPrice is null) return;

        // ── 2. Update peak price for trailing stop tracking ─────────────────────
        foreach (var trade in openTrades)
        {
            var newPeak = trade.Side == "SHORT"
                ? Math.Min(currentPrice.Value, trade.PeakPrice ?? trade.EntryPrice)
                : Math.Max(currentPrice.Value, trade.PeakPrice ?? trade.EntryPrice);

            if (trade.PeakPrice != newPeak)
            {
                trade.PeakPrice = newPeak;
                await repo.UpdatePeakPriceAsync(trade.Id, newPeak, ct);
            }
        }

        // ── 3. Manage all open trades exits ────────────────────────────────────
        foreach (var trade in openTrades)
        {
            var decision = strategy.EvaluateExit(trade, currentPrice.Value, opts);
            if (decision.ShouldExit)
            {
                log.LogInformation("[TradingBot] Closing trade {Id} at ${Price} reason={Reason}", 
                    trade.Id, currentPrice, decision.Reason);
                    
                var closed = await orderEngine.CloseTradeAsync(trade, currentPrice.Value, decision.Reason!, ct);
                state.RemoveOpenTrade(trade.Id);
                state.SetLastClosedAt(DateTime.UtcNow);
                state.RecordClose(closed.PnlUsd ?? 0m);
            }
        }

        // ── 4. Check for new entry (Per Strategy) ──────────────────────────────
        foreach (var strat in opts.ActiveStrategies)
        {
            var stratTrades = openTrades.Where(t => t.Strategy == strat).ToList();
            
            if (stratTrades.Count < opts.MaxOpenTradesPerStrategy)
            {
                bool cooldownOk = true;
                var lastEntry = state.GetLastEntryAt(strat);
                if (lastEntry.HasValue)
                {
                    var elapsed = DateTime.UtcNow - lastEntry.Value;
                    if (elapsed.TotalSeconds < opts.CooldownSeconds) cooldownOk = false;
                }

                if (cooldownOk)
                {
                    var decision = await strategy.ShouldEnterAsync(strat, opts, stratTrades, currentPrice.Value, ct);
                    if (decision.Pass)
                    {
                        log.LogInformation("[TradingBot] Opening {Strat} ({Side}) position {Num}/{Max} for {Symbol} confidence={Conf:P0}{Rationale}",
                            strat, decision.Side, stratTrades.Count + 1, opts.MaxOpenTradesPerStrategy, opts.Symbol,
                            decision.Confidence, decision.Rationale != null ? $" [{decision.Rationale}]" : "");

                        var trade = await orderEngine.OpenPositionAsync(
                            opts.Symbol, strat, decision.Side, currentPrice.Value, opts.CapitalUsd, opts.PositionPctOfCapital, ct,
                            decision.Confidence, opts.UseAiSizing);

                        state.AddOpenTrade(trade);
                        state.SetLastEntryAt(strat, DateTime.UtcNow);
                    }
                }
            }
        }
    }
}
