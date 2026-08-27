using CryptoDecision.BotService.Bot;
using CryptoDecision.BotService.Infrastructure;
using CryptoDecision.Shared.Bot;
using CryptoDecision.Shared.Signals;

namespace CryptoDecision.BotService.Strategies;

/// <summary>
/// Entry on cross-venue agreement in aggressive order flow; exit on
/// volatility-scaled levels fixed at entry.
///
/// What this replaces, and why
/// ---------------------------
/// MOMENTUM blended five components of different units and horizons into one 0-100
/// score and compared it to 62 or 38. Four separate problems compounded there:
///
///   • The score had no baseline. A buy ratio of 0.62 meant the same thing in a dead
///     market and a panic, so the threshold could not be right in both.
///   • Its 5m, 15m and 1h windows were cumulative, so weighting them as three
///     confirmations weighted one number three times. "Multi-timeframe agreement"
///     was arithmetic, not evidence.
///   • Two of its five components were structurally dead — the whale term needed a
///     single print above 100k USDT, which on SOL never happens, and the AI term
///     needed a prediction service that is routinely switched off.
///   • Because it was a weighted average, no single condition was ever required. A
///     strong reading on two components carried an entry past components that were
///     absent, stale, or pointing the other way.
///
/// Here the conditions are conjunctive and each can veto: the imbalance has to be
/// unusual against its own history, it has to be unusual on at least N venues
/// independently, those venues have to have actually been printing, no single order
/// may be the imbalance, and the venues have to agree on price. Any one failing means
/// no entry, with a named reason.
///
/// Where the real cross-venue agreement is
/// ---------------------------------------
/// The old ensemble called itself a consensus while running three models over one
/// identical four-number daily feature row — one opinion counted three times, with an
/// agreement bonus rewarding the duplication. Binance, Bybit and OKX have different
/// participants and their books can genuinely disagree, so requiring them to lean the
/// same way is corroboration from independent data rather than from independent
/// arithmetic over the same data.
///
/// Horizon
/// -------
/// The signal is measured on the quarter-hour grid and the position is held for
/// hours, because that is the horizon over which this kind of imbalance has any
/// documented predictive content — the same imbalance says nothing about the next
/// several minutes. Holding for minutes and re-evaluating every 30 seconds paid the
/// round-trip cost dozens of times a day against an hours-long signal, which is a
/// cost problem no entry threshold can fix.
/// </summary>
public sealed class CrossVenueFlowStrategy(
    IFlowBarRepository              flowRepo,
    FlowStrategyOptions             tuning,
    ILogger<CrossVenueFlowStrategy> log) : ITradingStrategy
{
    public const string StrategyName = "XVENUE_FLOW";

    public string Name => StrategyName;

    public async Task<EntryDecision> EvaluateEntryAsync(StrategyContext ctx, CancellationToken ct)
    {
        var opts = ctx.Options;

        try
        {
            var needed = tuning.Signal.MinimumBars + tuning.Signal.SignalBars;
            var set    = await flowRepo.GetRecentAsync(opts.Symbol, needed, ct);

            if (set.VenueCount == 0)
                return Refuse("NO_FLOW_BARS",
                    "flow_bars_15m has no closed buckets for this symbol. The aggregation " +
                    "worker has not run, or it has nothing to aggregate.");

            // ── Are the bars current? ─────────────────────────────────────────
            //
            // Checked explicitly rather than trusted, because a frozen table is the
            // failure this codebase keeps producing: the previous entry path read "the
            // latest prediction" with no upper bound on its age, so a row written days
            // earlier still decided entries while every health check stayed green. A
            // signal has to be able to say "I do not know yet".
            var age = set.Age(DateTime.UtcNow);
            if (age > tuning.MaxBarAge)
                return Refuse("FLOW_BARS_STALE",
                    $"The newest closed bucket is {age.TotalMinutes:F0} min old, past the " +
                    $"{tuning.MaxBarAge.TotalMinutes:F0} min limit. Ingestion or aggregation has " +
                    "stopped; trading on this would be trading on the past.");

            // ── The signal ────────────────────────────────────────────────────
            var verdict = CrossVenueFlowScorer.Score(set.ByVenue, tuning.Signal);

            if (!verdict.Actionable)
            {
                LogAbstention(opts.Symbol, verdict.AbstainCode, verdict.Reason);

                // The verdict travels with the refusal, not just its text. The caller
                // persists the aggregate z and the venue tally so "how close was it"
                // stays a number rather than something to parse back out of a
                // sentence — and the log line it would have been parsed from is
                // throttled to once an hour when the code has not changed.
                return new EntryDecision(
                    false,
                    Rationale: $"{verdict.AbstainCode}: {verdict.Reason}",
                    Flow:      verdict);
            }

            // ── Exit geometry, from measured volatility ────────────────────────
            var candles = await flowRepo.GetRecentCandlesAsync(
                opts.Symbol, tuning.AtrLookbackMinutes, ct);

            var volatility = Volatility.Measure(candles, tuning.AtrBarMinutes);

            if (!volatility.IsUsable)
                return Refuse("NO_VOLATILITY_READ",
                    $"Only {candles.Count} candle(s) available, so the stop cannot be scaled to " +
                    "the market. Refusing rather than falling back to a fixed percentage — a stop " +
                    "of the wrong width is what this strategy exists to stop doing.");

            var geometry = VolatilityStops.Resolve(
                entryPrice:         ctx.CurrentPrice,
                side:               verdict.Side!,
                volatility:         volatility,
                roundTripFeeRate:   tuning.RoundTripFeeRate,
                stopAtrMultiple:    tuning.StopAtrMultiple,
                targetRiskMultiple: tuning.TargetRiskMultiple,
                maxStopPct:         tuning.MaxStopPct);

            // A trade whose reward does not cover its risk after fees is refused here
            // rather than left for the gate. The gate is a judgement call on a
            // proposal; this is arithmetic, and arithmetic should not be delegated to
            // a language model.
            if (geometry.RewardRisk < tuning.MinRewardRisk)
                return Refuse("REWARD_RISK_TOO_LOW",
                    $"Stop {geometry.StopPct:P2} against target {geometry.TargetPct:P2} is " +
                    $"{geometry.RewardRisk:F2}:1 after fees, under the {tuning.MinRewardRisk:F2}:1 " +
                    $"minimum (ATR {volatility.AtrPct:F2}%).");

            // Confidence drives position sizing when AI sizing is on. Derived from how
            // unusual the reading is, capped so an extreme z cannot size past the
            // limit — an outlier is a reason for a normal position, not a bigger one.
            var confidence = (decimal)Math.Clamp(
                Math.Abs(verdict.AggregateZ) / (tuning.Signal.EnterZ * 2.0), 0.0, 1.0);

            log.LogInformation(
                "[XFlow] {Symbol} {Side} — z={Z:F2}, {Agree}/{Part} venues, dispersion {Disp:F1}bps, " +
                "stop {Stop:P2} target {Target:P2} ({Rr:F2}:1, ATR {Atr:F2}%)",
                opts.Symbol, verdict.Side, verdict.AggregateZ, verdict.AgreeingVenues,
                verdict.ParticipatingVenues, verdict.DispersionBps,
                geometry.StopPct, geometry.TargetPct, geometry.RewardRisk, volatility.AtrPct);

            return new EntryDecision(
                Pass:       true,
                Side:       verdict.Side!,
                Confidence: confidence,
                Rationale:  verdict.Reason,
                // The aggregate z, not a 0-100 composite. Stored so the question that
                // could not be answered after the last losing run — "were these entries
                // taken close to the threshold?" — is a SQL query.
                Composite:  (decimal)Math.Round(verdict.AggregateZ, 4),
                Geometry:   geometry,
                Flow:       verdict);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Refuse, never guess. An exception here means the evidence could not be
            // assembled, and an entry taken without it is an entry taken for no reason.
            log.LogError(ex, "[XFlow] Entry evaluation failed for {Symbol}; refusing.", opts.Symbol);
            return new EntryDecision(
                false,
                Rationale: $"EVALUATION_FAILED: {ex.Message}",
                // Same reason as Refuse(): the code belongs in a column, not only in
                // a sentence. This is the one an operator most needs to be able to
                // query for, because it means the evidence could not be built.
                Flow:      FlowVerdict.Abstain("EVALUATION_FAILED", ex.Message));
        }
    }

    private EntryDecision Refuse(string code, string message)
    {
        LogAbstention(null, code, message);

        // Carries a FlowVerdict even though the scorer was never reached. Without
        // it these refusals — FLOW_BARS_STALE, NO_CANDLES, the ones that mean the
        // evidence could not be assembled at all — reached the caller with a null
        // Flow, so the persisted code came out as a placeholder and the real one
        // survived only inside the rationale sentence. FLOW_BARS_STALE is exactly
        // a code worth querying on: it means ingestion or aggregation has stopped.
        return new EntryDecision(
            false,
            Rationale: $"{code}: {message}",
            Flow:      FlowVerdict.Abstain(code, message));
    }

    // The abstain code as of the previous evaluation, and how many cycles it has held.
    private string _lastAbstainCode = "";
    private int    _repeatCount;

    /// <summary>
    /// Report an abstention at Information when the reason <em>changes</em>, and at
    /// Debug while it repeats.
    ///
    /// Both extremes have been wrong here. Logging every abstention at Information
    /// buries the entries that actually happen under thousands of identical lines —
    /// the loop evaluates every 30 seconds and most buckets produce no signal by
    /// design. Logging them all at Debug is worse, and was the first version of this:
    /// with the default level at Information, a bot that had been refusing every entry
    /// for a day said nothing at all, which is indistinguishable from one waiting for
    /// a signal. That specific silence has cost this project real money more than once.
    ///
    /// A transition is the event. "Stopped signalling because the bars went stale" is
    /// news; the four hundredth repetition of it is not, but the count is — so the
    /// count comes out with the next transition.
    /// </summary>
    /// <summary>
    /// Cycles between the periodic restatements of an unchanged abstention.
    ///
    /// 120 cycles is an hour at the default 30-second interval. Without it the
    /// transition-only rule went too far the other way: waiting for 100 buckets to
    /// accumulate is an eighteen-hour condition that does not change, so the bot would
    /// have logged one line and then said nothing for the rest of the day. That is the
    /// silence that has cost this project money before — a bot with a good reason to do
    /// nothing and a bot that has quietly broken look identical from outside.
    /// </summary>
    private const int RestateEveryCycles = 120;

    private void LogAbstention(string? symbol, string code, string reason)
    {
        if (code == _lastAbstainCode)
        {
            _repeatCount++;

            if (_repeatCount % RestateEveryCycles == 0)
                log.LogInformation(
                    "[XFlow] still {Code} after {Count} cycles: {Reason}",
                    code, _repeatCount, reason);
            else
                log.LogDebug("[XFlow] still {Code} ({Count} cycles): {Reason}",
                    code, _repeatCount, reason);

            return;
        }

        if (_lastAbstainCode.Length > 0)
            log.LogInformation(
                "[XFlow] {Previous} held for {Count} cycle(s); now {Code}{Symbol}: {Reason}",
                _lastAbstainCode, _repeatCount + 1, code,
                symbol is null ? "" : $" on {symbol}", reason);
        else
            log.LogInformation("[XFlow] no entry — {Code}: {Reason}", code, reason);

        _lastAbstainCode = code;
        _repeatCount     = 0;
    }

    /// <summary>
    /// Exit on the levels fixed when the position was opened.
    ///
    /// Reads the trade's own stop and target rather than recomputing them from
    /// configuration. Two failures are being avoided:
    ///
    ///   • Recomputing from a fresh volatility reading moves the stop under the
    ///     position it is protecting, since the ATR has moved since entry.
    ///   • Reading percentages live from bot_config made a config edit retroactive —
    ///     widening stop_loss_pct silently moved the stop on positions already open.
    ///
    /// There is no trailing stop and no breakeven stop here, deliberately. Both were
    /// on before, and between them they truncated nearly every winner: the breakeven
    /// stop closed any trade that reached +0.8% and came back to entry, which after
    /// fees is a small loss, and the 1.20% trailing stop sat inside a 15.76% daily
    /// range and was hit by ordinary movement rather than by the trade being wrong.
    /// Four consecutive live entries were stopped out having moved at most +0.29% in
    /// their favour. A stop that wide relative to the target is a coin flip with a fee
    /// attached.
    /// </summary>
    public ExitDecision EvaluateExit(BotTrade trade, decimal currentPrice, BotOptions opts)
    {
        var rawChange = (currentPrice - trade.EntryPrice) / trade.EntryPrice;
        var changePct = trade.Side == "SHORT" ? -rawChange : rawChange;

        // Positions opened before volatility-scaled exits, or whose geometry write
        // failed, fall back to the configured percentages. Said out loud because the
        // fallback is a different — and usually tighter — stop than the entry was
        // sized against, and silently applying it is how a position ends up protected
        // by a level nobody chose for it.
        if (!trade.HasGeometry)
        {
            log.LogWarning(
                "[XFlow] Trade {Id} has no stored stop or target; falling back to the configured " +
                "{Sl:P2}/{Tp:P2}. That is not the geometry this entry was sized against.",
                trade.Id, opts.StopLossPct, opts.TakeProfitPct);

            if (changePct >= opts.TakeProfitPct) return Exit("TP", currentPrice, changePct);
            if (changePct <= -opts.StopLossPct)  return Exit("SL", currentPrice, changePct);
            return new ExitDecision(false, null, currentPrice, changePct);
        }

        var isLong = trade.Side != "SHORT";

        // Stop before target, matching how the backtester resolves a bar containing
        // both. Same ordering in both places or the live results cannot be compared
        // with the simulated ones they were validated on.
        var hitStop = isLong
            ? currentPrice <= trade.StopPrice!.Value
            : currentPrice >= trade.StopPrice!.Value;

        if (hitStop) return Exit("SL", currentPrice, changePct);

        var hitTarget = isLong
            ? currentPrice >= trade.TargetPrice!.Value
            : currentPrice <= trade.TargetPrice!.Value;

        if (hitTarget) return Exit("TP", currentPrice, changePct);

        return new ExitDecision(false, null, currentPrice, changePct);
    }

    private static ExitDecision Exit(string reason, decimal price, decimal changePct) =>
        new(true, reason, price, changePct);
}

