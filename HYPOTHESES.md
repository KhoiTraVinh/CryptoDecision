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

---

## H5 — The short side pays, but only above a threshold of its own

**Changed 2026-09-09.** `ReversalLongOnly: true -> false`, and a new
`ReversalRisePct = 1.00` that the short side is gated on instead of the mirrored
`ReversalDropPct = 0.60`.

### What was believed before, and why it was wrong

The short branch was left unimplemented on a measurement that only ever tested the
mirrored threshold: fading a 0.60% rally returns +0.066% in-sample and +0.068%
out-of-sample at one hour, against a 0.070% round trip. That number is correct. The
error was concluding from it that the short side does not pay, when what it shows is
that 0.60% is the wrong threshold for that side.

Worse, the branch that claimed to make this a config decision was unreachable. The
dip guard returned for every move above -0.60%, so the `movePct > 0` test below it
could never hold, and `ReversalLongOnly: false` silently changed nothing. A switch
that reports success and alters no behaviour is the same defect class as the stale
prediction read.

### Measured on production candles, 2026-08-21 to 2026-09-09

1,562 closed 15-minute buckets of SOLUSDT, short return over the two hours following
the signal bucket:

    rise >=    n    2h return   win %   avg favourable   avg adverse
      0.60%   126     +0.086%   59.5%       1.359%          1.200%
      0.80%    79     +0.133%   64.6%       1.541%          1.269%
      1.00%    49     +0.348%   67.3%       1.994%          1.460%
      1.25%    27     +0.495%   74.1%       2.283%          1.589%
      1.50%    20     +0.471%   70.0%       2.291%          1.537%

Split in half, at 1.00%: first half n 38 +0.419% (68.4% win), second half n 11
+0.103% (63.6% win). Both positive.

1.00 is taken rather than the higher-scoring 1.25 because 1.25 rests on 27
observations and the curve is flat past 1.00.

### Decision rule, fixed in advance

Evaluate when **20 SHORT trades have closed** or after **14 days**, whichever comes
first, on trades opened after this shipped.

- **Keep** if mean R over closed SHORT trades exceeds the LONG mean R over the same
  window. The comparison is against the concurrent long side, not against zero,
  because both sides face the same regime and the same geometry, and a market that
  is simply falling would flatter the short side on an absolute test.
- **Revert to `ReversalLongOnly: true`** if SHORT mean R is worse than LONG by more
  than 0.3R, or if fewer than 8 SHORT trades have been taken in 14 days — a rule that
  rarely fires cannot be evaluated and should not be carried.
- **Do not tune the 1.00.** A second threshold needs its own entry in this file.

### Cost of being wrong

None in money — `paper_mode = true`. The cost is that SHORT and LONG now compete for
attention within one 7-day window, and H4's entry-timing evaluation is diluted by a
second side entering on a different rule. `EntryPullbackAtr` is 0.0 in the shipped
config, so H4 is not live and nothing is actually contended.

### Result

_Open._

---


---

## H6 — Cutting the hold on a flow reversal is worth more than any entry threshold

**Changed 2026-09-09.** New `UseFlowReversalExit = true`, `FlowExitBars = 3`,
`FlowExitMinProfitPct = 0`. A position in profit is closed once every one of the last
three closed 15-minute buckets has leaned against it, without waiting for stop or
target. Exits are recorded as `FLOW_REVERSAL`.

### The finding this rests on

The entry rules are positive on raw forward returns and negative once packaged into a
trade. Simulating the shipped geometry — fee-floor stop at 0.40%, target at the
range boundary, stop resolved before target — over the same 1,562 buckets:

    stop width    LONG mean R    SHORT mean R
      0.4%          -0.255          -0.435
      0.6%          -0.183          -0.445
      0.8%          -0.206          -0.525
      1.2%          -0.098          -0.316

No stop width is positive. The defect is the hold, not the entry and not the stop
width: the near stop is reached far more often than the far target. Closing on the
flow rule instead:

    side    n    exits by flow   mean R with rule   mean R holding to TP/SL
    LONG   115        23              -0.154              -0.184
    SHORT   46        12              +0.055              -0.345

A cruder time-boxed cut agrees on direction — SHORT closed at 2 hours returns
-0.013R against -0.345R held to TP/SL — which is some evidence that the improvement
is about ending the hold rather than about OFI specifically.

Three buckets rather than the two first simulated, chosen by the operator on the
reasoning that two consecutive is a common enough coincidence to fire on noise. Not
swept; the sample cannot support choosing between 2, 3 and 4.

### What this does not claim

Flow still carries no usable direction — aggregate OFI correlates -0.015 with the
next hour's signed return, which is why entries are on price alone. This asks a
narrower question: not "which way next" but "is the pressure that was pushing this
position still there". The first question's failure is not evidence about the second,
and the second has not been tested on entries.

### Decision rule, fixed in advance

Evaluate when **30 trades have closed** or after **14 days**, whichever comes first.
Requires at least **8 closes with reason `FLOW_REVERSAL`** to be evaluable at all.

- **Keep** if mean R across all closed trades beats the pre-change simulated baseline
  of -0.255 (LONG) / -0.435 (SHORT), weighted by the actual side mix.
- **Revert to `UseFlowReversalExit: false`** if mean R does not beat that baseline, or
  if `FLOW_REVERSAL` exits average worse R than the `TP` exits they displaced — that
  would mean the rule is cutting winners rather than saving them.
- **Fewer than 8 `FLOW_REVERSAL` closes in 14 days** is its own verdict: the rule is
  too rare to matter and should come out rather than be loosened, since loosening it
  on the sample that produced it is not a test.
- **Do not extend this to losing positions** on this sample. Cutting losers early on
  flow is a separate rule with a separate failure mode and no measurement here.

### Cost of being wrong

None in money — `paper_mode = true`. The real cost is confounding: H5 and H6 shipped
together, so a 14-day window cannot attribute a change to one or the other. Mitigated
only partly by `close_reason`, which does separate what the exit rule did from what
the entry rule did. If the combined result is ambiguous, the honest next step is to
turn H6 off and re-run H5 alone rather than to reason about which half worked.

### Result

_Open._


### Correction, 2026-09-09 16:20 — the rule that ran was not the rule that was measured

The first shipped version read the last three closed buckets with no constraint on when
they closed. At the moment a long opens, those are the buckets that produced the entry:
"buy the dip" means "buy after price fell", price falls on selling, so the exit
condition is already satisfied before the position is a second old. The two rules are
near-negatives of each other by construction.

Observed live, twice inside twenty minutes:

    trade 62   opened 15:15:34   closed 15:16:04   buckets 14:30, 14:45, 15:00
    trade 63   opened 15:31:08   closed 15:31:38   buckets 14:45, 15:00, 15:15

Every one of those buckets closed before its own trade opened. Both exits banked
+0.14% — one 30-second cycle of drift — while a loser would still have paid the full
2.00% stop. Small wins and whole losses is worse than having no exit rule.

The simulation did carry the constraint (`r.bs > ok.b`: the run of buckets had to END
after the signal bucket). It was not carried into the code, so the +26.1R quoted above
described a rule that was never deployed. Nothing crashed and no log line was wrong,
which is the failure mode this repository keeps paying for.

Fixed by requiring the newest bucket in the window to have closed after
`trade.OpenedAt`. Earliest possible fire is now the first bucket close after entry,
about fifteen minutes.

**The H6 evaluation window therefore restarts.** Trades 62 and 63 are excluded — they
tested a rule nobody designed. Count from the deploy that carries this fix.

---

## H7 — The short side fades a spike, not a rally, so it gets its own window

**Changed 2026-09-09.** New `ReversalBarsShort = 1`. The rise that triggers a short is
now measured over a single closed 15-minute bar; the fall that triggers a long stays at
`ReversalBars = 2`. Previously one window served both sides.

### The gradient

Varying only the window the trigger move is measured over, across the 19-day production
window, with the shipped geometry and the H6 exit:

    window    SHORT n   mean R   1st half   2nd half     LONG n   mean R
      15m        17     +1.168    +1.467     +0.619        53     -0.213
      30m        46     +0.055    -0.014     +0.272       117     -0.151
       1h        91     -0.027    +0.009     -0.127       191     -0.195
       2h       166     -0.222    -0.154     -0.337       273     -0.215

Monotone on the short side, absent on the long side, and it has a mechanism: a 1% rise
inside a single 15-minute bar is a spike, usually a liquidation cascade, and fading a
spike is a different trade from fading a 1% rise that took two hours to build, which is
a trend. One window could not tell those apart, and the earlier finding that fading
rallies loses money in an uptrend (0 wins in 19 trades over the 26-27/08 rally) is the
same fact seen from the other side.

### Why the headline number is not the reason to believe it

Two of the seventeen trades carry the result:

    2026-08-22 04:30   move +4.34%   TP    +13.32R
    2026-08-28 15:15   move +1.38%   TP     +5.70R
    the other fifteen                       +0.83R   (mean +0.055R)

The +13.32R fell on the day SOL ranged 87.72 to 102.74 — 17% in one day. Its R:R of
13.32 is a restatement of that: the two-hour range was 6.7% wide. That is a market
event, not a repeatable edge, and stripping it takes the mean from +1.168 to +0.408;
stripping both takes it to +0.055, which is exactly what the 30-minute window already
produced.

The honest description is a fat-tailed, low-frequency setup: about one trade a day,
eight of seventeen stopped out, expectancy concentrated in rare large wins. That shape
needs far more evidence than a normal one. The second half of the sample carries no
outlier and returns +0.619R over six trades, which is encouraging and is not a sample.

Shipped on the operator's decision with all of the above stated.

### What else this changes

Both sides can now fire on the same evaluation, which was impossible while one window
served both: a 30-minute fall of 0.60% whose most recent bar rose 1.00% satisfies each
rule on its own evidence. The shorter window wins, on the same reasoning that motivates
the change — a dip whose last bar has already been reclaimed is a dip that has
finished. The verdict says so in its reason string rather than resolving it silently.

### Decision rule, fixed in advance

Evaluate when **25 SHORT trades have closed** or after **21 days**, whichever comes
first, on trades opened after this shipped.

- **Keep** if mean R over closed SHORT trades is positive **after discarding the single
  largest winning R**. The discard is not conservatism, it is the specific failure this
  entry is exposed to, and it must be fixed in advance or the first outlier will be
  read as confirmation.
- **Revert to `ReversalBarsShort = 2`** if that trimmed mean is negative, or if fewer
  than 12 SHORT trades have been taken in 21 days — the 15-minute window is expected to
  fire about once a day, and materially less than that means the threshold and the
  window together are too rare to evaluate.
- **Do not sweep the window.** 15m was chosen off a four-point gradient with a
  mechanism, not off an argmax. A fifth point needs its own entry.

### Cost of being wrong

None in money — `paper_mode = true`. The cost is that H5, H6 and H7 are now all open on
overlapping windows and all touch the short side. H5's threshold and H7's window cannot
be told apart by outcome alone; if the short side fails, the honest next step is to
revert H7 first, since it is the newer and thinner of the two, and re-run H5 on its own.

### Result

_Open._

---

## H8 — The stop was inside the noise, and that is where the money went

**Changed 2026-09-09.** New `MinStopPct = 0.020`, applied as a floor alongside the fee
floor rather than replacing it. The two are kept separate because they are different
claims: the fee floor is a property of the exchange, the noise floor a property of SOL.

### What was actually happening

Every trade this bot has ever taken carries `stop_pct` of exactly 0.400 -- the fee floor,
`roundTripFeeRate x MinStopAsFeeMultiple` = 0.001 x 4. The range low that
`UseRangeGeometry` claims to place the stop on has never once been reached, because the
entry rule buys after a fall and therefore enters near the range low by construction.
"Range geometry" in practice meant a fixed 0.40% stop with a range-high target.

SOL's median 15-minute true range is 1.07%. A 0.40% stop sits well inside it, so it is
not a barrier that fires when the trade is wrong -- it fires when nothing has happened.
87 of 115 long signals were stopped out.

### Measured, total P&L as a percent of notional over the 19-day window

    stop     total    less the biggest trade    1st half    2nd half
    0.40%    + 1.07        - 5.59                + 5.28      - 4.21
    0.60%    + 0.26        - 6.40                + 0.61      - 0.35
    0.80%    - 5.60        -12.26                - 4.58      - 1.03
    1.20%    + 4.57        - 2.09                - 1.05      + 5.62
    1.60%    +12.15        + 5.49                + 4.15      + 8.00
    2.00%    +15.94        + 9.28                + 2.92      +13.02
    2.40%    +10.35        + 3.69                + 1.63      + 8.72

1.60, 2.00 and 2.40 pass all three checks. Nothing narrower passes two. A plateau
rather than a peak is the reason to believe it: every other parameter swept in this
session produced a good cell sitting between bad ones.

Position size falls as the stop widens -- notional = capital x risk / stop -- so risk
per trade is unchanged. In money at constant fractional risk the sample returns about
3.5x what 0.40% did. Signal count falls from 134 to 102, since a wider stop pushes more
setups under MinRewardRisk. That is the intended trade.

