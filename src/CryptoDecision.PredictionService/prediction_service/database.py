"""
PostgreSQL helpers.

get_latest_features   – most recent daily_feature_table row for a symbol.
get_feature_history   – trailing daily rows, oldest first, for trend context.
get_timeframe_flows   – cumulative 5m/15m/1h order flow from the trades table.
get_exchange_quotes   – last price and 5-minute activity per venue.
upsert_prediction     – INSERT … ON CONFLICT DO UPDATE into prediction_table.

Every trades query is bounded to at most one hour so PostgreSQL prunes to the
current daily partition instead of scanning the full 30M+ row table.
"""
from __future__ import annotations

import psycopg2
import psycopg2.extras

from .config import settings

# Column names returned by get_latest_features
# Must match daily_feature_table schema exactly.
_FEATURE_COLS = (
    "symbol",
    "date",
    "return_24h",
    "volatility",
    "volume_change",
    "whale_count",
    "total_volume",
    "vwap",
)


def get_connection() -> psycopg2.extensions.connection:
    conn = psycopg2.connect(settings.postgres_url)
    conn.autocommit = False
    return conn


def _fetch_all(
    conn: psycopg2.extensions.connection, sql: str, params: tuple
) -> list[dict]:
    with conn.cursor(cursor_factory=psycopg2.extras.RealDictCursor) as cur:
        cur.execute(sql, params)
        return [dict(r) for r in cur.fetchall()]


# ── Daily features ────────────────────────────────────────────────────────────


def get_latest_features(
    conn: psycopg2.extensions.connection, symbol: str
) -> dict | None:
    """Return the most recent daily_feature_table row for *symbol*, or None."""
    sql = """
        SELECT
            symbol,
            date,
            return_24h,
            volatility,
            volume_change,
            whale_count,
            total_volume,
            vwap
        FROM daily_feature_table
        WHERE symbol = %s
        ORDER BY date DESC
        LIMIT 1
    """
    with conn.cursor(cursor_factory=psycopg2.extras.RealDictCursor) as cur:
        cur.execute(sql, (symbol.upper(),))
        row = cur.fetchone()
    return dict(row) if row else None


def get_feature_history(
    conn: psycopg2.extensions.connection, symbol: str, days: int = 7
) -> list[dict]:
    """
    Trailing daily rows *excluding* today, oldest first.

    Today's row is excluded because it is the row being predicted on — including
    it in the "recent history" section of the brief would show the model its own
    subject twice and read as a longer trend than actually exists.
    """
    sql = """
        SELECT date, return_24h, volatility, whale_count
        FROM daily_feature_table
        WHERE symbol = %s
          AND date < CURRENT_DATE
        ORDER BY date DESC
        LIMIT %s
    """
    rows = _fetch_all(conn, sql, (symbol.upper(), max(0, days)))
    return list(reversed(rows))  # chronological for the prompt


# ── Order flow ────────────────────────────────────────────────────────────────

#: (label, interval) pairs, widest last. Intervals are literals defined here, not
#: caller input, so interpolating them into SQL carries no injection risk.
_FLOW_WINDOWS: tuple[tuple[str, str], ...] = (
    ("5m", "5 minutes"),
    ("15m", "15 minutes"),
    ("1h", "1 hour"),
)

_FLOW_AGGREGATES = """
            COUNT(*) FILTER (WHERE NOT is_buyer_maker)                    AS buy_count,
            COUNT(*) FILTER (WHERE     is_buyer_maker)                    AS sell_count,
            COALESCE(SUM(quote_qty) FILTER (WHERE NOT is_buyer_maker), 0) AS buy_volume_usd,
            COALESCE(SUM(quote_qty) FILTER (WHERE     is_buyer_maker), 0) AS sell_volume_usd,
            COUNT(*) FILTER (WHERE is_whale AND NOT is_buyer_maker)       AS whale_buy_count,
            COUNT(*) FILTER (WHERE is_whale AND     is_buyer_maker)       AS whale_sell_count
"""


