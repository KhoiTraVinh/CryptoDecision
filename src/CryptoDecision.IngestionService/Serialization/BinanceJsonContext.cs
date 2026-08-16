using System.Text.Json.Serialization;
using CryptoDecision.IngestionService.Binance.Models;

namespace CryptoDecision.IngestionService.Serialization;

/// <summary>
/// Source generator context for Binance WebSocket messages. 
/// Eliminates reflection overhead during high-frequency deserialization.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CombinedStreamMessage))]
[JsonSerializable(typeof(BinanceTradeMessage))]
[JsonSerializable(typeof(BinanceKlineMessage))]
[JsonSerializable(typeof(BinanceKlineData))]
public partial class BinanceJsonContext : JsonSerializerContext
{
}
