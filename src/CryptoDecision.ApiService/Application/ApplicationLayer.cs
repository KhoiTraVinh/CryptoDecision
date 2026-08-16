using CryptoDecision.ApiService.Domain.Entities;
using CryptoDecision.ApiService.Domain.Interfaces;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CryptoDecision.ApiService.Application;

// ── Supported symbols (single source of truth) ───────────────────────────────

internal static class SupportedSymbols
{
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BTCUSDT", "ETHUSDT" };

    public static string ValidationMessage =>
        $"Symbol must be one of: {string.Join(", ", All)}";
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

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

public sealed record UserStatsDto(
    int                    TotalUsers,
    int                    TodayActive,
    IReadOnlyList<string>  RecentNames
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

// ── Alert DTOs ───────────────────────────────────────────────────────────────

public sealed record PriceAlertDto(
    long    Id,
    string? UserId,
    string  Symbol,
    string  Condition,
    decimal TargetPrice,
    bool    IsActive,
    bool    IsTriggered,
    DateTime? TriggeredAt,
    decimal?  TriggeredPrice,
    DateTime  CreatedAt,
    string?   Note
);

public sealed record AlertNotificationDto(
    long    AlertId,
    string  Symbol,
    string  Condition,
    decimal TargetPrice,
    decimal ActualPrice,
    DateTime TriggeredAt
);

public sealed record AlertTriggeredDto(
    long    AlertId,
    string? UserId,
    string  Symbol,
    string  Condition,
    decimal TargetPrice,
    decimal ActualPrice,
    string? Note,
    DateTime TriggeredAt
);

// ── Queries ────────────────────────────────────────────────────────────────────

public sealed record GetMarketStatusQuery(string Symbol, string Exchange = "BINANCE") : IRequest<MarketStatusDto>;
public sealed record GetDashboardQuery(string Symbol, int Days = 30, string Exchange = "BINANCE") : IRequest<DashboardDto>;
public sealed record GetMomentumQuery(string Symbol, string Exchange = "BINANCE") : IRequest<MomentumDto>;
public sealed record GetKlinesQuery(string Symbol, int Limit = 60, string Exchange = "BINANCE") : IRequest<IReadOnlyList<KlineDto>>;
public sealed record GetVolumeAnalysisQuery(string Symbol, string Exchange = "BINANCE") : IRequest<VolumeAnalysisDto>;

public sealed record RegisterUserCommand(string Name, string DeviceId) : IRequest<int>;
public sealed record GetUserStatsQuery : IRequest<UserStatsDto>;
public sealed record GetRecentWhalesQuery(string Symbol, string Exchange = "BINANCE", int Limit = 50) : IRequest<IReadOnlyList<WhaleTradeDto>>;

// Alert queries/commands
public sealed record CreateAlertCommand(string Symbol, string Condition, decimal TargetPrice, string? UserId = null, string? Note = null) : IRequest<PriceAlertDto>;
public sealed record GetAlertsQuery(string? Symbol) : IRequest<IReadOnlyList<PriceAlertDto>>;
public sealed record GetAlertHistoryQuery(string? Symbol, int Limit = 50) : IRequest<IReadOnlyList<AlertNotificationDto>>;
public sealed record DeleteAlertCommand(long Id) : IRequest<bool>;

// ── Validators ────────────────────────────────────────────────────────────────

public sealed class GetMarketStatusValidator : AbstractValidator<GetMarketStatusQuery>
{
    public GetMarketStatusValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty()
            .Must(s => SupportedSymbols.All.Contains(s))
            .WithMessage(SupportedSymbols.ValidationMessage);
    }
}

public sealed class GetDashboardValidator : AbstractValidator<GetDashboardQuery>
{
    public GetDashboardValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty()
            .Must(s => SupportedSymbols.All.Contains(s))
            .WithMessage(SupportedSymbols.ValidationMessage);
        RuleFor(x => x.Days)
            .InclusiveBetween(1, 90)
            .WithMessage("Days must be between 1 and 90");
    }
}