An earlier sweep in the same session found no stop width positive. It was run before
the H6 flow exit and the H7 short window existed, and it measured mean R rather than
money -- mean R is not comparable across stop widths, because 1R is the stop. Both
defects are corrected here.

### Why a wider stop is defensible now and was not before

The H6 flow exit gives the position an active way out, so the stop is a backstop rather
than the primary exit. That argument has one hole worth stating: the flow exit only
fires on a position in profit, so a trade that goes against it from the first minute
never meets that rule and rides the full 2.00% to the stop. Risk per trade is unchanged
in dollars, but the price distance is five times what it was.

### Decision rule, fixed in advance

Evaluate when **40 trades have closed** or after **21 days**, whichever comes first.

- **Keep** if total P&L as a fraction of risked capital beats the 0.40% baseline over
  the same window, **after discarding the single largest winning trade**.
- **Revert to `MinStopPct: null`** if it does not, or if fewer than 25 trades are taken
  in 21 days -- a floor that pushes most setups under MinRewardRisk has replaced the
  strategy with a different, rarer one, and that needs its own evaluation rather than
  inheriting this one.
- **Do not sweep between 1.6 and 2.4.** The plateau is the finding; picking its argmax
  is how a plateau gets turned back into a point.

### Cost of being wrong

None in money -- `paper_mode = true`. The real cost is that H5, H6, H7 and now H8 are
all open at once, and H8 changes the geometry every one of the others was measured
against. Their decision rules quote R against baselines computed at a 0.40% stop, and
those baselines no longer describe what is running. If the combined result is
ambiguous, H8 is the one to revert first: it is the newest, it moves the most, and the
other three were at least measured under the geometry they shipped with.

### Result

_Open._

---

## H9 — Enter with the tape, not against it

**Changed 2026-09-10.** `EntryMode: CandleReversal -> FlowRatio`. The entry rule is
replaced, not filtered. Price is no longer consulted at all; the bot enters WITH the
side that dominated the last closed 15-minute bucket, when it dominated by at least
`RatioMinimum` (2.1:1) on at least `RatioMinVolumeUsd` ($3M) of notional, after waiting
`RatioSettleMinutes` (3) for the bucket to settle.

`UseRangeGeometry` goes to false with it. Entering with the dominant side puts price at
the edge of its own range, so a range-boundary target lands on top of the entry and
fails MinRewardRisk — only 3 of 92 signals survived it. The ATR path with the 2.00%
noise floor and TargetRiskMultiple 2.0 gives the 2%/4% pair this was measured on.

### Where it came from

The operator read the raw per-bucket tape across five windows they chose themselves and
observed that the bot enters against the dominant side. That is measurably true: 138 of
155 CandleReversal signals entered against the last closed bucket's flow, because buying
a fall means buying while the tape sells.

### Measured, 1,562 production buckets, timeouts priced at the exit

    ratio    n     mean R   less top 1   1st half   2nd half
     1.8    200    +0.043     +0.034      +0.045     +0.042
     2.1     92    +0.185     +0.166      +0.457     +0.083
     2.5     31    +0.278     +0.225      +0.380     +0.258
     3.0     12    +0.027     -0.140      +0.011     +0.034

Three adjacent ratios positive on every column. 2.1 is the operator's choice and sits in
the middle of the plateau, not at its argmax.

The volume floor is a separate condition, measured at a fixed 2.1 ratio:

    total       n    mean R   less top 1   1st half   2nd half
    under $3M   50   +0.025     -0.012      -0.082     +0.048
    $3-6M       30   +0.383     +0.332      +0.521     +0.303
    $6-12M      10   +0.212     +0.030      +1.143     -0.408
    over $12M    2   +1.065        —           —          —

Hold time is a plateau, not a peak: +0.032 / +0.082 / +0.273 / +0.346 / +0.379 / +0.330
at 1, 2, 4, 6, 12 and 24 hours, every one positive in both halves and after discarding
the largest winner. The shipped 720 minutes is the peak and the rule is insensitive to
it — which is the opposite of what a curve-fit looks like.

Long/short balance: 19 long and 21 short over the sample, against CandleReversal's 104
and 13. The old rule was structurally biased toward buying; this one is not.

### Why this does not contradict "flow has no direction"

[[flow-has-no-direction-price-does]] measured aggregate OFI at -0.015 against the next
hour's signed return over 1,496 buckets. That is an average across the whole
distribution. This rule reads only the extreme tail: a 2.1:1 imbalance occurs in about
5% of buckets and, with the volume floor, in about 3%. An average of zero constrains a
tail very little, and the tail had never been measured on its own.

### The one earlier defect this design avoids

CandleReversal entered against the flow, which made the H6 flow-reversal exit fire
immediately — the exit condition was already satisfied at entry. FlowRatio enters WITH
the flow, so the exit only fires when the tape genuinely turns. The two rules are now
coherent rather than near-negatives of each other.

### Decision rule, fixed in advance

Evaluate when **30 trades have closed** or after **21 days**, whichever comes first.

- **Keep** if mean R is positive after discarding the single largest winner AND at
  least 12 trades have been taken. The discard is mandatory: this project has twice
  produced a "finding" that was one flash crash.
- **Revert to `CandleReversal`** if mean R after that discard is negative, or if fewer
  than 12 trades occur in 21 days. Expected rate is 2.6/day before the volume floor and
  about 0.7/day after it; materially less means the floors are wrong for live
  conditions.
- **Do not sweep the ratio or the volume floor.** Both were chosen from plateaus. A new
  value needs its own entry.

### Cost of being wrong

None in money — `paper_mode` is true. The real cost is that H5, H6 and H7 die here: they
are hypotheses about CandleReversal's entry, and CandleReversal is no longer running.
Their windows are void. H8's stop floor survives and is now load-bearing for a different
rule than it was measured on.

That is the operator's explicit choice, made against a recommendation to run both
strategies in parallel so the market could arbitrate. Recorded because a future session
will otherwise read the abandoned H5/H7 windows as failures rather than as cancelled.

### Result

_Open._

---

## H6 — RETIRED 2026-09-10, removed from the code

The flow-reversal exit is gone, on the operator's instruction, along with
`UseFlowReversalExit`, `FlowExitBars` and `FlowExitMinProfitPct`.

It never got a fair test. It shipped on 2026-09-09 with a defect that let it read the
buckets which had caused its own entry, closed two live trades thirty seconds after
opening them, and was fixed the next day. In the twenty hours it then ran under the fix
it fired exactly once, at minute 705 of a 720-minute hold, for +0.39% against the
+0.34% that simply timing out would have produced. One firing, worth 0.05%.

The +26.1R and the -49.4R to -15.2R improvement quoted for it were simulated, never
observed. They described the rule WITH the freshness guard, which only existed in SQL
until the last day of its life.

What it leaves behind, and what is worth keeping:

  - Flow measured AFTER entry does grade outcomes, monotonically and on decent counts:
    with the trade +0.165 (n 53), mildly against -0.190 (n 24), moderately against
    -0.403 (n 21). That is the strongest relationship found in two days of searching
    and it came from the operator, not from a sweep.
  - The rule as built could never act on the -0.403 group, because it was profit-only
    and those trades are never in profit. It cured the patients who were going to live.
  - Cutting losers on one-sided flow was therefore always the interesting half, and it
    was never built or measured.

If an exit rule on flow is revisited, start from the loser half, and note that under
FlowRatio the entry is WITH the tape — so a reversal exit is coherent there in a way it
never was under CandleReversal, where entry and exit were near-negatives of each other.

### Result

_Retired untested._

---

## H10 — Exit on a 10-bucket imbalance reversal (shipped against the evidence)

**Changed 2026-09-10.** New `UseFlowOfiExit = true`, `FlowOfiBars = 10`. Sum buy and
sell notional over the last ten closed buckets, take the imbalance of those sums, and
close the position when it points against the trade — provided it favoured the trade
at some earlier point in the hold.

**This is the first change in this file shipped on evidence that points the other way.**
Recorded plainly so the 21-day window is read for what it is.

### What was measured, and how many ways

Against no exit rule at all, on 42 FlowRatio signals:

    window    mean R with rule       no rule: +0.379
     8 bars        +0.391
    10 bars        +0.410   <- shipped
    12 bars        +0.340
    15 bars        +0.318
    20 bars        +0.346
    30 bars        +0.338

The shipped cell is the best of six and beats the baseline by 0.031R. The six values
scatter from 0.318 to 0.410 with the baseline sitting in the middle of that range, and
10 and 12 bars — thirty minutes apart — differ by 0.070R, more than the claimed effect.
Adjacent settings that disagree by more than the effect are measuring noise.

Without the "must have favoured the trade first" guard, at 20 bars, it was +0.303. The
guard exists because 8 of 42 signals had the imbalance already against them at entry,
and those would be closed fifteen minutes in on evidence predating the trade — the same
defect that closed two live positions thirty seconds after opening them on 2026-09-09.

### The operator's argument, and what happened when it was measured

Slot turnover: a 150-minute hold frees the single per-side slot sooner than a 12-hour
one, so more signals get taken. That mechanism is real and had been missing from every
earlier measurement in this file. Measured with the position limit applied:

    with rule      27 trades taken, 15 blocked, total +9.29R
    without        24 trades taken, 18 blocked, total +10.22R

Three extra trades worth about +1.0R, against 0.082R lost on each of the other
twenty-four. Net -0.93R — itself inside the noise of a 27-trade sample. The honest
statement is not that the rule hurts but that it has not shown a sign of helping on any
of three independent measures: mean R per trade, total R with the slot limit, and the
shape of the window curve.

### The mechanism that argues against it

The hold-time curve for FlowRatio: +0.032 at one hour, +0.082 at two, +0.273 at four,
+0.379 at twelve, +0.330 at twenty-four. This strategy earns by holding. A rule that
ends the hold early works against its own source of return, which is the simplest
explanation for why six window lengths all failed to beat doing nothing.

### Decision rule, fixed in advance

Evaluate when **30 trades have closed** or after **21 days**, whichever comes first.

- **Keep** only if total R over the window is positive AND at least 8 trades closed with
  reason `OFI_REVERSAL` AND those trades' mean R is above the mean R of the `TIMEOUT`
  trades in the same window. All three, because the rule's whole claim is that ending
  the hold early beats letting it run.
- **Revert to `UseFlowOfiExit: false`** otherwise. This is the default expectation given
  the evidence above, and reverting is one config value.
- **Do not sweep FlowOfiBars again.** Six values were measured across a flat, scattered
  curve. A seventh will eventually look good and will not be real.

### Cost of being wrong

None in money — `paper_mode` is true. The cost is the window: 21 days of FlowRatio
evidence collected under an exit rule that the pre-shipping measurement says subtracts
about 0.9R per 16 days. If H9 comes out marginal, this rule is the first thing to
remove before concluding anything about the entry.

### Result

_Open._

---

## H11 — A bucket large enough is its own signal (shipped against the evidence)

**Shipped 2026-09-11.** `FlowSignalOptions.RatioHighVolumeUsd = 20_000_000`. Set it to
`0` to remove the rule; nothing else changes.

### The claim

A news print produces a bucket so large that the event itself is the signal. Waiting for
a 2.1:1 lean misses it, because a stampede has size on both sides. So above $20M of
notional in one closed 15-minute bucket, skip the ratio test and enter with whichever
side traded more.

### The mechanism is real

Ratio falls as volume rises — mean 1.40 below $5M against 1.19–1.31 above $30M — so the
two rules are very nearly disjoint. Of the 42 buckets over $20M in the sample, exactly
**one** also cleared 2.1:1. This rule reaches trades the ratio rule structurally cannot,
which is the strongest thing that can be said for it.

### The measurement, which does not support it

1,770 buckets, 2026-08-21 to 2026-09-11. Entry 18 minutes after the bucket opens (its
close plus the 3-minute settle wait), signed to the heavier side, held 12 hours.

    threshold    n     +1h      +12h    1st half   2nd half   less top 1
      12M      112   -0.162   -0.035    +0.013     -0.182      -0.104
      15M       76   +0.062   -0.032    +0.076     -0.330      -0.137
      20M       41   +0.160   +0.141    +0.276     -0.341      -0.054
      25M       25   +0.004   -0.174    -0.166     -0.197      -0.527
      30M       18   -0.048   -0.239    -0.252     -0.201      -0.823

Against the three checks this repository applies to everything:

- **Plateau — fails.** 20M is one positive cell between two negative ones. Its
  neighbours are each further from it than it is from zero.
- **Both halves — fails.** The second half is negative at every threshold.
- **Minus the best trade — fails.** Negative at every threshold, 20M included. The
  positive mean is one trade.

The rule that already runs, measured identically on the same rows: n 45, **+0.838%** at
12h, 56.8% hit, +1.631 / +0.385 across halves, **+0.712** after removing its best trade.
It passes all three.

A weaker ratio filter on top does not rescue it. Within the $20M buckets, by ratio band:
<1.2 → +0.202, 1.2–1.4 → +0.200, 1.4–1.6 → +0.866, **≥1.6 → −1.004**. The gradient runs
the wrong way and every band flips sign between halves, on 6–17 observations each.

### The one thing worth watching

