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
    ExitDecision EvaluateExit(BotTrade trade, decimal currentPrice, BotOptions opts);
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
