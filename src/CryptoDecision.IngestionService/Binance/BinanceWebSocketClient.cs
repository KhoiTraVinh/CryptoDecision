using System.Net.WebSockets;
using System.Text.Json;
using CryptoDecision.IngestionService.Binance.Models;
using CryptoDecision.IngestionService.Channels;
using CryptoDecision.IngestionService.Models;
using CryptoDecision.IngestionService.Serialization;
using CryptoDecision.IngestionService.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoDecision.IngestionService.Binance;

/// <summary>
/// Binance combined-stream WebSocket client.
/// Extends ExchangeWebSocketClient (Template Method) — only message processing differs.
///
/// Binance uses WS-level keepalive (no app-level ping needed).
/// Do NOT use PropertyNameCaseInsensitive — Binance uses both "e" and "E" as distinct keys.
/// </summary>
public sealed class BinanceWebSocketClient(
    TradeChannel tradeChannel,
    KlineChannel klineChannel,
    BinanceNormalizer normalizer,
    IOptions<BinanceSettings> settings,
    ILogger<BinanceWebSocketClient> logger) : ExchangeWebSocketClient(logger)
{
    protected override string ExchangeName => "Binance";

    protected override Uri GetConnectionUri()
    {
        var cfg = settings.Value;
        var streams = string.Join("/", cfg.Streams);
        return new Uri($"{cfg.BaseUrl}/stream?streams={streams}");
    }

    protected override void ConfigureWebSocket(ClientWebSocket ws)
    {
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    // No subscription needed — Binance combined stream uses URL path

    protected override async ValueTask ProcessMessageAsync(MemoryStream ms, ClientWebSocket ws, CancellationToken ct)
    {
        CombinedStreamMessage? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(ms, BinanceJsonContext.Default.CombinedStreamMessage);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize Binance message");
            return;
        }

        if (envelope is null) return;

        if (envelope.Stream.Contains("@trade", StringComparison.OrdinalIgnoreCase))
        {
            var msg = envelope.Data.Deserialize(BinanceJsonContext.Default.BinanceTradeMessage);
            if (msg is null) return;

            var trade = normalizer.Normalize(msg);
            await tradeChannel.Writer.WriteAsync(trade, ct);
        }
        else if (envelope.Stream.Contains("@kline", StringComparison.OrdinalIgnoreCase))
        {
            var msg = envelope.Data.Deserialize(BinanceJsonContext.Default.BinanceKlineMessage);
            if (msg is null) return;

            var kline = normalizer.NormalizeKline(msg);
            await klineChannel.Writer.WriteAsync(kline, ct);
        }
        else
        {
            logger.LogDebug("Unknown stream type: {Stream}", envelope.Stream);
        }
    }
}

public sealed class BinanceSettings
{
    public const string Section = "Binance";
    public string BaseUrl { get; set; } = "wss://stream.binance.com:9443";
    public string[] Streams { get; set; } = [];
}
