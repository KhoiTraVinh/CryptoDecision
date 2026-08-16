using System.Text.Json;
using CryptoDecision.ProcessorService.Models;
using CryptoDecision.ProcessorService.Serialization;

namespace CryptoDecision.ProcessorService.Kafka;

/// <summary>
/// Source-generated deserializer for TradeBatch messages.
/// Uses KafkaConsumerJsonContext to avoid reflection overhead.
/// </summary>
public sealed class TradeBatchDeserializer : IMessageDeserializer<TradeBatch>
{
    public TradeBatch? Deserialize(string json)
        => JsonSerializer.Deserialize(json, KafkaConsumerJsonContext.Default.TradeBatch);
}