**+0.160% at one hour** is the only good number, and it beats the ratio rule's +0.139% at
the same horizon. If there is anything here it is a fast trade. The machinery around it
holds for 12 hours and pays roughly 63 bps of funding to do so — which is more than a
14 bps twelve-hour edge earns. This rule may be being judged on the wrong exits.

### Decision rule, fixed in advance

Evaluate when **20 trades have entered through this path** or after **21 days**,
whichever comes first. Trades are identifiable by an entry rationale containing
`news-print threshold`.

- **Keep** only if those trades' total R is positive AND remains positive after
  discarding the single best one. The second condition is not optional: it is the exact
  check the pre-shipping measurement failed, so keeping the rule on a result that fails
  it again would be ignoring the same evidence twice.
- **Remove** (`RatioHighVolumeUsd: 0`) otherwise. This is the expected outcome.
- **Do not sweep the threshold.** Five values were measured across a scattered curve with
  one positive cell. A sixth will eventually look good and will not be real. If the rule
  is to be revisited, revisit the **exit** — the 1-hour figure — not the entry threshold.

### Cost of being wrong

None in money; `paper_mode` is true. The cost is contamination of H9. This path adds
roughly two entries a day against FlowRatio's current 2.5, so within days most closed
trades will have come through a rule measured to be negative, and H9's 30-trade bar will
be reached by a mixed population. **Judge H9 only on trades whose rationale does not
mention the news-print threshold**, or remove this rule first and let H9 finish clean.

### Addendum, 2026-09-11 — measured again on a 10-bar hold, and it changes the verdict

The first measurement judged this rule on the shipped 12-hour hold and it failed two of
three checks. Re-simulated properly — entry 18 min after the bucket opens, stop and
target walked against 1-minute OHLC with the stop taken when a minute spans both, costs
7 bps round trip plus measured funding per hour, R against the 2.00% stop:

    hold 2.5h (10 bars), fixed 2%/4%
    rule            n    meanR    win%    1st half  2nd half  less top 1
    vol >= 10M    154   +0.057   45.5%    +0.048    +0.083      +0.045
    vol >= 20M     41   +0.206   53.7%    +0.179    +0.280      +0.162
    vol >= 30M     18   +0.125   50.0%    +0.084    +0.206      +0.017
    vol >= 40M      9   +0.320   55.6%    +0.336    +0.300      +0.115

All four thresholds are now positive on all three checks, where at 12 hours every one of
them failed the outlier check. The hold was the defect, not the entry.

And 10 bars is a plateau rather than an argmax:

    bars   hold    meanR    1st half  2nd half  less top 1
      4    1.0h   +0.072    +0.087    +0.031      +0.025
      6    1.5h   +0.085    +0.044    +0.196      +0.038
      8    2.0h   +0.167    +0.131    +0.265      +0.122
     10    2.5h   +0.206    +0.179    +0.280      +0.162
     12    3.0h   +0.203    +0.176    +0.278      +0.159
     16    4.0h   +0.095    +0.036    +0.255      +0.048
     24    6.0h   +0.198    +0.156    +0.313      +0.154
     48   12.0h   +0.137    +0.144    +0.120      +0.092

8, 10 and 12 bars agree within 0.04R with both halves positive throughout. Note what the
exits actually are at 2.5h: 30 of 41 close on TIMEOUT against 7 stops and 4 targets, so
the barriers barely participate. This rule is "hold 2.5 hours and take what is there",
not a stop-and-target trade.

### Why the hold was NOT changed

The two rules want opposite holds, and the same sweep run on the rule already deployed
makes that unmistakable — it rises monotonically with hold length:

    bars    vol >= 20M    3M & 2.1x
      10      +0.206        +0.120   (2nd half -0.003)
      48      +0.137        +0.382   (2nd half +0.149)

`max_hold_minutes` is one global value read by StrategyEvaluator before any strategy is
consulted, so setting it to 150 would cut the deployed rule from +0.382R to +0.120R and
push its second half negative. Honouring both needs a per-trade hold, which is a real
change and was deliberately not made on the strength of one 21-day window. **Operator
decision 2026-09-11: record the numbers, leave the hold at 720 minutes.** The news-print
entries therefore run on a 12-hour cap that this measurement says is the wrong one for
them, and H11 will be judged accordingly — see the decision rule above, which is
unchanged.

### Dynamic R:R, measured and then enabled anyway

    config                    fixed  ->  dynamic
    2.5h  vol >= 10M         +0.057  ->  +0.007
    2.5h  vol >= 20M         +0.206  ->  +0.098
    2.5h  vol >= 30M         +0.125  ->  +0.155
    2.5h  vol >= 40M         +0.320  ->  +0.292
    12h   vol >= 10M         +0.133  ->  +0.024
    12h   vol >= 20M         +0.137  ->  -0.057
    12h   vol >= 30M         -0.044  ->  -0.098
    12h   3M & 2.1x          +0.382  ->  +0.353

Worse in seven of eight cells, and the mechanism is visible in the exit mix rather than
inferred: at 12h/20M it takes TP closes from 12 down to 2 and pushes TIMEOUT from 12 to
24. Scaling the target by 1 + 10x excursion makes the target RETREAT as price advances
toward it, so winners are converted into timeouts. The stop half meanwhile widens against
a position sized at entry for a 2.00% stop, so a full stop-out can cost up to twice
risk_pct_per_trade.

**Decision taken 2026-09-11 to enable it globally, with all of the above shown; the
switch still has to be flipped in `bot_config`.**
H12 below carries its decision rule.

### Correction, same day — the hold measurement above answered the wrong question

"Close after 10 candles" was read as a 150-minute time cap. It was not: it meant the exit
that already exists — H10's rule, which sums buy and sell notional over the last
`FlowOfiBars` (10) closed buckets and closes when the imbalance of those sums turns
against the position. That rule is implemented, deployed (`UseFlowOfiExit: true`) and has
fired three times live as `OFI_REVERSAL`. **Nothing needed building; the hold sweep above
measured a cap nobody asked for.**

Re-measured with the real exit — stop 2%, target 4%, the 10-bucket OFI reversal, 12-hour
cap, a 2-minute action lag for the aggregation worker, and the live `wasFavourable` guard:

    rule                  n    meanR   win%   1st half  2nd half  less top   SL/ TP/OFI/ TO
    -- 12h cap only, no OFI exit
    news-print >= 10M   154   +0.133  46.8%   +0.151    +0.083    +0.121     56/ 36/  0/ 62
    news-print >= 20M    41   +0.137  41.5%   +0.144    +0.120    +0.092     17/ 12/  0/ 12
    3M & 2.1x            45   +0.382  53.3%   +0.767    +0.149    +0.346      9/ 11/  0/ 25
    -- with the 10-bucket OFI exit
    news-print >= 10M   154   +0.223  51.3%   +0.240    +0.177    +0.212     22/ 23/102/  7
    news-print >= 20M    41   +0.312  58.5%   +0.304    +0.336    +0.271      8/  8/ 24/  1
    news-print >= 30M    18   +0.097  44.4%   +0.027    +0.238    -0.013      5/  3/  9/  1
    news-print >= 40M     9   +0.242  55.6%   +0.169    +0.333    +0.027      2/  2/  4/  1
    3M & 2.1x            45   +0.385  53.3%   +0.679    +0.207    +0.349      0/  8/ 35/  2

**This is the best-supported result measured in this repository.** At $20M the rule
returns +0.312R with the halves within 0.03R of each other (+0.304 / +0.336) and 87% of
the mean surviving removal of the best single trade. It passes all three checks with room,
which neither the 12-hour version (+0.137, fails the outlier check) nor the 150-minute cap
(+0.206) did. The one blemish is that $30M dips to +0.097 between two strong neighbours,
on 18 observations — so this is not the clean plateau the 8/10/12-bar hold sweep was.

### And a finding about H10 itself

The OFI exit is worth almost nothing to the rule it was measured on, and a great deal to
the rule it was not:

    3M & 2.1x        +0.382  ->  +0.385     (+0.003)
    news-print 20M   +0.137  ->  +0.312     (+0.175)

H10 shipped against its own evidence because, on the ratio rule, six window lengths all
failed to beat doing nothing — and that reading was correct. One mechanism explains both
halves: the ratio rule enters on an extreme imbalance, so by the time flow reverses the
information is largely spent; the news-print rule enters on near-balanced flow at high
volume, where a later 10-bucket reversal is genuinely new information. Note also what it
does to stop-outs — 17 down to 8 at 20M, and 9 down to 0 on the ratio rule. It is getting
out before the stop, not instead of the target.

**H10 must not be removed while H11 is open.** That reverses the instruction in H10's own
decision rule ("if H9 comes out marginal, remove H10 first"), which was written when the
ratio rule was its only consumer. Recorded here rather than edited there, because the
rules at the top of this file forbid editing a decision rule after the fact.

### Concurrency: one high-volume position at a time

Added 2026-09-12 on the operator's instruction, before H11 opened.
`bot_config.max_open_high_volume = 1`; 0 disables the cap.

The existing limits do not cover this. `max_open_trades_per_strategy` is 2 and
`max_open_per_side` is 1, which reads as "two positions, never two the same way" — and
both sides of a single macro print qualify for the waiver, so that pair happily admits a
LONG and a SHORT fifteen minutes apart on one event. The qualifying buckets cluster hard:
of the nine above $40M in the sample, two pairs were fifteen minutes apart and five of
the nine fell on three days. Without a per-rule cap the waiver can put a whole day's risk
on one print, which is the single most likely failure mode of a rule built to trade news.

The rule is enforced on a column, `bot_trades.entry_path`, set on the INSERT rather than
by a follow-up UPDATE and read back by `GetOpenTradesAsync`. Not parsed out of
`entry_rationale`: the last branch in this codebase driven by matching prose was the
gate's "unavailable" state, recognised by the first two words of its reason string, which
silently missed four of six paths and blocked a live entry by a rule the operator had
switched off. NULL means "not recorded" and the count matches only rows that positively
say HIGH_VOLUME, so an unmarked position can never quietly consume the slot.

It also makes H9 and H11 separable. The two rules share a strategy name, a position book
and a trade stream; without the column, H11's 20-trade bar cannot be counted and H9's
30-trade bar would be reached by a mixed population.

### The guard is load-bearing

The deployed rule closes only when the imbalance turns against a position it had
previously favoured. Dropping that guard — closing on the sign of the sum alone, which is
the simpler rule as usually described — is worse everywhere:

    rule               guarded   sign only
    news-print >= 10M  +0.223    +0.189
    news-print >= 20M  +0.312    +0.240
    news-print >= 40M  +0.242    +0.209
    3M & 2.1x          +0.385    +0.329

The implemented version is already the better variant. Do not "simplify" it to the sign
test.

### Result

_Open._

---

## H12 — Dynamic TP/SL, enabled WITH the measurement (the first version of this entry had it backwards)

### Correction, 2026-09-11, before this was ever evaluated

Everything below the next heading was written on a measurement that omitted the OFI exit,
and it is wrong. Dynamic TP/SL was scored against a 12-hour cap and a 150-minute cap, and
came out worse in seven of eight cells. Neither of those is the deployed configuration:
`UseFlowOfiExit` is true, and the OFI reversal closes 35 of 45 trades on the live rule.
Judging a barrier rule on a configuration where the barriers do most of the closing, when
in production they do not, measures the wrong thing.

Re-measured with the exit that actually runs, R against the ORIGINAL 2% stop so that
widening is charged for the risk it adds:

    rule / barriers              n    meanR   win%   1st half  2nd half  less top  SL/ TP/OFI/ TO
    -- with the OFI exit, which is production
    3M & 2.1x    fixed          45   +0.385  53.3%   +0.679    +0.207    +0.349    0/  8/ 35/  2
    3M & 2.1x    DYNAMIC        45   +0.419  53.3%   +0.765    +0.208    +0.354    0/  1/ 41/  3
    news 20M     fixed          41   +0.312  58.5%   +0.304    +0.336    +0.271    8/  8/ 24/  1
    news 20M     DYNAMIC        41   +0.198  56.1%   +0.220    +0.136    +0.124    9/  2/ 29/  1
    -- without it, which is what the first version measured
    3M & 2.1x    fixed          45   +0.382  53.3%   +0.767    +0.149    +0.346    9/ 11/  0/ 25
    3M & 2.1x    DYNAMIC        45   +0.353  53.3%   +0.756    +0.109    +0.288    6/  1/  0/ 38

**On the deployed rule, dynamic is better on every column** — overall, both halves, and
after discarding the best trade. It passes all three checks.

This independently reproduces the 2x2x2 grid the operator relied on when they chose to
keep the feature on 2026-09-10: that grid gave dynamic+OFI +0.448 against fixed+OFI
+0.410, a gap of +0.038; this gives +0.419 against +0.385, a gap of +0.034. Same
direction, same magnitude, measured a day later by a different route. The operator's
decision was sound and the "against the measurement" framing below was my error.

The mechanism, now that both halves of the table exist: widening the target makes it
RETREAT as price advances, which strands winners — TP closes fall from 11 to 1 without
the OFI exit. With the OFI exit those trades are not stranded, they close on flow instead
(35 to 41), so the cost disappears and the wider stop's benefit remains.