public sealed class GetMomentumValidator : AbstractValidator<GetMomentumQuery>
{
    public GetMomentumValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty()
            .Must(s => SupportedSymbols.All.Contains(s))
            .WithMessage(SupportedSymbols.ValidationMessage);
    }
}

public sealed class GetKlinesValidator : AbstractValidator<GetKlinesQuery>
{
    public GetKlinesValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty()
            .Must(s => SupportedSymbols.All.Contains(s))
            .WithMessage(SupportedSymbols.ValidationMessage);
        RuleFor(x => x.Limit)
            .InclusiveBetween(1, 500)
            .WithMessage("Limit must be between 1 and 500");
    }
}

public sealed class GetVolumeAnalysisValidator : AbstractValidator<GetVolumeAnalysisQuery>
{
    public GetVolumeAnalysisValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty()
            .Must(s => SupportedSymbols.All.Contains(s))
            .WithMessage(SupportedSymbols.ValidationMessage);
    }
}

// ── MomentumScorer (shared by handler + broadcast service) ───────────────────

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
        if (data.WhaleBuyCount  > data.WhaleSellCount * 2)  whaleScore =  1.0m;
        else if (data.WhaleBuyCount  > data.WhaleSellCount)  whaleScore =  0.5m;
        else if (data.WhaleSellCount > data.WhaleBuyCount * 2) whaleScore = -1.0m;
        else if (data.WhaleSellCount > data.WhaleBuyCount)   whaleScore = -0.5m;

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

// ── VolumeAnalysisMapper (shared by handler + broadcast service) ──────────────

internal static class VolumeAnalysisMapper
{
    private static readonly string[] Windows = ["1h", "24h", "7d", "30d", "3m", "6m", "1y"];

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
                Window:         w,
                TotalTrades:    d.TotalTrades,
                BuyCount:       d.BuyCount,
                SellCount:      d.SellCount,
                BuyVolumeUsd:   Math.Round(d.BuyVolumeUsd, 2),
                SellVolumeUsd:  Math.Round(d.SellVolumeUsd, 2),
                BuyRatio:       buyRatio,
                NetVolumeUsd:   Math.Round(d.BuyVolumeUsd - d.SellVolumeUsd, 2),
                WhaleBuyCount:  d.WhaleBuyCount,
                WhaleSellCount: d.WhaleSellCount,
                WhaleVolumeUsd: Math.Round(d.WhaleVolumeUsd, 2)
            );
        }).ToList();

        return new VolumeAnalysisDto(symbol, dtos, DateTime.UtcNow);
    }
}

// ── Handlers ──────────────────────────────────────────────────────────────────

public sealed class GetMarketStatusHandler(
    IFeatureRepository featureRepo,
    IPredictionRepository predictionRepo,
    ILogger<GetMarketStatusHandler> logger)
    : IRequestHandler<GetMarketStatusQuery, MarketStatusDto>
{
    public async Task<MarketStatusDto> Handle(
        GetMarketStatusQuery request, CancellationToken ct)
    {
        var symbol = request.Symbol.ToUpperInvariant();
        logger.LogDebug("GetMarketStatus: {Symbol}", symbol);

        var (feature, prediction) = await (
            featureRepo.GetTodayAsync(symbol, ct),
            predictionRepo.GetLatestAsync(symbol, ct)
        ).WhenAll();

        return new MarketStatusDto(
            Symbol:             symbol,
            Return24h:          feature?.Return24h,
            Volatility:         feature?.Volatility,
            VolumeChange:       feature?.VolumeChange,
            WhaleCount:         feature?.WhaleCount,
            Vwap:               feature?.Vwap,
            PredictedDirection: prediction?.Direction,
            Confidence:         prediction?.Confidence,
            Rationale:          prediction?.Rationale,
            AsOf:               DateTime.UtcNow);
    }
}

