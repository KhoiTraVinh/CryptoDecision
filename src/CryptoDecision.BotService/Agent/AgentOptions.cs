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
    /// THE USABLE PROMPT IS ABOUT HALF THIS NUMBER. That is the thing to know, and it
    /// is not documented anywhere in ollama's API. Measured on the production host by
    /// sending prompts of known size and reading back prompt_eval_count:
    ///
    ///     prompt sent   num_ctx   ollama actually processed
    ///          920        2048              926
    ///        3,680        2048            1,026   <- cut
    ///        7,360        2048            1,026   <- cut
    ///        7,360        4096            2,050   <- cut
    ///
    /// Everything past ~num_ctx/2 is dropped, silently: the slot log still prints
    /// `truncated = 0`, the request returns 200, and the model answers confidently on
    /// whatever survived. There is no error to catch.
    ///
    /// 4096, because the gate's brief measures 1,680 tokens with five retrieved
    /// examples and must arrive whole. At 2048 it would be cut to ~1,024 — the model
    /// would lose the account block, the retrieved cases, and part of the per-venue
    /// table, then approve or refuse on the remainder. That is worse than the timeout
    /// this replaced: a timeout fails closed and costs an opportunity, a truncated
    /// brief produces a confident answer to a question the model was never shown.
    ///
    /// Headroom is ~370 tokens and the brief is bounded — five examples is a LIMIT in
    /// FindSimilarAsync and there are three venues. If either grows, measure again;
    /// past ~2,048 tokens of brief this must go to 8192, and the cost of that is on
    /// record in TimeoutSeconds below.
    ///
    /// The cost of 4096 over 2048 is time: ~44 s of prompt processing for the real
    /// brief against ~26 s, at the ~38 tok/s this 2-core host manages.
    /// </summary>
    public int NumCtx { get; set; } = 4096;

    /// <summary>
    /// Per-request timeout.
    ///
    /// 60 s was set when a gate call took 10-27 s with the model resident. That was
    /// measured before the brief carried retrieved examples and before this build of
    /// ollama started charging by context window; the same call now measures ~54 s at
    /// NumCtx 4096 with a 1,680-token brief on this 2-core host. 60 s left no margin
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
    ///     measured ~55 s  <  75 s here  <  120 s cycle budget  <  240 s liveness
    ///
    /// Move any one of those and check the other three.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 75;
}
