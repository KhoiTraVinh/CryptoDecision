using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace CryptoDecision.Shared.Signals;

/// <summary>One actionable signal, as it was when the strategy proposed it.</summary>
public sealed record SignalRecord(
    string       Symbol,
    string       Side,
    string       Strategy,
    DateTime     BucketStart,
    DateTime     SignalAt,
    FlowVerdict  Flow,
    StopGeometry Geometry,
    decimal      SignalPrice,
    decimal      Confidence);

/// <summary>
/// A past signal similar to the one being judged, with what actually happened to it.
///
/// <see cref="Distance"/> is the weighted feature distance that selected it, carried
/// through so a reader can see how close "similar" actually was. A neighbourhood
/// that is empty is reported as empty rather than padded with distant cases.
/// </summary>
public sealed record SimilarCase(
    DateTime SignalAt,
    string   Side,
    double   AggregateZ,
    int      AgreeingVenues,
    int      ParticipatingVenues,
    double   DispersionBps,
    decimal  StopPct,
    string   Outcome,
    decimal  OutcomeR,
    int?     MinutesToOutcome,
    string?  GateDecision,
    double   Distance);

/// <summary>How much of the table is labelled, and over what span.</summary>
public sealed record OutcomeCoverage(int Decided, int Pending, DateTime? First, DateTime? Last)
{
    /// <summary>
    /// Below this, statistics from the table describe one market regime rather than
    /// the gate. SOL's flow signal fires roughly 15 times a day here, so 200 decided
    /// signals is about two weeks — long enough to have contained more than one kind
    /// of day, which is the actual requirement. The number is a floor, not a target.
    /// </summary>
    public const int MinimumForStatistics = 200;

    public bool Sufficient => Decided >= MinimumForStatistics
                              && First is not null && Last is not null
                              && (Last.Value - First.Value) >= TimeSpan.FromDays(7);
}

