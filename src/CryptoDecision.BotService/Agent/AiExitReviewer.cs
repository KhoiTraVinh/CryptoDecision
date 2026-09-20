using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CryptoDecision.Shared.Signals;

namespace CryptoDecision.BotService.Agent;

/// <summary>
/// One open position, reduced to the evidence an exit decision needs.
/// </summary>
/// <param name="Buckets">
/// Closed 15-minute buckets since shortly before the position opened, oldest first. This
/// is the "is there still force" evidence: the model reads the sequence, not a single
/// aggregate, because a fading push and a reversed one look identical in a mean.
/// </param>
public sealed record ExitCandidate(
    long                      TradeId,
    string                    Symbol,
    string                    Side,
    string                    Strategy,
    decimal                   EntryPrice,
    decimal                   CurrentPrice,
    decimal                   ChangePct,
    decimal                   UnrealisedR,
    double                    HeldHours,
    double                    MaxHoldHours,
    decimal                   StopPrice,
    decimal                   TargetPrice,
    decimal                   Move1hPct,
    decimal                   Move4hPct,
    IReadOnlyList<BucketOfi>  Buckets);

/// <summary>What the reviewer decided about an open position.</summary>
/// <param name="Unavailable">
/// True when the model was never asked or could not answer. The caller must fall back to
/// the deterministic rule rather than treating this as HOLD — see the class remarks.
/// </param>
public sealed record ExitReview(bool Cut, string Reason, bool Unavailable = false, int LatencyMs = 0)
{
    public static ExitReview Hold(string reason)        => new(false, reason);
    public static ExitReview CutNow(string reason)      => new(true,  reason);
    public static ExitReview NotReviewed(string reason) => new(false, reason, Unavailable: true);
}

public interface IExitReviewer
{
    Task<ExitReview> ReviewAsync(ExitCandidate candidate, CancellationToken ct);
}

