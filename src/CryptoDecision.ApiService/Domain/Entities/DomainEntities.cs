namespace CryptoDecision.ApiService.Domain.Entities;

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

public sealed record Prediction(
    string   Symbol,
    DateOnly Date,
    string   Direction,     // UP | DOWN | NEUTRAL
    decimal  Confidence,
    string   ModelVersion,
    string   Rationale,
    DateTime CreatedAt,

    // Read out of the signals JSONB, because how the verdict was reached decides
    // how much to trust it. "unanimous" across three models and "insufficient"
    // because only one model took a side are very different claims that were
    // rendering as the same confidence percentage.
    string?  Agreement    = null,

    // Models configured to vote that did not answer. XGBoost abstained for six days
    // for want of a trained model.pkl, and the only trace was model_version quietly
    // reading `ensemble-heuristic+llm`.
    string[]? AbsentModels = null,

    // Whether a single model's share had to be capped because others abstained.
    bool     WeightCapped = false
);

public sealed record MomentumData(
    string  Symbol,
    int     TotalTrades,
    int     BuyCount,
    int     SellCount,
    int     WhaleBuyCount,
    int     WhaleSellCount,
    decimal VolumeUsd
);

public sealed record KlineData(
    DateTime OpenTime,
    decimal  Open,
    decimal  High,
    decimal  Low,
    decimal  Close,
    decimal  Volume,
    int      NumTrades
);

public sealed record VolumeWindowData(
    string  Window,         // "1h" | "24h" | "7d" | "30d"
    int     TotalTrades,
    int     BuyCount,
    int     SellCount,
    decimal BuyVolumeUsd,
    decimal SellVolumeUsd,
    int     WhaleBuyCount,
    int     WhaleSellCount,
    decimal WhaleVolumeUsd
);

public sealed record WhaleTradeData(
    string         Symbol,
    string         Exchange,
    decimal        Price,
    decimal        QuoteQty,
    bool           IsBuyerMaker,
    DateTime       TradeTime
);

