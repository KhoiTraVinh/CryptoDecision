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
    /// Set to 1.0 to arm as soon as a market appears. Note it interacts with
    /// <see cref="MinVolumeUsd"/>: a market that has not traded $25k yet is not eligible
    /// no matter how early this would arm it, so capture is bounded by how fast volume
    /// accumulates, not by this alone.
    /// </summary>
    public decimal ArmBelowAsk { get; set; } = 0.10m;

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

    // ── Football ──────────────────────────────────────────────────────────────
    //
    // Added on request. Three earlier readings of this were wrong and are worth naming,
    // because each one made football look less tradable than it is:
    //
    //   1. Price history was pulled in a window around Gamma's endDate. That field is the
    //      scheduled time, not the settle time - on Roma vs Real Madrid it reads 19:00Z
    //      while the market traded until 21:20Z - so the second half of every match was
    //      cut off, and with it the collapse the strategy exists to catch.
    //   2. The league filter matched tag LABELS, which silently excluded every Champions
    //      League match: Gamma tags those "UCL". See SoccerLeagueTagIds.
    //   3. The sample stopped two days short of the match that disproved the conclusion.
    //
    // Measured again on 1,495 closed winner-markets in these competitions, full price
    // history, settlement plateau removed. The pipeline is checked against a known case:
    // "Will Real Madrid CF win on 2026-10-14?" bottomed at 0.0065 and won.
    //
    //   level  fills  wins   rate    break-even   edge      z
    //    0.01    250     1   0.40%         1%      -60%   -0.95
    //    0.02    259     2   0.77%         2%      -61%   -1.41
    //    0.03    267     4   1.50%         3%      -50%   -1.44
    //    0.05    280     9   3.21%         5%      -36%   -1.37
    //    0.10    307    24   7.82%        10%      -22%   -1.27
    //    0.15    321    36  11.21%        15%      -25%   -1.90
    //    0.20    343    55  16.03%        20%      -20%   -1.84
    //
    // So it DOES fill, and often - 250 fills in 610 days, one every 2.4 days, not the
    // one-a-month claimed before. What it does not do is clear the bar: 0.40% against a
    // 1.00% break-even, and negative at every level above it. The one winner in 250 is
    // the Real Madrid match. Esports realises 2.16% on the same bet.
    //
    // Not significantly below break-even either (z = -0.95), which is the whole reason
    // this stays switched on: at $0.10 a fill it costs about $25 a year to find out, and
    // every order is logged so the question gets answered on this bot's own data.
    //
    // Caveat on the sample: only 588 of 2,990 tokens have CLOB price history at all, and
    // the ones that do skew to higher volume.

    /// <summary>
    /// Whether to scan football at all. The cost of being wrong is one $0.06 order a
    /// month; the cost of being right is unknown, which is the only argument for it.
    /// </summary>
    public bool SoccerEnabled { get; set; } = true;

    /// <summary>Gamma's parent Soccer tag. Per-league tags are used inconsistently - Serie A and
    /// Europa League return almost nothing through their own ids - so the parent plus
    /// <see cref="SoccerLeagues"/> is the only reliable way in.</summary>
    public int SoccerTagId { get; set; } = 100_350;

    /// <summary>
    /// Competitions to accept, as Gamma tag IDs on the event.
    ///
    /// IDs, not labels. The first version matched label text and silently dropped every
    /// Champions League match, because Gamma tags those events "UCL" - the string
    /// "Champions League" appears nowhere on them. Tag 1234 is labelled "Champions League"
    /// and is NOT the one in use; the live one is 100977. Labels are display text and
    /// change; ids do not.
    ///
    /// Empty by default for the binding reason on <see cref="EntryPrices"/>; filled in
    /// <see cref="Validate"/>.
    /// </summary>
    public int[] SoccerLeagueTagIds { get; set; } = [];

    /// <summary>
    /// Entry levels for football, separate from <see cref="EntryPrices"/> on purpose: the
    /// table above measures every level from 0.03 up as flat or losing, so esports levels
    /// must never leak into football markets.
    /// </summary>
    public decimal[] SoccerEntryPrices { get; set; } = [];

    /// <summary>Comma-separated override for <see cref="SoccerEntryPrices"/>. See <see cref="EntryPricesCsv"/>.</summary>
    public string SoccerEntryPricesCsv { get; set; } = "";

    /// <summary>
    /// How long a football market stays eligible after its Gamma endDate.
    ///
    /// Needed because for football that field is KICKOFF, not the final whistle. Measured
    /// against the last trade on the book: La Liga +140 minutes, Bundesliga +151, and Roma
    /// vs Real Madrid +140 - a match whose 1c print landed 100 minutes past its endDate.
    /// Applying the esports rule (<see cref="MinMinutesToEnd"/>) to football retires every
    /// market at kick-off, which is the moment before the only event this bot waits for.
    /// That is why football scanned 43 times and never kept a single market.
    ///
    /// Entering a market that has actually finished is not a risk this opens up: the
    /// losing side's ask sits at 0.001 by then, below the entry level, and the order is
    /// refused for crossing rather than resting.
    /// </summary>
    public double SoccerMinutesAfterKickoff { get; set; } = 180;

    /// <summary>The levels that apply to a market, by the family it came from.</summary>
    public decimal[] LevelsFor(Sport sport) => sport == Sport.Soccer ? SoccerEntryPrices : EntryPrices;

    /// <summary>
    /// Every level either family can use. Used when blocking re-entry on a token already
    /// held, where the sport is not known - a position read back from the exchange says
    /// what it holds, never which rule bought it.
    /// </summary>
    public IEnumerable<decimal> AllEntryPrices => EntryPrices.Concat(SoccerEntryPrices).Distinct();

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

        if (EntryPrices.Length == 0) EntryPrices = [0.01m];
        if (Games.Length == 0) Games = ["Counter-Strike", "LoL", "Dota 2", "Valorant"];

        if (!string.IsNullOrWhiteSpace(SoccerEntryPricesCsv))
            SoccerEntryPrices = SoccerEntryPricesCsv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var d)
                        ? d
                        : throw new InvalidOperationException($"Polymarket:SoccerEntryPricesCsv has \"{s}\", which is not a number"))
                .ToArray();

        if (SoccerEntryPrices.Length == 0) SoccerEntryPrices = [0.01m];
        if (SoccerLeagueTagIds.Length == 0)
            SoccerLeagueTagIds =
            [
                // Champions League - three live ids for the one competition
                100_977,  // "UCL"                 <- the one actually on match events
                102_469,  // "UEFA Champions League"
                1_234,    // "Champions League"

                // Europa League - likewise three
                101_787,  // "UEL"
                102_506,  // "UEFA Europa League"
                100_626,  // "Europa League"

                306,      // EPL
                82,       // Premier League
                780,      // La Liga
                100_618,  // Serie A
                102_070,  // Ligue 1
                1_494,    // Bundesliga
                101_735,  // Eredivisie
                102_154,  // DFB-Pokal
                101_807,  // FA Cup
                102_643,  // EFL Championship
                101_772,  // Primeira Liga
            ];

        // Deliberately NOT here, having been found by a label sweep and rejected: AFC
        // Champions League Elite (104937), Austria Bundesliga (104929), Bundesliga 2
        // (102864), La Liga 2 (102866), Premier League Kazakhstan (105246), Azerbaijan
        // Premier League (105913), Canadian Premier League (105910), Champions League
        // Final (102178 - a futures market, not a match). Matching league names by text
        // pulls all of these in; ids keep the list to the competitions that were asked
        // for. Any new id has to be added here on purpose.
        if (PolygonRpcUrls.Length == 0)
            PolygonRpcUrls =
            [
                "https://polygon-bor-rpc.publicnode.com",
                "https://polygon.drpc.org",
                "https://polygon-rpc.com",
            ];

        EntryPrices = EntryPrices.Distinct().OrderByDescending(p => p).ToArray();
        SoccerEntryPrices = SoccerEntryPrices.Distinct().OrderByDescending(p => p).ToArray();
        Games = Games.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        SoccerLeagueTagIds = SoccerLeagueTagIds.Distinct().ToArray();

        if (StakeUsd <= 0) throw new InvalidOperationException("Polymarket:StakeUsd must be > 0");
        foreach (var p in EntryPrices)
            if (p < MinAskPrice || p >= 1m)
                throw new InvalidOperationException($"Polymarket:EntryPrices has {p}; must be in [{MinAskPrice}, 1)");

        foreach (var p in SoccerEntryPrices)
            if (p < MinAskPrice || p >= 1m)
                throw new InvalidOperationException($"Polymarket:SoccerEntryPrices has {p}; must be in [{MinAskPrice}, 1)");
        if (!DryRun && string.IsNullOrWhiteSpace(PrivateKey))
            throw new InvalidOperationException("Polymarket:DryRun is false but no PrivateKey was supplied");
        if (SignatureType is not (0 or 1 or 2 or 3))
            throw new InvalidOperationException($"Polymarket:SignatureType {SignatureType} is not 0, 1, 2 or 3");
    }
}
