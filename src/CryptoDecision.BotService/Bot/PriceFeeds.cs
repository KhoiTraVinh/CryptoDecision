using System.Net.Http.Json;
using CryptoDecision.BotService.Exchanges;
using CryptoDecision.Shared.Bot;

namespace CryptoDecision.BotService.Bot;

/// <summary>A source of the current price for a symbol.</summary>
public interface IPriceFeed
{
    /// <summary>Venue this feed reports, matching BotOptions.Exchange.</summary>
    string Venue { get; }

    /// <summary>Current price, or null when the feed is unavailable this cycle.</summary>
    Task<decimal?> GetPriceAsync(string symbol, CancellationToken ct);
}

/// <summary>Binance public ticker. The original feed, and still the paper-mode default.</summary>
public sealed class BinancePriceFeed(
    IHttpClientFactory httpFactory,
    ILogger<BinancePriceFeed> log) : IPriceFeed
{
    public string Venue => "BINANCE";

    public async Task<decimal?> GetPriceAsync(string symbol, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient("binance-public");
            var resp = await http.GetFromJsonAsync<BinancePriceTicker>(
                $"/api/v3/ticker/price?symbol={symbol}", ct);
            return resp?.Price;
        }
        // The client's OWN timeout, not the caller's cancellation. HttpClient.Timeout
        // raises TaskCanceledException, which is an OperationCanceledException, so the
        // filter below deliberately does not catch it and it used to leave this method
        // entirely -- see the OKX feed for what that cost.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(
                "[Price] Binance ticker for {Symbol} timed out on the client's own deadline. " +
                "No price this cycle.", symbol);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("[Price] Binance ticker for {Symbol} failed: {Err}", symbol, ex.Message);
            return null;
        }
    }
}

/// <summary>
/// OKX perpetual swap ticker — the book live orders are actually matched against.
///
/// The swap instrument, not spot. A perp trades at a basis to spot that widens
/// with funding pressure, and every threshold derived from this price — entry,
/// take profit, stop loss, trailing — is compared against fills on the swap book.
/// Reading spot would put a systematic offset into all of them.
/// </summary>
public sealed class OkxPriceFeed(
    OkxTradingClient trading,
    ILogger<OkxPriceFeed> log) : IPriceFeed
{
    public string Venue => OkxOrderEngine.ExchangeName;

    public async Task<decimal?> GetPriceAsync(string symbol, CancellationToken ct)
    {
        try
        {
            return await trading.GetLastPriceAsync(OkxSymbols.ToSwapInstId(symbol), ct);
        }
        // THIS IS THE ONE THAT BIT, on 2026-09-24 at 21:51:13 UTC.
        //
        // Okx.TimeoutSeconds is 10. When the ticker call hit it, HttpClient raised
        // TaskCanceledException -- an OperationCanceledException -- which the filter
        // below excludes on purpose, so it was not caught here at all. It travelled out
        // of the price feed, out of EvalCycleAsync, and into the loop's own handler,
        // which has no way to tell one cancellation from another and reported it as
        // "Evaluation cycle exceeded its 00:02:00 budget and was cancelled after 10s" --
        // a sentence that contradicts itself, since 10s is not past a 120s budget.
        //
        // That single line is also the first named reject condition of H28: any "open
        // positions were not evaluated this cycle" is supposed to mean the one-hour
        // review cadence overloaded Ollama, and the correct response is to revert to two
        // hours. A ten-second exchange timeout would have fired it and rolled back a
        // hypothesis for a reason that has nothing to do with it.
        //
        // The caller's token being un-cancelled is what separates the two: the client's
        // own deadline leaves `ct` alone, a real shutdown or the cycle deadline does not.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(
                "[Price] OKX ticker for {Symbol} timed out on the client's own deadline. " +
                "No price this cycle; the cycle itself is unaffected.", symbol);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("[Price] OKX ticker for {Symbol} failed: {Err}", symbol, ex.Message);
            return null;
        }
    }
}

/// <summary>
/// Picks the price feed that matches where orders are going.
///
/// This is not a cosmetic preference. Entry price, the take-profit and stop-loss
/// thresholds derived from it, and the P&amp;L computed against it are all
/// arithmetic on one number — and if that number comes from a different order book
/// than the one the trade fills on, every one of them is measuring a market the
/// position is not in. Two venues quoting BTC-USDT differ by a small amount most
/// of the time, and by a much larger one exactly when a stop matters.
///
/// Paper mode keeps whatever venue the configuration names, so a simulated run
/// stays comparable to the live run it is meant to predict.
/// </summary>
public sealed class PriceFeedResolver
{
    private readonly IReadOnlyDictionary<string, IPriceFeed> _feeds;
    private readonly IPriceFeed _fallback;
    private readonly ILogger<PriceFeedResolver> _log;

    public PriceFeedResolver(
        IEnumerable<IPriceFeed> feeds,
        BinancePriceFeed fallback,
        ILogger<PriceFeedResolver> log)
    {
        _feeds    = feeds.ToDictionary(f => f.Venue, StringComparer.OrdinalIgnoreCase);
        _fallback = fallback;
        _log      = log;

        _log.LogInformation("[Price] Feeds available: [{Venues}]", string.Join(", ", _feeds.Keys));
    }

    public async Task<decimal?> GetPriceAsync(BotOptions opts, CancellationToken ct)
    {
        var venue = (opts.Exchange ?? "").Trim();

        if (!_feeds.TryGetValue(venue, out var feed))
        {
            _log.LogWarning(
                "[Price] No feed for venue '{Venue}', falling back to {Fallback}. " +
                "Prices will not match the venue orders are placed on.",
                venue, _fallback.Venue);
            feed = _fallback;
        }

        var price = await feed.GetPriceAsync(opts.Symbol, ct);

        // A missing price is a normal transient outcome, but for a live position it
        // means no exit was evaluated this cycle — worth saying out loud rather than
        // returning a silent null.
        if (price is null && !opts.PaperMode)
            _log.LogWarning(
                "[Price] {Venue} returned no price for {Symbol}. No exits were evaluated this " +
                "cycle; open positions rely on their exchange-side stop until the feed recovers.",
                feed.Venue, opts.Symbol);

        return price;
    }
}

internal sealed record BinancePriceTicker(string Symbol, decimal Price);
