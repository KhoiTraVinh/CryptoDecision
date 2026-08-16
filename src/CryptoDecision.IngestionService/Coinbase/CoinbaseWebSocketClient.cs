using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CryptoDecision.IngestionService.Channels;
using CryptoDecision.IngestionService.Coinbase.Models;
using CryptoDecision.IngestionService.WebSocket;
using Microsoft.Extensions.Logging;

namespace CryptoDecision.IngestionService.Coinbase;

/// <summary>
/// Coinbase Advanced Trade WebSocket client.
/// Extends ExchangeWebSocketClient (Template Method).
///
/// Subscribes to market_trades channel for BTC-USDT and ETH-USDT.
/// Coinbase uses JSON subscribe message with product_ids array.
/// No application-level ping needed — Coinbase handles keepalive via WS pings.
/// </summary>
public sealed class CoinbaseWebSocketClient(
    CoinbaseTradeChannel tradeChannel,
    CoinbaseNormalizer normalizer,
    ILogger<CoinbaseWebSocketClient> logger) : ExchangeWebSocketClient(logger)
{
    // Coinbase Advanced Trade WebSocket subscribe message
    private static readonly byte[] SubscribeMsg = Encoding.UTF8.GetBytes("""
        {"type":"subscribe","product_ids":["BTC-USDT","ETH-USDT"],"channel":"market_trades"}
        """);

    protected override string ExchangeName => "Coinbase";

    protected override Uri GetConnectionUri()
        => new("wss://advanced-trade-ws.coinbase.com");

    protected override async Task OnConnectedAsync(ClientWebSocket ws, CancellationToken ct)
    {
        await ws.SendAsync(SubscribeMsg, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    protected override async ValueTask ProcessMessageAsync(MemoryStream ms, ClientWebSocket ws, CancellationToken ct)
    {
        ms.Position = 0;
        CoinbaseWsMessage? msg;
        try { msg = JsonSerializer.Deserialize<CoinbaseWsMessage>(ms); }
        catch (JsonException) { return; }

        if (msg?.Channel != "market_trades" || msg.Events is null) return;

        foreach (var evt in msg.Events)
        {
            if (evt.Trades is null) continue;

            foreach (var t in evt.Trades)
            {
                try
                {
                    var trade = normalizer.Normalize(t);
                    await tradeChannel.Writer.WriteAsync(trade, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Coinbase failed to normalize trade {TradeId}", t.TradeId);
                }
            }
        }
    }
}
