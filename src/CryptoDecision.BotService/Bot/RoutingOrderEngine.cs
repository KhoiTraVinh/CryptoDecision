using CryptoDecision.Shared.Bot;
using CryptoDecision.Shared.Signals;

namespace CryptoDecision.BotService.Bot;

/// <summary>
/// Sends each order to the engine that should handle it.
///
/// Entries follow the current configuration; exits follow the trade. That
/// asymmetry is the whole reason this class exists. <c>paper_mode</c> lives in
/// bot_config and can be flipped through the API at any moment, including while
/// positions are open — and an exit routed by the current setting instead of by
/// the trade's own record would simulate the close of a real position (leaving
/// coins sitting on the exchange with the bot believing it had sold them), or
/// try to sell coins for a trade that was only ever imaginary.
///
/// So <see cref="OpenPositionAsync"/> reads <see cref="BotStateService.Options"/>,
/// and <see cref="CloseTradeAsync"/> reads <see cref="BotTrade.Mode"/> and
/// <see cref="BotTrade.Exchange"/>. A position always closes where it opened.
/// </summary>
public sealed class RoutingOrderEngine(
    BotStateService  state,
    PaperOrderEngine paper,
    OkxOrderEngine   okx,
    ILogger<RoutingOrderEngine> log) : IOrderEngine
{
    public string? DescribeRefusal(BotOptions opts)
    {
        if (opts.PaperMode)
            return paper.DescribeRefusal(opts);

        return ResolveVenue(opts.Exchange) switch
        {
            OkxOrderEngine.ExchangeName => okx.DescribeRefusal(opts),
            var venue => $"live trading is implemented for OKX only, but bot_config.exchange is " +
                         $"'{venue}'. Set the exchange to OKX, or run in paper mode.",
        };
    }

    public bool SupportsShort(BotOptions opts)
    {
        if (opts.PaperMode)
            return paper.SupportsShort(opts);

        return ResolveVenue(opts.Exchange) == OkxOrderEngine.ExchangeName
            && okx.SupportsShort(opts);
    }

    /// <summary>
    /// Routed by the trade, like <see cref="CloseTradeAsync"/> — asking the current
    /// configuration whether a position was closed elsewhere would check the wrong
    /// venue the moment paper_mode changes.
    /// </summary>
    public Task<BotTrade?> ReconcileAsync(BotTrade trade, CancellationToken ct)
    {
        if (!trade.IsLive)
            return paper.ReconcileAsync(trade, ct);

        return ResolveVenue(trade.Exchange) == OkxOrderEngine.ExchangeName
            ? okx.ReconcileAsync(trade, ct)
            : Task.FromResult<BotTrade?>(null);
    }

    public Task<BotTrade> OpenPositionAsync(
        string symbol, string strategy, string side, decimal price, decimal capitalUsd,
        decimal positionPct, CancellationToken ct, decimal confidence = 1.0m, bool useAiSizing = false,
        StopGeometry? geometry = null)
    {
        var opts = state.Options;

        if (opts.PaperMode)
            return paper.OpenPositionAsync(
                symbol, strategy, side, price, capitalUsd, positionPct, ct, confidence, useAiSizing,
                geometry);

        var venue = ResolveVenue(opts.Exchange);

        if (venue != OkxOrderEngine.ExchangeName)
            throw new InvalidOperationException(
                $"Live trading is implemented for OKX only; bot_config.exchange is '{venue}'. " +
                "No order was placed.");

        log.LogWarning(
            "[Router] Placing a LIVE order on {Venue} for {Symbol} — real funds. Strategy {Strategy}, side {Side}.",
            venue, symbol, strategy, side);

        return okx.OpenPositionAsync(
            symbol, strategy, side, price, capitalUsd, positionPct, ct, confidence, useAiSizing,
            geometry);
    }

    public Task<BotTrade> CloseTradeAsync(
        BotTrade trade, decimal exitPrice, string reason, CancellationToken ct)
    {
        if (!trade.IsLive)
            return paper.CloseTradeAsync(trade, exitPrice, reason, ct);

        var venue = ResolveVenue(trade.Exchange);

        if (venue != OkxOrderEngine.ExchangeName)
            throw new InvalidOperationException(
                $"Live trade {trade.Id} was opened on '{trade.Exchange}', which has no order engine " +
                "in this build. It cannot be closed automatically — close it on the exchange and " +
                "mark the row CLOSED by hand.");

        return okx.CloseTradeAsync(trade, exitPrice, reason, ct);
    }

    private static string ResolveVenue(string? exchange)
        => (exchange ?? "").Trim().ToUpperInvariant();
}
