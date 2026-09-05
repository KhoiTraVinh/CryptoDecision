# Registered hypotheses

One entry per parameter change made without proof, written **before** the result is
known. The point is a trial budget: with roughly four independent observations a day,
a few dozen untracked attempts will manufacture a convincing answer out of noise. An
entry that ends "rejected" is a result. Reaching for the next configuration because
this one failed is how the budget gets spent without anyone noticing.

Rules for an entry:

- Record the decision rule **before** running it, and do not edit it afterwards.
- Judge on data collected **after** the change. The sweep that suggested it does not
  count as evidence for it.
- One live change at a time. Two at once and neither can be attributed.

---

## H1 — Lower EnterZ from 1.5 to 1.0 to reach a measurable sample

- **Opened** 2026-08-27
- **Change** `FlowSignalOptions.EnterZ` 1.5 → 1.0 (appsettings + record default).
  `MinAgreeingVenues` stays 2, `TargetRiskMultiple` stays 2.0, `StopAtrMultiple`
  stays 1.5. Capital and the per-order ceiling move to $30 in the same window.
- **Purpose** Buy observations. Not a claim that 1.0 is better.

### Why

At 1.5 the strategy signalled on 8.2% of buckets. Six days of live running produced
three trades, and in the backtest the live configuration lands in a cell with n=4
in-sample and n=4 out-of-sample — the tool prints "—" because a win rate cannot be
computed from four. At that rate thirty R-multiples take about eight weeks. At 1.0
with venue agreement held at 2, coverage is 20%, so the same thirty arrive in about
three weeks.

### Correction, same day, before this was evaluated

The first version of this entry cited the sweep: `z=1.0 / vn=2 / rr=2.0` as "the only
cell not negative in both halves, break-even 9.2 bps in-sample and 17.2 bps
out-of-sample". **Both numbers were artefacts of two bugs in the backtester**, found
within the hour and fixed:

1. `PolicyConfig.AtrLookbackMinutes` defaulted to 1440 while production runs 240, so
   every cell sized its stops from a 24-hour ATR instead of a 4-hour one. Third
   instance of the same defect — a code default, a different appsettings value, and
   the tool holding its own copy of the code default.
2. The exit walk stepped straight through gaps in the candle series. A LONG entered
   08-23 10:30 was scored "TIMEOUT" after **30.2 hours** against a 12-hour limit, at
   +4.31R, because the deadline fell inside a gap and the next candle was 18 hours
   past it. That one trade carried 90% of the measured edge: without it meanR fell
   from +0.26 to +0.03. Such trades are now `GAP_UNRESOLVED` — their outcome is
   unknowable, since the stop may have been hit inside the gap with no candle to
   record it.

Corrected numbers for this cell: break-even **−2.85 bps** in-sample, **+7.57 bps**
out-of-sample. So the cell is negative in-sample, and the claim that motivated
picking it does not survive.

Corrected picture across the whole sweep: **not one populated configuration is
positive in both halves.** Every one has at least one negative half and most have
two. Six days is not enough to conclude anything, but it is the opposite of
encouraging, and the earlier positive reading was a bug.

### What this does to the rationale

The measurability argument survives untouched: n=4/4 cannot be evaluated, n=10/8 can.
The profitability argument was never made and now could not be.

But a sharper point emerged from the correction, and it argues against trading at all
right now: **the backtest does not need live trades.** Flow bars and candles
accumulate whether or not the bot has money at risk, so the same evidence arrives for
free by waiting. What live trading adds over backtesting is execution realism —
maker fills, slippage, funding, exchange-side OCO — and that has already been
validated: three live trades, post-only entries filling 9.2 bps better than the
signal price, OCO firing correctly on the exchange. That question is answered.

So the honest options are (a) keep trading at 1.0 and pay roughly $23 over four weeks
to learn a little sooner, or (b) stop trading, keep collecting, and run this same
sweep on a month of data for nothing. (b) is the better trade unless there is a
reason to want the live P&L series specifically.

Note that EnterZ gates two things — the aggregate threshold and the bar each venue
must clear to count as agreeing. Lowering it weakens both, so "2 of 3 agree" means
less at 1.0 than at 1.5. That is part of what is being tested.

