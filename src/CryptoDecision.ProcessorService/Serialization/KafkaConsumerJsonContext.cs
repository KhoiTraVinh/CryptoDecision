using System.Text.Json.Serialization;
using CryptoDecision.ProcessorService.Models;

namespace CryptoDecision.ProcessorService.Serialization;

/// <summary>
/// Source generator context for Kafka Consumer messages.
/// Eliminates reflection overhead when consuming data batches from Kafka.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TradeBatch))]
[JsonSerializable(typeof(KlineBatch))]
public partial class KafkaConsumerJsonContext : JsonSerializerContext
{
}
