namespace CryptoDecision.BotService.Exchanges;

/// <summary>The three values every signed OKX request needs.</summary>
public sealed record OkxCredentials(string ApiKey, string Secret, string Passphrase);

/// <summary>
/// Where <see cref="OkxSignedClient"/> gets the credentials to sign with.
///
/// Introduced so the account being traded can be the CUSTOMER's, supplied through the
/// app, instead of the operator's in environment variables. The signing code should not
/// know or care which of those it is holding.
/// </summary>
public interface IOkxCredentialSource
{
    /// <summary>Credentials to sign with, or null when there are none to use.</summary>
    Task<OkxCredentials?> GetAsync(CancellationToken ct);

    /// <summary>
    /// Null when a signed call could be made; otherwise the reason it cannot, phrased
    /// for an operator reading a log line. Synchronous because it gates the order path
    /// in places that are not async, so implementations answer from a snapshot rather
    /// than going to the database on the hot path.
    /// </summary>
    string? DescribeRefusal();

    /// <summary>One line for the startup banner: which account this process trades.</summary>
    string Describe();

    /// <summary>
    /// Loads the snapshot <see cref="DescribeRefusal"/> answers from. Called once at
    /// startup and again whenever the source may have changed.
    /// </summary>
    Task PrimeAsync(CancellationToken ct);
}

/// <summary>
/// The original behaviour: credentials come from Okx:ApiKey / ApiSecret / Passphrase.
/// This is what runs when the client API is off, so a deployment that only trades the
/// operator's own account is unchanged by any of this.
/// </summary>
public sealed class ConfiguredOkxCredentialSource(OkxOptions opts) : IOkxCredentialSource
{
    public Task<OkxCredentials?> GetAsync(CancellationToken ct) =>
        Task.FromResult(opts.HasCredentials
            ? new OkxCredentials(opts.ApiKey, opts.ApiSecret, opts.Passphrase)
            : null);

    public string? DescribeRefusal() =>
        opts.HasCredentials
            ? null
            : "OKX API credentials are not configured (Okx:ApiKey, Okx:ApiSecret, Okx:Passphrase).";

    public string Describe() =>
        opts.HasCredentials
            ? "configured credentials (Okx:ApiKey), i.e. the operator's own account"
            : "no credentials";

    public Task PrimeAsync(CancellationToken ct) => Task.CompletedTask;
}
