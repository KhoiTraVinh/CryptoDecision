using System.Collections.Concurrent;

namespace CryptoDecision.IngestionService.Telemetry;

/// <summary>
/// When each exchange feed last said anything, and when it last said anything that
/// mattered.
///
/// Why this exists
/// ---------------
/// The only health check this service had measured channel DEPTH, which detects one
/// failure — Kafka falling behind the WebSocket — and reports the opposite of the truth
/// for the failure that actually happens. A dead feed produces an EMPTY channel, so a
/// service ingesting nothing at all reported Healthy, forever.
///
/// That is not a hypothetical reading of the code. MarketSubscriptionSettings.Validate
/// carries a comment naming this exact scenario — "the channel health check would report
/// healthy because the channels are empty rather than backed up" — written when an empty
/// subscription list was the cause. The guard added then was for the configuration; the
/// same hole stayed open for every other way a feed dies: a half-open socket, a silently
/// rejected subscription, a venue delisting the pair.
///
/// Downstream, the bot abstains on FLOW_BARS_STALE and reports RUNNING while it does so,
/// so a stopped feed and a quiet market look identical from every surface. This is the
/// one place that can tell them apart.
///
/// Two clocks, not one
/// -------------------
/// <b>Frames</b> is any traffic at all, pongs included. It answers "is this socket
/// alive", and it is what the read watchdog uses to decide a connection has gone
/// half-open — the case where TCP is up, the exchange is gone, and ReceiveAsync blocks
/// forever with no exception to reconnect on.
///
/// <b>Data</b> is a trade or a kline actually reaching a channel. It answers "is this
/// feed useful", which is a different question with a different failure: a connection
/// that answers every ping and was never subscribed to anything is perfectly alive and
/// completely useless. Collapsing the two would hide precisely that.
/// </summary>
public sealed class FeedLiveness
{
    private sealed class FeedState
    {
        public long ConnectedAtTicks;
        public long LastFrameAtTicks;
        public long LastDataAtTicks;
        public long Connections;
    }

    private readonly ConcurrentDictionary<string, FeedState> _feeds =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Declare that a feed is supposed to exist, before it has produced anything.
    ///
    /// Without this an exchange that never connects has no entry at all, and a health
    /// check that iterates entries would pass it by silence — the same shape of bug as
    /// the empty-channel one, one level up.
    /// </summary>
    public void Expect(string exchange) => _feeds.GetOrAdd(exchange, _ => new FeedState());

    public void RecordConnected(string exchange)
    {
        var s = _feeds.GetOrAdd(exchange, _ => new FeedState());
        Interlocked.Exchange(ref s.ConnectedAtTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref s.LastFrameAtTicks, DateTime.UtcNow.Ticks);
        Interlocked.Increment(ref s.Connections);
    }

    /// <summary>Any frame from the venue, control messages included.</summary>
    public void RecordFrame(string exchange)
    {
        var s = _feeds.GetOrAdd(exchange, _ => new FeedState());
        Interlocked.Exchange(ref s.LastFrameAtTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>A trade or kline that reached a channel.</summary>
    public void RecordData(string exchange)
    {
        var s = _feeds.GetOrAdd(exchange, _ => new FeedState());
        Interlocked.Exchange(ref s.LastDataAtTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// How long since this feed last delivered a frame, or null when it has never
    /// connected. Read by the socket watchdog.
    /// </summary>
    public TimeSpan? SinceLastFrame(string exchange)
    {
        if (!_feeds.TryGetValue(exchange, out var s)) return null;

        var ticks = Interlocked.Read(ref s.LastFrameAtTicks);
        return ticks == 0 ? null : DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
    }

    public IReadOnlyList<FeedSnapshot> Snapshot()
    {
        var now = DateTime.UtcNow;

        return _feeds.Select(kv =>
        {
            var s = kv.Value;

            return new FeedSnapshot(
                Exchange:       kv.Key,
                SinceConnected: Elapsed(Interlocked.Read(ref s.ConnectedAtTicks), now),
                SinceFrame:     Elapsed(Interlocked.Read(ref s.LastFrameAtTicks), now),
                SinceData:      Elapsed(Interlocked.Read(ref s.LastDataAtTicks), now),
                Connections:    Interlocked.Read(ref s.Connections));
        }).ToList();

        static TimeSpan? Elapsed(long ticks, DateTime now) =>
            ticks == 0 ? null : now - new DateTime(ticks, DateTimeKind.Utc);
    }
}

/// <param name="SinceData">
/// Null means this feed has never produced a trade or a kline. Not the same as "it has
/// been quiet lately", and a health check must not treat it as such: a feed that has
/// delivered nothing since the process started is the failure, not an absence of one.
/// </param>
public sealed record FeedSnapshot(
    string    Exchange,
    TimeSpan? SinceConnected,
    TimeSpan? SinceFrame,
    TimeSpan? SinceData,
    long      Connections);