**It does NOT transfer to the news-print rule**, which loses +0.114R to it: that rule
was not stopping out much, so widening the stop buys nothing, while losing targets
costs (TP 8 to 2). `use_dynamic_tp_sl` is one global switch, so if H11 ships the two
rules will want opposite settings. Same shape as the finding about the OFI exit itself:
a feature that pays for one entry rule and not the other.

**The gap is +0.034R on n=45 and is inside the noise of that sample.** What the
measurement supports is "not harmful, probably mildly positive on the deployed rule",
not a demonstrated edge.

---

## What the first version of this entry said, kept for the record

**Decided 2026-09-11. NOT YET IN FORCE at the time of writing** — `use_dynamic_tp_sl` was
still `false` in `bot_config`. This hypothesis opens when the UPDATE below is run, not
when this section was committed. Check the column before reading any result here.

```sql
UPDATE bot_config SET use_dynamic_tp_sl = true, updated_at = now() WHERE id = 1;
```

The bot polls `bot_config` every 30 seconds, so it takes effect without a restart, and
setting it back to `false` is the whole of the revert.

Worth writing down because it was misremembered as already on: the CODE for dynamic TP/SL
was made real on 2026-09-10 in `a9cc21a` — before that it scaled two percentages only the
no-geometry fallback reads, so it had never done anything — and after the 2x2x2 grid the
operator decided to KEEP it. "Keep" meant keep the implementation. The switch was never
turned on. The feature has been present, correct and dormant since, which from outside
looks exactly like a feature that is running.

### The claim being tested

That scaling both barriers outward by `1 + 10x favourable excursion`, capped at 2x, earns
more than leaving them fixed — because a position that has already travelled is in a
market moving further than the entry assumed.

### The evidence as it was first (mis)measured

The eight-cell table in H11 above: worse in seven, and the one improvement (2.5h/30M) is
on 18 observations. The measured mechanism is that the target retreats faster than price
advances, so it converts winners into timeouts.

### Decision rule — REPLACED 2026-09-11, before this hypothesis opened

The rule first written here was: keep only if total R is positive AND the share of `TP`
closes does not fall below 24%. **That second condition is backwards.** The corrected
measurement shows TP closes dropping from 8 to 1 in the configuration where dynamic is
BETTER — the trades are not lost, they close on the OFI reversal instead, and they close
further ahead. A condition designed to catch a failure mode would have failed the feature
for doing the thing that makes it work.

Replaced rather than kept because this entry has not opened: `use_dynamic_tp_sl` is still
false, so no data has been collected under it and nothing is being edited after a result.
The rule at the top of this file forbids editing a decision rule after the fact, which is
not this. If the switch has been flipped by the time you read this, the rule below is
frozen.

Evaluate when **30 trades have closed** with it on, or after **21 days**.

- **Judge it by replaying the window, not by its total R.** The feature is stateless and
  recomputed from stored levels every cycle, so the counterfactual is exactly computable:
  take the signals from the new window and simulate them with the scaling on and off.
  Keep it if the on-version is ahead on mean R, in both halves of the new window, and
  after discarding the best single trade.
- **Revert to false** if the off-version wins on any of those three.
- **Do not judge it on live total R alone.** The measured effect is +0.034R on n=45,
  comfortably inside the noise of that sample; at roughly 2.5 trades a day a live window
  cannot resolve it either, so a positive total R would be evidence about the entry rule
  and not about this switch.
- **Re-open it if H11 ships.** The news-print rule loses +0.114R to this setting, and one
  global switch cannot serve both. Whichever rule is producing most of the trades decides.

### Cost of being wrong

None in money; `paper_mode` is true. In evidence, it is expensive: it lands on top of H9
and H11 and changes the exit for every trade both of them are judged on. Three open
hypotheses now share one trade stream, and the 21-day windows overlap. If all three come
out marginal, nothing here will be separable — remove H12 first, then H11, and let H9
finish on its own terms.

### Result

_Open._

---

## H13 — CandleReversal in parallel: buy the dip, short the spike (shipped against the measurement)

**Registered 2026-09-12.** A second `CrossVenueFlowStrategy` instance named
`CANDLE_REVERSAL`, bound to the `DipStrategy` section. Registered but **not live until**
its name is added to `bot_config.active_strategies`:

```sql
UPDATE bot_config SET active_strategies = '{XVENUE_FLOW,CANDLE_REVERSAL}', updated_at = now() WHERE id = 1;
-- revert:
UPDATE bot_config SET active_strategies = '{XVENUE_FLOW}', updated_at = now() WHERE id = 1;
```

Deliberately not seeded by a migration. Migrations run on deploy, so seeding it would
turn "register the rule" into "start trading it" without anyone choosing to.

### The rule

LONG when price has fallen >= 0.60% over 2 closed 15-minute bars. SHORT when it has
risen >= 1.00% over 1 closed bar. Price only; no order flow is read. Both windows and
both thresholds are the values the rule was retired with — see `ReversalBarsShort` for
why the two sides are not mirror images.

### The measurement, which does not support it

1,770 buckets, 2026-08-21 to 09-11, simulated with the **deployed exit set** — stop 2%,
target 4%, the 10-bucket OFI reversal with its guard, 12-hour cap, costs 7 bps plus
funding. R against the 2% stop.

    config                     n    meanR   win%   1st half  2nd half  less top 1
    fixed, no OFI exit       159   -0.057  40.9%   -0.078    -0.031     -0.070
    fixed, with OFI exit     159   -0.039  45.3%   -0.051    -0.024     -0.052
    dynamic, with OFI exit   159   -0.005  45.3%   -0.006    -0.005     -0.026
    long only, with OFI      138   -0.063  44.2%   -0.080    -0.044     -0.078

**Negative overall, negative in both halves, and negative after discarding the best
trade — in all four configurations.** Nothing else measured in this repository has failed
all three checks this consistently. For comparison, on the same rows and the same
arithmetic: FlowRatio 2.1x **+0.385**, the news-print waiver **+0.312**.

Split by side, which matters because the request was specifically for the dip:

    LONG  (the dip)    n=138   -0.028 to -0.063 depending on config
    SHORT (the spike)  n= 21   +0.013 to +0.146

**The dip-buying half is the losing half.** The short side is mildly positive on 21
observations, which is not enough to conclude anything and is the opposite of where the
interest was.

This is consistent with what was already on record: the FATAL IN A TREND note has this
rule taking **0 wins in 26 trades** across both trending periods, losing by buying
falling knives. Nothing here contradicts that; it measures the same thing on a longer
window with better exits and finds it smaller but still negative.

### The cadence problem, which is separate and larger

159 signals in 21 days is **~7.6 a day**, against FlowRatio's ~2. Position limits and the
15-minute cooldown will stop most of them becoming trades, but whatever gets through is
the majority of the stream. Four hypotheses are already open on that one stream — H9
(the ratio entry), H10 (the OFI exit), H11 (the news-print waiver), H12 (dynamic
barriers). Adding a fifth that outnumbers all of them 3:1 means none of the five can be
attributed, and the rule at the top of this file — one live change at a time — is already
being broken four ways.

If this is enabled, H9 and H11 should be read only on rows where
`strategy = 'XVENUE_FLOW'`, which `bot_trades.strategy` makes a one-predicate query.

### Decision rule, fixed in advance

Evaluate when **40 CANDLE_REVERSAL trades have closed**, or after **21 days**.

- **Keep** only if mean R over those trades is positive AND positive in both halves of
  the window AND positive after discarding the best single trade. The same three checks
  it has just failed four times; anything weaker would be accepting a result the
  pre-measurement already predicted would not hold.
- **Remove it from `active_strategies`** otherwise. This is the expected outcome.
- **If only the short side is positive**, do not conclude the short side works. n=21 in
  the pre-measurement, and `ReversalLongOnly` exists to test that separately with its own
  entry here.
- **Do not tune `ReversalDropPct` or `ReversalRisePct` in response to a negative result.**
  The bands below 0.60% were already measured flipping sign between halves, and the
  budget note applies: roughly eighty configurations have been measured against this
  window.

### Cost of being wrong

None in money; `paper_mode` is true. The cost is evidence: at 7.6 signals a day this rule
determines what the next three weeks of trade data is about, and the four open hypotheses
were all opened on the assumption that FlowRatio produces the stream.

### Result

_Open._

---

## H14 — Widen the OFI exit window from 10 buckets to 20

### The change

`FlowStrategy:FlowOfiBars` 10 → 20, for XVENUE_FLOW only. `DipStrategy` stays at 10.
Nothing else moves: entry rule, thresholds, stop floor, target multiple and
`max_hold_minutes` are all unchanged.

### Why, from production rather than from a sweep

Nine RATIO trades have closed since FlowRatio shipped on 2026-09-11:

    n   wins   pnl_usd   total_R   mean_R    mean_hold   max_hold
    9      0   -0.6500    -4.333   -0.481       3.83h      7.94h

    close_reason    n   avg_hrs      pnl
    OFI_REVERSAL    8     4.19    -0.4824
    SL              1     0.90    -0.1676

**Eight of nine closed on this exit, and not one reached the 12-hour cap.** The measured
hold curve for this rule is +0.032 / +0.082 / +0.273 / +0.346 / +0.379 / +0.330 at 1, 2,
4, 6, 12 and 24 hours, and the head-to-head table under H11 puts it at +0.120R on a
2.5-hour hold against +0.382R at 12 hours.

So the rule has been running nearer the low end of its own curve than the high one, and
`max_hold_minutes = 720` — kept at 720 in the H11 addendum specifically to protect the
+0.382R cell — has never once been reached, because this exit fires first. Widening the
window is the narrowest available lever on that: it does not touch the entry, the
barriers or the cap.

The mean is also stable rather than drifting: -0.486R at six trades, -0.481R at nine. The
result being corrected is consistent, not a swing.

### What is NOT claimed

20 was not swept and is not a measured value. The 8/10/12-bar plateau recorded under H10
does not reach it, and the neighbouring values there disagreed by more than the effect
being claimed. This is a change made on a mechanism and on nine observations, which is
what this file exists to make falsifiable rather than to prevent.

It is also not obviously safe in the direction it is being moved. A longer window makes
the exit slower in both directions: it will hold winners longer and losers longer. The
hold curve says the first effect should dominate for this rule; that is the claim under
test.

And the entry may be the real problem rather than the exit. `signal_outcomes` grades the
ENTRY against a fixed 2%/4% pair over 12 hours, independent of the deployed exit, and
XVENUE_FLOW scores 14 LOSS / 8 TIMEOUT / 1 WIN there — mean about -0.45R. If that holds,
no exit window rescues it. This hypothesis tests the exit; it does not defend the entry.

### What this costs H9

**H9's sample breaks here.** The nine RATIO trades already closed were exited under a
different rule and cannot be pooled with what follows. H9's 30-trade / 21-day clock
should be read from 2026-09-17, not from 2026-09-11, and the nine trades above belong to
a configuration that no longer runs.

That is the price of this change and it is the main argument against having made it: H9
was six days into a twenty-one day window with a decision rule already written down.

### Decision rule, fixed in advance

Evaluate when **20 RATIO trades have closed under this setting**, or after **21 days**
from 2026-09-17, whichever comes first. Trades are identifiable by
`entry_path = 'RATIO'` and `opened_at >= 2026-09-17`.

- **Keep 20** only if mean R over those trades is positive AND positive after discarding
  the single largest winner AND mean hold is materially above the 3.83h it replaces. All
  three: a positive mean arriving with an unchanged hold would mean the window did
  nothing and the result is noise.
- **Revert to 10** if mean R is negative, or if the hold does not move.
- **Do not try 15, 25 or 30 in response to a negative result.** One value has been tried
  without evidence; a second would be a sweep conducted one deploy at a time, which is
  the slowest and least honest form of the overfitting this repository has already paid
  for. If 20 fails, the question is whether the OFI exit belongs at all — H10 already
  carries that, and its own answer was "remove it".
- **DipStrategy stays at 10 throughout**, so the comparison is available at the end: two
  strategies, one market, two exit windows.

### Cost of being wrong

None in money; `paper_mode` is true. The cost is six days of H9 evidence, and a second
live change on a stream that already carries four open hypotheses.

### Result

_Open._

---

## H14 — ABANDONED 2026-09-18 after 3 trades, superseded by H15

Widening the OFI window 10 → 20 ran for one day and produced three RATIO trades, which is
not a sample. The operator judged 20 buckets (5h) too long and asked for 15.

This is the thing H14's own decision rule forbade, in its own words: *"Do not try 15, 25
or 30 in response to a negative result. One value has been tried without evidence; a
second would be a sweep conducted one deploy at a time, which is the slowest and least
honest form of the overfitting this repository has already paid for."*

It is recorded as abandoned rather than edited into H15, because a decision rule that gets
rewritten when it becomes inconvenient is not a decision rule. The three trades belong to
a configuration that ran for one day and are not poolable with anything.

### Result

_Abandoned untested._

---

## H15 — The OFI exit window at 15 buckets

### The change

`FlowStrategy:FlowOfiBars` 20 → 15, for XVENUE_FLOW only. `DipStrategy` stays at 10.
15 buckets is 3h45m.