### Decision rule, fixed in advance

Evaluate when **30 closed trades** have accumulated on data collected after
2026-08-27, or after **four weeks**, whichever comes first.

- **Keep 1.0** if break-even cost is ≥ 7 bps in both halves of the new data AND
  mean R is > 0.
- **Revert to 1.5** if mean R is < 0, or break-even cost is below 7 bps in either
  half.
- **Either way, stop.** A negative result closes this hypothesis. It does not open a
  search for the next cell in the sweep; the next attempt needs its own entry here
  and a reason that is not "the previous one failed".

### Cost of being wrong

Risk per trade is about $0.27 at capital $30 with a $30 ceiling and a ~0.9% stop.
Four entries a day for four weeks is roughly $30 of tuition if every trade loses.

### Result

_Open._

---

## H2 — Give the gate a scale for every check it is allowed to refuse on

- **Opened** 2026-08-28
- **Change** `AiEntryGate`: each of the four grounds for skipping now states the
  arithmetic condition that makes it available, and the brief renders every checked
  value next to the threshold the scorer applied (dispersion against its 25 bps
  ceiling, excluded venues against zero, open positions against the limit, today's
  loss against the daily loss limit). Plus `signal_outcomes` recording and the
  outcome labeler, which change no behaviour.
- **Purpose** Stop refusals whose stated premise the brief contradicts. Not a claim
  that the gate will approve more, and not a threshold change.

### Why

Twenty-three hours of production log, the only window that survived (the container
log is destroyed by every deploy). Fifteen signals, all fifteen put to the model:

- 5 of 12 refusals cited a premise the brief contradicted — "venues were excluded for
  thin data" where the brief said 0 excluded, "several positions are already open"
  where the brief said 0 open and where the loop guarantees 0, since it does not ask
  the gate while at the position limit.
- The other 8 refusals cited "dispersion is wide" at 2.8-13.2 bps against the 25 bps
  ceiling the scorer had already enforced.
- Replayed under the real constraints: the live gate scored −3.00R over the window,
  approving everything scored +1.00R.

The −3.00R vs +1.00R is 5 trades and decides nothing. The contradicted premises are
not a sample-size question: a refusal on a number the brief says is false is a defect
whichever way the trade would have gone. This entry exists because the fix is still a
live behaviour change made while H1 is open, and two concurrent live changes are
exactly what the rules at the top of this file forbid. It is recorded rather than
avoided because the alternative — leaving a known defect running to protect the
attribution of a hypothesis about a different parameter — is worse.

### Decision rule, fixed in advance

Evaluate when **60 signals** have been recorded in `signal_outcomes` under the new
prompt, or after **two weeks**, whichever comes first. Judged on refusal quality, not
on P&L, because P&L at four trades a day cannot separate this from H1:

- **Keep** if refusals on contradicted premises fall to **zero** (query: section 3 of
  `scripts/gate-report.sql`) AND the gate still refuses at least one signal in the
  window — a gate that approves everything is not a gate and would be a different
  regression.
- **Revert** if contradicted premises persist above 10% of refusals. That would mean
  the wording is not the binding constraint and the four grounds should move into
  code as deterministic checks, leaving the model only the judgement it can actually
  make.
- **Either way, stop.** No second prompt edit inside this window.

### Cost of being wrong

If the gate becomes too permissive it approves signals it used to refuse, at ~$0.27
risk per trade and a 4/day cap. Two weeks is at most ~$15 of tuition, bounded by the
same daily cap and per-order ceiling as everything else.

### Result

_Open._

---

## H3 — Binance may vouch alone at z≥1.5; the thin venues still need each other

- **Opened** 2026-08-28
- **Change** three parameters in `FlowSignalOptions`, in both places that hold them
  (the record defaults the backtester reads, and `FlowStrategy:Signal:*` in
  appsettings):
  - `MaxDispersionBps` 25.0 → **0.0** — the dispersion check is off.
  - `VenueAgreementZ` — **new**, 1.5. The bar an individual venue must clear, split
    out from `EnterZ` (1.0), which stays the bar for the aggregate.
  - `SufficientVenue` — **new**, "BINANCE". A venue whose agreement alone satisfies
    the consensus requirement.
  - `MinAgreeingVenues` stays **2**, and now means: two venues, when Binance is not
    one of them.
