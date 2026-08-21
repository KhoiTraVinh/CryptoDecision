using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CryptoDecision.IngestionService.Bybit.Models;
using CryptoDecision.IngestionService.Channels;
using CryptoDecision.IngestionService.Configuration;
using CryptoDecision.IngestionService.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoDecision.IngestionService.Bybit;

/// <summary>
/// Bybit v5 public spot WebSocket client.
/// Extends ExchangeWebSocketClient (Template Method) — only subscription, ping, and message processing differ.
///
/// Bybit heartbeat: client sends {"op":"ping"} every 20s.
/// </summary>
public sealed class BybitWebSocketClient(
    BybitTradeChannel tradeChannel,
    BybitNormalizer normalizer,
    IOptions<MarketSubscriptionSettings> subscription,
    ILogger<BybitWebSocketClient> logger) : ExchangeWebSocketClient(logger)
{
    private static readonly byte[] PingMsg = Encoding.UTF8.GetBytes("""{"op":"ping"}""");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = false
    };

    protected override string ExchangeName => "Bybit";

    protected override Uri GetConnectionUri()
        => new("wss://stream.bybit.com/v5/public/spot");

    protected override void ConfigureWebSocket(ClientWebSocket ws)
    {
        ws.Options.KeepAliveInterval = TimeSpan.Zero;
    }

    protected override async Task OnConnectedAsync(ClientWebSocket ws, CancellationToken ct)
    {
        // Bybit takes the undashed symbol form, which is the configured pair with
        // its dash removed — see MarketSubscriptionSettings for why the dashed form
        // is the one that gets configured.
        var symbols = subscription.Value.BybitSymbols.ToArray();
        var args    = string.Join(",", symbols.Select(s => $"\"publicTrade.{s}\""));
        var payload = Encoding.UTF8.GetBytes($"{{\"op\":\"subscribe\",\"args\":[{args}]}}");

        logger.LogInformation("[Bybit] Subscribing to trades: {Symbols}", string.Join(", ", symbols));

        await ws.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    protected override bool UsesAppLevelPing => true;
    protected override TimeSpan PingInterval => TimeSpan.FromSeconds(20);

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

            if (root.TryGetProperty("op", out var opEl))
            {
                logger.LogDebug("Bybit control message op={Op}", opEl.GetString());
                return;
            }

            if (root.TryGetProperty("success", out _)) return;

            if (!root.TryGetProperty("topic", out var topicEl)) return;
            var topic = topicEl.GetString() ?? string.Empty;
            if (!topic.StartsWith("publicTrade", StringComparison.OrdinalIgnoreCase)) return;
        }

        ms.Position = 0;
        BybitTradeEvent? evt;
        try { evt = JsonSerializer.Deserialize<BybitTradeEvent>(ms, JsonOpts); }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Bybit failed to deserialize trade event");
            return;
        }

        if (evt?.Data is null) return;

        foreach (var t in evt.Data)
        {
            try
            {
                var trade = normalizer.Normalize(t);
                await tradeChannel.Writer.WriteAsync(trade, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bybit failed to normalize trade {TradeId}", t.TradeId);
            }
        }
    }
}
