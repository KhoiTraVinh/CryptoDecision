using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CryptoDecision.Shared.Bot;
using CryptoDecision.Shared.Signals;

namespace CryptoDecision.BotService.Agent;

/// <summary>
/// A candidate entry, already fully specified by the deterministic layer.
///
/// Everything here is decided before the gate is asked anything: the direction came
/// from cross-venue flow consensus, the stop and target from measured volatility, the
/// size from the position sizer. The gate is handed a finished proposal and asked one
/// question.
///
/// The threshold fields are not decoration. Each corresponds to one of the grounds the
/// gate is allowed to refuse on, and each was added after the model cited that ground
/// against a number that did not support it — refusing on "dispersion is wide" at 2.8 bps
/// against a 25 bps ceiling it had already passed. A value without its scale is not
/// evidence, and the model was being handed values without scales.
/// </summary>
/// <param name="Evidence">
/// What this account's own history says about this exact candidate. Added 2026-09-19,
/// because the four original grounds were all unreachable and the gate had therefore
/// approved 45 of 45 — see <see cref="GateEvidence"/>.
/// </param>
/// <param name="OpenSameSide">
/// Positions already open on this side in this instrument, counted across EVERY
/// strategy. Not the same number as <paramref name="OpenPositions"/>, which is scoped to
/// the proposing strategy: max_open_per_side is enforced per strategy, so
/// CANDLE_REVERSAL and XVENUE_FLOW can each hold a LONG and neither limit notices. That
/// is the concentration question worth asking, and nothing was asking it.
/// </param>
public sealed record EntryCandidate(
    string        Symbol,
    string        Side,
    decimal       Price,
    FlowVerdict   Flow,
    StopGeometry  Geometry,
    decimal       NotionalUsd,
    int           OpenPositions,
    decimal       TodayPnlUsd,
    GateEvidence  Evidence,
    string        Strategy            = "",
    int           OpenSameSide        = 0,
    decimal       CapitalUsd          = 0m,
    decimal       DailyLossLimitPct   = 0m,
    double        MaxDispersionBps    = 0.0,
    int           MaxOpenPositions    = 0,

    /// <summary>
    /// This rule's last few closed trades, newest first. Empty when none or the query
    /// failed, which renders nothing rather than an empty heading.
    /// </summary>
    IReadOnlyList<RecentTrade>? RecentTrades = null);

/// <summary>What the gate decided, and why, in its own words.</summary>
/// <param name="Unavailable">
/// True when the gate did not review this candidate at all — unreachable, timed out,
/// empty, unparseable, or an answer that was neither APPROVE nor SKIP. False when the
/// model actually read the brief and said no.
///
/// This is a field rather than something inferred from <see cref="Reason"/> because it
/// used to be inferred from <see cref="Reason"/>, and that was a live defect.
/// TradingBotService applied <c>allow_entry_without_gate</c> only to reasons starting
/// with "Gate unreachable" or "Gate call failed". Production had that flag ON, the model
/// returned an empty answer at 2026-09-06 03:48 UTC, "Gate returned an empty answer."
/// matched neither prefix — and the entry was blocked anyway, by a rule the operator had
/// explicitly switched off. The two malformed-JSON paths and the unrecognised-decision
/// path had the same hole.
///
/// A refusal on the merits can never be overridden. That is the veto, and it stays.
/// </param>
/// <param name="LatencyMs">
/// How long the model took, in milliseconds. Zero when it was never asked.
///
/// Carried so <c>signal_outcomes.gate_latency_ms</c> stops being written as a literal
/// 0 — which it was for all 34 rows on record, making "what does the gate cost per
/// call" unanswerable from the one table built to answer questions about the gate.
/// </param>
public sealed record GateDecision(
    bool Approved, string Verdict, string Reason, bool Unavailable = false, int LatencyMs = 0)
{
    public static GateDecision Approve(string reason)  => new(true,  "APPROVED", reason);
    public static GateDecision Degraded(string reason) => new(true,  "APPROVED_DEGRADED", reason);
    public static GateDecision Ungated()               => new(true,  "NOT_GATED", "Gating is off.");

    /// <summary>The model read the brief and declined. Never overridable.</summary>
    public static GateDecision Refuse(string reason) => new(false, "REFUSED", reason);

    /// <summary>
    /// The gate could not produce a verdict. Still a refusal — every failure mode
    /// resolves to "no entry", which costs an opportunity and never a position — but one
    /// that <c>allow_entry_without_gate</c> is allowed to override, because that flag
    /// exists for exactly this case.
    /// </summary>
    public static GateDecision Unreviewed(string reason) => new(false, "REFUSED", reason, true);
}

public interface IEntryGate
{
    Task<GateDecision> ReviewAsync(EntryCandidate candidate, CancellationToken ct);
}

