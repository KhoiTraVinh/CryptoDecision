namespace CryptoDecision.ProcessorService.Models;

/// <summary>Normalized trade deserialized from Kafka TradeBatch envelope.</summary>
public sealed record Trade(
    string         Symbol,
    long           TradeId,
    decimal        Price,
    decimal        Quantity,
    decimal        QuoteQty,
    bool           IsBuyerMaker,
    DateTimeOffset TradeTime
)
{
    public bool IsWhaleTrade => QuoteQty > 100_000m;
}

/// <summary>Kafka batch envelope from IngestionService.</summary>
public sealed record TradeBatch(
    string               Exchange,
    string               Symbol,
    DateTimeOffset       BatchTimestamp,
    IReadOnlyList<Trade> Trades
);

public sealed record Kline(
    string         Symbol,
    DateTimeOffset OpenTime,
    DateTimeOffset CloseTime,
    decimal        Open,
    decimal        High,
    decimal        Low,
    decimal        Close,
    decimal        Volume,
    decimal        QuoteVolume,
    int            NumberOfTrades,
    bool           IsClosed
);

public sealed record KlineBatch(
    string         Exchange,
    string         Symbol,
    string         Interval,
    DateTimeOffset BatchTimestamp,
    Kline          Kline
);

/// <summary>
/// Daily feature row written to daily_feature_table.
/// Computed from the day's trade and kline data.
/// </summary>
public sealed record DailyFeature(
    string   Symbol,
    DateOnly Date,
    decimal  Return24h,      // (close - open) / open * 100
    decimal  Volatility,     // (high - low) / open * 100
    decimal  VolumeChange,   // today's vol vs yesterday's vol (%)
    int      WhaleCount,     // trades where quoteQty > 100k USDT
    decimal  TotalVolume,
    decimal  Vwap,           // volume-weighted average price
    DateTimeOffset ComputedAt
);
