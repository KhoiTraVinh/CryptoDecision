using CryptoDecision.Shared.Bot;

namespace CryptoDecision.BotService.Agent;

/// <summary>
/// Per-turn state shared by every tool in one agent run.
///
/// The tools are registered as singletons so their schemas are built once, but the
/// facts they operate on — current price, which positions are open, which options
/// apply — change every cycle. Rather than threading those through every call
/// signature, TradingBotService loads this scratchpad before invoking the agent
/// and the tools read from it.
///
/// Not thread-safe by design: exactly one agent turn runs at a time, driven by the
/// single evaluation loop.
/// </summary>
public sealed class AgentContext
{
    /// <summary>Strategy tag written to bot_trades for agent-opened positions.</summary>
    public const string AgentStrategyName = "AI_AGENT";

    private readonly List<BotTrade> _openTrades = [];
    private readonly List<string>   _actions    = [];

    public BotOptions Options      { get; private set; } = new();
    public decimal    CurrentPrice { get; private set; }
    public DateTime?  LastEntryAt  { get; private set; }

    public IReadOnlyList<BotTrade> OpenTrades => _openTrades;

    /// <summary>Human-readable log of what the agent actually did this turn.</summary>
    public IReadOnlyList<string> Actions => _actions;

    public int OrdersPlaced { get; private set; }

    /// <summary>Reset for a new turn. Called once per evaluation cycle.</summary>
    public void BeginTurn(
        BotOptions opts, decimal currentPrice,
        IEnumerable<BotTrade> openTrades, DateTime? lastEntryAt)
    {
        Options      = opts;
        CurrentPrice = currentPrice;
        LastEntryAt  = lastEntryAt;
        OrdersPlaced = 0;

        _openTrades.Clear();
        _openTrades.AddRange(openTrades);
        _actions.Clear();
    }

    public void RecordEntry(BotTrade trade, string reason)
    {
        _openTrades.Add(trade);
        LastEntryAt = DateTime.UtcNow;
        OrdersPlaced++;
        _actions.Add($"OPEN {trade.Side} id={trade.Id} @ {trade.EntryPrice:F2} — {reason}");
    }

    public void RecordClose(BotTrade trade)
    {
        _openTrades.RemoveAll(t => t.Id == trade.Id);
        _actions.Add($"CLOSE id={trade.Id} pnl=${trade.PnlUsd ?? 0m:F2}");
    }

    /// <summary>Trades opened this turn, so the caller can register them with BotStateService.</summary>
    public IEnumerable<BotTrade> TradesOpenedThisTurn(IReadOnlyCollection<long> preExistingIds)
        => _openTrades.Where(t => !preExistingIds.Contains(t.Id));
}
