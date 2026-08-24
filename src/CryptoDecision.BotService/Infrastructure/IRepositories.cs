using CryptoDecision.BotService.Domain;

namespace CryptoDecision.BotService.Infrastructure;

// ── Repository interfaces used by the Bot Engine ─────────────────────────────

public interface IFeatureRepository
{
    Task<DailyFeature?> GetTodayAsync(string symbol, CancellationToken ct = default);
}

public interface IMomentumRepository
{
    /// <summary>Cumulative buy/sell pressure over the trailing 5m, 15m and 1h windows.</summary>
    Task<MultiTimeframeMomentum> GetMultiTimeframeAsync(string symbol, CancellationToken ct = default);
}


/// <summary>Reads latest AI prediction from prediction_table.</summary>
public interface IPredictionRepository
{
    /// <summary>
    /// The most recent prediction for <paramref name="symbol"/>, or null when there
    /// is none or the newest one is older than <paramref name="maxAge"/>.
    /// </summary>
    /// <param name="maxAge">
    /// How old a prediction may be and still be used. Required rather than defaulted,
    /// because the version of this method without it is the bug: it returned the
    /// newest row unconditionally, with no upper bound on its age and no caller
    /// checking <see cref="PredictionSnapshot.PredictedAt"/>.
    ///
    /// The prediction service being absent is a normal operating state, not an
    /// incident — it is routinely switched off, and on a 16GB host sharing memory
    /// with Ollama that is a reasonable thing to do. That is exactly why the bound
    /// matters: while it is off, the last row it wrote sits in prediction_table
    /// indefinitely, and without an age check the bot keeps feeding it into the entry
    /// composite at full weight, keeps arming the AI filter with it, and keeps
    /// granting the confidence bonus that sizes the position. The failure is not the
    /// service stopping; it is the bot treating a frozen row as a current opinion,
    /// while reporting RUNNING and passing every health check.
    ///
    /// Making the bound a required argument means the next caller has to name a value
    /// rather than inherit an unbounded one.
    /// </param>
    Task<PredictionSnapshot?> GetLatestAsync(
        string symbol, TimeSpan maxAge, CancellationToken ct = default);
}
