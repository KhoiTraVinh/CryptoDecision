using System.Globalization;
using CryptoDecision.IngestionService.Coinbase.Models;
using CryptoDecision.IngestionService.Models;
using CryptoDecision.IngestionService.Normalization;

namespace CryptoDecision.IngestionService.Coinbase;

/// <summary>
/// Adapter: converts Coinbase Advanced Trade wire-format records into the internal Trade model.
///
/// Symbol mapping: "BTC-USDT" → "BTCUSDT" (strip hyphens)
/// Side mapping:   "BUY"  = taker buying  → IsBuyerMaker = false
///                 "SELL" = taker selling → IsBuyerMaker = true
/// </summary>
public sealed class CoinbaseNormalizer : ITradeNormalizer<CoinbaseTrade>
{
    public Trade Normalize(CoinbaseTrade t)
    {
        var price = decimal.Parse(t.Price, CultureInfo.InvariantCulture);
        var qty   = decimal.Parse(t.Size,  CultureInfo.InvariantCulture);

        // Coinbase SELL = taker sells = maker had the buy order → IsBuyerMaker = true
        var isBuyerMaker = t.Side.Equals("SELL", StringComparison.OrdinalIgnoreCase);

        // "BTC-USDT" → "BTCUSDT"
        var symbol = t.ProductId.Replace("-", "").ToUpperInvariant();

        // Trade ID can be numeric string
        long tradeId = long.TryParse(t.TradeId, out var id) ? id : t.TradeId.GetHashCode();

        // Parse ISO 8601 timestamp
        var tradeTime = DateTimeOffset.Parse(t.Time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        return new Trade(
            Symbol:       symbol,
            TradeId:      tradeId,
            Price:        price,
            Quantity:     qty,
            QuoteQty:     price * qty,
            IsBuyerMaker: isBuyerMaker,
            TradeTime:    tradeTime
        );
    }
}
