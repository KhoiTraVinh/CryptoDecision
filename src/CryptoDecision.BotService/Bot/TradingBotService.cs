using CryptoDecision.BotService.Agent;
using CryptoDecision.BotService.Infrastructure;
using CryptoDecision.Shared.Bot;
using CryptoDecision.Shared.Signals;

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
    IFeatureRepository    featureRepo,
    IEntryGate            gate,
    ILogger<TradingBotService> log) : BackgroundService
{
    /// <summary>
    /// Declines the gate has already given, keyed by symbol, side and the
    /// 15-minute bucket the verdict belongs to.
    ///
    /// The evaluation loop runs every 30 seconds but flow bars only change on the
    /// quarter hour, so one signal was being handed to the model up to thirty
    /// times. Measured: 55 gate calls carrying six distinct z-scores, and the
    /// same z=-2.62 candidate drew "1.73:1" and "1.74:1" a minute apart. That is
    /// not only ~9x of wasted inference — it makes entry timing a lottery, since
    /// a model that declines nine times and approves on the tenth enters at
    /// whatever moment it happened to change its mind.
    ///
    /// Only declines are cached. An approval leads straight to an order, and
    /// re-serving a stale approval from a dictionary is the one direction where
    /// being wrong costs money rather than an opportunity.
    /// </summary>
    private readonly Dictionary<(string Symbol, string Side, DateTime Bucket), GateDecision>
        _declinedThisBucket = new();

    private static DateTime BucketOf(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.FromMinutes(15).Ticks, DateTimeKind.Utc);

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
                    if (!await PassesRiskGateAsync(dbConfig, stoppingToken))
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

                // ── A cycle gets a deadline ────────────────────────────────────
                //
                // Without one, any single await that never completes stops the loop
                // forever while the process stays alive and the health endpoint keeps
                // answering. That is not hypothetical: trading halted for 64 minutes
                // on 2026-08-22 with the container reporting healthy the whole time and
                // the cause never established, and it happened again on 2026-08-23 —
                // the loop completed a cycle, wrote its heartbeat, and never started
                // another, with every managed thread parked in futex_wait and the
                // database showing no query in flight.
                //
                // Guessing which await hangs has now failed twice. A deadline does not
                // need to know: it cancels whatever is stuck, says how long it had run,
                // and lets the next cycle try again. The two candidates found so far —
                // a misconfigured GC and an exchange call — are both fixed elsewhere,
                // and this is what makes the third one survivable.
                //
                // Half the liveness window, so the loop recovers on its own before the
                // health check would call it dead. Both derive from the same function,
                // so they cannot drift into disagreeing.
                var cycleBudget = BotLiveness.StaleAfter(opts.EvalIntervalSeconds) / 2;

                using var cycleCts =
                    CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cycleCts.CancelAfter(cycleBudget);

                var cycleStarted = DateTime.UtcNow;

                try
                {
                    await EvalCycleAsync(opts, cycleCts.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // The cycle blew its deadline rather than the worker being shut
                    // down. Error, not Warning: a cycle that cannot finish inside three
                    // times its own interval means something is wrong, and open
                    // positions went un-evaluated for that whole stretch.
                    log.LogError(
                        "[TradingBot] Evaluation cycle exceeded its {Budget} budget and was " +
                        "cancelled after {Elapsed:F0}s. Open positions were not evaluated this " +
                        "cycle; the exchange-side stops are still in force. The loop continues.",
                        cycleBudget, (DateTime.UtcNow - cycleStarted).TotalSeconds);
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
    private async Task<bool> PassesRiskGateAsync(BotOptions opts, CancellationToken ct)
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

        // Today's realised range, so the gate can judge the trailing stop against the
        // market rather than only against fees. Best-effort: a missing feature row
        // must not stop the bot, it just costs one warning.
        decimal? volatility = null;
        try
        {
            volatility = (await featureRepo.GetTodayAsync(opts.Symbol, ct))?.Volatility;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("[Risk] Could not read today's volatility: {Err}", ex.Message);
        }

        var assessment = RiskEngine.Validate(opts, realizedVolatilityPct: volatility);
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
    /// Ask the gate whether a proposed entry is taken.
    ///
    /// Returns an approval without consulting the gate in exactly two cases, both of
    /// which the operator has to have chosen explicitly:
    ///
    ///   • Gating is switched off (<c>require_ai_gate = false</c>), recorded as
    ///     NOT_GATED so the row says the trade was never reviewed.
    ///   • The gate is unreachable and <c>allow_entry_without_gate = true</c>,
    ///     recorded as APPROVED_DEGRADED.
    ///
    /// The default for both settings is the safe one: gating on, no fallback. An
    /// unreachable gate then stops entries rather than silently reverting to ungated
    /// trading, because a deployment where the gate has been dead for a week and
    /// nothing looks different is the failure this whole arrangement is meant to
    /// avoid.
    /// </summary>
    private async Task<GateDecision> ReviewEntryAsync(
        BotOptions opts, string strategyName, EntryDecision decision,
        decimal price, int openPositions, CancellationToken ct)
    {
        if (!opts.RequireAiGate) return GateDecision.Ungated();

        // A candidate with no evidence to show cannot be meaningfully reviewed, and
        // handing the model an empty brief invites it to approve on nothing. Only the
        // flow strategy produces a FlowVerdict; anything else is treated as ungated
        // rather than pretend-reviewed, and the row says so.
        if (decision.Flow is not { } flow || decision.Geometry is not { } geometry)
        {
            log.LogDebug(
                "[Gate] {Strategy} produced no reviewable evidence; recording the entry as NOT_GATED.",
                strategyName);
            return GateDecision.Ungated();
        }

        decimal todayPnl;
        try
        {
            todayPnl = await repo.GetTodayPnlAsync(
                opts.Symbol, opts.PaperMode ? "PAPER" : "LIVE", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The day's P&L is context for the gate's judgement, not a precondition
            // for it. Zero is the neutral value and the omission is logged, rather
            // than refusing an otherwise good entry over a failed read.
            log.LogWarning("[Gate] Could not read today's P&L: {Err}. Reviewing without it.",
                ex.Message);
            todayPnl = 0m;
        }

        var sizing = PositionSizer.Resolve(
            opts.CapitalUsd, opts.PositionPctOfCapital,
            currentVolatilityPct: geometry.AtrPctUsed,
            confidence: decision.Confidence,
            useAiSizing: opts.UseAiSizing);

        var candidate = new EntryCandidate(
            Symbol:        opts.Symbol,
            Side:          decision.Side,
            Price:         price,
            Flow:          flow,
            Geometry:      geometry,
            NotionalUsd:   sizing.NotionalUsd,
            OpenPositions: openPositions,
            TodayPnlUsd:   todayPnl);

        // One verdict per side per 15-minute bucket. The evidence cannot change
        // until the next bar closes, so asking again inside the same bucket is
        // asking an identical question and accepting a different answer.
        (string Symbol, string Side, DateTime Bucket) key =
            (opts.Symbol, decision.Side, BucketOf(DateTime.UtcNow));

        if (_declinedThisBucket.TryGetValue(key, out var cached))
        {
            log.LogDebug(
                "[Gate] Already declined {Side} {Symbol} for the {Bucket:HH:mm} bucket: {Reason}",
                decision.Side, opts.Symbol, key.Bucket, cached.Reason);
            return cached;
        }

        var verdict = await gate.ReviewAsync(candidate, ct);

        if (!verdict.Approved)
        {
            // Bounded by dropping everything older than the current bucket. The
            // loop runs for weeks, so an unpruned dictionary keyed on a timestamp
            // is a slow leak rather than a cache.
            foreach (var stale in _declinedThisBucket.Keys.Where(k => k.Bucket < key.Bucket).ToList())
                _declinedThisBucket.Remove(stale);

            _declinedThisBucket[key] = verdict;
        }

        if (verdict.Approved || !opts.AllowEntryWithoutGate) return verdict;

        // Refused for want of a reachable gate, and the operator has allowed trading
        // to continue without one. Distinguished from a refusal on the merits: the
        // gate having an opinion and the gate being absent are different facts, and
        // only the second one may be overridden.
        if (verdict.Reason.StartsWith("Gate unreachable", StringComparison.Ordinal)
            || verdict.Reason.StartsWith("Gate call failed", StringComparison.Ordinal))
        {
            log.LogWarning(
                "[Gate] {Reason} allow_entry_without_gate is set, so this entry proceeds " +
                "unreviewed and is recorded as APPROVED_DEGRADED.", verdict.Reason);

            return GateDecision.Degraded(verdict.Reason);
        }

        return verdict;
    }

    private async Task EvalCycleAsync(BotOptions opts, CancellationToken ct)
    {
        var evalGap = state.TouchEval();

        // ── Is the wall clock believable this cycle? ───────────────────────────
        //
        // The loop sleeps EvalIntervalSeconds between cycles, so the gap between two
        // TouchEval stamps should be that interval plus however long a cycle took.
        // A gap far outside that means UtcNow moved for a reason other than time
        // passing — the WSL2 clock resyncing after the host suspended is the known
        // one on this host — and the timeout exit is the only decision that would act
        // on the bogus value. Four intervals with a 180s floor matches the liveness
        // window, so a normal slow cycle is not mistaken for a jump.
        //
        // A restart legitimately produces a large first gap, which is why the first
        // cycle after start returns null and is treated as trusted: there is no
        // previous stamp to compare against, and the recovery path has just re-read
        // every open position from the database.
        var window = BotLiveness.StaleAfter(opts.EvalIntervalSeconds);
        var clockTrusted = evalGap is null
                        || (evalGap.Value >= TimeSpan.Zero && evalGap.Value <= window);

        if (!clockTrusted)
            log.LogError(
                "[TradingBot] The evaluation clock jumped {Gap} between cycles against a {Window} " +
                "expectation. Clock-based exits are suspended for this cycle; price-based exits and " +
                "the exchange-side stops are unaffected.",
                evalGap, window);

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

        // Fetched once and filtered twice: the circuit breakers want closed trades on
        // this account, and the daily entry cap wants everything opened today on it.
        // Two queries for one list of 500 rows would just be two queries.
        var accountTrades = (await repo.GetRecentTradesAsync(500, ct))
            .Where(t => string.Equals(t.Symbol, opts.Symbol, StringComparison.OrdinalIgnoreCase))
            .Where(t => string.Equals(t.Mode, mode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var closedTrades = accountTrades
            .Where(t => t.Status is "CLOSED" or "STOPPED")
            .ToList();

        // Entries opened so far today, counted by UTC day to match how the daily loss
        // limit and the feature tables are bucketed.
        var entriesToday = accountTrades
            .Count(t => t.OpenedAt.Date == DateTime.UtcNow.Date);

        var breach = RiskEngine.CheckCircuitBreakers(closedTrades, opts, todayPnl);
        if (breach is not null)
        {
            // Persisted to bot_config, not only to memory.
            //
            // state.Stop() alone did not stop anything. It clears the in-process flag,
            // and the very next loop iteration sees bot_config.enabled still TRUE with
            // IsRunning now false — which is exactly the "operator pressed start"
            // condition — so it called Start() again and carried on trading. The breaker
            // halted the bot for one cycle and then logged "Stopping bot" every minute
            // forever while doing nothing. The daily loss limit, the consecutive-loss
            // streak and the max-drawdown breaker were all inoperative, and all three
            // logged as though they had worked.
            //
            // A breaker a restart clears is not a breaker either, which is why this
            // writes to the database rather than holding a flag: a loss limit has to
            // survive a container restart and require a person to look before trading
            // resumes.
            log.LogError(
                "[TradingBot] Circuit breaker {Code} tripped: {Message} Disabling the bot in " +
                "bot_config — it will NOT restart on its own. Review the trades, then re-arm " +
                "with: UPDATE bot_config SET enabled = true WHERE id = 1;",
                breach.Code, breach.Message);

            try
            {
                await configRepo.StopBotAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The write is what makes the halt stick. If it fails, say so loudly —
                // the in-memory stop below will be undone by the next poll.
                log.LogCritical(ex,
                    "[TradingBot] Circuit breaker {Code} tripped but bot_config could not be " +
                    "updated. The next poll will restart trading. Set enabled = false by hand.",
                    breach.Code);
            }

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
            var decision = strategy.EvaluateExit(trade, currentPrice.Value, opts, clockTrusted);
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
        //
        // Exits above are always deterministic, and now so is the entry decision.
        // The alternative branch here handed the whole decision to a tool-calling
        // agent when bot_config.use_ai_agent was set; that flag was never true in
        // production and the agent is gone. What survives is narrower on purpose:
        // XVENUE_FLOW proposes, and the gate may only refuse.

        // ── Daily entry cap ───────────────────────────────────────────────────
        //
        // Checked once, outside the strategy loop, because the cap is on the account's
        // trading for the day rather than on any one strategy's. Not a circuit
        // breaker: hitting it is a normal end to a busy day and should stop entries,
        // not stop the bot — open positions still need their exits evaluated.
        if (opts.MaxEntriesPerDay > 0 && entriesToday >= opts.MaxEntriesPerDay)
        {
            log.LogInformation(
                "[TradingBot] {Count} entries already opened today, at the {Max} limit. No further " +
                "entries until 00:00 UTC; open positions are still managed.",
                entriesToday, opts.MaxEntriesPerDay);

            await SafeRecordAsync(
                configRepo.RecordEntryRefusalAsync(
                    $"daily entry cap reached ({entriesToday}/{opts.MaxEntriesPerDay})", ct),
                "entry cap refusal");

            return;
        }

        foreach (var strat in opts.ActiveStrategies)
        {
            // Counted from live state at the moment of the decision, not from the
            // `openTrades` snapshot taken at the top of the cycle.
            //
            // The snapshot is stale by the time execution reaches here: the exit loop
            // above may have closed several of those positions, and each strategy's
            // own entry below adds one. Reading the snapshot made the concurrency
            // limit answer a question about the past — it counted positions that had
            // already been closed this cycle, and on the other side it could not see
            // a position opened by an earlier strategy in this same loop. Both
            // directions are wrong, and the second one lets the limit be exceeded.
            var stratTrades = state.GetOpenTrades()
                .Where(t => string.Equals(t.Symbol, opts.Symbol, StringComparison.OrdinalIgnoreCase))
                .Where(t => string.Equals(t.Strategy, strat, StringComparison.OrdinalIgnoreCase))
                .ToList();

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

                    // Persisted every cycle, pass or not. The strategy's own log is
                    // throttled to a code change and then every 120th repeat, which is
                    // right for a log and wrong for an operator: SOL fell 2.7% over
                    // three hours and the newest line was 33 minutes stale, so the
                    // actual state had to be rebuilt by hand from flow_bars_15m. A
                    // failed write must not stop an entry, hence SafeRecordAsync.
                    await SafeRecordAsync(
                        configRepo.RecordVerdictAsync(
                            decision.Flow?.AbstainCode is { Length: > 0 } code
                                ? code
                                : decision.Pass ? "ACTIONABLE" : "NO_FLOW_VERDICT",
                            decision.Rationale ?? "(no rationale)",
                            decision.Flow?.AggregateZ ?? 0.0,
                            decision.Flow?.AgreeingVenues ?? 0,
                            decision.Flow?.ParticipatingVenues ?? 0,
                            ct),
                        "verdict");

                    if (decision.Pass)
                    {
                        // ── The gate has the only veto on entry ────────────────
                        //
                        // Everything about the trade is already fixed: direction from
                        // cross-venue flow consensus, stop and target from measured
                        // volatility, size from the position sizer. The gate is asked
                        // one question about a finished proposal, and it can only ever
                        // answer no.
                        //
                        // That asymmetry is the safety property. Every failure mode —
                        // Ollama down, timed out, unparseable, ambiguous — resolves to
                        // "no entry", which costs an opportunity and never a position.
                        // The arrangement this replaces blended the model's output into
                        // a composite score, where a wrong answer moved real money in
                        // the wrong direction.
                        var gate = await ReviewEntryAsync(
                            opts, strat, decision, currentPrice.Value, stratTrades.Count, ct);

                        if (!gate.Approved)
                        {
                            log.LogInformation(
                                "[TradingBot] {Strat} {Side} was proposed and the gate declined it: {Reason}",
                                strat, decision.Side, gate.Reason);

                            // Persisted, because a declined entry is the outcome an
                            // operator is least able to see. A bot that refused every
                            // entry for hours looked identical to one waiting for a
                            // signal, and the only trace was in a container log that
                            // did not survive the container.
                            await SafeRecordAsync(
                                configRepo.RecordEntryRefusalAsync(
                                    $"gate declined {strat} {decision.Side}: {gate.Reason}", ct),
                                "gate refusal");

                            // No cooldown stamp: nothing was opened, so nothing should
                            // pace the next attempt.
                            continue;
                        }

                        log.LogInformation("[TradingBot] Opening {Strat} ({Side}) position {Num}/{Max} for {Symbol} confidence={Conf:P0} gate={Gate}{Rationale}",
                            strat, decision.Side, stratTrades.Count + 1, opts.MaxOpenTradesPerStrategy, opts.Symbol,
                            decision.Confidence, gate.Verdict,
                            decision.Rationale != null ? $" [{decision.Rationale}]" : "");

                        try
                        {
                            var trade = await orderEngine.OpenPositionAsync(
                                opts.Symbol, strat, decision.Side, currentPrice.Value, opts.CapitalUsd, opts.PositionPctOfCapital, ct,
                                decision.Confidence, opts.UseAiSizing,
                                // Passed into the engine, not attached afterwards: the
                                // exchange-side OCO is armed inside OpenPositionAsync
                                // and it is the stop that survives this process dying.
                                decision.Geometry);

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

                            // Why this trade exists, stored next to the trade. Exit
                            // reasons were always recorded and entry reasons never
                            // were, which made a losing run describable but not
                            // explainable — the score lived in a container log that
                            // does not survive the container.
                            await SafeRecordAsync(
                                repo.RecordEntryEvidenceAsync(
                                    trade.Id, decision.Composite, decision.Confidence,
                                    decision.Rationale, ct),
                                "entry evidence");

                            // ── Exit levels, onto the row ──────────────────────
                            //
                            // Not wrapped in SafeRecordAsync, unlike the two writes
                            // above. Those exist to make behaviour visible and losing
                            // them costs an explanation; this one is what the exit
                            // evaluation reads, and losing it means the position falls
                            // back to the configured percentages — a different, usually
                            // tighter stop than the entry was sized against.
                            //
                            // So it is recorded, and a failure is escalated rather than
                            // swallowed. The position is already open either way, which
                            // is why this cannot throw past here: the trade must stay in
                            // state and keep being managed. What it must not do is stay
                            // quiet.
                            if (decision.Geometry is { } signalGeometry)
                            {
                                // Re-anchored onto the price that actually filled, not
                                // the one the signal was scored at. With a resting
                                // maker entry those differ by minutes and a few basis
                                // points, and against a stop that may only be 40 bps
                                // wide a 5 bps drift moves the risk on the trade by
                                // over a tenth. The distances are the decision; the
                                // prices are that decision applied to the real fill.
                                var geometry = signalGeometry.RebaseTo(trade.EntryPrice, decision.Side);

                                try
                                {
                                    await repo.RecordEntryGeometryAsync(
                                        trade.Id, geometry.StopPrice, geometry.TargetPrice,
                                        (decimal)geometry.AtrPctUsed, gate.Verdict, gate.Reason, ct);

                                    trade.StopPrice   = geometry.StopPrice;
                                    trade.TargetPrice = geometry.TargetPrice;
                                    trade.GateVerdict = gate.Verdict;
                                    trade.GateReason  = gate.Reason;
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    log.LogCritical(ex,
                                        "[TradingBot] Trade {Id} is OPEN but its stop ({Stop}) and target " +
                                        "({Target}) could not be persisted. The bot will manage it with " +
                                        "the configured {Sl:P2}/{Tp:P2} instead, which is not what this " +
                                        "entry was sized against. Set the levels by hand or close it.",
                                        trade.Id, geometry.StopPrice, geometry.TargetPrice,
                                        opts.StopLossPct, opts.TakeProfitPct);
                                }
                            }
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
