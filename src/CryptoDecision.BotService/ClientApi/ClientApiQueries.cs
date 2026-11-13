using Npgsql;

namespace CryptoDecision.BotService.ClientApi;

/// <summary>Today's trading, as the bot's own book records it.</summary>
public sealed record TodayTally(int OpenPositions, decimal PnlTodayUsd, int TradesToday);

/// <summary>Whether the loop is running, and when it last looked.</summary>
public sealed record BotState(bool Enabled, bool PaperMode, DateTime? LastEvalAt);

/// <summary>
/// The read-only queries behind /api/status. Separate from <see cref="ClientLinkStore"/>
/// because nothing here touches a credential, so it can be read, changed and reasoned
/// about without the care that file needs.
/// </summary>
public sealed class ClientApiQueries(NpgsqlDataSource db)
{
    /// <summary>
    /// Read straight from <c>bot_config</c> rather than through BotConfigRepository,
    /// which does not select <c>last_eval_at</c> — and that column is the one the app
    /// needs: a stopped loop and a crashed one both report enabled = true, and only the
    /// timestamp separates them.
    /// </summary>
    public async Task<BotState?> GetBotStateAsync(CancellationToken ct)
    {
        const string sql =
            "SELECT enabled, paper_mode, last_eval_at FROM bot_config WHERE id = 1";

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);

        if (!await r.ReadAsync(ct)) return null;
        return new BotState(
            r.GetBoolean(0),
            r.GetBoolean(1),
            r.IsDBNull(2) ? null : r.GetDateTime(2));
    }

    /// <summary>
    /// Counted from <c>bot_trades</c>, which is the bot's own book.
    ///
    /// Until <c>client_link</c> is wired into the trading path this is the OPERATOR's
    /// trading, not the customer's. One account, so the numbers happen to be the ones
    /// the customer wants — but that is a coincidence of the single-tenant setup and
    /// stops being true the moment a second customer exists. See mobile/README.md.
    /// </summary>
    public async Task<TodayTally> GetTodayAsync(CancellationToken ct)
    {
        // Day boundary in UTC, matching every other date bucket in this schema. A
        // customer in Australia sees their "today" roll over mid-morning; a local
        // boundary is a real improvement and a real decision, not a tweak to slip in.
        const string sql = """
            SELECT
                (SELECT count(*) FROM bot_trades WHERE status = 'OPEN'),
                (SELECT coalesce(sum(pnl_usd), 0) FROM bot_trades
                   WHERE closed_at >= date_trunc('day', now() AT TIME ZONE 'UTC')),
                (SELECT count(*) FROM bot_trades
                   WHERE opened_at >= date_trunc('day', now() AT TIME ZONE 'UTC'))
            """;

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);

        if (!await r.ReadAsync(ct)) return new TodayTally(0, 0m, 0);
        return new TodayTally(r.GetInt32(0), r.GetDecimal(1), r.GetInt32(2));
    }

    /// <summary>
    /// Why nothing is happening, in the strategies' own words.
    ///
    /// This is the question the customer actually has when the bot sits quiet for days,
    /// and the bot already writes the answer to strategy_verdicts every cycle. Passing
    /// it straight through beats inventing a second, vaguer explanation that can drift
    /// away from what the bot is really doing.
    /// </summary>
    public async Task<string?> GetWaitingNoteAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT strategy, detail
            FROM strategy_verdicts
            ORDER BY updated_at DESC
            """;

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);

        var parts = new List<string>();
        while (await r.ReadAsync(ct))
        {
            var detail = r.IsDBNull(1) ? null : r.GetString(1);
            if (string.IsNullOrWhiteSpace(detail)) continue;

            // The stored detail is written for an operator and runs to several hundred
            // characters of measurement history. The phone gets the first sentence,
            // which is the part that says what is being waited for.
            var stop = detail.IndexOf(". ", StringComparison.Ordinal);
            var head = stop > 0 ? detail[..(stop + 1)] : detail;
            parts.Add($"{r.GetString(0)}: {head.Trim()}");
        }

        return parts.Count == 0 ? null : string.Join("\n", parts);
    }
}
