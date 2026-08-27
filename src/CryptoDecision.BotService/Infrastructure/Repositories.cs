using CryptoDecision.BotService.Domain;
using Npgsql;
using NpgsqlTypes;

namespace CryptoDecision.BotService.Infrastructure;

// ── FeatureRepository (read-only, used by PaperOrderEngine for vol-sizing) ───

public sealed class FeatureRepository(NpgsqlDataSource dataSource) : IFeatureRepository
{
    public async Task<DailyFeature?> GetTodayAsync(string symbol, CancellationToken ct = default)
    {
        const string sql = """
            SELECT symbol, date, return_24h, volatility, volume_change,
                   whale_count, total_volume, vwap, computed_at
            FROM daily_feature_table
            WHERE symbol = @symbol AND date = CURRENT_DATE
            LIMIT 1
            """;

        await using var conn   = await dataSource.OpenConnectionAsync(ct);
        await using var cmd    = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol", symbol);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new DailyFeature(
            Symbol:       reader.GetString(reader.GetOrdinal("symbol")),
            Date:         DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("date"))),
            Return24h:    reader.GetDecimal(reader.GetOrdinal("return_24h")),
            Volatility:   reader.GetDecimal(reader.GetOrdinal("volatility")),
            VolumeChange: reader.GetDecimal(reader.GetOrdinal("volume_change")),
            WhaleCount:   reader.GetInt32(reader.GetOrdinal("whale_count")),
            TotalVolume:  reader.GetDecimal(reader.GetOrdinal("total_volume")),
            Vwap:         reader.GetDecimal(reader.GetOrdinal("vwap")),
            ComputedAt:   reader.GetDateTime(reader.GetOrdinal("computed_at"))
        );
    }
}