public sealed class GetDashboardHandler(
    IFeatureRepository featureRepo,
    IPredictionRepository predictionRepo,
    ILogger<GetDashboardHandler> logger)
    : IRequestHandler<GetDashboardQuery, DashboardDto>
{
    public async Task<DashboardDto> Handle(
        GetDashboardQuery request, CancellationToken ct)
    {
        var symbol = request.Symbol.ToUpperInvariant();
        logger.LogDebug("GetDashboard: {Symbol} days={Days}", symbol, request.Days);

        var (history, prediction) = await (
            featureRepo.GetHistoryAsync(symbol, request.Days, ct),
            predictionRepo.GetLatestAsync(symbol, ct)
        ).WhenAll();

        return new DashboardDto(
            Symbol:             symbol,
            History:            history.Select(f => new DailyFeatureDto(
                                    f.Date, f.Return24h, f.Volatility,
                                    f.VolumeChange, f.WhaleCount,
                                    f.Vwap, f.TotalVolume)).ToList(),
            PredictedDirection: prediction?.Direction,
            Confidence:         prediction?.Confidence,
            Rationale:          prediction?.Rationale,
            ModelVersion:       prediction?.ModelVersion,
            AsOf:               DateTime.UtcNow);
    }
}

public sealed class GetMomentumHandler(
    IMomentumRepository momentumRepo,
    ILogger<GetMomentumHandler> logger)
    : IRequestHandler<GetMomentumQuery, MomentumDto>
{
    public async Task<MomentumDto> Handle(GetMomentumQuery request, CancellationToken ct)
    {
        var symbol = request.Symbol.ToUpperInvariant();
        logger.LogDebug("GetMomentum: {Symbol}", symbol);

        var data = await momentumRepo.GetAsync(symbol, request.Exchange, ct);
        return MomentumScorer.BuildDto(symbol, data);
    }
}

public sealed class GetKlinesHandler(
    IKlineRepository klineRepo,
    ILogger<GetKlinesHandler> logger)
    : IRequestHandler<GetKlinesQuery, IReadOnlyList<KlineDto>>
{
    public async Task<IReadOnlyList<KlineDto>> Handle(GetKlinesQuery request, CancellationToken ct)
    {
        var symbol = request.Symbol.ToUpperInvariant();
        logger.LogDebug("GetKlines: {Symbol} limit={Limit}", symbol, request.Limit);

        var klines = await klineRepo.GetRecentAsync(symbol, request.Limit, request.Exchange, ct);
        return klines.Select(k => new KlineDto(
            k.OpenTime, k.Open, k.High, k.Low, k.Close, k.Volume, k.NumTrades
        )).ToList();
    }
}

public sealed class GetVolumeAnalysisHandler(IVolumeRepository repo)
    : IRequestHandler<GetVolumeAnalysisQuery, VolumeAnalysisDto>
{
    public async Task<VolumeAnalysisDto> Handle(GetVolumeAnalysisQuery request, CancellationToken ct)
    {
        var symbol = request.Symbol.ToUpperInvariant();
        var raw    = await repo.GetWindowsAsync(symbol, request.Exchange, ct);
        return VolumeAnalysisMapper.BuildDto(symbol, raw);
    }
}

// ── Task tuple helper ─────────────────────────────────────────────────────────

file static class TaskExtensions
{
    public static async Task<(T1, T2)> WhenAll<T1, T2>(this (Task<T1> t1, Task<T2> t2) tasks)
    {
        await Task.WhenAll(tasks.t1, tasks.t2);
        return (tasks.t1.Result, tasks.t2.Result);
    }
}

// ── MediatR pipeline: validation behavior ─────────────────────────────────────

public sealed class ValidationBehavior<TRequest, TResponse>(
    IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct)
    {
        if (!validators.Any()) return await next();

        var ctx    = new ValidationContext<TRequest>(request);
        var errors = (await Task.WhenAll(validators.Select(v => v.ValidateAsync(ctx, ct))))
                     .SelectMany(r => r.Errors)
                     .Where(e => e is not null)
                     .ToList();

        if (errors.Count > 0)
            throw new ValidationException(errors);

        return await next();
    }
}

// ── User handlers ─────────────────────────────────────────────────────────────

