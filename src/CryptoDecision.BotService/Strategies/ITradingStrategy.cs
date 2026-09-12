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

    /// <summary>
    /// The rule this instance will actually apply, in one sentence, with its live
    /// thresholds substituted in. Logged once at startup.
    ///
    /// On the interface rather than in Program.cs, which is where it used to live as a
    /// switch over EntryMode — and that switch had no FlowRatio arm, so it fell to its
    /// catch-all and announced the ZScore rule while FlowRatio traded. The banner exists
    /// precisely to catch a configured mode differing from the running one, and it was
    /// producing that confusion instead of catching it. Third time this codebase has been
    /// bitten by a switch whose default silently absorbs a new case.
    ///
    /// Keeping it next to the code it describes is what makes it maintainable: a new
    /// entry mode cannot be added without the compiler pointing at this method.
    /// </summary>
    string DescribeRule();

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