### What is claimed, and what is not

Nothing is claimed from measurement. 15 WAS in the original H10 sweep and it was the
second-worst cell in it:

    window    mean R      (no rule: +0.379)
     8 bars   +0.391
    10 bars   +0.410
    12 bars   +0.340
    15 bars   +0.318   <- this value
    20 bars   +0.346
    30 bars   +0.338

That sweep is the one this file already describes as measuring noise — neighbours
disagreeing by more than the claimed effect — so it is not evidence against 15 either. It
is simply the case that no measurement supports this number, and the one that exists
ranks it last but one.

The argument for it is the operator's: 20 buckets held positions longer than they wanted.
That is a real preference about exposure, and it is not the same kind of claim as an edge.

### The sample cost, now paid twice

H9's clock was reset on 2026-09-17 by H14 and is reset again here. Nine RATIO trades ran
under 10 buckets, three under 20, and the count starts from zero under 15. Three
configurations in eight days on a rule whose decision rule asks for thirty trades.

### Decision rule, fixed in advance

Evaluate when **20 RATIO trades have closed under this setting**, or after **21 days**
from 2026-09-18. Identify them by `entry_path = 'RATIO'` and `opened_at >= 2026-09-18`.

- **Keep 15** if mean R is positive AND positive after discarding the single largest
  winner AND mean hold is above the 3.83h measured under 10 buckets.
- **Revert to 10** otherwise — to 10, the shipped default, not to 20 and not to 12.
- **This is the last window value to be tried.** If 15 fails, the question is whether the
  OFI exit belongs at all. H10 already answered that with "remove it", and the answer was
  overridden twice.
- **DipStrategy stays at 10 throughout.**

### Cost of being wrong

None in money; `paper_mode` is true. The cost is the third reset of H9's sample.

### Result

_Open._

---

## H16 — Turn the ratio path off, keep the high-volume waiver

### The change

`FlowStrategy:Signal:RatioMinimum` 2.1 → 99.0. Nothing else moves. The threshold is out
of reach by construction — the highest ratio on a qualifying bucket in 28 days averages
2.53 — so `RATIO_TOO_LOW` is now the verdict for every bucket under $20M, and the only
way XVENUE_FLOW can enter is the H11 waiver.

The volume floor stays at $3M, which is what makes this work: it sits below the $20M
waiver threshold, so a large bucket still clears the volume gate before reaching the
waiver. Lowering RatioHighVolumeUsd below $3M would silently close that door.

### The evidence that motivated it, and the evidence against it

Traded: 12 RATIO trades, 2 winners, **-4.477R** — LONG -2.231R over 9, SHORT -2.247R
over 3. It is where essentially all of the account's loss came from.

Against, and it is not weak. Reconstructed over all 59 qualifying buckets in 28 days —
five times the traded sample — signed to the side the rule takes, forward return:

    side    n    +1h      +2h     +4h    +12h   hit12
    LONG   33  -0.076   -0.010  +0.308  +0.406  45.5%
    SHORT  26  +0.154   +0.267  +0.221  +0.550  57.7%

Unconditional base rate over 2,454 buckets is +0.249% at 12h, so the alpha is +0.157%
for LONG and **+0.799%** for SHORT. Both sides beat holding at random; the short side
beats it by five times as much as the long side does.

Simulated on the deployed geometry (2% stop, 4% target, first touch on 1m bars, 12h cap):

    side   half  n   stops  targets  timeouts  mean_R
    LONG   1st   12    3       5        4      +0.615
    LONG   2nd   21    7       1       13      -0.088
    SHORT  1st   15    3       6        6      +0.759
    SHORT  2nd   11    3       0        8      -0.095

So the rule worked, then stopped, and the second half is roughly break-even rather than
catastrophic. The traded -0.248R / -0.749R is worse than the simulated -0.09R because
of the OFI exit, fees, and a dynamic target the rule was never measured against.

### What was known and not acted on

`use_dynamic_tp_sl` has been TRUE throughout. The real target was 6.667%, not 4.00%, and
**no trade has exited on TP in nine days**. This rule has therefore never once run the
configuration H9 measured. It is being retired without that test ever having been done,
and that is a deliberate operator decision taken with the numbers above in front of them.

Why the market changed, measured rather than assumed:

    half                 SOL          range   1m bar   |12h move| median   reaches 4%
    1st (08-21→09-03)  91.93→103.94   26.1%  12.41bps       1.577%          15.4%
    2nd (09-04→09-18)  103.93→110.89  15.9%   8.61bps       1.149%           6.8%

A 4% target needs an event that now happens once in fifteen tries; a 2% stop is 1.74x the
typical move and stays well within reach. The asymmetry, not the entry, is what turned
+0.6R into -0.09R.

### What this ends

- **H9 (the ratio entry) is terminated without a verdict.** It never ran its own
  configuration. Do not record it as refuted.
- **H15 (OFI window 15) loses its population.** Its rule counts RATIO trades and there
  will be none. It must not be re-scoped onto HIGH_VOLUME trades after the fact; that is
  changing a decision rule to fit the data available, which is the thing this file exists
  to prevent. Record it as cut short.
- **H11 becomes the whole of XVENUE_FLOW.** At ~0.9 qualifying buckets a day it will not
  reach its 20-trade bar inside its window either, and it now has no competition for the
  per-strategy slot, which is the one thing that changes in its favour.

### Decision rule, fixed in advance

This is a removal, not an experiment, so the rule governs bringing it BACK rather than
keeping it.

- **Restore `RatioMinimum` to 2.1 only if** the market's 12-hour move distribution returns
  to the first half's shape — median |move| above 1.5% and at least 12% of windows
  reaching 4% — measured over a trailing 14 days. That is the condition the rule's
  profitable half ran in, and it is checkable in one query.
- **Do not restore it on a run of good CANDLE_REVERSAL results**, or on a revised ratio
  or volume threshold. Those are different claims.
- **If it is restored, turn `use_dynamic_tp_sl` off first**, so the configuration under
  test is the one that was measured.

### Cost of being wrong

None in money; `paper_mode` is true. The cost is that the strongest alpha found anywhere
in this project — the short side at +0.799% against base rate — is switched off on twelve
trades taken under a geometry nobody chose.

### Result

_Open (removal in force)._

---

## H17 — Give the gate grounds it can actually reach

### Why

The gate approved **45 of 45** candidates and refused none, across two strategies and
eleven days. That was read as the model rubber-stamping. It was not.

The prompt offers exactly four grounds for skipping and states that a ground whose
condition is unmet "does not exist for this trade". Measured on production, all four were
unreachable by construction:

| ground | condition | reality |
|---|---|---|
| thin evidence | `excluded venues > 0` | neither surviving rule scores venues — brief prints `n/a` |
| late entry | dispersion >= 80% of ceiling | `MaxDispersionBps = 0`, so brief prints "NOT available" |
| losing day | today's loss >= half the limit | half is **$2.25**; worst day ever is **$0.65** |
| concentration | open positions >= 2 | `max_open_per_side=1` is checked **before** the gate |

The model said so itself on every call: "is not subject to any grounds for skipping",
"is not flagged by any of the grounds", "there are no grounds for skipping". It was
reporting the arithmetic correctly. A larger model or a tool loop would have changed
nothing.

A second defect, found in the same audit: `FindSimilarAsync` ranked neighbours on |z|,
venue-agreement ratio, dispersion and stop width. CANDLE_REVERSAL has **one distinct
value on all four** and XVENUE_FLOW has one on three of them, because neither rule
computes z, agreement or dispersion. The distance was a constant, so the query returned
the five most recent signals on that side and called them similar — and it did not filter
by strategy, so a price rule was shown a flow rule's outcomes as precedent.

### The change

Four new grounds, each computed from this account's own closed trades in one query
(`BotRepository.GetGateEvidenceAsync`), each marked AVAILABLE / NOT AVAILABLE in the brief:

- **this cell is losing** — same strategy, side and entry path, last 20 closed, needs n>=5
- **trend against the entry** — 4-hour move beyond +/-2.0% against the proposed side
- **one event twice** — same entry path fired inside 120 minutes
- **concentration** — a position already open on this side **across every strategy**
  (`max_open_per_side` is per-strategy, so two rules can both hold a LONG and neither
  limit sees it)

Dispersion, excluded venues, today's P&L and the per-rule position count move to a
CONTEXT block, explicitly not grounds. `ContradictsBrief` is updated to the new four.
Retrieval is filtered by strategy and keyed on ATR and |OFI|, the only two quantities
that vary.

Verified against production at the moment of the change:

    candidate                          cell n  wins  mean R   ground?
    CANDLE_REVERSAL LONG                  16    10   +0.290   no
    CANDLE_REVERSAL SHORT                  1     0   -1.049   no (below n=5)
    XVENUE_FLOW LONG via RATIO             9     2   -0.248   YES
    XVENUE_FLOW SHORT via HIGH_VOLUME      3     1   -0.349   no (below n=5)

### What is deliberately NOT being claimed

Each ground reduces to a threshold, and a threshold belongs in `RiskEngine`. What the
model is asked for is the combination — several marginal readings together — and that is
the only part an `if` cannot do. If any single ground turns out to be decisive on its
own, move it into the deterministic layer and take it out of the brief.

The gate has never once refused, so there is no evidence it refuses **well**. The first
audit of it, on five trades, had the live gate at **-3.00R** against **+1.00R** for
approving everything. This change makes refusal possible; it does not make it correct.

### Decision rule, fixed in advance

Judged on signals recorded **after** this ships, at 40 gated signals or 21 days,
whichever comes first.

- **Keep if** the gate refuses between 5% and 40% of candidates, AND the refused set has
  a lower mean R (by `signal_outcomes.outcome_r`) than the approved set.
- **Revert if** it refuses nothing, or refuses more than 40%, or the refused set's mean R
  is **higher** than the approved set's — the last being the case where it is removing
  the winners.
- **A refusal citing a ground the brief marked NOT AVAILABLE is a defect, not a result.**
  `ContradictsBrief` logs these at Warning; if more than 10% of refusals are flagged,
  revert regardless of the R comparison.

### Cost of being wrong

None in money; `paper_mode` is true. The cost is trades not taken, and 45 approvals of
evidence nobody could act on being replaced by refusals nobody can yet judge.

### Result

_Open._

---

## H18 — Restore the ratio path (operator override of H16's own condition)

### The change

`FlowStrategy:Signal:RatioMinimum` 99.0 -> 2.1. Nothing else in the rule moves. Shipped in
the same commit as H17, deliberately — see attribution below.

### H16's restore condition was measured, and it is NOT met

H16 fixed the condition in advance: restore only if the 12-hour move distribution returns
to the first half's shape — **median |move| above 1.5%** and **at least 12% of windows
reaching 4%** — over a trailing 14 days. Measured 2026-09-19 on hourly windows over
`klines_1m` (the median reproduces H16's published 1.577% for the first half exactly,
which is what makes the trailing figure trustworthy):

    window                       n     median |12h move|    reaches 4% (two-sided)
    1st half (H16: 1.577%)      260         1.577%                 30.0%
    2nd half (H16: 1.149%)      360         1.161%                 13.3%
    trailing 14 days            324         1.161%                 14.5%

The trailing window is indistinguishable from the second half — the one in which the rule
lost 4.477R. **Both legs fail**: 1.161% is below the 1.5% median bar, and the two-sided
14.5% corresponds to roughly 7% one-sided, below the 12% bar.

**This restore is therefore an operator override, taken with the numbers above in front
of them, not a condition being satisfied.** Recorded here so that nobody later reads the
restore as evidence the market had turned.

H16's second clause — "if it is restored, turn `use_dynamic_tp_sl` off first" — is also
not being honoured, and there is a measurement for that one: replaying all 41 XVENUE_FLOW
signals with the deployed exit set gives **-0.159R** mean with dynamic and **-0.158R**
without. For this rule the switch is indistinguishable, so the clause is being set aside
on evidence rather than ignored. It remains true that the rule has never run the geometry
H9 measured.

### Two changes at once, and why they are still attributable

`HYPOTHESES.md` requires one live change at a time. This breaks that rule, and the
mitigation is that the two are separable **in the data**: gate verdicts are recorded per
strategy in `signal_outcomes`, so "what did H17 refuse" and "how did the restored ratio
path do" are different queries. If gate refusals turn out to be concentrated on
XVENUE_FLOW RATIO — which the cell base rate above makes likely — then H18's population
is whatever H17 lets through, and **H18 cannot be judged on its own**. Say that then
rather than pooling.

### Decision rule, fixed in advance

Judged on RATIO trades opened after this ships, at 20 trades or 28 days.

- **Keep if** mean R over those is positive, positive in both halves, and positive after
  discarding the single best trade.
- **Turn it off again if** mean R is negative, or if fewer than 10 trades have been taken
  by the 28-day mark — in which case record it as inconclusive rather than refuted, the
  same way H9 was.
- **Do not re-tune `RatioMinimum` or `RatioMinVolumeUsd` inside the window.** That is a
  different claim and it restarts the count.

### Cost of being wrong

