using System.Text.Json;
using CryptoDecision.ProcessorService.Models;
using CryptoDecision.ProcessorService.Serialization;

namespace CryptoDecision.ProcessorService.Kafka;

/// <summary>
/// Source-generated deserializer for KlineBatch messages.
/// Uses KafkaConsumerJsonContext to avoid reflection overhead.
/// </summary>
public sealed class KlineBatchDeserializer : IMessageDeserializer<KlineBatch>
{
    public KlineBatch? Deserialize(string json)
        => JsonSerializer.Deserialize(json, KafkaConsumerJsonContext.Default.KlineBatch);
}