def _build_timeframe_flow_sql() -> str:
    """
    One materialised scan of the trailing hour, then a bare aggregate per window.

    An aggregate with no GROUP BY always yields exactly one row, so a window with
    no trades comes back as zeros rather than a missing row — which is why this
    shape is preferred over joining against a VALUES list of windows.
    """
    branches = [
        f"""
        SELECT
            '{label}' AS timeframe,
{_FLOW_AGGREGATES.rstrip()}
        FROM recent
        WHERE trade_time >= NOW() - INTERVAL '{interval}'
        """
        for label, interval in _FLOW_WINDOWS
    ]

    return f"""
        WITH recent AS MATERIALIZED (
            SELECT is_buyer_maker, is_whale, quote_qty, trade_time
            FROM trades
            WHERE symbol = %s
              AND trade_time >= NOW() - INTERVAL '1 hour'
        )
        {"UNION ALL".join(branches)}
    """


_TIMEFRAME_FLOW_SQL = _build_timeframe_flow_sql()


def get_timeframe_flows(
    conn: psycopg2.extensions.connection, symbol: str
) -> list[dict]:
    """
    Cumulative buy/sell pressure over the trailing 5m, 15m and 1h windows.

    Windows are cumulative, not disjoint: the 15m row includes the last 5 minutes.
    That is what "15-minute flow" means to a reader, and it lets the model compare
    a short window against the longer one it is nested in to see acceleration.

    is_buyer_maker = false means the taker was the buyer, i.e. an aggressive buy
    lifting the offer. That is the convention the ingestion layer normalises every
    exchange onto.
    """
    rows = _fetch_all(conn, _TIMEFRAME_FLOW_SQL, (symbol.upper(),))

    # Return in ascending window order regardless of what the planner produced.
    order = {label: i for i, (label, _) in enumerate(_FLOW_WINDOWS)}
    return sorted(rows, key=lambda r: order.get(str(r.get("timeframe")), 99))


def get_exchange_quotes(
    conn: psycopg2.extensions.connection, symbol: str
) -> list[dict]:
    """
    Last traded price and 5-minute activity for every venue that printed a trade.

    Price dispersion across venues is a regime signal this stack collects but has
    never used: it widens when books thin out or a move is underway, and collapses
    when the market is calm and well arbitraged.
    """
    sql = """
        WITH recent AS (
            SELECT exchange, price, quote_qty, trade_time
            FROM trades
            WHERE symbol = %s
              AND trade_time >= NOW() - INTERVAL '5 minutes'
        ),
        agg AS (
            SELECT
                exchange,
                COUNT(*)                    AS trade_count,
                COALESCE(SUM(quote_qty), 0) AS volume_usd
            FROM recent
            GROUP BY exchange
        ),
        last_print AS (
            SELECT DISTINCT ON (exchange)
                exchange,
                price AS last_price
            FROM recent
            ORDER BY exchange, trade_time DESC
        )
        SELECT a.exchange, l.last_price, a.volume_usd, a.trade_count
        FROM agg a
        JOIN last_print l ON l.exchange = a.exchange
        ORDER BY a.volume_usd DESC
    """
    return _fetch_all(conn, sql, (symbol.upper(),))


# ── Predictions ───────────────────────────────────────────────────────────────


def upsert_prediction(
    conn: psycopg2.extensions.connection,
    symbol: str,
    date: object,
    direction: str,
    confidence: float,
    model_version: str,
    rationale: str = "",
    signals: dict | None = None,
) -> None:
    """
    Write one prediction row.
    ON CONFLICT (symbol, date, model_version) → overwrite the verdict in place.
    """
    sql = """
        INSERT INTO prediction_table
            (symbol, date, direction, confidence, model_version, rationale, signals, created_at)
        VALUES
            (%s, %s, %s, %s, %s, %s, %s, NOW())
        ON CONFLICT (symbol, date, model_version)
        DO UPDATE SET
            direction   = EXCLUDED.direction,
            confidence  = EXCLUDED.confidence,
            rationale   = EXCLUDED.rationale,
            signals     = EXCLUDED.signals,
            created_at  = NOW()
    """
    with conn.cursor() as cur:
        cur.execute(
            sql,
            (
                symbol.upper(),
                date,
                direction,
                confidence,
                model_version,
                rationale,
                psycopg2.extras.Json(signals or {}),
            ),
        )
    conn.commit()
