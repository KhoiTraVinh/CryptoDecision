using System.Collections.Concurrent;

namespace CryptoDecision.BotService.Exchanges;

/// <summary>
/// Translation between the internal symbol form and OKX instrument ids.
///
/// The rest of this codebase says BTCUSDT; OKX says BTC-USDT. OkxNormalizer in
/// the ingestion service does the same translation in the opposite direction by
/// stripping the dash, which is lossless one way and ambiguous the other — you
/// cannot tell BTC/USDT from BTCU/SDT without knowing the quote currencies. The
/// list below is that knowledge, longest match first so USDT is not read as USD.
/// </summary>
public static class OkxSymbols
{
    private static readonly string[] QuoteCurrencies =
        ["USDT", "USDC", "TUSD", "EURT", "DAI", "EUR", "USD", "BTC", "ETH", "OKB"];

    /// <summary>
    /// Convert an internal symbol (BTCUSDT) to an OKX instrument id (BTC-USDT).
    /// A symbol that already carries a dash is passed through unchanged.
    /// </summary>
    public static string ToInstId(string symbol)
    {
        var s = (symbol ?? "").Trim().ToUpperInvariant();

        if (s.Length == 0)
            throw new ArgumentException("Symbol is empty.", nameof(symbol));

        if (s.Contains('-'))
            return s;

        foreach (var quote in QuoteCurrencies)
            if (s.Length > quote.Length && s.EndsWith(quote, StringComparison.Ordinal))
                return $"{s[..^quote.Length]}-{quote}";

        throw new ArgumentException(
            $"Cannot split '{s}' into an OKX instrument id: its quote currency is not one of " +
            $"{string.Join(", ", QuoteCurrencies)}. Add it to OkxSymbols or configure the " +
            "symbol as 'BASE-QUOTE' directly.",
            nameof(symbol));
    }
}

/// <summary>Order size arithmetic against an instrument's lot grid.</summary>
public static class OkxSizing
{
    /// <summary>
    /// Round a size down onto the instrument's lot grid.
    ///
    /// Down, never to nearest: rounding up on an entry asks to spend more than the
    /// position size allows, and rounding up on an exit asks to sell coins that are
    /// not there. Both are rejected by the exchange, the second only after the
    /// first leg has already been paid for.
    /// </summary>
    public static decimal FloorToStep(decimal value, decimal step)
    {
        if (step <= 0m) return value;
        return decimal.Floor(value / step) * step;
    }

    /// <summary>
    /// Round a value up onto a step grid.
    ///
    /// Used for a stop-loss trigger price, where the two directions are not
    /// equivalent: rounding a stop down moves it further from entry and quietly
    /// widens the risk past what was configured. Rounding up keeps the stop at or
    /// inside its intended level.
    /// </summary>
    public static decimal CeilToStep(decimal value, decimal step)
    {
        if (step <= 0m) return value;
        return decimal.Ceiling(value / step) * step;
    }
}

/// <summary>
/// Instrument rules for the pairs this bot trades, fetched once per process.
///
/// Cached for the process lifetime. Lot and tick sizes change on the order of
/// exchange announcements, not minutes, and the alternative — a public REST call
/// on the path of every order — adds latency to the one operation that is timing
/// sensitive. A container restart picks up any change.
/// </summary>
public sealed class OkxInstrumentCache(
    OkxSignedClient client,
    ILogger<OkxInstrumentCache> log)
{
    private readonly ConcurrentDictionary<string, OkxInstrument> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<OkxInstrument> GetSpotAsync(string symbol, CancellationToken ct)
    {
        var instId = OkxSymbols.ToInstId(symbol);

        if (_cache.TryGetValue(instId, out var cached))
            return cached;

        // Serialised so a burst of first-time orders on the same pair makes one
        // request rather than one each.
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(instId, out cached))
                return cached;

            var found = await client.GetPublicAsync<OkxInstrument>(
                $"/api/v5/public/instruments?instType=SPOT&instId={instId}", ct);

            var instrument = found.FirstOrDefault(i => i.InstId == instId)
                ?? throw new OkxApiException("UNKNOWN_INSTRUMENT",
                    $"OKX lists no spot instrument '{instId}' (from symbol '{symbol}').");

            if (!instrument.IsLive)
                throw new OkxApiException("INSTRUMENT_NOT_LIVE",
                    $"OKX instrument '{instId}' is in state '{instrument.State}', not live.");

            _cache[instId] = instrument;

            log.LogInformation(
                "[OKX] Instrument {InstId}: lotSz={Lot} minSz={Min} tickSz={Tick} ({Base}/{Quote})",
                instId, instrument.LotSize, instrument.MinSize, instrument.TickSize,
                instrument.BaseCcy, instrument.QuoteCcy);

            return instrument;
        }
        finally
        {
            _gate.Release();
        }
    }
}
