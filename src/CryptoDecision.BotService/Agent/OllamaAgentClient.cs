using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CryptoDecision.BotService.Agent;

/// <summary>
/// Ollama /api/chat client with tool calling.
///
/// Qwen 2.5 has native function calling, which Ollama surfaces as a `tools` array
/// on the request and `tool_calls` on the response message. The conversation is
/// driven by the caller: send messages plus tools, get back either prose or tool
/// calls, append the results as role="tool" messages, send again.
///
/// JsonNode is used throughout rather than typed records because the message list
/// is heterogeneous and grows by appending whatever the server returned verbatim —
/// round-tripping the assistant message exactly is what keeps multi-step tool
/// sequences coherent.
/// </summary>
public sealed class OllamaAgentClient(
    IHttpClientFactory httpFactory,
    ILogger<OllamaAgentClient> log)
{
    public sealed record ChatTurn(
        string?               Content,
        IReadOnlyList<ToolCall> ToolCalls,
        JsonObject            RawMessage);

    public sealed record ToolCall(string Name, JsonObject Arguments);

    /// <summary>True when Ollama answers and holds the configured model.</summary>
    public async Task<bool> IsAvailableAsync(string model, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient("ollama");
            var resp = await http.GetAsync("/api/tags", ct);
            if (!resp.IsSuccessStatusCode) return false;

            var body = await resp.Content.ReadFromJsonAsync<JsonNode>(ct);
            var models = body?["models"]?.AsArray();
            if (models is null) return false;

            var wanted = model.ToLowerInvariant();
            return models.Any(m =>
            {
                var name = m?["name"]?.ToString().ToLowerInvariant() ?? "";
                return name == wanted || name.Split(':')[0] == wanted.Split(':')[0];
            });
        }
        catch (Exception ex)
        {
            log.LogDebug("[Agent] Ollama availability probe failed: {Err}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// One round trip. Returns the assistant turn, which is either prose (the agent
    /// is done) or one or more tool calls (the caller must execute and continue).
    /// </summary>
    public async Task<ChatTurn?> ChatAsync(
        string          model,
        JsonArray       messages,
        JsonArray       tools,
        double          temperature,
        int             numCtx,
        CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"]    = model,
            ["messages"] = JsonNode.Parse(messages.ToJsonString())!.AsArray(),
            ["stream"]   = false,
            ["options"]  = new JsonObject
            {
                ["temperature"] = temperature,
                ["num_ctx"]     = numCtx,
            },
        };

        // Only advertise tools when there are some; an empty array confuses
        // some builds into emitting malformed tool_calls.
        if (tools.Count > 0)
            payload["tools"] = JsonNode.Parse(tools.ToJsonString())!.AsArray();

        try
        {
            var http = httpFactory.CreateClient("ollama");
            using var resp = await http.PostAsJsonAsync("/api/chat", payload, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                log.LogWarning("[Agent] Ollama returned {Status}: {Detail}",
                    (int)resp.StatusCode, Truncate(detail, 300));
                return null;
            }

            var body    = await resp.Content.ReadFromJsonAsync<JsonNode>(ct);
            var message = body?["message"]?.AsObject();
            if (message is null)
            {
                log.LogWarning("[Agent] Ollama response had no message object");
                return null;
            }

            var calls = ParseToolCalls(message);
            var text  = message["content"]?.ToString();

            return new ChatTurn(text, calls, JsonNode.Parse(message.ToJsonString())!.AsObject());
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("[Agent] Ollama request timed out");
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning("[Agent] Ollama request failed: {Err}", ex.Message);
            return null;
        }
    }

    private List<ToolCall> ParseToolCalls(JsonObject message)
    {
        var result = new List<ToolCall>();
        var calls  = message["tool_calls"]?.AsArray();
        if (calls is null) return result;

        foreach (var call in calls)
        {
            var fn = call?["function"]?.AsObject();
            if (fn is null) continue;

            var name = fn["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Arguments arrive as an object from Ollama, but some builds (and some
            // models) send a JSON-encoded string instead. Accept both.
            JsonObject args;
            var raw = fn["arguments"];
            if (raw is JsonObject obj)
            {
                args = JsonNode.Parse(obj.ToJsonString())!.AsObject();
            }
            else if (raw is not null)
            {
                try { args = JsonNode.Parse(raw.ToString())?.AsObject() ?? []; }
                catch (JsonException) { args = []; }
            }
            else
            {
                args = [];
            }

            result.Add(new ToolCall(name!, args));
        }

        return result;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";
}
