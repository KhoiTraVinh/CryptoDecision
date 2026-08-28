using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// The four threshold fields are not decoration. Each corresponds to one of the four
/// grounds the gate is allowed to refuse on, and each was added after the model cited
/// that ground against a number that did not support it — refusing on "dispersion is
/// wide" at 2.8 bps against a 25 bps ceiling it had already passed. A value without
/// its scale is not evidence, and the model was being handed values without scales.
/// </summary>
public sealed record EntryCandidate(
    string        Symbol,
    string        Side,
    decimal       Price,
    FlowVerdict   Flow,
    StopGeometry  Geometry,
    decimal       NotionalUsd,
    int           OpenPositions,
    decimal       TodayPnlUsd,
    decimal       CapitalUsd          = 0m,
    decimal       DailyLossLimitPct   = 0m,
    double        MaxDispersionBps    = 0.0,
    int           MaxOpenPositions    = 0);

/// <summary>What the gate decided, and why, in its own words.</summary>
public sealed record GateDecision(bool Approved, string Verdict, string Reason)
{
    public static GateDecision Approve(string reason)  => new(true,  "APPROVED", reason);
    public static GateDecision Degraded(string reason) => new(true,  "APPROVED_DEGRADED", reason);
    public static GateDecision Refuse(string reason)   => new(false, "REFUSED", reason);
    public static GateDecision Ungated()               => new(true,  "NOT_GATED", "Gating is off.");
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
        - Thin evidence: the brief shows EXCLUDED VENUES greater than zero AND
          agreement at the bare minimum. A venue that participated and did not reach
          the threshold was NOT excluded — it was counted, it disagreed, and the
          aggregate already reflects that. When excluded is 0 this ground is
          unavailable.
        - Late entry: the brief shows dispersion at 80% or more of its ceiling. Below
          that the market is not dislocated and the entry is not late. Dispersion at a
          fifth of the ceiling is a narrow market, and calling it wide is refusing on
          a number that says the opposite.
        - Adding to a losing day: today's realised loss is at least half the daily loss
          limit shown. A loss smaller than that is a normal outcome of trading, not a
          reason to stop.
        - Concentration: OPEN POSITIONS in the brief is 2 or more. You are not asked
          at all while the position limit is reached, so this is normally 0 and this
          ground is normally unavailable.

        Approve when the evidence is coherent and the trade is proportionate. A clean
        setup deserves approval — being reflexively cautious is not the same as being
        careful, and skipping every trade makes you useless rather than safe.

        SIMILAR PAST SIGNALS
        The brief may list the closest past setups and what the market did to them.
        They are evidence about this kind of situation, not a rule: a run of losses in
        a small sample is weak evidence, and the brief tells you how large the sample
        is. Never cite them as your only reason.

        CALIBRATION
        You are not being asked to predict the market. The system's edge, if it has
        one, is statistical and plays out over many trades. Your job is to catch the
        individual case that is obviously worse than the average one. Expect to
        approve most proposals.

