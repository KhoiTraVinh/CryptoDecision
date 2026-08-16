using CryptoDecision.IngestionService.Binance.Models;
using CryptoDecision.IngestionService.Models;
using CryptoDecision.IngestionService.Normalization;

namespace CryptoDecision.IngestionService.Binance;

/// <summary>
/// Adapter: converts raw Binance wire-format messages into clean internal domain models.
/// Implements ITradeNormalizer (Adapter Pattern / Anti-Corruption Layer).
/// </summary>
public sealed class BinanceNormalizer : ITradeNormalizer<BinanceTradeMessage>
{
    public Trade Normalize(BinanceTradeMessage msg)
    {
        var price = decimal.Parse(msg.Price,    System.Globalization.CultureInfo.InvariantCulture);
        var qty   = decimal.Parse(msg.Quantity, System.Globalization.CultureInfo.InvariantCulture);

        return new Trade(
            Symbol:       msg.Symbol.ToUpperInvariant(),
            TradeId:      msg.TradeId,
            Price:        price,
            Quantity:     qty,
            QuoteQty:     price * qty,
            IsBuyerMaker: msg.IsBuyerMaker,
            TradeTime:    DateTimeOffset.FromUnixTimeMilliseconds(msg.TradeTime)
        );
    }

    /// <summary>Kline normalization (no interface — only Binance ingests klines).</summary>
    public Kline NormalizeKline(BinanceKlineMessage msg)
    {
        var k = msg.Kline;
        return new Kline(
            Symbol:        msg.Symbol.ToUpperInvariant(),
            OpenTime:      DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTime),
            CloseTime:     DateTimeOffset.FromUnixTimeMilliseconds(k.CloseTime),
            Open:          decimal.Parse(k.Open,        System.Globalization.CultureInfo.InvariantCulture),
            High:          decimal.Parse(k.High,        System.Globalization.CultureInfo.InvariantCulture),
            Low:           decimal.Parse(k.Low,         System.Globalization.CultureInfo.InvariantCulture),
            Close:         decimal.Parse(k.Close,       System.Globalization.CultureInfo.InvariantCulture),
            Volume:        decimal.Parse(k.Volume,      System.Globalization.CultureInfo.InvariantCulture),
            QuoteVolume:   decimal.Parse(k.QuoteVolume, System.Globalization.CultureInfo.InvariantCulture),
            NumberOfTrades: k.NumberOfTrades,
            IsClosed:      k.IsClosed
        );
    }
}
