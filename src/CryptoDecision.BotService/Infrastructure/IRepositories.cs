using CryptoDecision.BotService.Domain;

namespace CryptoDecision.BotService.Infrastructure;

// ── Repository interfaces used by the Bot Engine ─────────────────────────────

public interface IFeatureRepository
{
    Task<DailyFeature?> GetTodayAsync(string symbol, CancellationToken ct = default);
}

