using CryptoDecision.ApiService.Application;
using CryptoDecision.ApiService.Domain.Entities;

namespace CryptoDecision.ApiService.Domain.Interfaces;

public interface IFeatureRepository
{
    Task<DailyFeature?> GetTodayAsync(string symbol, CancellationToken ct = default);
    Task<IReadOnlyList<DailyFeature>> GetHistoryAsync(string symbol, int days = 30, CancellationToken ct = default);
}

public interface IPredictionRepository
{
    Task<Prediction?> GetLatestAsync(string symbol, CancellationToken ct = default);
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

public interface IUserRepository
{
    /// <summary>Upsert user: insert new or update last_seen. Returns the user id.</summary>
    Task<int> UpsertAsync(string name, string deviceId, CancellationToken ct = default);

    Task<(int Total, int TodayActive, IReadOnlyList<string> RecentNames)>
        GetStatsAsync(CancellationToken ct = default);
}

public interface ITradeQueryRepository
{
    Task<IReadOnlyList<WhaleTradeData>> GetRecentWhalesAsync(DateTime since, CancellationToken ct = default);
    Task<IReadOnlyList<WhaleTradeData>> GetLatestWhalesAsync(string symbol, string exchange, int limit = 50, CancellationToken ct = default);
}

public interface IAlertRepository
{
    Task<PriceAlertDto> CreateAsync(string symbol, string condition, decimal targetPrice, string? userId, string? note, CancellationToken ct);
    Task<IReadOnlyList<PriceAlertDto>> GetActiveAlertsAsync(string? symbol, CancellationToken ct);
    Task<IReadOnlyList<AlertNotificationDto>> GetNotificationsAsync(string? symbol, int limit, CancellationToken ct);
    Task<bool> DeactivateAsync(long id, CancellationToken ct);
}
