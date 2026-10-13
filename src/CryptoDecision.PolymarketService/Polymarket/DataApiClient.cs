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

        var url = $"{_opt.DataApiUrl}/positions?user={Uri.EscapeDataString(address)}&sizeThreshold=0.01&limit=500";

        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        var positions = new List<ExchangePosition>();
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            var tokenId = Str(p, "asset");
            var conditionId = Str(p, "conditionId");
            if (string.IsNullOrEmpty(tokenId) || string.IsNullOrEmpty(conditionId)) continue;

            var size = Dec(p, "size");
            if (size <= 0) continue;

            positions.Add(new ExchangePosition(
                tokenId, conditionId, Str(p, "title") ?? "", Str(p, "outcome") ?? "", size, Dec(p, "avgPrice")));
        }

        log.LogDebug("Data API reports {Count} open positions for {Address}", positions.Count, address);
        return positions;
    }

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
