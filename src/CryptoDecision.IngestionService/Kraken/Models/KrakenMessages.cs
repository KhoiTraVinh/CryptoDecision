using System.Text.Json.Serialization;

namespace CryptoDecision.IngestionService.Kraken.Models;

// Kraken WebSocket v2 wire format.
// Docs: https://docs.kraken.com/api/docs/websocket-v2/trade
// Channel: "trade"
//
// Symbol mapping: Kraken uses "BTC/USDT" format → we normalize to "BTCUSDT".

public sealed record KrakenWsMessage(
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("type")]    string? Type,     // "snapshot" | "update"
    [property: JsonPropertyName("data")]    KrakenTrade[]? Data
);

/// <summary>
/// Single trade from Kraken trade channel.
/// </summary>
public sealed record KrakenTrade(
    [property: JsonPropertyName("symbol")]    string Symbol,     // "BTC/USDT"
    [property: JsonPropertyName("side")]      string Side,       // "buy" | "sell" (taker side)
    [property: JsonPropertyName("price")]     decimal Price,
    [property: JsonPropertyName("qty")]       decimal Qty,
    [property: JsonPropertyName("ord_type")]  string? OrdType,   // "market" | "limit"
    [property: JsonPropertyName("trade_id")]  long TradeId,
    [property: JsonPropertyName("timestamp")] string Timestamp   // ISO 8601
);
