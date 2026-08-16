using System.Text.Json.Serialization;

namespace CryptoDecision.IngestionService.Bybit.Models;

// Bybit v5 WebSocket public spot API wire format.
// Docs: https://bybit-exchange.github.io/docs/v5/websocket/public/trade

public sealed record BybitTradeEvent(
    [property: JsonPropertyName("topic")] string      Topic,
    [property: JsonPropertyName("data")]  BybitTrade[]? Data
);

/// <summary>
/// Single trade from Bybit publicTrade stream.
/// Field names are single-character to match Bybit's compact format.
/// </summary>
public sealed record BybitTrade(
    [property: JsonPropertyName("T")] long   Timestamp,  // unix ms
    [property: JsonPropertyName("s")] string Symbol,     // e.g. "BTCUSDT"
    [property: JsonPropertyName("S")] string Side,       // "Buy" | "Sell" (taker side)
    [property: JsonPropertyName("v")] string Volume,     // base quantity
    [property: JsonPropertyName("p")] string Price,
    [property: JsonPropertyName("i")] string TradeId
);
