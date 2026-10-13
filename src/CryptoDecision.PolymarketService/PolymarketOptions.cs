namespace CryptoDecision.PolymarketService;

/// <summary>
/// Everything the esports entry bot needs. Bound from the "Polymarket" configuration
/// section; every value is overridable with a <c>Polymarket__Name</c> environment
/// variable, which is how the secrets are expected to arrive.
/// </summary>
public sealed class PolymarketOptions
{
    public const string Section = "Polymarket";

    // ── Wallet ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Private key of the self-custody wallet (OKX Web3 Wallet, MetaMask, ...).
    /// This is NOT an exchange account: an OKX *exchange* login cannot sign Polymarket
    /// orders, because the CLOB wants an EIP-712 signature and an exchange never hands
    /// out the key. Supply it as <c>Polymarket__PrivateKey</c>; never commit it.
    /// </summary>
    public string PrivateKey { get; set; } = "";

    /// <summary>
    /// How Polymarket should interpret the signature:
    ///   0 = EOA          — you trade straight from the wallet address itself
    ///   1 = POLY_PROXY   — you signed up with email / Magic link
    ///   2 = POLY_GNOSIS_SAFE — a browser wallet with a Safe proxy holding the collateral
    ///   3 = POLY_1271      — a Deposit Wallet: a contract account that verifies via
    ///                      ERC-1271. Orders carry its address as BOTH maker and signer
    /// Connecting OKX Web3 Wallet to polymarket.com normally lands on 2. If orders come
    /// back rejected or the preflight balance reads 0, this and <see cref="FunderAddress"/>
    /// are the first two things to check — they are the only pair that can be wrong
    /// *and* still look plausible.
    /// </summary>
    public int SignatureType { get; set; } = 2;

    /// <summary>
    /// The address that actually HOLDS the USDC and the shares — the "maker" on every
    /// order. For SignatureType 2 this is the Safe proxy address Polymarket shows on its
    /// Deposit page, NOT your OKX wallet address. For SignatureType 0 leave it empty and
    /// the wallet address is used. Getting this wrong means orders are signed for an
    /// account with no money in it.
    /// </summary>
    public string FunderAddress { get; set; } = "";

    // ── Safety ────────────────────────────────────────────────────────────────

    /// <summary>
    /// When true the bot does everything except send the order: it scans, decides, signs
    /// nothing and logs the entry it would have made. Default ON. Flip to false only
    /// after a preflight run has shown the right address and a non-zero USDC balance.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>USD per entry, treated as a ceiling: 20 whole shares at 1c, 6 at 3c ($0.18).</summary>
    public decimal StakeUsd { get; set; } = 0.20m;

    /// <summary>
    /// Hard ceiling on USD spent per UTC day. Counted in memory, so a restart resets it —
    /// it is a runaway brake, not an accounting record. **0 disables it.**
    ///
    /// Disabling is the right call when the cap would bind on an ordinary day, because a
    /// cap that binds does not reduce risk so much as choose the sample by clock: it
    /// keeps every entry up to some hour and discards the rest, which is the one
    /// selection rule guaranteed to correlate with nothing useful. Size the stake instead
    /// so the expected daily spend fits the runway.
    /// </summary>
    public decimal MaxDailyUsd { get; set; } = 15m;

    // ── Strategy ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Price levels to buy at, in cents-as-fraction. Each level is its own disjoint band:
    /// 0.03 fires when the best ask sits in (0.01, 0.03], 0.01 fires when it sits at or
    /// below 0.01. Disjoint on purpose — a token that drifts down from 3c to 1c is allowed
    /// to trigger both, once each, and never more.
    ///
    /// Measured on @greenworld over 36 days: the 1c leg hit 1.74% against a 1.00%
    /// break-even (z = 3.36). The 3c leg has NO measurement behind it at all — it needs
    /// 3.00% to break even and nothing here says it gets there.
    ///
    /// Empty by default on purpose. Configuration binding APPENDS to a collection that
    /// already has items rather than replacing it, so a non-empty initializer here would
    /// silently turn the appsettings value into a duplicate-laden union of the two.
    /// Defaults are applied in <see cref="Validate"/> instead.
    /// </summary>
    public decimal[] EntryPrices { get; set; } = [];

