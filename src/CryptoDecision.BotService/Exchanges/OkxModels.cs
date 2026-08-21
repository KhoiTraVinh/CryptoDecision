using System.Globalization;
using System.Text.Json.Serialization;

namespace CryptoDecision.BotService.Exchanges;

// ─────────────────────────────────────────────────────────────────────────────
// OKX v5 response shapes.
//
// Every numeric field on the wire is a JSON string, and an inapplicable one is
// the empty string rather than null or 0 — an unfilled order reports
// avgPx: "" and accFillSz: "0". Binding these to decimal directly throws on the
// empty case, so they stay strings here and are read through OkxNum, which
// treats "" as absent. That distinction matters most on the field that decides
// whether an order filled at all.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Tolerant parsing for OKX's string-encoded numbers.</summary>
internal static class OkxNum
{
    /// <summary>Parse an OKX numeric string, treating empty/absent as zero.</summary>
    public static decimal Parse(string? raw)
        => decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    /// <summary>Parse an OKX numeric string, treating empty/absent as null.</summary>
    public static decimal? ParseOrNull(string? raw)
        => decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>
    /// Format a size or price for OKX. Invariant culture and plain notation: a
    /// comma decimal separator or an exponent (which is what "1E-05" becomes on a
    /// small BTC quantity) is rejected by the exchange as an invalid size.
    /// </summary>
    public static string Format(decimal value)
        => value.ToString("0.############################", CultureInfo.InvariantCulture);
}

// ── Instrument rules ─────────────────────────────────────────────────────────

