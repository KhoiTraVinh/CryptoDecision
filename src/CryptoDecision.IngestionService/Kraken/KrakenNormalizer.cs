using System.Globalization;
using CryptoDecision.IngestionService.Kraken.Models;
using CryptoDecision.IngestionService.Models;
using CryptoDecision.IngestionService.Normalization;

namespace CryptoDecision.IngestionService.Kraken;

/// <summary>
/// Adapter: converts Kraken v2 wire-format trade records into the internal Trade model.
///
/// Symbol mapping: "BTC/USDT" → "BTCUSDT" (strip slashes)
/// Side mapping:   "buy"  = taker buying  → IsBuyerMaker = false
///                 "sell" = taker selling → IsBuyerMaker = true
/// </summary>
public sealed class KrakenNormalizer : ITradeNormalizer<KrakenTrade>
{
    public Trade Normalize(KrakenTrade t)
    {
        // Kraken uses decimal types directly in JSON
        var price = t.Price;
        var qty   = t.Qty;

        var isBuyerMaker = t.Side.Equals("sell", StringComparison.OrdinalIgnoreCase);

        // "BTC/USDT" → "BTCUSDT"
        var symbol = t.Symbol.Replace("/", "").ToUpperInvariant();

        // Parse ISO 8601 timestamp
        var tradeTime = DateTimeOffset.Parse(t.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        return new Trade(
            Symbol:       symbol,
            TradeId:      t.TradeId,
            Price:        price,
            Quantity:     qty,
            QuoteQty:     price * qty,
            IsBuyerMaker: isBuyerMaker,
            TradeTime:    tradeTime
        );
    }
}
