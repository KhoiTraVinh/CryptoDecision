namespace CryptoDecision.ApiService.Application;

// ── Supported symbols (single source of truth) ───────────────────────────────

internal static class SupportedSymbols
{
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BTCUSDT", "ETHUSDT" };

    public static string ValidationMessage =>
        $"Symbol must be one of: {string.Join(", ", All)}";
}

/// <summary>
/// Thrown when a request argument fails validation. GlobalExceptionMiddleware
/// maps this to a 400 with a structured body.
///
/// This replaces FluentValidation plus a MediatR pipeline behaviour. The rules
/// here are "is this one of two symbols" and "is this integer in range" — a
/// validator class and a pipeline stage per query was more machinery than the
/// checks warranted.
/// </summary>
public sealed class RequestValidationException(string field, string message)
    : Exception(message)
{
    public string Field => field;
}

// ── Market DTOs ──────────────────────────────────────────────────────────────

public sealed record MarketStatusDto(
    string   Symbol,
    decimal? Return24h,
    decimal? Volatility,
    decimal? VolumeChange,
    int?     WhaleCount,
    decimal? Vwap,
    string?  PredictedDirection,
    decimal? Confidence,
    string?  Rationale,
    DateTime AsOf
);

public sealed record DailyFeatureDto(
    DateOnly Date,
    decimal  Return24h,
    decimal  Volatility,
    decimal  VolumeChange,
    int      WhaleCount,
    decimal  Vwap,
    decimal  TotalVolume
);

public sealed record DashboardDto(
    string                         Symbol,
    IReadOnlyList<DailyFeatureDto> History,
    string?  PredictedDirection,
    decimal? Confidence,
    string?  Rationale,
    string?  ModelVersion,
    DateTime AsOf
);

public sealed record MomentumDto(
    string   Symbol,
    int      WindowMinutes,
    int      TotalTrades,
    int      BuyCount,
    int      SellCount,
    decimal  BuyRatio,
    int      WhaleBuyCount,
    int      WhaleSellCount,
    decimal  VolumeUsd,
    decimal  Score,
    string   Signal,
    DateTime AsOf
);

public sealed record KlineDto(
    DateTime OpenTime,
    decimal  Open,
    decimal  High,
    decimal  Low,
    decimal  Close,
    decimal  Volume,
    int      NumTrades
);


public sealed record VolumeWindowDto(
    string  Window,
    int     TotalTrades,
    int     BuyCount,
    int     SellCount,
    decimal BuyVolumeUsd,
    decimal SellVolumeUsd,
    decimal BuyRatio,
    decimal NetVolumeUsd,
    int     WhaleBuyCount,
    int     WhaleSellCount,
    decimal WhaleVolumeUsd
);

public sealed record VolumeAnalysisDto(
    string                         Symbol,
    IReadOnlyList<VolumeWindowDto> Windows,
    DateTime                       AsOf
);

public sealed record WhaleTradeDto(
    string   Symbol,
    string   Exchange,
    decimal  Price,
    decimal  QuoteQty,
    bool     IsBuyerMaker,
    DateTime TradeTime
);

public sealed record WhaleAlertDto(
    string                         Symbol,
    string                         Exchange,
    IReadOnlyList<WhaleTradeDto>   Whales,
    DateTime                       AsOf
);
