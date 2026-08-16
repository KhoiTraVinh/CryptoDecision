using System.Globalization;
using CryptoDecision.IngestionService.Bybit.Models;
using CryptoDecision.IngestionService.Models;
using CryptoDecision.IngestionService.Normalization;

namespace CryptoDecision.IngestionService.Bybit;

/// <summary>
/// Adapter: converts Bybit v5 wire-format trade records into the internal Trade model.
///
/// Side mapping: "Buy"  = taker is buying  → IsBuyerMaker = false
///               "Sell" = taker is selling  → IsBuyerMaker = true
/// </summary>
public sealed class BybitNormalizer : ITradeNormalizer<BybitTrade>
{
    public Trade Normalize(BybitTrade t)
    {
        var price = decimal.Parse(t.Price,  CultureInfo.InvariantCulture);
        var qty   = decimal.Parse(t.Volume, CultureInfo.InvariantCulture);

        var isBuyerMaker = t.Side.Equals("Sell", StringComparison.OrdinalIgnoreCase);

        return new Trade(
            Symbol:       t.Symbol.ToUpperInvariant(),
            TradeId:      long.Parse(t.TradeId, CultureInfo.InvariantCulture),
            Price:        price,
            Quantity:     qty,
            QuoteQty:     price * qty,
            IsBuyerMaker: isBuyerMaker,
            TradeTime:    DateTimeOffset.FromUnixTimeMilliseconds(t.Timestamp)
        );
    }
}
