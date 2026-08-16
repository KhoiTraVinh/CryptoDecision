using System.Globalization;
using CryptoDecision.IngestionService.Models;
using CryptoDecision.IngestionService.Normalization;
using CryptoDecision.IngestionService.OKX.Models;

namespace CryptoDecision.IngestionService.OKX;

/// <summary>
/// Adapter: converts OKX v5 wire-format trade records into the internal Trade model.
///
/// Symbol mapping: OKX uses "BTC-USDT" → normalize to "BTCUSDT" (remove dash).
/// Side mapping:   "buy"  = taker is buying  → IsBuyerMaker = false
///                 "sell" = taker is selling  → IsBuyerMaker = true
/// </summary>
public sealed class OkxNormalizer : ITradeNormalizer<OkxTrade>
{
    public Trade Normalize(OkxTrade t)
    {
        var price = decimal.Parse(t.Px, CultureInfo.InvariantCulture);
        var qty   = decimal.Parse(t.Sz, CultureInfo.InvariantCulture);

        var isBuyerMaker = t.Side.Equals("sell", StringComparison.OrdinalIgnoreCase);

        return new Trade(
            Symbol:       t.InstId.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant(),
            TradeId:      long.Parse(t.TradeId, CultureInfo.InvariantCulture),
            Price:        price,
            Quantity:     qty,
            QuoteQty:     price * qty,
            IsBuyerMaker: isBuyerMaker,
            TradeTime:    DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(t.Ts, CultureInfo.InvariantCulture))
        );
    }
}
