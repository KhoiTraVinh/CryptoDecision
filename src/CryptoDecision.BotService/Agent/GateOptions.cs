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

    /// <summary>
    /// Context window handed to Ollama, in tokens.
    ///
    /// 2048, down from 8192, because on this ollama build the cost of a request scales
    /// with THIS number rather than with the length of the prompt. Measured on the
    /// production host, same prompt, only num_ctx varied:
    ///
    ///     num_ctx   total    tokens ollama reported processing
    ///       2048     50.1s    1,026
    ///       4096     80.2s    2,050
    ///       8192    256.7s    5,932
    ///
    /// The prompt was identical in all three. At 8192 a request that should cost about
    /// a thousand tokens cost nearly six thousand, and 257 seconds against a 60-second
    /// timeout. Every gate call returned empty, every entry was refused, and nothing
    /// anywhere reported an error: ollama stayed "healthy", the loop kept turning, and
    /// scripts/health.sh printed "All checks passed" for a bot that could not trade.
    ///
    /// 2048 is sized to the real brief, which measures 1,680 tokens with five retrieved
    /// examples — enough for the prompt plus a short JSON answer, and nothing spare.
    /// If the brief grows past ~1,900 this must grow with it, and the cost of doing so
    /// is now known rather than assumed.
    /// </summary>
    public int NumCtx { get; set; } = 2048;

    /// <summary>
    /// Per-request timeout.
    ///
    /// 60 s was set when a gate call took 10-27 s with the model resident. That was
    /// measured before the brief carried retrieved examples and before this build of
    /// ollama started charging by context window; the same call now measures ~54 s at
    /// NumCtx 2048 with a 1,680-token brief on this 2-core host. 60 s left no margin
    /// and every call came back empty.
    ///
    /// 75 s, and the ceiling on it is not arbitrary: the cycle cancellation token is
    /// passed into the call, so whichever of the two fires first does the cutting.
    /// The cycle budget is BotLiveness.StaleAfter(interval) / 2 = 120 s at the
    /// 30-second interval this runs at. This MUST stay comfortably under that. If it
    /// does not, a slow gate is cut by the cycle deadline instead, which logs as a
    /// stalled loop and leaves open positions unevaluated for that pass — the same
    /// symptom as a real hang, reported for a cause that is not one.
    ///
    ///     measured ~54 s  <  75 s here  <  120 s cycle budget  <  240 s liveness
    ///
    /// Move any one of those and check the other three.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 75;
}
