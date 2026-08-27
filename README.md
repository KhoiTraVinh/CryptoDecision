# CryptoDecision

A cross-venue order-flow strategy on SOL perpetuals. It collects every taker trade from
three exchanges, aggregates them into disjoint 15-minute flow buckets in PostgreSQL, and
enters only when the aggressive-flow imbalance is statistically unusual **and** at least
two venues independently agree. Exits are volatility-scaled, fixed at entry, and enforced
by an exchange-side OCO rather than by this process.

**It places real orders on OKX.** See [Arming](#arming) — three separate switches have to
be open, and they currently are.

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

`api` and `dashboard` sit behind the `ui` profile and are not deployed. The Python
prediction service that used to sit behind an `ensemble` profile has been deleted —
nothing in the entry path read its output.

## Services

| Service | Runtime | Role | Deployed |
|---|---|---|---|
| **Ingestion** | .NET 9 worker | 3 exchange WebSockets → Channels → Kafka | yes |
| **Processor** | .NET 9 worker | Kafka → `COPY` → `flow_bars_15m` + daily features | yes |
| **Bot** | .NET 9 worker | Scorer → risk engine → OKX orders (or paper) | yes |
| **Ollama** | ollama/ollama | Serves `qwen2.5:3b` for the entry gate | yes |
| **Kafka** | KRaft, no ZooKeeper | Trade + kline transport | yes |
| **PostgreSQL** | 16 | `trades` partitioned daily, 7-day retention | yes |
| **API** | .NET 9 web | REST + SignalR | `ui` profile |
| **Dashboard** | nginx | Static single-page UI | `ui` profile |

Exchanges: **Binance, Bybit, OKX**. Each has its own WebSocket client and a normalizer
onto one internal trade shape. Orders go to OKX only, and the price feed is deliberately
OKX too — a stop derived from a different venue's book is a stop for a market the position
is not in.

## The strategy: XVENUE_FLOW

`CrossVenueFlowScorer` is a pure function, which is what lets the live bot and the
backtester run the identical arithmetic. It measures volume-weighted taker order-flow
imbalance per venue on **disjoint** clock-aligned 15-minute buckets, standardises each
venue against **its own** trailing median (MAD × 1.4826 — not against 50%, because venues
have structural biases), and then requires agreement.

Conditions are **conjunctive** — every one can veto, and each refusal has a named code:

| code | meaning |
|---|---|
| `AGGREGATE_BELOW_THRESHOLD` | volume-weighted z inside ±`EnterZ` |
| `NO_CROSS_VENUE_CONSENSUS` | fewer than `MinAgreeingVenues` venues independently at that z |
| `NO_VENUE_QUALIFIED` | no venue cleared its volume / print-count / concentration floor |
| `VENUE_DISPERSION_TOO_WIDE` | cross-venue VWAP spread says the move is already gone |
| `REWARD_RISK_TOO_LOW` | ATR geometry gives less than `MinRewardRisk` after fees |
| `FLOW_BARS_STALE` | newest closed bucket older than `MaxBarAge` — ingestion has stopped |

Current parameters live in **one** place each (`FlowSignalOptions` and
`FlowGeometryDefaults` in `CryptoDecision.Shared`), and `appsettings.json` agrees with
them. That is deliberate: three different parameters have drifted between a code default,
an appsettings override and a backtester literal, and each time the backtester certified a
configuration nobody was running.

Exits are `1.5 × ATR` for the stop and `2 × stop` for the target, where ATR is the
**median** true range over 15-minute bars from the last 4 hours. Median, not mean: at 15
minutes the mean sits 51% above the median because one bar in the sample ranged 13%.

`bot_config.last_verdict_*` holds the current verdict, written every cycle — the
abstention log is throttled and once left the state 33 minutes stale during a 2.7% move.

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

Add the dashboard (http://localhost:8888):

```bash
docker compose --profile ui up -d
```

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
| `prediction_table` | Empty. Its writer is deleted; the API still selects from it |

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

The machinery is proven; the signal's edge is not. Those are different claims.

**Verified with real money:** post-only maker entries fill, and filled 9.2 bps better than
the signal price. Exchange-side OCO arms and fires — every closed trade so far was closed
by OKX, not by this process. Per-trade geometry, gate verdict and effective risk persist on
the row. Three venues have ingested without a gap in `flow_bars_15m` over 48 h.

**Not established:** whether the signal covers its execution cost. On 5.9 days at the
deployed configuration, break-even cost came out at 1.8 bps against the ~7 bps actually
paid, and across a 36-cell parameter sweep **no** configuration was positive in both halves
of the sample. Live: 3 trades, −$0.45, −0.23R. Entries are also late by construction —
high |z| coincides with roughly +0.6% already moved in the preceding hour, because order
flow *is* what moves price, so by the time it is measurable the price has moved.

Six days settles nothing either way. `HYPOTHESES.md` records every parameter changed
without proof, with its decision rule fixed in advance, because at four observations a day
an untracked search will find whatever it is looking for.

## API

Behind the `ui` profile. `GET /api/market-status/{symbol}`, `/dashboard/{symbol}`,
`/volume/{symbol}`, `/whales/{symbol}`, `/klines/{symbol}`, `/momentum/{symbol}`, and
`/api/bot/status` · `/pnl` · `/trades` · `/config` · `/debug`, plus
`POST /api/bot/start` · `/stop`.

SignalR hub at `/hubs/market` pushes `ReceiveVolumeAnalysis` and `ReceiveWhaleAlert`.

## Known constraints

**The API has no authentication.** Anything that can reach it can start or stop the bot.
Acceptable on a private LAN; not acceptable exposed. On the EC2 host `5432` and `8080` are
published in compose but not reachable from outside — use an SSH tunnel rather than opening
them.

**OKX aggregates same-side positions** on one instrument, so two bot trades of 0.08 show in
the OKX app as one 0.16 position at the weighted average entry. Both views are correct and
per-trade reduce-only OCOs sum correctly, but nothing in the UI explains it.