    /// <summary>
    /// Comma-separated override for <see cref="EntryPrices"/>, e.g. "0.01" or "0.03,0.01".
    /// Exists because a .NET array needs one indexed environment variable per element and
    /// leaves stale indexes behind when the list shrinks — the exact mistake that would
    /// quietly keep a level you meant to remove. When set, this replaces the list outright.
    /// </summary>
    public string EntryPricesCsv { get; set; } = "";

    /// <summary>
    /// Never place a level below this. At 0.002 a one-tick move is 50% of the position's
    /// value, so buying the ask and marking at the bid is an instant -50% with the price
    /// unmoved. Every sub-cent fill in the measured sample lost.
    /// </summary>
    public decimal MinAskPrice { get; set; } = 0.01m;

    /// <summary>
    /// Don't commit to a side until its price has already fallen this far. Below this,
    /// the team is visibly losing badly and the bet is on a comeback; above it, we would
    /// be pre-placing a 1c bid on a team that is merely the underdog.
    ///
    /// This is the difference between the two populations the strategy can buy. A bid
    /// left resting at 1c from the start fills on ANY side that passes through 1c —
    /// including a map already decided, where the price is falling through on its way to
    /// zero and there is no match left to come back in. Arming late buys only sides that
    /// collapsed while the game was still being played.
    ///
    /// It costs fill rate, and queue position with it: the 1c level on a busy market can
    /// already hold six figures of resting size, and arriving later means arriving behind
    /// it. Not measurable from the sample — the reference account's history shows what it
    /// bought, never when it placed — so this is a judgement call, and the bot logs the
    /// ask at arming time so it can be settled from its own fills later.
    ///
    /// Set to 1.0 to go back to arming as soon as a market appears.
    /// </summary>
    public decimal ArmBelowAsk { get; set; } = 0.15m;

    /// <summary>
    /// Rest on both outcomes of a match instead of one.
    ///
    /// The measured account averaged 1.01 positions per market over 2,169 markets and held
    /// both sides on only 24 of them - which is what resting on both sides looks like when
    /// only the collapsing one ever fills. It is also not a hedge: on the rare market where
    /// both fill you paid $0.40 for a ticket that pays out whichever team wins.
    ///
    /// Off by default because "one side only" is what was asked for. Turning it on roughly
    /// doubles the orders placed and the daily spend.
    /// </summary>
    public bool RestBothSides { get; set; }

    /// <summary>
    /// Skip markets that have traded less than this. Measured per volume bucket over the
    /// sample: under $20k the strategy lost money; at and above $50k it hit 2.20%.
    /// NOTE the bucket numbers are measured on *final* market volume, which is inflated
    /// by the comeback itself — the bot filters on volume seen AT ENTRY and logs it, so
    /// the question can be re-answered later without the bias.
    /// </summary>
    public decimal MinVolumeUsd { get; set; } = 50_000m;

    /// <summary>
    /// Games to play. Matched against the leading word of the market question.
    /// Empty by default for the same binding reason as <see cref="EntryPrices"/>.
    /// </summary>
    public string[] Games { get; set; } = [];

    /// <summary>Ignore matches scheduled to end further out than this. Cuts the scan universe.</summary>
    public double MaxHoursToEnd { get; set; } = 24;

    /// <summary>Don't enter in the dying minutes; the book is thin and resolution is a coin-flip away.</summary>
    public double MinMinutesToEnd { get; set; } = 2;

    // ── Cadence ───────────────────────────────────────────────────────────────

    public int ScanIntervalSeconds { get; set; } = 20;
    public int MarketRefreshSeconds { get; set; } = 120;
    public int PositionRefreshSeconds { get; set; } = 300;

    // ── Endpoints ─────────────────────────────────────────────────────────────

    public string ClobUrl { get; set; } = "https://clob.polymarket.com";
    public string GammaUrl { get; set; } = "https://gamma-api.polymarket.com";
    public string DataApiUrl { get; set; } = "https://data-api.polymarket.com";
    public int ChainId { get; set; } = 137;