/// <summary>
/// Everything <see cref="CrossVenueFlowStrategy"/> can be tuned by, in one object.
///
/// Separate from BotOptions because these are the parameters the backtester sweeps,
/// and they need to be settable together as a unit that was validated together.
/// Picking a threshold from one sweep and a stop multiple from another produces a
/// configuration that was never tested.
/// </summary>
public sealed class FlowStrategyOptions
{
    public const string Section = "FlowStrategy";

    public FlowSignalOptions Signal { get; set; } = new();

    /// <summary>
    /// How stale the newest closed bucket may be. Two bucket widths plus slack: the
    /// aggregation worker runs every two minutes, so anything past this means
    /// ingestion or aggregation has stopped rather than merely lagged.
    /// </summary>
    public TimeSpan MaxBarAge { get; set; } = TimeSpan.FromMinutes(35);

    /// <summary>Minutes of 1-minute candles to load. 1440 = 24 hours.</summary>
    public int AtrLookbackMinutes { get; set; } = FlowGeometryDefaults.AtrLookbackMinutes;

    /// <summary>
    /// Bar size the true range is measured on, in minutes.
    ///
    /// Has to be comparable to the holding period, not to the polling interval.
    /// Measured on real SOL data, per-minute ATR is 0.30% while 15-minute median true
    /// range is 1.07% — so measuring on 1-minute bars would place a 0.45% stop on a
    /// position held for hours inside a window that ranged 15.9%. 15 matches the
    /// signal grid; the backtester sweeps it once there is enough history to.
    /// </summary>
    public int AtrBarMinutes { get; set; } = FlowGeometryDefaults.AtrBarMinutes;

