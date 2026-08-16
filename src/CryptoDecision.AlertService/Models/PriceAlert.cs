namespace CryptoDecision.AlertService.Models;

/// <summary>
/// Represents a user-configured price alert rule.
/// When the market price crosses the target, the alert triggers once.
/// </summary>
public sealed record PriceAlert
{
    public long   Id             { get; init; }
    public string? UserId        { get; init; }
    public string Symbol         { get; init; } = "";
    public string Condition      { get; init; } = "";   // ABOVE, BELOW
    public decimal TargetPrice   { get; init; }
    public bool   IsActive       { get; init; } = true;
    public bool   IsTriggered    { get; init; }
    public DateTimeOffset? TriggeredAt    { get; init; }
    public decimal? TriggeredPrice { get; init; }
    public DateTimeOffset CreatedAt      { get; init; }
    public string? Note          { get; init; }
}

/// <summary>
/// Notification produced when a price alert fires.
/// Published to Kafka topic <c>alerts.notifications</c>.
/// </summary>
public sealed record AlertNotification(
    long   AlertId,
    string? UserId,
    string Symbol,
    string Condition,
    decimal TargetPrice,
    decimal ActualPrice,
    string? Note,
    DateTimeOffset TriggeredAt
);

/// <summary>
/// Trade message consumed from Kafka — minimal projection of the IngestionService TradeBatch.
/// Only the fields needed for price evaluation are deserialized.
/// </summary>
public sealed record TradeBatch(
    string Exchange,
    string Symbol,
    DateTimeOffset BatchTimestamp,
    IReadOnlyList<TradeItem> Trades
);

public sealed record TradeItem(
    string Symbol,
    decimal Price,
    decimal QuoteQty,
    DateTimeOffset TradeTime
);
