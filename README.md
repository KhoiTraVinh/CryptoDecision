# CryptoDecision

An order-flow strategy on SOL perpetuals. It collects every taker trade from three
exchanges, aggregates them into disjoint 15-minute flow buckets in PostgreSQL, and enters
**with** the side that dominated the last closed bucket — when it dominated by enough, on
enough volume. Exits are a fixed-percentage stop and target plus a flow-reversal rule.

**It is in paper mode and must stay there.** See [Arming](#arming) and
[Status, honestly](#status-honestly). Two of the three switches that reach real funds are
already open; `bot_config.paper_mode` is the only one still closed, and the strategy has
never been shown to survive a trending market.

```
Binance ┐
Bybit   ├─WebSocket─▶ Ingestion ─Kafka─▶ Processor ─▶ PostgreSQL
OKX     ┘                                              │  trades → flow_bars_15m
                                                       │
                                                       ▼
                                                Bot (XVENUE_FLOW)
                                                 │ scorer → geometry → sizing
                                                 │ optional LLM veto gate
                                                 ▼
                                             OKX  ─ post-only entry + OCO exit
```

`api` and `dashboard` have been deleted. They sat behind a `ui` profile and were never
deployed; the API's only client was the dashboard, and of the six endpoints it served the
dashboard called one. The Python prediction service that used to sit behind an `ensemble`
profile is gone for the same reason — nothing in the entry path read its output.

There is no UI. The bot is started, stopped and configured with SQL against `bot_config`,
and everything it wants to tell you is in that row or in `bot_trades`, `signal_outcomes`
and `flow_bars_15m`. See **Operating the bot** below.

## Services

| Service | Runtime | Role | Deployed |
|---|---|---|---|
| **Ingestion** | .NET 9 worker | 3 exchange WebSockets → Channels → Kafka | yes |
| **Processor** | .NET 9 worker | Kafka → `COPY` → `flow_bars_15m` + daily features | yes |
| **Bot** | .NET 9 worker | Scorer → risk engine → OKX orders (or paper) | yes |
| **Ollama** | ollama/ollama | Serves `qwen2.5:3b` for the entry gate | yes |
| **Kafka** | KRaft, no ZooKeeper | Trade + kline transport | yes |
| **PostgreSQL** | 16 | `trades` partitioned daily, 7-day retention | yes |

Exchanges: **Binance, Bybit, OKX**. Each has its own WebSocket client and a normalizer
onto one internal trade shape. Orders go to OKX only, and the price feed is deliberately
OKX too — a stop derived from a different venue's book is a stop for a market the position
is not in.

## The strategy: XVENUE_FLOW

One class, `CrossVenueFlowStrategy`, with the entry rule selected by
`FlowStrategy:Signal:EntryMode`. Scoring lives in `CrossVenueFlowScorer`, a pure function,
which is what lets the live bot and the backtester run identical arithmetic.

**`FlowRatio` is what runs.** Take the last closed 15-minute bucket, wait three minutes for
the aggregation worker to fold in late trades, and enter **with** the dominant side when:

    total notional  >= RatioMinVolumeUsd   ($3M)
    dominant side   >= RatioMinimum x the other   (2.1x)

Price is not consulted at all. Refusals carry named codes:

| code | meaning |
|---|---|
| `BUCKET_NOT_SETTLED` | bucket closed under `RatioSettleMinutes` ago; the worker is still writing it |
| `VOLUME_TOO_THIN` | under the notional floor — a 2:1 lean on $2M is what a quiet hour looks like |
| `RATIO_TOO_LOW` | neither side dominates by enough |
| `NO_CLOSED_BUCKET` | nothing to score yet |
| `FLOW_BARS_STALE` | newest bucket older than `MaxBarAge` — ingestion has stopped |

Three earlier modes remain in the enum and are dead unless configured. `ZScore` required a
statistically unusual imbalance with independent cross-venue agreement; `OfiMagnitude`
entered on raw |OFI| in a single bucket; `CandleReversal` bought a 0.60% fall and shorted a
1.00% rise on price alone. They are kept because switching back is a config edit and
because the backtester can still run them, not because they are recommended — see
`HYPOTHESES.md` for why each was replaced.

### Why the sign is what it is

CandleReversal entered **against** the tape by construction: buying a fall means buying
while sellers are lifting, and 138 of its 155 signals had the last closed bucket leaning
the other way. FlowRatio enters with it.

That is not a contradiction of the finding that aggregate OFI carries no direction — that
figure, -0.015 against the next hour's signed return over 1,496 buckets, is an average over
the whole distribution. This rule reads only its extreme tail: a 2.1x imbalance occurs in
about 5% of buckets, 3% with the volume floor. An average of zero constrains a tail very
little, and nobody had measured the tail on its own.

### Exits

    SL   2.00%   floor, from MinStopPct — the noise floor, not the fee floor
    TP   4.00%   = 2 x stop (TargetRiskMultiple), from the ATR path
    cap  12 hours
    OR   the 10-bucket (150 min) aggregate imbalance turns against the position,
         having favoured it earlier in the hold -> close_reason OFI_REVERSAL

`UseRangeGeometry` is **false** for this mode. Entering with the dominant side puts price at
the edge of its own range, so a range-boundary target lands almost on the entry and fails
`MinRewardRisk`: 3 of 92 signals survived it.

Two floors sit under the stop and they are separate claims. The **fee** floor
(`roundTripFeeRate x MinStopAsFeeMultiple`, 0.40%) says a stop must clear the cost of the
round trip. The **noise** floor (`MinStopPct`, 2.00%) says it must clear SOL's ordinary
movement — median 15-minute true range is 1.07%, so the 0.40% that bound every trade for
weeks was not a barrier that fired when the trade was wrong, it fired when nothing had
happened. Collapsing the two into one multiple named for fees is what hid that for so long.

The exit rule is stateless: every cycle re-reads the buckets since entry and re-asks the
question, and it requires the imbalance to have favoured the trade at some point after
entry before a turn counts. Without that guard it reads a window that mostly predates the
trade — the defect that closed two live positions thirty seconds after opening them on
2026-09-09.

`bot_config.last_verdict_*` holds the current verdict, written every cycle — the abstention
log is throttled and once left the state 33 minutes stale during a 2.7% move.

## The LLM gate

**The model does not decide entries.** `CrossVenueFlowStrategy` does. When
`require_ai_gate` is on, `qwen2.5:3b` is handed one finished proposal and may only
**refuse** it. It cannot choose direction, size, stop or target — there is no argument for
any of them. Every failure mode (Ollama down, timeout, unparseable, empty) resolves to *no
entry*, which costs an opportunity and never a position.

Verdicts are cached per `(symbol, side, 15-minute bucket)`: the loop runs every 30 seconds
while the evidence only changes on the quarter hour, and the model does not answer the same
question the same way twice.

**Caveat, stated because it is load-bearing.** The gate has twice refused candidates by
reciting a criterion from its own skip list without checking whether it applied — once
misjudging reward:risk that code had already validated, once claiming venues had been
"excluded" when none ever has been. Both prompts are fixed and the brief now states the
counts as explicit numbers, but the honest summary is that a small model handed a list of
skip criteria will treat it as a menu of excuses. If a criterion can be checked
arithmetically, check it in code before the model sees it.

## Risk engine

`RiskEngine.Expectancy()` derives what a TP/SL pair actually requires, net of the
configured round-trip fee (`FlowStrategy:RoundTripFeeRate`, 10 bps — OKX taker is 5 bps a
side):

```
TP 2.0% / SL 1.5%  →  1.06:1 after fees, breakeven win rate 48.6%
TP 0.3% / SL 5.0%  →  0.02:1 after fees, breakeven win rate 98.1%
```

The bot refuses to start on the second. That is not decoration: it was the shipped
default, and it needed one loss to undo 52 wins.

Note that this validates the `bot_config` TP/SL pair, which **XVENUE_FLOW does not use** —
it derives geometry from ATR per trade and checks it against `MinRewardRisk` at decision
time. The startup figure is therefore about a configuration the active strategy overrides.

Sizing is fixed-fractional: `notional = capital × risk_pct / stop_pct`, then capped by
`Okx__MaxOrderNotionalUsd`. **While that ceiling binds, `risk_pct_per_trade` has no
effect** and every order is exactly the ceiling; the log says so with both figures.

Circuit breakers stop trading on the daily loss limit, a consecutive-loss streak, or
realised drawdown. A breach writes `enabled = false` **to the database** — a breaker that a
container restart clears is not a breaker. Re-arming is manual and deliberate:

```sql
UPDATE bot_config SET enabled = true WHERE id = 1;
```

The consecutive-loss streak is scoped to one strategy and a window derived from the entry
cap. Unscoped, it walked from a new strategy's first losing trade into four losses from a
strategy retired three days earlier and disabled the bot for fifteen hours.

## Running it

```bash
docker compose up -d
```

Startup order is fixed and not incidental: `postgres → db-check → processor → db-migrate
→ bot`. `DatabaseInitializer` (in Processor) owns the base tables; `sql/*.sql` are
increments on top of them, so running SQL first fails at `006`.

First boot pulls `qwen2.5:3b` (~1.9 GB) into a named volume. `OLLAMA_KEEP_ALIVE=-1` keeps
it resident, which costs ~2.6 GB of RAM permanently for a model called a handful of times a
day — lower it to `30m` if the host needs the memory, at the price of a ~13 s cold load on
the first gate call.

## Migrations

`sql/migrate.sh` and the `db-migrate` compose service apply `sql/*.sql` in filename order,
**once each**, recorded in `schema_migrations` with SHA-256 checksums. This replaced
`docker-entrypoint-initdb.d`, which only ran on an empty volume and silently stopped at
`011`. `MIGRATE_BASELINE=1` records without executing; `CHECK_ONLY=1` stops after the
credential check.

`db-check` runs first and exists to say one thing out loud before anything else starts:
`POSTGRES_PASSWORD` only applies to an empty volume, so changing the secret against an
existing one leaves the database on the old password while `pg_isready` still reports
healthy — and the failure otherwise surfaces three services later as "processor is
unhealthy".

## Database

| Table | Contents |
|---|---|
| `trades` | RANGE-partitioned by `trade_time`, one partition per day, 7-day retention |
| `flow_bars_15m` | Per-venue 15-minute taker buckets — the strategy's only input |
| `klines_1m` | 1-minute OHLCV; feeds ATR and the backtester |
| `bot_config` | Singleton row: commands, heartbeat, and the current verdict |
| `bot_trades` | Trade history with realised P&L, per-trade stop/target/ATR/gate verdict |
| `bot_trades_archive` | Trades from retired strategies, kept out of the active series |
| `daily_feature_table` | return_24h, volatility, volume_change, whale_count, vwap |
| `prediction_table` | Empty. Its writer is deleted and so is the API that read it |

`is_whale` is a generated column, `quote_qty > 100000`. On SOL that fires rarely — 116 of
2.41 M trades in a recent 24 hours, largest single trade $488,913 — so treat it as an
outlier marker, not a routine signal. The same threshold was calibrated for BTC and
contributed nothing to the retired MOMENTUM score while appearing to carry 15% of it.

`v_flow_signal_readiness` answers "can the strategy score yet". Use it rather than
`v_flow_bar_coverage`, which measures from the first bucket ever written and is dragged
down by historical gaps.

## Arming

Real money needs **three** switches open, and they are separate on purpose:

| switch | where | meaning |
|---|---|---|
| `bot_config.paper_mode` | database | `true` = internal simulation, OKX never called |
| `Okx__EnableLiveTrading` | env | the arm switch |
| `Okx__DemoTrading` | env | `true` = OKX's simulated endpoint, not real funds |

Flipping `paper_mode` alone does **not** reach real funds if `DemoTrading` is still true.
`scripts/health.sh` prints all three together, because knowing two of them is how you
conclude you are safe when you are not.

The OKX API key is **IP-bound**. Code `50110` means the caller's address is not on its
allowlist; the bot runs and paper-trades fine, so this only bites the moment `paper_mode`
goes false. A default EC2 public IP changes on stop/start — allocate an Elastic IP first.

## Operations

```bash
bash scripts/health.sh
```

Nine sections, exits non-zero on FAIL. Service expectations are derived from
`docker compose config --services`, never hardcoded — a hardcoded exclusion list reported
"0 unhealthy" three times while `db-check` was failing.

`scripts/flow-vs-passive.sql` measures what the entry threshold costs: forward return in
the direction flow pointed, banded by |z|.

## Backtesting

```bash
dotnet run --project src/CryptoDecision.Backtest -- \
  --conn "Host=postgres;Port=5432;Database=crypto;Username=crypto;Password=crypto" \
  --symbol SOLUSDT --cost-bps 7 --sweep
```

With no flags it uses the deployed parameters, so a plain run validates what is running.
It reports **break-even round-trip cost in bps** rather than leading with Sharpe: that is
the number that decides whether a signal survives execution. Three rules are enforced — no
lookahead, entry at the next open, and the stop assumed first when one minute's range
contains both barriers. Trades whose holding window contains a gap in the candle series are
marked `GAP_UNRESOLVED`, because the stop may have been hit inside the gap with nothing to
record it; walking through one turned a 12-hour limit into a 30-hour hold at +4.31R and
carried 90% of a since-retracted result.

## Status, honestly

The machinery is proven; the signal's edge is not. Those are different claims, and the gap
between them has not narrowed.

**Verified with real money**, before the account went back to paper: post-only maker
entries fill, and filled 9.2 bps better than the signal price. Exchange-side OCO arms and
fires. Per-trade geometry, gate verdict and effective risk persist on the row. Three venues
have ingested without a gap in `flow_bars_15m`.

**Not established: whether any entry rule here covers its execution cost.** Ten paper
trades since the 2026-09-08 reset total +$0.21 on $30 of capital, five wins and five
losses. That is noise at that count, in both directions.

### The one thing that must be read before any live-money discussion

The predecessor rule, CandleReversal, was measured across the two trending stretches in a
19-day sample:

    regime                       side    n   win %   total R
    UPTREND   +13.1% / 2 days   SHORT   19    0.0%   -19.00
                                LONG    13   38.5%    +7.08
    DOWNTREND -11.1% / 5 days   LONG    44    9.1%   -27.76
                                SHORT    7    0.0%    -6.00
    SIDEWAYS  (everything else)                       +6.44

Zero wins in 26 trades on the fading side of both trends. Chop earned +6.44R; trends lost
45.68R. FlowRatio replaced it and enters on the opposite sign, which should help, but it
has three closed trades — it has not been observed in a trend at all.

Six detector families were then tried to tell trend from chop and five failed, for a reason
that is arithmetic rather than bad luck: a 13% move over two days is 0.068% of drift per
15-minute bucket against a typical 0.639% true range, a signal-to-noise ratio of 1:10. The
regime lives at a multi-day scale and the bot lives at a 15-minute one. (RSI(14) on 4h bars
did pass all three robustness checks as a long-side filter and was declined for reasons
recorded in `HYPOTHESES.md`.)

### How to read any number in this repository

Roughly **eighty configurations** were measured against a single 19-day window over two
days. At that density something will always look good. Nothing here is treated as a finding
unless it holds across a **plateau** of adjacent settings, in **both halves** of the sample,
and **after discarding its single best trade** — the last because two separate "findings"
turned out to be one flash crash each.

Two things have ever cleared all three: the 2.00% stop floor, and the declined RSI filter.

`HYPOTHESES.md` records every parameter changed without proof, with its decision rule fixed
in advance, including one — H10, the flow exit — that was **shipped against its own
evidence** on an explicit decision. Read the decision rule before reading the result.

## Operating the bot

There is no API and no UI. `bot_config` is the control surface — one row, id 1.

```sql
-- start / stop
UPDATE bot_config SET enabled = true  WHERE id = 1;
UPDATE bot_config SET enabled = false WHERE id = 1;

-- is it alive, and what did it last decide?
SELECT enabled, paper_mode, symbol, last_heartbeat, last_eval_at,
       open_trade_count, total_trades, total_pnl_usd,
       last_verdict_code, last_verdict_detail, last_verdict_at,
       last_refusal_reason, last_refusal_at, refusal_count,
       last_sizing_note
FROM bot_config WHERE id = 1;
```

The worker picks up `enabled` within one poll (`eval_interval_seconds`, default 30) and
refuses to start on a configuration `RiskEngine.Validate` calls impossible — it logs why
and re-checks every 30 s, so fixing the row is enough.

A tripped circuit breaker writes `enabled = false` itself and does **not** re-arm on its
own. That is deliberate: re-arming is the `UPDATE` above, run by a person who has looked
at the trades first.

Everything else worth reading is in `bot_trades` (what was traded and why),
`signal_outcomes` (every signal including the refused ones, and what the market did next),
and `flow_bars_15m` (the evidence the scorer runs on). `scripts/gate-report.sql` and
`scripts/z.sh` are the two queries used most.

## Known constraints

**Postgres is the control surface and has no application-level auth.** Anything that can
reach `5432` can start the bot. On the EC2 host `5432` is published in compose but not
reachable from outside — use an SSH tunnel rather than opening it.

**OKX aggregates same-side positions** on one instrument, so two bot trades of 0.08 show in
the OKX app as one 0.16 position at the weighted average entry. Both views are correct and
per-trade reduce-only OCOs sum correctly, but the OKX app cannot show you the split —
`bot_trades` is where the individual positions live.
