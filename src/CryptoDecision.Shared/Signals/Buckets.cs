namespace CryptoDecision.Shared.Signals;

/// <summary>
/// The 15-minute grid, in one place.
///
/// This existed five times. `FlowBarRepository.FloorTo15Minutes`,
/// `FlowBarAggregationWorker.FloorTo15Minutes`, two inline copies inside
/// <see cref="CrossVenueFlowScorer"/> and `SignalOutcomeRepository.BucketOf` all computed
/// <c>Ticks - Ticks % 15min</c> independently, across three projects.
///
/// The grid is not a storage detail. It is the identity of a decision — the gate's decline
/// cache and `signal_outcomes` both key on it, and the comment in TradingBotService that
/// consolidated two of those copies says why: "the shape of a drift nobody would notice
/// until the gate cache and the signal table disagreed about which bucket a decision
/// belonged to." It is also the aggregation boundary the worker writes on, and the test for
/// which bucket is still filling and must not be read.
///
/// That consolidation covered two of five. This is the other three.
/// </summary>
public static class Buckets
{
    /// <summary>Width of one flow bucket. The signal grid, not a tunable.</summary>
    public static readonly TimeSpan Width = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Floor a timestamp onto the grid.
    ///
    /// Ticks arithmetic rather than minute subtraction, so seconds and sub-second
    /// precision are dropped too. Always returns UTC kind: every caller is working in UTC
    /// and an Unspecified kind here has reached Npgsql as a local timestamp before.
    /// </summary>
    public static DateTime FloorUtc(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % Width.Ticks, DateTimeKind.Utc);

    /// <summary>
    /// The bucket currently filling. Anything at or after this is partial and must not be
    /// scored — the aggregation worker rewrites it every two minutes, so whatever it says
    /// now will change. Reading a live bucket turned a +0.17 OFI into -0.081 an hour later,
    /// and the 16:30 bar on 2026-09-08 showed +0.92% at minute ten and closed at +0.48%.
    /// </summary>
    public static DateTime OpenBucketUtc(DateTime nowUtc) => FloorUtc(nowUtc);
}
