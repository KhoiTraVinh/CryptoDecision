using System.Text.Json;

namespace CryptoDecision.BotService.ClientApi;

/// <summary>What OKX said about a key the customer just handed us.</summary>
public sealed record OkxKeyCheck(bool Ok, string? Permissions, string? Uid, string? Error);

/// <summary>
/// Checks a customer-supplied OKX key by reading /api/v5/account/config with it.
///
/// Deliberately not built on <see cref="Exchanges.OkxSignedClient"/>: that client signs
/// with the credentials in configuration — the account the bot already trades — and it
/// sits on the live order path. Verifying somebody else's key is a different job, and
/// threading "sign with these instead" through the trading client would put a parameter
/// on every signed call whose only safe value is null.
/// </summary>
public sealed class OkxCredentialVerifier(
    OkxReadClient okx,
    ILogger<OkxCredentialVerifier> log)
{
    public async Task<OkxKeyCheck> CheckAsync(
        string apiKey, string secret, string passphrase, CancellationToken ct)
    {
        var creds = new OkxCreds(apiKey, secret, passphrase);
        var res   = await okx.GetAsync(creds, "/api/v5/account/config", ct);

        if (!res.Ok)
        {
            log.LogInformation("[ClientApi] Key check refused by OKX, code {Code}", res.Code);
            return new OkxKeyCheck(false, null, null, res.Error);
        }

        string? perm = null, uid = null;
        if (res.Data.ValueKind == JsonValueKind.Array && res.Data.GetArrayLength() > 0)
        {
            var first = res.Data[0];
            perm = first.TryGetProperty("perm", out var p) ? p.GetString() : null;
            uid  = first.TryGetProperty("uid",  out var u) ? u.GetString() : null;
        }

        // The whole point of verifying before storing: a read-only key authenticates
        // perfectly and then fails at the first order, hours later, with nobody watching.
        if (perm is null || !perm.Contains("trade", StringComparison.OrdinalIgnoreCase))
        {
            return new OkxKeyCheck(false, perm, uid,
                $"This key has '{perm ?? "no"}' permission. It needs Trade as well as Read. "
              + "Create a new key with both ticked — leave Withdraw off.");
        }

        // Not a hard failure: OKX has never returned withdraw here for a key made the way
        // the app asks, so refusing outright would most likely mean refusing a good key
        // over a field we misread. Loud in the log is the right weight.
        if (perm.Contains("withdraw", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning(
                "[ClientApi] The linked key reports WITHDRAW permission ({Perm}). Nothing here "
              + "can withdraw, but the customer was told to leave it off and should be asked "
              + "to replace this key with a read+trade one.", perm);
        }

        return new OkxKeyCheck(true, perm, uid, null);
    }
}
