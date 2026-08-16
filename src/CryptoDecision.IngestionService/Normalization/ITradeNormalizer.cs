using CryptoDecision.IngestionService.Models;

namespace CryptoDecision.IngestionService.Normalization;

/// <summary>
/// Adapter Pattern: anti-corruption layer between exchange-specific wire formats
/// and the internal Trade domain model.
///
/// Each exchange has its own raw trade type (TRaw) with different field names,
/// side conventions, and symbol formats. The normalizer translates these into
/// a unified Trade record that downstream consumers (Kafka, PostgreSQL) understand.
///
/// Interview point: this is a classic Adapter / Anti-Corruption Layer from DDD —
/// prevents external API changes from leaking into the bounded context.
/// </summary>
public interface ITradeNormalizer<in TRaw>
{
    Trade Normalize(TRaw raw);
}
