using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CryptoDecision.BotService.ClientApi;

/// <summary>The customer's OKX credentials, decrypted, for one call.</summary>
public sealed record OkxCreds(string ApiKey, string Secret, string Passphrase);

/// <summary>A parsed OKX reply, or the reason there isn't one.</summary>
public sealed record OkxReadResult(bool Ok, JsonElement Data, string? Code, string? Error);

/// <summary>
/// Signed GETs against OKX using credentials supplied per call, rather than the ones in
/// configuration that <see cref="Exchanges.OkxSignedClient"/> is bound to.
///
/// Every method here is a READ. There is no code path in this class that can place,
/// amend or cancel an order — which is what makes it safe to point at a key belonging to
/// somebody else.
/// </summary>
public sealed class OkxReadClient(
    IHttpClientFactory factory,
    ILogger<OkxReadClient> log)
{
    public const string HttpClientName = "okx-client-api";

    /// <summary>Signed GET. Returns the <c>data</c> element on success.</summary>
    public async Task<OkxReadResult> GetAsync(OkxCreds creds, string path, CancellationToken ct)
    {
        var http = factory.CreateClient(HttpClientName);

        var timestamp = DateTime.UtcNow.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        // timestamp + VERB + path (query included) + body — OKX signs exactly this.
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(creds.Secret));
        var signature = Convert.ToBase64String(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(timestamp + "GET" + path)));

        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("OK-ACCESS-KEY", creds.ApiKey);
        req.Headers.Add("OK-ACCESS-SIGN", signature);
        req.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
        req.Headers.Add("OK-ACCESS-PASSPHRASE", creds.Passphrase);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        string text;
        try
        {
            using var resp = await http.SendAsync(req, ct);
            text = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "[ClientApi] OKX unreachable for {Path}", path);
            return new OkxReadResult(false, default, null, "Could not reach OKX.");
        }

        try
        {
            // Cloned because the JsonDocument is disposed when this scope ends and a
            // JsonElement into a disposed document throws on first read, at the caller,
            // far from here.
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.GetString() : null;
            var msg  = root.TryGetProperty("msg",  out var m) ? m.GetString() : null;

            if (code != "0")
                return new OkxReadResult(false, default, code, Friendly(code, msg));

            var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
            return new OkxReadResult(true, data, code, null);
        }
        catch (JsonException)
        {
            // HTML, not JSON: Cloudflare blocked the caller rather than OKX refusing it.
            log.LogWarning("[ClientApi] Non-JSON reply from OKX for {Path}", path);
            return new OkxReadResult(false, default, null, "OKX returned an unexpected reply.");
        }
    }

    /// <summary>
    /// The three OKX codes a customer can actually fix, each with a different fix. A
    /// single "invalid key" would send them to re-paste a key that was never the problem.
    /// </summary>
    public static string Friendly(string? code, string? msg) => code switch
    {
        "50110" => $"This server's IP is not on the key's whitelist. Add it on the OKX API "
                 + $"page and try again. (OKX said: {msg})",
        "50111" or "50113" or "50102" =>
            "The key, secret or passphrase does not match. Check all three.",
        "50114" => "That key is not permitted from this server.",
        _ => $"OKX rejected the request: {msg ?? code ?? "unknown error"}",
    };

    /// <summary>One closed position, as OKX settled it.</summary>
    public sealed record ClosedPosition(
        string Side, string Symbol, DateTime ClosedAt, string CloseReason, decimal PnlUsd);

    /// <summary>
    /// Closed positions straight from the exchange.
    ///
    /// <c>realizedPnl</c> is the number reported, NOT the bot's own <c>pnl_usd</c>. The
    /// two disagree: OKX nets fees and funding into realizedPnl, while the bot derives
    /// its figure from its own fills and excludes funding — it says so in a warning on
    /// every close. Showing the customer anything other than what their exchange
    /// statement says is a support conversation waiting to happen, so this is the
    /// source and the app says so on the screen.
    ///
    /// Checked against a live row: pnl 0.2405 + fee -0.02079104 + fundingFee 0
    /// = realizedPnl 0.21970896.
    /// </summary>
    public async Task<IReadOnlyList<ClosedPosition>?> GetClosedPositionsAsync(
        OkxCreds creds, int limit, string? instId, CancellationToken ct)
    {
        // Filtered at OKX rather than after fetching, so the row budget is spent on
        // the pair being traded. Without this, an account that has ever held anything
        // else spends part of its 100 rows on positions this bot never opened — and
        // the customer sees trades they cannot connect to anything the app told them.
        var query = $"/api/v5/account/positions-history?instType=SWAP&limit={limit}"
                  + (string.IsNullOrWhiteSpace(instId) ? "" : $"&instId={instId}");

        var res = await GetAsync(creds, query, ct);

        if (!res.Ok || res.Data.ValueKind != JsonValueKind.Array) return null;

        var list = new List<ClosedPosition>();
        foreach (var p in res.Data.EnumerateArray())
        {
            string? Str(string n) => p.TryGetProperty(n, out var v) ? v.GetString() : null;

            decimal Dec(string n) =>
                decimal.TryParse(Str(n), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                    ? d : 0m;

            // uTime is when the position closed; cTime is when it opened.
            var closedAt = long.TryParse(Str("uTime"), out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                : default;

            list.Add(new ClosedPosition(
                (Str("direction") ?? Str("posSide") ?? "").ToUpperInvariant(),
                Str("instId") ?? "",
                closedAt,
                CloseReason(Str("type"), Dec("liqPenalty")),
                Dec("realizedPnl")));
        }
        return list;
    }

    /// <summary>
    /// Only type 1 and 2 are confirmed against OKX's own documentation. 3, 4 and 5 are
    /// widely repeated as liquidation / partial liquidation / ADL but I could not
    /// confirm them, and telling a customer their position was LIQUIDATED when it was
    /// not is a far worse error than saying "Closed" about one that was.
    ///
    /// So liquidation is detected from liqPenalty instead, which is a number OKX
    /// actually populates rather than an enum being guessed at.
    /// </summary>
    private string CloseReason(string? type, decimal liqPenalty)
    {
        if (liqPenalty != 0m) return "Liquidated";

        switch (type)
        {
            case "1": return "Closed partially";
            case "2": return "Closed";
            default:
                // Logged so an unmapped code can be identified from a real occurrence
                // rather than from a blog post.
                if (!string.IsNullOrEmpty(type))
                    log.LogInformation(
                        "[ClientApi] positions-history returned close type {Type}, which is not "
                      + "mapped. Reported to the customer as \"Closed\".", type);
                return "Closed";
        }
    }

    /// <summary>
    /// Spendable USD-stable collateral. Sums the USDT and USDC legs of the trading
    /// account: a customer who funded in one and reads a balance of zero because we
    /// looked at the other would reasonably think the link failed.
    /// </summary>
    public async Task<decimal?> GetAvailableUsdAsync(OkxCreds creds, CancellationToken ct)
    {
        var res = await GetAsync(creds, "/api/v5/account/balance", ct);
        if (!res.Ok || res.Data.ValueKind != JsonValueKind.Array || res.Data.GetArrayLength() == 0)
            return null;

        if (!res.Data[0].TryGetProperty("details", out var details)
            || details.ValueKind != JsonValueKind.Array)
            return null;

        decimal total = 0m;
        var found = false;

        foreach (var leg in details.EnumerateArray())
        {
            var ccy = leg.TryGetProperty("ccy", out var c) ? c.GetString() : null;
            if (ccy is not ("USDT" or "USDC")) continue;

            var raw = leg.TryGetProperty("availBal", out var a) ? a.GetString() : null;
            if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            {
                total += v;
                found = true;
            }
        }

        return found ? total : null;
    }
}
