using System.Text.Json.Serialization;

namespace CryptoDecision.IngestionService.Binance.Models;

// ── Combined stream wrapper ─────────────────────────────────────────────────

public sealed class CombinedStreamMessage
{
    [JsonPropertyName("stream")]
    public string Stream { get; init; } = string.Empty;

    [JsonPropertyName("data")]
    public System.Text.Json.JsonElement Data { get; init; }
}

// ── Trade stream (@trade) ────────────────────────────────────────────────────

public sealed class BinanceTradeMessage
{
    [JsonPropertyName("e")] public string EventType  { get; init; } = string.Empty;  // "trade"
    [JsonPropertyName("E")] public long   EventTime  { get; init; }
    [JsonPropertyName("s")] public string Symbol     { get; init; } = string.Empty;
    [JsonPropertyName("t")] public long   TradeId    { get; init; }
    [JsonPropertyName("p")] public string Price      { get; init; } = string.Empty;
    [JsonPropertyName("q")] public string Quantity   { get; init; } = string.Empty;
    [JsonPropertyName("T")] public long   TradeTime  { get; init; }
    [JsonPropertyName("m")] public bool   IsBuyerMaker { get; init; }
}

// ── Kline stream (@kline_1m) ─────────────────────────────────────────────────

public sealed class BinanceKlineMessage
{
    [JsonPropertyName("e")] public string EventType { get; init; } = string.Empty;  // "kline"
    [JsonPropertyName("E")] public long   EventTime { get; init; }
    [JsonPropertyName("s")] public string Symbol    { get; init; } = string.Empty;

    [JsonPropertyName("k")] public BinanceKlineData Kline { get; init; } = new();
}

public sealed class BinanceKlineData
{
    [JsonPropertyName("t")] public long   OpenTime        { get; init; }
    [JsonPropertyName("T")] public long   CloseTime       { get; init; }
    [JsonPropertyName("s")] public string Symbol          { get; init; } = string.Empty;
    [JsonPropertyName("i")] public string Interval        { get; init; } = string.Empty;
    [JsonPropertyName("o")] public string Open            { get; init; } = string.Empty;
    [JsonPropertyName("h")] public string High            { get; init; } = string.Empty;
    [JsonPropertyName("l")] public string Low             { get; init; } = string.Empty;
    [JsonPropertyName("c")] public string Close           { get; init; } = string.Empty;
    [JsonPropertyName("v")] public string Volume          { get; init; } = string.Empty;
    [JsonPropertyName("q")] public string QuoteVolume     { get; init; } = string.Empty;
    [JsonPropertyName("n")] public int    NumberOfTrades  { get; init; }
    [JsonPropertyName("x")] public bool   IsClosed        { get; init; }
}