- **Purpose** Operator's rule: if Binance is clearly unusual, that is enough. If the
  case rests on OKX and Bybit, both have to say so.

### Why

The venues are not interchangeable and the old rule treated them as if they were.
Measured median volume in a 15-minute bucket: **Binance $7.0M, OKX $2.07M, Bybit
$0.89M** — an eightfold spread. An imbalance that is statistically unusual on Binance
is unusual across most of the traded market; the same z on Bybit is unusual across a
tenth of it. "Two of three agree" counted a Bybit vote and a Binance vote as one each.

Splitting `VenueAgreementZ` from `EnterZ` fixes a second conflation. The aggregate is
volume-weighted and standardised against its own history, so it is already a quieter
series than any single venue; asking both to clear 1.0 asked much more of the
aggregate. 1.5 for a venue against 1.0 for the aggregate says the aggregate must be
unusual and its voucher must be *clearly* unusual.

The dispersion check goes off because it shows no relationship to outcomes in the
data that exists (winners at 4.3 and 7.5 bps, losers 2.2-13.2), it fired once in the
24 hours audited, and disabling it removes the gate's most-used excuse: `AiEntryGate`
permits the "late entry" ground only at 80% of the ceiling, and with no ceiling the
brief states the ground is unavailable.

**What is not claimed.** Nothing here is read off outcome data. Per-venue z was never
recorded for the fifteen signals with known results, so the sufficient-venue rule is
a judgement about market structure that the existing data cannot test. It becomes
testable from now on: `signal_outcomes.venue_votes` stores every venue's z, so "would
a Bybit-led signal have won" is a query once enough of them exist.

**Direction still comes from the aggregate.** Binance clearing 1.5 satisfies the
*consensus* requirement; it does not bypass `EnterZ`. If Binance is at +1.6 while the
aggregate sits inside ±1.00, the bucket still abstains as AGGREGATE_BELOW_THRESHOLD.
That is deliberate — the aggregate falling below the band while Binance is extreme
means the other two venues are actively leaning the other way — but it is a narrower
reading than "Binance above 1.5 enters", and reversing it is a one-line change if the
abstain counts show it binding often.

Third live change open at once (H1 EnterZ, H2 gate brief). H1's decision rule reads
signal frequency, which this affects in both directions: the sufficient-venue rule
admits Binance-led signals the old rule refused, while raising the per-venue bar to
1.5 refuses marginal ones it used to accept.

### Decision rule, fixed in advance

Evaluate when **60 signals** are recorded in `signal_outcomes` after this ships, or
after **two weeks**, whichever comes first. Judged per source, from `venue_votes`:

- **Keep** if Binance-alone signals (Binance agreed, fewer than 2 venues agreed) have
  mean R no worse than signals with 2+ agreeing venues, and the overall mean R is
  ≥ −0.60R (the mean of the 15 audited signals under the old rule).
- **Revert `SufficientVenue` to null** if Binance-alone signals are materially worse
  than corroborated ones — that is the rule failing on its own terms.
- **Revert `VenueAgreementZ` to 1.0** only if signal count collapses below ~5/day,
  and never in the same window as another change.
- Dispersion stays off either way unless section 6 of `scripts/gate-report.sql` shows
  a win-rate gradient across dispersion buckets.

### Cost of being wrong

Bounded by the caps that do not move: 4 entries/day, one position at a time, ~$0.30
risk per trade, $30 per-order ceiling, 15% daily loss limit. Two weeks at the cap is
roughly $17 if every trade loses.

### Result

_Open._

---

## H4 — The signal is late; wait for the move to give some of itself back

- **Opened** 2026-09-05
- **Change** `FlowStrategyOptions.EntryPullbackAtr` — **new**, 0.75. An actionable
  verdict no longer enters at market. It enters only once price has retraced
  0.75 × ATR from the close of the bucket that produced it, and abstains as
  `AWAITING_PULLBACK` until then. Nothing else moves: `EnterZ` 1.0,
  `VenueAgreementZ` 1.5, `SufficientVenue` BINANCE, `MinAgreeingVenues` 2,
  `StopAtrMultiple` 1.5, `TargetRiskMultiple` 2.0 all stay.
