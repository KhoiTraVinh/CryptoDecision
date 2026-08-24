namespace CryptoDecision.BotService.Exchanges;

/// <summary>
/// Credentials and guard rails for live order placement on OKX.
///
/// Two separate switches have to be thrown before a real order can leave this
/// process: <see cref="EnableLiveTrading"/> here, which is deployment
/// configuration an operator sets on the container, and <c>bot_config.paper_mode
/// = false</c>, which is a runtime decision made through the API. Neither alone
/// is enough. The split is deliberate — the API is reachable from the dashboard,
/// and a single mis-click there should not be able to start spending real money
/// on a deployment that was never provisioned for it.
/// </summary>
public sealed class OkxOptions
{
    /// <summary>REST host. Same host serves live and demo; demo is selected by a header.</summary>
    public string BaseUrl { get; set; } = "https://www.okx.com";

    // ── Futures (USDT-margined perpetual swaps) ──
    //
    // Perps are traded rather than spot because the signal is symmetric: the
    // strategy scores both directions and a cash account can only act on half of
    // them. What comes with that is liquidation, which spot does not have — so
    // leverage stays low and the margin mode stays isolated by default.

    /// <summary>
    /// Leverage applied per instrument before the first order.
    ///
    /// Low on purpose. Leverage does not change the edge, only how fast the
    /// account reaches zero when the edge is absent: at 3x a 1.5% stop is a 4.5%
    /// equity move, while liquidation sits roughly 33% away — a wide margin for
    /// the stop to work in. At 20x the stop and the liquidation price are close
    /// enough that a single wick can settle the position before the stop does.
    /// </summary>
    public decimal Leverage { get; set; } = 3m;

    /// <summary>
    /// Margin mode: "isolated" or "cross".
    ///
    /// Isolated by default. It caps the loss on a position at the margin posted
    /// for it; cross margin puts the whole account balance behind every open
    /// position, so one bad trade can take the others with it. For an unattended
    /// bot that difference is the whole point.
    /// </summary>
    public string MarginMode { get; set; } = "isolated";

    public string ApiKey     { get; set; } = "";
    public string ApiSecret  { get; set; } = "";
    public string Passphrase { get; set; } = "";

    /// <summary>
    /// Send <c>x-simulated-trading: 1</c>, routing every order to OKX's demo
    /// account instead of the funded one. Defaults to true: the safe value is the
    /// one you get by forgetting to set it.
    /// </summary>
    public bool DemoTrading { get; set; } = true;

    /// <summary>
    /// Master arm switch. While false, no order is placed on OKX under any
    /// bot_config setting, and the bot refuses to start out of paper mode rather
    /// than quietly simulating what the operator asked to be real.
    /// </summary>
    public bool EnableLiveTrading { get; set; } = false;

    /// <summary>
    /// Hard ceiling on a single order's notional, in USD, applied after position
    /// sizing. This is the backstop against a fat-fingered capital_usd: sizing is
    /// a percentage of a number the API accepts without an upper bound, so a
    /// misplaced zero there would otherwise become a real order.
    /// </summary>
    public decimal MaxOrderNotionalUsd { get; set; } = 100m;

    /// <summary>
    /// Refuse to place an order if the resolved notional is below this. OKX will
    /// reject dust orders anyway; catching it here produces a readable log line
    /// instead of an exchange error code.
    /// </summary>
    public decimal MinOrderNotionalUsd { get; set; } = 5m;

    /// <summary>How many times to poll an order for its fill before giving up.</summary>
    public int FillPollAttempts { get; set; } = 12;

    /// <summary>Delay between fill polls, in milliseconds.</summary>
    public int FillPollDelayMs { get; set; } = 500;

    /// <summary>Per-request timeout for the OKX REST API, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    // ── Maker entries ─────────────────────────────────────────────────────────

    /// <summary>
    /// Enter by resting a post-only order on the book instead of crossing the spread.
    ///
    /// This is the largest remaining cost lever, and for a signal with a small gross
    /// edge it is close to decisive. OKX charges roughly 5 bps taker and 2 bps maker
    /// per side on USDT-margined swaps, so a taker-in/taker-out round trip costs about
    /// 10 bps against about 4 for maker-in. Published work on order-flow-imbalance
    /// strategies in crypto perpetuals found them net negative at an assumed 4 bps
    /// round trip — which puts 10 bps well outside anything the signal can pay for,
    /// and 4 bps merely at the edge.
    ///
    /// What it costs: a resting order is not an entry until someone trades against it,
    /// so some signals are simply missed. That is the right trade here — a missed
    /// entry costs an opportunity, while 6 extra basis points costs money on every
    /// trade actually taken. It is only the right trade because the signal is held for
    /// hours; for a strategy that needed to be in within seconds it would not be.
    ///
    /// Exits stay taker regardless. A stop that waits for a fill is not a stop.
    /// </summary>
    public bool UseMakerEntries { get; set; } = true;

    /// <summary>
    /// How long to leave an entry order resting, expressed as polls × delay.
    ///
    /// 60 × 1000ms = one minute. Bounded because the price the signal was scored at
    /// goes stale: an order still resting after several minutes would, if it filled,
    /// be entering on evidence that has since been superseded. Cancelling and letting
    /// the next cycle re-decide is the honest handling.
    /// </summary>
    public int MakerFillPollAttempts { get; set; } = 60;

    /// <summary>Delay between polls while an entry order rests, in milliseconds.</summary>
    public int MakerFillPollDelayMs { get; set; } = 1_000;

    /// <summary>
    /// How many ticks inside the touch to rest, on the passive side.
    ///
    /// Zero rests at the best bid (buying) or best ask (selling): the front of the
    /// queue, the highest fill probability that is still maker. A positive value
    /// steps away from the touch, which improves the price and lowers the chance of
    /// filling at all. Zero is the right default — the edge being chased here is the
    /// fee saving, not another basis point of entry price.
    /// </summary>
    public int MakerPriceOffsetTicks { get; set; } = 0;

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(ApiSecret)
        && !string.IsNullOrWhiteSpace(Passphrase);

    /// <summary>
    /// Null when this configuration could place a real order; otherwise the reason
    /// it cannot, phrased for an operator reading a log line.
    /// </summary>
    public string? DescribeRefusal()
    {
        if (!EnableLiveTrading)
            return "live trading is not armed on this deployment (Okx:EnableLiveTrading is false).";

        if (!HasCredentials)
            return "OKX API credentials are not configured (Okx:ApiKey, Okx:ApiSecret, Okx:Passphrase).";

        if (MaxOrderNotionalUsd <= 0m)
            return $"Okx:MaxOrderNotionalUsd is {MaxOrderNotionalUsd}, so every order would be refused.";

        if (Leverage < 1m || Leverage > 125m)
            return $"Okx:Leverage is {Leverage}, outside the 1-125 range OKX accepts.";

        if (MarginMode is not ("isolated" or "cross"))
            return $"Okx:MarginMode is '{MarginMode}'; it must be 'isolated' or 'cross'.";

        return null;
    }

    /// <summary>
    /// Price move against the position that would liquidate it, as a fraction —
    /// roughly 1/leverage on isolated margin, before maintenance margin and fees
    /// eat into it. Approximate by design: it is used to keep the configured stop
    /// loss a wide distance clear of liquidation, not to predict the exact level.
    /// </summary>
    public decimal ApproxLiquidationDistance => Leverage > 0m ? 1m / Leverage : 1m;
}
