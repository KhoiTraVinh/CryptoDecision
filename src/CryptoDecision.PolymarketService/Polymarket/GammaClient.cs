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

    /// <summary>
    /// The one question shape that is a football match winner: "Will Arsenal win on
    /// 2026-10-18?", two-outcome Yes/No, where a draw resolves No.
    ///
    /// An earlier version of this also accepted the "A vs. B?" shape on the theory that it
    /// was the moneyline. It is not. Of 405 such questions in the closed history, 208 are
    /// first-goalscorer lines ("Ascot United FC to score first vs. Clevedon Town FC?") and
    /// the other 197 are draw markets ("Will Arsenal vs. Olympiakos Piraeus end in a
    /// draw?"). Not one was a match winner, and a 1c bid on a draw line is a completely
    /// different bet from a 1c bid on a team. Only the volume floor kept that version from
    /// trading them.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex SoccerWinner = new(
        @"^Will .+ win on \d{4}-\d{2}-\d{2}\?$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Tags on an event, which is where Gamma records the competition. Both halves are
    /// needed: the id is what gets matched, the label is what goes in the log.
    /// </summary>
    private static IReadOnlyList<(int Id, string Label)> EventTags(JsonElement ev)
    {
        if (!ev.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) return [];

        var found = new List<(int, string)>();
        foreach (var t in tags.EnumerateArray())
        {
            if (!t.TryGetProperty("id", out var idEl)) continue;

            // Gamma returns tag ids as strings on events and as numbers elsewhere.
            var id = idEl.ValueKind switch
            {
                JsonValueKind.Number => idEl.TryGetInt32(out var n) ? n : 0,
                JsonValueKind.String => int.TryParse(idEl.GetString(), out var s) ? s : 0,
                _ => 0,
            };
            if (id == 0) continue;

            var label = t.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String
                ? l.GetString()!
                : id.ToString();

            found.Add((id, label));
        }

        return found;
    }

    /// <summary>Counters for one scan. A class rather than ref parameters, which async forbids.</summary>
    private sealed class Skipped
    {
        public int Kind, Volume, Window, League;
    }

    public async Task<IReadOnlyList<EsportsMarket>> FetchMarketsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var results = new List<EsportsMarket>();
        var skipped = new Skipped();

        await ScanTagAsync(_opt.EsportsTagId, Sport.Esports, results, now, skipped, ct);

        var esports = results.Count;

        // One request per competition, not one for the parent Soccer tag.
        //
        // The parent tag does not return its children reliably: a sweep of all 2,100
        // events under tag 100350 found six stale markets in the chosen competitions,
        // while asking tag 306 directly returned sixteen Premier League fixtures inside
        // 48 hours - Chelsea at $50k, Liverpool at $25k - every one of them carrying the
        // Soccer tag it was supposedly reachable through. Scanning the parent is why
        // football kept nothing for two days.
        //
        // Competitions share markets (UCL carries three different tag ids), so results
        // are de-duplicated on conditionId.
        if (_opt.SoccerEnabled)
        {
            var seen = results.Select(r => r.ConditionId).ToHashSet();

            foreach (var tagId in _opt.SoccerLeagueTagIds)
            {
                var before = results.Count;
                await ScanTagAsync(tagId, Sport.Soccer, results, now, skipped, ct);

                // Drop anything this tag repeated from an earlier one.
                for (var i = results.Count - 1; i >= before; i--)
                {
                    if (!seen.Add(results[i].ConditionId)) results.RemoveAt(i);
                }
            }
        }

        log.LogInformation(
            "Gamma scan: {Kept} markets kept ({Esports} esports, {Soccer} football); skipped {Kind} by kind, " +
            "{League} outside the chosen competitions, {Volume} under ${MinVolume} volume, {Window} outside the time window",
            results.Count, esports, results.Count - esports, skipped.Kind, skipped.League,
            skipped.Volume, _opt.MinVolumeUsd, skipped.Window);

        return results;
    }

    /// <summary>
    /// Walks one tag tree. related_tags=true pulls the children of the parent tag - CS2,
    /// LoL, Dota 2, Valorant under Esports; every competition under Soccer - without
    /// having to enumerate them. For football the per-competition tags cannot be used
    /// directly: Serie A and Europa League return almost nothing through their own ids.
    /// </summary>
    private async Task ScanTagAsync(
        int tagId, Sport sport, List<EsportsMarket> results, DateTimeOffset now, Skipped skipped, CancellationToken ct)
    {
        for (var offset = 0; offset < 2000; offset += 100)
        {
            var url = $"{_opt.GammaUrl}/events?tag_id={tagId}&related_tags=true&closed=false" +
                      $"&limit=100&offset={offset}&order=startDate&ascending=false";

            JsonElement page;
            try
            {
                page = await GetJsonAsync(url, ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Gamma page at offset {Offset} for {Sport} failed; using what we have", offset, sport);
                break;
            }

            if (page.ValueKind != JsonValueKind.Array || page.GetArrayLength() == 0) break;

            foreach (var ev in page.EnumerateArray())
            {
                if (!ev.TryGetProperty("markets", out var markets) || markets.ValueKind != JsonValueKind.Array)
                    continue;

                // Competition lives on the event, not the market, so it is read once here.
                var leagues = EventTags(ev);

                foreach (var m in markets.EnumerateArray())
                {
                    var outcome = Map(m, sport, leagues, now, skipped);
                    if (outcome is not null) results.Add(outcome);
                }
            }

            if (page.GetArrayLength() < 100) break;
        }
    }

    private EsportsMarket? Map(JsonElement m, Sport sport, IReadOnlyList<(int Id, string Label)> leagues, DateTimeOffset now, Skipped skipped)
    {
        ref var skippedKind = ref skipped.Kind;
        ref var skippedVolume = ref skipped.Volume;
        ref var skippedWindow = ref skipped.Window;
        if (Bool(m, "closed") == true) return null;
        if (Bool(m, "active") == false) return null;
        if (Bool(m, "enableOrderBook") == false) return null;
        if (Bool(m, "acceptingOrders") == false) return null;

        var conditionId = Str(m, "conditionId") ?? Str(m, "condition_id");
        var question = Str(m, "question") ?? "";
        if (string.IsNullOrEmpty(conditionId) || string.IsNullOrEmpty(question)) return null;

        string? game;

        if (sport == Sport.Soccer)
        {
            // Whitelist here, not blacklist. One football event carries around fifty
            // markets - O/U 0.5 through 8.5, both-teams-to-score, first-half lines,
            // spreads, exact score, draw-at-halftime, first goalscorer - and exactly one
            // shape of question is the match winner. Listing what is allowed is the only
            // filter that holds; a blacklist missed first-goalscorer and draw lines
            // because neither carries a colon.
            if (!SoccerWinner.IsMatch(question))
            {
                skippedKind++;
                return null;
            }

            var league = leagues.FirstOrDefault(l => _opt.SoccerLeagueTagIds.Contains(l.Id));
            if (league.Id == 0)
            {
                skipped.League++;
                return null;
            }

            game = league.Label;
        }
        else
        {
            // The game prefix does most of the filtering on its own: side markets are titled
            // "Map Handicap: ..." or "Games Total: ...", never "Counter-Strike: ...".
            game = _opt.Games.FirstOrDefault(g => question.StartsWith(g + ":", StringComparison.OrdinalIgnoreCase));
            if (game is null) return null;

            var lower = question.ToLowerInvariant();
            if (ExcludedKinds.Any(k => lower.Contains(k)))
            {
                skippedKind++;
                return null;
            }
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

            // Football's endDate is kick-off, not the whistle, so the market is still the
            // live one for about two and a half hours after it goes negative. See
            // PolymarketOptions.SoccerMinutesAfterKickoff for the measurements.
            var floor = sport == Sport.Soccer
                ? -_opt.SoccerMinutesAfterKickoff
                : _opt.MinMinutesToEnd;

            if (minutesLeft < floor || minutesLeft > _opt.MaxHoursToEnd * 60)
            {
                skippedWindow++;
                return null;
            }
        }

        return new EsportsMarket(
            conditionId, question, Str(m, "slug") ?? "", game, sport,
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
