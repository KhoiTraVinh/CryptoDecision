using CryptoDecision.BotService.Agent;
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
    TradingAgent          agent,
    AgentContext          agentContext,
    ILogger<TradingBotService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("[TradingBot] Worker Service started. Polling bot_config for commands...");

        // ── Seed in-memory stats from the DB ──
        //
        // Narrowed to the configured instrument and execution mode, because these
        // counters are what the dashboard shows as the bot's record. Unfiltered they
        // blend simulated results into a live P&L figure, which is the one number an
        // operator is most likely to act on. Reading the config first is what makes
        // the narrowing possible at all — Options is still at its defaults here.
        var seedConfig = await configRepo.GetConfigAsync(stoppingToken);
        var history    = await repo.GetRecentTradesAsync(500, stoppingToken);

        if (seedConfig is not null)
        {
            var seedMode = seedConfig.PaperMode ? "PAPER" : "LIVE";
            state.SeedStats(history
                .Where(t => string.Equals(t.Symbol, seedConfig.Symbol, StringComparison.OrdinalIgnoreCase))
                .Where(t => string.Equals(t.Mode, seedMode, StringComparison.OrdinalIgnoreCase))
                .ToList());
        }
        else
        {
            state.SeedStats(history);
        }

        // Take over anything still open before the loop can open more.
        await RecoverOpenPositionsAsync(stoppingToken);

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

                try
                {
                    await EvalCycleAsync(opts, stoppingToken);
                }
                finally
                {
                    // ── Heartbeat ─────────────────────────────────────────────
                    // Written in a finally block because it answers "is the worker
                    // alive", not "did this cycle do anything". EvalCycleAsync
                    // returns early on perfectly normal conditions — a failed price
                    // fetch, a tripped circuit breaker — and skipping the heartbeat
                    // on those made a healthy bot read as STOPPED in the dashboard
                    // after 60 seconds, which is exactly when an operator most needs
                    // to trust the status.
                    if (state.IsRunning)
                    {
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
                }
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
    /// Adopt every position the database still shows as open.
    ///
    /// Deliberately fatal on failure. A worker that cannot read its open positions
    /// but starts anyway will happily open new ones while the existing holdings sit
    /// unmanaged — no stop loss, no take profit, no timeout — and on a live account
    /// those are real coins. Crashing lets the container restart and try again,
    /// which is the safe failure; trading blind is not.
    /// </summary>
    private async Task RecoverOpenPositionsAsync(CancellationToken ct)
    {
        IReadOnlyList<BotTrade> open;
        try
        {
            open = await repo.GetOpenTradesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogCritical(ex,
                "[TradingBot] Could not read open positions on startup. Refusing to run: the bot " +
                "would open new positions while existing ones go unmanaged.");
            throw;
        }

        if (open.Count == 0)
        {
            log.LogInformation("[TradingBot] No open positions to recover.");
            return;
        }

        state.SeedOpenTrades(open);

        var live      = open.Where(t => t.IsLive).ToList();
        var unguarded = live.Count(t => string.IsNullOrEmpty(t.ExitAlgoId));

        log.LogInformation(
            "[TradingBot] Recovered {Total} open position(s): {Paper} paper, {Live} live. " +
            "Exits, stops and timeouts now apply to them again.",
            open.Count, open.Count - live.Count, live.Count);

        if (live.Count > 0)
            log.LogWarning(
                "[TradingBot] Took over {Live} LIVE position(s) worth ${Notional} on {Venues}. " +
                "{Unguarded} of them have no exchange-side stop order and are protected only by " +
                "this process.",
                live.Count, live.Sum(t => t.NotionalUsd),
                string.Join("/", live.Select(t => t.Exchange).Distinct()), unguarded);
    }

    /// <summary>
    /// Ask the venue whether it closed any position on its own — an OCO that fired
    /// while the bot was down, or between cycles — and settle those rows from the
    /// exchange's fill. Returns the trades that are genuinely still open.
    ///
    /// Runs before exits are evaluated, because evaluating a stop for a position
    /// that no longer exists ends in a sell order against a zero balance, retried
    /// every cycle forever. Paper trades short-circuit without any network call.
    /// </summary>
    private async Task<IReadOnlyList<BotTrade>> ReconcileVenueClosuresAsync(
        IReadOnlyList<BotTrade> openTrades, CancellationToken ct)
    {
        var stillOpen = new List<BotTrade>(openTrades.Count);

        foreach (var trade in openTrades)
        {
            try
            {
                var settled = await orderEngine.ReconcileAsync(trade, ct);
                if (settled is null)
                {
                    stillOpen.Add(trade);
                    continue;
                }

                state.RemoveOpenTrade(trade.Id);
                state.SetLastClosedAt(DateTime.UtcNow);
                state.RecordClose(settled.PnlUsd ?? 0m);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Cannot establish what the venue did — assume the position is still
                // ours. Treating an unreachable exchange as "position closed" would
                // drop a real holding from management.
                log.LogWarning(ex,
                    "[TradingBot] Could not reconcile trade {Id} with the exchange; " +
                    "treating it as still open.", trade.Id);
                stillOpen.Add(trade);
            }
        }

        return stillOpen;
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
        // ── Can the configured execution mode actually be honoured? ──
        //
        // Checked before the expectancy arithmetic because it is the more dangerous
        // failure. A bad TP/SL loses money slowly; a request for live trading that
        // silently falls back to simulation reports fictional P&L as real, and an
        // operator acting on those numbers has no way to tell.
        var executionRefusal = orderEngine.DescribeRefusal(opts);
        if (executionRefusal is not null)
        {
            log.LogError(
                "[TradingBot] Refusing to start: paper_mode is {PaperMode} and exchange is {Exchange}, " +
                "but {Reason} Fix the deployment or switch to paper mode; the bot will start automatically.",
                opts.PaperMode, opts.Exchange, executionRefusal);
            return false;
        }

        if (!opts.PaperMode)
            log.LogWarning(
                "[TradingBot] LIVE MODE on {Exchange}: orders placed from here commit real funds. " +
                "Capital ${Capital}, {Pct:P0} per position, up to {Max} positions per strategy.",
                opts.Exchange, opts.CapitalUsd, opts.PositionPctOfCapital, opts.MaxOpenTradesPerStrategy);

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

    /// <summary>
    /// Hand the entry decision to the LLM agent for one turn.
    ///
    /// The agent opens positions through its own risk-gated tool, so nothing here
    /// re-checks limits — that would duplicate the gate and let the two drift apart.
    /// What this does own is reconciling the agent's actions back into
    /// BotStateService, since the tools operate on AgentContext rather than on the
    /// bot's in-memory state directly.
    ///
    /// A turn that opens nothing is the expected outcome most cycles.
    /// </summary>
    private async Task RunAgentEntryAsync(BotOptions opts, decimal currentPrice, CancellationToken ct)
    {
        if (!await agent.IsAvailableAsync(ct))
        {
            log.LogWarning(
                "[TradingBot] AI agent is enabled but Ollama is unreachable. " +
                "Skipping entries this cycle; open positions are still managed normally.");
            return;
        }

        var openBefore = state.GetOpenTrades();
        var knownIds   = openBefore.Select(t => t.Id).ToHashSet();

        agentContext.BeginTurn(
            opts, currentPrice, openBefore,
            state.GetLastEntryAt(AgentContext.AgentStrategyName));

        var outcome = await agent.RunTurnAsync(ct);

        // Reconcile: register anything the agent opened, and drop anything it closed.
        foreach (var opened in agentContext.TradesOpenedThisTurn(knownIds))
        {
            state.AddOpenTrade(opened);
            state.SetLastEntryAt(AgentContext.AgentStrategyName, DateTime.UtcNow);
        }

        var stillOpen = agentContext.OpenTrades.Select(t => t.Id).ToHashSet();
        foreach (var closed in openBefore.Where(t => !stillOpen.Contains(t.Id)))
        {
            state.RemoveOpenTrade(closed.Id);
            state.SetLastClosedAt(DateTime.UtcNow);
        }

        if (outcome.OrdersRefused > 0)
            log.LogInformation(
                "[TradingBot] Risk engine refused {Count} agent order(s) this cycle.",
                outcome.OrdersRefused);
    }

    private async Task EvalCycleAsync(BotOptions opts, CancellationToken ct)
    {
        state.TouchEval();

        // ── 1. Circuit breakers ───────────────────────────────────────────────
        // Daily loss limit, consecutive-loss streak and realised drawdown. Any
        // breach stops the bot rather than letting a losing regime compound.
        // Narrowed to this instrument and this execution mode on purpose.
        //
        // Unfiltered, the breakers read whatever is in the table: a paper session's
        // simulated profits offset real losses so the daily limit never trips, a
        // paper losing streak halts a live bot that has done nothing wrong, and
        // results from a symbol the bot no longer trades decide whether it may keep
        // trading the one it does. The breakers should judge the account they are
        // protecting, and nothing else.
        var mode         = opts.PaperMode ? "PAPER" : "LIVE";
        var todayPnl     = await repo.GetTodayPnlAsync(opts.Symbol, mode, ct);
        var closedTrades = (await repo.GetRecentTradesAsync(500, ct))
            .Where(t => t.Status is "CLOSED" or "STOPPED")
            .Where(t => string.Equals(t.Symbol, opts.Symbol, StringComparison.OrdinalIgnoreCase))
            .Where(t => string.Equals(t.Mode, mode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var breach = RiskEngine.CheckCircuitBreakers(closedTrades, opts, todayPnl);
        if (breach is not null)
        {
            log.LogWarning("[TradingBot] Circuit breaker {Code} tripped: {Message} Stopping bot.",
                breach.Code, breach.Message);
            state.Stop();
            return;
        }

        // ── 2. Reconcile anything the venue closed without us ──────────────────
        // An exchange-side OCO can fire between cycles, or while the bot was down.
        // Settling those rows first keeps the exit evaluation below from working on
        // positions that no longer exist.
        var allOpen = await ReconcileVenueClosuresAsync(state.GetOpenTrades(), ct);

        // ── Only manage positions in the instrument this cycle has a price for ──
        //
        // One price is fetched per cycle, for opts.Symbol. Applying it to a position
        // in a different instrument is not an approximation, it is a different
        // number entirely: a BTCUSDT entry at 77,382 measured against a SOL price of
        // 92 reads as -99.9%, which trips a stop loss, a timeout close, or a
        // breakeven exit on arithmetic that means nothing. It happened here — a
        // paper BTC position left open when the symbol changed to SOL — and on a
        // live account the same path would close a real position at a fabricated
        // trigger, or record a fabricated loss large enough to trip the daily
        // circuit breaker and halt a working bot.
        //
        // Positions in other instruments are left untouched and reported, because
        // untouched is the safe state and silence is not: they still need an
        // operator to close them.
        var openTrades = allOpen
            .Where(t => string.Equals(t.Symbol, opts.Symbol, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var strays = allOpen.Count - openTrades.Count;
        if (strays > 0)
            log.LogWarning(
                "[TradingBot] {Strays} open position(s) are in an instrument other than {Symbol} and " +
                "are NOT being managed — no stop loss, take profit or timeout applies to them. " +
                "Close them manually, or point bot_config.symbol back at them. [{Detail}]",
                strays, opts.Symbol,
                string.Join("; ", allOpen
                    .Where(t => !string.Equals(t.Symbol, opts.Symbol, StringComparison.OrdinalIgnoreCase))
                    .Select(t => $"id={t.Id} {t.Mode} {t.Side} {t.Symbol}")));

        // Price comes from the venue orders are placed on — see PriceFeedResolver.
        var currentPrice = await strategy.GetCurrentPriceAsync(opts, ct);
        if (currentPrice is null) return;

        // ── 3. Update peak price for trailing stop tracking ─────────────────────
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

        // ── 4. Manage all open trades exits ────────────────────────────────────
        foreach (var trade in openTrades)
        {
            var decision = strategy.EvaluateExit(trade, currentPrice.Value, opts);
            if (decision.ShouldExit)
            {
                log.LogInformation("[TradingBot] Closing trade {Id} at ${Price} reason={Reason}",
                    trade.Id, currentPrice, decision.Reason);

                try
                {
                    var closed = await orderEngine.CloseTradeAsync(trade, currentPrice.Value, decision.Reason!, ct);

                    // An exit that only partially filled returns the trade still
                    // OPEN, holding what is left. Recording a close here would drop
                    // the remainder from management while it is still a real
                    // position, so the status decides, not the fact that the call
                    // returned.
                    if (closed.Status == "CLOSED" || closed.Status == "STOPPED")
                    {
                        state.RemoveOpenTrade(trade.Id);
                        state.SetLastClosedAt(DateTime.UtcNow);
                        state.RecordClose(closed.PnlUsd ?? 0m);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A live exit can fail on a rejected order or an unreachable
                    // exchange. The trade deliberately stays open in state so the
                    // next cycle tries again — dropping it here would abandon a real
                    // position with its stop loss no longer being evaluated.
                    log.LogError(ex,
                        "[TradingBot] Exit for trade {Id} failed ({Reason}). The position is still " +
                        "open and will be retried next cycle.", trade.Id, decision.Reason);
                }
            }
        }

        // ── 5. Check for new entry ─────────────────────────────────────────────
        // Exits above are always deterministic. Only the entry decision is
        // delegated, and only when the operator has explicitly enabled the agent.
        if (opts.UseAiAgent)
        {
            await RunAgentEntryAsync(opts, currentPrice.Value, ct);
            return;
        }

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

                        try
                        {
                            var trade = await orderEngine.OpenPositionAsync(
                                opts.Symbol, strat, decision.Side, currentPrice.Value, opts.CapitalUsd, opts.PositionPctOfCapital, ct,
                                decision.Confidence, opts.UseAiSizing);

                            state.AddOpenTrade(trade);
                            state.SetLastEntryAt(strat, DateTime.UtcNow);

                            // Recorded so the dashboard can show what sizing actually
                            // produced, not only what it asked for. The venue's lot
                            // grid is applied inside the order engine, so this is the
                            // only place the surviving number is known.
                            await SafeRecordAsync(
                                configRepo.RecordSizingNoteAsync(
                                    $"{trade.Quantity} {opts.Symbol} @ ${trade.EntryPrice} " +
                                    $"= ${trade.NotionalUsd:F2} ({strat} {decision.Side})", ct),
                                "sizing note");
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // A refused entry is a normal outcome live: a SHORT signal
                            // on a spot account, insufficient balance, an order below
                            // the exchange minimum. Caught per strategy so one refusal
                            // does not skip the remaining strategies, and the cooldown
                            // is deliberately not stamped — nothing was opened.
                            log.LogError(ex,
                                "[TradingBot] Entry for {Strat} ({Side}) was not placed: {Message}",
                                strat, decision.Side, ex.Message);

                            // Normal does not mean invisible. Logging alone left a bot
                            // that had refused every entry for hours looking identical
                            // to one waiting for a signal — RUNNING, healthy, silent.
                            // Persisting it puts the reason where the operator is
                            // already looking.
                            await SafeRecordAsync(
                                configRepo.RecordEntryRefusalAsync(
                                    $"{strat} {decision.Side}: {ex.Message}", ct),
                                "entry refusal");
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Await a bookkeeping write, swallowing its failure.
    ///
    /// These writes exist to make the bot's behaviour visible; none of them is worth
    /// stopping trading over. The refusal case matters most: it runs inside a catch
    /// block, so an exception escaping here would replace the real reason an entry
    /// was refused with a database error and lose the thing being reported.
    /// </summary>
    private async Task SafeRecordAsync(Task write, string what)
    {
        try
        {
            await write;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "[TradingBot] Could not persist {What}.", what);
        }
    }
}
