namespace CryptoDecision.BotService.Agent;

/// <summary>
/// Ollama settings for the entry gate.
///
/// This is what survived the tool-calling agent. It used to be `AgentOptions` and it
/// carried `MaxIterations` for the agent's tool loop; the gate makes exactly one call
/// and takes exactly one answer, so that field is gone with the loop it bounded.
///
/// The defaults below are only reached when configuration is missing. In practice
/// `Agent:Model` is bound from `OLLAMA_MODEL`, which is `qwen2.5:3b` — the old
/// literal here said 7b long after 3b was deployed, which is the same
/// default-versus-config drift that cost three parameters elsewhere in this repo.
/// The default now names what actually runs.
/// </summary>
public sealed class AgentOptions
{
    public string Model   { get; set; } = "qwen2.5:3b";
    public string BaseUrl { get; set; } = "http://ollama:11434";

    /// <summary>Low, for repeatable decisions. Trading is not a place for creative sampling.</summary>
    public double Temperature { get; set; } = 0.1;

    public int NumCtx { get; set; } = 8192;

    /// <summary>
    /// Per-request timeout. Measured on the 2-core host: ~10-27 s for a gate call with
    /// the model resident, ~13 s more on a cold load. The cycle deadline is 90 s, and
    /// the cycle's cancellation token is passed through, so a hang is cut there and
    /// resolves to no entry.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;
}