    /// <summary>Gamma tag id for Esports. Children (CS2, LoL, Dota 2, Valorant) come with related_tags=true.</summary>
    public int EsportsTagId { get; set; } = 64;

    /// <summary>
    /// The CTF Exchange an order is signed against — the EIP-712 verifyingContract.
    ///
    /// These defaults are the ones @polymarket/clob-client 5.8.1 (the current release)
    /// uses, and their EIP-712 domain separators were checked against the deployed
    /// contracts: both match what this service computes.
    ///
    /// They are configurable because Polymarket is visibly mid-migration. Newer exchange
    /// contracts are already deployed and collateralised in pUSD
    /// (0xC011a7E12a19f7B1f670d46F03B03f3342E82DFB, "Polymarket USD", 6 decimals) —
    /// 0xE111180000d2663C0091e4f400237545B87B996B and
    /// 0xe2222d279d744050d28e00520010520000310F59 — and a funded account whose
    /// balance still reads zero through /balance-allowance is the symptom of being on
    /// those rails instead of these. Switching is then a config change, not a rebuild.
    /// </summary>
    public string ExchangeAddress { get; set; } = "0x4bFb41d5B3570DeFd03C39a9A4D8dE6Bd8B8982E";

    /// <summary>The exchange used for neg-risk (multi-outcome) markets. See <see cref="ExchangeAddress"/>.</summary>
    public string NegRiskExchangeAddress { get; set; } = "0xC5d563A36AE78145C45a50134d48A1215220f80a";

    /// <summary>
    /// Polygon JSON-RPC endpoints, read-only, used once at startup to check the balance
    /// and approval that actually govern whether an order can settle. The CLOB's own
    /// /balance-allowance reports the legacy USDC.e figure and returns zero for an
    /// account funded in pUSD, so it cannot be trusted as a go-live gate on its own.
    ///
    /// A list, tried in order, because the free endpoints refuse traffic unpredictably:
    /// polygon-rpc.com answers from a laptop and returns "tenant disabled" from inside a
    /// container on the same machine. One endpoint is a single point of failure for the
    /// check that decides whether this bot is allowed to trade.
    ///
    /// Empty by default for the same binding reason as <see cref="EntryPrices"/>.
    /// </summary>
    public string[] PolygonRpcUrls { get; set; } = [];

    public void Validate()
    {
        // Configuration binding unions arrays with whatever is already in the property,
        // so both of these start empty and are filled here. Distinct() also protects
        // against a half-overridden appsettings file repeating a value.
        if (!string.IsNullOrWhiteSpace(EntryPricesCsv))
            EntryPrices = EntryPricesCsv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var d)
                        ? d
                        : throw new InvalidOperationException($"Polymarket:EntryPricesCsv has \"{s}\", which is not a number"))
                .ToArray();

        if (EntryPrices.Length == 0) EntryPrices = [0.03m, 0.01m];
        if (Games.Length == 0) Games = ["Counter-Strike", "LoL", "Dota 2", "Valorant"];
        if (PolygonRpcUrls.Length == 0)
            PolygonRpcUrls =
            [
                "https://polygon-bor-rpc.publicnode.com",
                "https://polygon.drpc.org",
                "https://polygon-rpc.com",
            ];

        EntryPrices = EntryPrices.Distinct().OrderByDescending(p => p).ToArray();
        Games = Games.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (StakeUsd <= 0) throw new InvalidOperationException("Polymarket:StakeUsd must be > 0");
        foreach (var p in EntryPrices)
            if (p < MinAskPrice || p >= 1m)
                throw new InvalidOperationException($"Polymarket:EntryPrices has {p}; must be in [{MinAskPrice}, 1)");
        if (!DryRun && string.IsNullOrWhiteSpace(PrivateKey))
            throw new InvalidOperationException("Polymarket:DryRun is false but no PrivateKey was supplied");
        if (SignatureType is not (0 or 1 or 2 or 3))
            throw new InvalidOperationException($"Polymarket:SignatureType {SignatureType} is not 0, 1, 2 or 3");
    }
}
