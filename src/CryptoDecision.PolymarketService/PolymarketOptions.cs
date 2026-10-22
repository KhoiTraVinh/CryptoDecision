using CryptoDecision.PolymarketService.Polymarket;

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
    /// Only 0.01 by default. Re-measured on @greenworld's full public cash flow — 38
    /// days, 2,164 markets, 2,189 positions — 2,184 of those positions were bought at
    /// exactly 1c. The 3c leg therefore has no measurement behind it on either side of
    /// the trade: it needs 3.00% to break even, and the single-sided population of that
    /// same account hit 0.75% at 1c.
    ///
    /// The earlier "1c hits 1.74%, z = 3.36" figure counted both legs of a market where
    /// both sides were held as two independent trials. They are not independent — one of
    /// them is guaranteed to win. See <see cref="RestBothSides"/>.
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
    /// How much it costs, measured directly: 474 tokens the reference account bought at
    /// 1c, CLOB price history at 1-minute resolution, counting the minutes the price
    /// spent inside (1c, threshold] before it went dead. A scan every 20s needs at least
    /// one such minute to have any chance at all.
    ///
    ///     threshold   median minutes in band   share with ZERO minutes (unreachable)
    ///       0.03              0                          50.6%
    ///       0.05              1                          34.4%
    ///       0.07              3                          22.8%
    ///       0.10              4                          15.4%   <- current
    ///       0.11              5                          14.1%
    ///       0.15              7                           5.3%
    ///       0.20              9                           2.1%
    ///       0.30             21                           0.2%
    ///       1.00            240                           0.0%
    ///
    /// Gapping is not a low-volume phenomenon, which is worth knowing before blaming
    /// MinVolumeUsd for a miss. Capture at 0.07 by the market's volume: 76.1% under $25k,
    /// 73.9% at $25-100k, 84.4% at $100-500k, 77.0% above $500k. Flat.
    ///
    /// An equivalent way to say all this is "arm the other side once this one trades
    /// above 0.90". On live books the two asks of a two-outcome market sum to 1.010-1.030,
    /// so that rule IS this threshold at ~0.11 - no new mechanism, just a number. Do not
    /// implement it by watching the strong side's ASK: that book empties exactly when the
    /// match ends (14 of 25 live eligible markets had no asks at all on the strong side
    /// while the weak side showed 0.001). The robust complement is the strong side's BID,
    /// and it was not worth the code - across those same 25 markets the weak side always
    /// had an ask quoted, so the fallback would have fired zero times.
    ///
    /// This supersedes an earlier "55.5% at 0.15 against 24.6% at 0.05" reading taken on
    /// 171 markets; that one is left in the git history rather than reconciled.
    ///
    /// The old reason to keep this low was that the market was locked to the first side
    /// an order touched, so arming early risked locking onto the side that recovers (on
    /// 171 markets: 7.6% of the time at 0.15, 1.2% at 0.05). With
    /// <see cref="RestBothSides"/> on that risk is gone - the other side stays armable -
    /// so the only thing a low threshold still buys is fewer resting orders.
    ///
    /// NOTE ON UNITS, because this is the easy one to get wrong: the value is a price in
    /// dollars, so 1.0 means 100 cents - "arm immediately, at any price" - and NOT one
    /// cent. The level the order actually rests at is <see cref="EntryPrices"/>, which is
    /// 0.01 and separate from this.
    ///
    /// 0.09 by operator decision. It was briefly set to 1.0 to capture the gap-downs: a
    /// CS2 map ends the instant a team reaches 13 rounds and the price goes from 0.10 to
    /// 0.001 without printing anything in between, so 15.4% of collapses never show a
    /// single minute inside the 0.10 band and no scan interval can catch them. Resting on
    /// the book beforehand is the only way to be filled on those, and a 1c bid costs
    /// nothing until the price collapses onto it.
    ///
    /// It was reverted because 1.0 puts an order on both sides of every qualifying market
    /// the moment it appears, including matches still at 50-50, and the operator cancelled
    /// the 38 that produced. The trade-off, measured on 474 tokens by counting the minutes
    /// a price spends inside the band before the side dies: capture is 82.3% at 0.09,
    /// 84.6% at 0.10, 97.9% at 0.20, 99.8% at 0.30 and 100% at 1.0. If the gap-downs ever
    /// look worth chasing again, 0.30 buys almost all of it without resting on even
    /// matches.
    ///
    /// Note it interacts with <see cref="MinVolumeUsd"/>: a market that has not traded $25k
    /// yet is not eligible no matter how early this would arm it, so in practice arming
    /// still waits on volume rather than on price.
    /// </summary>
    public decimal ArmBelowAsk { get; set; } = 0.09m;

    /// <summary>
    /// Rest on both outcomes of a match instead of one.
    ///
    /// ON by default, because on the measured account this is where all of the profit is.
    /// Splitting that account's public cash flow (38 days, 2,164 markets) by how many
    /// sides of a market it ended up holding:
    ///
    ///     both sides @1c:     25 markets, $128 in,   $3,371 back   (+$3,243)
    ///     one side only:   2,139 markets, $3,290 in, $1,329 back   (-$1,962)
    ///
    /// Holding both sides at 1c is not a hedge and not $0.40 spent on a coin flip: it is
    /// a guaranteed holding of the winning token for about 2c, which is why 23 of those
    /// 25 markets paid. The single-sided population hit 0.75% against a 1.00% break-even.
    ///
    /// It does not place two orders at once. Two sides cannot both sit under
    /// <see cref="ArmBelowAsk"/> at the same time, so the second side is armed only if
    /// the match swings and it collapses in its turn - which is exactly the 25 markets.
    ///
    /// The sample behind this is small: 25 markets, 1.2% of those scanned. The mechanism
    /// is arithmetic, the frequency is not. If that 1.2% does not hold, the whole strategy
    /// is negative, so this bot's own fills should be split the same way and re-checked.
    /// </summary>
    public bool RestBothSides { get; set; } = true;

    /// <summary>
    /// Skip markets that have traded less than this.
    ///
    /// Re-measured on the full 5 months of the strategy (@greenworld, 2026-05-15 onward,
    /// 7,305 entries at 1c over 145 days), and the old reason for $50k did not survive it.
    /// The gate does NOT find the markets that pay: the rate at which a market ends up
    /// held on both sides is flat across every volume band (0.92% under $10k, 1.55% at
    /// $25-50k, 0.73% at $100-250k, 1.32% above $250k), and both-sided markets clear $50k
    /// at 53.5% against 52.3% for the rest - no selectivity at all.
    ///
    /// What the gate actually does is trim low-volume one-sided bleed, and it trims the
    /// both-sided wins at nearly the same rate. Net over those 5 months: +$2,048 with no
    /// gate, +$2,846 at $20k, +$2,850 at $30k, +$2,584 at $50k, +$1,917 at $100k.
    ///
    /// $25k is the setting, and the reason is capture, not that in-sample peak: it keeps
    /// 77% of both-sided opportunities against 53% at $50k. Both-sided markets are the
    /// only population that earns, so seeing more of them beats a prettier ROI on fewer.
    ///
    /// NOTE all of the above is measured on *final* market volume, which is inflated by
    /// the comeback itself. That bias flatters the HIGH thresholds, so it argues for this
    /// floor being lower rather than higher. The bot filters on volume seen AT ENTRY and
    /// logs it, so the question can be re-answered later without the bias.
    /// </summary>
    public decimal MinVolumeUsd { get; set; } = 25_000m;

    /// <summary>
    /// Games to play. Matched against the leading word of the market question.
    /// Empty by default for the same binding reason as <see cref="EntryPrices"/>.
    /// </summary>
    public string[] Games { get; set; } = [];

    /// <summary>
    /// Per-game arming thresholds as "Game=price" pairs, e.g.
    /// "Counter-Strike=0.03,Valorant=0.03,LoL=0.09,Dota 2=0.09".
    ///
    /// The shape of the collapse differs by game. A CS2 or Valorant map ends the moment a
    /// team reaches 13 rounds, so its price falls in one step; a LoL or Dota game bleeds
    /// out over several minutes, so a higher threshold is both reachable and useful.
    ///
    /// Names are matched against the canonical game on the market (see GammaClient's game
    /// resolution, which reads the question prefix first and the event's tags second).
    /// Anything unmatched falls back to <see cref="ArmBelowAsk"/>.
    ///
    /// Empty by default for the binding reason on <see cref="EntryPrices"/>; filled in
    /// <see cref="Validate"/>.
    /// </summary>
    public string ArmBelowAskByGameCsv { get; set; } = "";

    /// <summary>Parsed form of <see cref="ArmBelowAskByGameCsv"/>.</summary>
    public Dictionary<string, decimal> ArmBelowAskByGame { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The arming threshold that applies to one market.</summary>
    public decimal ArmFor(string game) =>
        ArmBelowAskByGame.TryGetValue(game, out var v) ? v : ArmBelowAsk;

    /// <summary>One-line summary of the thresholds, for the startup log.</summary>
    public string ArmSummary() =>
        string.Join(", ", ArmBelowAskByGame.Select(kv => $"{kv.Key}={kv.Value}").Append($"other={ArmBelowAsk}"));

    /// <summary>Ignore matches scheduled to end further out than this. Cuts the scan universe.</summary>
    public double MaxHoursToEnd { get; set; } = 24;

    /// <summary>Don't enter in the dying minutes; the book is thin and resolution is a coin-flip away.</summary>
    public double MinMinutesToEnd { get; set; } = 2;

    // ── Cadence ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 5 seconds, down from 20, and the reason is measured at second resolution off the
    /// public trade prints of the matches that paid.
    ///
    /// The final leg of a collapse is fast. Imperial vs Fluxo W7M went 0.10 -> 0.01 in six
    /// seconds, megoshort vs Privateer 0.11 -> 0.01 in eleven, MIBR LOS vs AG.AL
    /// 0.053 -> 0.01 in three. In four of five CS2/Valorant wins examined there was no
    /// print at all between 1.5c and 5c. A 20-second interval samples such a window zero
    /// or one times; 5 seconds samples it one to three.
    ///
    /// What it does not change: where the price pauses. All four of those sat at 0.09-0.13
    /// for fifteen to forty-three seconds before the final gap, which is why the arming
    /// threshold belongs there rather than inside the gap.
    ///
    /// Cost measured against the live endpoint: 12 consecutive POST /books a minute with
    /// 46 token ids, all 200, slowest response 360ms. The expensive calls are the Gamma
    /// scans, and they stay on their own slower clocks below.
    /// </summary>
    public int ScanIntervalSeconds { get; set; } = 5;
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

        if (EntryPrices.Length == 0) EntryPrices = [0.01m];
        if (Games.Length == 0) Games = ["Counter-Strike", "LoL", "Dota 2", "Valorant"];

        if (ArmBelowAskByGame.Count == 0)
        {
            var csv = string.IsNullOrWhiteSpace(ArmBelowAskByGameCsv)
                ? "Counter-Strike=0.03,Valorant=0.03,LoL=0.09,Dota 2=0.09"
                : ArmBelowAskByGameCsv;

            foreach (var pair in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = pair.LastIndexOf('=');
                if (eq <= 0)
                    throw new InvalidOperationException($"Polymarket:ArmBelowAskByGameCsv has \"{pair}\", expected Game=price");

                var name = pair[..eq].Trim();
                if (!decimal.TryParse(pair[(eq + 1)..].Trim(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var price))
                    throw new InvalidOperationException($"Polymarket:ArmBelowAskByGameCsv has \"{pair}\", which is not a number");

                if (price <= 0m || price > 1m)
                    throw new InvalidOperationException($"Polymarket:ArmBelowAskByGameCsv has {name}={price}; must be in (0, 1]");

                ArmBelowAskByGame[name] = price;
            }
        }

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