- **Purpose** Fix entry *timing*, not entry *selection*. This is the operator's
  point and it is the right one: every threshold change proposed so far only
  removes signals, and most of them remove them by preferring a higher z — which
  is the reading that is most late.

### Why

The signal is late by construction and it is now measured how late. Aggregate z
correlates **+0.467** with the PRECEDING hour's return and **−0.116** with the
following one, over 440 buckets. Order flow is what moves price, so a closed
15-minute bucket showing an imbalance is describing a move that has happened.
Entering at market on that reading buys the end of the move, and the retracement
then counts as adverse excursion against the position.

Bucketing outcomes by |z| says the same thing from the other side — the *weakest*
signals do best, which is the opposite of what a threshold is supposed to buy:

    z 1.0-1.5   n=8    mean R -0.184
    z 1.5-2.0   n=10   mean R -0.400
    z >= 2.0    n=9    mean R -0.333

Measured on the first 28 paper trades, over the 12 hours after entry:

    entry                median MFE   median MAE   ratio   win at 2R
    at market (current)    2.00 ATR     3.46 ATR    0.58       14.3%
    0.25 ATR pullback      2.00 ATR     3.18 ATR    0.63       17.4%
    0.50 ATR pullback      2.06 ATR     2.96 ATR    0.70       25.0%
    0.75 ATR pullback      1.96 ATR     2.72 ATR    0.72       27.3%
    1.00 ATR pullback      2.05 ATR     2.51 ATR    0.82       26.3%

The favourable excursion barely moves; the adverse one falls by a fifth. Waiting
does not find better trades, it finds a better price in the same trade. That is
precisely what a late entry costs, and the mechanism was predicted before the
numbers were run rather than read off them afterwards.

**Why this and not more geometry.** An 80-cell sweep of stop width (1.5–5.0 × ATR)
against target multiple (0.75–3.0 × stop) produced **no positive cell**; the best
was −0.286R and the 2R win rate stayed pinned at 17.9% however wide the stop went,
because the target scales with it. The exit-geometry lever is exhausted. Entry
timing is the first thing that has moved the win rate at all.

### What is not claimed

Read off 28 trades, in-sample, one market regime, from a 24-cell grid — and picking
a cell from a grid is what the backtester prints unsorted to discourage. 27.3% is
still well under the ~42% this geometry needs to break even; the gap narrows from
28 points to 15, it does not close. The 1.5 ATR / 15-minute cell that shows a 1.67
ratio is n=3 and is noise.

### Implementation note

Stateless. The reference is the close of the signal bucket's last minute, recomputed
identically every cycle, so there is no pending-order state to keep or recover. The
waiting window is therefore however long the verdict stays actionable rather than a
fixed timer — narrower than the 120 minutes that scored best above, so expect fewer
fills than the table suggests.

### Decision rule, fixed in advance

Evaluate on trades opened **after 2026-09-05**, when **40 closed paper trades** have
accumulated or after **7 days**, whichever comes first. Judged against the 28-trade
pre-change baseline (win rate 28.6%, mean R −0.434, median MFE/MAE 0.58):

- **Keep** if median MAE in ATR units falls below **3.0** (baseline 3.46) AND mean R
  improves on −0.434. The MAE test is the primary one: it is the mechanism this
  change claims, it is measurable at n=40, and mean R at n=40 is not.
- **Revert to 0** if median MAE does not fall, or if fills drop below **4/day**
  (baseline 7.3) — a rule that waits for a price the market never returns to is a
  rule that stops trading, and that is a different failure from a better entry.
- **Do not tune the 0.75.** If it fails, it fails; sweeping the multiple on the same
  data that produced it is how this file's trial budget gets spent invisibly. A
  second value needs its own entry.

### Cost of being wrong

None in money — `paper_mode = true`, no funds reachable. The cost is the window:
7 days of paper trades attributed to this change rather than to something else, and
H3 (60 signals) evaluated on a sample whose entry timing changed midway. H3's venue
comparison survives, since both venue configurations are affected equally.

### Result

_Open._
