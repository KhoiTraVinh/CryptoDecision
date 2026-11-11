using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CryptoDecision.PolymarketService.Polymarket;

/// <summary>
/// Polymarket's public position API. This bot keeps no database, so this is where
/// "have I already entered this market?" is answered - from the chain, not from memory.
///
/// That choice matters more than it looks. An in-process set forgets everything on
/// restart, and a bot that forgets re-buys the same losing longshot every time it is
/// redeployed. Positions are on-chain and survive the container.
///
/// Reads v2. Polymarket retired data-api v1 on 2026-11-04 and the old `/positions` has
/// answered 410 Gone ever since - which this service logged 11,810 times in 24 hours
/// while quietly running its duplicate-entry defence on a stale picture. v2 differs in
/// three ways that all have to change together: rows arrive wrapped in `data`, paging is
/// cursor-only (`offset` is rejected outright), and the fields are snake_case with
/// `size` renamed to `current_size`.
/// </summary>
public sealed class DataApiClient(
    HttpClient http,
    IOptions<PolymarketOptions> options,
    ILogger<DataApiClient> log)
{
    private readonly PolymarketOptions _opt = options.Value;

    /// <summary>
    /// What <paramref name="address"/> actually holds. Throws on a read failure rather
    /// than returning an empty list: "the API is down" and "you own nothing" must not
    /// look the same to the caller, or a blip turns into a wave of duplicate entries.
    /// </summary>
    public async Task<IReadOnlyList<ExchangePosition>> GetPositionsAsync(string address, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(address)) return [];

        var positions = new List<ExchangePosition>();
        string? cursor = null;

        // Cursor pages, not offsets: v2 rejects `offset` outright. The cap is a guard
        // against a cursor that never terminates, not an expected limit - 10 pages is
        // 5,000 positions.
        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"{_opt.DataApiUrl}/v2/positions?user={Uri.EscapeDataString(address)}&limit=500";
            if (!string.IsNullOrEmpty(cursor)) url += $"&cursor={Uri.EscapeDataString(cursor)}";

            using var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            // v2 wraps the rows: { "data": [...], "pagination": { "next_cursor": ... } }.
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                break;

            foreach (var p in data.EnumerateArray())
            {
                var tokenId = Str(p, "token_id");
                var conditionId = Str(p, "condition_id");
                if (string.IsNullOrEmpty(tokenId) || string.IsNullOrEmpty(conditionId)) continue;

                var size = Dec(p, "current_size");
                if (size <= 0) continue;

                positions.Add(new ExchangePosition(
                    tokenId, conditionId, Str(p, "title") ?? "", Str(p, "outcome") ?? "", size, Dec(p, "avg_price")));
            }

            cursor = doc.RootElement.TryGetProperty("pagination", out var pg)
                ? Str(pg, "next_cursor")
                : null;

            if (string.IsNullOrEmpty(cursor) || data.GetArrayLength() == 0) break;
        }

        log.LogDebug("Data API reports {Count} open positions for {Address}", positions.Count, address);
        return positions;
    }

    /// <summary>Guard against a cursor that keeps handing back a next page.</summary>
    private const int MaxPages = 10;

    private static string? Str(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal Dec(JsonElement p, string name)
    {
        if (!p.TryGetProperty(name, out var v)) return 0m;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : 0m,
            JsonValueKind.String => decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : 0m,
            _ => 0m,
        };
    }
}
