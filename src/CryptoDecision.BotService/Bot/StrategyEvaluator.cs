using System.Net.Http.Json;
using CryptoDecision.BotService.Strategies;
using CryptoDecision.Shared.Bot;

namespace CryptoDecision.BotService.Bot;

/// <summary>
/// Thin coordinator that resolves ITradingStrategy by name from DI.
/// No strategy logic lives here — each strategy is a separate class (Strategy Pattern / OCP).
/// Also provides live price from Binance REST.
/// </summary>
public sealed class StrategyEvaluator
{
    private readonly IReadOnlyDictionary<string, ITradingStrategy> _strategies;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<StrategyEvaluator> _log;

    public StrategyEvaluator(
        IEnumerable<ITradingStrategy> strategies,
        IHttpClientFactory            httpFactory,
        ILogger<StrategyEvaluator>    log)
    {
        // Build a lookup dictionary keyed by strategy name for O(1) resolution
        _strategies = strategies.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        _httpFactory = httpFactory;
        _log = log;

        _log.LogInformation("[StrategyEvaluator] Registered strategies: [{Names}]",
            string.Join(", ", _strategies.Keys));
    }

    // ── Live price from Binance public REST ───────────────────────────────────

    public async Task<decimal?> GetCurrentPriceAsync(string symbol, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient("binance-public");
            var resp = await http.GetFromJsonAsync<BinancePriceTicker>(
                $"/api/v3/ticker/price?symbol={symbol}", ct);
            return resp?.Price;
        }
        catch (Exception ex)
        {
            _log.LogWarning("[Bot] GetCurrentPrice failed: {Err}", ex.Message);
            return null;
        }
    }

    // ── Entry Evaluation (delegates to resolved strategy) ─────────────────────

    public async Task<EntryDecision> ShouldEnterAsync(
        string strategy,
        BotOptions opts,
        IReadOnlyList<BotTrade> openTrades,
        decimal currentPrice,
        CancellationToken ct)
    {
        if (!_strategies.TryGetValue(strategy, out var impl))
        {
            _log.LogWarning("[StrategyEvaluator] Unknown strategy '{Strategy}', skipping", strategy);
            return new EntryDecision(false);
        }

        var ctx = new StrategyContext(opts, openTrades, currentPrice);
        return await impl.EvaluateEntryAsync(ctx, ct);
    }

    // ── Exit Evaluation (delegates to resolved strategy) ──────────────────────

    public ExitDecision EvaluateExit(BotTrade trade, decimal currentPrice, BotOptions opts)
    {
        var rawChange = (currentPrice - trade.EntryPrice) / trade.EntryPrice;
        var changePct = trade.Side == "SHORT" ? -rawChange : rawChange;
        var held = DateTime.UtcNow - trade.OpenedAt;

        // Timeout is universal across all strategies
        if (held.TotalMinutes >= opts.MaxHoldMinutes)
            return new ExitDecision(true, "TIMEOUT", currentPrice, changePct);

        // ── Breakeven stop (universal) ──────────────────────────────────────
        // Once trade reaches BreakevenTriggerPct profit, treat entry price as floor.
        // If price retraces back to entry → close at breakeven (risk-free exit).
        if (opts.UseBreakevenStop && changePct < 0m && trade.PeakPrice.HasValue)
        {
            var peakChange = trade.Side == "SHORT"
                ? -(trade.PeakPrice.Value - trade.EntryPrice) / trade.EntryPrice
                : (trade.PeakPrice.Value - trade.EntryPrice) / trade.EntryPrice;

            // Peak was above breakeven trigger, but current price reverted to entry
            if (peakChange >= opts.BreakevenTriggerPct)
            {
                _log.LogInformation(
                    "[Breakeven] Trade {Id} peak was +{Peak:P2} but now {Cur:P2} → closing at breakeven",
                    trade.Id, peakChange, changePct);
                return new ExitDecision(true, "BREAKEVEN", currentPrice, changePct);
            }
        }

        if (!_strategies.TryGetValue(trade.Strategy, out var impl))
            return new ExitDecision(false, null, currentPrice, changePct);

        // ── Dynamic TP/SL scaling (universal) ───────────────────────────────
        // Adjust thresholds based on volatility: high vol → wider TP/SL
        var effectiveOpts = opts;
        if (opts.UseDynamicTpSl && trade.PeakPrice.HasValue)
        {
            // Use peak-to-entry ratio as a volatility proxy for the trade
            var volatilityFactor = Math.Abs(
                (trade.PeakPrice.Value - trade.EntryPrice) / trade.EntryPrice);
            var scale = Math.Max(1m, 1m + volatilityFactor * 10m); // scale 1x-2x
            scale = Math.Min(scale, 2m);

            effectiveOpts = new BotOptions
            {
                TakeProfitPct  = opts.TakeProfitPct * scale,
                StopLossPct    = opts.StopLossPct * scale,
                UseTrailingStop = opts.UseTrailingStop,
                TrailingStopPct = opts.TrailingStopPct * scale,
                UseBreakevenStop    = opts.UseBreakevenStop,
                BreakevenTriggerPct = opts.BreakevenTriggerPct,
            };
        }

        return impl.EvaluateExit(trade, currentPrice, effectiveOpts);
    }
}

public sealed record ExitDecision(bool ShouldExit, string? Reason, decimal CurrentPrice, decimal ChangePct);

file sealed record BinancePriceTicker(string Symbol, decimal Price);
