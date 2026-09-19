# CryptoDecision

An order-flow strategy on SOL perpetuals. It collects every taker trade from three
exchanges, aggregates them into disjoint 15-minute flow buckets in PostgreSQL, and enters
**with** the side that dominated the last closed bucket — when it dominated by enough, on
enough volume. Exits are a fixed-percentage stop and target plus a flow-reversal rule.

**It is in paper mode.** Two of the three switches that reach real funds are already open —
`Okx__EnableLiveTrading=true` and `Okx__DemoTrading=false`, pointing at **real funds** with
a $30-per-order ceiling. `bot_config.paper_mode` is the only one still closed. See
[Arming](#arming) and [Status, honestly](#status-honestly) before opening it: 45 closed
trades sit at +0.003R with the account net negative, two trades carry the entire result,
and the strategy has never been observed in a sustained trend.

Paper is sized and filled to match live: `$100 capital × 0.60% risk / 2% stop = $30
notional = $10 margin at 3x`, filled on the **real top of book** — the entry rests on its
own side like the live post-only order, the exit crosses like the live OCO — and charged
maker 2 bps in, taker 5 bps out. **One gap remains and it is not small:** the live
post-only entry can fail to fill, and measured on 68 recorded signals that drops 29% of
them — the *better* 29%, at +0.014R against −0.023R. Live will take fewer trades than paper
and worse ones. Do not read paper P&L as a forecast of live.

```
Binance ┐
Bybit   ├─WebSocket─▶ Ingestion ─Kafka─▶ Processor ─▶ PostgreSQL
OKX     ┘                                              │  trades → flow_bars_15m
                                                       │
                                                       ▼
                                                Bot (2 strategies, both live)
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

## The strategies

One class, `CrossVenueFlowStrategy`, registered **twice** — each instance bound to its own
configuration section, each carrying its own name, entry mode and thresholds. Scoring lives
in `CrossVenueFlowScorer`, a pure function over its inputs.

| name | section | rule | live? |
|---|---|---|---|
| `XVENUE_FLOW` | `FlowStrategy` | `FlowRatio` — order flow | yes, ~2/day |
| `CANDLE_REVERSAL` | `DipStrategy` | `CandleReversal` — price | yes, ~7.6/day — the MAJORITY of trades |

Which of them trade is `bot_config.active_strategies`, a database edit rather than a
redeploy. A name there with no registration logs "Unknown strategy" every cycle and trades
nothing; a registration missing from there is idle. The two names must differ —
`StrategyEvaluator` keys its lookup on `Name`, so a collision throws at startup.

### XVENUE_FLOW — two ways in

Take the last closed 15-minute bucket and wait `RatioSettleMinutes` (3) for the
aggregation worker to fold in late trades. Price is not consulted at all. There are two
entry paths and `bot_trades.entry_path` records which one fired:

    RATIO        notional >= RatioMinVolumeUsd ($3M)
                 AND dominant side >= RatioMinimum x the other (2.1x, = |OFI| 0.355)
                 SWITCHED OFF as H16 on 2026-09-18 after 12 trades at -4.477R by
                 raising RatioMinimum to 99.0, and switched back ON as H18 on
                 2026-09-19 -- by operator override, with H16's own restore
                 condition measured and NOT met. See H18.

    HIGH_VOLUME  notional >= RatioHighVolumeUsd ($20M)
                 -> the ratio test is WAIVED, entry takes whichever side traded more,
                    however narrow the lead

The waiver exists to catch a news print, where a stampede has size on both sides and never
produces a 2.1:1 lean — measured, ratio falls as volume rises, and only 1 of the 42 buckets
over $20M also cleared 2.1:1. It is **H11, shipped against its own first measurement**;
read that entry before trusting it.

`entry_path` is a column and not a substring of `entry_rationale`, because the code
branches on it and this repository has already paid once for recovering a branch condition
from prose.

Refusals carry named codes:

| code | meaning |
|---|---|
| `BUCKET_NOT_SETTLED` | bucket closed under `RatioSettleMinutes` ago; the worker is still writing it |
| `VOLUME_TOO_THIN` | under the notional floor — a 2:1 lean on $2M is what a quiet hour looks like |
| `RATIO_TOO_LOW` | neither side dominates by enough, and the bucket is under the waiver |
| `BUCKET_PERFECTLY_BALANCED` | waiver volume reached with buy exactly equal to sell — no side to take |
| `ONE_SIDED_BUCKET` | no volume on one side at all; a data fault, not a market state |
| `NO_CLOSED_BUCKET` | nothing to score yet |
| `FLOW_BARS_STALE` | newest bucket older than `MaxBarAge` — ingestion has stopped |

### CANDLE_REVERSAL — the dip rule, and the majority of the trade stream

Buy after price has fallen `ReversalDropPct` (0.60%) over `ReversalBars` (2) closed
15-minute bars; short after it has risen `ReversalRisePct` (1.00%) over
`ReversalBarsShort` (1). Price only. The two thresholds are deliberately not mirror
images — the dip pays from 0.60% and the rally does not pay until 1.00%.

**It was measured negative in every pre-launch configuration** — −0.039R with ATR
geometry, −0.068R with the range geometry that has since been deleted, negative in both
halves and after discarding the best trade in all of them — and was enabled anyway as H13. Live it is the only rule in
positive R: **17 closed at +0.211 mean** as of 2026-09-19. Read that with the outlier check
applied, which is the whole point of the check: two trades from the 2026-09-18 rally carry
it, and without them the same 17 trades are **−0.102**.

It fires about 7.6 times a day against FlowRatio's 2, so it is most of what the account
does. See H13, and the FATAL IN A TREND note — this is the rule that took 0 wins in 26
trades across both trending stretches, by buying falling knives.

`ZScore` and `OfiMagnitude` **were deleted from the code on 2026-09-18**, along with the
~660 lines only reachable through a config typo. The enum numbering starts at 2 because
renumbering would silently change what an existing `entry_mode` string resolves to. An
unrecognised mode now abstains with `UNRECOGNISED_ENTRY_MODE` instead of falling through
to the z-rule — which is the defect that got the backtester deleted. `git log` has them.

### Position limits

Four nested caps sit between an actionable verdict and an order. All are checked **before
the gate**, because a gate call costs 24-42 seconds -- measured at 24.0s with no retrieved
examples in the brief and 34.4s with five -- and there is no point spending it on an entry
that cannot be placed.

    max_open_total                 5   across every strategy -- INERT, see below
    max_open_trades_per_strategy   2   within one strategy
    max_open_per_side              1   within one strategy, one direction
                                       -> "two at once, never two the same way"
    max_open_high_volume           1   positions opened through the HIGH_VOLUME waiver

Any of them set to 0 is disabled. The first exists because the other three are scoped by
strategy name: two strategies at 2 each is 4 and a third makes it 6, with nothing but a
startup log line noticing.

**`max_open_total` at 5 can never bind** -- two strategies at 2 each is 4 -- and the bot
says so at every start with `ACCOUNT_LIMIT_INERT`. Lower it below 4 or accept that the
per-strategy caps are the only ones in force.

**Slots are first come, first served and are not reserved per strategy.** A rule that
signals several times a day will hold them against one that signals twice. From outside, a
strategy blocked by a cap is indistinguishable from a strategy with nothing to trade —
`scripts/flow.sh` section 5 and `scripts/rules.sql` section 4 both exist to tell them
apart.

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
    TP   4.00%   = 2 x stop (TargetRiskMultiple)   *** has NEVER fired, see below ***
    cap  12 hours
    OR   the aggregate imbalance over the last N closed buckets turns against the
         position, having favoured it earlier in the hold -> OFI_REVERSAL
         N = 15 for XVENUE_FLOW (H15), 10 for CANDLE_REVERSAL

**The OFI reversal is the only exit that earns.** Over the 40 trades since the 2.00% stop
floor shipped: OFI_REVERSAL 30 exits at **+4.647R**, SL 6 exits at **−6.615R**, TIMEOUT 3 at
−0.424R, TP **zero**. Every barrier is scaled by `1 + 10 × excursion` capped at 2 while
`use_dynamic_tp_sl` is on, so the target *retreats* as price advances and the stored 4.00%
is only reachable at 6.67% — which is why TP has never fired. `dynamic_stop_price` and
`dynamic_target_price` make the live barrier one `SELECT` away; they are write-only, because
feeding them back into their own input compounds the barrier past +116% in five minutes.

Neither the OFI exit nor the dynamic barriers are open questions — both are the operator's
findings from live data, and the "shipped against the evidence" phrasing that used to sit in
the source came from measurements taken without the OFI exit in the loop.

A **range-boundary geometry** stood beside the ATR one until 2026-09-19 and was deleted with
the other dormant features. It measured *better* — mean R +0.107 against +0.015 over 1,486
decision points — and was still off, because entering with the dominant side puts price at
the edge of its own range and leaves a boundary target sitting on the entry. The tables are
in `HYPOTHESES.md` under "Removed features"; reach for them if a rule is ever added that
enters *into* a range.

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

**`strategy_verdicts`** holds the current verdict for each active strategy, written every
cycle — the abstention log is throttled and once left the state 33 minutes stale during a
2.7% move. It replaced `bot_config.last_verdict_*` in `sql/034`, because one row could not
hold two strategies' verdicts and whichever ran last silently overwrote the other. Those six
columns still exist and are read by nothing.

## The LLM gate

**The model does not decide entries.** `CrossVenueFlowStrategy` does. When
`require_ai_gate` is on, `qwen2.5:3b` is handed one finished proposal and may only
**refuse** it. It cannot choose direction, size, stop or target — there is no argument for
any of them. Every failure mode (Ollama down, timeout, unparseable, empty) resolves to *no
entry*, which costs an opportunity and never a position.

Verdicts are cached per `(symbol, side, 15-minute bucket)`: the loop runs every 30 seconds
while the evidence only changes on the quarter hour, and the model does not answer the same
question the same way twice.

**The gate approved 45 of 45 and refused nothing, and it was not the model's fault.** All
four grounds the prompt allowed it to refuse on were arithmetically unreachable: dispersion
needs a ceiling and `MaxDispersionBps` is 0; thin evidence needs an excluded venue and
neither surviving rule scores venues; a losing day needs half the daily limit against a
worst day of $0.65; concentration needs two open positions while the per-side cap is
checked *before* the gate is called. The model said so on every call — *"is not subject to
any grounds for skipping"*. A bigger model or a tool loop would have changed nothing.

**H17 (2026-09-19) replaced them with four grounds this account can actually meet**,
computed from its own closed trades in one query (`BotRepository.GetGateEvidenceAsync`) and
marked AVAILABLE / NOT AVAILABLE in the brief:

| ground | condition |
|---|---|
| this setup is losing | **any** slice below with n≥5 and mean R < 0 |
| trend against the entry | 4-hour move beyond ±2.0% against the proposed side |
| one event twice | same `entry_path` fired inside 120 minutes |
| concentration | a position already open on this side **across every strategy** |

**H19 (2026-09-19) made the first ground a table rather than a number.** The gate is shown
the same account history cut three ways, each with its own count, and the ground fires when
any slice that has enough trades is negative:

```
CANDLE_REVERSAL LONG              16 closed,  10 won, mean R +0.290
CANDLE_REVERSAL in 12-20 UTC       6 closed,   3 won, mean R -0.199
CANDLE_REVERSAL overall           17 closed,  10 won, mean R +0.211
```

Those are real production values, and they are the case that justifies the cut: the narrow
slice says the setup is fine, the session slice says *not in this window*, and before H19
the gate could only see the first. A slice under 5 trades prints its count and is marked
"too thin to read" rather than being hidden — "no evidence" and "evidence that says nothing"
are different, and omitting the thin one invites the model to read the wide slice as the
narrow one.

The **session** split is 12:00–20:00 UTC against everything else. Replaying all 68 recorded
signals with the deployed exit set, that window totals **−10.745R over 32 trades** against
+9.923R over 36 outside it, and every threshold from 06:00 to 18:00 splits the same
direction. Only half of that finding survives the outlier check, and it is the useful half:
the two largest trades both fall inside the *favourable* window, so "accept the winners"
halves when they are removed while "refuse the losers" does not move at all. It ships as
evidence behind a veto and not as a reason to size up. See H19, including why 19 days cannot
separate "the US session trends" from "these particular 19 days trended during the US
session".

It is a **base rate and not a rule** on purpose. `if (hour >= 12 && hour < 20) refuse;`
would freeze one measurement in place forever; a count and a mean drift toward zero on their
own if the effect was noise, and the ground quietly stops being available.

That last one is the concentration question nothing was asking: `max_open_per_side` is
scoped **per strategy**, so CANDLE_REVERSAL and XVENUE_FLOW can each hold a LONG and both
checks pass.

Each ground reduces to a threshold, and a threshold belongs in `RiskEngine`. What the model
is asked for is the **combination** — the prompt says one available ground is usually not
enough and two or more usually is. If any single ground turns out to be decisive alone,
move it into code and take it out of the brief, or this becomes a 35-second inference call
performing an `if`.

**It has never refused, so there is no evidence it refuses well.** The first audit of it,
on five trades, scored the live gate at −3.00R against +1.00R for approving everything.
H17 carries the decision rule; judge it there, not here.

Retrieval of similar past signals was broken in the same way and fixed alongside: its
distance summed four quantities that are **constant** for both surviving rules, so it
returned the five most recent signals wearing similarity's label — and it did not filter by
strategy, so a price rule was shown a flow rule's outcomes as precedent.

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

It used to validate the `bot_config` TP/SL pair, which neither strategy uses — certifying
a 2.00%/1.50% setup while every position ran a 2.00% stop against a 4.00% target. Each
strategy now states the geometry it will actually place (`ITradingStrategy.DescribeRisk`)
and is judged on that, so the startup line is about the configuration that runs:

```
XVENUE_FLOW: target 6.67% / stop 2.00% → 3.13:1, breakeven win rate 24.2%
```

**6.67%, not the 4.00% stored on the row.** `use_dynamic_tp_sl` scales both barriers by
`1 + 10 × excursion` (capped at 2), so the target *retreats* as price advances and the two
only meet where `x = t / (1 − 10t)`. That is why **no trade has ever exited on TP**;
`dynamic_stop_price` / `dynamic_target_price` exist so the live barrier is one `SELECT`
away. They are write-only — feeding them back into their own input compounds the barrier
past +116% in five minutes.

Sizing is fixed-fractional: `notional = capital × risk_pct / stop_pct`, then capped by
`Okx__MaxOrderNotionalUsd`. At the current settings the two agree exactly, so the ceiling
never binds:

```
capital $100 × risk 0.60% / stop 2.00%  =  $30.00 notional
                                        =  $10 margin at 3x
                                        =  $0.60 at risk per trade
```

After the 0.01 lot grid that is 0.26 contracts ≈ $29.05. **If the ceiling ever does bind,
`risk_pct_per_trade` stops meaning anything** and every order is exactly the ceiling; the
log says so with both figures.

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

`scripts/rules.sql` prices each entry rule on its own rows — by `strategy` and by
`entry_path` — and checks the position book against all four caps. Run it before believing
any figure about "the strategy", because more than one rule is producing the trades.

## Measuring a rule

There is no backtester in this repository. `CryptoDecision.Backtest` was deleted on
2026-09-11; recover it from git history if it is ever wanted back.

It was removed because it had stopped being the thing it claimed to be. Two defects found
on the day it went: `CrossVenueFlowScorer.Score()` had no `FlowRatio` branch and sent
every mode except `OfiMagnitude` to the z-score rule, so the tool measured a retired rule
while reporting the deployed one; and it never passed a `minStopPct`, so it simulated a
~1.6% stop against a deployed 2.00% floor. Both were invisible — nothing errored, and
every number it printed looked plausible. That is the third time a validation tool in this
repository has certified a configuration nobody was running.

Rules are now measured directly against `flow_bars_15m` and `klines_1m`: take the buckets
a rule would have fired on, walk the 1-minute candles forward from the entry instant, and
report the result. The three rules that mattered in the old engine still apply and have to
be applied by hand each time:

- **No lookahead.** Only buckets that had closed at the decision instant.
- **Entry at the price the bot could actually have got** — the bucket close plus the
  settle wait, not the price that produced the signal.
- **The stop is taken first** when one minute's range spans both barriers. 1-minute OHLC
  does not say which came first, and assuming the favourable one is how a losing policy
  reports a win rate.

- **Model the whole deployed exit set.** Stop, target, the dynamic widening *and* the OFI
  reversal. The OFI exit closes 30 of the last 40 trades, so a replay without it measures a
  strategy that does not exist — and that omission is what produced the "shipped against
  the evidence" label that sat wrongly on two features for weeks.
- **Validate the replay against reality before believing its output.** Run it at its
  no-change setting and compare against the real trades. The one written on 2026-09-19
  reproduced 93% of exit reasons at mean |ΔR| 0.237 and came out slightly *pessimistic*
  (−0.079R against an actual +0.149R). The deleted backtester never once did this.

Judge a result on three checks before believing it, all in `HYPOTHESES.md`: it holds
across a plateau of neighbouring parameter values, it holds in both halves of the sample,
and it survives discarding the single best trade. Almost nothing measured here has passed
all three.

**And check where the outliers sit.** The session finding in H19 looked like it did two
things until the two largest trades were located — both inside the favourable window. Half
the finding evaporated and half did not, and which half was which is the entire result.

## Status, honestly

The machinery is proven; the signal's edge is not. Those are different claims, and the gap
between them has not narrowed.

**Verified with real money**, before the account went back to paper: post-only maker
entries fill, and filled 9.2 bps better than the signal price. Exchange-side OCO arms and
fires. Per-trade geometry, gate verdict and effective risk persist on the row. Three venues
have ingested without a gap in `flow_bars_15m`.

**Not established: whether any entry rule here covers its execution cost.** As of
2026-09-19, **45 closed trades at +0.003R mean, +0.149R total, and the account is net
negative at −$0.05.** Two trades carry +5.117R of that, so the other 43 come to −4.97R.

The exit breakdown is the thing to read before touching anything:

    OFI_REVERSAL   30  +4.647 R      the only exit producing positive R
    FLOW_REVERSAL   2  +0.101 R
    TIMEOUT         3  -0.424 R
    SL              6  -6.615 R      all of the damage
    TP              0       —        has never fired

Four levers have now been measured on the deployed exit set. Three are not where the problem
is. Stop width: 2.00% is the best of six, 1.0% the worst — narrowing it takes stop-outs
from 12 to 29 and OFI exits from 51 to 30, destroying the exit that earns. Entry timing:
no pullback depth improves anything, because waiting shrinks losers and winners by the same
factor and skips winners 11:1. Dynamic barriers: indistinguishable on XVENUE_FLOW,
mildly better on CANDLE_REVERSAL.

The fourth is the only one that separated anything. **Entries between 12:00 and 20:00 UTC
total −10.745R over 32 trades; everything outside that window totals +9.923R over 36.** The
refusing half survives the outlier check unchanged while the accepting half halves, so it
ships as evidence behind the gate veto rather than as a reason to trade more. H19.

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
45.68R. FlowRatio enters on the opposite sign, which should help, but it has 28 closed trades at
-0.123R and has still not been observed in a sustained trend.

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

Three things have ever cleared all three: the 2.00% stop floor, the declined RSI filter,
and — negatively — the finding that entry timing is not a lever, which failed at every
pullback depth tried.

`HYPOTHESES.md` records every parameter changed without proof, with its decision rule fixed
in advance. Read the decision rule before reading the result.

One correction worth carrying: H10, the OFI reversal exit, was long described as "shipped
against its own evidence". That label came from a measurement taken **without the OFI exit
in the loop**, which is the exact error `measure-the-deployed-exit` warns about. On the
configuration that actually runs it is the only exit producing positive R. The same applies
to `use_dynamic_tp_sl`. Neither is an open question.

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
and `flow_bars_15m` (the evidence the scorer runs on).

`scripts/flow.sh` is the one to reach for: it shows every closed bucket with its volume,
buy/sell split, imbalance and OFI, and scores each against the deployed thresholds — read
from the running container, not from this checkout, because `src/` is not deployed. It
replaced `z.sh` on 2026-09-11. `z.sh` reconstructed the ZScore statistic, which no rule
has read since the entry moved to FlowRatio, and it was flagging entry conditions on
buckets the bot was correctly ignoring. A monitor that reports signals the strategy does
not act on makes an idle bot look broken and would make a broken one look busy.

`scripts/rules.sql` is the one to run before believing any result. Several hypotheses are
open on a single trade stream at once, and it splits them: by `strategy` and by
`entry_path`, with the repository's three checks — both sample halves, and after
discarding the single best trade — evaluated per rule, and the position book against every
cap. It replaced `flow-vs-passive.sql` on 2026-09-12, which banded forward returns by |z|
and labelled the band `>= 1.5` as "bot ENTERS": `EnterZ` has been 1.0 since 2026-08-27 and
no deployed rule has read z since the entry moved to FlowRatio, so both halves of that
label were wrong.

One hypothesis it cannot split is dynamic TP/SL, which changes the barriers on every trade
and leaves no column behind. Its counterfactual has to be simulated offline — the feature
is stateless and recomputed from stored levels, so replaying the same signals with it on
and off is exact.

`scripts/gate-report.sql` prices what the gate's refusals were worth. It still runs, but
its premise is currently empty: the gate has refused nothing, so there is nothing to
price.

Both SQL files read the newer `bot_config` caps through `row_to_json` rather than by
column name, so they still print against a database that has not had `sql/032` and
`sql/033` applied. Naming a missing column aborts the statement; a missing JSON key is
NULL and prints as "not migrated" — which is the more useful answer during exactly the
half-finished deploy where you would want to run them.

## Known constraints

**Postgres is the control surface and has no application-level auth.** Anything that can
reach `5432` can start the bot. On the EC2 host `5432` is published in compose but not
reachable from outside — use an SSH tunnel rather than opening it.

**OKX aggregates same-side positions** on one instrument, so two bot trades of 0.08 show in
the OKX app as one 0.16 position at the weighted average entry. Both views are correct and
per-trade reduce-only OCOs sum correctly, but the OKX app cannot show you the split —
`bot_trades` is where the individual positions live.

**`sql/023` does not describe the running configuration.** It is the only record of
`bot_config` in the repo and it seeds `{XVENUE_FLOW}` alone, `use_dynamic_tp_sl FALSE`,
`allow_entry_without_gate FALSE`, `max_entries_per_day 4`, capital 60 and risk 0.01.
Production runs both strategies, dynamic barriers ON, the gate fallback ON, 20 entries a
day, capital 100 and risk 0.006. **A fresh database comes up as a different bot, silently,
and no migration records the live values.** Read `bot_config` before believing any
configuration statement — including the ones in this file.

**Sixteen `bot_config` columns are read by nothing.** `grid_step_pct`, `min_ai_confidence`,
`min_buy_ratio_1h`, `min_momentum_buy_ratio`, `trailing_stop_pct`, `use_trailing_stop`,
`use_ai_agent`, `use_ai_filter`, `use_breakeven_stop`, `breakeven_trigger_pct` and the six
`last_verdict_*` columns are all leftovers of features that have been removed. They are
harmless but misleading: grepping for one of them finds a column and suggests the feature
still exists. The code that read the last four was deleted on 2026-09-19.