/// <summary>
/// The AI decides whether each proposed entry is taken.
///
/// Why the model sits here and not where it used to
/// -----------------------------------------------
/// The point of handing trading to a machine is discipline — a rule that executes
/// the same way whether the last four trades won or lost. A language model is the
/// wrong instrument for the parts of that job that need to be repeatable: it is
/// non-deterministic, it takes 45-90 seconds per generation on this hardware, and it
/// must never sit between a losing position and its stop. So sizing, stops, exits and
/// circuit breakers stay deterministic and are not reachable from here.
///
/// What a model is genuinely good at is the judgement call on a specific situation
/// that has already been reduced to evidence — and that is a real decision, not a
/// rubber stamp. So the gate holds the only veto on entry: no position is opened that
/// it has not approved. The AI decides whether to trade; the machine decides
/// everything about how.
///
/// The asymmetry is the safety property
/// ------------------------------------
/// The gate can only ever say no. It cannot propose a trade, pick a direction, change
/// a size, move a stop, or reach an exit. Every failure mode — unreachable, timed
/// out, unparseable, contradictory — resolves to "no entry", which costs an
/// opportunity and never a position. That is the opposite of the arrangement it
/// replaces, where the model's output was blended into a composite score and a wrong
/// answer moved real money in the wrong direction.
///
/// What the first audit of this gate found, and what changed because of it
/// ---------------------------------------------------------------------
/// Twenty-three hours of production log, 15 distinct signals, every one of them put
/// to the model:
///
///   • 3 approved. All three lost.
///   • 12 refused. Two of them would have won.
///   • Under the bot's real constraints the live gate scored -3.00R over the window;
///     approving everything scored +1.00R.
///   • 5 of the 12 refusals cited a premise the brief contradicted: "venues were
///     excluded for thin data" where the brief said 0 excluded, and "several
///     positions are already open" where the brief said 0 open — which it always
///     will, since the loop does not ask the gate while at the position limit.
///   • The other 8 cited "dispersion is wide" at 2.8, 4.3, 5.0, 5.8, 6.5, 6.7, 6.9
///     and 13.2 bps, against a 25 bps ceiling the scorer had already enforced. The
///     model was re-judging a check the code owns, with no scale to judge it by.
///
/// Five trades is not a verdict on the gate and this class does not treat it as one.
/// But the fabricated premises are not a sample-size question: a refusal on a number
/// the brief contradicts is a defect whichever way the trade would have gone. Two
/// things changed here in response, and both are visible in the prompt below: every
/// threshold is now stated next to its value, and each ground for refusing names the
/// arithmetic condition that makes it available.
/// </summary>
public sealed class AiEntryGate(
    OllamaAgentClient        client,
    AgentOptions             options,
    SignalOutcomeRepository  outcomes,
    GateRetrievalOptions     retrieval,
    ILogger<AiEntryGate>     log) : IEntryGate
{
    private const string SystemPrompt = """
        You are the final check on a trade that a quantitative system has already
        decided to propose. Your only job is to approve it or skip it.

        WHAT IS ALREADY DECIDED, AND NOT YOURS TO CHANGE
        The direction, the position size, the stop price and the target price were all
        set by the system before you were asked. You cannot alter them, and you cannot
        propose a different trade. You answer one question: take this one, or skip it.

        WHAT IS ALREADY CHECKED IN CODE — DO NOT RE-JUDGE IT
        Reward:risk, fees, stop distance, position size, cross-venue dispersion and
        per-venue data sufficiency were all computed and checked before you were
        called. A candidate that failed any of them never reaches you. Each is shown
        below with the threshold that was applied and how far the candidate sits from
        it. None of them is a reason to skip on its own: at 1.5:1 a 40% win rate is
        already profitable, and you cannot judge a ratio better than the arithmetic
        that produced it.

        EVERY CLAIM YOU MAKE MUST BE TRUE OF A NUMBER IN THE BRIEF
        Do not state a fact the brief does not contain, and do not state the opposite
        of one it does. Each ground below names the condition that makes it available
        to you. If the condition is not met, that ground does not exist for this
        trade, however plausible the sentence sounds.

        THE ONLY GROUNDS FOR SKIPPING
        Each is marked AVAILABLE or NOT AVAILABLE in the brief, computed from this
        account's own closed trades. Read the marker; do not decide for yourself whether
        a condition is met.
        - This setup is losing: the brief shows this setup's own closed trades cut three
          ways — the exact cell (same rule, side and entry path), the same rule in the
          same session of day, and the rule overall. The ground is available when ANY
          slice with at least 5 closed trades has a negative mean R. Read the counts:
          a slice marked too thin is not evidence either way, and a narrow slice with
          5 trades is weaker than a wide one with 20 even when both are negative.
          This is the strongest ground you have, because it is this account's own result
          rather than a view about the market.
        - Trend against the entry: the brief marks the 4-hour move as running against the
          direction proposed. This rule enters on a short-horizon pattern and has no view
          on the larger move; entering a LONG into a sustained fall is buying a knife.
        - One event twice: the brief shows the same entry path already fired within the
          last 2 hours. A second entry on the same print is not a second opportunity, it
          is the same one at a worse price, and the position limits do not catch it
          because the first trade may already have closed.
        - Concentration: the brief shows a position ALREADY OPEN on this side, counted
          across every strategy. The per-strategy limits do not see this, so it is
          genuinely yours to weigh.

        HOW TO WEIGH THEM
        A ground being available does not mean you must skip. One marginal reading is
        usually not enough; two or more pointing the same way usually is. That judgement
        — several weak signals together — is the only thing you are here for, because
        anything decided by a single threshold is already decided in code before you are
        asked.

        Approve when the evidence is coherent and the trade is proportionate. Do not skip
        because nothing looks exciting: with no ground available, approve.

        SIMILAR PAST SIGNALS
        The brief may list the closest past setups and what the market did to them.
        They are evidence about this kind of situation, not a rule: a run of losses in
        a small sample is weak evidence, and the brief tells you how large the sample
        is. Never cite them as your only reason.

        CALIBRATION
        You are not being asked to predict the market. The system's edge, if it has
        one, is statistical and plays out over many trades. Your job is to catch the
        individual case that is obviously worse than the average one.

        Most candidates should be approved, because most of them will have no ground
        available. But a gate that has never once skipped is not being careful, it is
        being ornamental — and this one approved 45 of 45 before the grounds above were
        made reachable. When two grounds are marked AVAILABLE, skipping is the expected
        answer, not a bold one.

        OUTPUT
        Reply with a single JSON object and nothing else:
        {"decision": "APPROVE" | "SKIP", "reason": "one sentence, citing a number"}
        """;

    public async Task<GateDecision> ReviewAsync(EntryCandidate candidate, CancellationToken ct)
    {
        if (!await client.IsAvailableAsync(options.Model, ct))
            return GateDecision.Unreviewed(
                $"Gate unreachable: Ollama is not serving {options.Model}.");

        var examples = await RetrieveSimilarAsync(candidate, ct);

        var brief = Describe(candidate, examples);

        // The brief is the entire input to the decision and nothing recorded it. Every
        // audit of this gate so far — six of them — has had to rebuild the brief by hand
        // from flow_bars_15m and bot_trades to find out whether a cited number was real.
        // The exit reviewer logs its brief for the same reason; appsettings raises this
        // namespace to Debug so both actually emit.
        log.LogDebug("[Gate] Brief for {Strategy} {Side}:\n{Brief}",
            candidate.Strategy, candidate.Side, brief);

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
            new JsonObject { ["role"] = "user",   ["content"] = brief },
        };

        OllamaAgentClient.ChatTurn? turn;
        var clock = Stopwatch.StartNew();
        try
        {
            // No tools array. The gate is a single question with a single answer, and
            // giving it tools would give it a way to act — which is precisely what the
            // asymmetry in this class exists to prevent.
            turn = await client.ChatAsync(
                options.Model, messages, tools: new JsonArray(),
                temperature: options.Temperature, numCtx: options.NumCtx, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return GateDecision.Unreviewed($"Gate call failed: {ex.Message}");
        }

        var latencyMs = (int)clock.ElapsedMilliseconds;

        log.LogDebug("[Gate] Model answered in {Ms} ms with {Count} retrieved case(s) in the brief.",
            latencyMs, examples.Count);

        if (string.IsNullOrWhiteSpace(turn?.Content))
            return GateDecision.Unreviewed("Gate returned an empty answer.") with { LatencyMs = latencyMs };

        return Parse(turn.Content, candidate) with { LatencyMs = latencyMs };
    }

    /// <summary>
    /// The closest past signals on this side that had already resolved when this one
    /// fired, or nothing at all.
    ///
    /// Three properties make this safe to put in front of a model that can veto real
    /// trades:
    ///
    ///   • No lookahead. The repository only returns cases whose own outcome landed
    ///     before this signal's timestamp. Without that bound, every backtest of this
    ///     retrieval would be reading tomorrow's newspaper and would look excellent
    ///     until it ran live.
    ///   • A floor on the evidence. Below <see cref="GateRetrievalOptions.MinDecidedSignals"/>
    ///     resolved signals in the table, nothing is retrieved at all. Three losing
    ///     neighbours drawn from a table of eleven rows is not a base rate, it is an
    ///     anecdote, and an anecdote in a prompt is an anchor.
    ///   • It cannot refuse anything. A failure here logs and returns empty; the gate
    ///     then decides exactly as it did before this feature existed. A research
    ///     query must never be able to stop a trade.
    /// </summary>
    private async Task<IReadOnlyList<SimilarCase>> RetrieveSimilarAsync(
        EntryCandidate c, CancellationToken ct)
    {
        if (!retrieval.Enabled) return [];

        try
        {
            var coverage = await outcomes.GetCoverageAsync(c.Symbol, ct);

            if (coverage.Decided < retrieval.MinDecidedSignals)
            {
                log.LogDebug(
                    "[Gate] {Decided} resolved signal(s) on record, below the {Floor} needed to " +
                    "retrieve examples. Deciding without them.",
                    coverage.Decided, retrieval.MinDecidedSignals);
                return [];
            }

            return await outcomes.FindSimilarAsync(
                symbol:              c.Symbol,
                side:                c.Side,
                // Scoped to the rule that proposed this. Without it a price rule was
                // shown a flow rule's outcomes and told they were similar setups.
                strategy:            c.Strategy,
                atrPct:              c.Geometry.AtrPctUsed,
                aggregateOfi:        c.Flow.AggregateOfi,
                stopPct:             c.Geometry.StopPct,
                asOfUtc:             DateTime.UtcNow,
                // The axis that actually separates one setup of this rule from another.
                // See sql/039: without it the distance collapsed to ATR for
                // CandleReversal, whose stop_pct and aggregate_ofi are single-valued.
                triggerValue:        c.Flow.TriggerValue,
                k:                   retrieval.Examples,
                ct:                  ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex,
                "[Gate] Could not retrieve similar past signals; deciding on the brief alone.");
            return [];
        }
    }

    /// <summary>
    /// Render the candidate as the evidence a reader would need to second-guess it.
    ///
    /// Per-venue z-scores are listed individually rather than summarised, because the
    /// failure this is meant to catch is a headline consensus resting on one venue
    /// while the others were excluded — and that is invisible in an aggregate.
    ///
    /// Every checked quantity is written as "value against threshold", not as a bare
    /// value. That is the fix for the largest single failure mode this gate has
    /// shown: eight refusals in one day for "wide dispersion" at 2.8-13.2 bps, when
    /// the scorer's own ceiling is 25 bps and anything above it never reaches the
    /// model. A number with no scale invites the model to supply one from nowhere.
    /// </summary>
    private static string Describe(EntryCandidate c, IReadOnlyList<SimilarCase> examples)
    {
        var flow = c.Flow;

        var g = c.Geometry;

        // Each of these is one of the four grounds for refusing, rendered so the
        // condition attached to that ground can be evaluated by reading one line.
        var dispersionShare = c.MaxDispersionBps > 0
            ? $"{flow.DispersionBps / c.MaxDispersionBps:P0} of the {c.MaxDispersionBps:F1} bps ceiling"
            : "no ceiling configured";

        var e = c.Evidence;

        // The cell's own record, named so the model cannot mistake it for the strategy's.
        // "CANDLE_REVERSAL SHORT" and "CANDLE_REVERSAL LONG" are different trades and the
        // first has one observation against the second's twenty-nine.
        // One line per slice, narrowest first, each with its own n so the model can see
        // which of them is worth anything. A slice under MinCellTrades is printed with its
        // count and explicitly marked as too thin rather than left out — "no evidence" and
        // "evidence that says nothing" are different, and hiding the thin one invites the
        // model to assume the wider slice is the narrow one.
        static string Slice(string label, int n, int wins, decimal meanR) =>
            n == 0
                ? $"{label,-34} no closed trades yet"
                : $"{label,-34} {n,3} closed, {wins,3} won, mean R {meanR:+0.000;-0.000}" +
                  (n < GateEvidence.MinCellTrades ? "   (too thin to read)" : "");

        var inUs = GateEvidence.IsUsSession(DateTime.UtcNow);

        var cell = Slice(
            $"{c.Strategy} {c.Side}{(string.IsNullOrEmpty(flow.EntryPath) ? "" : $" via {flow.EntryPath}")}",
            e.CellTrades, e.CellWins, e.CellMeanR);

        var session = Slice(
            $"{c.Strategy} in {(inUs ? "12-20 UTC" : "20-12 UTC")}",
            e.SessionTrades, e.SessionWins, e.SessionMeanR);

        var rule = Slice($"{c.Strategy} overall", e.RuleTrades, e.RuleWins, e.RuleMeanR);

        var lossLimitUsd = c.CapitalUsd * c.DailyLossLimitPct;
        var lossShare    = lossLimitUsd > 0
            ? $"{Math.Max(0m, -c.TodayPnlUsd) / lossLimitUsd:P0} of the ${lossLimitUsd:F2} daily loss limit"
            : "no daily loss limit configured";

        return $"""
            PROPOSED ENTRY
              {c.Side} {c.Symbol} at {c.Price:F4}, notional ${c.NotionalUsd:F2}

            EXIT LEVELS (already set, not yours to change)
              stop   {g.StopPrice:F4}  ({g.StopPct:P2} away)
              target {g.TargetPrice:F4}  ({g.TargetPct:P2} away)
              reward:risk after fees {g.RewardRisk:F2}:1 (VALIDATED IN CODE — context only,
                never a reason to skip; breakeven win rate {1m / (1m + g.RewardRisk):P1})
              stop basis: {g.Basis}, from ATR {g.AtrPctUsed:F2}% of price

            EVIDENCE — {(flow.ParticipatingVenues == 0
                ? "price only (this rule reads no order flow at all)"
                : "aggregate aggressive order flow (this rule does not score venues)")}
            {(
                // NEITHER surviving rule scores venues. The per-venue branch that used to
                // sit here was unreachable: VenueVote was never constructed by anything,
                // so Votes was always empty, PER VENUE always rendered as a blank section,
                // and the excluded count was always null. It went with the ZScore rule on
                // 2026-09-18 and the shape it left behind was deleted on 2026-09-19.
                //
                // A rule that reads no flow at all reports ParticipatingVenues 0, and
                // running the imbalance arithmetic on it produced "1.00:1 sell-dominated
                // (OFI +0.000) across 0 venue(s)" — a sentence with a direction, a ratio
                // and a venue count in it, every one of them invented from a record that
                // was never filled in. CANDLE_REVERSAL reads price and nothing else, so
                // what it says about price is the whole of its evidence.
                flow.ParticipatingVenues == 0
                    ? $"  {flow.Reason}\n" +
                      "  This rule reads PRICE ONLY. Order flow, z, venue agreement and\n" +
                      "  dispersion are not consulted — absent, not zero — and are not grounds\n" +
                      "  for anything."
                    : $"  imbalance {(1.0 + Math.Abs(flow.AggregateOfi)) / (1.0 - Math.Abs(flow.AggregateOfi)):F2}:1 " +
                      $"{(flow.AggregateOfi > 0 ? "buy" : "sell")}-dominated " +
                      $"(OFI {flow.AggregateOfi:+0.000;-0.000}) across {flow.ParticipatingVenues} venue(s)\n" +
                      $"  entry path: {(string.IsNullOrEmpty(flow.EntryPath) ? "n/a" : flow.EntryPath)}\n" +
                      "  z, venue agreement and dispersion are NOT computed by this rule. They are\n" +
                      "  absent, not zero, and are not grounds for anything.")}

            THE FOUR GROUNDS, EACH MARKED FROM THIS ACCOUNT'S OWN CLOSED TRADES
              this setup's own record, measured over its last closed trades:
                {cell}
                {session}
                {rule}
                                {(e.CellIsLosing
                                    ? "at least one slice with enough trades is NEGATIVE — " +
                                      "'this setup is losing' is AVAILABLE as a ground"
                                    : e.CellTrades < GateEvidence.MinCellTrades
                                   && e.SessionTrades < GateEvidence.MinCellTrades
                                        ? $"no slice has reached {GateEvidence.MinCellTrades} closed " +
                                          "trades yet, so 'this setup is losing' is NOT AVAILABLE"
                                        : "every slice with enough trades is positive — " +
                                          "'this setup is losing' is NOT AVAILABLE")}
              4-hour move       {e.Move4hPct,6:+0.00;-0.00}%   (12-hour {e.Move12hPct:+0.00;-0.00}%)
                                {(e.TrendAgainst(c.Side)
                                    ? $"the market has moved {e.Move4hPct:+0.00;-0.00}% against a {c.Side} " +
                                      $"over four hours, past the {GateEvidence.TrendPct:F1}% that counts as " +
                                      "a trend — 'trend against the entry' is AVAILABLE as a ground"
                                    : $"inside the {GateEvidence.TrendPct:F1}% that counts as a trend, or " +
                                      "running with this side — 'trend against the entry' is NOT AVAILABLE")}
              same path fired   {(e.MinutesSinceSamePath < 0 ? "  never" : $"{e.MinutesSinceSamePath,6:F0} min ago")}
                                {(e.ClusteredPath
                                    ? $"inside the {GateEvidence.ClusterMinutes:F0} min that makes this one " +
                                      "event twice — 'one event twice' is AVAILABLE as a ground"
                                    : "no recent entry on this path — 'one event twice' is NOT AVAILABLE")}
              open on this side {c.OpenSameSide,6}       (across ALL strategies, not just this one)
                                {(c.OpenSameSide >= 1
                                    ? "already exposed in this direction — 'concentration' is AVAILABLE " +
                                      "as a ground"
                                    : "nothing open on this side — 'concentration' is NOT AVAILABLE")}

            CONTEXT, NOT GROUNDS — already checked in code, never a reason to skip
              dispersion        {flow.DispersionBps,6:F1} bps   — {dispersionShare}
              today's P&L       ${c.TodayPnlUsd,6:F2}   — {lossShare}
              open, this rule   {c.OpenPositions,6}       — limit {c.MaxOpenPositions}

            ACCOUNT
              capital ${c.CapitalUsd:F2}, open positions {c.OpenPositions} of {c.MaxOpenPositions}
              realised P&L today: ${c.TodayPnlUsd:F2}
            {DescribeRecentTrades(c)}
            {DescribeExamples(examples, flow.TriggerValue)}
            Approve or skip.
            """;
    }

    /// <summary>
    /// The retrieved neighbourhood, with its base rate stated rather than left to be
    /// counted.
    ///
    /// The base rate is the whole value of this block, so it is computed here instead
    /// of being implied by a list — a model that miscounts five rows would otherwise
    /// invent a statistic. Wins and losses are deliberately not balanced: if eleven of
    /// twelve similar setups lost, eleven losses is the true neighbourhood and
    /// presenting a tidy half-and-half sample would be manufacturing a different one.
    /// </summary>
    /// <summary>
    /// This rule's last few closed trades, in order, newest first.
    ///
    /// The slices above are means, and a mean hides the one thing the operator most wants
    /// noticed: that the previous trade from this same rule just lost. On 2026-09-20 the
    /// gate approved a CANDLE_REVERSAL long while the previous CANDLE_REVERSAL long — 98
    /// minutes earlier — had stopped out at -1.12R seventeen seconds before the evidence
    /// query ran. "One event twice" was marked available; how the event ended was nowhere.
    ///
    /// Rendered as raw rows rather than another statistic, because a sequence is something
    /// a reader can judge and a mean is something they have to trust. Five lines is about
    /// sixty tokens.
    /// </summary>
    private static string DescribeRecentTrades(EntryCandidate c)
    {
        if (c.RecentTrades is not { Count: > 0 } recent) return "";

        var sb = new StringBuilder(
            $"\nTHIS RULE'S LAST {recent.Count} CLOSED TRADE(S), NEWEST FIRST — what just happened\n");

        foreach (var t in recent)
            sb.AppendLine(
                $"  {t.ClosedAt:MM-dd HH:mm}Z  {t.Side,-5} {t.CloseReason,-13} {t.R,6:+0.00;-0.00}R");

        // Stated as a count rather than left for the model to tally. It has miscounted a
        // five-row list before, and an invented statistic here would be a fabricated
        // premise of exactly the kind the prompt forbids.
        var losers = recent.Count(t => t.R < 0m);
        sb.AppendLine(
            losers == recent.Count
                ? $"  ALL {recent.Count} of the last {recent.Count} lost."
                : $"  {losers} of the last {recent.Count} lost.");

        return sb.ToString();
    }

    private static string DescribeExamples(IReadOnlyList<SimilarCase> examples, double candidateTrigger)
    {
        if (examples.Count == 0) return "";

        var wins   = examples.Count(e => e.Outcome == "WIN");
        var losses = examples.Count(e => e.Outcome == "LOSS");
        var flat   = examples.Count - wins - losses;

        var sb = new StringBuilder("\nSIMILAR PAST SIGNALS ON THIS SIDE (already resolved before now)\n");

        // The three fields that used to print here — aggregate z, the venue tally and
        // dispersion — are structurally 0 under both surviving rules, so every line read
        // "z=+0.00  0/3 venues  disp 0.0bps" and the model was handed three constants
        // dressed as evidence. What replaces them is the one number that actually differs
        // between two setups of the same rule: how hard it fired. See sql/039.
        foreach (var e in examples)
            sb.Append("  ")
              .Append($"{e.SignalAt:MM-dd HH:mm}Z  ")
              .Append(e.TriggerValue is { } tv
                          ? $"fired at {tv,5:F2}  "
                          : "strength not recorded  ")
              .Append($"-> {e.Outcome}")
              .Append(e.MinutesToOutcome is { } m ? $" after {m} min" : "")
              .Append(e.OutcomeR != 0m ? $" ({e.OutcomeR:+0.00;-0.00}R)" : "")
              .AppendLine();

        sb.AppendLine($"  Of these {examples.Count}: {wins} win, {losses} loss, {flat} neither. " +
                      $"This is a small sample from one instrument — weak evidence, not a rule.");

        // The losing ones, called out by name. The list above is ordered by similarity and
        // a run of losses inside it is easy to skim past; this is the question the operator
        // actually wants the gate asking — "has this kind of setup already lost money on
        // this account, and how much".
        if (losses > 0)
        {
            var lost = examples.Where(e => e.Outcome == "LOSS").ToList();
            sb.AppendLine(
                $"  OF THE CLOSEST {examples.Count}, {losses} LOST: " +
                string.Join(", ", lost.Select(e =>
                    e.TriggerValue is { } t
                        ? $"{t:F2} -> {e.OutcomeR:+0.00;-0.00}R"
                        : $"{e.OutcomeR:+0.00;-0.00}R")) +
                $". The candidate is at {candidateTrigger:F2}.");
        }

        return sb.ToString();
    }

    private GateDecision Parse(string content, EntryCandidate candidate)
    {
        // Small models wrap JSON in prose or a fence often enough that finding the
        // object is part of parsing rather than an error case.
        var start = content.IndexOf('{');
        var end   = content.LastIndexOf('}');

        if (start < 0 || end <= start)
            return GateDecision.Unreviewed(
                $"Gate answer was not JSON: \"{Truncate(content)}\"");

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(content[start..(end + 1)]);
        }
        catch (JsonException ex)
        {
            return GateDecision.Unreviewed($"Gate answer was malformed JSON: {ex.Message}");
        }

        var decision = node?["decision"]?.ToString()?.Trim().ToUpperInvariant() ?? "";
        var reason   = node?["reason"]?.ToString()?.Trim() ?? "";

        if (string.IsNullOrEmpty(reason)) reason = "(no reason given)";

        switch (decision)
        {
            case "APPROVE":
                log.LogInformation(
                    "[Gate] APPROVED {Side} {Symbol}: {Reason}",
                    candidate.Side, candidate.Symbol, reason);
                return GateDecision.Approve(reason);

            case "SKIP":
                // A REFUSAL ON A PREMISE THE BRIEF CONTRADICTS IS NOT A REVIEW, AND NO
                // LONGER STANDS AS ONE.
                //
                // This used to log and let the refusal through, on the argument that the
                // class must not overrule the veto or the veto is not a veto. That was
                // right about the veto and wrong about what had happened: the prompt's one
                // hard rule is that every claim must be true of a number in the brief, and
                // a reason that breaks it is evidence the brief was not read. There is no
                // veto to protect, because nothing was reviewed.
                //
                // Measured over the 23 hours to 2026-09-21 13:20: the gate refused 14 of
                // 14 signals and the bot took ZERO trades. Five of the seven
                // CANDLE_REVERSAL refusals cited a negative mean R while all three slices
                // were positive — two of them literally reported "a negative mean R of
                // +0.165", and one reported "-0.165" where the brief said +0.165. One
                // simply quoted the marker text back: "shows 'this setup is losing' is
                // available".
                //
                // Downgraded to Unreviewed rather than to an approval. That keeps it a
                // refusal, leaves `allow_entry_without_gate` as the thing that decides
                // whether the entry proceeds, and records it as APPROVED_DEGRADED so these
                // are countable and separable from real approvals. A refusal whose premise
                // CHECKS OUT is untouched and remains absolute.
                if (ContradictsBrief(reason, candidate) is { } contradiction)
                {
                    log.LogWarning(
                        "[Gate] {Side} {Symbol} was SKIPPED on a premise the brief contradicts, " +
                        "so it is recorded as UNREVIEWED rather than refused: {Detail} " +
                        "Reason given: {Reason}",
                        candidate.Side, candidate.Symbol, contradiction, reason);

                    return GateDecision.Unreviewed(
                        $"Gate answered SKIP on a premise the brief contradicts ({contradiction}) " +
                        $"Its stated reason was: {reason}");
                }

                log.LogInformation(
                    "[Gate] SKIPPED {Side} {Symbol} (flow z={Z:F2}, {Agree}/{Part} venues): {Reason}",
                    candidate.Side, candidate.Symbol, candidate.Flow.AggregateZ,
                    candidate.Flow.AgreeingVenues, candidate.Flow.ParticipatingVenues, reason);
                return GateDecision.Refuse(reason);

            default:
                // Anything other than the two permitted answers is refused rather than
                // guessed at. A model that replied "MAYBE" has not approved the trade,
                // and coercing an ambiguous answer into an approval is how a safety
                // check turns into a formality.
                return GateDecision.Unreviewed(
                    $"Gate gave an unrecognised decision \"{decision}\" — treating as no. " +
                    $"Reason field said: {Truncate(reason)}");
        }
    }

    /// <summary>
    /// Whether the stated reason asserts something the brief says is false, and what.
    ///
    /// Only the two grounds that reduce to a count are checked, because only those
    /// have an unambiguous answer. "The evidence is incoherent" is a judgement and is
    /// not checkable; "venues were excluded" is arithmetic and was wrong twice in one
    /// day.
    ///
    /// THE EXCLUDED COUNT IS DERIVED THE SAME WAY <see cref="Describe"/> DERIVES IT, and
    /// was not until this was fixed. Both read <c>Votes.Count - ParticipatingVenues</c>;
    /// Describe was corrected for the rule that does not score venues, and this one was
    /// left behind. Under FlowRatio that expression is <c>0 - 3 = -3</c>, so
    /// <c>excluded == 0</c> never held and the detector was dead for the only rule that
    /// trades — the exact defect it exists to catch, in the code that catches it.
    /// </summary>
    private static string? ContradictsBrief(string reason, EntryCandidate c)
    {
        var text = reason.ToLowerInvariant();

        // Citing an excluded venue is now a fabricated premise unconditionally: no rule in
        // this build scores venues, so no venue can ever have been excluded. There is no
        // count to compare against any more, which is a stronger statement than the one
        // this check used to make.
        if (text.Contains("excluded") || text.Contains("thin data"))
            return "it cites excluded venues, and no rule in this build scores venues at " +
                   "all — the brief does not contain an excluded count, and none could be " +
                   "above zero.";

        if ((text.Contains("already open") || text.Contains("positions are open")) && c.OpenSameSide < 1)
            return $"it cites open positions, and the brief showed {c.OpenSameSide} on this side.";

        // Dispersion is no longer a ground at all, so citing it is a fabricated premise
        // whatever the number says — not merely a misread of the scale.
        if (text.Contains("dispersion"))
            return "it cites dispersion, which is listed under CONTEXT and is not a ground " +
                   "for skipping under any value.";

        var e = c.Evidence;

        // The three new grounds, checked the same way and for the same reason: each
        // reduces to a comparison the brief already printed, so a reason that asserts
        // the opposite is asserting something the model was shown to be false.
        // Every phrasing the model has actually produced for this ground, not just the one
        // it used first. A probe caught "a negative mean R of -0.165 over the last 19 closed
        // trades", which contains none of the original three words; production happened to
        // append ", indicating a losing setup" so it matched by luck. Matching by luck is
        // how the exit detector missed its first real case one day earlier.
        if ((text.Contains("losing") || text.Contains("base rate") || text.Contains("track record")
          || text.Contains("negative mean") || text.Contains("negative r")
          || text.Contains("lost money") || text.Contains("has lost"))
            && !e.AnySliceIsLosing)
            // All THREE slices are enumerated. The rule slice was missing from both this
            // message and CellIsLosing until 2026-09-20, which produced a warning that
            // listed cell and session, called the setup positive, and was contradicted by
            // the rule line printed three lines above it in the same brief.
            return e.CellTrades    < GateEvidence.MinCellTrades
                && e.SessionTrades < GateEvidence.MinCellTrades
                && e.RuleTrades    < GateEvidence.MinCellTrades
                ? $"it cites this setup's record, and no slice has reached " +
                  $"{GateEvidence.MinCellTrades} closed trades — cell {e.CellTrades}, session " +
                  $"{e.SessionTrades}, rule {e.RuleTrades}, so that ground does not exist yet."
                : $"it cites this setup as losing, and every slice with enough trades is " +
                  $"positive: cell {e.CellMeanR:+0.000;-0.000} over {e.CellTrades}, session " +
                  $"{e.SessionMeanR:+0.000;-0.000} over {e.SessionTrades}, rule " +
                  $"{e.RuleMeanR:+0.000;-0.000} over {e.RuleTrades}.";

        if (text.Contains("trend") && !e.TrendAgainst(c.Side))
            return $"it cites the trend, and the brief showed a 4-hour move of " +
                   $"{e.Move4hPct:+0.00;-0.00}% — inside the {GateEvidence.TrendPct:F1}% that " +
                   $"counts as one, or running with the {c.Side}.";

        if ((text.Contains("recent entry") || text.Contains("same path") || text.Contains("twice"))
            && !e.ClusteredPath)
            return e.MinutesSinceSamePath < 0
                ? "it cites a recent entry on this path, and the brief showed none in 24 hours."
                : $"it cites a recent entry on this path, and the brief showed the last one " +
                  $"{e.MinutesSinceSamePath:F0} min ago — past the " +
                  $"{GateEvidence.ClusterMinutes:F0} min window.";

        return null;
    }

    private static string Truncate(string s) =>
        s.Length <= 160 ? s.Replace('\n', ' ') : s[..160].Replace('\n', ' ') + "…";
}

