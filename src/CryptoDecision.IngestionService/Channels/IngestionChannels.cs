using System.Threading.Channels;
using CryptoDecision.IngestionService.Models;

namespace CryptoDecision.IngestionService.Channels;

/// <summary>
/// Bounded channel for raw trades. Acts as a backpressure buffer between the
/// WebSocket receiver and the Kafka batch publisher.
///
/// BoundedChannelFullMode.Wait: the writer blocks when the channel is full,
/// which signals upstream (WebSocket) to slow down — natural backpressure.
///
/// Capacity 50,000 gives roughly 5–10s of runway at Binance peak throughput.
/// </summary>
public class TradeChannel
{
    private readonly Channel<Trade> _channel;

    public TradeChannel(int capacity = 50_000)
    {
        _channel = Channel.CreateBounded<Trade>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,  // multiple batch publishers could read
            SingleWriter = false   // WebSocket writes; could be multiple symbols
        });
    }

    public ChannelWriter<Trade>  Writer => _channel.Writer;
    public ChannelReader<Trade>  Reader => _channel.Reader;

    public int PendingCount => _channel.Reader.Count;
}

/// <summary>Bounded trade channel for OKX trades (isolated from Binance channel).</summary>
public sealed class OkxTradeChannel() : TradeChannel(50_000);

/// <summary>Bounded trade channel for Bybit trades (isolated from Binance channel).</summary>
public sealed class BybitTradeChannel() : TradeChannel(50_000);

/// <summary>Bounded trade channel for Coinbase trades (isolated per exchange).</summary>
public sealed class CoinbaseTradeChannel() : TradeChannel(50_000);

/// <summary>Bounded trade channel for Kraken trades (isolated per exchange).</summary>
public sealed class KrakenTradeChannel() : TradeChannel(50_000);

/// <summary>
/// Bounded channel for normalized klines. Klines are lower volume (one per
/// second per symbol) so a smaller buffer is fine.
/// </summary>
public sealed class KlineChannel
{
    private readonly Channel<Kline> _channel;

    public KlineChannel(int capacity = 1_000)
    {
        _channel = Channel.CreateBounded<Kline>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public ChannelWriter<Kline>  Writer => _channel.Writer;
    public ChannelReader<Kline>  Reader => _channel.Reader;

    public int PendingCount => _channel.Reader.Count;
}