    public double StopAtrMultiple    { get; set; } = FlowGeometryDefaults.StopAtrMultiple;
    public double TargetRiskMultiple { get; set; } = FlowGeometryDefaults.TargetRiskMultiple;

    /// <summary>
    /// Round-trip cost assumption used when placing the stop and target, as a
    /// fraction of notional.
    ///
    /// 10 bps is taker in and taker out on OKX perpetual swaps, which is what the
    /// order engine actually does today. Note this is not what RiskEngine assumes —
    /// its 20 bps default is documented as "Binance spot without BNB discount", a
    /// venue and product this bot no longer trades. Neither figure includes slippage.
    /// </summary>
    public decimal RoundTripFeeRate { get; set; } = 0.001m;

    /// <summary>
    /// Refuse an entry whose reward:risk after fees is below this. Arithmetic, checked
    /// before the gate is asked anything.
    /// </summary>
    public decimal MinRewardRisk { get; set; } = 1.2m;

    /// <summary>
    /// Optional hard ceiling on the stop distance. Null by default, and that is the
    /// recommended setting: capping the stop reintroduces the failure this strategy
    /// exists to remove, a stop narrower than the market's own movement. When risk per
    /// trade has to come down, reduce the position size instead.
    /// </summary>
    public decimal? MaxStopPct { get; set; } = null;
}
