using CryptoDecision.ApiService.Domain.Entities;
using CryptoDecision.ApiService.Domain.Interfaces;

namespace CryptoDecision.ApiService.Application;

/// <summary>
/// Every read the API and the SignalR broadcasters perform.
///
/// This replaces a MediatR request/handler/validator/pipeline stack that existed
/// to serve what are, without exception, single-repository reads projected into a
/// DTO. Each endpoint previously needed a query record, a handler class, a
/// validator class and two pipeline passes; it now needs a method. Same behaviour,
/// same DTOs, same validation rules — roughly a fifth of the code, and the path
/// from an HTTP route to the SQL behind it is one call deep.
/// </summary>
public sealed class MarketQueries(
    IFeatureRepository    featureRepo,
    IMomentumRepository   momentumRepo,
    IKlineRepository      klineRepo,
    IVolumeRepository     volumeRepo,
    ITradeQueryRepository tradeRepo)
{
    // ── Validation helpers ────────────────────────────────────────────────────

    private static string RequireSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) || !SupportedSymbols.All.Contains(symbol))
            throw new RequestValidationException(nameof(symbol), SupportedSymbols.ValidationMessage);
        return symbol.ToUpperInvariant();
    }

    private static int RequireRange(int value, int min, int max, string field)
    {
        if (value < min || value > max)
            throw new RequestValidationException(field, $"{field} must be between {min} and {max}");
        return value;
    }

    // ── Market status ─────────────────────────────────────────────────────────

    public async Task<MarketStatusDto> GetMarketStatusAsync(string symbol, CancellationToken ct = default)
    {
        var s = RequireSymbol(symbol);

        var feature = await featureRepo.GetTodayAsync(s, ct);

        return new MarketStatusDto(
            Symbol:       s,
            Return24h:    feature?.Return24h,
            Volatility:   feature?.Volatility,
            VolumeChange: feature?.VolumeChange,
            WhaleCount:   feature?.WhaleCount,
            Vwap:         feature?.Vwap,
            AsOf:         DateTime.UtcNow);
    }

    // ── Dashboard ─────────────────────────────────────────────────────────────

    public async Task<DashboardDto> GetDashboardAsync(
        string symbol, int days = 30, CancellationToken ct = default)
    {
        var s = RequireSymbol(symbol);
        var d = RequireRange(days, 1, 90, nameof(days));

        var history = await featureRepo.GetHistoryAsync(s, d, ct);

        return new DashboardDto(
            Symbol:  s,
            History: history.Select(f => new DailyFeatureDto(
                         f.Date, f.Return24h, f.Volatility,
                         f.VolumeChange, f.WhaleCount, f.Vwap, f.TotalVolume)).ToList(),
            AsOf:    DateTime.UtcNow);
    }

    // ── Momentum ──────────────────────────────────────────────────────────────

    public async Task<MomentumDto> GetMomentumAsync(
        string symbol, string exchange = "BINANCE", CancellationToken ct = default)
    {
        var s    = RequireSymbol(symbol);
        var data = await momentumRepo.GetAsync(s, exchange, ct);
        return MomentumScorer.BuildDto(s, data);
    }

    // ── Klines ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<KlineDto>> GetKlinesAsync(
        string symbol, int limit = 60, string exchange = "BINANCE", CancellationToken ct = default)
    {
        var s = RequireSymbol(symbol);
        var n = RequireRange(limit, 1, 500, nameof(limit));

        var klines = await klineRepo.GetRecentAsync(s, n, exchange, ct);
        return klines
            .Select(k => new KlineDto(k.OpenTime, k.Open, k.High, k.Low, k.Close, k.Volume, k.NumTrades))
            .ToList();
    }

    // ── Volume ────────────────────────────────────────────────────────────────

    public async Task<VolumeAnalysisDto> GetVolumeAnalysisAsync(
        string symbol, string exchange = "BINANCE", CancellationToken ct = default)
    {
        var s   = RequireSymbol(symbol);
        var raw = await volumeRepo.GetWindowsAsync(s, exchange, ct);
        return VolumeAnalysisMapper.BuildDto(s, raw);
    }

    // ── Whales ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WhaleTradeDto>> GetRecentWhalesAsync(
        string symbol, string exchange = "BINANCE", int limit = 50, CancellationToken ct = default)
    {
        var s = RequireSymbol(symbol);
        var n = RequireRange(limit, 1, 500, nameof(limit));

        var data = await tradeRepo.GetLatestWhalesAsync(s, exchange, n, ct);
        return data
            .Select(x => new WhaleTradeDto(x.Symbol, x.Exchange, x.Price, x.QuoteQty, x.IsBuyerMaker, x.TradeTime))
            .ToList();
    }
}

