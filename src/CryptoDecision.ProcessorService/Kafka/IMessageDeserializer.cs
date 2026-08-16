namespace CryptoDecision.ProcessorService.Kafka;

/// <summary>
/// Pluggable deserialization strategy for Kafka messages.
/// Eliminates typeof(T) type dispatch in KafkaConsumerBase (OCP fix).
///
/// Interview point: each consumer type gets its own deserializer —
/// adding a new message schema = one new IMessageDeserializer implementation.
/// The consumer base class never changes.
/// </summary>
public interface IMessageDeserializer<TMessage> where TMessage : class
{
    TMessage? Deserialize(string json);
}
