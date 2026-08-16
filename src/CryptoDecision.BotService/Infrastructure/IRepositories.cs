using CryptoDecision.BotService.Domain;

namespace CryptoDecision.BotService.Infrastructure;

// ── Repository interfaces used by the Bot Engine ─────────────────────────────

public interface IFeatureRepository
{
    Task<DailyFeature?> GetTodayAsync(string symbol, CancellationToken ct = default);
}

public interface IMomentumRepository
{
    Task<MomentumData> GetAsync(string symbol, string exchange = "BINANCE", CancellationToken ct = default);

    /// <summary>Multi-timeframe momentum: returns 5m, 15m, 1h snapshots.</summary>
    Task<MultiTimeframeMomentum> GetMultiTimeframeAsync(string symbol, CancellationToken ct = default);
}

public interface IVolumeRepository
{
    Task<IReadOnlyList<VolumeWindowData>> GetWindowsAsync(string symbol, string exchange = "BINANCE", CancellationToken ct = default);
}

/// <summary>Reads latest AI prediction from prediction_table.</summary>
public interface IPredictionRepository
{
    Task<PredictionSnapshot?> GetLatestAsync(string symbol, CancellationToken ct = default);
}