None in money; `paper_mode` is true. The cost is the sample: the ratio path fires ~2x a
day and will be most of the trade stream again, so a repeat of the -4.477R spends four
weeks of observations that H11 and H13 also need.

### Result

_Open._

---

## Removed features and the numbers that outlive them — 2026-09-19

Four config-gated features were deleted on operator instruction. All four were off, so
nothing about live behaviour changes. What would have been lost with them is the
measurement each one carries, and the tool that produced most of these numbers — the
backtester — was deleted on 2026-09-11 and cannot regenerate them. So they are recorded
here. `git show e22a31a~1` and the commit that follows it have the code.

### 1. Range geometry (`UseRangeGeometry`, `RangeLookbackMinutes`, `VolatilityStops.ResolveFromRange`)

Placed the stop at the recent range's low and the target at its high, instead of at
multiples of ATR. **It measured better than the ATR geometry that survives it**, on 1,486
decision points over 18 days of SOL, first touch within 12 hours, stop taken when a single
minute spans both barriers:

    geometry                    stop%   win%    mean R
    1.5x ATR, 2:1 target        0.672   33.9%   +0.015
    range boundary, 2h look     0.79    52.9%   +0.107
    range boundary, 4h look     1.11    52.2%   +0.030

The ATR pair sits on its own break-even line — 33.9% against the 33.3% a 2:1 needs — which
is why two weeks of live trading produced 28%. Two hours beat four clearly, so the 120-minute
lookback was measurement rather than preference.

Reward:risk under it is not a parameter; it falls out of where the entry sits inside the
range, which makes `MinRewardRisk` a positional filter by the back door:

    position in range   avg R:R   win%    mean R
    0-25%   (near low)   13.37    14.2%   +0.390
    25-50%                1.72    38.8%   +0.025
    50-75%                0.63    68.9%   +0.105
    75-100% (near high)   0.17    83.0%   -0.033

**Why it was off despite being better:** FlowRatio enters WITH the dominant side, which puts
price at the edge of its own range, so the boundary target sits almost on top of the entry
and fails `MinRewardRisk`. The finding is real and the rule it was measured on is not the
rule that runs. If an entry rule is ever added that enters into a range rather than out of
one, this is the geometry to reach for.

### 2. Entry pullback (`EntryPullbackAtr`, `AWAITING_PULLBACK`)

Waited for price to give back k×ATR from the signal bucket's close before entering. Shipped
at 0.75 as H4 off 28 in-sample trades, then set to 0 and never re-enabled.

**Re-measured 2026-09-19 on all 68 resolved signals with the deployed exit set, and it does
not work at any depth.** Net total R by k: 0 → −0.822, 0.25 → −1.222, 0.5 → −0.667,
0.75 → −3.617, 1.0 → −1.777, 1.5 → −0.835. No trend, no plateau. H4's shipped 0.75 is the
**worst** of the six.

The mechanism is why this one is deleted rather than parked. Waiting does help each loser —
mean R on the k=0 losers goes −0.740 → −0.542 → −0.398 → −0.301 — but it helps the winners
by the same factor in the opposite direction, +0.760 → +0.465 → +0.368 → +0.294, because a
pullback shifts *everyone's* entry price by the same fraction rather than selecting better
trades. And the fills are asymmetric the wrong way: at k=0.75, **11 winners never fill
against 1 loser**, because a trade that runs in your favour immediately never gives back.
See the entry-timing note.

### 3. Print concentration (`RatioMaxConcentration`, `PRINT_CONCENTRATION_TOO_HIGH`)

Refused a bucket whose imbalance was one order — largest single print over the dominant
side's notional, against a configurable cap. **Never enabled and never measured**, so
nothing is lost but the idea, which is worth keeping: it guarded the HIGH_VOLUME waiver
specifically, where the dominant side can lead by as little as a tenth of the notional and
a single print could be most of that lead. `flow_bars_15m.max_buy_usd` / `max_sell_usd` are
the columns it read, and they are still being written, so it can be rebuilt without a
schema change.

### 4. Breakeven stop (`UseBreakevenStop`, `BreakevenTriggerPct`)

Moved the stop to entry once the trade had reached `BreakevenTriggerPct` of profit. **Off
for a measured reason**: it closed any trade that reached +0.8% and came back to entry,
which after fees is a small loss, and together with the 1.20% trailing stop it truncated
nearly every winner the bot had — four consecutive live entries stopped out having moved at
most +0.29% in their favour. It also sat in `StrategyEvaluator`, *before* the strategy's own
exit logic, so it fired on rules that did not ask for it.

`bot_config.use_breakeven_stop` and `breakeven_trigger_pct` remain as columns and are no
longer read, joining the other orphaned columns listed under Known constraints in README.

### 5. Dead members swept on 2026-09-20 (`TradingCosts` composites and six unread accessors)

Found by counting references to every declared name against a comment-stripped copy of the
source: a name appearing exactly once is a declaration nobody uses. Nine in C#, all deleted,
none of them reachable, so live behaviour is unchanged.

  `BotStateService._lastClosedAt` with `SetLastClosedAt` and `LastClosedAt`. **Write-only
  state**: two call sites stamped it on every close and nothing ever read it back. It is
  what remains of a post-trade cooldown; the cooldown that runs keys off the last *entry*
  per strategy, in `_lastEntryAtByStrategy`.

  `RiskAssessment.HasCritical`, `OkxOrderState.IsPartial`, `OkxPosition.UnrealisedPnl` and
  `OkxPosition.LiquidationPrice` — unread accessors. The last two took their raw JSON
  fields (`upl`, `liqPx`) with them, since nothing else parsed them. Partial fills are
  still handled: `OkxOrderEngine` reads `State` and the filled quantity directly.

**The two cost constants are the ones worth keeping numbers for**, because both encode a
measurement rather than a preference:

  `PostOnlyRoundTrip` = maker + taker = **7 bps**, what the bot actually pays. The engine
  rests a post-only entry (maker) and exits through the OCO (taker) — maker in, taker out,
  not taker both ways — and 7 bps matches what short holds cost against the account bills.
  It excludes funding, which is charged every eight hours against the position and settles
  in the bills rather than on either order. Funding is lumpy and much larger over a long
  hold: a measured 12-hour hold cost **63 bps all-in**. Any horizon past a few hours needs
  it added separately rather than folded into a per-trip figure. Deleted because
  `PaperOrderEngine` now charges `OkxMakerFeeRate` at open and `OkxTakerFeeRate` at close
  separately, which is the same 7 bps applied to the right leg at the right moment.

  `BacktestStressRoundTrip` = **21 bps**, a stress level and never a fee estimate. It was
  the deleted backtester's default cost. The rule it encodes outlives the tool and any
  replacement measurement should use it: published audits of this class of strategy found
  policies that looked viable at an optimistic 10 bps and were solidly negative at a
  realistic 21+, once slippage and adverse selection on the resting order are counted
  rather than assumed away. **A policy that only survives at 7 bps has not survived —
  measure at 21 before believing anything.**

### What was deliberately NOT removed

`MaxDispersionBps` is also a dead knob — no scorer reads it — but it still renders one line
of CONTEXT in the gate brief, and **H17 is being measured on that prompt**. Remove it when
H17 closes.

`FlowVerdict.AggregateZ`, `AgreeingVenues` and `DispersionBps` are structurally 0 under both
surviving rules and are still written to three `signal_outcomes` columns and logged on every
entry. They are dead *data*, not dead code, and removing them moves a schema the two open
hypotheses are being measured against. Same deadline as `MaxDispersionBps`.

The confidence path is also dead by construction and must stay: the strategy returns a
literal `0m`, and `PositionSizer` scales only when `useAiSizing && confidence > 0`, so the
scalar stays at 1.0 and sizing is purely risk-based. Substituting 1.0 to look neutral would
multiply every order by 1.5 the moment `use_ai_sizing` was switched on.

---

## H19 — Give the gate the session-conditional base rate

### The change

`GetGateEvidenceAsync` gains a third slice beside the cell and the rule overall: the same
strategy's closed trades **in the same session of day**, split at 12:00–20:00 UTC. Ground
one, "this setup is losing", becomes available when ANY slice with at least 5 closed
trades has a negative mean R, and the brief prints all three with their counts so the
model can see which slice is carrying it and how thin it is.

No new kind of evidence and no new ground — the same account history, cut one more way.

### Why this cut and not another

Asked to find data that separates winners from losers, which is a request to fit the known
outcome, so the features were chosen by mechanism first and then measured. Four were tried.
**Range position failed**: the favourable side gives mean +0.079 but only +0.008 after
discarding the best trade, with a negative first half. Session passed, and passed the part
that matters:

    khung UTC    n    mean R    win
    00-04        9    +0.622    67%
    04-08        4    -0.475    25%
    08-12       12    +0.510    67%
    12-16       23    -0.309    35%
    16-20        9    -0.404    44%
    20-24       11    +0.010    55%

Every threshold from 06:00 to 18:00 splits dương-below / âm-above, so it is a plateau
rather than one lucky cut. Refusing 12:00–20:00 over the 68 replayed signals: 36 kept at
**+9.923R**, 32 refused at **−10.745R**, against −0.822R for taking everything.

**The decisive check is the outlier one, and only half the finding survives it.** The two
largest trades (+2.83R, +2.80R) both fall at 11:00 UTC, inside the favourable window:

                        full sample    less the two
    kept   (hour <13)     +0.351          +0.146     <- halves
    refused(hour >=13)    -0.237          -0.237     <- unchanged

So "accept the winners" rests on two trades and, split by rule, on n=8. **"Refuse the
losers" does not move.** Split by rule, hours ≥13 is negative for both: CANDLE_REVERSAL
−0.270 on 17, XVENUE_FLOW −0.214 on 25. That asymmetry is why this ships as evidence
behind a veto rather than as a reason to size up, and it happens to suit a gate that can
only ever say no.

Mechanism, which was written down before the split was measured: 12:00–20:00 UTC is the
European afternoon and the US session, where macro news and institutional flow produce the
sustained directional moves that both of these short-horizon rules die in. See the FATAL
IN A TREND note.

### Why a base rate and not a rule

`if (hour >= 12 && hour < 20) refuse;` would encode today's measurement permanently. The
slice is presented as a **count and a mean** instead, so if the effect is noise the number
drifts toward zero, the ground stops being available, and nothing needs to be noticed and
removed. A hard threshold has no such property.

Cost: each cell is cut in two, so a session slice needs longer to reach 5 trades. Until it
does it is printed as "too thin to read" and the ground is not available from it — failing
toward the permissive side, which is the direction this gate should fail in.

### The caveat that cannot be measured away

**19 days cannot separate "the US session trends" from "these particular 19 days trended
during the US session".** The boundary also came out of a threshold scan, which is worth
saying plainly. What makes it different in kind from the five regime detectors that failed
(see the regime-detection note) is that a calendar feature has no estimation error, is
known before the trade rather than inferred from price at a timescale 10x shorter than the
regime, and cannot be tuned into existence the way a z-score threshold can. Different in
kind is not the same as correct.

### Decision rule, fixed in advance

Judged on signals recorded **after** this ships, at 30 gated signals in the 12:00–20:00
window or 28 days, whichever comes first.

- **Keep if** the session slice is still negative on the new data AND the gate's refusals
  in that window have a lower mean R than its approvals there.
- **Revert if** the session slice turns positive on the new data, or if refusals in that
  window have a HIGHER mean R than approvals — the case where it is removing winners.
- **Do not re-cut the session boundary inside the window.** Moving 12:00 to 13:00 because
  the first ten trades suggest it is the whole failure mode this file exists to prevent.

### Interaction with H17

This edits the prompt H17 is being measured on, so **H17's count restarts from this
deploy**. That is a real cost and it is being paid deliberately: H17 has not produced a
single gated signal yet, so there is nothing to lose but the clock.

### Cost of being wrong

None in money; `paper_mode` is true. The cost is entries not taken in the session that
happens to contain roughly half the trade stream.

### Result

_Open._

---

## H20 — Hold the SHORT side to a higher bar than the LONG side

> **CLOSED 2026-09-20 as SUPERSEDED by H21, after one observation and about eight hours.
> Operator override — H20's own reject condition was NOT met.** See the Result section.

- **Opened** 2026-09-20
- **Change** `FlowSignalOptions.ShortRatioMinimum` 3.0 and `ShortMinOfi` 0.60, applied to
  XVENUE_FLOW only, on **both** entry paths. The long side is untouched at 2.1x / $3M, and
  the $20M waiver still waives the ratio test — for longs.
- **Purpose** Operator decision to stop a side that has never produced a winner.

### What was measured, 2026-09-19

Every closed XVENUE_FLOW trade, split by side:

    side    n   wins   win%    pnl_usd   mean_R   total_R   hold
    LONG   21      9   42.9%   -0.0827   -0.003    -0.063   2.98h
    SHORT   7      1   14.3%   -0.5074   -0.483    -3.383   3.44h

The long side is flat. The whole of this strategy's loss is the short side. At signal
level, which includes signals that never became trades, it is worse: **10 short signals,
0 WIN, 7 LOSS, 3 TIMEOUT, -0.760 mean R, -7.596 total R.** Not one has ever resolved as a
win. Account-wide across both strategies the split is LONG 37 trades +4.581R against
SHORT 8 trades -4.432R.

