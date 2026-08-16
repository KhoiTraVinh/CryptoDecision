using CryptoDecision.AlertService.Models;
using Npgsql;

namespace CryptoDecision.AlertService.Repository;

/// <summary>
/// PostgreSQL repository for price alert rules.
/// Reads active alerts and marks them as triggered when conditions are met.
/// </summary>
public sealed class AlertRepository(NpgsqlDataSource dataSource, ILogger<AlertRepository> logger)
{
    /// <summary>
    /// Load all active (non-triggered) alerts for a given symbol.
    /// Called periodically by AlertEngine to refresh in-memory rule set.
    /// </summary>
    public async Task<List<PriceAlert>> GetActiveAlertsAsync(string symbol, CancellationToken ct)
    {
        const string sql = """
            SELECT id, user_id, symbol, condition, target_price,
                   is_active, is_triggered, triggered_at, triggered_price, created_at, note
            FROM price_alerts
            WHERE symbol = @symbol AND is_active = TRUE AND is_triggered = FALSE
            ORDER BY created_at
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol", symbol);

        var alerts = new List<PriceAlert>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            alerts.Add(new PriceAlert
            {
                Id            = reader.GetInt64(0),
                UserId        = reader.IsDBNull(1) ? null : reader.GetString(1),
                Symbol        = reader.GetString(2),
                Condition     = reader.GetString(3),
                TargetPrice   = reader.GetDecimal(4),
                IsActive      = reader.GetBoolean(5),
                IsTriggered   = reader.GetBoolean(6),
                TriggeredAt   = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                TriggeredPrice = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                CreatedAt     = reader.GetFieldValue<DateTimeOffset>(9),
                Note          = reader.IsDBNull(10) ? null : reader.GetString(10),
            });
        }

        return alerts;
    }

    /// <summary>
    /// Load ALL active alerts across all symbols. Used on startup for initial cache load.
    /// </summary>
    public async Task<List<PriceAlert>> GetAllActiveAlertsAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT id, user_id, symbol, condition, target_price,
                   is_active, is_triggered, triggered_at, triggered_price, created_at, note
            FROM price_alerts
            WHERE is_active = TRUE AND is_triggered = FALSE
            ORDER BY symbol, created_at
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);

        var alerts = new List<PriceAlert>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            alerts.Add(new PriceAlert
            {
                Id            = reader.GetInt64(0),
                UserId        = reader.IsDBNull(1) ? null : reader.GetString(1),
                Symbol        = reader.GetString(2),
                Condition     = reader.GetString(3),
                TargetPrice   = reader.GetDecimal(4),
                IsActive      = reader.GetBoolean(5),
                IsTriggered   = reader.GetBoolean(6),
                TriggeredAt   = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                TriggeredPrice = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                CreatedAt     = reader.GetFieldValue<DateTimeOffset>(9),
                Note          = reader.IsDBNull(10) ? null : reader.GetString(10),
            });
        }

        return alerts;
    }

    /// <summary>
    /// Mark an alert as triggered and record the actual price.
    /// Also inserts into alert_notifications for audit trail.
    /// Uses a transaction to ensure atomicity.
    /// </summary>
    public async Task MarkTriggeredAsync(long alertId, decimal actualPrice, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            // 1. Update the alert rule
            const string updateSql = """
                UPDATE price_alerts
                SET is_triggered = TRUE, is_active = FALSE,
                    triggered_at = NOW(), triggered_price = @price
                WHERE id = @id
                """;

            await using (var cmd = new NpgsqlCommand(updateSql, conn, tx))
            {
                cmd.Parameters.AddWithValue("id", alertId);
                cmd.Parameters.AddWithValue("price", actualPrice);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // 2. Insert notification record
            const string insertSql = """
                INSERT INTO alert_notifications (alert_id, symbol, condition, target_price, actual_price)
                SELECT id, symbol, condition, target_price, @price
                FROM price_alerts WHERE id = @id
                """;

            await using (var cmd = new NpgsqlCommand(insertSql, conn, tx))
            {
                cmd.Parameters.AddWithValue("id", alertId);
                cmd.Parameters.AddWithValue("price", actualPrice);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            logger.LogInformation("Alert {AlertId} triggered at price {Price}", alertId, actualPrice);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
