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
    /// <summary>The name this class registered under when only one instance existed.</summary>
    public const string StrategyName = "XVENUE_FLOW";

    /// <summary>
    /// This instance's name, from its own options block rather than a constant.
    ///
    /// It was <c>const string StrategyName</c> returned directly, which made the class a
    /// singleton by construction: StrategyEvaluator builds its lookup with
    /// <c>ToDictionary(s =&gt; s.Name)</c>, so registering a second instance threw
    /// ArgumentException at startup — loudly, at least, rather than quietly shadowing one
    /// rule with another.
    ///
    /// The name now travels with the options, so one configuration section is one
    /// strategy: its own name, its own entry mode, its own thresholds, its own slot in
    /// bot_config.active_strategies, and its own rows in bot_trades.strategy. Two
    /// instances of this class cannot silently share a position limit or a cooldown,
    /// because every per-strategy limit in TradingBotService keys off exactly this
    /// string.
    /// </summary>
    public string Name => tuning.Name;

    /// <inheritdoc />
    public string DescribeRule() => tuning.Signal.EntryMode switch
    {
        // The ratio path is switched off by raising its threshold out of reach rather than
        // by a flag, so the banner has to say so out loud. A reader seeing "99.00x" can
        // work it out; a reader seeing "the ratio path is DISABLED" cannot miss it, and
        // the difference matters because a threshold nobody can reach and a threshold
        // nobody meant to set look identical in a config file.
        //
        // 10.0 as the cut-off is not a tuning knob: the highest ratio ever observed on a
        // qualifying bucket in 28 days of production is 2.53 on average and the sample
        // maximum is far under 10, so anything at or above it is unreachable by
        // construction rather than merely strict.
        FlowEntryMode.FlowRatio when tuning.Signal.RatioMinimum >= 10m =>
            $"RATIO PATH DISABLED (RatioMinimum {tuning.Signal.RatioMinimum:F2}x is " +
            "unreachable — no bucket in 28 days came close). " +
            (tuning.Signal.RatioHighVolumeUsd > 0m
                ? $"The ONLY way in is the high-volume waiver: a closed 15m bucket, settled " +
                  $"{tuning.Signal.RatioSettleMinutes} min, trading >= " +
                  $"${tuning.Signal.RatioHighVolumeUsd / 1_000_000m:F1}M at ANY ratio -> enter " +
                  "WITH the heavier side (H11). Expect ~0.9 signals/day."
                : "and RatioHighVolumeUsd is 0, so THIS STRATEGY CANNOT ENTER AT ALL.") +
            " PRICE IS NOT READ.",

        FlowEntryMode.FlowRatio =>
            $"the last closed 15m bucket, settled {tuning.Signal.RatioSettleMinutes} min, must " +
            $"trade >= ${tuning.Signal.RatioMinVolumeUsd / 1_000_000m:F1}M with one side >= " +
            $"{tuning.Signal.RatioMinimum:F2}x the other -> enter WITH that side" +
            (tuning.Signal.RatioHighVolumeUsd > 0m
                ? $"; OR >= ${tuning.Signal.RatioHighVolumeUsd / 1_000_000m:F1}M at ANY ratio, " +
                  "which waives the ratio test entirely (H11)"
                : "") +
            ". " + DescribeShortGate(tuning.Signal) +
            " PRICE IS NOT READ, so every price and z threshold is inert.",

        FlowEntryMode.CandleReversal =>
            $"LONG when price has fallen at least {tuning.Signal.ReversalDropPct:F2}% over " +
            $"{tuning.Signal.ReversalBars} closed 15m bar(s)" +
            (tuning.Signal.ReversalLongOnly
                ? ", long only"
                : $"; SHORT when it has risen {tuning.Signal.ReversalRisePct:F2}% over " +
                  $"{tuning.Signal.ReversalBarsShort} bar(s)") +
            ". PRICE ONLY — no order flow is read. signal_outcomes records " +
            "AggregateZ = 0 meaning 'not measured', not 'measured as nothing'.",

        // No catch-all that describes a real rule. An unrecognised mode says so, rather
        // than borrowing the nearest description -- which is the defect this method was
        // extracted to fix. Two arms for ZScore and OfiMagnitude sat here until those
        // modes were removed on 2026-09-18.
        _ => $"UNRECOGNISED EntryMode '{tuning.Signal.EntryMode}'. Only FlowRatio and " +
             "CandleReversal have scorers in this build, and the evaluator will refuse " +
             "rather than score. Fix the config; do not trust anything below this line.",
    };

    /// <summary>
    /// The SHORT side's own notional floor, stated at startup rather than left to be
    /// inferred.
    ///
    /// H20 put a ratio and an |OFI| gate here and both were out of reach, which stopped the
    /// short side instead of raising its bar; H21 replaces them with size. The banner still
    /// has to say how rare the result is, because a floor almost nothing clears and a floor
    /// nobody meant to set look identical in a config file.
    /// </summary>
    private static string DescribeShortGate(FlowSignalOptions s)
    {
        if (s.ShortMinVolumeUsd <= 0m || s.ShortMinVolumeUsd <= s.RatioMinVolumeUsd)
            return "SHORT and LONG are held to the same floor.";

        var gate = $"SHORT requires >= ${s.ShortMinVolumeUsd / 1_000_000m:F1}M instead of the " +
                   $"long side's ${s.RatioMinVolumeUsd / 1_000_000m:F1}M, at the same " +
                   $"{s.RatioMinimum:F2}x ratio (H21).";

        // The waiver sits above this floor, so it is untouched and becomes the main way a
        // short still happens. Said out loud because "I raised the short floor" and "I
        // slowed the short side" are different claims and only the first one is true.
        return s.RatioHighVolumeUsd > 0m && s.RatioHighVolumeUsd > s.ShortMinVolumeUsd
            ? gate + $" The ${s.RatioHighVolumeUsd / 1_000_000m:F1}M waiver is ABOVE that floor " +
                     "and still takes shorts at any ratio, so it is now where most shorts will " +
                     "come from — 19 qualifying buckets in the last 30 days against 1 for this " +
                     "ratio path."
            : gate;
    }

    /// <summary>
    /// Where the target really sits once <c>use_dynamic_tp_sl</c> is on.
    ///
    /// The widening scales the barrier by <c>1 + 10 x excursion</c>, capped at 2, and the
    /// excursion is the trade's own peak — so the target RETREATS as price advances. Price
    /// and barrier meet where
    ///
    ///     x = t (1 + 10x)   =>   x = t / (1 - 10t)
    ///
    /// which turns a stored 4.00% target into 6.667%. Past t = 5% the scale clamps at 2
    /// before they converge, and the effective target is simply 2t.
    ///
    /// This is not a refinement. Until 2026-09-18 the startup risk line quoted the stored
    /// 4.00% and a 35.0% breakeven win rate, and NO TRADE HAD EXITED ON TP IN NINE DAYS —
    /// the level being reported was one nothing was measured against.
    /// </summary>
    internal static decimal EffectiveTargetPct(decimal storedTargetPct, bool dynamicOn)
    {
        if (!dynamicOn || storedTargetPct <= 0m) return storedTargetPct;

        // Below 5% the two converge before the scale caps; at or above it the cap binds
        // first. The two branches agree exactly at t = 5%, so this is continuous.
        return storedTargetPct < 0.05m
            ? storedTargetPct / (1m - 10m * storedTargetPct)
            : storedTargetPct * 2m;
    }

    /// <inheritdoc />
    public StrategyRiskProfile? DescribeRisk(BotOptions opts)
    {
        // The narrowest stop this configuration can place. Both floors, exactly as
        // VolatilityStops applies them — reproduced rather than shared because the live
        // path needs a volatility reading and this one is a statement about the config.
        // If the two ever disagree the geometry is what runs; this is only a report.
        var feeFloor = tuning.RoundTripFeeRate * VolatilityStops.MinStopAsFeeMultiple;
        var stopPct  = Math.Max(feeFloor, tuning.MinStopPct ?? 0m);

        if (tuning.MaxStopPct is { } cap && cap > 0m && stopPct > cap) stopPct = cap;
        if (stopPct <= 0m) return null;

        // The stop is reported at its STORED width even when the dynamic widening is on.
        // That half is genuinely path-dependent — it only widens once the trade is in
        // profit, and the measured scale at the moment of an actual stop-out averaged
        // 1.04 — so the stored width is the honest summary and the overrun is named in
        // the basis rather than guessed at.
        var dyn = opts.UseDynamicTpSl;

        // One geometry now. The range branch that stood beside this went with
        // ResolveFromRange on 2026-09-19; see HYPOTHESES.md "Removed features".
        var stored    = stopPct * (decimal)tuning.TargetRiskMultiple;
        var effective = EffectiveTargetPct(stored, dyn);

        return new StrategyRiskProfile(
            Name, stopPct, effective,
            $"ATR geometry: stop floored at {stopPct:P2} (fee floor {feeFloor:P2}, noise " +
            $"floor {tuning.MinStopPct ?? 0m:P2}), target {tuning.TargetRiskMultiple:F2}x " +
            "the stop. A larger ATR widens both together, so the ratio holds." +
            (dyn
                ? $" use_dynamic_tp_sl is ON, so the {stored:P2} target RETREATS as price " +
                  $"advances and is only reachable at {effective:P2}; the stop may widen to " +
                  "2x the same way, measured at 1.04x when stops actually fired."
                : ""));
    }

    public async Task<EntryDecision> EvaluateEntryAsync(StrategyContext ctx, CancellationToken ct)
    {
        var opts = ctx.Options;

        try
        {
            var set = await flowRepo.GetRecentAsync(opts.Symbol, tuning.Signal.MinimumBars, ct);

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

            // ── Candles ───────────────────────────────────────────────────────
            //
            // Fetched before scoring now, because CandleReversal scores on price and
            // the flow scorer never sees them. The staleness check above still runs
            // for every mode: flow_bars_15m going cold is the clearest signal that
            // ingestion has stopped, whether or not this mode reads it.
            var candles = await flowRepo.GetRecentCandlesAsync(
                opts.Symbol, tuning.AtrLookbackMinutes, ct);

            // ── The signal ────────────────────────────────────────────────────
            var verdict = tuning.Signal.EntryMode switch
            {
                FlowEntryMode.CandleReversal =>
                    CrossVenueFlowScorer.ScoreReversal(candles, DateTime.UtcNow, tuning.Signal),

                // Reads flow, not price, and enters with the dominant side. The candles
                // fetched above are still used for the volatility reading the geometry
                // needs; this mode simply does not score on them.
                FlowEntryMode.FlowRatio =>
                    CrossVenueFlowScorer.ScoreFlowRatio(set.ByVenue, DateTime.UtcNow, tuning.Signal),

                // No catch-all that scores something. ZScore and OfiMagnitude were removed
                // on 2026-09-18 and this arm used to send anything unrecognised to the
                // z-rule — which is the exact defect that got the backtester deleted, a
                // caller silently scored on a rule it did not ask for.
                _ => FlowVerdict.Abstain(
                    "UNRECOGNISED_ENTRY_MODE",
                    $"EntryMode '{tuning.Signal.EntryMode}' has no scorer in this build. " +
                    "FlowRatio and CandleReversal are the only rules that exist; fix the " +
                    "config rather than trusting anything downstream of this."),
            };

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
            // Candles were fetched above, before scoring, since CandleReversal needs
            // them to score at all. One read serves both.
            var volatility = Volatility.Measure(candles, tuning.AtrBarMinutes);

            if (!volatility.IsUsable)
                return Refuse("NO_VOLATILITY_READ",
                    $"Only {candles.Count} candle(s) available, so the stop cannot be scaled to " +
                    "the market. Refusing rather than falling back to a fixed percentage — a stop " +
                    "of the wrong width is what this strategy exists to stop doing.");


            // The entry-pullback wait was deleted on 2026-09-19. It held the entry until
            // price gave back k x ATR from the signal bucket close, and re-measured on all
            // 68 recorded signals with the deployed exit set it does not work at any depth:
            // net total R by k is -0.822 / -1.222 / -0.667 / -3.617 / -1.777 / -0.835, with
            // H4's shipped 0.75 the WORST of the six. Waiting shrinks losers and winners by
            // the same factor and skips winners 11:1, because a trade that runs in your
            // favour immediately never gives back. HYPOTHESES.md, "Removed features".

            // ── Exit levels from measured volatility ──────────────────────────
            //
            // The range geometry that used to sit beside this -- stop at the recent
            // range low, target at its high -- was deleted on 2026-09-19. It MEASURED
            // BETTER (mean R +0.107 against +0.015 over 1,486 decision points) and was
            // still switched off, because FlowRatio enters WITH the dominant side, which
            // puts price at the edge of its own range and leaves the boundary target
            // sitting on top of the entry. HYPOTHESES.md carries the table under
            // "Removed features"; reach for it if an entry rule is ever added that enters
            // INTO a range rather than out of one.
            var geometry = VolatilityStops.Resolve(
                entryPrice:         ctx.CurrentPrice,
                side:               verdict.Side!,
                volatility:         volatility,
                roundTripFeeRate:   tuning.RoundTripFeeRate,
                stopAtrMultiple:    tuning.StopAtrMultiple,
                targetRiskMultiple: tuning.TargetRiskMultiple,
                maxStopPct:         tuning.MaxStopPct,
                minStopPct:         tuning.MinStopPct);


            // A trade whose reward does not cover its risk after fees is refused here
            // rather than left for the gate. The gate is a judgement call on a
            // proposal; this is arithmetic, and arithmetic should not be delegated to
            // a language model.
            if (geometry.RewardRisk < tuning.MinRewardRisk)
                return Refuse("REWARD_RISK_TOO_LOW",
                    $"Stop {geometry.StopPct:P2} against target {geometry.TargetPct:P2} is " +
                    $"{geometry.RewardRisk:F2}:1 after fees, under the {tuning.MinRewardRisk:F2}:1 " +
                    $"minimum (ATR {volatility.AtrPct:F2}%).");

            // ── Confidence: there isn't one ───────────────────────────────────
            //
            // This was |AggregateZ| / (EnterZ * 2). Neither surviving rule computes an
            // aggregate z — FlowRatio reads one bucket's ratio, CandleReversal reads price
            // — so AggregateZ has been structurally 0 since 2026-09-11 and the expression
            // has evaluated to 0 on every entry since. Removing EnterZ only made that
            // visible; it did not change a number.
            //
            // Zero is also the SAFE value rather than merely the honest one. PositionSizer
            // applies confidence scaling only when `useAiSizing && confidence > 0`, so 0
            // leaves the scalar at 1.0 and sizing stays purely risk-based. Substituting
            // 1.0 to look neutral would silently multiply every order by 1.5 the moment
            // use_ai_sizing was switched on.
            //
            // If a rule ever produces a real confidence measure, it belongs on FlowVerdict
            // where the rule that knows it can set it.
            const decimal confidence = 0m;

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
    public async Task<ExitDecision> EvaluateExitAsync(
        BotTrade trade, decimal currentPrice, BotOptions opts, CancellationToken ct)
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

        var isLong    = trade.Side != "SHORT";
        var stopPrice = trade.StopPrice!.Value;
        var tgtPrice  = trade.TargetPrice!.Value;

        // The dynamic levels, when they differ from the stored ones. Reported on the
        // decision so the caller can persist them for an operator to read; nothing in
        // this method ever reads them back.
        decimal? dynamicStop = null, dynamicTarget = null;

        // ── Dynamic widening, off by default ──────────────────────────────────
        //
        // Scales both barriers outward as the trade's own favourable excursion grows,
        // on the argument that a position that has already travelled is in a market
        // moving further than the entry assumed. Recomputed from the STORED levels
        // every cycle rather than written back, so it is stateless: nothing to recover
        // after a restart, and switching it off restores the original levels exactly.
        //
        // WIDENING THE STOP COSTS MORE THAN IT LOOKS, and the number is not a matter of
        // taste. Position size is set at entry by notional = capital x risk% / stopPct,
        // so the dollars at risk are fixed by the stop that existed then. Doubling the
        // stop afterwards doubles the loss the position can take while the size stays
        // put: at risk_pct_per_trade 0.005 a full stop-out stops costing 0.5% of
        // capital and starts costing up to 1.0%. The cap below bounds it at 2x, which
        // is a bound on the overrun, not a removal of it.
        //
        // The target half carries no such cost — holding out for more is free apart
        // from time — which is why the two halves are not equally defensible even
        // though one switch controls both.
        //
        // IT IS ON. `use_dynamic_tp_sl` is TRUE in bot_config and has been throughout, and
        // this comment said "Left OFF" until 2026-09-18 — which is how the following went
        // unnoticed for nine days: NOT ONE TRADE HAS EXITED ON TP since the 2.00% stop
        // floor shipped. 28 closed on the OFI reversal, 5 on the stop, 2 on the timeout,
        // 0 on target. Trade 93 was the first whose price genuinely cleared its stored
        // target (peak 106.12 against a 105.06 target) and it still did not fire, because
        // the level it was actually compared against was 107.75.
        //
        // WHERE THE TARGET REALLY SITS. The barrier retreats as price advances, so the two
        // only meet where x = t(1 + 10x), i.e. x = t / (1 - 10t): a stored 4.00% target is
        // a 6.667% one in practice, and the stored number is not the number that trades.
        // Above t = 5% the scale clamps at 2 first and the effective target is simply 2t.
        // DescribeRisk reports this, so the startup risk line stops quoting a level
        // nothing is measured against.
        //
        // The levels are still RECOMPUTED FROM THE STORED ONES every cycle and never
        // written back to `target_price`. That is not an oversight and must not be
        // "fixed": `tgtDist = |tgtPrice - entry| * scale` reads its own input, so writing
        // the scaled value into the column it reads compounds it — d·s, d·s², d·s³ once
        // every 30 seconds. At s≈1.4 the barrier is past +116% within five minutes and the
        // stop goes with it, which is a position with no stop at all. The observable copy
        // goes to dedicated columns instead; see DynamicStopPrice / DynamicTargetPrice on
        // ExitDecision.
        if (opts.UseDynamicTpSl && trade.PeakPrice.HasValue)
        {
            var excursion = isLong
                ? (trade.PeakPrice.Value - trade.EntryPrice) / trade.EntryPrice
                : (trade.EntryPrice - trade.PeakPrice.Value) / trade.EntryPrice;

            // Only favourable travel widens anything. PeakPrice tracks the high for a
            // long and the low for a short, so an adverse reading here means the peak
            // has not moved past entry and there is nothing to scale on.
            if (excursion > 0m)
            {
                var scale = Math.Clamp(1m + excursion * 10m, 1m, 2m);

                var stopDist = Math.Abs(trade.EntryPrice - stopPrice) * scale;
                var tgtDist  = Math.Abs(tgtPrice - trade.EntryPrice) * scale;

                stopPrice = isLong ? trade.EntryPrice - stopDist : trade.EntryPrice + stopDist;
                tgtPrice  = isLong ? trade.EntryPrice + tgtDist  : trade.EntryPrice - tgtDist;

                dynamicStop   = stopPrice;
                dynamicTarget = tgtPrice;

                // Information, not Debug. At Debug this was invisible under the default
                // minimum level, so a mechanism that moves both barriers on every open
                // position left no trace anywhere — not in the log, not on the row. The
                // operator reported it as "price passed TP and nothing happened", which is
                // exactly what a silent barrier looks like from outside.
                if (scale > 1.01m)
                    log.LogInformation(
                        "[XFlow] Trade {Id} dynamic scale {Scale:F2}x on {Exc:P2} excursion: " +
                        "stop {Stop:F4}, target {Target:F4} (stored target was {Stored:F4}). " +
                        "Risk now up to {Risk:F2}x what this position was sized for.",
                        trade.Id, scale, excursion, stopPrice, tgtPrice,
                        trade.TargetPrice!.Value, scale);
            }
        }

        // Stop before target when a single reading clears both. It is the conservative
        // resolution -- it books the loss rather than the win on an ambiguous bar -- and
        // it is the ordering the deleted backtester used, so replayed results stay
        // comparable with what production actually did.
        var hitStop = isLong ? currentPrice <= stopPrice : currentPrice >= stopPrice;
        if (hitStop) return Exit("SL", currentPrice, changePct, dynamicStop, dynamicTarget);

        var hitTarget = isLong ? currentPrice >= tgtPrice : currentPrice <= tgtPrice;
        if (hitTarget) return Exit("TP", currentPrice, changePct, dynamicStop, dynamicTarget);

        // ── Exit when the aggregate imbalance turns against the position ───────
        //
        // Sum buy and sell notional over the last FlowOfiBars closed buckets, take the
        // imbalance of those sums, and close when it points against the trade. Ten
        // buckets is 150 minutes.
        //
        // THIS IS THE ONLY EXIT THAT EARNS. Over the 40 trades since the 2% stop floor
        // shipped: OFI_REVERSAL 30 exits +4.647R, SL 6 exits -6.615R, TIMEOUT 3 exits
        // -0.424R, TP zero. Measured 2026-09-19.
        //
        // The pre-launch sweep called this "shipped with negative evidence" and that
        // label was wrong — it compared windows against a no-rule baseline on 42 signals
        // before the stop floor existed, and the configuration it measured is not the one
        // that runs. H10, H14 and H15 in HYPOTHESES.md carry those tables in full.
        //
        // Do not propose removing it. Narrowing the stop is the same mistake from the
        // other side: at a 1% stop the replay takes OFI exits from 51 to 30 and stop-outs
        // from 12 to 29, because positions get swept out before the flow can turn.
        //
        // Set UseFlowOfiExit false to remove it.
        if (tuning.UseFlowOfiExit)
        {
            var reversal = await OfiTurnedAgainstAsync(trade, ct);

            if (reversal is not null)
            {
                log.LogInformation(
                    "[XFlow] Trade {Id} {Side} closing on OFI reversal at {Change:P2}: {Why}",
                    trade.Id, trade.Side, changePct, reversal);

                return Exit("OFI_REVERSAL", currentPrice, changePct, dynamicStop, dynamicTarget);
            }
        }

        return new ExitDecision(false, null, currentPrice, changePct, dynamicStop, dynamicTarget);
    }

    /// <summary>
    /// Has the aggregate imbalance over the last <see cref="FlowStrategyOptions.FlowOfiBars"/>
    /// closed buckets turned against this position, after having been with it?
    ///
    /// The "after having been with it" half is not decoration. Without it the rule
    /// reads a window that mostly predates the entry: at ten buckets, 8 of 42 signals
    /// had the imbalance already against them at the moment they opened, and those
    /// trades would be closed on the first bucket after entry — fifteen minutes in,
    /// on evidence that has nothing to do with the trade. That is the same defect that
    /// closed two live positions thirty seconds after opening them on 2026-09-09, and
    /// the guard is the same shape as the fix. The measured +0.410 is the guarded
    /// version; the unguarded one was never measured at this window.
    ///
    /// Stateless by construction. Every cycle it re-reads the buckets from entry
    /// onward and asks the question again, so there is no "have I seen a favourable
    /// reading yet" flag to persist, recover after a restart, or get wrong.
    ///
    /// Returns a sentence, or null for no — including every case where the question
    /// cannot be answered. Not knowing never closes a position that its stop is
    /// already protecting.
    /// </summary>
    private async Task<string?> OfiTurnedAgainstAsync(BotTrade trade, CancellationToken ct)
    {
        var bars = Math.Max(1, tuning.FlowOfiBars);

        try
        {
            // Enough history to compute the rolling window at every bucket since the
            // trade opened: one per elapsed quarter hour, plus the window itself, plus
            // slack for the bucket still filling. Capped so a stuck position cannot ask
            // for an unbounded read.
            var elapsed = DateTime.UtcNow - trade.OpenedAt;
            var since   = (int)Math.Ceiling(Math.Max(0, elapsed.TotalMinutes) / 15.0);
            var needed  = Math.Min(since + bars + 2, 96);

            var set = await flowRepo.GetRecentAsync(trade.Symbol, needed, ct);
            if (set.VenueCount == 0) return null;

            var age = set.Age(DateTime.UtcNow);
            if (age > tuning.MaxBarAge)
            {
                log.LogWarning(
                    "[XFlow] OFI exit skipped for trade {Id}: newest bucket is {Age:F0} min old, " +
                    "past the {Limit:F0} min limit. Holding; the stop still applies.",
                    trade.Id, age.TotalMinutes, tuning.MaxBarAge.TotalMinutes);
                return null;
            }

            var closed = CrossVenueFlowScorer.OfiByClosedBucket(set.ByVenue, DateTime.UtcNow, needed);
            if (closed.Count < bars) return null;

            // Buy and sell notional recovered exactly from the imbalance and the total:
            //   ofi = (b-s)/(b+s)  =>  b = v(1+ofi)/2,  s = v(1-ofi)/2
            // Done here rather than by adding a second aggregation to the scorer, so
            // the live bucket keeps being dropped in exactly one place.
            var buy  = closed.Select(x => x.VolumeUsd * (1m + (decimal)x.Ofi) / 2m).ToArray();
            var sell = closed.Select(x => x.VolumeUsd * (1m - (decimal)x.Ofi) / 2m).ToArray();

            var isLong = trade.Side != "SHORT";
            bool wasFavourable = false;
            double latestOfi = 0.0;
            DateTime latestBucket = default;
            decimal windowVol = 0m;

            for (var i = bars - 1; i < closed.Count; i++)
            {
                // Only readings whose bucket closed after the position opened count.
                if (closed[i].Bucket.AddMinutes(15) <= trade.OpenedAt) continue;

                decimal b = 0m, s = 0m;
                for (var j = i - bars + 1; j <= i; j++) { b += buy[j]; s += sell[j]; }

                var total = b + s;
                if (total <= 0m) continue;

                var ofi = (double)((b - s) / total);
                var withTrade = isLong ? ofi > 0.0 : ofi < 0.0;

                if (withTrade) wasFavourable = true;
                else if (wasFavourable)
                {
                    latestOfi    = ofi;
                    latestBucket = closed[i].Bucket;
                    windowVol    = total;

                    return $"{bars}-bucket imbalance turned {(isLong ? "sell" : "buy")}-side at " +
                           $"{latestBucket:HH:mm} — OFI {latestOfi:+0.000;-0.000} on " +
                           $"${windowVol / 1_000_000m:F1}M over {bars * 15} minutes, after having " +
                           $"favoured this {trade.Side} earlier in the hold.";
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed read never closes a position. The stop does not depend on this
            // query succeeding.
            log.LogError(ex, "[XFlow] OFI exit check failed for trade {Id}; holding.", trade.Id);
            return null;
        }
    }


    private static ExitDecision Exit(
        string reason, decimal price, decimal changePct,
        decimal? dynamicStop = null, decimal? dynamicTarget = null) =>
        new(true, reason, price, changePct, dynamicStop, dynamicTarget);
}

/// <summary>
/// Everything <see cref="CrossVenueFlowStrategy"/> can be tuned by, in one object.
///
/// Separate from BotOptions because these are the parameters a sweep varies together,
/// and they need to be settable as the unit that was validated together. Picking a
/// threshold from one measurement and a stop multiple from another produces a
/// configuration that was never tested.
/// </summary>
public sealed class FlowStrategyOptions
{
    public const string Section = "FlowStrategy";

    /// <summary>
    /// Configuration section for the second, parallel instance: the dip rule.
    ///
    /// A separate section rather than a list, because every field below is a threshold
    /// that was measured for one entry rule and means nothing for the other. Binding
    /// them separately makes "which numbers is CANDLE_REVERSAL running?" a question with
    /// one answer you can read.
    /// </summary>
    public const string DipSection = "DipStrategy";

    /// <summary>
    /// The strategy name this options block configures. Must match an entry in
    /// bot_config.active_strategies, and it is what lands in bot_trades.strategy.
    ///
    /// Every per-strategy limit keys off this string — the concurrency limit, the
    /// per-side limit, the cooldown, the consecutive-loss breaker's scope. Two blocks
    /// sharing a name would silently merge all four, so a name is as load-bearing as
    /// any threshold here.
    /// </summary>
    public string Name { get; set; } = CrossVenueFlowStrategy.StrategyName;

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
    /// signal grid, and it has never been swept -- the tool that would have is gone.
    /// </summary>
    public int AtrBarMinutes { get; set; } = FlowGeometryDefaults.AtrBarMinutes;

    public double StopAtrMultiple    { get; set; } = FlowGeometryDefaults.StopAtrMultiple;
    public double TargetRiskMultiple { get; set; } = FlowGeometryDefaults.TargetRiskMultiple;



    /// <summary>
    /// Round-trip cost assumed when placing the stop and target, as a fraction of
    /// notional.
    ///
    /// Deliberately the taker-both-legs figure rather than the 7 bps the bot actually
    /// pays with a post-only entry: at the moment the geometry is fixed it is not yet
    /// settled whether the resting order fills as maker or gets crossed, and erring high
    /// puts the target slightly further out and rejects marginal reward:risk. That fails
    /// toward not trading. Excludes slippage and funding — see <see cref="TradingCosts"/>.
    ///
    /// RiskEngine's default used to disagree with this at 20 bps, documented as
    /// "Binance spot without BNB discount". Both now come from the same constant.
    /// </summary>
    public decimal RoundTripFeeRate { get; set; } = TradingCosts.TakerRoundTrip;

    /// <summary>
    /// Refuse an entry whose reward:risk after fees is below this. Arithmetic, checked
    /// before the gate is asked anything.
    /// </summary>
    public decimal MinRewardRisk { get; set; } = FlowGeometryDefaults.MinRewardRisk;



    /// <summary>
    /// Optional hard ceiling on the stop distance. Null by default, and that is the
    /// recommended setting: capping the stop reintroduces the failure this strategy
    /// exists to remove, a stop narrower than the market's own movement. When risk per
    /// trade has to come down, reduce the position size instead.
    /// </summary>
    public decimal? MaxStopPct { get; set; } = null;

    /// <summary>
    /// Floor under the stop distance, as a fraction of entry — the market's noise, not
    /// the exchange's fees. Null leaves only the fee floor in place.
    ///
    /// 0.020 because SOL's median 15-minute true range is 1.07% and the fee floor put
    /// every stop at 0.40%, well inside it. That is not a stop, it is a guarantee of
    /// being stopped: 87 of 115 long signals were closed by ordinary movement rather
    /// than by being wrong.
    ///
    /// **H8 carries the seven-width sweep** that chose it: 1.60 / 2.00 / 2.40 pass all
    /// three checks and nothing narrower passes any two, which is a plateau rather than
    /// a peak. Position size falls as the stop widens (notional = capital x risk / stop),
    /// so risk per trade is unchanged and only the notional moves.
    ///
    /// Re-measured 2026-09-19 on 68 signals with the deployed exit set, and it holds:
    /// 2.00% is the best cell, 1.0% and 1.2% the worst. The mechanism is that a narrow
    /// stop destroys the OFI exit — at 1% the replay takes stop-outs from 12 to 29 and
    /// OFI exits from 51 to 30, sweeping positions out before the flow can turn.
    ///
    /// The literal lives on <see cref="FlowGeometryDefaults.MinStopPct"/> so anything
    /// outside BotService can reach it. The backtester could not, and applied no floor
    /// at all as a result — see that constant.
    /// </summary>
    public decimal? MinStopPct { get; set; } = FlowGeometryDefaults.MinStopPct;

    /// <summary>
    /// Close a position when the aggregate imbalance over the last
    /// <see cref="FlowOfiBars"/> closed buckets turns against it, after having
    /// favoured it earlier in the hold.
    ///
    /// **This is the only exit that earns** — 30 of the last 40 closures, +4.647R, against
    /// the stop's -6.615R over 6 and a take-profit that has never fired. The pre-launch
    /// sweep called it negative; it measured a configuration that does not run. H10, H14
    /// and H15 carry those tables. Do not propose removing it.
    /// </summary>
    public bool UseFlowOfiExit { get; set; } = true;

    /// <summary>
    /// Closed 15-minute buckets summed for that imbalance. 10 = 150 minutes.
    ///
    /// **XVENUE_FLOW runs 15, set in appsettings (H15).** The default here stays at 10 so
    /// the change lives in one configuration section and DipStrategy keeps the value it was
    /// running under — the two rules are genuinely on different windows, not drifting.
    ///
    /// The reasoning, from production: trades were closing on this exit at a mean hold of
    /// 3.83h and never reaching the 12-hour cap, while the rule measures better the longer
    /// it holds. Widening the window is the narrowest lever on that — entry, barriers and
    /// cap are all untouched. H14 (20 buckets) was abandoned after 3 trades and superseded
    /// by H15; both entries carry the numbers and the decision rules.
    /// </summary>
    public int FlowOfiBars { get; set; } = 10;
}
