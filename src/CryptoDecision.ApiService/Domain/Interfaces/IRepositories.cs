using CryptoDecision.ApiService.Application;
using CryptoDecision.ApiService.Domain.Entities;

namespace CryptoDecision.ApiService.Domain.Interfaces;

public interface IFeatureRepository
{
    Task<DailyFeature?> GetTodayAsync(string symbol, CancellationToken ct = default);
    Task<IReadOnlyList<DailyFeature>> GetHistoryAsync(string symbol, int days = 30, CancellationToken ct = default);
}

public interface IMomentumRepository
{
    Task<MomentumData> GetAsync(string symbol, string exchange = "BINANCE", CancellationToken ct = default);
}

public interface IKlineRepository
{
    Task<IReadOnlyList<KlineData>> GetRecentAsync(string symbol, int limit, string exchange = "BINANCE", CancellationToken ct = default);
}

public interface IVolumeRepository
{
    Task<IReadOnlyList<VolumeWindowData>> GetWindowsAsync(string symbol, string exchange = "BINANCE", CancellationToken ct = default);
}

public interface ITradeQueryRepository
{
    Task<IReadOnlyList<WhaleTradeData>> GetRecentWhalesAsync(DateTime since, CancellationToken ct = default);
    Task<IReadOnlyList<WhaleTradeData>> GetLatestWhalesAsync(string symbol, string exchange, int limit = 50, CancellationToken ct = default);
}

