using System.Text.Json.Nodes;
using CryptoDecision.Shared.Bot;

namespace CryptoDecision.BotService.Agent;

/// <summary>
/// The autonomous trading agent: an LLM given a bounded tool set and asked to
/// decide what, if anything, to do this cycle.
///
/// Division of responsibility
/// --------------------------
/// The agent owns *entry judgement*. It reads order flow, whale pressure and the
/// AI prediction, and decides whether an entry is warranted.
///
/// The agent does NOT own risk or exits.
///   - Every order passes through RiskEngine inside OpenPositionTool, which can
///     refuse regardless of how confident the model sounds. Position size is never
///     a model argument.
///   - Stop loss, take profit, trailing and breakeven exits run deterministically
///     in TradingBotService every cycle. A model that takes 40+ seconds to answer
///     must never sit between a losing position and its stop.
///
/// The loop is bounded by MaxIterations. A model that keeps calling read-only
/// tools without deciding is stopped and the turn ends with no action, which is a
/// perfectly acceptable outcome — most cycles should do nothing.
/// </summary>
public sealed class TradingAgent(
    OllamaAgentClient         client,
    IEnumerable<ITradingTool> tools,
    AgentContext              context,
    AgentOptions              options,
    ILogger<TradingAgent>     log)
{
    private readonly Dictionary<string, ITradingTool> _tools =
        tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

    private JsonArray? _toolSchemas;

    private JsonArray ToolSchemas => _toolSchemas ??= new JsonArray(
        _tools.Values.Select(t => (JsonNode)ToolSchema.ToOllamaTool(t)).ToArray());

    public Task<bool> IsAvailableAsync(CancellationToken ct)
        => client.IsAvailableAsync(options.Model, ct);

    // ── Prompt ────────────────────────────────────────────────────────────────

    private static string BuildSystemPrompt(BotOptions opts) => $"""
        You are an autonomous crypto trading agent managing a real position book for {opts.Symbol}.

        YOUR JOB
        Each time you are called, decide whether to act. Most of the time the correct
        decision is to do nothing. You are judged on profit over many cycles, not on
        being active.

        HOW TO WORK
        1. Call get_market_snapshot to see current order flow and the AI prediction.
        2. Call get_open_positions and get_account_state to see what you already hold
           and how much risk budget is left.
        3. Only then decide. If you open a position, call open_position once.
        4. When you are finished, reply with a short plain-text summary. Do not call
           any more tools after that.

        WHAT MAKES A GOOD ENTRY
        Judge agreement across the three timeframes, weighting the shorter ones more:
        the trade is held for hours, not days, so 5m and 15m are what it actually rides.
        - STRONG setup: 5m, 15m and 1h all lean the same way. Highest conviction.
        - VALID setup: 5m and 15m both lean the same way strongly, and 1h does not
          strongly lean the other way. Take it. Waiting for the 1h to confirm as well
          usually means entering after the move has already happened.
        - NO setup: only one timeframe leans, or 5m and 15m contradict each other, or
          the tape is balanced. That is noise — wait.
        - A line marked MIXED means trade count and volume disagree on that timeframe.
          Treat MIXED as "does not strongly contradict" rather than as confirmation.
        - Whale activity confirming the direction raises conviction, but whales are
          often absent. "0 buy vs 0 sell" is no information, not a reason to refuse.
        - The AI prediction agreeing raises conviction. A NEUTRAL prediction is not a
          veto — it means the daily model has no strong view, which is common.
        - Read flow percentages against a 50% baseline: "18% of volume were aggressive
          buys" means the tape is SELLING, not buying. Each line names its dominant side.
        - You may open a SHORT on selling pressure exactly as readily as a LONG on
          buying pressure. Both directions are available and equally valid.

        WHAT TO AVOID
        - Do not open a position because nothing is happening. Balanced flow is a
          reason to wait.
        - Do not open against a position you already hold in the opposite direction.
        - Do not retry an order that was refused. A refusal explains a hard limit;
          read it and stop.
        - Do not close positions to protect them. Stop loss ({opts.StopLossPct:P2}) and
          take profit ({opts.TakeProfitPct:P2}) are applied automatically every cycle
          without you. Only close early if the market has clearly turned against your
          original reasoning.

        RISK
        Position sizing is decided by the risk engine, not by you. Your confidence
        value scales it within limits you cannot exceed. Orders that breach exposure,
        drawdown or daily-loss limits will be refused no matter how certain you are.
        """;

    // ── Main turn ─────────────────────────────────────────────────────────────

    public async Task<AgentOutcome> RunTurnAsync(CancellationToken ct)
    {
        var opts       = context.Options;
        var transcript = new List<string>();
        var messages   = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = BuildSystemPrompt(opts) },
            new JsonObject
            {
                ["role"]    = "user",
                ["content"] = $"Evaluate {opts.Symbol} now. Current price is " +
                              $"{context.CurrentPrice:F2} USDT. Decide whether to act.",
            },
        };

        var toolCallCount = 0;
        var refusedCount  = 0;

        for (var iteration = 1; iteration <= options.MaxIterations; iteration++)
        {
            var turn = await client.ChatAsync(
                options.Model, messages, ToolSchemas,
                options.Temperature, options.NumCtx, ct);

            if (turn is null)
            {
                log.LogWarning("[Agent] Ollama call failed at iteration {N}; ending turn", iteration);
                transcript.Add($"iteration {iteration}: model call failed");
                return Finish("Agent turn aborted: the model was unreachable.",
                              toolCallCount, refusedCount, false, transcript);
            }

            // The assistant message must be appended verbatim so the model sees its
            // own tool calls in the next round.
            messages.Add(JsonNode.Parse(turn.RawMessage.ToJsonString())!);

            // No tool calls means the agent is done and this is its summary.
            if (turn.ToolCalls.Count == 0)
            {
                var summary = (turn.Content ?? "").Trim();
                if (summary.Length == 0) summary = "Agent finished without a summary.";

                transcript.Add($"iteration {iteration}: final — {Truncate(summary, 200)}");
                return Finish(summary, toolCallCount, refusedCount, false, transcript);
            }

            foreach (var call in turn.ToolCalls)
            {
                toolCallCount++;

                ToolResult result;
                if (!_tools.TryGetValue(call.Name, out var tool))
                {
                    result = ToolResult.Failed(
                        $"unknown tool '{call.Name}'. Available: {string.Join(", ", _tools.Keys)}");
                }
                else
                {
                    try
                    {
                        result = await tool.ExecuteAsync(call.Arguments, ct);
                    }
                    catch (Exception ex)
                    {
                        // A tool contract violation must not kill the turn.
                        log.LogError(ex, "[Agent] Tool {Tool} threw", call.Name);
                        result = ToolResult.Failed($"tool raised an exception: {ex.Message}");
                    }
                }

                if (!result.Success && result.Content.StartsWith("REFUSED", StringComparison.Ordinal))
                    refusedCount++;

                log.LogInformation("[Agent] tool={Tool} ok={Ok} → {Content}",
                    call.Name, result.Success, Truncate(result.Content, 220));

                transcript.Add($"iteration {iteration}: {call.Name} → {Truncate(result.Content, 200)}");

                messages.Add(new JsonObject
                {
                    ["role"]    = "tool",
                    ["content"] = result.Content,
                });
            }
        }

        log.LogWarning("[Agent] Hit the {Max}-iteration limit without a conclusion", options.MaxIterations);
        return Finish(
            $"Agent stopped after {options.MaxIterations} iterations without concluding.",
            toolCallCount, refusedCount, true, transcript);
    }

    private AgentOutcome Finish(
        string summary, int toolCalls, int refused, bool hitLimit, List<string> transcript)
    {
        var outcome = new AgentOutcome(
            Summary:           summary,
            ToolCallCount:     toolCalls,
            OrdersPlaced:      context.OrdersPlaced,
            OrdersRefused:     refused,
            HitIterationLimit: hitLimit,
            Transcript:        transcript);

        log.LogInformation(
            "[Agent] Turn complete: {Calls} tool calls, {Placed} orders placed, " +
            "{Refused} refused. {Summary}",
            outcome.ToolCallCount, outcome.OrdersPlaced, outcome.OrdersRefused,
            Truncate(outcome.Summary, 1200));

        return outcome;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";
}

/// <summary>Agent runtime settings, bound from configuration.</summary>
public sealed class AgentOptions
{
    public string Model       { get; set; } = "qwen2.5:7b";
    public string BaseUrl     { get; set; } = "http://ollama:11434";

    /// <summary>
    /// Hard cap on model round trips per cycle. Enough for snapshot → positions →
    /// account → order → summary, with a little slack.
    /// </summary>
    public int    MaxIterations { get; set; } = 8;

    /// <summary>Low, for repeatable decisions. Trading is not a place for creative sampling.</summary>
    public double Temperature   { get; set; } = 0.1;

    public int    NumCtx        { get; set; } = 8192;

    /// <summary>Per-request timeout. A 7B on CPU can take 60s+ per tool-calling round trip.</summary>
    public int    TimeoutSeconds { get; set; } = 180;
}
