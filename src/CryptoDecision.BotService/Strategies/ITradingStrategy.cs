using CryptoDecision.BotService.Bot;
using CryptoDecision.Shared.Bot;

namespace CryptoDecision.BotService.Strategies;

/// <summary>
/// Strategy Pattern: each trading strategy implements this interface.
/// Adding a new strategy = one new class + DI registration. Zero existing code modified (OCP).
/// </summary>
public interface ITradingStrategy
{
    /// <summary>Strategy identifier matching BotOptions.ActiveStrategies values.</summary>
    string Name { get; }

    /// <summary>Evaluate whether to open a new position.</summary>
    Task<EntryDecision> EvaluateEntryAsync(StrategyContext ctx, CancellationToken ct);

    /// <summary>Evaluate whether to close an existing position.</summary>
    /// <remarks>
    /// Asynchronous because an exit may now consult live market data rather than only
    /// the trade's own stored levels — the flow-reversal exit reads flow_bars_15m. It
    /// was synchronous while every exit was pure arithmetic on the row; making the I/O
    /// explicit here is better than a strategy blocking on a database read inside what
    /// the signature promises is a pure function.
    /// </remarks>
    Task<ExitDecision> EvaluateExitAsync(
        BotTrade trade, decimal currentPrice, BotOptions opts, CancellationToken ct);
}

/// <summary>
/// Immutable context passed to strategy entry evaluation.
/// Decouples strategies from infrastructure concerns.
/// </summary>
public sealed record StrategyContext(
    BotOptions               Options,
    IReadOnlyList<BotTrade>  OpenTrades,
    decimal                  CurrentPrice
);
