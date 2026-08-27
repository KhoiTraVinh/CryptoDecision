using CryptoDecision.ApiService.Application;
using Microsoft.AspNetCore.SignalR;

namespace CryptoDecision.ApiService.Infrastructure.Hubs;

// ── Strongly-typed client interface ──────────────────────────────────────────

/// <summary>
/// Everything pushed to connected clients. This list is kept in step with what the
/// dashboard actually renders — a broadcast nobody listens to is a database query
/// on a timer for no one.
/// </summary>
public interface IMarketClient
{
    /// <summary>Pushed every 30 s with volume analysis for the subscribed symbol.</summary>
    Task ReceiveVolumeAnalysis(VolumeAnalysisDto data);

    /// <summary>Pushed whenever new whale trades are detected.</summary>
    Task ReceiveWhaleAlert(WhaleAlertDto data);

    /// <summary>Pushed every 30s with today's feature metrics for the symbol.</summary>
    Task ReceiveMarketStatus(MarketStatusDto data);

    /// <summary>Pushed every 15s with trading bot status.</summary>
    Task ReceiveBotStatus(dynamic data);
}

// ── Hub ───────────────────────────────────────────────────────────────────────

/// <summary>
/// WebSocket hub for real-time market data.
/// Clients subscribe to symbol groups to receive pushed updates.
/// </summary>
public sealed class MarketHub : Hub<IMarketClient>
{
    /// <summary>Subscribe to momentum pushes for <paramref name="symbol"/> and <paramref name="exchange"/>.</summary>
    public Task Subscribe(string symbol, string exchange = "BINANCE") =>
        Groups.AddToGroupAsync(Context.ConnectionId, GroupName(symbol, exchange));

    /// <summary>Unsubscribe from momentum pushes for <paramref name="symbol"/>.</summary>
    public Task Unsubscribe(string symbol, string exchange = "BINANCE") =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(symbol, exchange));

    /// <summary>Subscribe to volume analysis pushes for <paramref name="symbol"/>.</summary>
    public Task SubscribeVolume(string symbol, string exchange = "BINANCE") =>
        Groups.AddToGroupAsync(Context.ConnectionId, VolumeGroupName(symbol, exchange));

    /// <summary>Unsubscribe from volume analysis pushes for <paramref name="symbol"/>.</summary>
    public Task UnsubscribeVolume(string symbol, string exchange = "BINANCE") =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, VolumeGroupName(symbol, exchange));

    /// <summary>Canonical group name for momentum updates.</summary>
    public static string GroupName(string symbol, string exchange = "BINANCE") =>
        $"momentum_{(exchange ?? "BINANCE").ToUpperInvariant()}_{(symbol ?? "UNKNOWN").ToUpperInvariant()}";

    /// <summary>Canonical group name for volume analysis updates.</summary>
    public static string VolumeGroupName(string symbol, string exchange = "BINANCE") =>
        $"volume_{(exchange ?? "BINANCE").ToUpperInvariant()}_{(symbol ?? "UNKNOWN").ToUpperInvariant()}";
}
