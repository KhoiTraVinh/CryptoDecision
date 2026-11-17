using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace CryptoDecision.BotService.ClientApi;

/// <summary>
/// A linked customer key, as the API reports it. Never carries the secret.
/// </summary>
/// <param name="AccountId">
/// Which account the bearer token belongs to. Carried on the record rather than
/// looked up again because every caller that authenticates immediately needs it: the
/// client API serves handsets for every account from one process, so "which account"
/// is the first thing it must know and the easiest thing to get wrong.
/// </param>
public sealed record ClientLink(
    int AccountId,
    string ApiKeyMasked,
    string Permissions,
    string? OkxUid,
    DateTime LinkedAt);

/// <summary>
/// Reads and writes <c>client_link</c>: one row per trading account.
///
/// It held exactly one row until 057, enforced by a CHECK in the database. That was
/// right while the operator's key was the only key, and became the wrong shape the
/// day a customer was handed the app -- their link would have overwritten the
/// operator's, moved the bot onto their funds, and logged nothing alarming.
///
/// Secret and passphrase are AES-GCM ciphertext under <c>ClientApi:EncryptionKey</c>,
/// which lives in the environment and never in the database, so a dump of this table
/// alone yields nothing usable. The bearer token is stored only as its SHA-256, so the
/// table cannot be used to impersonate the handset either.
/// </summary>
public sealed class ClientLinkStore
{
    private readonly NpgsqlDataSource _db;
    private readonly byte[] _key;

