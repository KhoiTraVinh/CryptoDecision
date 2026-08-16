using System.Text.Json.Serialization;
using CryptoDecision.IngestionService.Models;

namespace CryptoDecision.IngestionService.Serialization;

/// <summary>
/// Source generator context for Kafka Producer messages. 
/// Eliminates reflection overhead when publishing data batches to Kafka.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TradeBatch))]
[JsonSerializable(typeof(KlineBatch))]
public partial class KafkaProducerJsonContext : JsonSerializerContext
{
}
