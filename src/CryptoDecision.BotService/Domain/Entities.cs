namespace CryptoDecision.BotService.Domain;

// ── Entities used by the Bot Engine ─────────────────────────────────────────

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



