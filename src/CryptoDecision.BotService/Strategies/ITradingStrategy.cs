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

    /// <summary>
    /// The stop and target this strategy will actually place, or null when it expresses
    /// no geometry and the configured percentages are what will be used.
    ///
    /// Why the risk gate needs this, and did not have it
    /// -------------------------------------------------
    /// RiskEngine.Validate judged bot_config.take_profit_pct against
    /// bot_config.stop_loss_pct — 2.00% and 1.50%, a 1.19:1 needing a 45.7% win rate.
    /// Neither number decides anything: they are read only by the no-geometry fallback in
    /// EvaluateExitAsync, and every position this strategy opens carries geometry. What
    /// actually runs is a 2.00% stop floor against a 2.0x target, which is 4.00% and a
    /// different configuration entirely.
    ///
    /// So the startup gate was certifying a setup nobody trades — the same defect the
    /// three trailing-stop findings were deleted for, reappearing on the other side of
    /// the file. It is also the defect this repository has paid for most often: a check
    /// that reports on a control the bot does not have.
    ///
    /// This returns the FLOOR geometry — the stop at its narrowest and the target that
    /// goes with it. A live reading can only widen the stop, and under the ATR rule the
    /// target widens with it, so the reward:risk this reports is the one that holds. It
    /// is a statement about the configuration, not a forecast of any one trade.
    /// </summary>
    StrategyRiskProfile? DescribeRisk();

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
