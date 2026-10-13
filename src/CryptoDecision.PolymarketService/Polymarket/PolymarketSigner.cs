using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Nethereum.ABI.EIP712;
using Nethereum.Signer;
using Nethereum.Signer.EIP712;
using Nethereum.Util;

namespace CryptoDecision.PolymarketService.Polymarket;

/// <summary>
/// The three cryptographic things Polymarket asks for, and nothing else.
///
///   L1    - an EIP-712 "ClobAuth" signature, used once to fetch the API credentials.
///   L2    - an HMAC-SHA256 over (timestamp + method + path + body), on every private call.
///   Order - an EIP-712 "Order" signature over the exchange contract's struct.
///
/// Every constant here was read out of @polymarket/order-utils and @polymarket/clob-client
/// rather than from memory, and both EIP-712 outputs were compared byte-for-byte against
/// ethers v5 _signTypedData on a shared test key before this file was trusted. If you
/// change a type name, a field order, or a domain string, that equivalence is gone - the
/// CLOB will simply recover a different address and reject you with no useful error.
/// </summary>
public sealed class PolymarketSigner
{
    private const string AuthMessage = "This message attests that I control the given wallet";

    private readonly EthECKey _key;
    private readonly int _chainId;
    private readonly ExchangeDomain _exchange;
    private readonly ExchangeDomain _negRiskExchange;

    public string Address { get; }

    public PolymarketSigner(string privateKey, int chainId, ExchangeDomain exchange, ExchangeDomain negRiskExchange)
    {
        _exchange = exchange;
        _negRiskExchange = negRiskExchange;
        if (string.IsNullOrWhiteSpace(privateKey))
            throw new ArgumentException("private key is empty", nameof(privateKey));

        var normalised = privateKey.Trim();
        if (!normalised.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            normalised = "0x" + normalised;

        _key = new EthECKey(normalised);
        _chainId = chainId;
        Address = _key.GetPublicAddress();
    }

    // -- L1: ClobAuth ---------------------------------------------------------

    /// <summary>
    /// Signs the ClobAuth struct that /auth/api-key and /auth/derive-api-key authenticate
    /// with. Note the domain carries NO verifyingContract - three fields only. Adding a
    /// fourth changes the domain separator and the signature stops recovering to this
    /// address.
    /// </summary>
    public string SignClobAuth(long timestamp, int nonce)
    {
        var raw = new TypedDataRaw
        {
            Types = new Dictionary<string, MemberDescription[]>
            {
                ["EIP712Domain"] =
                [
                    new MemberDescription { Name = "name",    Type = "string"  },
                    new MemberDescription { Name = "version", Type = "string"  },
                    new MemberDescription { Name = "chainId", Type = "uint256" },
                ],
                ["ClobAuth"] =
                [
                    new MemberDescription { Name = "address",   Type = "address" },
                    new MemberDescription { Name = "timestamp", Type = "string"  },
                    new MemberDescription { Name = "nonce",     Type = "uint256" },
                    new MemberDescription { Name = "message",   Type = "string"  },
                ],
            },
            PrimaryType = "ClobAuth",
            DomainRawValues =
            [
                new MemberValue { TypeName = "string",  Value = "ClobAuthDomain" },
                new MemberValue { TypeName = "string",  Value = "1" },
                new MemberValue { TypeName = "uint256", Value = new BigInteger(_chainId) },
            ],
            Message =
            [
                new MemberValue { TypeName = "address", Value = Address },
                // timestamp is a *string* in this struct, not a number. It is signed as
                // text and must be sent as the identical text in the POLY_TIMESTAMP header.
                new MemberValue { TypeName = "string",  Value = timestamp.ToString() },
                new MemberValue { TypeName = "uint256", Value = new BigInteger(nonce) },
                new MemberValue { TypeName = "string",  Value = AuthMessage },
            ],
        };

        return SignRaw(raw);
    }

    // -- L2: HMAC -------------------------------------------------------------

    /// <summary>
    /// The CLOB's HMAC signature. Two details that silently break it:
    ///  * the secret arrives base64-URL encoded, so '-' and '_' must be mapped back to
    ///    '+' and '/' before decoding - .NET's Convert.FromBase64String rejects them,
    ///    where Node's Buffer.from(s, "base64") quietly accepts both alphabets;
    ///  * the output is base64-URL too, but keeps its '=' padding.
    /// The signed path is the bare path: no host, no query string.
    /// </summary>
    public static string BuildHmac(string secret, long timestamp, string method, string requestPath, string? body)
    {
        var normalised = secret.Replace('-', '+').Replace('_', '/');

        // Restore padding if the issuer stripped it, otherwise FromBase64String throws.
        switch (normalised.Length % 4)
        {
            case 2: normalised += "=="; break;
            case 3: normalised += "="; break;
        }

        var message = timestamp.ToString() + method + requestPath + (body ?? string.Empty);

        using var hmac = new HMACSHA256(Convert.FromBase64String(normalised));
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));

