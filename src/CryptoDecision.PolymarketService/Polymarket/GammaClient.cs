using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CryptoDecision.PolymarketService.Polymarket;

/// <summary>
/// Gamma is Polymarket's public catalogue - no key, no auth. It answers the only
/// question this bot asks of it: which esports match markets are open right now, and
/// how much has traded through them.
/// </summary>
public sealed class GammaClient(
    HttpClient http,
    IOptions<PolymarketOptions> options,
    ILogger<GammaClient> log)
{
    private readonly PolymarketOptions _opt = options.Value;

    /// <summary>
    /// Market kinds that are not a match/map/series winner. The measured sample never
    /// touched one of these - 2169 markets, every single one a winner market - and their
    /// prices mean something entirely different, so a 1c ask on a handicap line is not
    /// the same bet at all.
    /// </summary>
    private static readonly string[] ExcludedKinds =
        ["handicap", "over/under", "total rounds", "total maps", "games total", "correct score", "o/u"];

    public async Task<IReadOnlyList<EsportsMarket>> FetchEsportsMarketsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var results = new List<EsportsMarket>();
        var skippedKind = 0;
        var skippedVolume = 0;
        var skippedWindow = 0;

        // related_tags=true pulls the children of the Esports tag - CS2, LoL, Dota 2,
        // Valorant - without having to enumerate them.
        for (var offset = 0; offset < 2000; offset += 100)
        {
            var url = $"{_opt.GammaUrl}/events?tag_id={_opt.EsportsTagId}&related_tags=true&closed=false" +
                      $"&limit=100&offset={offset}&order=startDate&ascending=false";

            JsonElement page;
            try
            {
                page = await GetJsonAsync(url, ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Gamma page at offset {Offset} failed; using what we have", offset);
                break;
            }

            if (page.ValueKind != JsonValueKind.Array || page.GetArrayLength() == 0) break;

            foreach (var ev in page.EnumerateArray())
            {
                if (!ev.TryGetProperty("markets", out var markets) || markets.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var m in markets.EnumerateArray())
                {
                    var outcome = Map(m, now, ref skippedKind, ref skippedVolume, ref skippedWindow);
                    if (outcome is not null) results.Add(outcome);
                }
            }

            if (page.GetArrayLength() < 100) break;
        }

        log.LogInformation(
            "Gamma scan: {Kept} markets kept; skipped {Kind} by kind, {Volume} under ${MinVolume} volume, {Window} outside the time window",
            results.Count, skippedKind, skippedVolume, _opt.MinVolumeUsd, skippedWindow);

        return results;
    }

    private EsportsMarket? Map(JsonElement m, DateTimeOffset now, ref int skippedKind, ref int skippedVolume, ref int skippedWindow)
    {
        if (Bool(m, "closed") == true) return null;
        if (Bool(m, "active") == false) return null;
        if (Bool(m, "enableOrderBook") == false) return null;
        if (Bool(m, "acceptingOrders") == false) return null;

        var conditionId = Str(m, "conditionId") ?? Str(m, "condition_id");
        var question = Str(m, "question") ?? "";
        if (string.IsNullOrEmpty(conditionId) || string.IsNullOrEmpty(question)) return null;

        // The game prefix does most of the filtering on its own: side markets are titled
        // "Map Handicap: ..." or "Games Total: ...", never "Counter-Strike: ...".
        var game = _opt.Games.FirstOrDefault(g => question.StartsWith(g + ":", StringComparison.OrdinalIgnoreCase));
        if (game is null) return null;

        var lower = question.ToLowerInvariant();
        if (ExcludedKinds.Any(k => lower.Contains(k)))
        {
            skippedKind++;
            return null;
        }

        var outcomes = JsonArrayOfStrings(m, "outcomes");
        var tokenIds = JsonArrayOfStrings(m, "clobTokenIds");

        // A mismatch means we cannot say which token is which outcome. Never guess that.
        if (tokenIds.Count == 0 || tokenIds.Count != outcomes.Count) return null;

        // Two-outcome only. "Vao mot phia thoi" is only a meaningful rule when there are
        // exactly two sides and the other one is the complement.
        if (tokenIds.Count != 2) return null;

        var volume = Dec(m, "volumeNum") ?? Dec(m, "volume") ?? 0m;
        if (volume < _opt.MinVolumeUsd)
        {
            skippedVolume++;
            return null;
        }

        var endDate = Date(m, "endDate") ?? Date(m, "end_date_iso");
        if (endDate is not null)
        {
            var minutesLeft = (endDate.Value - now).TotalMinutes;
            if (minutesLeft < _opt.MinMinutesToEnd || minutesLeft > _opt.MaxHoursToEnd * 60)
            {
                skippedWindow++;
                return null;
            }
        }

        return new EsportsMarket(
            conditionId, question, Str(m, "slug") ?? "", game,
            outcomes, tokenIds,
            Bool(m, "negRisk") ?? Bool(m, "neg_risk") ?? false,
            endDate, volume);
    }

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    // -- Gamma returns some fields as JSON-encoded strings; read both shapes. ---

    private static IReadOnlyList<string> JsonArrayOfStrings(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var v)) return [];

        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Select(x => x.ToString()).ToList();

        if (v.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(v.GetString() ?? "[]");
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    return doc.RootElement.EnumerateArray().Select(x => x.ToString()).ToList();
            }
            catch (JsonException)
            {
                // Fall through to empty - a market we cannot map is a market we skip.
            }
        }

        return [];
    }

    private static string? Str(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static decimal? Dec(JsonElement p, string name)
    {
        if (!p.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : null,
            JsonValueKind.String => decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : null,
            _ => null,
        };
    }

    private static DateTimeOffset? Date(JsonElement p, string name)
    {
        var s = Str(p, name);
        return DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }
}
