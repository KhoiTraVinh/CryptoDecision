using Npgsql;
using NpgsqlTypes;

namespace CryptoDecision.Shared.Bot;

/// <summary>Persists paper/real trades to the bot_trades table.</summary>
public sealed class BotRepository(NpgsqlDataSource dataSource)
{
    // ── Insert a new trade ────────────────────────────────────────────────────

    public async Task<long> InsertTradeAsync(BotTrade t, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO bot_trades
              (symbol, side, strategy, entry_price, quantity, notional_usd, status, opened_at)
            VALUES
              (@symbol, @side, @strategy, @entryPrice, @qty, @notional, @status, @openedAt)
            RETURNING id
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol",     t.Symbol);
        cmd.Parameters.AddWithValue("side",       t.Side);
        cmd.Parameters.AddWithValue("strategy",   t.Strategy);
        cmd.Parameters.AddWithValue("entryPrice", NpgsqlDbType.Numeric, t.EntryPrice);
        cmd.Parameters.AddWithValue("qty",        NpgsqlDbType.Numeric, t.Quantity);
        cmd.Parameters.AddWithValue("notional",   NpgsqlDbType.Numeric, t.NotionalUsd);
        cmd.Parameters.AddWithValue("status",     t.Status);
        cmd.Parameters.AddWithValue("openedAt",   NpgsqlDbType.TimestampTz, t.OpenedAt);

        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ── Close trade (update exit info) ────────────────────────────────────────

    public async Task CloseTradeAsync(BotTrade t, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bot_trades
            SET exit_price   = @exitPrice,
                pnl_usd      = @pnlUsd,
                pnl_pct      = @pnlPct,
                status       = @status,
                closed_at    = @closedAt,
                close_reason = @reason
            WHERE id = @id
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("exitPrice", NpgsqlDbType.Numeric, t.ExitPrice!.Value);
        cmd.Parameters.AddWithValue("pnlUsd",    NpgsqlDbType.Numeric, t.PnlUsd!.Value);
        cmd.Parameters.AddWithValue("pnlPct",    NpgsqlDbType.Numeric, t.PnlPct!.Value);
        cmd.Parameters.AddWithValue("status",    t.Status);
        cmd.Parameters.AddWithValue("closedAt",  NpgsqlDbType.TimestampTz, t.ClosedAt!.Value);
        cmd.Parameters.AddWithValue("reason",    t.CloseReason ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("id",        t.Id);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Update peak price (trailing stop tracking) ────────────────────────────

    public async Task UpdatePeakPriceAsync(long tradeId, decimal peakPrice, CancellationToken ct = default)
    {
        const string sql = "UPDATE bot_trades SET peak_price = @peak WHERE id = @id";
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("peak", NpgsqlDbType.Numeric, peakPrice);
        cmd.Parameters.AddWithValue("id",   tradeId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Query recent trades ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<BotTrade>> GetRecentTradesAsync(
        int limit = 50, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, symbol, side, strategy, entry_price, exit_price, quantity, notional_usd,
                   pnl_usd, pnl_pct, status, opened_at, closed_at, close_reason, peak_price
            FROM bot_trades
            ORDER BY opened_at DESC
            LIMIT @limit
            """;

        await using var conn   = await dataSource.OpenConnectionAsync(ct);
        await using var cmd    = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<BotTrade>();
        while (await reader.ReadAsync(ct))
            result.Add(MapRow(reader));
        return result;
    }

    // ── Daily P&L for loss limit check ────────────────────────────────────────

    public async Task<decimal> GetTodayPnlAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COALESCE(SUM(pnl_usd), 0)
            FROM bot_trades
            WHERE DATE(closed_at AT TIME ZONE 'UTC') = CURRENT_DATE
              AND status IN ('CLOSED','STOPPED')
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ── Get latest price for a symbol (for unrealized PnL calculation) ─────────

    public async Task<decimal?> GetLatestPriceAsync(string symbol, CancellationToken ct = default)
    {
        const string sql = """
            SELECT close_price FROM klines_1m
            WHERE symbol = @symbol
            ORDER BY open_time DESC
            LIMIT 1
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol", symbol);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is decimal d ? d : null;
    }

    // ── Mapper ────────────────────────────────────────────────────────────────

    private static BotTrade MapRow(NpgsqlDataReader r) => new()
    {
        Id          = r.GetInt64(0),
        Symbol      = r.GetString(1),
        Side        = r.GetString(2),
        Strategy    = r.GetString(3),
        EntryPrice  = r.GetDecimal(4),
        ExitPrice   = r.IsDBNull(5)  ? null : r.GetDecimal(5),
        Quantity    = r.GetDecimal(6),
        NotionalUsd = r.GetDecimal(7),
        PnlUsd      = r.IsDBNull(8)  ? null : r.GetDecimal(8),
        PnlPct      = r.IsDBNull(9)  ? null : r.GetDecimal(9),
        Status      = r.GetString(10),
        OpenedAt    = r.GetDateTime(11),
        ClosedAt    = r.IsDBNull(12) ? null : r.GetDateTime(12),
        CloseReason = r.IsDBNull(13) ? null : r.GetString(13),
        PeakPrice   = r.IsDBNull(14) ? null : r.GetDecimal(14),
    };
}
