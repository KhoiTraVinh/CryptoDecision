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
