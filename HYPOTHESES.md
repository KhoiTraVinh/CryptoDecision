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
