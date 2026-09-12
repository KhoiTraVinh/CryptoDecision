namespace CryptoDecision.IngestionService.Configuration;

/// <summary>
/// Which markets the exchange WebSocket clients subscribe to.
///
/// Pairs are configured in dashed form — "SOL-USDT" — because that is the
/// direction of the conversion that cannot go wrong. Removing the dash to get
/// OKX's instId into the internal "SOLUSDT" form is unambiguous; putting one back
/// requires knowing where the base currency ends, and "BTCUSDT" could be BTC/USDT
/// or BTCU/SDT to anything that does not already hold a list of quote currencies.
/// OkxNormalizer strips dashes for exactly this reason; this is the same fact,
/// used before the connection rather than after it.
///
/// Kept out of the WebSocket clients themselves so that changing which coin the
/// bot trades is a config edit and a restart, not a rebuild. The subscription had
/// previously been a compiled-in byte array in each client, which meant the answer
/// to "what are we trading" lived in three different places, two of them in C#.
/// </summary>
public sealed class MarketSubscriptionSettings
{
    public const string Section = "MarketSubscription";

    /// <summary>
    /// Pairs in dashed form, e.g. ["SOL-USDT"].
    ///
    /// THESE ARE SPOT INSTRUMENTS, AND THE BOT TRADES THE PERPETUAL. All three clients
    /// subscribe to the spot book — Binance's solusdt@trade, OKX's SOL-USDT, Bybit's
    /// /v5/public/spot — while OkxOrderEngine places orders on SOL-USDT-SWAP and
    /// OkxPriceFeed quotes it. klines_1m is Binance spot as well, so the ATR and the
    /// range boundaries are measured there too.
    ///
    /// Every number the entry rule is calibrated against therefore comes from a
    /// different instrument than the one the position is in: the $3M bucket floor, the
    /// 2.1:1 ratio, the per-venue medians ($7.0M / $2.07M / $0.89M), and the $20M
    /// news-print threshold are all spot notional. A perp carries its own flow —
    /// liquidation cascades and funding-driven positioning that never touch the spot
    /// tape — which is precisely the kind of event the high-volume waiver is trying to
    /// catch, and it is the half this feed cannot see.
    ///
    /// This is not a defect to fix by editing the list. Switching to SOL-USDT-SWAP would
    /// change what every open hypothesis was measured on, and H9 and H11 are both live
    /// against the spot-derived numbers. It is written down because it was not written
    /// down anywhere, and an unstated assumption is how this repository loses weeks.
    ///
    /// Empty by default, and it has to stay that way. The .NET configuration binder
    /// <em>appends</em> to a collection property that already holds items rather than
    /// replacing it, so a non-empty default plus a config value yields both — which
    /// on first run subscribed to SOL-USDT twice and double-counted every trade in
    /// the momentum flow. BinanceSettings.Streams defaults to empty for the same
    /// reason.
    /// </summary>
    public string[] Pairs { get; set; } = [];

    /// <summary>OKX instrument ids — the dashed form, used verbatim.</summary>
    public IEnumerable<string> OkxInstIds => Normalised;

    /// <summary>Bybit symbols — the dashed form with the dash removed.</summary>
    public IEnumerable<string> BybitSymbols =>
        Normalised.Select(p => p.Replace("-", "", StringComparison.Ordinal));

    /// <summary>
    /// Throw if nothing is configured.
    ///
    /// An empty list is not a quiet no-op, it is the worst kind of failure this
    /// service has: both clients would send a subscribe frame with an empty args
    /// array, both connections would succeed, the channel health check would report
    /// healthy because the channels are empty rather than backed up, and the
    /// container would sit green forever with no data reaching Postgres. The bot
    /// downstream then reads an empty order book and never leaves the dead zone,
    /// with nothing anywhere saying why. Failing at startup is the only version of
    /// this that an operator can see.
    /// </summary>
    public void Validate()
    {
        if (!Normalised.Any())
            throw new InvalidOperationException(
                $"No market pairs configured. Set {Section}:Pairs to at least one pair in dashed " +
                "form, e.g. [\"SOL-USDT\"]. Without it the exchange clients would connect, subscribe " +
                "to nothing, and report healthy while ingesting no data.");
    }

    /// <summary>
    /// Deduplicated, because a repeated pair is a repeated subscription: the same
    /// trade arrives twice, and every buy/sell ratio computed from it counts that
    /// trade twice. Cheap to guard against here, invisible if it happens.
    /// </summary>
    private IEnumerable<string> Normalised => Pairs
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => p.Trim().ToUpperInvariant())
        .Distinct(StringComparer.Ordinal);
}