        OUTPUT
        Reply with a single JSON object and nothing else:
        {"decision": "APPROVE" | "SKIP", "reason": "one sentence, citing a number"}
        """;

    public async Task<GateDecision> ReviewAsync(EntryCandidate candidate, CancellationToken ct)
    {
        if (!await client.IsAvailableAsync(options.Model, ct))
            return GateDecision.Refuse(
                $"Gate unreachable: Ollama is not serving {options.Model}.");

        var examples = await RetrieveSimilarAsync(candidate, ct);

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
            new JsonObject { ["role"] = "user",   ["content"] = Describe(candidate, examples) },
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
            return GateDecision.Refuse($"Gate call failed: {ex.Message}");
        }

        log.LogDebug("[Gate] Model answered in {Ms} ms with {Count} retrieved case(s) in the brief.",
            clock.ElapsedMilliseconds, examples.Count);

        if (string.IsNullOrWhiteSpace(turn?.Content))
            return GateDecision.Refuse("Gate returned an empty answer.");

        return Parse(turn.Content, candidate);
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
                aggregateZ:          c.Flow.AggregateZ,
                agreeingVenues:      c.Flow.AgreeingVenues,
                participatingVenues: c.Flow.ParticipatingVenues,
                dispersionBps:       c.Flow.DispersionBps,
                stopPct:             c.Geometry.StopPct,
                asOfUtc:             DateTime.UtcNow,
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

        var venues = string.Join("\n", flow.Votes.Select(v => v.Participated
            ? $"  {v.Exchange,-8} z={v.Z,+6:F2}  OFI {v.Ofi,+6:F3} (its median {v.OfiMedian,+6:F3})  " +
              $"${v.VolumeUsd,14:N0}  {v.TradeCount,7:N0} prints  " +
              $"concentration {v.Concentration:P1}{(v.Agreed ? "   <-- agrees" : "")}"
            : $"  {v.Exchange,-8} EXCLUDED: {v.ExclusionReason}"));

        var g        = c.Geometry;
        var excluded = flow.Votes.Count - flow.ParticipatingVenues;

        // Each of these is one of the four grounds for refusing, rendered so the
        // condition attached to that ground can be evaluated by reading one line.
        var dispersionShare = c.MaxDispersionBps > 0
            ? $"{flow.DispersionBps / c.MaxDispersionBps:P0} of the {c.MaxDispersionBps:F1} bps ceiling"
            : "no ceiling configured";

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

            EVIDENCE — cross-venue aggressive order flow
              aggregate z {flow.AggregateZ:+0.00;-0.00}, OFI {flow.AggregateOfi:+0.000;-0.000}
              {flow.AgreeingVenues} of {flow.ParticipatingVenues} participating venues agree
              venues that participated but did not reach the threshold: {flow.ParticipatingVenues - flow.AgreeingVenues}

            CHECKS ALREADY PASSED IN CODE, with the threshold each was judged against
              dispersion        {flow.DispersionBps,6:F1} bps   — {dispersionShare}
                                {(c.MaxDispersionBps <= 0
                                    ? "no dispersion limit is in force — 'late entry' is NOT available as a ground"
                                    : flow.DispersionBps >= 0.8 * c.MaxDispersionBps
                                        ? "AT OR NEAR THE CEILING — 'late entry' is available as a ground"
                                        : "well inside the ceiling — 'late entry' is NOT available as a ground")}
              excluded venues   {excluded,6}       — {(excluded > 0
                                    ? "above zero — 'thin evidence' is available as a ground"
                                    : "zero — 'thin evidence' is NOT available as a ground")}
              open positions    {c.OpenPositions,6}       — limit {c.MaxOpenPositions}; {(c.OpenPositions >= 2
                                    ? "'concentration' is available as a ground"
                                    : "below 2, so 'concentration' is NOT available as a ground")}
              today's P&L       ${c.TodayPnlUsd,6:F2}   — {lossShare}; {(lossLimitUsd > 0 && -c.TodayPnlUsd >= lossLimitUsd / 2
                                    ? "'losing day' is available as a ground"
                                    : "'losing day' is NOT available as a ground")}

            PER VENUE
            {venues}

            ACCOUNT
              capital ${c.CapitalUsd:F2}, open positions {c.OpenPositions} of {c.MaxOpenPositions}
              realised P&L today: ${c.TodayPnlUsd:F2}
            {DescribeExamples(examples)}
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
    private static string DescribeExamples(IReadOnlyList<SimilarCase> examples)
    {
        if (examples.Count == 0) return "";

        var wins   = examples.Count(e => e.Outcome == "WIN");
        var losses = examples.Count(e => e.Outcome == "LOSS");
        var flat   = examples.Count - wins - losses;

        var sb = new StringBuilder("\nSIMILAR PAST SIGNALS ON THIS SIDE (already resolved before now)\n");

        foreach (var e in examples)
            sb.Append("  ")
              .Append($"{e.SignalAt:MM-dd HH:mm}Z  z={e.AggregateZ,+5:F2}  {e.AgreeingVenues}/{e.ParticipatingVenues} venues  ")
              .Append($"disp {e.DispersionBps,4:F1}bps  stop {e.StopPct:P2}  -> {e.Outcome}")
              .Append(e.MinutesToOutcome is { } m ? $" after {m} min" : "")
              .Append(e.OutcomeR != 0m ? $" ({e.OutcomeR:+0.00;-0.00}R)" : "")
              .AppendLine();

        sb.AppendLine($"  Of these {examples.Count}: {wins} win, {losses} loss, {flat} neither. " +
                      $"This is a small sample from one instrument — weak evidence, not a rule.");

        return sb.ToString();
    }

    private GateDecision Parse(string content, EntryCandidate candidate)
    {
        // Small models wrap JSON in prose or a fence often enough that finding the
        // object is part of parsing rather than an error case.
        var start = content.IndexOf('{');
        var end   = content.LastIndexOf('}');

        if (start < 0 || end <= start)
            return GateDecision.Refuse(
                $"Gate answer was not JSON: \"{Truncate(content)}\"");

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(content[start..(end + 1)]);
        }
        catch (JsonException ex)
        {
            return GateDecision.Refuse($"Gate answer was malformed JSON: {ex.Message}");
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
                // Logged at Warning when the stated ground is one the brief closed
                // off. The refusal still stands — this class cannot be allowed to
                // overrule the veto, or the veto is not a veto — but a refusal whose
                // premise contradicts the brief is a defect, and it now says so in the
                // log at the moment it happens rather than only in a table nobody has
                // opened yet. Five of twelve refusals in the first audited day were
                // of this kind.
                if (ContradictsBrief(reason, candidate) is { } contradiction)
                    log.LogWarning(
                        "[Gate] SKIPPED {Side} {Symbol} on a premise the brief contradicts: {Detail} " +
                        "Reason given: {Reason}",
                        candidate.Side, candidate.Symbol, contradiction, reason);

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
                return GateDecision.Refuse(
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
    /// </summary>
    private static string? ContradictsBrief(string reason, EntryCandidate c)
    {
        var text     = reason.ToLowerInvariant();
        var excluded = c.Flow.Votes.Count - c.Flow.ParticipatingVenues;

        if ((text.Contains("excluded") || text.Contains("thin data")) && excluded == 0)
            return $"it cites excluded venues, and the brief showed {excluded} excluded.";

        if ((text.Contains("already open") || text.Contains("positions are open")) && c.OpenPositions < 2)
            return $"it cites open positions, and the brief showed {c.OpenPositions}.";

        if (text.Contains("dispersion") && c.MaxDispersionBps > 0
            && c.Flow.DispersionBps < 0.8 * c.MaxDispersionBps)
            return $"it calls dispersion wide at {c.Flow.DispersionBps:F1} bps, " +
                   $"which is {c.Flow.DispersionBps / c.MaxDispersionBps:P0} of the " +
                   $"{c.MaxDispersionBps:F1} bps ceiling the scorer already enforced.";

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
