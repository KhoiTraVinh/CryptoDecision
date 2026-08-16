using System.Text.Json.Serialization;
using CryptoDecision.AlertService.Models;

namespace CryptoDecision.AlertService.Serialization;

/// <summary>
/// Source-generated JSON context for Kafka message deserialization/serialization.
/// Avoids reflection overhead — critical for high-throughput trade consumption.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TradeBatch))]
[JsonSerializable(typeof(AlertNotification))]
public partial class AlertJsonContext : JsonSerializerContext;
