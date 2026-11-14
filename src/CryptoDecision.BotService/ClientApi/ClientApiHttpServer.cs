using System.Net;
using System.Text;
using System.Text.Json;
using CryptoDecision.BotService.Bot;
using CryptoDecision.BotService.Exchanges;
using CryptoDecision.Shared.Bot;

namespace CryptoDecision.BotService.ClientApi;

/// <summary>
/// The API the customer's phone talks to. Raw <see cref="HttpListener"/> for the same
/// reason the health endpoint is: a worker service has no Kestrel.
///
/// Exposed publicly through a Cloudflare tunnel, so every handler here assumes hostile
/// input. The tunnel terminates TLS; this listener is plain HTTP bound to the port the
/// tunnel points at and must not be published to the internet directly.
///
/// Implemented so far: /api/health and /api/link. The rest of the contract in
/// mobile/README.md — /api/settings, /api/status, /api/history — is not written yet,
/// and this returns 501 for those rather than 404 so the app's errors say "not built"
/// instead of sending someone hunting for a typo in a URL.
/// </summary>
public sealed class ClientApiHttpServer(
    ClientLinkStore store,
    ClientApiQueries queries,
    OkxCredentialVerifier verifier,
    OkxReadClient okx,
    OkxOptions okxOptions,
    IOkxCredentialSource credentials,
    BotConfigRepository configRepo,
    StrategyEvaluator evaluator,
    IConfiguration config,
    ILogger<ClientApiHttpServer> log) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record LinkRequest(string? ApiKey, string? SecretKey, string? Passphrase);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var port = config.GetValue("ClientApi:Port", 8090);

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{port}/");
        listener.Start();
        log.LogInformation(
            "[ClientApi] Listening on :{Port}. Public access is expected to arrive through the "
          + "tunnel, which terminates TLS — do not expose this port directly.", port);

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext? http = null;
            try
            {
                http = await listener.GetContextAsync().WaitAsync(ct);
                await RouteAsync(http, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "[ClientApi] Unhandled error");
                // The customer gets a flat message. Whatever went wrong here is for the
                // log, not for a handset half a planet away.
                if (http is not null)
                    try { await WriteAsync(http, 500, new { error = "Server error." }); }
                    catch { /* client already gone */ }
            }
            finally { try { http?.Response.Close(); } catch { } }
        }

        listener.Stop();
    }

    private async Task RouteAsync(HttpListenerContext http, CancellationToken ct)
    {
        var path   = http.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
        var method = http.Request.HttpMethod;

        // The app is a WebView on a capacitor:// or https://localhost origin, so it is
        // cross-origin to this host and preflights anything with a JSON body.
        http.Response.Headers["Access-Control-Allow-Origin"]  = "*";
        http.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
        http.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";

        if (method == "OPTIONS") { http.Response.StatusCode = 204; return; }

        switch (path, method)
        {
            case ("/api/health", "GET"):
                await WriteAsync(http, 200, new { ok = true });
                return;

            case ("/api/link", "POST"):
                await LinkAsync(http, ct);
                return;

            case ("/api/link", "GET"):
                await GetLinkAsync(http, ct);
                return;

            case ("/api/link", "DELETE"):
                await UnlinkAsync(http, ct);
                return;

            case ("/api/settings", "GET"):
                await GetSettingsAsync(http, ct);
                return;

            case ("/api/settings", "PUT"):
                await PutSettingsAsync(http, ct);
                return;

            case ("/api/status", "GET"):
                await GetStatusAsync(http, ct);
                return;

            case ("/api/history", "GET"):
                await GetHistoryAsync(http, ct);
                return;

            default:
                await WriteAsync(http, 404, new { error = "No such endpoint." });
                return;
        }
    }

    /* ── POST /api/link ─────────────────────────────────────────────────── */

    private async Task LinkAsync(HttpListenerContext http, CancellationToken ct)
    {
        LinkRequest? body;
        try
        {
            using var reader = new StreamReader(http.Request.InputStream, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync(ct);

            // Bounded before parsing: this endpoint is open to the internet and takes
            // no auth, so an unbounded read is a free way to exhaust the process.
            if (raw.Length > 4096)
            {
                await WriteAsync(http, 400, new { error = "Request too large." });
                return;
            }
            body = JsonSerializer.Deserialize<LinkRequest>(raw, Json);
        }
        catch (JsonException)
        {
            await WriteAsync(http, 400, new { error = "Malformed request." });
            return;
        }

        var apiKey = body?.ApiKey?.Trim();
        var secret = body?.SecretKey?.Trim();
        var pass   = body?.Passphrase?.Trim();

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(pass))
        {
            await WriteAsync(http, 400, new { error = "Send apiKey, secretKey and passphrase." });
            return;
        }

        // Verified against OKX BEFORE anything is stored. A key with the wrong
        // permissions or a missing whitelist entry has to fail here, while the customer
        // is looking at the screen that can fix it — not silently at the first entry
        // signal, which may be days away.
        var check = await verifier.CheckAsync(apiKey, secret, pass, ct);
        if (!check.Ok)
        {
            log.LogInformation("[ClientApi] Link refused: {Error}", check.Error);
            await WriteAsync(http, 400, new { error = check.Error });
            return;
        }

        var token = await store.LinkAsync(
            apiKey, secret, pass, check.Permissions!, check.Uid, ct);

        // Without this the bot keeps signing with whatever it cached for up to the
        // source TTL — on a RE-link that means orders going to the previous key.
        (credentials as LinkedOkxCredentialSource)?.Invalidate();


        log.LogWarning(
            "[ClientApi] OKX key {Masked} LINKED (uid {Uid}, perm {Perm}). This server can now "
          + "place real orders on that account.",
            ClientLinkStore.Mask(apiKey), check.Uid ?? "?", check.Permissions);

        await WriteAsync(http, 200, new
        {
            token,
            apiKeyMasked = ClientLinkStore.Mask(apiKey),
            permissions  = check.Permissions,
        });
    }

    /* ── GET / DELETE /api/link ─────────────────────────────────────────── */

    private async Task GetLinkAsync(HttpListenerContext http, CancellationToken ct)
    {
        var link = await AuthAsync(http, ct);
        if (link is null) return;

        await WriteAsync(http, 200, new
        {
            apiKeyMasked = link.ApiKeyMasked,
            permissions  = link.Permissions,
            linkedAt     = link.LinkedAt,
        });
    }

    private async Task UnlinkAsync(HttpListenerContext http, CancellationToken ct)
    {
        var link = await AuthAsync(http, ct);
        if (link is null) return;

        await store.UnlinkAsync(ct);

        // Unlink is a kill switch, so it takes effect now rather than whenever the
        // cached credentials happen to expire.
        (credentials as LinkedOkxCredentialSource)?.Invalidate();


        log.LogWarning(
            "[ClientApi] OKX key {Masked} UNLINKED. No further orders will be placed for it.",
            link.ApiKeyMasked);

        await WriteAsync(http, 200, new { ok = true });
    }

    /* ── /api/settings ──────────────────────────────────────────────────── */

    /// <summary>
    /// The size bounds the app shows and enforces.
    ///
    /// Upper bound is Okx:MaxOrderNotionalUsd — the backstop against a fat-fingered
    /// size — and NOT the balance: leverage means a position can exceed the collateral
    /// behind it, so a balance-derived ceiling would be wrong in both directions.
    /// </summary>
    private (decimal Min, decimal Max) SizeBounds()
    {
        // Floor is the smallest order OKX accepts across the instruments traded here,
        // rounded up to a whole dollar. Below it the exchange rejects outright and the
        // customer sees a failure they cannot explain.
        var min = config.GetValue("ClientApi:MinAmountUsd", 10m);
        var max = okxOptions.MaxOrderNotionalUsd;
        return (min, max);
    }

    /// <summary>
    /// The stop the bot would actually place, read from the strategy rather than from
    /// bot_config. They disagree — the live stop is ATR geometry floored at MinStopPct,
    /// while bot_config.stop_loss_pct is a stale literal — and the app multiplies this
    /// number by the customer's size to tell them what they can lose. Quoting the wrong
    /// one understates their risk.
    /// </summary>
    private async Task<decimal?> LiveStopPctAsync(CancellationToken ct)
    {
        var opts = await configRepo.GetConfigAsync(ct);
        if (opts is null) return null;

        // Worst case across the active strategies: the customer gets one number, and
        // the honest one is the largest loss any of them could take.
        decimal? worst = null;
        foreach (var strat in opts.ActiveStrategies)
        {
            var profile = evaluator.DescribeRisk(strat, opts);
            if (profile is null) continue;
            if (worst is null || profile.StopPct > worst) worst = profile.StopPct;
        }
        return worst;
    }

    private async Task GetSettingsAsync(HttpListenerContext http, CancellationToken ct)
    {
        var link = await AuthAsync(http, ct);
        if (link is null) return;

        var (min, max) = SizeBounds();

        await WriteAsync(http, 200, new
        {
            amountPerTradeUsd = (await configRepo.GetConfigAsync(ct))?.FixedNotionalUsd is > 0m and var a ? a : (decimal?)null,
            minAmountUsd      = min,
            maxAmountUsd      = max,
            stopPct           = await LiveStopPctAsync(ct),
        });
    }

    private sealed record SettingsRequest(decimal? AmountPerTradeUsd);

    private async Task PutSettingsAsync(HttpListenerContext http, CancellationToken ct)
    {
        var link = await AuthAsync(http, ct);
        if (link is null) return;

        SettingsRequest? body;
        try
        {
            using var reader = new StreamReader(http.Request.InputStream, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync(ct);
            if (raw.Length > 1024)
            {
                await WriteAsync(http, 400, new { error = "Request too large." });
                return;
            }
            body = JsonSerializer.Deserialize<SettingsRequest>(raw, Json);
        }
        catch (JsonException)
        {
            await WriteAsync(http, 400, new { error = "Malformed request." });
            return;
        }

        var amount = body?.AmountPerTradeUsd;
        if (amount is null or <= 0m)
        {
            await WriteAsync(http, 400, new { error = "Send an amount greater than zero." });
            return;
        }

        // Checked again here on purpose. The app checks these too, for a fast answer,
        // but anything holding the token can call this endpoint and the app's copy of
        // the bounds is whatever was built into that APK.
        var (min, max) = SizeBounds();
        if (amount < min)
        {
            await WriteAsync(http, 400, new { error = $"OKX rejects orders under ${min}." });
            return;
        }
        if (amount > max)
        {
            await WriteAsync(http, 400, new { error = $"Above the per-order limit of ${max}." });
            return;
        }

        // bot_config, not client_link: this is the column EntrySizing actually reads.
        await configRepo.SetFixedNotionalAsync(amount.Value, ct);
        log.LogInformation(
            "[ClientApi] Size for {Masked} set to ${Amount}. Applies to the next entry; "
          + "open positions keep the size they were opened with.",
            link.ApiKeyMasked, amount);

        await WriteAsync(http, 200, new { amountPerTradeUsd = amount });
    }

    /* ── /api/status ────────────────────────────────────────────────────── */

    private async Task GetStatusAsync(HttpListenerContext http, CancellationToken ct)
    {
        var link = await AuthAsync(http, ct);
        if (link is null) return;

        var state = await queries.GetBotStateAsync(ct);
        var today = await queries.GetTodayAsync(ct);

        // Read with the CUSTOMER's key, so the balance is theirs. Everything else on
        // this screen still comes from the bot's own book — see ClientApiQueries.
        decimal? available = null;
        var creds = await store.GetCredentialsAsync(ct);
        if (creds is { } c)
            available = await okx.GetAvailableUsdAsync(
                new OkxCreds(c.ApiKey, c.Secret, c.Passphrase), ct);

        await WriteAsync(http, 200, new
        {
            enabled       = state?.Enabled ?? false,
            paperMode     = state?.PaperMode ?? true,
            lastEvalAt    = state?.LastEvalAt,
            availableUsd  = available,
            openPositions = today.OpenPositions,
            pnlTodayUsd   = today.PnlTodayUsd,
            tradesToday   = today.TradesToday,
            note          = await queries.GetWaitingNoteAsync(ct),
        });
    }

    /* ── /api/history ───────────────────────────────────────────────────── */

    /// <summary>
    /// Read from OKX with the customer's own key, not from bot_trades. Operator's
    /// choice, 2026-11-13, and the right one: the numbers then match the customer's
    /// exchange statement exactly, including fees and funding, which the bot's own
    /// pnl_usd does not.
    ///
    /// The consequence to be aware of: these figures will NOT tie out against
    /// bot_trades, and a position closed by hand on okx.com appears here while being
    /// absent from the bot's book. That is the exchange telling the truth about the
    /// account, which is what the customer is being shown.
    /// </summary>
    private async Task GetHistoryAsync(HttpListenerContext http, CancellationToken ct)
    {
        var link = await AuthAsync(http, ct);
        if (link is null) return;

        var creds = await store.GetCredentialsAsync(ct);
        if (creds is not { } c)
        {
            await WriteAsync(http, 401, new { error = "This device is no longer linked." });
            return;
        }

        // Only the pair this bot trades. Operator's call, 2026-11-13: the customer's
        // account may hold anything, and history that mixes in positions the bot never
        // opened reads as the bot having done things it did not.
        var instId = config.GetValue("ClientApi:HistoryInstId", "SOL-USDT-SWAP");

        var closed = await okx.GetClosedPositionsAsync(
            new OkxCreds(c.ApiKey, c.Secret, c.Passphrase), 100, instId, ct);

        if (closed is null)
        {
            // Distinguished from an empty history on purpose: "no trades yet" and "we
            // could not ask OKX" look identical on the screen otherwise, and only one
            // of them is worth the customer doing something about.
            await WriteAsync(http, 502, new { error = "Could not read your history from OKX." });
            return;
        }

        var wins = closed.Count(p => p.PnlUsd > 0m);

        await WriteAsync(http, 200, new
        {
            netPnlUsd = closed.Sum(p => p.PnlUsd),
            count     = closed.Count,
            // Null rather than 0 on an empty history: a 0% win rate reads like a
            // losing streak, and no trades is not one.
            winRate   = closed.Count == 0 ? (double?)null : (double)wins / closed.Count,
            trades    = closed.Select(p => new
            {
                side        = p.Side,
                symbol      = p.Symbol,
                closedAt    = p.ClosedAt,
                closeReason = p.CloseReason,
                pnlUsd      = p.PnlUsd,
            }),
        });
    }

    /* ── Helpers ────────────────────────────────────────────────────────── */

    /// <summary>
    /// Resolves the bearer token, or writes 401 and returns null. Callers return
    /// immediately on null — the response is already finished.
    /// </summary>
    private async Task<ClientLink?> AuthAsync(HttpListenerContext http, CancellationToken ct)
    {
        var header = http.Request.Headers["Authorization"];
        if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            await WriteAsync(http, 401, new { error = "Not linked." });
            return null;
        }

        var link = await store.GetByTokenAsync(header["Bearer ".Length..].Trim(), ct);
        if (link is null)
        {
            // Also the path taken after the key is unlinked from elsewhere, which is why
            // the app treats 401 as "show the Connect screen" rather than as an error.
            await WriteAsync(http, 401, new { error = "This device is no longer linked." });
            return null;
        }
        return link;
    }

    private static async Task WriteAsync(HttpListenerContext http, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        http.Response.StatusCode  = status;
        http.Response.ContentType = "application/json";
        http.Response.ContentLength64 = bytes.Length;
        await http.Response.OutputStream.WriteAsync(bytes);
    }
}
