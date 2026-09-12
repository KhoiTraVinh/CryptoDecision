using System.Net.WebSockets;
using CryptoDecision.IngestionService.Telemetry;
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
public abstract class ExchangeWebSocketClient(ILogger logger, FeedLiveness liveness)
{
    /// <summary>
    /// How long a connection may go without a single frame before it is treated as dead
    /// and torn down.
    ///
    /// The reconnect loop below only re-enters on an exception, and a half-open socket
    /// does not throw: TCP stays up, the peer is gone, and ReceiveAsync blocks forever.
    /// Nothing in this class noticed that — the ping loop kept sending into the void,
    /// because sending on a half-open socket succeeds, and nothing ever checked that a
    /// reply came back. The service would sit connected, silent, and reporting healthy
    /// for as long as the container lived.
    ///
    /// 90 seconds is comfortably past every heartbeat here: OKX pings at 25s and expects
    /// traffic within 30, Bybit at 20, and Binance's own keepalive is 20. A feed with
    /// nothing to say for a minute and a half on a pair that prints continuously is not
    /// quiet, it is gone.
    /// </summary>
    protected virtual TimeSpan IdleTimeout => TimeSpan.FromSeconds(90);

    /// <summary>
    /// Record that this feed delivered something worth having — a trade or a kline that
    /// reached a channel, not merely a frame.
    ///
    /// Called by subclasses because only they can tell the two apart. A connection that
    /// answers every ping and was subscribed to nothing is alive by every measure this
    /// base class can take, and useless.
    /// </summary>
    protected void MarkDataReceived() => liveness.RecordData(ExchangeName);

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

        // Declared before the first connection attempt, so a feed that never connects at
        // all is still a feed the health check knows should exist.
        liveness.Expect(ExchangeName);

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

                liveness.RecordConnected(ExchangeName);

                await OnConnectedAsync(ws, ct);
                backoff = TimeSpan.FromSeconds(1); // reset on success

                // The watchdog shares the connection's lifetime: cancelling it when the
                // receive loop ends is what stops it outliving the socket it watches.
                using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                var watchdogTask = WatchdogAsync(ws, connCts.Token);

                try
                {
                    if (UsesAppLevelPing)
                    {
                        var pingTask    = PingLoopAsync(ws, connCts.Token);
                        var receiveTask = ReceiveLoopAsync(ws, ct);

                        await Task.WhenAny(pingTask, receiveTask);

                        await connCts.CancelAsync();

                        try { await pingTask; }    catch (OperationCanceledException) { }
                        try { await receiveTask; } catch (OperationCanceledException) { }
                    }
                    else
                    {
                        await ReceiveLoopAsync(ws, ct);
                    }
                }
                finally
                {
                    await connCts.CancelAsync();
                    try { await watchdogTask; } catch (OperationCanceledException) { }
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

            // Stamped before processing, not after: this clock answers "is the socket
            // alive", and a message that arrived and then failed to parse still proves
            // the connection is carrying traffic. Whether it carried anything USEFUL is
            // MarkDataReceived's question, and the subclasses answer it.
            liveness.RecordFrame(ExchangeName);

            ms.Position = 0;
            await ProcessMessageAsync(ms, ws, ct);
        }
    }

    // ── Invariant: read watchdog ─────────────────────────────────────────────

    /// <summary>
    /// Abort a connection that has stopped delivering frames.
    ///
    /// <see cref="ClientWebSocket.Abort"/> rather than a graceful close: the point is to
    /// make the blocked ReceiveAsync throw so the reconnect loop re-enters, and a
    /// graceful close handshake needs the peer to answer — which is exactly what is not
    /// happening. Aborting is the only move that works on a socket whose other end is
    /// gone.
    /// </summary>
    private async Task WatchdogAsync(ClientWebSocket ws, CancellationToken ct)
    {
        // Checked several times per timeout so the detection delay is a fraction of the
        // window rather than a second copy of it.
        var tick = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, IdleTimeout.Ticks / 6));

        using var timer = new PeriodicTimer(tick);

        while (await timer.WaitForNextTickAsync(ct))
        {
            if (ws.State != WebSocketState.Open) return;

            var idle = liveness.SinceLastFrame(ExchangeName);
            if (idle is not { } silence || silence <= IdleTimeout) continue;

            logger.LogWarning(
                "{Exchange} WebSocket has delivered nothing for {Idle:F0}s, past the {Limit:F0}s " +
                "limit. The socket still reports Open, which is what a half-open connection looks " +
                "like — aborting it so the reconnect loop can run.",
                ExchangeName, silence.TotalSeconds, IdleTimeout.TotalSeconds);

            ws.Abort();
            return;
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
