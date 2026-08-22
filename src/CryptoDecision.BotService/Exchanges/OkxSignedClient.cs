using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CryptoDecision.BotService.Exchanges;

/// <summary>An error OKX reported, carrying the exchange's own error code.</summary>
public sealed class OkxApiException(string code, string message) : Exception(message)
{
    /// <summary>OKX error code, or <c>HTTP_nnn</c> when the response was not an OKX envelope.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Signed transport for the OKX v5 REST API.
///
/// Everything about OKX error reporting is unusual enough to be worth stating:
/// business failures come back as HTTP 200 with a non-zero <c>code</c> in the
/// body, and a batch endpoint can return <c>code: "0"</c> overall while an
/// individual entry in <c>data</c> carries its own failing <c>sCode</c>. Reading
/// only the status line, or only the outer code, reports a rejected order as
/// placed — which on a live account means the bot believes it holds a position it
/// does not have. Both levels are checked: the outer one here, the per-entry one
/// by the caller that knows the shape of its own response.
/// </summary>
public sealed class OkxSignedClient(
    IHttpClientFactory factory,
    OkxOptions         opts,
    ILogger<OkxSignedClient> log)
{
    public const string HttpClientName = "okx";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>GET a public (unauthenticated) endpoint.</summary>
    public Task<List<T>> GetPublicAsync<T>(string pathWithQuery, CancellationToken ct)
        => SendAsync<T>(HttpMethod.Get, pathWithQuery, body: null, signed: false, ct);

    /// <summary>GET an account endpoint, signed with the configured credentials.</summary>
    public Task<List<T>> GetPrivateAsync<T>(string pathWithQuery, CancellationToken ct)
        => SendAsync<T>(HttpMethod.Get, pathWithQuery, body: null, signed: true, ct);

    /// <summary>POST to an account endpoint, signed with the configured credentials.</summary>
    public Task<List<T>> PostPrivateAsync<T>(string path, object payload, CancellationToken ct)
        => SendAsync<T>(HttpMethod.Post, path, JsonSerializer.Serialize(payload, Json), signed: true, ct);

    // ── Transport ─────────────────────────────────────────────────────────────

    private async Task<List<T>> SendAsync<T>(
        HttpMethod method, string pathWithQuery, string? body, bool signed, CancellationToken ct)
    {
        var http = factory.CreateClient(HttpClientName);

        using var req = new HttpRequestMessage(method, pathWithQuery);

        if (body is not null)
        {
            // Content-Type is set without a charset parameter on purpose. The
            // three-argument StringContent constructor appends "; charset=utf-8",
            // and OKX matches this header literally when validating a signed
            // request body.
            req.Content = new StringContent(body, Encoding.UTF8);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        if (signed)
        {
            if (!opts.HasCredentials)
                throw new OkxApiException("NO_CREDENTIALS",
                    "OKX credentials are not configured, so no signed request can be made.");

            // OKX signs the timestamp, the verb, the path *including* its query
            // string, and the raw request body — in that order, over the base64
            // decoding of nothing: the secret is used as the raw HMAC key.
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            var prehash   = timestamp + method.Method.ToUpperInvariant() + pathWithQuery + (body ?? "");

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(opts.ApiSecret));
            var signature  = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(prehash)));

            req.Headers.Add("OK-ACCESS-KEY",        opts.ApiKey);
            req.Headers.Add("OK-ACCESS-SIGN",       signature);
            req.Headers.Add("OK-ACCESS-TIMESTAMP",  timestamp);
            req.Headers.Add("OK-ACCESS-PASSPHRASE", opts.Passphrase);
        }

        // Demo trading is a header, not a different host. It has to be set on every
        // request including the public ones, so instrument rules are read from the
        // same book the orders will be matched against.
        if (opts.DemoTrading)
            req.Headers.Add("x-simulated-trading", "1");

        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        OkxEnvelope<T>? envelope = null;
        try
        {
            envelope = JsonSerializer.Deserialize<OkxEnvelope<T>>(text, Json);
        }
        catch (JsonException ex)
        {
            log.LogWarning("[OKX] Unparseable response from {Method} {Path}: {Err}",
                method.Method, pathWithQuery, ex.Message);
        }

        if (envelope is null)
            throw new OkxApiException(
                $"HTTP_{(int)resp.StatusCode}",
                $"OKX {method.Method} {pathWithQuery} returned {(int)resp.StatusCode} " +
                $"with a body that is not an OKX envelope: {Truncate(text)}");

        if (envelope.Code != "0")
        {
            // The outer code is often the useless half of the answer. OKX reports a
            // batch that failed as code "1" with msg "All operations failed", and puts
            // the reason a caller can act on in data[0].sCode / sMsg — so throwing on
            // the outer pair alone reported every rejected order, whatever the cause,
            // as "All operations failed". PlaceSwapMarketOrderAsync has a per-entry
            // check for exactly this, and it never ran: this throw fires first.
            var entryCode    = (string?)null;
            var entryMessage = (string?)null;

            if (DescribePerEntryFailure(text) is { } detail)
            {
                entryCode    = detail.Code;
                entryMessage = detail.Message;
            }

            throw new OkxApiException(
                entryCode ?? envelope.Code,
                $"OKX rejected {method.Method} {pathWithQuery}: code={envelope.Code} " +
                $"msg={envelope.Msg ?? "(none)"}" +
                (entryCode is not null ? $" — sCode={entryCode} sMsg={entryMessage}" : "") +
                $" [raw: {Truncate(text)}]");
        }

        return envelope.Data ?? [];
    }

    /// <summary>
    /// Pull the first per-entry failure out of a response body, if it has one.
    ///
    /// Read from the raw JSON rather than the typed envelope because the entry shape
    /// differs per endpoint and the failure fields are the same everywhere. Returns
    /// null when there is no nested reason, which is the normal case for an
    /// envelope-level rejection like a bad signature.
    /// </summary>
    private static (string Code, string Message)? DescribePerEntryFailure(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;

                var sCode = entry.TryGetProperty("sCode", out var c) ? c.GetString() : null;
                if (string.IsNullOrEmpty(sCode) || sCode == "0") continue;

                var sMsg = entry.TryGetProperty("sMsg", out var m) ? m.GetString() : null;
                return (sCode, sMsg ?? "(no sMsg)");
            }
        }
        catch (JsonException)
        {
            // Already reported through the raw body in the caller's message.
        }

        return null;
    }

    private static string Truncate(string s, int max = 300)
        => string.IsNullOrEmpty(s) ? "(empty)"
         : s.Length <= max ? s
         : s[..max] + "…";
}

/// <summary>
/// The wrapper every OKX v5 response arrives in. <c>code</c> is "0" on success;
/// anything else is a business failure regardless of the HTTP status.
///
/// Deliberately internal at namespace level rather than nested and private:
/// System.Text.Json constructs records through their primary constructor by
/// reflection, and keeping the type reachable avoids depending on how the
/// serializer treats a private nested generic.
/// </summary>
internal sealed record OkxEnvelope<T>(
    [property: JsonPropertyName("code")] string   Code,
    [property: JsonPropertyName("msg")]  string?  Msg,
    [property: JsonPropertyName("data")] List<T>? Data
);
