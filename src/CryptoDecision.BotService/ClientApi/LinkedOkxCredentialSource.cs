using CryptoDecision.BotService.Exchanges;

namespace CryptoDecision.BotService.ClientApi;

/// <summary>
/// Signs with the key the CUSTOMER linked through the app, read from <c>client_link</c>.
///
/// Registered in place of <see cref="ConfiguredOkxCredentialSource"/> when the client
/// API is on, and it does NOT fall back to the environment. That is the point: with a
/// customer's key in use, Okx__ApiKey can be removed from the server entirely, and a
/// silent fallback would mean the bot quietly trading the OPERATOR's own account the
/// moment the customer unlinks — the worst failure this feature could have, because
/// nothing about it would look wrong in a log.
///
/// No link means no trading, said plainly.
/// </summary>
public sealed class LinkedOkxCredentialSource(
    ClientLinkStore store,
    ILogger<LinkedOkxCredentialSource> log) : IOkxCredentialSource
{
    // Signed calls happen many times a cycle and each read is a database round trip
    // plus an AES decrypt, so the result is held briefly. The TTL is also the window
    // in which an unlink is still honoured late; 15s is short enough that a customer
    // revoking access sees it stop within a cycle, and long enough to keep the order
    // path off the database.
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private OkxCredentials? _cached;
    private DateTime _loadedAt = DateTime.MinValue;
    private volatile bool _primed;

    public async Task<OkxCredentials?> GetAsync(CancellationToken ct)
    {
        if (_primed && DateTime.UtcNow - _loadedAt < Ttl) return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            // Re-checked inside the gate: several signed calls arrive together at the
            // start of a cycle and only one of them should hit the database.
            if (_primed && DateTime.UtcNow - _loadedAt < Ttl) return _cached;
            return await LoadAsync(ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<OkxCredentials?> LoadAsync(CancellationToken ct)
    {
        var was = _cached;

        var creds = await store.GetCredentialsAsync(ct);
        _cached   = creds is { } c ? new OkxCredentials(c.ApiKey, c.Secret, c.Passphrase) : null;
        _loadedAt = DateTime.UtcNow;
        _primed   = true;

        // Logged on transition only. This is checked every few seconds and a line each
        // time would bury everything else; the CHANGE is the event worth seeing, and
        // "which account is being traded" is the most important fact about this process.
        if (was is not null && _cached is null)
            log.LogWarning(
                "[ClientApi] No OKX key is linked any more. The bot will place no orders "
              + "until a key is linked through the app.");
        else if (_cached is not null && was?.ApiKey != _cached.ApiKey)
            log.LogWarning(
                "[ClientApi] Now trading the LINKED account ({Masked}). Orders from here "
              + "commit THAT account's funds, not the operator's.",
                ClientLinkStore.Mask(_cached.ApiKey));

        return _cached;
    }

    public string? DescribeRefusal()
    {
        // Self-healing rather than fatal: the snapshot is loaded at startup, so this
        // only shows if something asks before PrimeAsync has finished. One skipped
        // cycle is the cost, and it says honestly why.
        if (!_primed)
            return "the linked OKX key has not been loaded yet.";

        return _cached is null
            ? "no OKX key is linked through the app yet (client_link is empty)."
            : null;
    }

    public string Describe() =>
        _cached is null
            ? "the app-linked key — none linked, so no orders can be placed"
            : $"the app-linked key {ClientLinkStore.Mask(_cached.ApiKey)}";

    /// <summary>
    /// Forces the next read to hit the database. Called after link and unlink, so a
    /// re-link does not keep signing with the previous key for the rest of the TTL
    /// and an unlink takes effect as the kill switch it is meant to be.
    /// </summary>
    public void Invalidate() => _loadedAt = DateTime.MinValue;

    public async Task PrimeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await LoadAsync(ct); }
        finally { _gate.Release(); }
    }
}
