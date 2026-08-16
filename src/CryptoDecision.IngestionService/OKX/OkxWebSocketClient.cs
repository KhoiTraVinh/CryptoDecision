using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CryptoDecision.IngestionService.Channels;
using CryptoDecision.IngestionService.OKX.Models;
using CryptoDecision.IngestionService.WebSocket;
using Microsoft.Extensions.Logging;

namespace CryptoDecision.IngestionService.OKX;

/// <summary>
/// OKX v5 public WebSocket client.
/// Extends ExchangeWebSocketClient (Template Method) — only subscription, ping, and message processing differ.
///
/// OKX heartbeat protocol (application-level, not WS ping frames):
///   - Server sends {"event":"ping"} if no message for 30s
///   - Client must respond {"op":"pong"}
///   - Client should also proactively send {"op":"ping"} every 25s
/// </summary>
public sealed class OkxWebSocketClient(
    OkxTradeChannel tradeChannel,
    OkxNormalizer normalizer,
    ILogger<OkxWebSocketClient> logger) : ExchangeWebSocketClient(logger)
{
    private static readonly byte[] SubscribeMsg = Encoding.UTF8.GetBytes("""
        {"op":"subscribe","args":[{"channel":"trades","instId":"BTC-USDT"},{"channel":"trades","instId":"ETH-USDT"}]}
        """);
    private static readonly byte[] PingMsg  = Encoding.UTF8.GetBytes("ping");
    private static readonly byte[] PongMsg  = Encoding.UTF8.GetBytes("pong");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = false
    };

    protected override string ExchangeName => "OKX";

    protected override Uri GetConnectionUri()
        => new("wss://ws.okx.com:8443/ws/v5/public");

    protected override void ConfigureWebSocket(ClientWebSocket ws)
    {
        ws.Options.KeepAliveInterval = TimeSpan.Zero; // OKX uses app-level heartbeat
    }

    protected override async Task OnConnectedAsync(ClientWebSocket ws, CancellationToken ct)
    {
        await ws.SendAsync(SubscribeMsg, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    protected override bool UsesAppLevelPing => true;
    protected override TimeSpan PingInterval => TimeSpan.FromSeconds(25);

    protected override async Task SendPingAsync(ClientWebSocket ws, CancellationToken ct)
    {
        await ws.SendAsync(PingMsg, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    protected override async ValueTask ProcessMessageAsync(MemoryStream ms, ClientWebSocket ws, CancellationToken ct)
    {
        ms.Position = 0;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(ms); }
        catch (JsonException) { return; }

        using (doc)
        {
            var root = doc.RootElement;

            // Handle server ping: {"event":"ping"} → respond pong
            if (root.TryGetProperty("event", out var evtEl))
            {
                var evt = evtEl.GetString();
                if (evt == "ping")
                {
                    if (ws.State == WebSocketState.Open)
                        await ws.SendAsync(PongMsg, WebSocketMessageType.Text, endOfMessage: true, ct);
                    return;
                }
                if (evt == "error")
                {
                    root.TryGetProperty("msg", out var msgEl);
                    logger.LogWarning("OKX error event: {Msg}", msgEl.GetString());
                }
                return;
            }

            if (!root.TryGetProperty("data", out _)) return;
        }

        ms.Position = 0;
        OkxTradeEvent? evt2;
        try { evt2 = JsonSerializer.Deserialize<OkxTradeEvent>(ms, JsonOpts); }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "OKX failed to deserialize trade event");
            return;
        }

        if (evt2?.Data is null) return;

        foreach (var t in evt2.Data)
        {
            try
            {
                var trade = normalizer.Normalize(t);
                await tradeChannel.Writer.WriteAsync(trade, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "OKX failed to normalize trade {TradeId}", t.TradeId);
            }
        }
    }
}
