using CryptoDecision.ApiService.Application;
using CryptoDecision.ApiService.Domain.Interfaces;
using Npgsql;

namespace CryptoDecision.ApiService.Infrastructure.Persistence;

public sealed class AlertRepository(NpgsqlDataSource dataSource) : IAlertRepository
{
    public async Task<PriceAlertDto> CreateAsync(
        string symbol, string condition, decimal targetPrice,
        string? userId, string? note, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO price_alerts (symbol, condition, target_price, user_id, note)
            VALUES (@symbol, @condition, @price, @userId, @note)
            RETURNING id, user_id, symbol, condition, target_price,
                      is_active, is_triggered, triggered_at, triggered_price, created_at, note
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.AddWithValue("condition", condition);
        cmd.Parameters.AddWithValue("price", targetPrice);
        cmd.Parameters.AddWithValue("userId", (object?)userId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("note", (object?)note ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return ReadAlertDto(reader);
    }

    public async Task<IReadOnlyList<PriceAlertDto>> GetActiveAlertsAsync(string? symbol, CancellationToken ct)
    {
        var sql = """
            SELECT id, user_id, symbol, condition, target_price,
                   is_active, is_triggered, triggered_at, triggered_price, created_at, note
            FROM price_alerts
            WHERE is_active = TRUE
            """;

        if (symbol is not null) sql += " AND symbol = @symbol";
        sql += " ORDER BY created_at DESC";

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (symbol is not null) cmd.Parameters.AddWithValue("symbol", symbol);

        var results = new List<PriceAlertDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadAlertDto(reader));

        return results;
    }

    public async Task<IReadOnlyList<AlertNotificationDto>> GetNotificationsAsync(
        string? symbol, int limit, CancellationToken ct)
    {
        var sql = """
            SELECT alert_id, symbol, condition, target_price, actual_price, triggered_at
            FROM alert_notifications
            """;

        if (symbol is not null) sql += " WHERE symbol = @symbol";
        sql += " ORDER BY triggered_at DESC LIMIT @limit";

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (symbol is not null) cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.AddWithValue("limit", limit);

        var results = new List<AlertNotificationDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new AlertNotificationDto(
                AlertId:     reader.GetInt64(0),
                Symbol:      reader.GetString(1),
                Condition:   reader.GetString(2),
                TargetPrice: reader.GetDecimal(3),
                ActualPrice: reader.GetDecimal(4),
                TriggeredAt: reader.GetFieldValue<DateTime>(5)
            ));
        }

        return results;
    }

    public async Task<bool> DeactivateAsync(long id, CancellationToken ct)
    {
        const string sql = "UPDATE price_alerts SET is_active = FALSE WHERE id = @id AND is_active = TRUE";

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static PriceAlertDto ReadAlertDto(NpgsqlDataReader reader) => new(
        Id:             reader.GetInt64(0),
        UserId:         reader.IsDBNull(1) ? null : reader.GetString(1),
        Symbol:         reader.GetString(2),
        Condition:      reader.GetString(3),
        TargetPrice:    reader.GetDecimal(4),
        IsActive:       reader.GetBoolean(5),
        IsTriggered:    reader.GetBoolean(6),
        TriggeredAt:    reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTime>(7),
        TriggeredPrice: reader.IsDBNull(8) ? null : reader.GetDecimal(8),
        CreatedAt:      reader.GetFieldValue<DateTime>(9),
        Note:           reader.IsDBNull(10) ? null : reader.GetString(10)
    );
}