/// <summary>
/// Reads and writes signal_outcomes: the record of every signal the strategy made,
/// what the gate did with it, and what the market did next.
///
/// Shared rather than owned by one service because three callers need it and they
/// must not disagree about what a row means — the bot writes signals and gate
/// verdicts, the processor labels outcomes, and the gate reads neighbours back as
/// evidence. Every method here is safe to call twice: writes are idempotent on the
/// natural key, and labeling only ever moves a row from unresolved to resolved.
/// </summary>
public sealed class SignalOutcomeRepository(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// The 15-minute decision bucket a timestamp falls in. Duplicated nowhere: the
    /// gate's own decline cache keys on this same grid, and two definitions of "the
    /// same decision" would let a signal be recorded twice and gated once.
    /// </summary>
    public static DateTime BucketOf(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.FromMinutes(15).Ticks, DateTimeKind.Utc);

    // ── Write path (BotService) ───────────────────────────────────────────────

    /// <summary>
    /// Record an actionable signal, or return the id of the row already recorded for
    /// this bucket.
    ///
    /// The strategy re-proposes the same bucket every 30-second cycle until the next
    /// bar closes — 298 proposals for 15 real signals in the audited window — so the
    /// insert has to collapse those onto one row. ON CONFLICT DO NOTHING keeps the
    /// FIRST proposal, which is the one whose timestamp answers "how quickly could
    /// this have been entered".
    /// </summary>
    public async Task<long?> RecordSignalAsync(SignalRecord r, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO signal_outcomes (
                symbol, side, strategy, bucket_start, signal_at,
                aggregate_z, aggregate_ofi, agreeing_venues, participating_venues,
                excluded_venues, dispersion_bps, atr_pct, signal_price,
                stop_pct, target_pct, reward_risk, confidence, venue_votes)
            VALUES (
                @symbol, @side, @strategy, @bucket, @signalAt,
                @z, @ofi, @agree, @part,
                @excluded, @disp, @atr, @price,
                @stopPct, @targetPct, @rr, @conf, @votes)
            ON CONFLICT (symbol, side, bucket_start, strategy) DO NOTHING
            RETURNING id
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            var flow = r.Flow;
            var geo  = r.Geometry;

            cmd.Parameters.AddWithValue("symbol",   r.Symbol);
            cmd.Parameters.AddWithValue("side",     r.Side);
            cmd.Parameters.AddWithValue("strategy", r.Strategy);
            cmd.Parameters.AddWithValue("bucket",   r.BucketStart);
            cmd.Parameters.AddWithValue("signalAt", r.SignalAt);
            cmd.Parameters.AddWithValue("z",        (decimal)flow.AggregateZ);
            cmd.Parameters.AddWithValue("ofi",      (decimal)flow.AggregateOfi);
            cmd.Parameters.AddWithValue("agree",    (short)flow.AgreeingVenues);
            cmd.Parameters.AddWithValue("part",     (short)flow.ParticipatingVenues);
            // The count the gate is told, and the one it has twice claimed was above
            // zero when it was not. Stored so that claim is checkable after the fact.
            cmd.Parameters.AddWithValue("excluded", (short)(flow.Votes.Count - flow.ParticipatingVenues));
            cmd.Parameters.AddWithValue("disp",     (decimal)flow.DispersionBps);
            cmd.Parameters.AddWithValue("atr",      (decimal)geo.AtrPctUsed);
            cmd.Parameters.AddWithValue("price",    r.SignalPrice);
            cmd.Parameters.AddWithValue("stopPct",  geo.StopPct);
            cmd.Parameters.AddWithValue("targetPct", geo.TargetPct);
            cmd.Parameters.AddWithValue("rr",       geo.RewardRisk);
            cmd.Parameters.AddWithValue("conf",     r.Confidence);
            cmd.Parameters.Add(new NpgsqlParameter("votes", NpgsqlDbType.Jsonb)
            {
                Value = JsonSerializer.Serialize(flow.Votes.Select(v => new
                {
                    v.Exchange, v.Z, v.Ofi, v.VolumeUsd, v.TradeCount,
                    v.Concentration, v.Participated, v.Agreed, v.ExclusionReason,
                })),
            });

            if (await cmd.ExecuteScalarAsync(ct) is long inserted) return inserted;
        }

        // Conflict: the bucket is already recorded. Return the existing id so the
        // gate verdict still lands on it.
        const string existing = """
            SELECT id FROM signal_outcomes
            WHERE symbol = @symbol AND side = @side AND strategy = @strategy
              AND bucket_start = @bucket
            """;

        await using var find = new NpgsqlCommand(existing, conn);
        find.Parameters.AddWithValue("symbol",   r.Symbol);
        find.Parameters.AddWithValue("side",     r.Side);
        find.Parameters.AddWithValue("strategy", r.Strategy);
        find.Parameters.AddWithValue("bucket",   r.BucketStart);

        return await find.ExecuteScalarAsync(ct) as long?;
    }

    /// <summary>
    /// Attach the gate's verdict to a recorded signal.
    ///
    /// Overwrites rather than appends: the gate is asked once per bucket, and a
    /// second verdict for the same bucket means the cache was bypassed — in which
    /// case the newest answer is the one that decided whether an order was placed.
    /// </summary>
    public async Task StampGateAsync(
        long id, string decision, string reason, string model, int latencyMs,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE signal_outcomes
            SET gate_decision   = @decision,
                gate_reason     = @reason,
                gate_model      = @model,
                gate_latency_ms = @latency
            WHERE id = @id
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id",       id);
        cmd.Parameters.AddWithValue("decision", decision);
        cmd.Parameters.AddWithValue("reason",   reason.Length > 1000 ? reason[..1000] : reason);
        cmd.Parameters.AddWithValue("model",    model);
        cmd.Parameters.AddWithValue("latency",  latencyMs);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Link the signal to the trade it became, once the order actually filled.
    ///
    /// Only approved signals get one, which is what makes "approved but never
    /// placed" — a sizing refusal, a venue rejection — visible as its own state
    /// rather than looking like a refusal by the gate.
    /// </summary>
    public async Task AttachTradeAsync(long id, long botTradeId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(
            "UPDATE signal_outcomes SET bot_trade_id = @trade WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id",    id);
        cmd.Parameters.AddWithValue("trade", botTradeId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Label path (ProcessorService) ─────────────────────────────────────────

    /// <summary>
    /// Resolve every unlabelled signal whose horizon has passed, from the tick
    /// stream, in one statement.
    ///
    /// Set-based rather than row-by-row because the whole job is two ordered index
    /// probes per signal, and a loop would turn a second of SQL into a round trip per
    /// row. Idempotent by construction: it only touches rows that are unresolved, and
    /// a resolved row is never revisited unless <paramref name="labelVersion"/> is
    /// raised — which is how a corrected labeler re-labels history deliberately
    /// rather than by accident.
    ///
    /// The rules it applies, and why each exists:
    ///
    ///   • Entry is the first OKX print at or after the signal. The bot trades OKX,
    ///     so a fill on any other venue's price is a fill that could not have
    ///     happened.
    ///   • Stop and target are the strategy's own percentages, taken from the row.
    ///     Recomputing them from today's volatility would score the signal against a
    ///     trade nobody would have made.
    ///   • Both levels are searched independently and the earlier timestamp wins. A
    ///     candle-based check cannot resolve a bar that contains both, and resolving
    ///     it wrongly is how a backtest manufactures an edge — this repository has
    ///     paid for that once already.
    ///   • Nothing beyond <paramref name="horizonMinutes"/> counts. bot_config.
    ///     max_hold_minutes closes the position there, so a target reached after it
    ///     is a target the bot would never have been holding for. The audited window
    ///     contains a win that took 348 minutes; against a 720-minute limit it counts,
    ///     and against a 240-minute one it would not. The limit is passed in rather
    ///     than assumed so the two can never drift apart.
    ///   • A signal whose ticks have aged out of retention is EXPIRED, not TIMEOUT.
    ///     Unknowable and no-outcome are different facts and only one of them is
    ///     evidence.
    /// </summary>
    public async Task<int> LabelAsync(
        string symbol, int horizonMinutes, int labelVersion = 1, CancellationToken ct = default)
    {
        const string sql = """
            WITH bounds AS (
                SELECT min(trade_time) AS first_tick, max(trade_time) AS last_tick
                FROM trades WHERE symbol = @symbol AND exchange = 'OKX'
            ),
            candidates AS (
                SELECT s.id, s.side, s.signal_at, s.stop_pct, s.target_pct,
                       b.first_tick, b.last_tick
                FROM signal_outcomes s CROSS JOIN bounds b
                WHERE s.symbol = @symbol
                  AND (s.outcome IS NULL OR s.outcome = 'PENDING'
                       OR s.label_version < @version)
            ),
            entered AS (
                SELECT c.*,
                       (SELECT t.price FROM trades t
                        WHERE t.symbol = @symbol AND t.exchange = 'OKX'
                          AND t.trade_time >= c.signal_at
                        ORDER BY t.trade_time ASC LIMIT 1) AS entry_price
                FROM candidates c
            ),
            levelled AS (
                SELECT e.*,
                       CASE WHEN side = 'LONG' THEN entry_price * (1 - stop_pct)
                            ELSE entry_price * (1 + stop_pct) END AS stop_price,
                       CASE WHEN side = 'LONG' THEN entry_price * (1 + target_pct)
                            ELSE entry_price * (1 - target_pct) END AS target_price
                FROM entered e
            ),
            resolved AS (
                SELECT l.*,
                    (SELECT t.trade_time FROM trades t
                     WHERE t.symbol = @symbol AND t.exchange = 'OKX'
                       AND t.trade_time > l.signal_at
                       AND t.trade_time <= l.signal_at + make_interval(mins => @horizon)
                       AND ((l.side = 'LONG'  AND t.price <= l.stop_price)
                         OR (l.side = 'SHORT' AND t.price >= l.stop_price))
                     ORDER BY t.trade_time ASC LIMIT 1) AS stop_at,
                    (SELECT t.trade_time FROM trades t
                     WHERE t.symbol = @symbol AND t.exchange = 'OKX'
                       AND t.trade_time > l.signal_at
                       AND t.trade_time <= l.signal_at + make_interval(mins => @horizon)
                       AND ((l.side = 'LONG'  AND t.price >= l.target_price)
                         OR (l.side = 'SHORT' AND t.price <= l.target_price))
                     ORDER BY t.trade_time ASC LIMIT 1) AS target_at,
                    -- Where the position stood when the clock ran out, for the R of a
                    -- timeout. A timeout is not zero: it is whatever the trade was
                    -- worth when the hold limit closed it.
                    (SELECT t.price FROM trades t
                     WHERE t.symbol = @symbol AND t.exchange = 'OKX'
                       AND t.trade_time <= l.signal_at + make_interval(mins => @horizon)
                     ORDER BY t.trade_time DESC LIMIT 1) AS horizon_price
                FROM levelled l
            ),
            verdict AS (
                SELECT r.*,
                    CASE
                        WHEN r.entry_price IS NULL AND r.signal_at < r.first_tick THEN 'EXPIRED'
                        WHEN r.entry_price IS NULL                                THEN 'NO_TICKS'
                        WHEN r.target_at IS NOT NULL
                             AND (r.stop_at IS NULL OR r.target_at < r.stop_at)    THEN 'WIN'
                        WHEN r.stop_at IS NOT NULL                                 THEN 'LOSS'
                        WHEN r.last_tick < r.signal_at + make_interval(mins => @horizon)
                                                                                   THEN 'PENDING'
                        ELSE 'TIMEOUT'
                    END AS result
                FROM resolved r
            )
            UPDATE signal_outcomes s
            SET entry_price        = v.entry_price,
                stop_price         = v.stop_price,
                target_price       = v.target_price,
                stop_hit_at        = v.stop_at,
                target_hit_at      = v.target_at,
                outcome            = v.result,
                outcome_r          = CASE v.result
                    WHEN 'WIN'  THEN  ROUND(v.target_pct / NULLIF(v.stop_pct, 0), 4)
                    WHEN 'LOSS' THEN -1.0
                    WHEN 'TIMEOUT' THEN ROUND(
                        CASE WHEN v.side = 'LONG'
                             THEN (v.horizon_price - v.entry_price) / NULLIF(v.entry_price, 0)
                             ELSE (v.entry_price - v.horizon_price) / NULLIF(v.entry_price, 0)
                        END / NULLIF(v.stop_pct, 0), 4)
                    ELSE NULL END,
                minutes_to_outcome = CASE
                    WHEN v.result IN ('WIN', 'LOSS')
                    THEN CEIL(EXTRACT(epoch FROM (
                        LEAST(COALESCE(v.stop_at, 'infinity'::timestamptz),
                              COALESCE(v.target_at, 'infinity'::timestamptz)) - v.signal_at)) / 60)
                    ELSE NULL END,
                horizon_minutes    = @horizon,
                label_version      = @version,
                labeled_at         = NOW()
            FROM verdict v
            WHERE s.id = v.id
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol",  symbol);
        cmd.Parameters.AddWithValue("horizon", horizonMinutes);
        cmd.Parameters.AddWithValue("version", (short)labelVersion);
        // Two ordered index probes per unresolved signal against a table of millions
        // of ticks. Bounded generously rather than left at the default, because a
        // backfill's first pass is the one that has thousands of rows to resolve.
        cmd.CommandTimeout = 300;

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Read path (the gate, and the report) ──────────────────────────────────

    /// <summary>
    /// The k most similar past signals that had already RESOLVED before this one
    /// fired.
    ///
    /// The time bound is the whole safety property. "Resolved before signal_at" — not
    /// "recorded before" — is what keeps the gate from being shown an outcome that
    /// had not happened yet at the moment it is being asked to decide. Without it,
    /// every backtest of this retrieval would be reading tomorrow's newspaper, and it
    /// would look excellent right up until it ran live.
    ///
    /// Distance is a weighted Euclidean distance over four features, each divided by
    /// a scale that makes one unit mean roughly "one meaningful step":
    ///
    ///     |z|              / 1.0   — the entry threshold is 1.0, so one unit is the
    ///                                distance between abstaining and acting
    ///     agreement ratio  / 0.33  — one venue out of three
    ///     dispersion       / 5 bps — the observed spread of actionable signals is
    ///                                2-13 bps, so 5 bps is a genuine step, not noise
    ///     stop width       / 0.4%  — stops in the audited window ran 0.55-1.42%
    ///
    /// Deliberately not a cosine similarity over a learned embedding: four features
    /// with stated scales can be argued with, and a reviewer can see why a case was
    /// selected. Nothing here is worth the opacity of a vector nobody can read.
    ///
    /// Outcomes are NOT balanced across wins and losses. The base rate is the single
    /// most useful thing the neighbourhood has to say — if eleven of twelve similar
    /// setups lost, the gate should see eleven losses.
    /// </summary>
    public async Task<IReadOnlyList<SimilarCase>> FindSimilarAsync(
        string symbol, string side, double aggregateZ, int agreeingVenues,
        int participatingVenues, double dispersionBps, decimal stopPct,
        DateTime asOfUtc, int k = 5, CancellationToken ct = default)
    {
        const string sql = """
            SELECT signal_at, side, aggregate_z, agreeing_venues, participating_venues,
                   dispersion_bps, stop_pct, outcome, outcome_r, minutes_to_outcome,
                   gate_decision,
                   sqrt(
                       pow((abs(aggregate_z) - @absZ) / 1.0, 2) +
                       pow((agreeing_venues::float / NULLIF(participating_venues, 0)
                            - @agreeRatio) / 0.33, 2) +
                       pow((COALESCE(dispersion_bps, 0) - @disp) / 5.0, 2) +
                       pow((stop_pct - @stopPct) / 0.004, 2)
                   ) AS distance
            FROM signal_outcomes
            WHERE symbol = @symbol
              AND side = @side
              AND outcome IN ('WIN', 'LOSS', 'TIMEOUT')
              -- Resolved strictly before the signal being judged. Both halves are
              -- needed: labeled_at guards against reading a row the labeler wrote
              -- after the fact, and the stop/target timestamps guard against a case
              -- whose own outcome landed after this signal fired.
              AND signal_at < @asOf
              AND COALESCE(GREATEST(stop_hit_at, target_hit_at),
                           signal_at + make_interval(mins => COALESCE(horizon_minutes, 720))) < @asOf
            ORDER BY distance ASC, signal_at DESC
            LIMIT @k
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol",     symbol);
        cmd.Parameters.AddWithValue("side",       side);
        cmd.Parameters.AddWithValue("absZ",       Math.Abs(aggregateZ));
        cmd.Parameters.AddWithValue("agreeRatio",
            participatingVenues > 0 ? (double)agreeingVenues / participatingVenues : 0.0);
        cmd.Parameters.AddWithValue("disp",       (decimal)dispersionBps);
        cmd.Parameters.AddWithValue("stopPct",    stopPct);
        cmd.Parameters.AddWithValue("asOf",       asOfUtc);
        cmd.Parameters.AddWithValue("k",          k);

        var cases = new List<SimilarCase>(k);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            cases.Add(new SimilarCase(
                SignalAt:            r.GetDateTime(0),
                Side:                r.GetString(1),
                AggregateZ:          (double)r.GetDecimal(2),
                AgreeingVenues:      r.GetInt16(3),
                ParticipatingVenues: r.GetInt16(4),
                DispersionBps:       r.IsDBNull(5) ? 0.0 : (double)r.GetDecimal(5),
                StopPct:             r.GetDecimal(6),
                Outcome:             r.GetString(7),
                OutcomeR:            r.IsDBNull(8) ? 0m : r.GetDecimal(8),
                MinutesToOutcome:    r.IsDBNull(9) ? null : r.GetInt32(9),
                GateDecision:        r.IsDBNull(10) ? null : r.GetString(10),
                Distance:            r.GetDouble(11)));
        }

        return cases;
    }

    /// <summary>
    /// How much labelled evidence exists, so a caller can say "not enough yet"
    /// instead of quoting a win rate from five trades.
    /// </summary>
    public async Task<OutcomeCoverage> GetCoverageAsync(
        string symbol, CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(*) FILTER (WHERE outcome IN ('WIN', 'LOSS', 'TIMEOUT')),
                   COUNT(*) FILTER (WHERE outcome IS NULL OR outcome = 'PENDING'),
                   MIN(signal_at) FILTER (WHERE outcome IN ('WIN', 'LOSS', 'TIMEOUT')),
                   MAX(signal_at) FILTER (WHERE outcome IN ('WIN', 'LOSS', 'TIMEOUT'))
            FROM signal_outcomes WHERE symbol = @symbol
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol", symbol);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return new OutcomeCoverage(0, 0, null, null);

        return new OutcomeCoverage(
            (int)r.GetInt64(0),
            (int)r.GetInt64(1),
            r.IsDBNull(2) ? null : r.GetDateTime(2),
            r.IsDBNull(3) ? null : r.GetDateTime(3));
    }
}
