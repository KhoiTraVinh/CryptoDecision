using System.Diagnostics.Metrics;

namespace CryptoDecision.AlertService.Telemetry;

/// <summary>
/// Custom metrics for the AlertService, exposed via Prometheus.
/// </summary>
public sealed class AlertMetrics : IDisposable
{
    public static readonly string MeterName = "CryptoDecision.Alert";

    private readonly Meter _meter;

    /// <summary>Number of trades evaluated against alert rules.</summary>
    public readonly Counter<long> TradesEvaluated;

    /// <summary>Number of alerts that have been triggered.</summary>
    public readonly Counter<long> AlertsTriggered;

    /// <summary>Number of active alerts currently in cache.</summary>
    public readonly ObservableGauge<int> ActiveAlerts;

    private Func<int>? _activeAlertCountProvider;

    public AlertMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        TradesEvaluated = _meter.CreateCounter<long>(
            "alert_trades_evaluated_total",
            unit: "trades",
            description: "Total trades evaluated against alert rules");

        AlertsTriggered = _meter.CreateCounter<long>(
            "alert_triggered_total",
            unit: "alerts",
            description: "Total alerts triggered");

        ActiveAlerts = _meter.CreateObservableGauge(
            "alert_active_count",
            () => _activeAlertCountProvider?.Invoke() ?? 0,
            unit: "alerts",
            description: "Number of active alerts in cache");
    }

    /// <summary>Register the AlertEngine to provide active alert count.</summary>
    public void RegisterActiveAlertProvider(Func<int> provider) =>
        _activeAlertCountProvider = provider;

    public void Dispose() => _meter.Dispose();
}
