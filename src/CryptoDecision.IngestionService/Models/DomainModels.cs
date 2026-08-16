namespace CryptoDecision.IngestionService.Models;

/// <summary>
/// Normalized internal trade record. Decoupled from Binance wire format.
/// </summary>
public sealed record Trade(
    string Symbol,          // e.g. "BTCUSDT"
    long   TradeId,
    decimal Price,
    decimal Quantity,
    decimal QuoteQty,       // Price * Quantity — pre-computed for whale detection
    bool    IsBuyerMaker,   // true = sell-initiated
    DateTimeOffset TradeTime
)
{
    public bool IsWhaleTrade => QuoteQty > 100_000m;   // >100k USDT = whale
}

/// <summary>
/// Normalized 1-minute kline (OHLCV) candle.
/// </summary>
public sealed record Kline(
    string  Symbol,
    DateTimeOffset OpenTime,
    DateTimeOffset CloseTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal QuoteVolume,
    int     NumberOfTrades,
    bool    IsClosed         // true = candle finalized
);

/// <summary>
/// Kafka batch envelope wrapping a slice of trades for one symbol.
/// </summary>
public sealed record TradeBatch(
    string         Exchange,
    string         Symbol,
    DateTimeOffset BatchTimestamp,
    IReadOnlyList<Trade> Trades
);

/// <summary>
/// Kafka envelope for a single kline update.
/// </summary>
public sealed record KlineBatch(
    string  Exchange,
    string  Symbol,
    string  Interval,
    DateTimeOffset BatchTimestamp,
    Kline   Kline
);