/// <summary>
/// Whether the gate is shown past cases, and how many.
///
/// Separate from <see cref="AgentOptions"/> because these are research knobs and
/// those are connection knobs, and because turning retrieval off has to be a
/// one-line config change: it is the newest thing that can influence a live veto,
/// so it is the first thing to switch off if the gate starts behaving oddly.
/// </summary>
public sealed class GateRetrievalOptions
{
    public const string Section = "GateRetrieval";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Five is enough to show a base rate and short enough that the model reads all
    /// of it. Long example lists in a small model's context push the actual brief out
    /// of attention, which trades the decision for the examples.
    /// </summary>
    public int Examples { get; set; } = 5;

    /// <summary>
    /// Nothing is retrieved until the table holds this many resolved signals.
    ///
    /// The floor exists because the alternative is anchoring: with eleven rows on
    /// record, "the three closest past setups all lost" is one bad afternoon rendered
    /// as a statistic, and a model shown three losses will refuse. Twenty is not a
    /// statistically meaningful sample either — the floor for drawing conclusions is
    /// 200, stated in OutcomeCoverage — it is the point at which a neighbourhood is
    /// worth showing as context rather than as noise. At ~15 signals a day this is
    /// reached in under two days.
    /// </summary>
    public int MinDecidedSignals { get; set; } = 20;
}
