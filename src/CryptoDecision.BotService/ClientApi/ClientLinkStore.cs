using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace CryptoDecision.BotService.ClientApi;

/// <summary>A linked customer key, as the API reports it. Never carries the secret.</summary>
public sealed record ClientLink(
    string ApiKeyMasked,
    string Permissions,
    string? OkxUid,
    decimal? AmountPerTradeUsd,
    DateTime LinkedAt);

/// <summary>
/// Reads and writes the single <c>client_link</c> row.
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

    /// <summary>Replaces the link and returns a fresh bearer token for the handset.</summary>
    public async Task<string> LinkAsync(
        string apiKey, string secret, string passphrase,
        string permissions, string? uid, CancellationToken ct)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                           .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        // Re-linking keeps amount_per_trade_usd: the customer set a size they meant, and
        // silently resetting it to the server default because they rotated a key is how
        // a position comes back ten times larger than they expect.
        const string sql = """
            INSERT INTO client_link
                (id, api_key, secret_enc, passphrase_enc, permissions, okx_uid, token_hash, linked_at)
            VALUES (1, @k, @s, @p, @perm, @uid, @th, now())
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
        cmd.Parameters.AddWithValue("k", apiKey);
        cmd.Parameters.AddWithValue("s", Encrypt(secret));
        cmd.Parameters.AddWithValue("p", Encrypt(passphrase));
        cmd.Parameters.AddWithValue("perm", permissions);
        cmd.Parameters.AddWithValue("uid", (object?)uid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("th", HashToken(token));
        await cmd.ExecuteNonQueryAsync(ct);

        return token;
    }

    /// <summary>Stores the position size the customer chose. Bounds are the caller's job.</summary>
    public async Task SetAmountPerTradeAsync(decimal amountUsd, CancellationToken ct)
    {
        const string sql =
            "UPDATE client_link SET amount_per_trade_usd = @a WHERE id = 1";

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("a", amountUsd);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> UnlinkAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM client_link WHERE id = 1", conn);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /* ── Queries ────────────────────────────────────────────────────────── */

    /// <summary>The link for a bearer token, or null if the token does not match.</summary>
    public async Task<ClientLink?> GetByTokenAsync(string token, CancellationToken ct)
    {
        const string sql = """
            UPDATE client_link SET last_seen_at = now()
            WHERE id = 1 AND token_hash = @th
            RETURNING api_key, permissions, okx_uid, amount_per_trade_usd, linked_at
            """;

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("th", HashToken(token));

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        return new ClientLink(
            Mask(r.GetString(0)),
            r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : r.GetDecimal(3),
            r.GetDateTime(4));
    }

    /// <summary>
    /// The decrypted credentials, for the code that actually trades. Separate from
    /// <see cref="GetByTokenAsync"/> so that everything serving the app works with a
    /// type that cannot leak a secret into a log line or a JSON response.
    /// </summary>
    public async Task<(string ApiKey, string Secret, string Passphrase)?> GetCredentialsAsync(
        CancellationToken ct)
    {
        const string sql =
            "SELECT api_key, secret_enc, passphrase_enc FROM client_link WHERE id = 1";

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        return (r.GetString(0), Decrypt(r.GetString(1)), Decrypt(r.GetString(2)));
    }
}
