using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace CryptoDecision.IngestionService.WebSocket;

/// <summary>
/// Template Method Pattern: encapsulates the invariant reconnect → receive → ping
/// lifecycle shared by all exchange WebSocket clients.
///
/// Subclasses override only what differs per exchange:
///   - Connection URI, keepalive settings
///   - Post-connect subscription messages
///   - Message processing (deserialization + normalization + channel write)
///   - Ping behavior (interval, payload, server-ping response)
///
/// Adding a new exchange = extend this class + override 4–5 methods. Zero base code modified (OCP).
/// </summary>
public abstract class ExchangeWebSocketClient(ILogger logger)
{
    /// <summary>Exchange name used in log messages.</summary>
    protected abstract string ExchangeName { get; }

    /// <summary>WebSocket URI to connect to.</summary>
    protected abstract Uri GetConnectionUri();

    /// <summary>Configure the WebSocket before connecting (e.g. KeepAliveInterval).</summary>
    protected virtual void ConfigureWebSocket(ClientWebSocket ws)
    {
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    /// <summary>Called after connect — send subscription messages here.</summary>
    protected virtual Task OnConnectedAsync(ClientWebSocket ws, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>Process a complete (reassembled) message. Override per exchange.</summary>
    protected abstract ValueTask ProcessMessageAsync(MemoryStream ms, ClientWebSocket ws, CancellationToken ct);

    /// <summary>Whether this exchange requires an application-level ping loop.</summary>
    protected virtual bool UsesAppLevelPing => false;

    /// <summary>Interval between app-level pings. Only used if UsesAppLevelPing = true.</summary>
    protected virtual TimeSpan PingInterval => TimeSpan.FromSeconds(20);

    /// <summary>Send a ping frame. Only called if UsesAppLevelPing = true.</summary>
    protected virtual Task SendPingAsync(ClientWebSocket ws, CancellationToken ct)
        => Task.CompletedTask;

    // ── Template Method: reconnect loop ──────────────────────────────────────

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff    = TimeSpan.FromSeconds(1);
        var maxBackoff = TimeSpan.FromSeconds(60);

        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            ConfigureWebSocket(ws);

            try
            {
                var uri = GetConnectionUri();
                logger.LogInformation("Connecting to {Exchange} WebSocket: {Uri}", ExchangeName, uri);
                await ws.ConnectAsync(uri, ct);
                logger.LogInformation("{Exchange} WebSocket connected", ExchangeName);

                await OnConnectedAsync(ws, ct);
                backoff = TimeSpan.FromSeconds(1); // reset on success

                if (UsesAppLevelPing)
                {
                    using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var pingTask    = PingLoopAsync(ws, pingCts.Token);
                    var receiveTask = ReceiveLoopAsync(ws, ct);

                    await Task.WhenAny(pingTask, receiveTask);
                    await pingCts.CancelAsync();

                    try { await pingTask; }    catch (OperationCanceledException) { }
                    try { await receiveTask; } catch (OperationCanceledException) { }
                }
                else
                {
                    await ReceiveLoopAsync(ws, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogInformation("{Exchange} WebSocket shutting down gracefully", ExchangeName);
                return;
            }
            catch (WebSocketException ex)
            {
                logger.LogWarning(ex, "{Exchange} WebSocket lost. Reconnecting in {Backoff}s",
                    ExchangeName, backoff.TotalSeconds);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Exchange} WebSocket unexpected error. Reconnecting in {Backoff}s",
                    ExchangeName, backoff.TotalSeconds);
            }

            if (!ct.IsCancellationRequested)
            {
                await Task.Delay(backoff, ct);
                backoff = TimeSpan.FromMilliseconds(
                    Math.Min(backoff.TotalMilliseconds * 2, maxBackoff.TotalMilliseconds));
            }
        }
    }

    // ── Invariant: receive loop with fragment reassembly ─────────────────────

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;

            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    logger.LogWarning("{Exchange} server closed WebSocket: {Description}",
                        ExchangeName, result.CloseStatusDescription);
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            ms.Position = 0;
            await ProcessMessageAsync(ms, ws, ct);
        }
    }

    // ── Invariant: ping loop ─────────────────────────────────────────────────

    private async Task PingLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PingInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (ws.State != WebSocketState.Open) return;
            await SendPingAsync(ws, ct);
            logger.LogDebug("{Exchange} ping sent", ExchangeName);
        }
    }
}