// ── Scoring / mapping helpers (shared with the SignalR broadcasters) ─────────

internal static class MomentumScorer
{
    private const int WindowMinutes = 5;

    public static (decimal score, string signal) Compute(MomentumData data)
    {
        if (data.TotalTrades == 0)
            return (0m, "NEUTRAL");

        var buyRatio = (decimal)data.BuyCount / data.TotalTrades;

        decimal ratioScore = buyRatio switch
        {
            > 0.70m  =>  3m,
            > 0.60m  =>  2m,
            > 0.55m  =>  1m,
            >= 0.45m =>  0m,
            > 0.40m  => -1m,
            > 0.30m  => -2m,
            _        => -3m
        };

        decimal whaleScore = 0m;
        if (data.WhaleBuyCount  > data.WhaleSellCount * 2)     whaleScore =  1.0m;
        else if (data.WhaleBuyCount  > data.WhaleSellCount)    whaleScore =  0.5m;
        else if (data.WhaleSellCount > data.WhaleBuyCount * 2) whaleScore = -1.0m;
        else if (data.WhaleSellCount > data.WhaleBuyCount)     whaleScore = -0.5m;

        var total  = Math.Round(ratioScore + whaleScore, 2);
        var signal = total switch
        {
            >= 2m  => "STRONG_BUY",
            >= 1m  => "BUY",
            > -1m  => "NEUTRAL",
            >= -2m => "SELL",
            _      => "STRONG_SELL"
        };
        return (total, signal);
    }

    public static MomentumDto BuildDto(string symbol, MomentumData data)
    {
        var (score, signal) = Compute(data);
        var buyRatio = data.TotalTrades > 0
            ? Math.Round((decimal)data.BuyCount / data.TotalTrades, 4)
            : 0m;

        return new MomentumDto(
            Symbol:         symbol,
            WindowMinutes:  WindowMinutes,
            TotalTrades:    data.TotalTrades,
            BuyCount:       data.BuyCount,
            SellCount:      data.SellCount,
            BuyRatio:       buyRatio,
            WhaleBuyCount:  data.WhaleBuyCount,
            WhaleSellCount: data.WhaleSellCount,
            VolumeUsd:      Math.Round(data.VolumeUsd, 2),
            Score:          score,
            Signal:         signal,
            AsOf:           DateTime.UtcNow);
    }
}

internal static class VolumeAnalysisMapper
{
    /// <summary>
    /// The windows the response advertises, in order. Must match what
    /// VolumeRepository actually queries: a window listed here but not computed is
    /// filled with zeros, and a zero is indistinguishable from "no trading" — the
    /// API would report a flat 30-day tape it never looked at.
    /// </summary>
    private static readonly string[] Windows = ["1h", "24h", "7d"];

    public static VolumeAnalysisDto BuildDto(string symbol, IReadOnlyList<VolumeWindowData> raw)
    {
        var byWindow = raw.ToDictionary(w => w.Window, StringComparer.Ordinal);

        var dtos = Windows.Select(w =>
        {
            var d = byWindow.GetValueOrDefault(w)
                ?? new VolumeWindowData(w, 0, 0, 0, 0m, 0m, 0, 0, 0m);
            var total    = d.BuyVolumeUsd + d.SellVolumeUsd;
            var buyRatio = total > 0 ? Math.Round(d.BuyVolumeUsd / total, 4) : 0m;

            return new VolumeWindowDto(
                Window:         d.Window,
                TotalTrades:    d.TotalTrades,
                BuyCount:       d.BuyCount,
                SellCount:      d.SellCount,
                BuyVolumeUsd:   Math.Round(d.BuyVolumeUsd, 2),
                SellVolumeUsd:  Math.Round(d.SellVolumeUsd, 2),
                BuyRatio:       buyRatio,
                NetVolumeUsd:   Math.Round(d.BuyVolumeUsd - d.SellVolumeUsd, 2),
                WhaleBuyCount:  d.WhaleBuyCount,
                WhaleSellCount: d.WhaleSellCount,
                WhaleVolumeUsd: Math.Round(d.WhaleVolumeUsd, 2));
        }).ToList();

        return new VolumeAnalysisDto(symbol, dtos, DateTime.UtcNow);
    }
}
