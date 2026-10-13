using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CryptoDecision.PolymarketService.Polymarket;

/// <summary>
/// The CLOB: order books (public) and order entry (signed). Deliberately small - this
/// bot reads books, posts resting buys, lists them, and cancels them. It never sells,
/// because the strategy it serves has no exit leg.
///
/// Orders are GTC and rest on the book. That is not a preference, it is the mechanism:
/// in the measured history 99% of positions have a round total share count (100, 80,
/// 250, 200, 150) and 26% of them filled in several pieces seconds to minutes apart.
/// That is a limit order sitting at 1c being eaten by sellers, not a taker lifting an
/// offer. The 1c-to-3c band is almost always EMPTY on the ask side - esports longshots
/// gap straight from 5c to a tenth of a cent - so a taker would hardly ever fill at 1c,
/// and when it did it would be buying a different, losing population.
/// </summary>
public sealed class ClobClient(
    HttpClient http,
    ChainClient chain,
    IOptions<PolymarketOptions> options,
    ILogger<ClobClient> log)
{
    private readonly PolymarketOptions _opt = options.Value;
    private readonly Dictionary<string, decimal> _tickSizes = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private PolymarketSigner? _signer;
    private ApiCreds? _creds;
    private ExchangeDomain? _regularDomain;
    private ExchangeDomain? _negRiskDomain;

    public string? SignerAddress => _signer?.Address;

    /// <summary>The address that holds the money: the Safe proxy for signature type 1 and 2, the wallet itself for 0.</summary>
    public string HolderAddress =>
        _opt.SignatureType != 0 && !string.IsNullOrWhiteSpace(_opt.FunderAddress)
            ? _opt.FunderAddress
            : _signer?.Address ?? _opt.FunderAddress;

    // -- Public data ----------------------------------------------------------

    /// <summary>
    /// Books for many tokens in one call. Batched on purpose: fetching the two sides of a
    /// market in separate requests lets them drift apart in time, and a decision made
    /// across that gap is a decision about a book that never existed.
    /// </summary>
    public async Task<Dictionary<string, OrderBook>> GetBooksAsync(IReadOnlyList<string> tokenIds, CancellationToken ct)
    {
        var books = new Dictionary<string, OrderBook>();

        for (var i = 0; i < tokenIds.Count; i += 100)
        {
            var chunk = tokenIds.Skip(i).Take(100).Select(id => new { token_id = id }).ToArray();
            try
            {
                var body = JsonSerializer.Serialize(chunk, Json);
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await http.PostAsync(_opt.ClobUrl + "/books", content, ct);
                response.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                foreach (var raw in doc.RootElement.EnumerateArray())
                {
                    var assetId = raw.TryGetProperty("asset_id", out var a) ? a.GetString() : null;
                    if (!string.IsNullOrEmpty(assetId)) books[assetId] = Normalise(assetId, raw);
                }
            }
            catch (Exception ex)
            {
                // One bad chunk must not cost the whole scan.
                log.LogWarning(ex, "Book batch of {Count} failed; those tokens sit this cycle out", chunk.Length);
            }
        }

        return books;
    }

    private static OrderBook Normalise(string tokenId, JsonElement raw)
    {
        // Never trust the order the API sends. Everything downstream - best ask, depth,
        // the entry decision itself - assumes bids descend and asks ascend.
        var bids = Levels(raw, "bids").OrderByDescending(l => l.Price).ToList();
        var asks = Levels(raw, "asks").OrderBy(l => l.Price).ToList();
        return new OrderBook(tokenId, bids, asks);
    }

    private static List<BookLevel> Levels(JsonElement raw, string name)
    {
        var result = new List<BookLevel>();
        if (!raw.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return result;

        foreach (var l in arr.EnumerateArray())
        {
            if (!TryDecimal(l, "price", out var price) || !TryDecimal(l, "size", out var size)) continue;
            // Valid Polymarket prices live strictly inside (0, 1). Anything else is junk.
            if (price <= 0 || price >= 1 || size <= 0) continue;
            result.Add(new BookLevel(price, size));
        }
        return result;
    }

    private static bool TryDecimal(JsonElement parent, string name, out decimal value)
    {
        value = 0;
        if (!parent.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    /// <summary>
    /// Tick size for a token, cached. Orders priced off-tick are rejected outright, and
    /// the tick also decides how many decimals the signed amounts may carry.
    /// </summary>
    public async Task<decimal> GetTickSizeAsync(string tokenId, CancellationToken ct)
    {
        if (_tickSizes.TryGetValue(tokenId, out var cached)) return cached;

        var tick = 0.01m;
        try
        {
            using var response = await http.GetAsync($"{_opt.ClobUrl}/tick-size?token_id={Uri.EscapeDataString(tokenId)}", ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (TryDecimal(doc.RootElement, "minimum_tick_size", out var t) && t > 0) tick = t;
        }
        catch (Exception ex)
        {
            // 0.01 is the common case and the conservative one: it rounds our buy price
            // to a coarser grid than the market may allow, never a finer one.
            log.LogWarning(ex, "Tick size lookup failed for {Token}; assuming 0.01", Short(tokenId));
        }

        _tickSizes[tokenId] = tick;
        return tick;
    }

    // -- Auth -----------------------------------------------------------------

    /// <summary>
    /// Brings up the signer and derives API credentials. Called once at startup; throws
    /// if the wallet cannot authenticate, because a bot that cannot sign should not be
    /// quietly scanning.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        // Ask each exchange for its own EIP-712 domain before signing anything against
        // it. The pre-2026 contracts do not implement ERC-5267, so a null falls back to
        // the documented v1 domain and nothing changes for them.
        var regular = _regularDomain = await ResolveDomainAsync(_opt.ExchangeAddress, ct);
        var negRisk = _negRiskDomain = await ResolveDomainAsync(_opt.NegRiskExchangeAddress, ct);

        _signer = new PolymarketSigner(_opt.PrivateKey, _opt.ChainId, regular, negRisk);
        log.LogInformation("Wallet {Address} loaded (signature type {SigType})", _signer.Address, _opt.SignatureType);

        _creds = await CreateOrDeriveApiKeyAsync(ct);
        log.LogInformation("CLOB credentials ready (api key {Key})", Short(_creds.Key));
    }

    private async Task<ExchangeDomain> ResolveDomainAsync(string exchange, CancellationToken ct)
    {
        var declared = await chain.ReadEip712DomainAsync(exchange, ct);
        if (declared is not null)
            return new ExchangeDomain(exchange, declared.Value.Name, declared.Value.Version, Modern: true);

        log.LogInformation("Exchange {Exchange} does not implement ERC-5267; assuming the v1 domain", exchange);
        return new ExchangeDomain(exchange, "Polymarket CTF Exchange", "1", Modern: false);
    }

    private async Task<ApiCreds> CreateOrDeriveApiKeyAsync(CancellationToken ct)
    {
        // POST creates on first use and is idempotent-ish afterwards; GET derives the
        // existing pair. Try both before giving up - which one works depends on whether
        // this wallet has ever traded.
        foreach (var (method, path) in new[] { (HttpMethod.Post, "/auth/api-key"), (HttpMethod.Get, "/auth/derive-api-key") })
        {
            try
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                using var request = new HttpRequestMessage(method, _opt.ClobUrl + path);
                request.Headers.TryAddWithoutValidation("POLY_ADDRESS", _signer!.Address);
                request.Headers.TryAddWithoutValidation("POLY_SIGNATURE", _signer.SignClobAuth(timestamp, 0));
                request.Headers.TryAddWithoutValidation("POLY_TIMESTAMP", timestamp.ToString());
                request.Headers.TryAddWithoutValidation("POLY_NONCE", "0");

                using var response = await http.SendAsync(request, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    log.LogWarning("{Method} {Path} returned {Status}: {Body}", method, path, (int)response.StatusCode, Trim(text));
                    continue;
                }

                using var doc = JsonDocument.Parse(text);
                var key = doc.RootElement.TryGetProperty("apiKey", out var k) ? k.GetString() : null;
                var secret = doc.RootElement.TryGetProperty("secret", out var s) ? s.GetString() : null;
                var passphrase = doc.RootElement.TryGetProperty("passphrase", out var p) ? p.GetString() : null;

                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(secret) && !string.IsNullOrEmpty(passphrase))
                    return new ApiCreds(key, secret, passphrase);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "{Method} {Path} failed", method, path);
            }
        }

        throw new InvalidOperationException(
            "Could not obtain CLOB API credentials. The usual cause is a wallet that has never been " +
            "connected to polymarket.com - make one manual trade there first, which also sets the " +
            "USDC approvals this bot cannot set for you.");
    }

    private async Task<HttpRequestMessage> SignL2Async(HttpMethod method, string path, string query, string? body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var request = new HttpRequestMessage(method, _opt.ClobUrl + path + query);

        // The HMAC covers the bare path only - no host, no query string. Including the
        // query here is the classic way to get a 401 that looks like a key problem.
        request.Headers.TryAddWithoutValidation("POLY_ADDRESS", _signer!.Address);
        request.Headers.TryAddWithoutValidation("POLY_SIGNATURE", PolymarketSigner.BuildHmac(_creds!.Secret, timestamp, method.Method, path, body));
        request.Headers.TryAddWithoutValidation("POLY_TIMESTAMP", timestamp.ToString());
        request.Headers.TryAddWithoutValidation("POLY_API_KEY", _creds.Key);
        request.Headers.TryAddWithoutValidation("POLY_PASSPHRASE", _creds.Passphrase);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        await Task.CompletedTask;
        return request;
    }

    /// <summary>
    /// USDC balance and exchange allowance for the configured holder, in dollars.
    /// Returns nulls when the call fails - which is itself the answer worth logging,
    /// because it usually means FunderAddress or SignatureType is wrong.
    /// </summary>
    public async Task<(decimal? Balance, decimal? Allowance)> GetBalanceAllowanceAsync(CancellationToken ct)
    {
        try
        {
            using var request = await SignL2Async(HttpMethod.Get, "/balance-allowance",
                $"?asset_type=COLLATERAL&signature_type={_opt.SignatureType}", null);
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Balance check returned {Status}: {Body}", (int)response.StatusCode, Trim(text));
                return (null, null);
            }

            using var doc = JsonDocument.Parse(text);
            var balance = ToUsd(doc.RootElement, "balance");
            var allowance = ReadAllowance(doc.RootElement);

            // A zero or unreadable answer is the one case worth seeing in full. It is
            // what stands between the operator and going live, and the field names have
            // changed across versions - so print the body rather than make them guess
            // whether the account is empty or the response simply was not understood.
            if (balance is null or 0 || allowance is null)
                log.LogWarning("Balance/allowance came back empty or unparsed. Raw response: {Body}", Trim(text));

            return (balance, allowance);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Balance check failed");
            return (null, null);
        }

        // USDC has 6 decimals and the API answers in base units.
        static decimal? ToUsd(JsonElement root, string name) =>
            TryDecimal(root, name, out var v) ? v / 1_000_000m : null;

        // The field has been a scalar, an array and a spender-keyed object across
        // versions. Take the largest value found: the question being asked is "is this
        // wallet approved for anything", not "approved for exactly which spender".
        static decimal? ReadAllowance(JsonElement root)
        {
            var scalar = ToUsd(root, "allowance");
            if (scalar is not null) return scalar;

            if (!root.TryGetProperty("allowances", out var a)) return null;

            decimal? best = null;
            var values = a.ValueKind switch
            {
                JsonValueKind.Array => a.EnumerateArray(),
                JsonValueKind.Object => a.EnumerateObject().Select(p => p.Value),
                _ => Enumerable.Empty<JsonElement>(),
            };

            foreach (var v in values)
            {
                var text = v.ValueKind switch
                {
                    JsonValueKind.Number => v.GetRawText(),
                    JsonValueKind.String => v.GetString(),
                    _ => null,
                };
                if (text is null || !System.Numerics.BigInteger.TryParse(text, out var raw)) continue;

                // An unlimited approval is 2^256-1 and does not fit in a decimal; every
                // caller only asks whether this is above zero, so saturate rather than
                // report "unparsed" for a perfectly healthy account.
                var scaled = raw >= new System.Numerics.BigInteger(decimal.MaxValue)
                    ? decimal.MaxValue
                    : (decimal)raw / 1_000_000m;

                if (best is null || scaled > best) best = scaled;
            }

            return best;
        }
    }

    // -- Order entry ----------------------------------------------------------

    /// <summary>
    /// Rests a good-til-cancelled buy for <paramref name="shares"/> of
    /// <paramref name="tokenId"/> at <paramref name="price"/>. The order sits on the book
    /// and fills only if the market later collapses through that price - which is the
    /// entire strategy. Returns as soon as the CLOB accepts it; filling may take minutes
    /// or never happen.
    /// </summary>
    public async Task<OrderResult> PlaceRestingBuyAsync(string tokenId, decimal price, decimal shares, bool negRisk, CancellationToken ct)
    {
        if (_signer is null || _creds is null) return new OrderResult(false, 0, 0, null, "not connected");

        var tick = await GetTickSizeAsync(tokenId, ct);
        var (priceDecimals, amountDecimals) = RoundingFor(tick);

        // Round the limit DOWN to the tick. A resting bid is a price we are willing to be
        // filled at, and rounding up would quietly pay more than the level the strategy
        // was measured on.
        var limit = Math.Max(tick, Math.Floor(price / tick) * tick);
        limit = Math.Round(limit, priceDecimals, MidpointRounding.AwayFromZero);

        // Size is always 2dp on Polymarket, rounded down so we never overspend.
        var size = Math.Floor(shares * 100m) / 100m;
        if (size <= 0) return new OrderResult(false, 0, 0, null, "size rounded to zero");

        var makerUsd = Math.Round(size * limit, amountDecimals, MidpointRounding.AwayFromZero);

        var exchange = negRisk ? _negRiskDomain! : _regularDomain!;

        var order = new OrderStruct
        {
            // Salt must stay inside JavaScript's safe integer range: the REST body carries
            // it as a JSON number and the exchange parses it back as one.
            Salt = Random.Shared.NextInt64(1, 1L << 52),
            Maker = HolderAddress,
            // A Deposit Wallet signs as itself: the account is a contract and the order
            // carries its address in both slots, with the EOA proving control through
            // ERC-1271. Putting the EOA in `signer` there gets the order refused with
            // "maker address not allowed, please use the deposit wallet flow".
            Signer = _opt.SignatureType == PolymarketSigner.SignatureTypePoly1271
                ? HolderAddress
                : _signer.Address,
            TokenId = tokenId,
            MakerAmount = ToBaseUnits(makerUsd),   // collateral we hand over
            TakerAmount = ToBaseUnits(size),       // shares we receive
            SideCode = 0,
            SignatureType = _opt.SignatureType,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        };

        var signature = _signer.SignOrder(order, negRisk);

        var payload = new SignedOrderPayload
        {
            Salt = order.Salt,
            Maker = order.Maker,
            Signer = order.Signer,
            TokenId = order.TokenId,
            MakerAmount = order.MakerAmount,
            TakerAmount = order.TakerAmount,
            Side = order.SideName,
            Expiration = order.Expiration,
            SignatureType = order.SignatureType,
            Signature = signature,

            // Only the fields the target exchange's own client sends. The serializer drops
            // the nulls, so neither side receives the other's vocabulary.
            Timestamp = exchange.Modern ? order.Timestamp : null,
            Metadata = exchange.Modern ? order.Metadata : null,
            Builder = exchange.Modern ? order.Builder : null,
            Taker = exchange.Modern ? null : order.Taker,
            Nonce = exchange.Modern ? null : order.Nonce,
            FeeRateBps = exchange.Modern ? null : order.FeeRateBps,
        };

        var body = JsonSerializer.Serialize(new PostOrderBody
        {
            DeferExec = false,
            Owner = _creds.Key,
            // GTC: rest on the book until filled or cancelled. See the class remarks -
            // this is the mechanism the measured edge actually runs on.
            OrderType = "GTC",
            Order = payload,
        }, Json);

        try
        {
            // The HMAC must cover exactly the bytes that go on the wire, so the serialized
            // string is signed and sent - never re-serialized in between.
            using var request = await SignL2Async(HttpMethod.Post, "/order", string.Empty, body);
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return new OrderResult(false, 0, 0, null, $"HTTP {(int)response.StatusCode}: {Trim(text)}");

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            var success = !(root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False);
            var error = root.TryGetProperty("errorMsg", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (!success || !string.IsNullOrEmpty(error))
                return new OrderResult(false, 0, 0, null, string.IsNullOrEmpty(error) ? Trim(text) : error);

            var orderId = root.TryGetProperty("orderID", out var o) ? o.GetString() : null;

            // A GTC order that matches nothing is the NORMAL case, not a failure: it is now
            // resting at 1c waiting for the market to come to it. Only a rejection is a
            // failure. Any immediate match is reported so the caller can count what it spent.
            var filled = FirstDecimal(root, "takingAmount", "sizeMatched", "matchedSize");
            var notional = FirstDecimal(root, "makingAmount");
            if (notional <= 0) notional = filled * limit;

            return new OrderResult(true, filled, notional, orderId, null);
        }
        catch (Exception ex)
        {
            return new OrderResult(false, 0, 0, null, ex.Message);
        }
    }

    /// <summary>
    /// Every order this wallet currently has resting. Read from the exchange on every
    /// sync, never cached to disk: it is what makes a restart safe without a database,
    /// because the exchange already knows what we placed yesterday.
    /// </summary>
    public async Task<IReadOnlyList<OpenOrder>> GetOpenOrdersAsync(CancellationToken ct)
    {
        if (_signer is null || _creds is null) return [];

        var orders = new List<OpenOrder>();
        var cursor = "MA==";   // INITIAL_CURSOR; "LTE=" is the end marker

        for (var page = 0; page < 50 && cursor != "LTE="; page++)
        {
            using var request = await SignL2Async(HttpMethod.Get, "/data/orders", $"?next_cursor={Uri.EscapeDataString(cursor)}", null);
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Open order read returned {Status}: {Body}", (int)response.StatusCode, Trim(text));
                break;
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            // The endpoint answers either a bare array or a {data, next_cursor} envelope.
            var items = root.ValueKind == JsonValueKind.Array ? root
                      : root.TryGetProperty("data", out var d) ? d
                      : default;
            if (items.ValueKind != JsonValueKind.Array) break;

            foreach (var o in items.EnumerateArray())
            {
                var id = Str(o, "id") ?? Str(o, "orderID") ?? Str(o, "order_id");
                var token = Str(o, "asset_id") ?? Str(o, "tokenID") ?? Str(o, "token_id");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(token)) continue;

                TryDecimal(o, "price", out var price);
                TryDecimal(o, "original_size", out var size);
                if (size == 0) TryDecimal(o, "size", out size);
                TryDecimal(o, "size_matched", out var matched);

                orders.Add(new OpenOrder(id, token, (Str(o, "side") ?? "BUY").ToUpperInvariant(), price, size, matched));
            }

            cursor = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("next_cursor", out var c)
                ? c.GetString() ?? "LTE="
                : "LTE=";
        }

        return orders;
    }

    /// <summary>Cancels orders by id. Returns how many the exchange confirmed.</summary>
    public async Task<int> CancelOrdersAsync(IReadOnlyList<string> orderIds, CancellationToken ct)
    {
        if (_signer is null || _creds is null || orderIds.Count == 0) return 0;

        var body = JsonSerializer.Serialize(orderIds, Json);
        try
        {
            using var request = await SignL2Async(HttpMethod.Delete, "/orders", string.Empty, body);
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Cancel of {Count} orders returned {Status}: {Body}", orderIds.Count, (int)response.StatusCode, Trim(text));
                return 0;
            }

            return orderIds.Count;
        }
        catch (Exception ex)
        {
            // A stale resting bid is real exposure: it can still be filled on a match
            // nobody is watching any more. Loud, not swallowed.
            log.LogError(ex, "Could not cancel {Count} resting orders - check them by hand on polymarket.com", orderIds.Count);
            return 0;
        }
    }

    private static string? Str(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Price and amount decimals per tick size, straight out of the official client's
    /// ROUNDING_CONFIG. Exceeding them makes the signed amounts disagree with what the
    /// exchange recomputes, and the order is rejected.
    /// </summary>
    private static (int Price, int Amount) RoundingFor(decimal tick) => tick switch
    {
        >= 0.1m    => (1, 3),
        >= 0.01m   => (2, 4),
        >= 0.001m  => (3, 5),
        _          => (4, 6),
    };

    private static string ToBaseUnits(decimal amount) =>
        decimal.ToInt64(Math.Round(amount * 1_000_000m, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    private static decimal FirstDecimal(JsonElement root, params string[] names)
    {
        foreach (var n in names)
            if (TryDecimal(root, n, out var v)) return v;
        return 0m;
    }

    private static string Short(string s) => s.Length <= 12 ? s : s[..12];
    private static string Trim(string s) => s.Length <= 300 ? s : s[..300];
}