public sealed class RegisterUserHandler(IUserRepository repo)
    : IRequestHandler<RegisterUserCommand, int>
{
    public Task<int> Handle(RegisterUserCommand request, CancellationToken ct)
        => repo.UpsertAsync(request.Name, request.DeviceId, ct);
}

public sealed class GetUserStatsHandler(IUserRepository repo)
    : IRequestHandler<GetUserStatsQuery, UserStatsDto>
{
    public async Task<UserStatsDto> Handle(GetUserStatsQuery request, CancellationToken ct)
    {
        var (total, todayActive, names) = await repo.GetStatsAsync(ct);
        return new UserStatsDto(total, todayActive, names);
    }
}

public sealed class GetRecentWhalesHandler(ITradeQueryRepository repo)
    : IRequestHandler<GetRecentWhalesQuery, IReadOnlyList<WhaleTradeDto>>
{
    public async Task<IReadOnlyList<WhaleTradeDto>> Handle(GetRecentWhalesQuery request, CancellationToken ct)
    {
        var data = await repo.GetLatestWhalesAsync(request.Symbol, request.Exchange, request.Limit, ct);
        return data.Select(x => new WhaleTradeDto(
            x.Symbol,
            x.Exchange,
            x.Price,
            x.QuoteQty,
            x.IsBuyerMaker,
            x.TradeTime
        )).ToList();
    }
}

// ── Alert Validator ──────────────────────────────────────────────────────────

public sealed class CreateAlertValidator : AbstractValidator<CreateAlertCommand>
{
    private static readonly HashSet<string> ValidConditions = new(StringComparer.OrdinalIgnoreCase)
        { "ABOVE", "BELOW" };

    public CreateAlertValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty()
            .Must(s => SupportedSymbols.All.Contains(s))
            .WithMessage(SupportedSymbols.ValidationMessage);
        RuleFor(x => x.Condition)
            .NotEmpty()
            .Must(c => ValidConditions.Contains(c))
            .WithMessage("Condition must be one of: ABOVE, BELOW");
        RuleFor(x => x.TargetPrice)
            .GreaterThan(0)
            .WithMessage("Target price must be positive");
    }
}

// ── Alert Handlers ──────────────────────────────────────────────────────────

public sealed class CreateAlertHandler(IAlertRepository repo)
    : IRequestHandler<CreateAlertCommand, PriceAlertDto>
{
    public async Task<PriceAlertDto> Handle(CreateAlertCommand request, CancellationToken ct)
    {
        var alert = await repo.CreateAsync(
            request.Symbol.ToUpperInvariant(),
            request.Condition.ToUpperInvariant(),
            request.TargetPrice,
            request.UserId,
            request.Note,
            ct);
        return alert;
    }
}

public sealed class GetAlertsHandler(IAlertRepository repo)
    : IRequestHandler<GetAlertsQuery, IReadOnlyList<PriceAlertDto>>
{
    public Task<IReadOnlyList<PriceAlertDto>> Handle(GetAlertsQuery request, CancellationToken ct)
        => repo.GetActiveAlertsAsync(request.Symbol, ct);
}

public sealed class GetAlertHistoryHandler(IAlertRepository repo)
    : IRequestHandler<GetAlertHistoryQuery, IReadOnlyList<AlertNotificationDto>>
{
    public Task<IReadOnlyList<AlertNotificationDto>> Handle(GetAlertHistoryQuery request, CancellationToken ct)
        => repo.GetNotificationsAsync(request.Symbol, request.Limit, ct);
}

public sealed class DeleteAlertHandler(IAlertRepository repo)
    : IRequestHandler<DeleteAlertCommand, bool>
{
    public Task<bool> Handle(DeleteAlertCommand request, CancellationToken ct)
        => repo.DeactivateAsync(request.Id, ct);
}

// ── MediatR pipeline: logging behavior ───────────────────────────────────────

public sealed class LoggingBehavior<TRequest, TResponse>(
    ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct)
    {
        var name = typeof(TRequest).Name;
        logger.LogDebug("Handling {Request}", name);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await next();
        logger.LogInformation("{Request} handled in {Elapsed}ms", name, sw.ElapsedMilliseconds);
        return response;
    }
}
