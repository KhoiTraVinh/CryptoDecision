"""
MarketContext — the single input object every prediction model receives.

Why this exists
---------------
The old pipeline handed models a raw daily_feature_table row: four floats with no
units, no history and no order-flow detail. That is enough for XGBoost (which
learned the scale during training) but close to useless for an LLM, which reasons
over described situations rather than unlabelled numbers.

MarketContext gathers everything the stack already collects — daily features,
multi-timeframe order flow, whale pressure, a short daily history, and per-exchange
quotes — and exposes two views of it:

  feature_row()  → flat dict, the shape heuristic/XGBoost already expect
  describe()     → a labelled text brief with units, for the LLM prompt

Cross-exchange quotes are included because this stack ingests five venues and
nothing downstream has ever compared them. Price dispersion across venues is a
genuine regime signal: it widens under stress and collapses in quiet markets.
"""
from __future__ import annotations

import datetime as _dt
from dataclasses import dataclass, field


def _f(value: object, default: float = 0.0) -> float:
    """Coerce psycopg2 Decimal / None / str to float without raising."""
    if value is None:
        return default
    try:
        return float(value)  # type: ignore[arg-type]
    except (TypeError, ValueError):
        return default


def _i(value: object, default: int = 0) -> int:
    try:
        return int(value)  # type: ignore[arg-type]
    except (TypeError, ValueError):
        return default


# ── Order flow ────────────────────────────────────────────────────────────────


@dataclass(frozen=True)
class TimeframeFlow:
    """Buy/sell pressure over one lookback window."""

    label: str
    buy_count: int = 0
    sell_count: int = 0
    buy_volume_usd: float = 0.0
    sell_volume_usd: float = 0.0
    whale_buy_count: int = 0
    whale_sell_count: int = 0

    @property
    def total_trades(self) -> int:
        return self.buy_count + self.sell_count

    @property
    def buy_ratio(self) -> float:
        """Share of trades that were aggressive buys. 0.5 = balanced."""
        total = self.total_trades
        return (self.buy_count / total) if total else 0.5

    @property
    def volume_buy_ratio(self) -> float:
        """Share of *notional* that was aggressive buying. 0.5 = balanced."""
        total = self.buy_volume_usd + self.sell_volume_usd
        return (self.buy_volume_usd / total) if total else 0.5

    @property
    def whale_total(self) -> int:
        return self.whale_buy_count + self.whale_sell_count

    @property
    def whale_buy_ratio(self) -> float:
        return (self.whale_buy_count / self.whale_total) if self.whale_total else 0.5

    @staticmethod
    def _lean_label(ratio: float, buy_word: str, sell_word: str) -> str:
        """
        Name the dominant side outright instead of leaving it to be inferred.

        A 7B model reading "17.9% buy-side" will latch onto the phrase "buy-side"
        and describe the tape as buying, because comparing 0.179 against an
        unstated 0.5 baseline is exactly the implicit numeric step small models
        skip. Stating the lean in words removes that step; the percentages stay
        alongside for anything that can use them.
        """
        if ratio >= 0.58:
            return f"strongly {buy_word}"
        if ratio >= 0.52:
            return f"mildly {buy_word}"
        if ratio <= 0.42:
            return f"strongly {sell_word}"
        if ratio <= 0.48:
            return f"mildly {sell_word}"
        return "balanced"

    def describe(self) -> str:
        if self.total_trades == 0:
            return f"  {self.label:>4}: no trades recorded"

        lean = self._lean_label(self.volume_buy_ratio, "BUYING", "SELLING")

        if self.whale_total:
            whale_lean = self._lean_label(self.whale_buy_ratio, "BUYING", "SELLING")
            whale = (
                f"; whales {self.whale_buy_count} buy vs "
                f"{self.whale_sell_count} sell ({whale_lean})"
            )
        else:
            whale = "; no whale trades"

        # Each figure carries its own label. The earlier phrasing — "39% of trades
        # and 43% of volume were aggressive buys" — required carrying "were
        # aggressive buys" across a conjunction, and the model reliably read the
        # second number as the *sell* share instead, inverting the tape it was
        # reasoning about.
        return (
            f"  {self.label:>4}: {self.total_trades:,} trades — {lean}. "
            f"buy share: {self.buy_ratio:.0%} of trade count, "
            f"{self.volume_buy_ratio:.0%} of volume "
            f"(so sell share is {1 - self.buy_ratio:.0%} and "
            f"{1 - self.volume_buy_ratio:.0%}){whale}"
        )