        return Convert.ToBase64String(digest).Replace('+', '-').Replace('/', '_');
    }

    // -- Order ----------------------------------------------------------------

    /// <summary>
    /// Signs the exchange Order struct. <paramref name="negRisk"/> selects the verifying
    /// contract: a neg-risk market signed against the ordinary exchange produces a
    /// perfectly valid signature for the wrong contract, and is rejected.
    /// </summary>
    /// <summary>The Order type string. Hashed for the typehash, and sent verbatim inside an ERC-7739 signature.</summary>
    private const string ModernOrderType =
        "Order(uint256 salt,address maker,address signer,uint256 tokenId,uint256 makerAmount," +
        "uint256 takerAmount,uint8 side,uint8 signatureType,uint256 timestamp,bytes32 metadata,bytes32 builder)";

    private const string DomainType = "EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)";

    /// <summary>
    /// ERC-7739 wrapper. Referenced struct types follow the primary type, so "Order"
    /// is appended whole — change either string and the typehash stops matching.
    /// </summary>
    private const string TypedDataSignType =
        "TypedDataSign(Order contents,string name,string version,uint256 chainId,address verifyingContract,bytes32 salt)"
        + ModernOrderType;

    /// <summary>Polymarket's smart-account domain, as the official client declares it.</summary>
    private const string DepositWalletName = "DepositWallet";
    private const string DepositWalletVersion = "1";

    /// <summary>signatureType for a Deposit Wallet: the account is a contract and verifies via ERC-1271.</summary>
    public const int SignatureTypePoly1271 = 3;

    /// <summary>
    /// Signs an order for a Deposit Wallet account.
    ///
    /// These accounts are contracts, so the exchange verifies through ERC-1271 rather
    /// than recovering an address. The payload is ERC-7739 nested typed data: the outer
    /// domain is the exchange's, and the message restates the wallet's own domain so the
    /// wallet can confirm the signature was meant for it. The CLOB rejects anything else
    /// from such an account with "maker address not allowed, please use the deposit
    /// wallet flow".
    ///
    /// The signature is then suffixed with the material a verifier needs to re-derive
    /// what was signed: domain separator, contents hash, the contents type string, and
    /// its length as a big-endian uint16.
    /// </summary>
    public string SignOrderAsDepositWallet(OrderStruct order, bool negRisk)
    {
        var exchange = negRisk ? _negRiskExchange : _exchange;

        var orderHash = Keccak(Concat(
            Keccak(Utf8(ModernOrderType)),
            Word(new BigInteger(order.Salt)),
            Word(order.Maker),
            Word(order.Signer),
            Word(BigInteger.Parse(order.TokenId)),
            Word(BigInteger.Parse(order.MakerAmount)),
            Word(BigInteger.Parse(order.TakerAmount)),
            Word(new BigInteger(order.SideCode)),
            Word(new BigInteger(order.SignatureType)),
            Word(BigInteger.Parse(order.Timestamp)),
            Bytes32(order.Metadata),
            Bytes32(order.Builder)));

        var domainSeparator = Keccak(Concat(
            Keccak(Utf8(DomainType)),
            Keccak(Utf8(exchange.Name)),
            Keccak(Utf8(exchange.Version)),
            Word(new BigInteger(_chainId)),
            Word(exchange.Address)));

        // The wallet's own domain travels inside the message, not as the outer domain.
        var wrapperHash = Keccak(Concat(
            Keccak(Utf8(TypedDataSignType)),
            orderHash,
            Keccak(Utf8(DepositWalletName)),
            Keccak(Utf8(DepositWalletVersion)),
            Word(new BigInteger(_chainId)),
            Word(order.Signer),
            Bytes32(Zero.Bytes32)));

        var digest = Keccak(Concat([0x19, 0x01], domainSeparator, wrapperHash));
        var signature = EthECDSASignature.CreateStringSignature(_key.SignAndCalculateV(digest));

        var typeBytes = Utf8(ModernOrderType);
        var length = ((ushort)typeBytes.Length).ToString("x4");

        return signature
             + Convert.ToHexString(domainSeparator).ToLowerInvariant()
             + Convert.ToHexString(orderHash).ToLowerInvariant()
             + Convert.ToHexString(typeBytes).ToLowerInvariant()
             + length;
    }

    private static byte[] Utf8(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static byte[] Keccak(byte[] data) => Sha3Keccack.Current.CalculateHash(data);
    private static byte[] Bytes32(string hex) => Convert.FromHexString(hex[2..]);

    /// <summary>Left-pads a uint to the 32-byte word abi.encode uses for static types.</summary>
    private static byte[] Word(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var word = new byte[32];
        bytes.CopyTo(word, 32 - bytes.Length);
        return word;
    }

    private static byte[] Word(string address)
    {
        var word = new byte[32];
        Convert.FromHexString(address[2..]).CopyTo(word, 12);
        return word;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var p in parts) { p.CopyTo(result, offset); offset += p.Length; }
        return result;
    }

    public string SignOrder(OrderStruct order, bool negRisk)
    {
        if (order.SignatureType == SignatureTypePoly1271)
            return SignOrderAsDepositWallet(order, negRisk);

        var exchange = negRisk ? _negRiskExchange : _exchange;

        // Two genuinely different structs, not two versions of one. The modern contracts
        // dropped taker/expiration/nonce/feeRateBps and added timestamp/metadata/builder;
        // the typehash of each was found in the bytecode of exactly the contracts that
        // use it, so this is not a guess. Signing the old shape at a modern exchange gets
        // "invalid order version, please use the latest clob-client".
        var (fields, values) = exchange.Modern ? ModernOrder(order) : LegacyOrder(order);

        var raw = new TypedDataRaw
        {
            Types = new Dictionary<string, MemberDescription[]>
            {
                ["EIP712Domain"] =
                [
                    new MemberDescription { Name = "name",              Type = "string"  },
                    new MemberDescription { Name = "version",           Type = "string"  },
                    new MemberDescription { Name = "chainId",           Type = "uint256" },
                    new MemberDescription { Name = "verifyingContract", Type = "address" },
                ],
                ["Order"] = fields,
            },
            PrimaryType = "Order",
            // The domain is whatever the exchange itself declares via ERC-5267, not a
            // constant. The current contracts report version "2"; every published client
            // still signs "1".
            DomainRawValues = Domain(exchange),
            Message = values,
        };

        return SignRaw(raw);
    }

    /// <summary>
    /// Order(uint256 salt,address maker,address signer,uint256 tokenId,uint256 makerAmount,
    /// uint256 takerAmount,uint8 side,uint8 signatureType,uint256 timestamp,bytes32 metadata,
    /// bytes32 builder) — typehash 0xbb86318a…18589, present in both current exchanges.
    /// </summary>
    private static (MemberDescription[], MemberValue[]) ModernOrder(OrderStruct o) =>
    ([
        new MemberDescription { Name = "salt",          Type = "uint256" },
        new MemberDescription { Name = "maker",         Type = "address" },
        new MemberDescription { Name = "signer",        Type = "address" },
        new MemberDescription { Name = "tokenId",       Type = "uint256" },
        new MemberDescription { Name = "makerAmount",   Type = "uint256" },
        new MemberDescription { Name = "takerAmount",   Type = "uint256" },
        new MemberDescription { Name = "side",          Type = "uint8"   },
        new MemberDescription { Name = "signatureType", Type = "uint8"   },
        new MemberDescription { Name = "timestamp",     Type = "uint256" },
        new MemberDescription { Name = "metadata",      Type = "bytes32" },
        new MemberDescription { Name = "builder",       Type = "bytes32" },
    ],
    [
        new MemberValue { TypeName = "uint256", Value = new BigInteger(o.Salt) },
        new MemberValue { TypeName = "address", Value = o.Maker },
        new MemberValue { TypeName = "address", Value = o.Signer },
        new MemberValue { TypeName = "uint256", Value = BigInteger.Parse(o.TokenId) },
        new MemberValue { TypeName = "uint256", Value = BigInteger.Parse(o.MakerAmount) },
        new MemberValue { TypeName = "uint256", Value = BigInteger.Parse(o.TakerAmount) },
        new MemberValue { TypeName = "uint8",   Value = o.SideCode },
        new MemberValue { TypeName = "uint8",   Value = o.SignatureType },
        new MemberValue { TypeName = "uint256", Value = BigInteger.Parse(o.Timestamp) },
        new MemberValue { TypeName = "bytes32", Value = Convert.FromHexString(o.Metadata[2..]) },
        new MemberValue { TypeName = "bytes32", Value = Convert.FromHexString(o.Builder[2..]) },
    ]);

    /// <summary>The pre-2026 struct — typehash 0xa852566c…9767c, still used by the legacy exchanges.</summary>
    private static (MemberDescription[], MemberValue[]) LegacyOrder(OrderStruct o) =>
    ([
        new MemberDescription { Name = "salt",          Type = "uint256" },
        new MemberDescription { Name = "maker",         Type = "address" },
        new MemberDescription { Name = "signer",        Type = "address" },
        new MemberDescription { Name = "taker",         Type = "address" },
        new MemberDescription { Name = "tokenId",       Type = "uint256" },
        new MemberDescription { Name = "makerAmount",   Type = "uint256" },
        new MemberDescription { Name = "takerAmount",   Type = "uint256" },
        new MemberDescription { Name = "expiration",    Type = "uint256" },
        new MemberDescription { Name = "nonce",         Type = "uint256" },
        new MemberDescription { Name = "feeRateBps",    Type = "uint256" },
        new MemberDescription { Name = "side",          Type = "uint8"   },
        new MemberDescription { Name = "signatureType", Type = "uint8"   },
    ],
    [
        new MemberValue { TypeName = "uint256", Value = new BigInteger(o.Salt) },
        new MemberValue { TypeName = "address", Value = o.Maker },
        new MemberValue { TypeName = "address", Value = o.Signer },
        new MemberValue { TypeName = "address", Value = o.Taker },
        new MemberValue { TypeName = "uint256", Value = BigInteger.Parse(o.TokenId) },
        new MemberValue { TypeName = "uint256", Value = BigInteger.Parse(o.MakerAmount) },
        new MemberValue { TypeName = "uint256", Value = BigInteger.Parse(o.TakerAmount) },
        new MemberValue { TypeName = "uint256", Value = BigInteger.Parse(o.Expiration) },
        new MemberValue { TypeName = "uint256", Value = BigInteger.Parse(o.Nonce) },
        new MemberValue { TypeName = "uint256", Value = BigInteger.Parse(o.FeeRateBps) },
        new MemberValue { TypeName = "uint8",   Value = o.SideCode },
        new MemberValue { TypeName = "uint8",   Value = o.SignatureType },
    ]);

    private MemberValue[] Domain(ExchangeDomain exchange) =>
    [
        new MemberValue { TypeName = "string",  Value = exchange.Name },
        new MemberValue { TypeName = "string",  Value = exchange.Version },
        new MemberValue { TypeName = "uint256", Value = new BigInteger(_chainId) },
        new MemberValue { TypeName = "address", Value = exchange.Address },
    ];

    private string SignRaw(TypedDataRaw raw)
    {
        // EncodeTypedDataRaw returns 0x1901 || domainSeparator || hashStruct; the signer's
        // own SignTypedDataV4 does exactly this keccak-then-sign on the typed overload.
        var encoded = Eip712TypedDataSigner.Current.EncodeTypedDataRaw(raw);
        var hash = Sha3Keccack.Current.CalculateHash(encoded);
        return EthECDSASignature.CreateStringSignature(_key.SignAndCalculateV(hash));
    }
}
