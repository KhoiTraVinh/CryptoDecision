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
    Task<PredictionSnapshot?> GetLatestAsync(string symbol, CancellationToken ct = default);
}
