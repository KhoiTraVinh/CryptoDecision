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



// ── Multi-timeframe momentum for enhanced MomentumStrategy ───────────────────

public sealed record TimeframeMomentum(
    string  Timeframe,       // "5m" | "15m" | "1h"
    int     BuyCount,
    int     SellCount,
    decimal BuyVolumeUsd,
    decimal SellVolumeUsd,
    int     WhaleBuyCount,
    int     WhaleSellCount
)
{
    public int     TotalTrades   => BuyCount + SellCount;
    public decimal TotalVolume   => BuyVolumeUsd + SellVolumeUsd;
    public decimal BuyRatio      => TotalTrades > 0 ? (decimal)BuyCount / TotalTrades : 0.5m;
    public decimal VolBuyRatio   => TotalVolume > 0 ? BuyVolumeUsd / TotalVolume : 0.5m;
    public decimal WhalePressure => (WhaleBuyCount + WhaleSellCount) > 0
        ? (decimal)WhaleBuyCount / (WhaleBuyCount + WhaleSellCount) - 0.5m  // range: [-0.5, +0.5]
        : 0m;
}

public sealed record MultiTimeframeMomentum(
    TimeframeMomentum M5,
    TimeframeMomentum M15,
    TimeframeMomentum M1h
);

// ── AI Prediction snapshot from prediction_table ─────────────────────────────

public sealed record PredictionSnapshot(
    string   Symbol,
    string   Direction,      // UP | DOWN | NEUTRAL
    decimal  Confidence,     // 0.35 - 0.90
    string   ModelVersion,
    string?  Rationale,
    DateTime PredictedAt
)
{
    public bool IsAligned(string side)
        => (side == "LONG" && Direction == "UP")
        || (side == "SHORT" && Direction == "DOWN");
};
