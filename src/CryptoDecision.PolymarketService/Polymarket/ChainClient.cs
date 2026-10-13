using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CryptoDecision.PolymarketService.Polymarket;

/// <summary>
/// A three-call Polygon JSON-RPC client, used only to answer one question before the
/// first order: can this account actually trade through the configured exchange?
///
/// Why not just ask the CLOB. Its /balance-allowance endpoint reports the LEGACY
/// USDC.e collateral. Measured on a live account holding $45.00 of pUSD with unlimited
/// approvals to the current exchanges, that endpoint returned
/// {"balance":"0","allowances":{...all zero}} - a funded, fully approved account looked
/// identical to an empty one. Guarding on that number refuses to start a bot that would
/// have worked perfectly.
///
/// So the collateral token is not hardcoded here either. It is read from the exchange
/// contract itself with getCollateral(), which makes the check correct on whichever
/// rails the configured exchange happens to be on.
/// </summary>
public sealed class ChainClient(
    HttpClient http,
    IOptions<PolymarketOptions> options,
    ILogger<ChainClient> log)
{
    private readonly PolymarketOptions _opt = options.Value;

    // getCollateral() / balanceOf(address) / allowance(address,address), ERC-20 + exchange.
    private const string SelectorGetCollateral = "0x5c1548fb";
    private const string SelectorBalanceOf     = "0x70a08231";
    private const string SelectorAllowance     = "0xdd62ed3e";
    private const string SelectorDecimals      = "0x313ce567";

    /// <summary>
    /// Collateral token of <paramref name="exchange"/>, its decimals, and the holder's
    /// balance and approval for that exchange. Any field may be null when the RPC is
    /// unreachable - an unknown answer must stay distinguishable from a zero one.
    /// </summary>
    public async Task<ChainAccountState> ReadAccountStateAsync(string exchange, string holder, CancellationToken ct)
    {
        var collateral = await CallAddressAsync(exchange, SelectorGetCollateral, ct);
        if (collateral is null)
        {
            log.LogWarning("Could not read getCollateral() from exchange {Exchange}; on-chain checks skipped", exchange);
            return new ChainAccountState(null, null, null, null);
        }

        var decimals = await CallIntAsync(collateral, SelectorDecimals, ct) ?? 6;
        var scale = (decimal)BigInteger.Pow(10, (int)decimals);

        var balance = await CallBigAsync(collateral, SelectorBalanceOf + Pad(holder), ct);
        var allowance = await CallBigAsync(collateral, SelectorAllowance + Pad(holder) + Pad(exchange), ct);

        return new ChainAccountState(
            collateral,
            await CallStringAsync(collateral, "0x95d89b41", ct),   // symbol()
            ToDecimal(balance, scale),
            ToDecimal(allowance, scale));

        // An unlimited ERC-20 approval is 2^256-1, which is ~10^59 times larger than
        // decimal can hold - converting it directly throws OverflowException and takes
        // the whole service down at startup. Saturate instead; every caller only ever
        // asks whether this is above zero.
        static decimal? ToDecimal(BigInteger? raw, decimal scale)
        {
            if (raw is null) return null;

            // Saturate first, then divide — dividing in BigInteger would silently drop
            // the cents off an ordinary balance.
            return raw.Value >= new BigInteger(decimal.MaxValue)
                ? decimal.MaxValue
                : (decimal)raw.Value / scale;
        }
    }

    /// <summary>
    /// The exchange's own EIP-712 domain, read with ERC-5267 eip712Domain().
    ///
    /// Asking rather than hardcoding, because this is exactly what broke: the current
    /// exchanges report version "2" where every published client still signs "1", and
    /// the CLOB rejects the result with "invalid order version, please use the latest
    /// clob-client". The old contracts do not implement ERC-5267 at all, so a null here
    /// means "assume the documented v1 domain".
    /// </summary>
    public async Task<(string Name, string Version)?> ReadEip712DomainAsync(string exchange, CancellationToken ct)
    {
        // eip712Domain() -> (bytes1, string, string, uint256, address, bytes32, uint256[])
        var hex = await CallAsync(exchange, "0x84b0196e", ct);
        if (hex is null) return null;

        try
        {
            var body = hex[2..];
            var name = ReadDynamicString(body, wordIndex: 1);
            var version = ReadDynamicString(body, wordIndex: 2);
            if (name is null || version is null) return null;

            log.LogInformation("Exchange {Exchange} declares EIP-712 domain name=\"{Name}\" version=\"{Version}\"", exchange, name, version);
            return (name, version);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not decode eip712Domain() from {Exchange}", exchange);
            return null;
        }
    }

    /// <summary>Reads an ABI dynamic string whose offset sits in head word <paramref name="wordIndex"/>.</summary>
    private static string? ReadDynamicString(string body, int wordIndex)
    {
        var offsetHex = body.Substring(wordIndex * 64, 64);
        var offset = (int)BigInteger.Parse("0" + offsetHex, NumberStyles.HexNumber) * 2;
        if (offset + 64 > body.Length) return null;

        var length = (int)BigInteger.Parse("0" + body.Substring(offset, 64), NumberStyles.HexNumber) * 2;
        if (length <= 0 || offset + 64 + length > body.Length) return null;

        return Encoding.UTF8.GetString(Convert.FromHexString(body.Substring(offset + 64, length)));
    }

    private async Task<string?> CallAsync(string to, string data, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "eth_call",
            @params = new object[] { new { to, data }, "latest" },
        });

        foreach (var endpoint in _opt.PolygonRpcUrls)
        {
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await http.PostAsync(endpoint, content, ct);
                var text = await response.Content.ReadAsStringAsync(ct);

                using var doc = JsonDocument.Parse(text);
                var result = doc.RootElement.TryGetProperty("result", out var r) ? r.GetString() : null;
                if (!string.IsNullOrEmpty(result) && result != "0x") return result;

                // A refusal here is silent otherwise: the endpoint answers 200 with an
                // error body, the call returns null, and the whole check degrades to
                // "unknown" with nothing saying why.
                log.LogDebug("RPC {Endpoint} gave no result for {To}: {Body}", endpoint, to, text.Length > 200 ? text[..200] : text);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "RPC {Endpoint} failed for {To}", endpoint, to);
            }
        }

        return null;
    }

    private async Task<BigInteger?> CallBigAsync(string to, string data, CancellationToken ct)
    {
        var hex = await CallAsync(to, data, ct);
        if (hex is null) return null;
        // Parse as unsigned: a leading byte >= 0x80 would otherwise read as negative.
        return BigInteger.Parse("0" + hex[2..], NumberStyles.HexNumber);
    }

    private async Task<string?> CallAddressAsync(string to, string data, CancellationToken ct)
    {
        var hex = await CallAsync(to, data, ct);
        return hex is null || hex.Length < 66 ? null : "0x" + hex[^40..];
    }

    private async Task<uint?> CallIntAsync(string to, string data, CancellationToken ct) =>
        (uint?)await CallBigAsync(to, data, ct);

    /// <summary>Decodes an ABI-encoded string return value, best effort - it is only ever a log label.</summary>
    private async Task<string?> CallStringAsync(string to, string data, CancellationToken ct)
    {
        var hex = await CallAsync(to, data, ct);
        if (hex is null || hex.Length < 130) return null;

        try
        {
            var length = (int)BigInteger.Parse("0" + hex.Substring(66, 64), NumberStyles.HexNumber);
            if (length is <= 0 or > 64) return null;

            var bytes = Convert.FromHexString(hex.Substring(130, length * 2));
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static string Pad(string address) =>
        address.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase).ToLowerInvariant().PadLeft(64, '0');
}

/// <summary>What the chain says about an account's ability to trade on one exchange.</summary>
public sealed record ChainAccountState(string? CollateralToken, string? Symbol, decimal? Balance, decimal? Allowance);
