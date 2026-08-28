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