Price rose after entry on 6 of the 7 shorts. Three of the seven came through the
HIGH_VOLUME waiver and two of those were the worst trades in the set, which is why this
gate sits before the waiver rather than inside the ratio path.

### Why this is weak evidence, stated before the result

SOL rose **7.4%** across the sample, 103.37 to 111.06, with a single **+10.90%** day on
2026-09-18 — and three of the seven shorts sit on that day. Shorting a one-way market
loses whatever the rule is. Excluding 09-18 leaves 4 trades at -0.62R: same sign, n=4, no
evidence at all. The defect this most likely measures is that the rule has **no trend
filter**, which is the same thing CandleReversal shows from the other side (0 wins in 26
trades across both trending periods). A trend filter would address both; this addresses
one side of one rule.

### The thresholds are one number, and it is out of reach

`ratio = (1+|ofi|)/(1-|ofi|)`, so 3.0x **is** |ofi| 0.500 and |ofi| 0.60 **is** 4.0x. The
OFI leg is therefore strictly the stricter of the two and the ratio leg can never be the
condition that binds at these values. Both are checked and carry separate abstain codes
(`SHORT_RATIO_TOO_LOW`, `SHORT_OFI_TOO_LOW`) so SQL can say which one bound if either
moves.

Measured over 30 days to 2026-09-19 on `flow_bars_15m`:

    sell-dominated buckets                    1250
      past the $3M floor                       738
      clearing the old 2.1x                     29
      clearing 3.0x                              1
      clearing |ofi| 0.60                        0
    all-time maximum                 ratio 3.88 / |ofi| 0.590

**So this does not make shorts rare, it stops them.** Same mechanism as H16 — a threshold
out of reach rather than a flag — and the startup banner says so out loud rather than
leaving a reader to work it out. Expect LONG-ONLY behaviour from XVENUE_FLOW.

### Decision rule, fixed in advance

Judge on data collected after this deploy, at **20 closed XVENUE_FLOW trades** or
**14 days**, whichever comes first.

- **Keep** if XVENUE_FLOW's mean R over the window is above its pre-change long-only
  figure of -0.003R, and the long side's signal count has not fallen — this change must
  not touch longs, so a drop in long signals means it leaked.
- **Reject and restore 2.1x symmetric** if mean R over the window is below -0.003R, i.e.
  removing shorts made the strategy worse, or if SOL falls more than 5% over the window
  and the missed shorts would have been the only thing working.
- **Reject regardless of R** if any LONG behaviour changed: `SHORT_RATIO_TOO_LOW` or
  `SHORT_OFI_TOO_LOW` appearing on a buy-dominated bucket is a bug, not a result.
- **Do not move 0.60 down inside the window** to "get some shorts back". If the intent
  becomes long-only, say so by setting it there and closing this entry, rather than
  discovering a value that admits exactly the trades that would have worked.

A trend filter, which is the thing actually indicated, is a separate hypothesis and must
not be opened while this one is running.

### Cost of being wrong

None in money; `paper_mode` is true. The cost is the short side of one rule in a market
that may turn down, and roughly 1 signal per 30 days by the measurement above.

### Result

**SUPERSEDED by H21 on 2026-09-20, ~8 hours after deploy, on 1 observation.**

What it did in that window. It was live from 18:59 UTC and refused the short side 8 times,
every one correctly on a sell-dominated bucket, never on a buy-dominated one — so the
implementation was right and the LONG side was untouched, which was the one condition that
would have rejected it outright. Seven of the eight were buckets the OLD 2.1x rule would
have refused anyway, differing only in which code was logged.

**The eighth is the whole result.** At 02:30 on 09-20 a bucket traded $14.81M at 2.44:1
sell-dominated — over the $3M floor, over the old 2.1x — so the old rule would have taken
a SHORT. H20 blocked it, and SOL fell from about 109.5 to 108.1 over the following half
hour. One short, blocked, that would have worked.

**H20's own reject condition was not met, and it is being overridden anyway.** The rule
said reject "if SOL falls more than 5% over the window and the missed shorts would have
been the only thing working". SOL was −4.93% over 24h, under the bar, and n=1 is not a
window. This is an operator override on a single observation, the same shape as H18
overriding H16, and it is recorded as that rather than dressed up as a measured result.

What the eight hours DID establish, independent of the override: the two thresholds were
unreachable exactly as predicted before deploy — 0 buckets cleared |OFI| 0.60, and the one
bucket that cleared 3.0x in 30 days was the 02:30 one. H20 stopped the short side rather
than raising its bar, which is what the entry said it would do.

---

## H21 — Hold the SHORT side to a bigger print, at the same ratio

> **CLOSED 2026-09-20 as SUPERSEDED by H22, with ZERO observations — it was never
> deployed.** Production was still running H20 when the replacement was written. See the
> Result section.

- **Opened** 2026-09-20, replacing H20 after one observation
- **Change** `ShortMinVolumeUsd` = **$10M** for XVENUE_FLOW, used in place of
  `RatioMinVolumeUsd` ($3M) when the bucket is sell-dominated. `ShortRatioMinimum` and
  `ShortMinOfi` are deleted: **the ratio test is symmetric again at 2.1x.**
- **Purpose** Operator decision. H20 raised the short bar so far it removed the side; this
  puts the bar on the size of the print instead, keeping the ratio where it was.

### What changes, exactly

    long side    >= $3M   AND >= 2.1x      unchanged
    short side   >= $10M  AND >= 2.1x      was $3M / 2.1x before H20, 3.0x + |OFI| 0.60 under H20
    waiver       >= $20M  at any ratio     UNCHANGED, and see below

### Know what this number is fitted to

**Over 30 days to 2026-09-20, sell-dominated buckets clearing $10M AND 2.1x number
EXACTLY ONE** — the 02:30 bucket on 09-20, $14.81M at 2.44x, which is the bucket that was
on screen when the threshold was chosen. The old $3M floor admitted 30 over the same
window.

A threshold picked from one observation, which then matches one observation, is a
description of that event rather than a filter. It is written down here before the result
so that "it worked" cannot later be claimed for it on the same single bucket that chose
it. See the trial-budget note at the top of this file.

### It does not slow the short side as a whole

`RatioHighVolumeUsd` is $20M, already above the new floor, so the news-print waiver is
untouched and keeps taking shorts at ANY ratio: **19 qualifying sell-dominated buckets over
the same 30 days, against 1 for the ratio path.** The waiver is therefore now the source of
essentially every short this rule takes. Of the 7 shorts on record, 3 came through it,
including two of the three worst. If the intent was to slow shorts generally rather than to
restrict the ratio path, `RatioHighVolumeUsd` has to move too, and that is a separate
change.

The startup banner states this, so it cannot be discovered later by surprise.

### Decision rule, fixed in advance

Judge on data collected after this deploy, at **10 closed XVENUE_FLOW shorts** or
**21 days**, whichever comes first. The count is on shorts, not on all trades, because
shorts are what changed.

- **Keep** if short-side mean R over the window is above **-0.10R**, i.e. the size floor
  turned a -0.483R side into something near break-even.
- **Reject and restore the symmetric $3M floor** if short-side mean R is below -0.483R,
  the pre-change figure — the floor would then be selecting worse shorts, not better ones.
- **Reject as untestable** if fewer than 5 shorts arrive in 21 days. A rule that produces
  no observations cannot be judged, and at that point the honest options are long-only by
  explicit choice or moving the waiver, not another threshold.
- **Do not attribute the 02:30 bucket to this rule.** It is the observation that chose the
  threshold and it predates the deploy.
- **Split the result by entry_path.** Ratio-path and waiver shorts are now different
  populations with different floors, and pooling them would hide which one is working.

### Cost of being wrong

None in money; `paper_mode` is true. The cost is taking ratio-path shorts at roughly 1 per
month, which means this hypothesis is very likely to hit the "untestable" branch above.
That is itself the finding worth having.

### Result

**SUPERSEDED by H22 on 2026-09-20, with ZERO observations. It never reached production.**

Committed as `9f20a66` and pushed; CI had not finished deploying it when the operator
changed the thresholds again, so the bot never ran a single cycle under $10M / 2.1x. There
is nothing to report about its behaviour because it had none.

What survives is the measurement that prompted the replacement, and it was already in the
entry above: **$10M AND 2.1x matched exactly one sell-dominated bucket in 30 days**, the
one that chose the threshold. H22 uses 2.5x / $2.5M, which matches 11 spread across the
whole window. Replacing an untestable threshold with a testable one before either has run
is the right direction; doing it inside the same hour is not, and the count below is the
point.

**THIS PARAMETER HAS NOW MOVED THREE TIMES IN ONE DAY** — H20 (3.0x + |OFI| 0.60), H21
($10M + 2.1x), H22 (2.5x + $2.5M) — against 7 closed shorts of lifetime evidence. That
ratio is exactly what the trial budget at the top of this file exists to make visible.

---

## H22 — Short side: 2.5x ratio on a $2.5M floor

- **Opened** 2026-09-20, replacing H21 before H21 ran
- **Change** `ShortRatioMinimum` **2.5x** (long side stays 2.1x) and `ShortMinVolumeUsd`
  **$2.5M** (long side stays $3M), for XVENUE_FLOW. Both apply only to a sell-dominated
  bucket. The ratio check sits AFTER the waiver, so the $20M news-print path is untouched.
- **Purpose** Operator decision: keep the short side alive but make the imbalance carry it.

### What changes

    long side    >= $3.0M  AND >= 2.1x     unchanged
    short side   >= $2.5M  AND >= 2.5x
    waiver       >= $20M   at any ratio    unchanged, NOT gated

**The short notional floor is BELOW the long one and that is deliberate.** On this side the
ratio is doing the work; the notional is a sanity floor, not the filter. The -0.012 mean R
measured below $3M is a pooled figure over both sides at 2:1 — it is not a short-side
measurement at 2.5:1, so it does not directly argue against $2.5M, but it is also not
evidence for it.

### Why this threshold is testable where the last two were not

    setting                        qualifying sell-dominated buckets / 30 days
    old symmetric  $3M   + 2.1x    30
    H20            any   + 4.0x     0   (|OFI| 0.60 is above the all-time max of 0.590)
    H21            $10M  + 2.1x     1   (and it was the bucket that chose the threshold)
    H22 (this)     $2.5M + 2.5x    11

The eleven run 08-27, 09-01, 09-02, 09-06 ×2, 09-08, 09-10 ×2, 09-17, 09-19, 09-20 —
spread across the whole window rather than clustered on one event, at roughly 0.37/day.
That is a population that can actually produce a verdict inside three weeks.

### The honest problem with this entry

**This parameter has moved three times today** — H20, H21, H22 — against a lifetime
evidence base of 7 closed shorts. H21 never ran at all. Two of the three moves were made
while looking at a specific bucket on screen. The trial budget at the top of this file
exists for exactly this pattern, and the count is recorded here rather than left to be
reconstructed later from `git log`.

Nothing about H22 is more measured than H20 or H21 were; what it has is a larger
qualifying population, which makes it falsifiable rather than correct.

### Decision rule, fixed in advance

Judge at **10 closed XVENUE_FLOW shorts** or **21 days**, whichever comes first. Split by
`entry_path` — RATIO and HIGH_VOLUME shorts are different populations under different
rules and pooling them hides which one is working.

- **Keep** if RATIO-path short mean R is above **-0.10R** over the window.
- **Reject and go long-only by explicit choice** if RATIO-path short mean R is below
  **-0.483R**, the pre-H20 figure. Three failed thresholds would then be enough: the
  conclusion is that the short side of this rule does not work, not that the fourth number
  will fix it.
- **Do not move this parameter again before the window closes.** If it moves, this entry
  is void and nothing has been learned from any of H20, H21 or H22.
- **Do not attribute the 02:15 or 02:30 buckets of 09-20 to this rule.** Both predate the
  deploy and one of them chose the threshold.

### Cost of being wrong

None in money; `paper_mode` is true. At 0.37 qualifying buckets a day the window will
likely close on the 21-day branch with fewer than 10 shorts, and the waiver will contribute
most of whatever short flow appears.

### Result

_Open._

---

## H23 — Make the LONG side match the short: 2.5x on a $2.5M floor

- **Opened** 2026-09-20, alongside H22 rather than replacing it
- **Change** `RatioMinimum` 2.1 → **2.5** and `RatioMinVolumeUsd` $3M → **$2.5M**. The short
  overrides from H22 keep their values, which now equal the long ones, so **both sides run
  2.5x on $2.5M** and the asymmetry introduced by H20 is gone.
- **Purpose** Operator decision: one rule for both directions.

### What runs now

    long side    >= $2.5M  AND >= 2.5x
    short side   >= $2.5M  AND >= 2.5x     (ShortRatioMinimum/ShortMinVolumeUsd, equal)
    waiver       >= $20M   at any ratio    unchanged, still not gated

