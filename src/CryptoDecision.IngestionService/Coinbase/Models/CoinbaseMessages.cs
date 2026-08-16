using System.Text.Json.Serialization;

namespace CryptoDecision.IngestionService.Coinbase.Models;

// Coinbase Advanced Trade WebSocket (v2) wire format.
// Docs: https://docs.cdp.coinbase.com/advanced-trade/docs/ws-channels#market-trades-channel
// Channel: "market_trades"
//
// Symbol mapping: Coinbase uses "BTC-USDT" format → we normalize to "BTCUSDT".

public sealed record CoinbaseWsMessage(
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("events")]  CoinbaseEvent[]? Events
);

public sealed record CoinbaseEvent(
    [property: JsonPropertyName("type")]   string? Type,    // "snapshot" | "update"
    [property: JsonPropertyName("trades")] CoinbaseTrade[]? Trades
);

/// <summary>
/// Single trade from Coinbase market_trades channel.
/// </summary>
public sealed record CoinbaseTrade(
    [property: JsonPropertyName("trade_id")]   string TradeId,
    [property: JsonPropertyName("product_id")] string ProductId,  // "BTC-USDT"
    [property: JsonPropertyName("price")]      string Price,
    [property: JsonPropertyName("size")]       string Size,       // base quantity
    [property: JsonPropertyName("side")]       string Side,       // "BUY" | "SELL" (taker side)
    [property: JsonPropertyName("time")]       string Time        // ISO 8601
);
