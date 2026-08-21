using System.Text.Json;
using System.Text.Json.Nodes;

namespace CryptoDecision.BotService.Agent;

/// <summary>
/// A capability the trading agent can invoke, described the way an MCP server
/// describes its tools: a name, a natural-language description the model reads to
/// decide when to reach for it, and a JSON Schema for the arguments.
///
/// The model never touches a repository or an order engine directly. It can only
/// call these, which is what makes the agent's authority bounded and auditable —
/// the tool set *is* the permission set.
/// </summary>
public interface ITradingTool
{
    /// <summary>snake_case identifier the model calls by name.</summary>
    string Name { get; }

    /// <summary>
    /// What the tool does and when to use it. This is prompt text, not
    /// documentation — the model's tool choice is only as good as this string.
    /// </summary>
    string Description { get; }

    /// <summary>JSON Schema for the arguments object.</summary>
    JsonObject ParameterSchema { get; }

    /// <summary>
    /// Run the tool. Implementations must not throw: a failure the model can read
    /// and react to is far more useful than an exception that kills the loop.
    /// </summary>
    Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct);
}

/// <summary>
/// Outcome of a tool call. <paramref name="Success"/> false is a normal, expected
/// result — a refused order is a refusal, not an error — and the message is fed
/// back to the model so it can adapt rather than retry blindly.
/// </summary>
public sealed record ToolResult(bool Success, string Content)
{
    public static ToolResult Ok(string content)       => new(true, content);
    public static ToolResult Refused(string reason)   => new(false, $"REFUSED: {reason}");
    public static ToolResult Failed(string reason)    => new(false, $"ERROR: {reason}");
}

/// <summary>Helpers for building schemas and reading arguments defensively.</summary>
public static class ToolSchema
{
    public static JsonObject Object(params (string Name, JsonObject Spec)[] properties)
        => Object(properties, required: properties.Select(p => p.Name).ToArray());

    public static JsonObject Object(
        IEnumerable<(string Name, JsonObject Spec)> properties, params string[] required)
    {
        var props = new JsonObject();
        foreach (var (name, spec) in properties) props[name] = spec;

        return new JsonObject
        {
            ["type"]       = "object",
            ["properties"] = props,
            ["required"]   = new JsonArray(required.Select(r => (JsonNode)r!).ToArray()),
        };
    }

    public static JsonObject String(string description) => new()
    {
        ["type"] = "string", ["description"] = description,
    };

    public static JsonObject Enum(string description, params string[] values) => new()
    {
        ["type"]        = "string",
        ["description"] = description,
        ["enum"]        = new JsonArray(values.Select(v => (JsonNode)v!).ToArray()),
    };

    public static JsonObject Number(string description, double? min = null, double? max = null)
    {
        var node = new JsonObject { ["type"] = "number", ["description"] = description };
        if (min is not null) node["minimum"] = min.Value;
        if (max is not null) node["maximum"] = max.Value;
        return node;
    }

    public static JsonObject Integer(string description) => new()
    {
        ["type"] = "integer", ["description"] = description,
    };

    public static JsonObject Empty() => new()
    {
        ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray(),
    };

    // ── Argument readers ──────────────────────────────────────────────────────
    // A 7B will occasionally send a number as a string, or omit an optional field.
    // These coerce rather than throw, so a recoverable formatting slip does not
    // abort the whole agent turn.

    public static string? GetString(JsonObject args, string key)
        => args.TryGetPropertyValue(key, out var node) ? node?.ToString() : null;

    public static decimal GetDecimal(JsonObject args, string key, decimal fallback = 0m)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null) return fallback;
        if (decimal.TryParse(node.ToString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            return value;
        return fallback;
    }

    public static long GetLong(JsonObject args, string key, long fallback = 0L)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null) return fallback;
        return long.TryParse(node.ToString(), out var value) ? value : fallback;
    }

    /// <summary>Render this tool in the shape Ollama's /api/chat `tools` array expects.</summary>
    public static JsonObject ToOllamaTool(ITradingTool tool) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"]        = tool.Name,
            ["description"] = tool.Description,
            ["parameters"]  = JsonNode.Parse(tool.ParameterSchema.ToJsonString())!.AsObject(),
        },
    };
}

/// <summary>What one agent turn concluded.</summary>
public sealed record AgentOutcome(
    string  Summary,
    int     ToolCallCount,
    int     OrdersPlaced,
    int     OrdersRefused,
    bool    HitIterationLimit,
    IReadOnlyList<string> Transcript
);

public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented        = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