# ── Cross-exchange ────────────────────────────────────────────────────────────


@dataclass(frozen=True)
class ExchangeQuote:
    """Most recent price and recent activity on one venue."""

    exchange: str
    last_price: float = 0.0
    volume_usd: float = 0.0
    trade_count: int = 0


# ── Daily history ─────────────────────────────────────────────────────────────


@dataclass(frozen=True)
class DailyPoint:
    date: _dt.date
    return_24h: float
    volatility: float
    whale_count: int


# ── The context object ────────────────────────────────────────────────────────


@dataclass(frozen=True)
class MarketContext:
    symbol: str
    as_of: _dt.datetime
    date: _dt.date

    # Daily aggregates (percentages stored as plain numbers: 2.31 means 2.31%)
    return_24h: float = 0.0
    volatility: float = 0.0
    volume_change: float = 0.0
    whale_count: int = 0
    total_volume: float = 0.0
    vwap: float = 0.0

    flows: tuple[TimeframeFlow, ...] = field(default_factory=tuple)
    history: tuple[DailyPoint, ...] = field(default_factory=tuple)
    quotes: tuple[ExchangeQuote, ...] = field(default_factory=tuple)

    last_price: float = 0.0

    # ── Derived views ─────────────────────────────────────────────────────────

    def flow(self, label: str) -> TimeframeFlow:
        """Look up one timeframe, returning an empty flow when absent."""
        for f in self.flows:
            if f.label == label:
                return f
        return TimeframeFlow(label=label)

    @property
    def price_vs_vwap_pct(self) -> float:
        """Percent the last price sits above (+) or below (-) the session VWAP."""
        if self.vwap <= 0 or self.last_price <= 0:
            return 0.0
        return (self.last_price - self.vwap) / self.vwap * 100.0

    @property
    def volatility_regime(self) -> str:
        if self.volatility > 5.0:
            return "high"
        if self.volatility > 3.0:
            return "elevated"
        if self.volatility > 1.0:
            return "normal"
        return "low"

    @property
    def exchange_spread_bps(self) -> float:
        """
        Dispersion between the cheapest and dearest venue, in basis points.

        Wide dispersion means the venues disagree — typically thin books or a fast
        move in progress. Near-zero means a calm, well-arbitraged market.
        """
        prices = [q.last_price for q in self.quotes if q.last_price > 0]
        if len(prices) < 2:
            return 0.0
        low, high = min(prices), max(prices)
        if low <= 0:
            return 0.0
        return (high - low) / low * 10_000.0

    @property
    def trend_7d(self) -> float:
        """Sum of daily returns over the available history window, in percent."""
        return sum(p.return_24h for p in self.history)

    # ── Views for consumers ───────────────────────────────────────────────────

    def feature_row(self) -> dict:
        """
        Flat dict in the shape heuristic.py and the XGBoost model already consume.

        Keeps those models working unchanged while the richer context is available
        to anything that wants it.
        """
        return {
            "symbol": self.symbol,
            "date": self.date,
            "return_24h": self.return_24h,
            "volatility": self.volatility,
            "volume_change": self.volume_change,
            "whale_count": self.whale_count,
            "total_volume": self.total_volume,
            "vwap": self.vwap,
        }

    def describe(self) -> str:
        """
        Human-readable market brief handed to the LLM.

        Every number carries its unit and, where the convention is not obvious,
        a note on how to read it — an LLM cannot be assumed to know that
        `is_buyer_maker = false` means an aggressive buy.
        """
        lines: list[str] = []
        lines.append(f"SYMBOL: {self.symbol}")
        lines.append(f"AS OF (UTC): {self.as_of:%Y-%m-%d %H:%M}")
        if self.last_price > 0:
            lines.append(f"LAST PRICE: {self.last_price:,.2f} USDT")

        lines.append("")
        lines.append("DAILY AGGREGATES")
        lines.append(f"  24h return: {self.return_24h:+.2f}%")
        lines.append(
            f"  Realised volatility: {self.volatility:.2f}% ({self.volatility_regime} regime)"
        )
        lines.append(f"  Volume change vs prior day: {self.volume_change:+.2f}%")
        lines.append(f"  Whale trades today (>100k USDT): {self.whale_count}")
        if self.vwap > 0:
            lines.append(
                f"  Session VWAP: {self.vwap:,.2f} USDT "
                f"(last price is {self.price_vs_vwap_pct:+.2f}% vs VWAP)"
            )

        if self.flows:
            lines.append("")
            lines.append(
                "ORDER FLOW BY TIMEFRAME "
                "(an aggressive buy is a taker lifting the offer. Above 50% means "
                "net buying pressure, below 50% means net selling pressure — the "
                "dominant side is named on each line)"
            )
            for f in self.flows:
                lines.append(f.describe())

        if self.quotes:
            lines.append("")
            lines.append("CROSS-EXCHANGE (last 5 minutes)")
            for q in sorted(self.quotes, key=lambda x: -x.volume_usd):
                if q.last_price <= 0:
                    continue
                lines.append(
                    f"  {q.exchange:<9} {q.last_price:>12,.2f} USDT  "
                    f"{q.trade_count:>6,} trades  {q.volume_usd:>14,.0f} USDT volume"
                )
            lines.append(
                f"  Price dispersion across venues: {self.exchange_spread_bps:.1f} bps "
                "(wide dispersion suggests thin books or a fast move in progress)"
            )

        if self.history:
            lines.append("")
            lines.append("RECENT DAILY HISTORY (most recent last)")
            for p in self.history:
                lines.append(
                    f"  {p.date:%Y-%m-%d}: return {p.return_24h:+6.2f}%, "
                    f"volatility {p.volatility:5.2f}%, whales {p.whale_count}"
                )
            lines.append(f"  Cumulative return over window: {self.trend_7d:+.2f}%")

        return "\n".join(lines)

    # ── Construction ──────────────────────────────────────────────────────────

    @classmethod
    def from_rows(
        cls,
        symbol: str,
        as_of: _dt.datetime,
        feature_row: dict,
        flow_rows: list[dict] | None = None,
        history_rows: list[dict] | None = None,
        quote_rows: list[dict] | None = None,
    ) -> "MarketContext":
        """Assemble a context from raw database rows, coercing Decimal → float."""
        flows = tuple(
            TimeframeFlow(
                label=str(r.get("timeframe") or "?"),
                buy_count=_i(r.get("buy_count")),
                sell_count=_i(r.get("sell_count")),
                buy_volume_usd=_f(r.get("buy_volume_usd")),
                sell_volume_usd=_f(r.get("sell_volume_usd")),
                whale_buy_count=_i(r.get("whale_buy_count")),
                whale_sell_count=_i(r.get("whale_sell_count")),
            )
            for r in (flow_rows or [])
        )

        history = tuple(
            DailyPoint(
                date=r["date"],
                return_24h=_f(r.get("return_24h")),
                volatility=_f(r.get("volatility")),
                whale_count=_i(r.get("whale_count")),
            )
            for r in (history_rows or [])
        )

        quotes = tuple(
            ExchangeQuote(
                exchange=str(r.get("exchange") or "?"),
                last_price=_f(r.get("last_price")),
                volume_usd=_f(r.get("volume_usd")),
                trade_count=_i(r.get("trade_count")),
            )
            for r in (quote_rows or [])
        )

        # Prefer a live cross-exchange print; fall back to VWAP when no venue
        # reported a trade in the lookback window.
        volume_weighted = [q for q in quotes if q.last_price > 0]
        if volume_weighted:
            last_price = max(volume_weighted, key=lambda q: q.volume_usd).last_price
        else:
            last_price = _f(feature_row.get("vwap"))

        return cls(
            symbol=symbol.upper(),
            as_of=as_of,
            date=feature_row["date"],
            return_24h=_f(feature_row.get("return_24h")),
            volatility=_f(feature_row.get("volatility")),
            volume_change=_f(feature_row.get("volume_change")),
            whale_count=_i(feature_row.get("whale_count")),
            total_volume=_f(feature_row.get("total_volume")),
            vwap=_f(feature_row.get("vwap")),
            flows=flows,
            history=history,
            quotes=quotes,
            last_price=last_price,
        )
