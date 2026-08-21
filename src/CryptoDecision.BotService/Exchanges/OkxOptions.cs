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

        return null;
    }
}
