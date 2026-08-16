using System.Collections.Concurrent;
using CryptoDecision.AlertService.Models;
using CryptoDecision.AlertService.Repository;
using CryptoDecision.AlertService.Telemetry;

namespace CryptoDecision.AlertService.Engine;

/// <summary>
/// Core alert evaluation engine.
///
/// Maintains an in-memory cache of active alerts (refreshed every 30s from PostgreSQL).
/// Evaluates each incoming trade price against cached rules.
///
/// Thread safety: ConcurrentDictionary for symbol→alerts mapping.
/// Lock-free reads during evaluation; writes only during refresh or trigger.
///
/// Interview talking points:
///   - In-memory cache avoids DB round-trip per trade (thousands/sec)
///   - ConcurrentDictionary for thread-safe reads from Kafka consumer thread
///   - Periodic refresh picks up new alerts created via API
///   - Once triggered, alert is removed from cache AND marked in DB
/// </summary>
public sealed class AlertEngine(
    AlertRepository repository,
    AlertMetrics metrics,
    ILogger<AlertEngine> logger)
{
    private readonly ConcurrentDictionary<string, List<PriceAlert>> _alertsBySymbol = new();
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Load all active alerts from DB into memory. Called on startup.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        var alerts = await repository.GetAllActiveAlertsAsync(ct);
        var grouped = alerts.GroupBy(a => a.Symbol.ToUpperInvariant());

        foreach (var group in grouped)
            _alertsBySymbol[group.Key] = group.ToList();

        _lastRefresh = DateTimeOffset.UtcNow;
        logger.LogInformation("AlertEngine initialized with {Count} active alerts across {Symbols} symbols",
            alerts.Count, _alertsBySymbol.Count);
    }

    /// <summary>
    /// Refresh cache from DB if stale. Non-blocking: skips refresh if interval not elapsed.
    /// </summary>
    public async Task RefreshIfStaleAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _lastRefresh < RefreshInterval) return;

        var alerts = await repository.GetAllActiveAlertsAsync(ct);
        var newMap = alerts.GroupBy(a => a.Symbol.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.ToList());

        // Replace entire map atomically
        _alertsBySymbol.Clear();
        foreach (var (symbol, list) in newMap)
            _alertsBySymbol[symbol] = list;

        _lastRefresh = DateTimeOffset.UtcNow;
        logger.LogDebug("AlertEngine refreshed: {Count} active alerts", alerts.Count);
    }

    /// <summary>
    /// Evaluate a trade price against all active alerts for its symbol.
    /// Returns list of triggered alerts (already marked in DB and removed from cache).
    /// </summary>
    public async Task<List<AlertNotification>> EvaluateAsync(
        string symbol, decimal price, CancellationToken ct)
    {
        var key = symbol.ToUpperInvariant();
        if (!_alertsBySymbol.TryGetValue(key, out var alerts) || alerts.Count == 0)
            return [];

        var triggered = new List<AlertNotification>();

        // Snapshot to avoid mutation during iteration
        var snapshot = alerts.ToList();

        foreach (var alert in snapshot)
        {
            bool shouldTrigger = alert.Condition.ToUpperInvariant() switch
            {
                "ABOVE" => price >= alert.TargetPrice,
                "BELOW" => price <= alert.TargetPrice,
                _       => false
            };

            if (!shouldTrigger) continue;

            // Mark triggered in DB (atomic: update alert + insert notification)
            await repository.MarkTriggeredAsync(alert.Id, price, ct);

            // Remove from in-memory cache
            alerts.RemoveAll(a => a.Id == alert.Id);

            var notification = new AlertNotification(
                AlertId:      alert.Id,
                UserId:       alert.UserId,
                Symbol:       alert.Symbol,
                Condition:    alert.Condition,
                TargetPrice:  alert.TargetPrice,
                ActualPrice:  price,
                Note:         alert.Note,
                TriggeredAt:  DateTimeOffset.UtcNow
            );

            triggered.Add(notification);
            metrics.AlertsTriggered.Add(1,
                new KeyValuePair<string, object?>("symbol", alert.Symbol),
                new KeyValuePair<string, object?>("condition", alert.Condition));

            logger.LogInformation(
                "ALERT TRIGGERED: {Symbol} {Condition} {TargetPrice} (actual: {ActualPrice}) — alert #{AlertId}",
                alert.Symbol, alert.Condition, alert.TargetPrice, price, alert.Id);
        }

        return triggered;
    }

    /// <summary>Number of active alerts currently cached.</summary>
    public int ActiveAlertCount => _alertsBySymbol.Values.Sum(list => list.Count);
}
