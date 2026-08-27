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

