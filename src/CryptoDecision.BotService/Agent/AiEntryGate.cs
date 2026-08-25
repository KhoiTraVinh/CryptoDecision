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
/// </summary>
public sealed record EntryCandidate(
    string        Symbol,
    string        Side,
    decimal       Price,
    FlowVerdict   Flow,
    StopGeometry  Geometry,
    decimal       NotionalUsd,
    int           OpenPositions,
    decimal       TodayPnlUsd);

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
/// </summary>
public sealed class AiEntryGate(
    OllamaAgentClient        client,
    AgentOptions             options,
    ILogger<AiEntryGate>     log) : IEntryGate
{
    private const string SystemPrompt = """
        You are the final check on a trade that a quantitative system has already
        decided to propose. Your only job is to approve it or skip it.

        WHAT IS ALREADY DECIDED, AND NOT YOURS TO CHANGE
        The direction, the position size, the stop price and the target price were all
        set by the system before you were asked. You cannot alter them, and you cannot
        propose a different trade. You answer one question: take this one, or skip it.

        THE ARITHMETIC IS ALREADY VALIDATED — DO NOT RE-JUDGE IT
        Reward:risk, fees, stop distance and position size were computed and checked in
        code before you were called. A candidate that failed those checks never reaches
        you. The reward:risk figure is shown to you as context only. It is NOT a reason
        to skip, no matter how modest it looks: at 1.5:1 a 40% win rate is already
        profitable, and you are not able to judge whether a ratio is "worth it" better
        than the arithmetic that produced it. Never cite reward:risk, fees, stop size or
        position size as grounds for skipping.

        WHAT YOU ARE CHECKING FOR
        The system checks that the evidence is statistically unusual and that the
        venues agree. It cannot check whether the situation makes sense as a whole.
        Skip when you see:
        - The consensus rests on the bare minimum number of venues while the rest
          were excluded, so "agreement" is thinner than the headline suggests.
        - Cross-venue price dispersion is wide, meaning the move is already underway
          and this entry is late.
        - The account has already lost significantly today and this adds to it.
        - Several positions are already open in the same direction; this concentrates
          rather than diversifies.

        Approve when the evidence is coherent and the trade is proportionate. A clean
        setup deserves approval — being reflexively cautious is not the same as being
        careful, and skipping every trade makes you useless rather than safe.

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

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
            new JsonObject { ["role"] = "user",   ["content"] = Describe(candidate) },
        };

        OllamaAgentClient.ChatTurn? turn;
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

        if (string.IsNullOrWhiteSpace(turn?.Content))
            return GateDecision.Refuse("Gate returned an empty answer.");

        return Parse(turn.Content, candidate);
    }

    /// <summary>
    /// Render the candidate as the evidence a reader would need to second-guess it.
    ///
    /// Per-venue z-scores are listed individually rather than summarised, because the
    /// failure this is meant to catch is a headline consensus resting on one venue
    /// while the others were excluded — and that is invisible in an aggregate.
    /// </summary>
    private static string Describe(EntryCandidate c)
    {
        var flow = c.Flow;

        var venues = string.Join("\n", flow.Votes.Select(v => v.Participated
            ? $"  {v.Exchange,-8} z={v.Z,+6:F2}  OFI {v.Ofi,+6:F3} (its median {v.OfiMedian,+6:F3})  " +
              $"${v.VolumeUsd,14:N0}  {v.TradeCount,7:N0} prints  " +
              $"concentration {v.Concentration:P1}{(v.Agreed ? "   <-- agrees" : "")}"
            : $"  {v.Exchange,-8} EXCLUDED: {v.ExclusionReason}"));

        var g = c.Geometry;

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
              cross-venue price dispersion {flow.DispersionBps:F1} bps

            PER VENUE
            {venues}

            ACCOUNT
              open positions: {c.OpenPositions}
              realised P&L today: ${c.TodayPnlUsd:F2}

            Approve or skip.
            """;
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

    private static string Truncate(string s) =>
        s.Length <= 160 ? s.Replace('\n', ' ') : s[..160].Replace('\n', ' ') + "…";
}