/// <summary>
/// Asks the model whether an open position still has force behind it, or should be cut.
///
/// What this replaces
/// ------------------
/// A hard rule: sum buy and sell notional over the last 15 closed buckets, and close when
/// the imbalance turns against the position after having favoured it. That rule is the
/// only exit on this account that has produced positive R — 30 exits, +4.647R, against the
/// stop's -6.615R over 6 and a take-profit that has never fired — so it is not deleted. It
/// becomes the fallback for when this reviewer cannot answer.
///
/// THE ASYMMETRY THAT PROTECTS THE ENTRY GATE DOES NOT EXIST HERE
/// --------------------------------------------------------------
/// <see cref="AiEntryGate"/> is safe to hand to a 3B model because every failure mode
/// resolves to "no entry", which costs an opportunity and never a position. An exit has no
/// such default: silence must mean either hold — risking a position with no early exit —
/// or cut, which pays a round trip every time Ollama hiccups. Neither is free, so the
/// reviewer does not pick. It reports <see cref="ExitReview.Unavailable"/> and the caller
/// runs the deterministic rule, which is the behaviour that was already earning.
///
/// Pacing, and why it is not negotiable on this host
/// -------------------------------------------------
/// One call costs a measured 42-43s. The host has 2 vCPUs, Ollama sits at 3.406 of its
/// 3.418 GiB limit with NUM_PARALLEL=1 so calls serialise, and the evaluation cycle has a
/// 120s budget it must share with every other open position and with the entry gate. The
/// caller therefore asks at most once every ExitReviewIntervalHours per position, and not
/// at all for the first ExitReviewAfterHours. At the measured 4.04h average hold that is
/// about two calls per trade.
///
/// The model is NOT asked to predict the price
/// -------------------------------------------
/// It is asked one bounded question about evidence already assembled: does the flow still
/// push this way, or has it turned. Everything it would need to invent — the stop, the
/// target, the size, the maximum hold — is fixed and shown to it as context it cannot
/// change.
/// </summary>
public sealed class AiExitReviewer(
    OllamaAgentClient         client,
    AgentOptions              options,
    ILogger<AiExitReviewer>   log) : IExitReviewer
{
    private const string SystemPrompt = """
        You are reviewing one position that is already open. Decide whether to close it now
        or leave it alone.

        WHAT IS ALREADY DECIDED, AND NOT YOURS TO CHANGE
        The direction, the size, the stop price, the target price and the maximum hold were
        all fixed when the position opened. You cannot move any of them and you cannot add
        to the position. You answer one question: cut it now, or hold.

        The stop and the target are still live and are checked in code before you are asked.
        A position that had hit either would never reach you, so "the stop will protect it"
        is true but is not a reason for either answer.

        THE ONE QUESTION
        Is there still force behind this position, or has it turned against it?

        You are given the closed 15-minute buckets since the position opened, oldest first.
        Each shows the order-flow imbalance (OFI) and the notional traded. OFI is positive
        when aggressive buying dominated and negative when aggressive selling did.

        For a LONG, force behind it means OFI has been staying positive. For a SHORT, it
        means OFI has been staying negative.

        CUT when either of these is true of the sequence:
          - THE FORCE IS GONE. The buckets that favoured the position have stopped doing so
            and the recent ones sit near zero or on the wrong side. A position held on a
            push that has finished is a position held for no reason.
          - THE TREND HAS TURNED. The recent buckets lean clearly against the position, or
            the 1-hour and 4-hour price moves both run against it.

        HOLD when the flow still leans the position's way, or when the sequence is genuinely
        mixed and you cannot tell. Do not cut for boredom, and do not cut a position that is
        simply young.

        WEIGH VOLUME, NOT JUST SIGN
        A $0.5M bucket leaning the wrong way is noise; a $10M one is information. The
        notional is given for every bucket for this reason.

        EVERY CLAIM YOU MAKE MUST BE TRUE OF A NUMBER IN THE BRIEF
        Do not state a fact the brief does not contain, and do not state the opposite of one
        it does. Cite the number you used.

        OUTPUT
        Reply with a single JSON object and nothing else:
        {"decision": "CUT" | "HOLD", "reason": "one sentence, citing a number"}
        """;

    public async Task<ExitReview> ReviewAsync(ExitCandidate c, CancellationToken ct)
    {
        if (!await client.IsAvailableAsync(options.Model, ct))
            return ExitReview.NotReviewed(
                $"Exit reviewer unreachable: Ollama is not serving {options.Model}.");

        var brief = Describe(c);

        // The brief is the whole input to the decision and nothing else records it. The
        // entry gate has the same gap and it is why "why did it say that" has twice had to
        // be reconstructed by hand from flow_bars_15m.
        log.LogDebug("[ExitReview] Brief for trade {Id}:\n{Brief}", c.TradeId, brief);

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
            new JsonObject { ["role"] = "user",   ["content"] = brief },
        };

        OllamaAgentClient.ChatTurn? turn;
        var clock = Stopwatch.StartNew();

        try
        {
            turn = await client.ChatAsync(
                options.Model, messages, tools: new JsonArray(),
                temperature: options.Temperature, numCtx: options.NumCtx, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ExitReview.NotReviewed($"Exit review call failed: {ex.Message}");
        }

        var latencyMs = (int)clock.ElapsedMilliseconds;

        if (string.IsNullOrWhiteSpace(turn?.Content))
            return ExitReview.NotReviewed("Exit reviewer returned an empty answer.")
                   with { LatencyMs = latencyMs };

        return Parse(turn.Content, c) with { LatencyMs = latencyMs };
    }

    private static string Describe(ExitCandidate c)
    {
        var isLong = !string.Equals(c.Side, "SHORT", StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine($"POSITION  {c.Strategy} {c.Side} {c.Symbol}");
        sb.AppendLine($"  entry {c.EntryPrice:F2}, now {c.CurrentPrice:F2} — " +
                      $"{c.ChangePct:+0.00%;-0.00%} in this position's direction " +
                      $"({c.UnrealisedR:+0.00;-0.00}R unrealised). A negative number here means " +
                      "the position is losing.");
        sb.AppendLine($"  held {c.HeldHours:F1}h of a {c.MaxHoldHours:F0}h maximum");
        sb.AppendLine($"  stop {c.StopPrice:F2}, target {c.TargetPrice:F2} — both live, both checked in code");
        sb.AppendLine();
        // Each move is LABELLED with what it means for this position, and the "both against"
        // test is EVALUATED here rather than left to the model.
        //
        // This line used to read "A {falling|rising} market runs against this {side}" — a
        // legend explaining which direction hurts. The model recited it as an observation on
        // its first review, and on the second and third it emitted "the 1-hour and 4-hour
        // price moves both run against it" — verbatim one of the CUT conditions below —
        // which was FALSE on review #2 (4h was +0.75%, with the position) and TRUE on #3.
        // A fixed phrase emitted regardless of the data is not a reading of the brief. So
        // the brief now states the answer and there is nothing left to recite.
        // Parameter deliberately NOT named isLong: a local function parameter that shadows
        // an enclosing local is CS0136, and this file cannot be compile-checked on every
        // edit here.
        static string Runs(decimal move, bool forLong) =>
            move == 0m                ? "flat"
            : (move > 0m) == forLong  ? "runs WITH the position"
                                      : "runs AGAINST the position";

        var oneAgainst  = (c.Move1hPct > 0m) != isLong && c.Move1hPct != 0m;
        var fourAgainst = (c.Move4hPct > 0m) != isLong && c.Move4hPct != 0m;

        sb.AppendLine("PRICE CONTEXT");
        sb.AppendLine($"  1h {c.Move1hPct,6:+0.00;-0.00}%   {Runs(c.Move1hPct, isLong)}");
        sb.AppendLine($"  4h {c.Move4hPct,6:+0.00;-0.00}%   {Runs(c.Move4hPct, isLong)}");
        sb.AppendLine(
            oneAgainst && fourAgainst
                ? "  BOTH the 1h and 4h moves run against this position."
                : "  They do NOT both run against this position, so the price half of the " +
                  "'trend has turned' test is not met.");
        sb.AppendLine();

        sb.AppendLine("CLOSED 15-MINUTE BUCKETS SINCE THE POSITION OPENED, OLDEST FIRST");
        sb.AppendLine($"  {"time",-6} {"OFI",8}  {"notional",10}   with this " + c.Side + "?");

        foreach (var b in c.Buckets)
        {
            var favours = isLong ? b.Ofi > 0 : b.Ofi < 0;
            sb.AppendLine(
                $"  {b.Bucket:HH:mm} {b.Ofi,8:+0.000;-0.000}  " +
                $"${b.VolumeUsd / 1_000_000m,8:F2}M   {(favours ? "yes" : "NO")}");
        }

        // Counted here rather than left to the model. It is the one arithmetic claim the
        // brief makes, and a model that miscounts a twelve-row list would otherwise invent
        // a statistic and cite it as a number from the brief.
        var withIt = c.Buckets.Count(b => isLong ? b.Ofi > 0 : b.Ofi < 0);
        sb.AppendLine($"  {withIt} of the last {c.Buckets.Count} bucket(s) favoured this {c.Side}.");

        if (c.Buckets.Count >= 4)
        {
            var recent      = c.Buckets.TakeLast(4).ToList();
            var recentWith  = recent.Count(b => isLong ? b.Ofi > 0 : b.Ofi < 0);
            sb.AppendLine($"  Of the most recent 4, {recentWith} favoured it.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Read the model's answer, and refuse to guess at anything ambiguous.
    ///
    /// An unparseable or unrecognised answer is <see cref="ExitReview.NotReviewed"/> rather
    /// than HOLD, because the caller treats "not reviewed" as "run the rule that was
    /// already earning" — silently downgrading a malformed answer to HOLD would leave the
    /// position with no early exit at all and no trace of why.
    /// </summary>
    private ExitReview Parse(string content, ExitCandidate c)
    {
        var start = content.IndexOf('{');
        var end   = content.LastIndexOf('}');

        if (start < 0 || end <= start)
            return ExitReview.NotReviewed(
                $"Exit reviewer answered without JSON for trade {c.TradeId}: " +
                Trim(content));

        try
        {
            using var doc = JsonDocument.Parse(content[start..(end + 1)]);
            var root = doc.RootElement;

            var decision = root.TryGetProperty("decision", out var d) ? d.GetString() : null;
            var reason   = root.TryGetProperty("reason", out var r)
                           ? r.GetString() ?? "(no reason given)"
                           : "(no reason given)";

            if (ContradictsBrief(reason, c) is { } contradiction)
                log.LogWarning(
                    "[ExitReview] Trade {Id} answered {Decision} on a premise the brief " +
                    "contradicts: {Detail} Reason given: {Reason}",
                    c.TradeId, decision, contradiction, reason);

            if (string.Equals(decision, "CUT", StringComparison.OrdinalIgnoreCase))
            {
                log.LogInformation(
                    "[ExitReview] Trade {Id} {Side}: CUT — {Reason}", c.TradeId, c.Side, reason);
                return ExitReview.CutNow(reason);
            }

            if (string.Equals(decision, "HOLD", StringComparison.OrdinalIgnoreCase))
            {
                log.LogInformation(
                    "[ExitReview] Trade {Id} {Side}: HOLD — {Reason}", c.TradeId, c.Side, reason);
                return ExitReview.Hold(reason);
            }

            return ExitReview.NotReviewed(
                $"Exit reviewer gave an unrecognised decision '{decision}' for trade {c.TradeId}.");
        }
        catch (JsonException ex)
        {
            return ExitReview.NotReviewed(
                $"Exit reviewer answer for trade {c.TradeId} did not parse: {ex.Message}");
        }
    }

    /// <summary>
    /// Does the stated reason assert something the brief showed to be false?
    ///
    /// The same shape as <c>AiEntryGate.ContradictsBrief</c>, and here for the same reason:
    /// the model's first three reviews all produced a defensible DECISION with a recited or
    /// false PREMISE, and the premise is the only part that survives into the log. Review #2
    /// claimed "the 1-hour and 4-hour price moves both run against it" when 4h was +0.75%,
    /// with the position.
    ///
    /// It only LOGS. The decision stands either way — a detector that could overturn the
    /// model would make the model decorative, which is the arrangement this replaced. What
    /// it buys is that a wrong premise is visible at the moment it happens rather than
    /// reconstructed from SQL hours later, which is how all three were caught.
    /// </summary>
    private static string? ContradictsBrief(string reason, ExitCandidate c)
    {
        var text   = reason.ToLowerInvariant();
        var isLong = !string.Equals(c.Side, "SHORT", StringComparison.OrdinalIgnoreCase);

        var oneAgainst  = (c.Move1hPct > 0m) != isLong && c.Move1hPct != 0m;
        var fourAgainst = (c.Move4hPct > 0m) != isLong && c.Move4hPct != 0m;

        if (text.Contains("both run against") || text.Contains("both running against"))
            if (!(oneAgainst && fourAgainst))
                return $"it says both price moves run against the position, and the brief " +
                       $"showed 1h {c.Move1hPct:+0.00;-0.00}% and 4h {c.Move4hPct:+0.00;-0.00}% " +
                       $"against a {c.Side} — {(oneAgainst || fourAgainst ? "only one does" : "neither does")}.";

        // Claims about which way the buckets lean, checked against the count the brief
        // printed. Both directions are wrong in the same way and both are worth catching.
        var withIt = c.Buckets.Count(b => isLong ? b.Ofi > 0 : b.Ofi < 0);

        if ((text.Contains("lean the position") || text.Contains("lean towards the position")
          || text.Contains("favour") || text.Contains("favor"))
            && c.Buckets.Count > 0 && withIt * 2 < c.Buckets.Count)
            return $"it says the buckets favour the position, and the brief showed only " +
                   $"{withIt} of {c.Buckets.Count} doing so.";

        if ((text.Contains("turned against") || text.Contains("lean against")
          || text.Contains("force is gone"))
            && c.Buckets.Count > 0 && withIt * 2 > c.Buckets.Count)
            return $"it says the flow has turned against the position, and the brief showed " +
                   $"{withIt} of {c.Buckets.Count} buckets still favouring it.";

        return null;
    }

    private static string Trim(string s) =>
        s.Length <= 160 ? s.Replace('\n', ' ') : s[..160].Replace('\n', ' ') + "…";
}
