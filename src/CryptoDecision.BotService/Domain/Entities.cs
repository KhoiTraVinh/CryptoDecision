namespace CryptoDecision.BotService.Domain;

// ── Entities used by the Bot Engine (subset of ApiService domain) ────────────

public sealed record DailyFeature(
    string   Symbol,
    DateOnly Date,
    decimal  Return24h,
    decimal  Volatility,
    decimal  VolumeChange,
    int      WhaleCount,
    decimal  TotalVolume,
    decimal  Vwap,
    DateTime ComputedAt
);



