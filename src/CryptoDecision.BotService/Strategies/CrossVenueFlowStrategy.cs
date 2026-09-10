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

                _ => CrossVenueFlowScorer.Score(set.ByVenue, tuning.Signal),
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


            // ── Entry timing: wait for the move to give some of itself back ────
            //
            // The signal is late by construction and this is the correction for it.
            // Aggressive order flow IS what moves price, so by the time an imbalance
            // is measurable on a closed 15-minute bucket the move has already
            // happened: aggregate z correlates +0.467 with the PRECEDING hour's
            // return and -0.116 with the following one, measured over 440 buckets.
            // Entering at market on that reading buys the top of the move and then
            // counts the retracement as adverse excursion against the position.
            //
            // Measured on the first 28 paper trades, over the 12 hours after entry:
            //
            //     entry            median MFE   median MAE   ratio   win at 2R
            //     at market          2.00 ATR     3.46 ATR    0.58       14.3%
            //     0.75 ATR pullback  1.96 ATR     2.72 ATR    0.72       27.3%
            //
            // The favourable excursion barely moves; the ADVERSE one falls by a
            // fifth. Waiting does not find better trades, it finds a better price in
            // the same trade — which is exactly what a late entry costs. This was the
            // only lever that moved the win rate at all: an 80-cell sweep of stop
            // width against target multiple left it pinned at 17.9%.
            //
            // Stateless on purpose. The reference is the close of the signal bucket's
            // last minute, which every cycle recomputes identically, so there is no
            // pending-order state to keep, recover after a restart, or get wrong. The
            // waiting window is therefore however long the verdict stays actionable
            // rather than a fixed timer — the signal decides how long it is willing to
            // wait for its own price, which is a more honest bound than a constant.
            //
            // NOT PROVEN. Read off 28 trades, in-sample, one market regime, and the
            // 27.3% it reaches is still well under the ~42% this geometry needs to
            // break even. It is registered in HYPOTHESES.md as H4 and is to be judged
            // on trades taken AFTER it shipped. Set EntryPullbackAtr to 0 to disable.
            if (tuning.EntryPullbackAtr > 0 && set.LatestBucket is { } signalBucket)
            {
                // Last minute of the closed bucket that produced this verdict.
                var referenceAt = signalBucket.AddMinutes(14);
                var reference   = candles.LastOrDefault(c => c.OpenTime <= referenceAt)?.Close;

                if (reference is { } refPrice && refPrice > 0m)
                {
                    var isLong  = verdict.Side == "LONG";
                    var giveBack = (decimal)(tuning.EntryPullbackAtr * volatility.AtrPct) / 100m;

                    var limit = isLong
                        ? refPrice * (1m - giveBack)
                        : refPrice * (1m + giveBack);

                    var reached = isLong
                        ? ctx.CurrentPrice <= limit
                        : ctx.CurrentPrice >= limit;

                    if (!reached)
                        return Refuse("AWAITING_PULLBACK",
                            $"{verdict.Side} is live (z={verdict.AggregateZ:F2}) but price " +
                            $"{ctx.CurrentPrice:F4} has not given back " +
                            $"{tuning.EntryPullbackAtr:F2}xATR from the {refPrice:F4} bucket close — " +
                            $"waiting for {limit:F4}. Entering here would pay for a move that has " +
                            "already happened.");
                }
                // A missing reference candle is not a reason to refuse: the pullback
                // rule is an improvement on entry timing, not a safety check, and
                // failing open costs a worse price rather than an unmanaged position.
            }

            // ── Exit levels: the range's own boundaries, or a multiple of ATR ──
            //
            // Range is the default because it measured seven times the mean R of the
            // ATR pair over 1,486 decision points, and because the ATR pair sat on its
            // own break-even line — see VolatilityStops.ResolveFromRange for the table.
            // The lookback is deliberately short: 2 hours beat 4 clearly.
            StopGeometry geometry;

            if (tuning.UseRangeGeometry)
            {
                var since = candles.Count > 0
                    ? candles[^1].OpenTime.AddMinutes(-tuning.RangeLookbackMinutes)
                    : DateTime.MinValue;

                var window = candles.Where(c => c.OpenTime >= since).ToList();

                if (window.Count < 2)
                    return Refuse("NO_RANGE_READ",
                        $"Only {window.Count} candle(s) in the last " +
                        $"{tuning.RangeLookbackMinutes} minutes, so the range has no boundaries " +
                        "to place the stop and target on.");

                geometry = VolatilityStops.ResolveFromRange(
                    entryPrice:       ctx.CurrentPrice,
                    side:             verdict.Side!,
                    rangeHigh:        window.Max(c => c.High),
                    rangeLow:         window.Min(c => c.Low),
                    volatility:       volatility,
                    roundTripFeeRate: tuning.RoundTripFeeRate,
                    maxStopPct:       tuning.MaxStopPct,
                    minStopPct:       tuning.MinStopPct);

                // The entry has already broken out of the range it was measured
                // against. Refusing beats inventing a barrier: a breakout is exactly
                // when the boundary stops being the level price respects.
                if (geometry.StopPct <= 0m)
                    return Refuse("PRICE_OUTSIDE_RANGE", geometry.Basis);
            }
            else
            {
                geometry = VolatilityStops.Resolve(
                    entryPrice:         ctx.CurrentPrice,
                    side:               verdict.Side!,
                    volatility:         volatility,
                    roundTripFeeRate:   tuning.RoundTripFeeRate,
                    stopAtrMultiple:    tuning.StopAtrMultiple,
                    targetRiskMultiple: tuning.TargetRiskMultiple,
                    maxStopPct:         tuning.MaxStopPct,
                    minStopPct:         tuning.MinStopPct);
            }

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
        // Left OFF in bot_config. It was inert before this: it scaled TakeProfitPct
        // and StopLossPct, which only the no-geometry fallback above ever reads, and
        // all eight trades on this deployment carry geometry. A switch that reports
        // success and changes nothing is the third of its kind found in this project.
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

                if (scale > 1.01m)
                    log.LogDebug(
                        "[XFlow] Trade {Id} dynamic scale {Scale:F2}x on {Exc:P2} excursion: " +
                        "stop {Stop:F4}, target {Target:F4}. Risk now up to {Risk:F2}x what this " +
                        "position was sized for.",
                        trade.Id, scale, excursion, stopPrice, tgtPrice, scale);
            }
        }

        // Stop before target, matching how the backtester resolves a bar containing
        // both. Same ordering in both places or the live results cannot be compared
        // with the simulated ones they were validated on.
        var hitStop = isLong ? currentPrice <= stopPrice : currentPrice >= stopPrice;
        if (hitStop) return Exit("SL", currentPrice, changePct);

        var hitTarget = isLong ? currentPrice >= tgtPrice : currentPrice <= tgtPrice;
        if (hitTarget) return Exit("TP", currentPrice, changePct);

        // ── Exit when the aggregate imbalance turns against the position ───────
        //
        // Sum buy and sell notional over the last FlowOfiBars closed buckets, take the
        // imbalance of those sums, and close when it points against the trade. Ten
        // buckets is 150 minutes.
        //
        // SHIPPED WITH NEGATIVE EVIDENCE, on the operator's decision after the numbers
        // below were put in front of them twice. Recorded here rather than in a commit
        // message alone, because the next person to read this file should not have to
        // reconstruct whether it was ever tested.
        //
        // Measured on 42 FlowRatio signals over the production window, this rule
        // against no rule at all:
        //
        //     window    mean R with rule      (no rule: +0.379)
        //      8 bars        +0.391
        //     10 bars        +0.410   <- shipped
        //     12 bars        +0.340
        //     15 bars        +0.318
        //     20 bars        +0.346
        //     30 bars        +0.338
        //
        // The chosen cell is the best of six and beats the baseline by 0.031R. It is
        // not a plateau: 10 and 12 bars are thirty minutes apart and differ by 0.070R,
        // which is more than the gap to the baseline. A parameter whose neighbours
        // disagree by more than its claimed effect is measuring noise.
        //
        // The operator's argument for it was slot turnover, which is a real mechanism
        // this file's earlier measurements had missed: a shorter hold frees the single
        // per-side slot sooner and lets more signals through. Measured with the slot
        // limit applied, it does exactly that and still loses:
        //
        //     with rule      27 trades taken, 15 blocked, total +9.29R
        //     without        24 trades taken, 18 blocked, total +10.22R
        //
        // Three extra trades worth about +1.0R against 0.082R lost on each of the
        // other twenty-four. Net -0.93R, which is itself inside the noise of a
        // 27-trade sample — the honest summary is not that this rule hurts but that it
        // has never shown a sign of helping, on three independent measures.
        //
        // The mechanism that argues against it is the hold-time curve: +0.032 at one
        // hour, +0.082 at two, +0.273 at four, +0.379 at twelve. This strategy earns by
        // holding. Any rule that ends the hold early is working against its own source
        // of return.
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

                return Exit("OFI_REVERSAL", currentPrice, changePct);
            }
        }

        return new ExitDecision(false, null, currentPrice, changePct);
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
    /// Place the stop at the recent range's low and the target at its high, instead of
    /// at multiples of ATR. See <see cref="VolatilityStops.ResolveFromRange"/> for the
    /// measurement that chose this, and for why it changes what MinRewardRisk means.
    ///
    /// StopAtrMultiple and TargetRiskMultiple are not read while this is on. They are
    /// left in place so switching back is a config edit.
    /// </summary>
    public bool UseRangeGeometry { get; set; } = false;

    /// <summary>
    /// Minutes of 1-minute candles the range boundaries are taken from.
    ///
    /// 120 by measurement, not by preference: at a 2-hour lookback the geometry
    /// returned mean R +0.107 against +0.030 at 4 hours over the same 1,486 decision
    /// points. A longer window gives wider, staler boundaries — the levels stop being
    /// where this market is currently turning.
    ///
    /// Must not exceed AtrLookbackMinutes, since the candles come from that fetch.
    /// </summary>
    public int RangeLookbackMinutes { get; set; } = 120;

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
    /// How far the market must give back, in multiples of ATR, before an actionable
    /// signal is taken. 0 disables the wait and enters at market as before.
    ///
    /// 0.75 was read off the first 28 paper trades and is not a proven value — see
    /// H4 in HYPOTHESES.md. The reasoning, and the reason it is expressed in ATR
    /// rather than basis points, is in CrossVenueFlowStrategy where it is applied.
    ///
    /// The cost of waiting is signals that never fill: at 0.75 ATR, six of those 28
    /// never came back and would not have been traded. That is the intended trade —
    /// the ones it drops are the ones that ran away, which are exactly the entries
    /// this is meant to stop paying for.
    /// </summary>
    public double EntryPullbackAtr { get; set; } = FlowGeometryDefaults.EntryPullbackAtr;

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
    /// than by being wrong, and every trade this bot has taken carries stop_pct of
    /// exactly 0.400 — the range low the geometry claims to use has never once been
    /// reached before the floor bound.
    ///
    /// Measured over the 19-day production window under the shipped rules, total P&amp;L
    /// as a percent of notional, with the three checks this file applies to everything:
    ///
    ///     stop     total    less the biggest trade    1st half    2nd half
    ///     0.40%    + 1.07        - 5.59                + 5.28      - 4.21
    ///     0.60%    + 0.26        - 6.40                + 0.61      - 0.35
    ///     0.80%    - 5.60        -12.26                - 4.58      - 1.03
    ///     1.20%    + 4.57        - 2.09                - 1.05      + 5.62
    ///     1.60%    +12.15        + 5.49                + 4.15      + 8.00
    ///     2.00%    +15.94        + 9.28                + 2.92      +13.02
    ///     2.40%    +10.35        + 3.69                + 1.63      + 8.72
    ///
    /// 1.60, 2.00 and 2.40 all pass all three; nothing narrower passes any two. Three
    /// adjacent widths agreeing is a plateau rather than a peak, which is the whole
    /// reason to believe it — every other parameter swept in this session produced a
    /// good cell sitting alone between bad ones.
    ///
    /// Position size falls as the stop widens, since notional = capital x risk / stop,
    /// so the risk per trade is unchanged and only the notional moves. In money at a
    /// constant risk fraction the sample returns roughly 3.5x what the 0.40% floor did.
    ///
    /// The count falls with it: 134 signals become 102, because a wider stop pushes
    /// more setups under MinRewardRisk. That is the intended trade.
    ///
    /// NOT PROVEN. One 19-day window, and roughly fifty configurations were measured
    /// against it in the session that produced this. What separates it from the other
    /// forty-nine is that the mechanism was written down in this repository before it
    /// was measured — see the stop-geometry note — and that it holds across a plateau
    /// rather than at a point. H8 in HYPOTHESES.md carries the decision rule.
    /// </summary>
    public decimal? MinStopPct { get; set; } = 0.020m;

    /// <summary>
    /// Close a position when the aggregate imbalance over the last
    /// <see cref="FlowOfiBars"/> closed buckets turns against it, after having
    /// favoured it earlier in the hold.
    ///
    /// Shipped with negative evidence on the operator's decision — the full table is in
    /// CrossVenueFlowStrategy where the rule is applied, and H10 in HYPOTHESES.md
    /// carries the decision rule. Summary: best of six windows, beats no-rule by 0.031R,
    /// neighbouring windows disagree by more than that, and with the position limit
    /// applied it takes three more trades and finishes 0.93R behind.
    /// </summary>
    public bool UseFlowOfiExit { get; set; } = true;

    /// <summary>
    /// Closed 15-minute buckets summed for that imbalance. 10 = 150 minutes.
    ///
    /// The operator's choice, argued from slot turnover: a shorter hold frees the one
    /// per-side slot sooner. The mechanism is real and was missing from earlier
    /// measurements here; the magnitude was then measured and does not pay.
    /// </summary>
    public int FlowOfiBars { get; set; } = 10;
}