/// <summary>
/// Trading rules for one spot instrument. These are the constraints an order has
/// to satisfy before it is worth sending: a size off the lot grid, or below the
/// minimum, is rejected outright.
/// </summary>
public sealed record OkxInstrument(
    [property: JsonPropertyName("instId")]    string  InstId,
    [property: JsonPropertyName("baseCcy")]   string? BaseCcyRaw,
    [property: JsonPropertyName("quoteCcy")]  string? QuoteCcyRaw,
    [property: JsonPropertyName("settleCcy")] string? SettleCcy,
    [property: JsonPropertyName("ctVal")]     string? CtValRaw,
    [property: JsonPropertyName("ctValCcy")]  string? CtValCcy,
    [property: JsonPropertyName("lotSz")]     string? LotSzRaw,
    [property: JsonPropertyName("minSz")]     string? MinSzRaw,
    [property: JsonPropertyName("tickSz")]    string? TickSzRaw,
    [property: JsonPropertyName("state")]     string? State
)
{
    /// <summary>Size increment, in contracts. Any order size must be a whole multiple of this.</summary>
    public decimal LotSize  => OkxNum.Parse(LotSzRaw);
    /// <summary>Smallest order the exchange accepts, in contracts.</summary>
    public decimal MinSize  => OkxNum.Parse(MinSzRaw);
    /// <summary>Price increment.</summary>
    public decimal TickSize => OkxNum.Parse(TickSzRaw);
    /// <summary>OKX reports "live" for a tradable instrument; suspended ones are not.</summary>
    public bool    IsLive   => string.Equals(State, "live", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Base currency of the underlying. SWAP instruments leave baseCcy empty and
    /// express the pair through ctValCcy/settleCcy instead, so it is derived from
    /// the instId when absent.
    /// </summary>
    public string BaseCcy => !string.IsNullOrWhiteSpace(BaseCcyRaw)
        ? BaseCcyRaw!
        : !string.IsNullOrWhiteSpace(CtValCcy) ? CtValCcy!
        : InstId.Split('-') is { Length: > 0 } parts ? parts[0] : InstId;

    /// <summary>Currency the position is quoted and settled in — USDT for linear perps.</summary>
    public string QuoteCcy => !string.IsNullOrWhiteSpace(QuoteCcyRaw)
        ? QuoteCcyRaw!
        : !string.IsNullOrWhiteSpace(SettleCcy) ? SettleCcy!
        : InstId.Split('-') is { Length: > 1 } parts ? parts[1] : "USDT";

    /// <summary>
    /// Base-currency amount one contract represents — 0.01 BTC for BTC-USDT-SWAP.
    ///
    /// This is the conversion nothing else can be computed without: order sizes go
    /// to OKX in contracts, while every risk figure the bot reasons about (notional,
    /// stop distance, P&amp;L) is in base units. Falls back to 1 so a spot
    /// instrument, which has no contract size, still behaves sensibly.
    /// </summary>
    public decimal ContractValue => OkxNum.Parse(CtValRaw) is var v && v > 0m ? v : 1m;
}

// ── Account configuration ────────────────────────────────────────────────────

/// <summary>
/// Account-level trading configuration. Only <see cref="PosMode"/> is read, and
/// it decides whether an order must name which side of the book it belongs to:
/// hedge mode ("long_short_mode") requires posSide, net mode rejects it. Getting
/// this wrong has the exchange refuse every order.
/// </summary>
public sealed record OkxAccountConfig(
    [property: JsonPropertyName("posMode")] string? PosMode,
    [property: JsonPropertyName("acctLv")]  string? AccountLevel
)
{
    public bool IsHedgeMode =>
        string.Equals(PosMode, "long_short_mode", StringComparison.OrdinalIgnoreCase);
}

// ── Positions ────────────────────────────────────────────────────────────────

/// <summary>
/// An open derivatives position as the exchange sees it. This — not a coin
/// balance — is what says whether a futures position still exists.
/// </summary>
public sealed record OkxPosition(
    [property: JsonPropertyName("instId")]  string? InstId,
    [property: JsonPropertyName("posSide")] string? PosSide,
    [property: JsonPropertyName("pos")]     string? PosRaw,
    [property: JsonPropertyName("avgPx")]   string? AvgPxRaw,
    [property: JsonPropertyName("upl")]     string? UplRaw,
    [property: JsonPropertyName("liqPx")]   string? LiqPxRaw,
    [property: JsonPropertyName("lever")]   string? LeverRaw
)
{
    /// <summary>Signed contract count: positive long, negative short, zero flat.</summary>
    public decimal Contracts => OkxNum.Parse(PosRaw);
    /// <summary>Absolute contract count.</summary>
    public decimal AbsContracts => Math.Abs(Contracts);
    public decimal? AveragePrice => OkxNum.ParseOrNull(AvgPxRaw);
    public decimal? UnrealisedPnl => OkxNum.ParseOrNull(UplRaw);
    /// <summary>Estimated liquidation price, when OKX reports one.</summary>
    public decimal? LiquidationPrice => OkxNum.ParseOrNull(LiqPxRaw);
    public bool IsFlat => AbsContracts <= 0m;
}

// ── Order placement ──────────────────────────────────────────────────────────

/// <summary>
/// Acknowledgement of a submitted order. <see cref="SCode"/> is the per-order
/// result and is <em>not</em> implied by the envelope's outer code — an
/// acknowledged batch can contain a rejected order.
/// </summary>
public sealed record OkxOrderAck(
    [property: JsonPropertyName("ordId")]   string? OrdId,
    [property: JsonPropertyName("clOrdId")] string? ClOrdId,
    [property: JsonPropertyName("sCode")]   string? SCode,
    [property: JsonPropertyName("sMsg")]    string? SMsg
)
{
    public bool Accepted => SCode is null or "" or "0";
}

/// <summary>
/// An order as the exchange currently sees it. This is the only trustworthy
/// source of what a market order actually cost: the requested size and the
/// reference price are both guesses until the fill comes back.
/// </summary>
public sealed record OkxOrderDetail(
    [property: JsonPropertyName("ordId")]     string? OrdId,
    [property: JsonPropertyName("instId")]    string? InstId,
    [property: JsonPropertyName("side")]      string? Side,
    [property: JsonPropertyName("state")]     string? State,
    [property: JsonPropertyName("sz")]        string? SzRaw,
    [property: JsonPropertyName("accFillSz")] string? AccFillSzRaw,
    [property: JsonPropertyName("avgPx")]     string? AvgPxRaw,
    [property: JsonPropertyName("fee")]       string? FeeRaw,
    [property: JsonPropertyName("feeCcy")]    string? FeeCcy
)
{
    /// <summary>Base-currency amount filled so far.</summary>
    public decimal FilledSize => OkxNum.Parse(AccFillSzRaw);

    /// <summary>Volume-weighted fill price, or null while nothing has filled.</summary>
    public decimal? AverageFillPrice => OkxNum.ParseOrNull(AvgPxRaw);

    /// <summary>
    /// Fee charged, as an absolute value. OKX reports it negative (it is a
    /// deduction); the sign is dropped here so callers cannot accidentally add a
    /// negative fee and end up crediting themselves.
    /// </summary>
    public decimal FeeAbs => Math.Abs(OkxNum.Parse(FeeRaw));

    public bool IsFilled    => string.Equals(State, "filled",   StringComparison.OrdinalIgnoreCase);
    public bool IsCanceled  => string.Equals(State, "canceled", StringComparison.OrdinalIgnoreCase);
    public bool IsPartial   => string.Equals(State, "partially_filled", StringComparison.OrdinalIgnoreCase);

    /// <summary>True once the order can no longer change: filled, or cancelled.</summary>
    public bool IsTerminal  => IsFilled || IsCanceled;
}

// ── Algo (conditional) orders ────────────────────────────────────────────────

/// <summary>Acknowledgement of a submitted algo order. Same per-entry sCode rule as a normal order.</summary>
public sealed record OkxAlgoAck(
    [property: JsonPropertyName("algoId")]  string? AlgoId,
    [property: JsonPropertyName("clOrdId")] string? ClOrdId,
    [property: JsonPropertyName("sCode")]   string? SCode,
    [property: JsonPropertyName("sMsg")]    string? SMsg
)
{
    public bool Accepted => SCode is null or "" or "0";
}

/// <summary>
/// An OCO order as the exchange sees it.
///
/// <see cref="State"/> is the field that answers "did the exchange close my
/// position without telling me": OKX reports <c>live</c> while the order waits,
/// and <c>effective</c> once a trigger fired — at which point <see cref="OrdId"/>
/// names the real order that was placed, and that order carries the actual fill.
/// </summary>
public sealed record OkxAlgoOrder(
    [property: JsonPropertyName("algoId")] string? AlgoId,
    [property: JsonPropertyName("instId")] string? InstId,
    [property: JsonPropertyName("ordId")]  string? OrdId,
    [property: JsonPropertyName("state")]  string? State
)
{
    /// <summary>A trigger has fired and a real order exists.</summary>
    public bool HasTriggered =>
        string.Equals(State, "effective", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(OrdId);

    /// <summary>Still waiting, so the position it guards is presumed open.</summary>
    public bool IsWaiting =>
        string.Equals(State, "live",  StringComparison.OrdinalIgnoreCase)
        || string.Equals(State, "pause", StringComparison.OrdinalIgnoreCase);
}

// ── Market data ──────────────────────────────────────────────────────────────

public sealed record OkxTicker(
    [property: JsonPropertyName("instId")] string? InstId,
    [property: JsonPropertyName("last")]   string? LastRaw
)
{
    /// <summary>Last traded price, or null when the field is absent or empty.</summary>
    public decimal? Last => OkxNum.ParseOrNull(LastRaw);
}

// ── Account balance ──────────────────────────────────────────────────────────

public sealed record OkxBalanceResponse(
    [property: JsonPropertyName("details")] List<OkxBalanceDetail>? Details
);

public sealed record OkxBalanceDetail(
    [property: JsonPropertyName("ccy")]     string? Ccy,
    [property: JsonPropertyName("availBal")] string? AvailBalRaw,
    [property: JsonPropertyName("eq")]      string? EqRaw
)
{
    /// <summary>Free balance — what can actually be committed to a new order.</summary>
    public decimal Available => OkxNum.Parse(AvailBalRaw);
}