`ShortRatioMinimum` and `ShortMinVolumeUsd` are deliberately left in place at equal values
rather than deleted: the two sides were split yesterday and may be split again, and the
startup banner now reports "SHORT and LONG are held to the same thresholds" so the equality
is visible rather than implied. A consequence worth knowing: **`SHORT_RATIO_TOO_LOW` and
`SHORT_VOLUME_TOO_THIN` are unreachable while the values are equal** — refusals on both
sides come back as `RATIO_TOO_LOW` and `VOLUME_TOO_THIN`.

### The two halves of this change are not equally supported

**The ratio move is inside its own measurement.** The original sweep found a three-cell
plateau at 1.8 / 2.1 / 2.5 — all positive overall after discarding the largest winner, and
in both sample halves — with 3.0 failing the outlier check. 2.1 was chosen from the middle
of that plateau and 2.5 is its top edge, still on it. This is the only threshold change in
the H20–H23 sequence that lands inside a measurement rather than outside one.

It also moves toward the evidence rather than away from it. The RATIO path is negative on
both sides so far:

    entry_path  side    n   pnl_usd   mean_R
    RATIO       LONG    9   -0.3346   -0.248
    RATIO       SHORT   3   -0.3370   -0.749

so raising the long bar is not tightening a rule that was working.

**The volume move goes against its measurement, and that is recorded rather than softened.**
$2.5M reaches half a million dollars into the band that measured **-0.012 mean R** once the
largest winner was removed, and -0.082 in the first sample half, against +0.332 and +0.521
above $3M. The argument for it is that the band was measured at a **2:1** imbalance while
the ratio is now 2.5:1, so most of the thin-volume cases it describes are excluded by the
ratio instead. That is a reason to expect it to survive, not evidence that it will.

### Coverage

Measured on 30 days of `flow_bars_15m`:

    side     dominated buckets   old ($3M, 2.1x)   new ($2.5M, 2.5x)   waiver
    LONG                 1,322                34                  16       32
    SHORT                1,274                30                  11       19

So the ratio path goes from 64 to 27 signals per 30 days, about 0.9/day, with the waiver
unchanged at 51.

### Decision rule, fixed in advance

Judge with H22, at **10 closed RATIO-path trades** or **21 days**, whichever comes first,
split by side.

- **Keep** if RATIO-path mean R over the window is above **-0.248R**, the long side's
  pre-change figure, on the long side and above -0.10R overall.
- **Reject the volume half first** if the losing trades cluster in the $2.5M–$3.0M band —
  restore `RatioMinVolumeUsd` to $3M and keep 2.5x. The two halves have different support
  and should be able to fail separately.
- **Reject both** if mean R is below -0.248R on the long side, i.e. tightening the ratio
  made the long side worse.
- **Do not move either number before the window closes.** With H22 this is the fourth
  change to this rule in one day; a fifth inside the window voids both entries.

### Cost of being wrong

None in money; `paper_mode` is true. The cost is roughly half the ratio-path signals, and
the risk that the $2.5M floor admits the thin-volume cases the original sweep warned about.

### Result

_Open._

---

## H24 — The model decides the early exit, the OFI rule becomes its fallback

- **Opened** 2026-09-20
- **Change** After a position has been held `ExitReviewAfter` (2h), and then at most once
  every `ExitReviewEvery` (2h), `AiExitReviewer` is asked whether force remains behind it
  or the trend has turned. CUT closes the position as `LLM_EXIT`. The 15-bucket
  `OFI_REVERSAL` rule **no longer runs every cycle** — it runs only when a review was due
  and the model could not answer.
- **Purpose** Operator decision: stop cutting on a hard-coded sign flip.

### What is actually being replaced

`OFI_REVERSAL` is the only exit on this account that has produced positive R: **30 exits,
+4.647R**, against the stop's -6.615R over 6 and a take-profit that has never fired. It is
not deleted and it is not switched off — it is demoted to the failure path. That ordering
is the whole change: if it kept running each cycle it would close positions long before
the first review was due (mean hold to an OFI exit was 3.83h against a 2h first review) and
the model would effectively never be asked.

### Why the cadence is two hours and not per bucket

The evidence only changes when a bucket closes, so a per-bucket review would be the
theoretical maximum. It is not affordable here, and the numbers are measured rather than
guessed:

    one call                        42-43 s
    host                            2 vCPU, Ollama at 3.406 of its 3.418 GiB limit
    concurrency                     OLLAMA_NUM_PARALLEL=1 — calls serialise
    cycle budget                    120 s, shared with every open position and the gate
    per-bucket cadence              ~16 calls per 4h position, ~32-100/day
    two-hour cadence                ~2 calls per position, ~4/day

Two positions reviewed synchronously in one cycle is 86s; add an entry-gate call at 43s and
the cycle exceeds its budget and logs "Open positions were not evaluated this cycle". The
two-hour pacing keeps total model load at the same order as the entry gate's current
2-10 calls a day.

### The asymmetry that makes the entry gate safe does not exist here

Every gate failure resolves to "no entry", which costs an opportunity and never a position.
An exit has no such default: silence must mean hold, which leaves a position with no early
exit, or cut, which pays a round trip on every Ollama hiccup. So the reviewer does not
pick — it reports `Unavailable` and the caller runs the deterministic rule. An unparseable
or unrecognised answer is also `Unavailable`, deliberately, rather than being quietly
downgraded to HOLD.

### What the model is and is not asked

It is asked one bounded question about evidence already assembled — does the flow still
push this way, or has it turned — and is shown the closed buckets since the position
opened, each with OFI and notional, plus the 1h and 4h price moves. Stop, target, size and
max hold are shown as context it cannot change. It is not asked to predict price.

### Decision rule, fixed in advance

Judge at **15 closed trades** or **21 days**, whichever comes first, splitting exits by
reason.

- **Keep** if mean R across all exits is above **+0.003R**, the account's pre-change
  figure, AND `LLM_EXIT` mean R is above `OFI_REVERSAL`'s historical +0.155 (4.647/30).
- **Revert to the deterministic rule** if `LLM_EXIT` mean R is below 0 while
  `OFI_REVERSAL` remains positive, or if stop-outs rise above 25% of exits — the failure
  mode to watch is the model holding through a reversal the rule would have caught, which
  shows up as damage moving from `OFI_REVERSAL` into `SL`.
- **Revert regardless of R** if reviews are not happening: fewer than one review per closed
  trade means the pacing or the plumbing is wrong, not the judgement.
- **Watch the fallback rate.** If `Unavailable` exceeds a third of due reviews, this is
  measuring Ollama's uptime rather than the model's judgement, and the result is void.

### Cost of being wrong

None in money; `paper_mode` is true. The real cost is that the exit producing all of this
account's positive R is now gated behind a 3B model on a saturated host — which is why the
fallback exists and why the fallback rate is part of the decision rule.

### Result

_Open._

---

## Defect fixes, 2026-09-20 — and the measurement windows they reset

Four defects found by watching H24's first three exit reviews and the gate's first two
refusals. All four are fixed; three of them edit a brief, and **that resets the windows of
H17, H19 and H24**. The counts below start again from this deploy. That cost is paid
deliberately: a window measuring a brief that contradicts itself measures nothing.

### 1. `GateEvidence.CellIsLosing` ignored the rule slice

The system prompt tells the model the ground is available when **ANY** slice with at least
five closed trades is negative. The property checked cell and session only. On signal 892
the slices were:

    cell     XVENUE_FLOW SHORT/RATIO    n=3    -0.749   (too thin)
    session  XVENUE_FLOW non-US          n=14   +0.078
    rule     XVENUE_FLOW overall         n=20   -0.262   <-- negative, n >= 5

So the brief printed that rule line and then asserted, two lines below it, *"every slice
with enough trades is positive — 'this setup is losing' is NOT AVAILABLE"*. The model read
the rule line, refused, **and was right**; `ContradictsBrief` flagged its true statement as
a fabricated premise. The property now includes the rule slice and the warning enumerates
all three. Verified against the real 892 numbers plus three controls.

**This is the reverse of the defect recorded against the gate so far.** Five of six earlier
instances were the model asserting something the brief denied. This one was the brief
denying something it had itself printed.

### 2. The exit brief handed the model a sentence to recite

It printed `A {falling|rising} market runs against this {side}.` — a legend explaining which
direction hurts. Review #1 recited it as an observation. Reviews #2 and #3 then emitted *"the
1-hour and 4-hour price moves both run against it"*, which is verbatim one of the CUT
conditions in the prompt: **false on #2** (4h was +0.75%, with the position) and **true on
#3** — where it should then have produced a CUT and did not.

A fixed phrase emitted regardless of the data is not a reading of the brief. The brief now
labels each move and evaluates the test itself:

    PRICE CONTEXT
      1h  -0.46%   runs AGAINST the position
      4h  -1.00%   runs AGAINST the position
      BOTH the 1h and 4h moves run against this position.

or the explicit negative. There is nothing left to recite, and the comparison is arithmetic
in code rather than a judgement handed to a 3B model.

### 3. The exit reviewer had no contradiction detector

`AiEntryGate` has had one since the first audit; the reviewer shipped without. It now has
the same check for the three claims it can make — both-moves-against, buckets-favour,
flow-has-turned — each compared against the number the brief printed. Probed on the real
review #2 inputs: the claim is caught with *"only one does"*, and the same sentence against
review #3's inputs is correctly left alone.

**It only logs.** A detector that could overturn the model would make the model decorative,
which is the arrangement all of this replaced.

### 4. Both briefs were invisible in production

Both are logged at Debug and Serilog's `MinimumLevel.Default` is Information, so neither
ever emitted. Every audit so far — six of the gate, three of the reviewer — rebuilt the
brief by hand from `flow_bars_15m` and `bot_trades`. `appsettings.json` now overrides
`CryptoDecision.BotService.Agent` to Debug, and the gate logs its brief too, which it never
did. Volume is small: the gate runs 2-10 times a day, the reviewer about twice per position.

### What this does NOT change

No threshold moves. H22 and H23 keep their populations and their decision rules — neither
touches a brief. H24's mechanism is unchanged; only its prompt text and its instrumentation
move, so its 15-trade / 21-day clock restarts rather than its criteria changing.

---

# Observation window W1 — 2026-09-20 14:16 UTC to 2026-09-27 14:16 UTC

Seven days, opened by the operator after a day in which this rule changed four times and
five defects were found and fixed. **Nothing is to be changed inside it.** That is the
whole point: every entry above was opened, measured for hours, and replaced before it could
produce a verdict, and a file full of hypotheses that never ran is not a trial budget — it
is a record of not having one.

Running `f8e1f46`. `paper_mode` is true throughout.

## What is being measured

Three open hypotheses share this window, and each already has its decision rule fixed:

    H22   short side 2.5x / $2.5M            10 closed shorts or 21 days
    H23   long side matched to it            10 closed RATIO-path trades or 21 days
    H24   the model owns the early exit      15 closed trades or 21 days

W1 is shorter than all three deliberately. It is not a verdict window — it is the period in
which **no parameter moves**, so that when those counts fill, they are filled by one
configuration rather than four. If a threshold moves inside W1, all three entries are void
and say so.

## Baseline at 14:16 UTC, to measure the week against

    closed trades          48      mean R -0.0282    total -$0.9515    20 wins
    signals                79      trigger_value on 3, gate approved 47, refused 2

    by exit reason              n     total R
      OFI_REVERSAL             31      +4.378
      SL                        9     -10.008
      TIMEOUT                   3      -0.424
      FLOW_REVERSAL             2      +0.101
      MANUAL_FLOW_REVERSAL      1      +2.326
      LLM_EXIT                  1      -0.114
      TP                        1      +2.388

    by rule and side            n      mean R
      CANDLE_REVERSAL LONG     19      +0.165
      CANDLE_REVERSAL SHORT     1      -1.049
      XVENUE_FLOW     LONG     21      -0.003
      XVENUE_FLOW     SHORT     7      -0.483

    config   capital $100 · risk 0.60% · both strategies live · enabled · paper

Only trades **opened after 14:16 UTC on 2026-09-20** count toward W1.

## The four things worth watching, in order

1. **Does damage move from `OFI_REVERSAL` into `SL`?** That is H24's named failure mode: the
   model holding through a reversal the 15-bucket rule would have caught. The stop already
   carries −10.008R of the account's −0.95; it does not need more.
2. **Is the exit reviewer's PREMISE true?** Four reviews so far: one sound, three with a
   false or recited premise, including the one CUT it has made. The decisions were
   defensible each time, which is exactly what makes this hard to judge — a right answer for
   a fabricated reason is indistinguishable from a right answer until it is a wrong one.
   `ContradictsBrief` now logs these as they happen; **count the warnings**.
3. **What is the fallback rate?** If `Unavailable` exceeds a third of due reviews, W1
   measured Ollama's uptime, not the model's judgement, and H24 is void.
4. **Does the gate refuse again, and is it right?** It refused twice in its life, both on
   2026-09-20, both correctly. `CellIsLosing` now includes the rule slice; the next refusal
   is the first test of that fix.

## What would end W1 early

- `paper_mode` going false. This window is not evidence for live trading and nothing in it
  is designed to be.
- A circuit breaker tripping, which disables the bot in `bot_config` and requires a person.
- Any parameter change, which voids H22, H23 and H24 as stated above.

## Result

_Open. Review on or after 2026-09-27 14:16 UTC._
