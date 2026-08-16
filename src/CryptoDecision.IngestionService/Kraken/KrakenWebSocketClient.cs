using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CryptoDecision.IngestionService.Channels;
using CryptoDecision.IngestionService.Kraken.Models;
using CryptoDecision.IngestionService.WebSocket;
using Microsoft.Extensions.Logging;

namespace CryptoDecision.IngestionService.Kraken;

/// <summary>
/// Kraken WebSocket v2 client.
/// Extends ExchangeWebSocketClient (Template Method).
///
/// Subscribes to trade channel for BTC/USDT and ETH/USDT.
/// Kraken v2 uses JSON subscribe with "method":"subscribe".
/// Kraken sends ping frames natively — no app-level ping needed.
/// </summary>
public sealed class KrakenWebSocketClient(
    KrakenTradeChannel tradeChannel,
    KrakenNormalizer normalizer,
    ILogger<KrakenWebSocketClient> logger) : ExchangeWebSocketClient(logger)
{
    // Kraken v2 subscribe message
    private static readonly byte[] SubscribeMsg = Encoding.UTF8.GetBytes("""
        {"method":"subscribe","params":{"channel":"trade","symbol":["BTC/USDT","ETH/USDT"]}}
        """);

    protected override string ExchangeName => "Kraken";

    protected override Uri GetConnectionUri()
        => new("wss://ws.kraken.com/v2");

    protected override async Task OnConnectedAsync(ClientWebSocket ws, CancellationToken ct)
    {
        await ws.SendAsync(SubscribeMsg, WebSocketMessageType.Text, endOfMessage: true, ct);
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

            // Ignore system/subscription confirmation messages
            if (root.TryGetProperty("method", out _)) return;
            if (root.TryGetProperty("result", out _)) return;

            // Only process trade channel messages
            if (!root.TryGetProperty("channel", out var chEl)) return;
            if (chEl.GetString() != "trade") return;
        }

        // Re-parse as typed message
        ms.Position = 0;
        KrakenWsMessage? msg;
        try { msg = JsonSerializer.Deserialize<KrakenWsMessage>(ms); }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Kraken failed to deserialize trade message");
            return;
        }

        if (msg?.Data is null) return;

        foreach (var t in msg.Data)
        {
            try
            {
                var trade = normalizer.Normalize(t);
                await tradeChannel.Writer.WriteAsync(trade, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Kraken failed to normalize trade {TradeId}", t.TradeId);
            }
        }
    }
}
