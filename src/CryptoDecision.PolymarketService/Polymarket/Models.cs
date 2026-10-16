using System.Text.Json.Serialization;

namespace CryptoDecision.PolymarketService.Polymarket;

/// <summary>
/// Which market family a market came from. It decides which entry levels apply, because
/// the two behave nothing alike: an esports map keeps trading after it is decided and its
/// losing side reaches 1c almost every time, while a football market closes on the final
/// whistle and its losing side stops at 10c or above 80.7% of the time.
/// </summary>
public enum Sport { Esports, Soccer }

/// <summary>A tradable match market, reduced to what the entry rule actually reads.</summary>
public sealed record EsportsMarket(
    string ConditionId,
    string Question,
    string Slug,
    string Game,
    Sport Sport,
    IReadOnlyList<string> Outcomes,
    IReadOnlyList<string> TokenIds,
    bool NegRisk,
    DateTimeOffset? EndDate,
    decimal VolumeUsd);

/// <summary>One side of the book, best price first.</summary>
public sealed record BookLevel(decimal Price, decimal Size);

public sealed record OrderBook(string TokenId, IReadOnlyList<BookLevel> Bids, IReadOnlyList<BookLevel> Asks)
{
    /// <summary>Cheapest price someone is willing to sell at, or null when nobody is offering.</summary>
    public decimal? BestAsk => Asks.Count > 0 ? Asks[0].Price : null;
}

/// <summary>CLOB API credentials, derived once from the wallet key.</summary>
public sealed record ApiCreds(string Key, string Secret, string Passphrase);

/// <summary>
/// An exchange contract and the EIP-712 domain it verifies against. Name and version
/// are read from the contract with ERC-5267 rather than assumed: the live exchanges
/// report version "2" while every published client still signs "1".
///
/// <paramref name="Modern"/> says which Order struct the contract verifies. The two are
/// different types, not two versions of one, and the typehash of each was found in the
/// bytecode of exactly the contracts that use it. ERC-5267 support is the discriminator:
/// only the new contracts implement it.
/// </summary>
public sealed record ExchangeDomain(string Address, string Name, string Version, bool Modern);

/// <summary>
/// The exchange Order struct. Amounts are integer strings in the collateral's 6-decimal
/// base units.
///
/// Carries the union of both struct shapes; which subset is signed depends on the
/// exchange. The modern contracts dropped <see cref="Taker"/>, <see cref="Nonce"/> and
/// <see cref="FeeRateBps"/> from the signed type and added <see cref="Timestamp"/>,
/// <see cref="Metadata"/> and <see cref="Builder"/>. <see cref="Expiration"/> survives
/// in the REST body but is no longer signed.
/// </summary>
public sealed class OrderStruct
{
    public long Salt { get; init; }
    public string Maker { get; init; } = "";
    public string Signer { get; init; } = "";
    public string Taker { get; init; } = Zero.Address;
    public string TokenId { get; init; } = "";
    public string MakerAmount { get; init; } = "0";
    public string TakerAmount { get; init; } = "0";
    public string Expiration { get; init; } = "0";
    public string Nonce { get; init; } = "0";
    public string FeeRateBps { get; init; } = "0";

    /// <summary>Unix milliseconds. Signed by the modern struct, absent from the old one.</summary>
    public string Timestamp { get; init; } = "0";

    public string Metadata { get; init; } = Zero.Bytes32;
    public string Builder { get; init; } = Zero.Bytes32;

    /// <summary>0 = BUY, 1 = SELL. This is what gets signed.</summary>
    public int SideCode { get; init; }

    /// <summary>The string the REST body carries for the same field. Signed as a number, sent as text.</summary>
    public string SideName => SideCode == 0 ? "BUY" : "SELL";

    public int SignatureType { get; init; }
}

public static class Zero
{
    public const string Address = "0x0000000000000000000000000000000000000000";
    public const string Bytes32 = "0x0000000000000000000000000000000000000000000000000000000000000000";
}

/// <summary>
/// POST /order body. Null members are dropped by the serializer, so one shape serves
/// both the modern contracts (timestamp/metadata/builder) and the old ones
/// (taker/nonce/feeRateBps) without sending fields the other side does not expect.
/// </summary>
public sealed class SignedOrderPayload
{
    [JsonPropertyName("salt")]          public long Salt { get; init; }
    [JsonPropertyName("maker")]         public string Maker { get; init; } = "";
    [JsonPropertyName("signer")]        public string Signer { get; init; } = "";
    [JsonPropertyName("tokenId")]       public string TokenId { get; init; } = "";
    [JsonPropertyName("makerAmount")]   public string MakerAmount { get; init; } = "";
    [JsonPropertyName("takerAmount")]   public string TakerAmount { get; init; } = "";
    [JsonPropertyName("side")]          public string Side { get; init; } = "BUY";
    [JsonPropertyName("expiration")]    public string Expiration { get; init; } = "0";
    [JsonPropertyName("signatureType")] public int SignatureType { get; init; }
    [JsonPropertyName("signature")]     public string Signature { get; init; } = "";

    [JsonPropertyName("timestamp")]  public string? Timestamp { get; init; }
    [JsonPropertyName("metadata")]   public string? Metadata { get; init; }
    [JsonPropertyName("builder")]    public string? Builder { get; init; }

    [JsonPropertyName("taker")]      public string? Taker { get; init; }
    [JsonPropertyName("nonce")]      public string? Nonce { get; init; }
    [JsonPropertyName("feeRateBps")] public string? FeeRateBps { get; init; }
}

public sealed class PostOrderBody
{
    [JsonPropertyName("deferExec")] public bool DeferExec { get; init; }
    [JsonPropertyName("order")]     public SignedOrderPayload Order { get; init; } = new();
    [JsonPropertyName("owner")]     public string Owner { get; init; } = "";
    [JsonPropertyName("orderType")] public string OrderType { get; init; } = "GTC";
}

/// <summary>
/// What came back from POST /order, read defensively because the shape varies by version.
/// For a GTC order <see cref="Ok"/> means accepted, not filled, and <see cref="FilledShares"/>
/// is usually zero — the rest of the size is now sitting on the book.
/// </summary>
public sealed record OrderResult(bool Ok, decimal FilledShares, decimal NotionalUsd, string? OrderId, string? Error);

/// <summary>An order currently resting on the CLOB. Read back from the exchange, never cached to disk.</summary>
public sealed record OpenOrder(string Id, string TokenId, string Side, decimal Price, decimal OriginalSize, decimal SizeMatched);

/// <summary>A position the exchange says we hold. The source of truth for "have I already entered this".</summary>
public sealed record ExchangePosition(string TokenId, string ConditionId, string Title, string Outcome, decimal Size, decimal AvgPrice);