    public ClientLinkStore(NpgsqlDataSource db, IConfiguration config)
    {
        _db = db;

        var raw = config["ClientApi:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                "ClientApi__EncryptionKey is not set. The client API stores a customer's OKX "
              + "secret, so it refuses to start without a key to encrypt it with. Generate one "
              + "with: openssl rand -base64 32");

        try { _key = Convert.FromBase64String(raw); }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                "ClientApi__EncryptionKey is not valid base64. Generate one with: "
              + "openssl rand -base64 32");
        }

        if (_key.Length != 32)
            throw new InvalidOperationException(
                $"ClientApi__EncryptionKey decodes to {_key.Length} bytes; AES-256-GCM needs 32. "
              + "Generate one with: openssl rand -base64 32");
    }

    /* ── Crypto ─────────────────────────────────────────────────────────── */

    // nonce (12) ‖ tag (16) ‖ ciphertext, base64. Packed together because the nonce
    // must be fresh per encryption and is useless to an attacker, so storing it
    // beside the ciphertext costs nothing and saves a second column that could get
    // out of step with this one.
    private string Encrypt(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var bytes = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[bytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, bytes, cipher, tag);

        var packed = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, nonce.Length);
        cipher.CopyTo(packed, nonce.Length + tag.Length);
        return Convert.ToBase64String(packed);
    }

    private string Decrypt(string packedB64)
    {
        var packed = Convert.FromBase64String(packedB64);
        var nonce  = packed.AsSpan(0, 12);
        var tag    = packed.AsSpan(12, 16);
        var cipher = packed.AsSpan(28);

        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Shows enough to match against the OKX key list, not enough to use.</summary>
    public static string Mask(string apiKey) =>
        apiKey.Length <= 8 ? "••••" : apiKey[..4] + "…" + apiKey[^4..];

    /* ── Commands ───────────────────────────────────────────────────────── */

    /// <summary>
    /// The account a new link should attach to, or null when every provisioned
    /// account already has a key.
    ///
    /// Two rules, in order:
    ///
    ///   1. If this OKX key is already linked, it keeps the account it has. Without
    ///      this a customer reinstalling the app would consume a second slot, or hit
    ///      the unique index and be told their own key is in use.
    ///   2. Otherwise the lowest PROVISIONED account with no key. "Provisioned" means
    ///      a bot_config row exists, which is deliberate: accounts are created by an
    ///      operator alongside the container that will trade them, so a stranger with
    ///      the tunnel URL cannot call /api/link and conjure an account that nothing
    ///      is running for.
    ///
    /// Returning null rather than inventing an id is the point. The alternative --
    /// allocating the next integer -- writes a key that no process is watching, and
    /// the customer sees a successful link followed by a bot that never trades.
    /// </summary>
    public async Task<int?> AllocateAccountAsync(string apiKey, CancellationToken ct)
    {
        const string sql = """
            SELECT COALESCE(
                (SELECT id FROM client_link WHERE api_key = @k),
                (SELECT c.id
                   FROM bot_config c
                   LEFT JOIN client_link l ON l.id = c.id
                  WHERE l.id IS NULL
                  ORDER BY c.id
                  LIMIT 1)
            )
            """;

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("k", apiKey);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : (int)result;
    }

    /// <summary>
    /// Attaches a key to one account and returns a fresh bearer token for the handset.
    ///
    /// The account id is supplied rather than assumed. It used to be the literal 1,
    /// which meant a second customer's link silently took over the first's account --
    /// the failure this whole change exists to remove.
    /// </summary>
    public async Task<string> LinkAsync(
        int accountId,
        string apiKey, string secret, string passphrase,
        string permissions, string? uid, CancellationToken ct)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                           .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        const string sql = """
            INSERT INTO client_link
                (id, api_key, secret_enc, passphrase_enc, permissions, okx_uid, token_hash, linked_at)
            VALUES (@id, @k, @s, @p, @perm, @uid, @th, now())
            ON CONFLICT (id) DO UPDATE SET
                api_key        = EXCLUDED.api_key,
                secret_enc     = EXCLUDED.secret_enc,
                passphrase_enc = EXCLUDED.passphrase_enc,
                permissions    = EXCLUDED.permissions,
                okx_uid        = EXCLUDED.okx_uid,
                token_hash     = EXCLUDED.token_hash,
                linked_at      = now()
            """;

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", accountId);
        cmd.Parameters.AddWithValue("k", apiKey);
        cmd.Parameters.AddWithValue("s", Encrypt(secret));
        cmd.Parameters.AddWithValue("p", Encrypt(passphrase));
        cmd.Parameters.AddWithValue("perm", permissions);
        cmd.Parameters.AddWithValue("uid", (object?)uid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("th", HashToken(token));
        await cmd.ExecuteNonQueryAsync(ct);

        return token;
    }

    /// <summary>Removes one account's key. The kill switch, scoped to that account.</summary>
    public async Task<bool> UnlinkAsync(int accountId, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM client_link WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", accountId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /* ── Queries ────────────────────────────────────────────────────────── */

    /// <summary>
    /// The link for a bearer token, or null if the token does not match.
    ///
    /// Looked up by token alone now that there is more than one row: the token IS the
    /// statement of which account the caller is, and 057 made token_hash unique so
    /// that claim cannot be ambiguous.
    /// </summary>
    public async Task<ClientLink?> GetByTokenAsync(string token, CancellationToken ct)
    {
        const string sql = """
            UPDATE client_link SET last_seen_at = now()
            WHERE token_hash = @th
            RETURNING id, api_key, permissions, okx_uid, linked_at
            """;

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("th", HashToken(token));

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        return new ClientLink(
            r.GetInt32(0),
            Mask(r.GetString(1)),
            r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.GetDateTime(4));
    }

    /// <summary>
    /// The decrypted credentials for ONE account, for the code that actually trades.
    /// Separate from <see cref="GetByTokenAsync"/> so that everything serving the app
    /// works with a type that cannot leak a secret into a log line or a JSON response.
    /// </summary>
    public async Task<(string ApiKey, string Secret, string Passphrase)?> GetCredentialsAsync(
        int accountId, CancellationToken ct)
    {
        const string sql =
            "SELECT api_key, secret_enc, passphrase_enc FROM client_link WHERE id = @id";

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", accountId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        return (r.GetString(0), Decrypt(r.GetString(1)), Decrypt(r.GetString(2)));
    }
}
