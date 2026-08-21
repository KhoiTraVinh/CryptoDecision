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
    /// Deduplicated, because a repeated pair is a repeated subscription: the same
    /// trade arrives twice, and every buy/sell ratio computed from it counts that
    /// trade twice. Cheap to guard against here, invisible if it happens.
    /// </summary>
    private IEnumerable<string> Normalised => Pairs
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => p.Trim().ToUpperInvariant())
        .Distinct(StringComparer.Ordinal);
}
