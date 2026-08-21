using System.Collections.Concurrent;

namespace CryptoDecision.BotService.Exchanges;

/// <summary>
/// Translation between the internal symbol form and OKX instrument ids.
///
/// The rest of this codebase says BTCUSDT; OKX says BTC-USDT for spot and
/// BTC-USDT-SWAP for the USDT-margined perpetual. OkxNormalizer in the ingestion
/// service does the same translation in the opposite direction by stripping the
/// dash, which is lossless one way and ambiguous the other — you cannot tell
/// BTC/USDT from BTCU/SDT without knowing the quote currencies. The list below is
/// that knowledge, longest match first so USDT is not read as USD.
/// </summary>
public static class OkxSymbols
{
    private static readonly string[] QuoteCurrencies =
        ["USDT", "USDC", "TUSD", "EURT", "DAI", "EUR", "USD", "BTC", "ETH", "OKB"];

    private const string SwapSuffix = "-SWAP";

    /// <summary>Convert an internal symbol (BTCUSDT) to a spot instrument id (BTC-USDT).</summary>
    public static string ToSpotInstId(string symbol)
    {
        var s = (symbol ?? "").Trim().ToUpperInvariant();

        if (s.Length == 0)
            throw new ArgumentException("Symbol is empty.", nameof(symbol));

        if (s.EndsWith(SwapSuffix, StringComparison.Ordinal))
            s = s[..^SwapSuffix.Length];

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

    /// <summary>
    /// Convert an internal symbol (BTCUSDT) to the perpetual swap instrument id
    /// (BTC-USDT-SWAP). This is the instrument the bot trades, and therefore the
    /// one whose price, tick size and contract value everything else must use —
    /// a perp trades at a basis to spot, so the two books are not interchangeable
    /// for a stop-loss calculation.
    /// </summary>
    public static string ToSwapInstId(string symbol)
    {
        var s = (symbol ?? "").Trim().ToUpperInvariant();

        return s.EndsWith(SwapSuffix, StringComparison.Ordinal)
            ? s
            : ToSpotInstId(s) + SwapSuffix;
    }
}

/// <summary>Order size arithmetic against an instrument's lot grid.</summary>
public static class OkxSizing
{
    /// <summary>
    /// Round a size down onto the instrument's lot grid.
    ///
    /// Down, never to nearest: rounding up on an entry commits more than the
    /// position size allows, and rounding up on an exit tries to close more than
    /// is held. Both are rejected by the exchange, the second only after the first
    /// leg has already been paid for.
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
    /// equivalent: rounding a long's stop down moves it further from entry and
    /// quietly widens the risk past what was configured.
    /// </summary>
    public static decimal CeilToStep(decimal value, decimal step)
    {
        if (step <= 0m) return value;
        return decimal.Ceiling(value / step) * step;
    }
}

/// <summary>
/// Instrument rules for the perpetuals this bot trades, fetched once per process.
///
/// Cached for the process lifetime. Contract value, lot and tick sizes change on
/// the order of exchange announcements, not minutes, and the alternative — a
/// public REST call on the path of every order — adds latency to the one
/// operation that is timing sensitive. A container restart picks up any change.
/// </summary>
public sealed class OkxInstrumentCache(
    OkxSignedClient client,
    ILogger<OkxInstrumentCache> log)
{
    private readonly ConcurrentDictionary<string, OkxInstrument> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Rules for the USDT-margined perpetual swap behind an internal symbol.</summary>
    public async Task<OkxInstrument> GetSwapAsync(string symbol, CancellationToken ct)
    {
        var instId = OkxSymbols.ToSwapInstId(symbol);

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
                $"/api/v5/public/instruments?instType=SWAP&instId={instId}", ct);

            var instrument = found.FirstOrDefault(i => i.InstId == instId)
                ?? throw new OkxApiException("UNKNOWN_INSTRUMENT",
                    $"OKX lists no perpetual swap '{instId}' (from symbol '{symbol}').");

            if (!instrument.IsLive)
                throw new OkxApiException("INSTRUMENT_NOT_LIVE",
                    $"OKX instrument '{instId}' is in state '{instrument.State}', not live.");

            // A linear perp settles in the quote currency. An inverse one (settled
            // in the coin) inverts every P&L and margin calculation in this codebase,
            // so it is refused rather than silently mispriced.
            if (!string.Equals(instrument.SettleCcy, instrument.QuoteCcy, StringComparison.OrdinalIgnoreCase))
                throw new OkxApiException("INVERSE_CONTRACT_UNSUPPORTED",
                    $"'{instId}' settles in {instrument.SettleCcy}, not {instrument.QuoteCcy}. " +
                    "This engine only handles linear (quote-settled) perpetuals.");

            _cache[instId] = instrument;

            log.LogInformation(
                "[OKX] Instrument {InstId}: ctVal={CtVal} {CtValCcy} lotSz={Lot} minSz={Min} " +
                "tickSz={Tick} settle={Settle}",
                instId, instrument.ContractValue, instrument.CtValCcy, instrument.LotSize,
                instrument.MinSize, instrument.TickSize, instrument.SettleCcy);

            return instrument;
        }
        finally
        {
            _gate.Release();
        }
    }
}
