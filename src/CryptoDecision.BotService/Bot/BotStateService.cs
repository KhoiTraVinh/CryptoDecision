using CryptoDecision.Shared.Bot;

namespace CryptoDecision.BotService.Bot;

/// <summary>
/// Singleton state container for the trading bot.
/// Holds runtime status, open trades collection and P&L aggregates.
/// </summary>
public sealed class BotStateService
{
    private readonly object _lock = new();

    private bool             _isRunning    = false;
    private List<BotTrade>   _openTrades   = new();
    private DateTime?        _lastEvalAt   = null;
    private DateTime?        _lastClosedAt = null;
    private DateTime?        _runningSince = null;
    private Dictionary<string, DateTime> _lastEntryAtByStrategy = new();

    // Aggregate stats (in-memory, reconciled from DB on start)
    private decimal _totalPnlUsd = 0m;
    private int     _totalTrades = 0;
    private int     _winCount    = 0;
    private int     _lossCount   = 0;

    public BotOptions Options { get; private set; } = new();

    // ── Control ───────────────────────────────────────────────────────────────

    public void Start(BotOptions opts)
    {
        lock (_lock)
        {
            // Stamped only on the transition into running. The worker calls Start on
            // every cycle to pick up configuration changes, so stamping it
            // unconditionally would push the clock forward forever — and a loop that
            // hung before completing its very first cycle would look freshly started
            // for as long as it stayed hung.
            if (!_isRunning) _runningSince = DateTime.UtcNow;

            _isRunning = true;
            Options    = opts;
        }
    }

    public void Stop()
    {
        lock (_lock) { _isRunning = false; _runningSince = null; }
    }

    public bool IsRunning { get { lock (_lock) return _isRunning; } }

    /// <summary>When the bot last went from stopped to running, or null if stopped.</summary>
    public DateTime? RunningSince { get { lock (_lock) return _runningSince; } }

    // ── Open trades ───────────────────────────────────────────────────────────

    public IReadOnlyList<BotTrade> GetOpenTrades()
    {
        lock (_lock) return _openTrades.ToList();
    }

    public void AddOpenTrade(BotTrade t)
    {
        lock (_lock) _openTrades.Add(t);
    }

    /// <summary>
    /// Take over positions that were already open — on startup, after a restart.
    ///
    /// Without this a restarted worker starts with an empty position list while the
    /// database still says OPEN: nothing evaluates those trades' stops, take
    /// profits or timeouts ever again. In paper mode that is a leaked row. On a
    /// live account it is real coins with nobody watching them.
    ///
    /// The per-strategy cooldown is seeded from the recovered entries too, so a
    /// restart cannot be used — accidentally, in a crash loop — to bypass entry
    /// pacing and stack positions.
    /// </summary>
    public void SeedOpenTrades(IReadOnlyList<BotTrade> openTrades)
    {
        lock (_lock)
        {
            _openTrades = openTrades.ToList();

            _lastEntryAtByStrategy = openTrades
                .GroupBy(t => t.Strategy)
                .ToDictionary(g => g.Key, g => g.Max(t => t.OpenedAt));
        }
    }

    public void RemoveOpenTrade(long id)
    {
        lock (_lock)
        {
            var t = _openTrades.FirstOrDefault(x => x.Id == id);
            if (t != null) _openTrades.Remove(t);
        }
    }

    // ── Eval & Cooldown ───────────────────────────────────────────────────────

    /// <summary>
    /// Stamp the start of an evaluation cycle and report the gap since the previous
    /// one, or null on the first cycle.
    ///
    /// The gap is returned rather than merely recorded because it is the only signal
    /// available that the wall clock moved when it should not have. On 2026-08-22 two
    /// live positions opened 13 and 16 minutes earlier were both closed in the same
    /// instant with reason TIMEOUT, against a max_hold_minutes of 1440 — a 24-hour
    /// threshold tripped by a quarter-hour-old trade. The only way
    /// <c>DateTime.UtcNow - OpenedAt</c> reaches 24 hours there is if UtcNow jumped,
    /// which is a documented behaviour of the WSL2 clock after the host suspends, and
    /// this host has had Docker killed by memory pressure more than once. It
    /// self-corrected on the next cycle, so nothing was left behind to find except
    /// two closed positions with a reason that was not true.
    ///
    /// Every other exit is a comparison against a price. The timeout is the one that
    /// trusts the clock alone, so the clock is the input that needs checking.
    /// </summary>
    public TimeSpan? TouchEval()
    {
        lock (_lock)
        {
            var previous = _lastEvalAt;
            _lastEvalAt  = DateTime.UtcNow;
            return previous is { } p ? _lastEvalAt.Value - p : null;
        }
    }

    public DateTime? LastEvalAt { get { lock (_lock) return _lastEvalAt; } }

    public void SetLastClosedAt(DateTime? dt) { lock (_lock) _lastClosedAt = dt; }
    public DateTime? LastClosedAt { get { lock (_lock) return _lastClosedAt; } }

    public void SetLastEntryAt(string strategy, DateTime dt) 
    { 
        lock (_lock) _lastEntryAtByStrategy[strategy] = dt; 
    }
    public DateTime? GetLastEntryAt(string strategy) 
    { 
        lock (_lock) 
            return _lastEntryAtByStrategy.TryGetValue(strategy, out var dt) ? dt : null; 
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    public void RecordClose(decimal pnlUsd)
    {
        lock (_lock)
        {
            _totalTrades++;
            _totalPnlUsd += pnlUsd;
            if (pnlUsd >= 0) _winCount++;
            else             _lossCount++;
        }
    }

    public void SeedStats(IReadOnlyList<BotTrade> history)
    {
        lock (_lock)
        {
            _totalPnlUsd = 0; _totalTrades = 0; _winCount = 0; _lossCount = 0;
            foreach (var t in history.Where(t => t.Status is "CLOSED" or "STOPPED"))
            {
                _totalTrades++;
                var p = t.PnlUsd ?? 0m;
                _totalPnlUsd += p;
                if (p >= 0) _winCount++; else _lossCount++;
            }
        }
    }

    public BotStatus GetStatus()
    {
        lock (_lock)
        {
            var cap = Options.CapitalUsd;

            // Only positions in the configured instrument are counted, because only
            // those are managed — EvalCycleAsync skips the rest, since one price per
            // cycle cannot evaluate a stop in a different instrument. Counting them
            // here would publish an open position to the dashboard that nothing is
            // watching, which reads as safer than it is.
            var openCount = _openTrades.Count(t =>
                string.Equals(t.Symbol, Options.Symbol, StringComparison.OrdinalIgnoreCase));
            
            return new BotStatus(
                IsRunning      : _isRunning,
                PaperMode      : Options.PaperMode,
                Symbol         : Options.Symbol,
                CapitalUsd     : cap,
                TotalPnlUsd    : _totalPnlUsd,
                TotalPnlPct    : cap > 0 ? _totalPnlUsd / cap : 0m,
                TotalTrades    : _totalTrades,
                WinCount       : _winCount,
                LossCount      : _lossCount,
                OpenTradeCount : openCount,
                LastEvalAt     : _lastEvalAt);
        }
    }
}
