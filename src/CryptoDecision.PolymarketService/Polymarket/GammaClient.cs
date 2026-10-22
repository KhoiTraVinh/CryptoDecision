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
        ["handicap", "over/under", "total rounds", "total maps", "games total", "correct score", "o/u", "spread"];

    /// <summary>
    /// Whether an outcome label is a Yes/No or Over/Under leg rather than a team name.
    /// This is what separates a match winner from every side market in the catalogue.
    /// </summary>
    private static bool IsBinaryLabel(string outcome) =>
        outcome.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        outcome.Equals("no", StringComparison.OrdinalIgnoreCase) ||
        outcome.Equals("over", StringComparison.OrdinalIgnoreCase) ||
        outcome.Equals("under", StringComparison.OrdinalIgnoreCase);


    /// <summary>
    /// Tags on an event, which is where Gamma records the competition. Both halves are
    /// needed: the id is what gets matched, the label is what goes in the log.
    /// </summary>
    private static readonly (string Game, string[] Aliases)[] GameAliases =
    [
        ("Counter-Strike", ["counter-strike", "counter strike", "counter strike 2", "cs", "cs2", "csgo"]),
        ("LoL",            ["lol", "league of legends", "msi", "lol worlds", "lec"]),
        ("Dota 2",         ["dota 2", "dota2", "dota"]),
        ("Valorant",       ["valorant", "vct"]),
    ];

    /// <summary>
    /// The canonical game for a market: question prefix first, event tags second, raw
    /// prefix if neither says anything.
    ///
    /// The order matters. The prefix is the more trustworthy of the two - events carry the
    /// wrong game tag often enough to notice, with five LoL markets tagged
    /// "counter-strike" and "Valorant: Paper Rex vs G2 Esports" tagged counter-strike as
    /// well - while a question beginning "LoL:" is never a CS2 match. Tags are only there
    /// for the tournament-named markets ("IEM Dallas: Liquid vs. FaZe"), where the
    /// question says nothing about the game at all.
    /// </summary>
    private static string ResolveGame(string question, IReadOnlyList<(int Id, string Label)> tags)
    {
        var prefix = question.Split(':')[0].Trim();

        foreach (var (game, aliases) in GameAliases)
            if (aliases.Any(a => prefix.Equals(a, StringComparison.OrdinalIgnoreCase)))
                return game;

        foreach (var (game, aliases) in GameAliases)
            if (tags.Any(t => aliases.Any(a => t.Label.Equals(a, StringComparison.OrdinalIgnoreCase))))
                return game;

        return prefix;
    }

    /// <summary>
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
        public int Kind, Volume, Window;
    }

    /// <summary>
    /// Every open esports market worth watching. related_tags=true pulls the children of
    /// the Esports tag - CS2, LoL, Dota 2, Valorant - without having to enumerate them.
    ///
    /// Football lived here too for a week and was removed on 2026-10-20 after it measured
    /// negative at every entry level. The git history has the whole thing if it is ever
    /// worth revisiting: the competition tag ids (several per competition, and never the
    /// ones their names suggest), the Yes-leg-only rule, and the three separate bugs that
    /// kept it silently dead - a parent tag that does not return its children, an endDate
    /// that is kick-off rather than the whistle, and league matching by label.
    /// </summary>
    public async Task<IReadOnlyList<EsportsMarket>> FetchMarketsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var results = new List<EsportsMarket>();
        var skipped = new Skipped();

        for (var offset = 0; offset < 2000; offset += 100)
        {
            var url = $"{_opt.GammaUrl}/events?tag_id={_opt.EsportsTagId}&related_tags=true&closed=false" +
                      $"&limit=100&offset={offset}&order=startDate&ascending=false";

            int onPage;
            try
            {
                onPage = await ReadPageAsync(url, results, now, skipped, ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Gamma page at offset {Offset} failed; using what we have", offset);
                break;
            }

            if (onPage < 100) break;
        }

        log.LogInformation(
            "Gamma scan: {Kept} markets kept; skipped {Kind} by kind, {Volume} under ${MinVolume} volume, " +
            "{Window} outside the time window",
            results.Count, skipped.Kind, skipped.Volume, _opt.MinVolumeUsd, skipped.Window);

        return results;
    }

    private EsportsMarket? Map(JsonElement m, IReadOnlyList<(int Id, string Label)> leagues, DateTimeOffset now, Skipped skipped)
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

        // Parsed before the filter below, because the only reliable way to tell a match
        // winner from a side market is the shape of its outcomes.
        var outcomes = JsonArrayOfStrings(m, "outcomes");
        var tokenIds = JsonArrayOfStrings(m, "clobTokenIds");

        // A mismatch means we cannot say which token is which outcome. Never guess that.
        if (tokenIds.Count == 0 || tokenIds.Count != outcomes.Count) return null;

        // Two-outcome only. One side per match is only a meaningful rule when there are
        // exactly two sides and the other one is the complement.
        if (tokenIds.Count != 2) return null;

        string? game;

        {
            // Not a prefix whitelist any more. Polymarket names the same kind of market
            // several ways and matching the game name only caught 164 of the 611 eligible
            // winner markets in the catalogue. What it missed, by prefix: "League of
            // Legends" (209 markets, $36.2M of volume), "IEM Cologne" (44), "BLAST.tv
            // Austin Major" (31), "CS" (28), "BLAST Bounty" (26), "BLAST Open" (25), "MSI
            // Playoffs" (13), "IEM Dallas" (11) and more - the config said "LoL" and
            // "Counter-Strike", and a great many matches are titled after the tournament
            // instead of the game.
            //
            // What every winner market has in common is the shape of its outcomes: two
            // TEAM NAMES. Side markets carry Yes/No or Over/Under instead. Checked against
            // the whole esports catalogue this keeps all 611 and admits nothing that is
            // not a match - every question kept contains "vs", none is a handicap, total
            // or spread.
            if (IsBinaryLabel(outcomes[0]) || IsBinaryLabel(outcomes[1]))
            {
                skippedKind++;
                return null;
            }

            var lower = question.ToLowerInvariant();
            if (ExcludedKinds.Any(k => lower.Contains(k)))
            {
                skippedKind++;
                return null;
            }

            // Games is no longer a gate - the gate is the Esports tag plus the shape above.
            // But the game still has to be named, because the arming threshold is per game
            // now and an unresolved game quietly takes the fallback instead.
            game = ResolveGame(question, leagues);
        }

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

    /// <summary>
    /// Reads one page of events and maps it in place, returning how many events were on it.
    ///
    /// Mapping happens INSIDE the JsonDocument's scope on purpose. The previous version
    /// returned <c>doc.RootElement.Clone()</c>, which copies the whole page onto the managed
    /// heap - and a page of 100 football events carries about fifty markets each, six to
    /// eight megabytes of JSON. That was survivable while there was one tag to scan; with
    /// seventeen competition tags it threw OutOfMemoryException against the container's
    /// 256 MB limit and every football page after the first was silently dropped.
    /// </summary>
    private async Task<int> ReadPageAsync(
        string url, List<EsportsMarket> results, DateTimeOffset now, Skipped skipped, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var page = doc.RootElement;
        if (page.ValueKind != JsonValueKind.Array) return 0;

        foreach (var ev in page.EnumerateArray())
        {
            if (!ev.TryGetProperty("markets", out var markets) || markets.ValueKind != JsonValueKind.Array)
                continue;

            // Competition lives on the event, not the market, so it is read once here.
            var leagues = EventTags(ev);

            foreach (var m in markets.EnumerateArray())
            {
                var outcome = Map(m, leagues, now, skipped);
                if (outcome is not null) results.Add(outcome);
            }
        }

        return page.GetArrayLength();
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
