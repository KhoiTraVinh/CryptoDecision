using System.Text.Json.Serialization;

namespace CryptoDecision.IngestionService.OKX.Models;

// OKX v5 WebSocket public API wire format.
// Docs: https://www.okx.com/docs-v5/en/#overview-websocket

public sealed record OkxTradeEvent(
    [property: JsonPropertyName("arg")]  OkxArg     Arg,
    [property: JsonPropertyName("data")] OkxTrade[]? Data
);

public sealed record OkxArg(
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("instId")]  string InstId
);

/// <summary>Single trade record from OKX trades channel.</summary>
public sealed record OkxTrade(
    [property: JsonPropertyName("instId")]  string InstId,   // e.g. "BTC-USDT"
    [property: JsonPropertyName("tradeId")] string TradeId,
    [property: JsonPropertyName("px")]      string Px,       // price string
    [property: JsonPropertyName("sz")]      string Sz,       // size in base currency
    [property: JsonPropertyName("side")]    string Side,     // "buy" | "sell" (taker side)
    [property: JsonPropertyName("ts")]      string Ts        // unix ms timestamp
);

/// <summary>
/// Envelope for server-sent events (subscribe confirmation, heartbeat, error).
/// Only the event type is needed for routing.
/// </summary>
public sealed record OkxEventEnvelope(
    [property: JsonPropertyName("event")] string? Event
);
