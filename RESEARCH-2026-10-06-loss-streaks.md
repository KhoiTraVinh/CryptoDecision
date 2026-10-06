# Consecutive losing trades: are the streaks a phenomenon?

**Date:** 2026-10-06
**Data:** all 113 closed `bot_trades`, 2026-09-08 19:00 → 2026-10-06 10:37 UTC (97 PAPER, 16 LIVE)
**Question asked:** analyse the runs of consecutive losses and say what is behind them.
**Answer in one line:** nothing is behind them — they are what a 46%-win-rate process
produces by chance — but looking for them surfaced a unit error that misstates a
sentence in the H29 decision record.

---

## 0. Summary of findings

| # | Finding | Strength |
|---|---------|----------|
| 1 | The loss streaks are statistically indistinguishable from random. Three independent tests agree. | **Strong** — pre-specified, three tests, consistent |
| 2 | Trend regime does not separate the losses. Both regimes lose at the same rate. | **Moderate** — n=113, CIs overlap but both are negative |
| 3 | The damage partitions cleanly by exit, but that partition is selection-biased and cannot be read causally. | **Descriptive only** |
| 4 | Position size quadrupled on 2026-09-19 while the R denominator stayed at $0.60. The recorded R series is **not in constant units**. | **Strong** — arithmetic, reproduced exactly |
| 5 | Because of #4, H29's sub-claim "negative in both halves *and worsening*" is wrong. Per-trade it **improved**. H29's decision is unaffected. | **Strong** — reproduces H29's own numbers, then corrects them |
| 6 | `max_consecutive_losses = 15` has a ~4% chance of ever firing in 8 months. It cannot protect anything. | **Strong** — Monte Carlo |

---

## 1. The six streaks

Runs of 3 or more consecutive non-positive trades, ordered by `opened_at`.
R is the house convention, `pnl_usd / 0.60` (validated in §5).

| # | Len | Window | R | Sides | Strategies | Exits |
|---|-----|--------|---|-------|-----------|-------|
| 1 | 3 | 09-09 20:00 → 09-11 00:03 (28h) | −0.210 | 2L / 1S | XVENUE ×3 | OFI ×2, TIMEOUT |
| 2 | 5 | 09-14 20:18 → 09-15 13:45 (17h) | −0.936 | 5L | XVENUE ×2, CANDLE ×3 | OFI ×3, SL ×2 |
| **3** | **9** | **09-18 14:18 → 09-22 03:00 (85h)** | **−2.788** | 7L / 2S | XVENUE ×3, CANDLE ×6 | OFI ×3, LLM ×3, SL ×2, TIMEOUT |
| 4 | 5 | 09-23 01:45 → 09-23 14:19 (13h) | −1.037 | 4L / 1S | CANDLE ×3, XVENUE ×2 | LLM ×4, RATCHET |
| 5 | 4 | 09-28 19:31 → 09-30 06:15 (35h) | −1.162 | 4L | CANDLE ×4 | LLM ×4 |
| 6 | 5 | 10-02 14:15 → 10-05 14:45 (73h) | −1.332 | 4L / 1S | CANDLE ×4, XVENUE ×1 | LLM ×3, HV_REGIME, HV_TIMEOUT |

No streak is confined to one strategy, one side, or one exit. Streak 6 is the only
LIVE one, and it ended on its own when SOL started moving again on 10-05.

> **Caution on this table:** streaks 1–2 and the first half of streak 3 were traded at
> $7.50 notional, the rest at $30.00. Their R values are not on the same scale. See §5.

---

## 2. Finding 1 — the streaks are noise

Three tests, all pre-specified before looking at the streak contents.

**Base rates:** 113 trades, 61 losses, 52 wins. Loss rate q = 0.5398.

### 2.1 Longest-run test (Monte Carlo, 200,000 shuffles)

Independent draws at q = 0.540 over 113 trades:

```
mean longest loss streak under the null = 6.86
observed longest                        = 9
P(longest >= 9 | independence)          = 0.175
```

Distribution of the longest streak under the null:

```
  5: 19.0%    8: 13.1%   11: 2.4%
  6: 23.9%    9:  7.7%   12: 1.4%
  7: 19.5%   10:  4.4%   13: 0.7%
```

A run of 9 is the 18th percentile of ordinary luck. It needs no explanation.

Counts of streaks are, if anything, *below* random:

| | expected | observed |
|---|---|---|
| streaks ≥3 | 8.12 | 6 |
| streaks ≥5 | 2.33 | 4 |
| streaks ≥9 | 0.19 | 1 |

### 2.2 Wald–Wolfowitz runs test

```
observed runs = 60      expected under independence = 57.14 (sd 5.26)
z = 0.544               two-sided p = 0.587
```

Cannot reject independence. Observed runs are slightly *above* expectation, i.e. the
sequence alternates marginally more than chance — the opposite of clustering.

### 2.3 Conditional win rate and autocorrelation

```
P(win | previous was a WIN ) = 0.431   (22/51)
P(win | previous was a LOSS) = 0.492   (30/61)
```

A loss is followed by a win slightly *more* often than a win is. Again the opposite
of a streak effect, and a 6pp gap at n=113 is noise.

Lag-1 autocorrelation was the one test that initially looked significant, so it was
stress-tested:

| Series | ρ₁ | 95% band | Verdict |
|---|---|---|---|
| raw USD P&L | −0.206 | ±0.184 | significant |
| **pnl_pct** | **+0.014** | ±0.184 | **not significant** |
| sign only | −0.060 | ±0.184 | not significant |
| drop 5 largest \|P&L\| | −0.058 | ±0.189 | not significant |
| CANDLE only | −0.071 | ±0.238 | not significant |
| XVENUE only | −0.218 | ±0.292 | not significant |
| first half | +0.029 | ±0.262 | not significant |
| second half | −0.342 | ±0.260 | significant |

It survives nothing. It vanishes in percentage terms, vanishes when five outliers go,
vanishes in each strategy separately, and **flips sign between the time halves**. The
raw-USD version is an artifact of the position-size change documented in §5 — mixing
$7.50 and $30.00 trades into one dollar series manufactures serial structure that is
not in the returns.

**Conclusion.** There is no streak phenomenon. The right question is not "why did we
lose 9 in a row" but "why is the per-trade expectancy negative" — those are different
investigations and only the second is worth running.

---

## 3. Finding 2 — the trend hypothesis does not separate them

The standing explanation on record is *FATAL IN A TREND*. Tested here as a per-trade
filter: trend strength = |SOL return over the 12h before entry|, cut at 1.5%
(the same bar H16 used, so the threshold is not newly chosen).

| Bucket | n | win% | total R | mean R | 95% CI |
|---|---|---|---|---|---|
| \|12h\| < 1.5% (range) | 57 | 50.9 | −2.197 | −0.0385 | [−0.146, +0.069] |
| \|12h\| ≥ 1.5% (trend) | 56 | 41.1 | −3.147 | −0.0562 | [−0.129, +0.017] |
| \|12h\| ≥ 2.5% | 32 | 50.0 | −1.770 | −0.0553 | [−0.163, +0.053] |
| \|12h\| ≥ 4.0% | 11 | 45.5 | −0.713 | −0.0648 | [−0.161, +0.032] |

Entering *against* the preceding move, which is the falling-knife case:

| | n | win% | mean R |
|---|---|---|---|
| LONG after a ≥1.5% 12h fall | 19 | 42.1 | −0.0330 |
| LONG after a ≥1.5% 12h rise | 21 | 42.9 | −0.0765 |
| SHORT after a ≥1.5% 12h rise | 10 | 40.0 | −0.0530 |
| SHORT after a ≥1.5% 12h fall | 6 | 33.3 | −0.0638 |

Win rate is 10pp lower in trend (41.1% vs 50.9%), but **mean R is the same within
noise** and every bucket is negative. The confidence intervals overlap almost entirely.

This does **not** refute the FATAL IN A TREND record, which was measured on two
identified trending *periods* rather than a rolling 12h filter — a different and
stricter construction. What it does say is narrower and still useful:

> A 12h-move trend filter would not have rescued the account. It would have removed
> trades that lose at −0.056R and kept trades that lose at −0.039R. Both are losses.

Any trend filter proposed from here has to clear that bar, not just "avoid trends".

---

## 4. Finding 3 — where the loss sits, and why it cannot be read causally

| Exit | n | win% | total R | mean R | 95% CI |
|---|---|---|---|---|---|
| RATCHET | 23 | 95.7 | **+4.337** | +0.1886 | [+0.115, +0.262] |
| OFI_REVERSAL | 31 | 48.4 | +0.892 | +0.0288 | [−0.043, +0.101] |
| HV_REGIME | 4 | 50.0 | +0.075 | +0.0188 | [−0.091, +0.128] |
| TIMEOUT | 3 | 33.3 | −0.106 | −0.0353 | [−0.235, +0.165] |
| HV_TIMEOUT | 1 | 0.0 | −0.255 | — | — |
| **SL** | 10 | 0.0 | **−4.309** | −0.4309 | [−0.650, −0.212] |
| **LLM_EXIT** | 37 | 21.6 | **−6.947** | −0.1878 | [−0.324, −0.051] |

Two intervals exclude zero: RATCHET positive, LLM_EXIT negative. Together SL and
LLM_EXIT account for −11.26R against +5.30R from everything else.

**This table must not be read as "the LLM exit loses money".** The exit label is
assigned *by what the trade did*. A trade that ran in favour gets ratcheted out; a
trade that went nowhere or went against is the one still open for the LLM to close.
Conditioning on the exit conditions on the outcome. The partition is real and worth
knowing, but it is not evidence about the exits themselves, and no exit change is
proposed on the strength of it.

The only way to turn this into a causal statement is a counterfactual replay that
holds the entries fixed and varies the exit set — and per the project's own record,
a replay has to be validated against the real trades it overlaps before it is believed.

---

## 5. Finding 4 — the R series is not in constant units

Notional and the actual dollars at risk, by day:

| Period | trades | notional | stop | **actual risk/trade** | counted as |
|---|---|---|---|---|---|
| 09-08 → 09-18 | 34 | $7.50 | 2.00% | **$0.15** | 1R = $0.60 |
| 09-19 → 09-30 | 47 | $30.00 | 2.00% | **$0.60** | 1R = $0.60 |
| 09-30 → 10-06 (LIVE) | 16 | ~$29.3–29.9 | 2.00% | **~$0.59** | 1R = $0.60 |

Position size quadrupled on 2026-09-19. The R convention divides every trade by a
constant $0.60 regardless. **The first 34 trades are therefore recorded at one quarter
of their risk-adjusted magnitude.**

The house convention was confirmed, not guessed. Reproducing H29's decomposition over
the same 87 trades:

| Bucket | n | H29 recorded | `pnl/0.60` | match |
|---|---|---|---|---|
| CANDLE_REVERSAL | 45 | −0.007R | −0.007 | exact |
| XVENUE HIGH_VOLUME | 13 | −2.769R | −2.769 | exact |
| XVENUE RATIO | 19 | −1.736R | −1.736 | exact |
| XVENUE untagged | 10 | +0.355R | +0.355 | exact |
| **total** | **87** | **−4.157R** | **−4.157** | **exact** |

Same trades, same buckets, same numbers. The convention is `pnl_usd / 0.60`, and it
breaks at 09-19.

---

## 6. Finding 5 — the correction this forces on H29

H29 wrote, of the two tagged XVENUE paths:

> "Split at 09-19 it is -1.339R over 18 then -3.166R over 14: negative in both halves
> and worsening."

That split lands **exactly on the sizing change**. Recomputed:

| Half | n | actual risk/trade | P&L | house R | **true R** | **true mean R** |
|---|---|---|---|---|---|---|
| before 09-19 | 18 | $0.1500 | −$0.8032 | −1.339 | **−5.355** | **−0.2975** |
| 09-19 onward | 14 | $0.6000 | −$1.8998 | −3.166 | **−3.166** | **−0.2262** |

The house figures reproduce H29 exactly. In constant risk units the per-trade result
went from −0.2975R to −0.2262R: **it improved by 24%, it did not worsen.** The
apparent deterioration is entirely the 4× size increase showing up in a fixed-$0.60
denominator.

**What this changes:** the adjective only. Both halves remain solidly negative, the
two tagged XVENUE paths remain the lifetime loss, and under true risk units they are
*worse* than recorded (−8.52R rather than −4.51R), so **H29's decision to switch the
ratio path off is strengthened, not undermined.** Nothing here argues for reopening it,
and H29's 60-signal replay restore condition stands untouched.

**What it does mean** is that every before/after comparison that crosses 09-19 — in
HYPOTHESES.md or anywhere else — is distorted by a factor of four, in the direction of
making later performance look worse than it was.

---

## 7. Finding 6 — the consecutive-loss breaker cannot fire

`bot_config.max_consecutive_losses = 15`. Monte Carlo at the observed q = 0.540:

| Horizon | mean longest streak | P(≥9) | **P(≥15, breaker fires)** |
|---|---|---|---|
| 113 trades (today) | 6.86 | 17.7% | **0.45%** |
| 250 trades | 8.14 | 35.9% | **1.08%** |
| 500 trades | 9.26 | 59.3% | **2.12%** |
| 1000 trades (~8 months) | 10.39 | 83.6% | **4.38%** |

At ~4 closed trades/day, the breaker has roughly a 1-in-23 chance of firing in eight
months of trading. It is set outside the distribution it is meant to police: by the
time 15 losses arrive in a row, ordinary variance has already been exhausted many
times over. It is not a safety net, and it should not be counted as one when reasoning
about downside.

Note also the corollary: a 9-streak has an 84% chance of occurring within 1000 trades
*purely by chance*. Streak 3 was never a signal that something had broken.

---

## 8. What follows

Deliberately short. The project's own record notes a ~250:1 ratio of hypotheses to
daily observations; adding trials is the main way to manufacture a false edge, so this
proposes measurement and one repair, not a parameter sweep.

**R1 — repair the R unit (no decision attached).** Record risk-adjusted R as
`pnl_usd / (notional_usd × stop_pct)` alongside the house figure, and annotate the
09-19 break in HYPOTHESES.md so later readers do not compare across it. This is
bookkeeping; it changes no live behaviour.

**R2 — restate the breaker in terms that can bind, or drop the claim.** Either set
`max_consecutive_losses` where the null distribution says it will act (the observed
distribution puts 9–10 at the middle of an 8-month horizon), or accept that drawdown
control rests entirely on `daily_loss_limit_pct` and stop treating the streak breaker
as protection. **This is a risk setting and is the operator's call — no change made.**

**R3 — the only question worth a replay.** Expectancy, not streaks. Entries fixed,
exit set varied, validated against the real trades it overlaps before anything is
believed. Decision rule fixed in advance, per house practice.

**Not proposed:** any change to the OFI exit or dynamic TP/SL; any reopening of the
ratio path; any trend filter — §3 shows the obvious one would not have helped.

---

## Appendix — provenance and reproduction

All figures come from production `bot_trades` and `klines_1m` on
`ec2-3-107-91-212.ap-southeast-2.compute.amazonaws.com`, read-only `SELECT` only.
No production state was modified in producing this report.

Trade set: `SELECT ... FROM bot_trades WHERE status='CLOSED'` → 113 rows.
Regime columns are `LATERAL` joins to `klines_1m` at entry −12h, −24h and +4h.

R conventions used:
- **house R** = `pnl_usd / 0.60` — matches every historical record, use for comparability
- **true R** = `pnl_usd / (notional_usd × |entry−stop|/entry)` — use for anything crossing 09-19

Null models are 200,000-run Monte Carlo at the empirical loss rate q = 61/113,
independent draws, streaks counted on the same gaps-and-islands rule as the observed
series.

Scripts used to produce §2 and §7 are throwaway and were not added to the repo.

---

# Addendum — the LIVE losing trades and the TP/SL asymmetry

**Added 2026-10-06** after the operator observed that losing trades lose more than
winning trades gain. Confirmed, and the cause is not where it looks.

## A1. In LIVE, the bracket has never fired

All 16 LIVE trades carry the same bracket: **SL 2.00%, TP 4.00%** — nominally 2:1.

```
reached TP (4.00%):  0 / 16
reached SL (2.00%):  0 / 16
best trade ever got  34.4% of the way to the target
worst trade ever got 82.2% of the way to the stop
```

Max favourable excursion across all 16 LIVE trades is **1.376%**. Max adverse is
**1.644%**. Every exit was RATCHET, LLM_EXIT, HV_REGIME or HV_TIMEOUT — the bracket is
decorative at these horizons.

## A2. The realised asymmetry

| | LIVE (n=16) | all bracketed (n=108) |
|---|---|---|
| win rate | 50.0% | 45.4% |
| avg win | **+0.503%** | +0.794% |
| avg loss | **−0.616%** | −0.917% |
| ratio | **1.23:1 against** | 1.16:1 against |
| largest win / loss | +0.877% / **−1.401%** | +5.595% / −2.343% |
| expectancy | −0.0567%/trade | −0.1411%/trade |
| **break-even win rate needed** | **55.0%** | **53.6%** |

The bot delivers 50% and needs 55%. The gap is the whole deficit.

## A3. The bracket is 4.75:1 against, not 2:1

Excursion over a **fixed 12h window from entry**, ignoring the actual exit, so this
measures the geometry rather than the exit policy:

| | touched within 12h |
|---|---|
| TP at 4.00% | **8 / 109 (7.3%)** |
| SL at 2.00% | **38 / 109 (34.9%)** |

First-touch simulation (whichever side is hit first):

| | n | TP first | SL first | neither | if the bracket ran |
|---|---|---|---|---|---|
| LIVE | 16 | 1 | 5 | 10 | −0.375%/trade |
| PAPER | 93 | 7 | 33 | 53 | −0.409%/trade |
| **TOTAL** | **109** | **8** | **38** | **63** | **−0.404%/trade** |

The stop is touched **4.75× more often** than the target. A bracket written as "2:1
reward:risk" is, in the distribution SOL actually produces, strongly negative by
construction — it cannot be rescued by any win rate the entry rule is capable of.

For reference, the level that would be touched as often as the 2.00% stop is a target
at **1.97%**. The current 4.00% sits near the 90th percentile of 12h favourable
excursion (p90 = 3.67%); the 2.00% stop sits near the 65th percentile of adverse
excursion. That is the asymmetry, stated in one line.

## A4. Where the skew actually comes from

Winners are cut early; losers are held to near their worst point.

**LIVE winners — fraction of the favourable move kept:**

```
#170 RATCHET    MFE 0.939%  kept 0.876%  =  93%
#160 HV_REGIME  MFE 0.310%  kept 0.257%  =  83%
#156 LLM_EXIT   MFE 0.492%  kept 0.346%  =  70%
#169 LLM_EXIT   MFE 1.334%  kept 0.877%  =  66%
#158 RATCHET    MFE 1.113%  kept 0.563%  =  51%
#161 RATCHET    MFE 1.376%  kept 0.610%  =  44%
#163 RATCHET    MFE 0.622%  kept 0.249%  =  40%
#159 RATCHET    MFE 0.663%  kept 0.244%  =  37%
                                   mean =  60.5%
```

**LIVE losers — fraction of the adverse move given back:**

```
#166 HV_REGIME  MAE 0.145%  exit -0.172%  = 119%
#157 LLM_EXIT   MAE 0.627%  exit -0.714%  = 114%
#165 LLM_EXIT   MAE 0.491%  exit -0.461%  =  94%
#155 LLM_EXIT   MAE 0.905%  exit -0.841%  =  93%
#164 LLM_EXIT   MAE 1.644%  exit -1.401%  =  85%
#168 LLM_EXIT   MAE 0.259%  exit -0.179%  =  69%
#167 HV_TIMEOUT MAE 1.023%  exit -0.522%  =  51%
#162 LLM_EXIT   MAE 1.297%  exit -0.640%  =  49%
```

(Values above 100% are the round-trip fee, ~0.07–0.09% of notional, which is charged
on top of the price move. On an average +0.503% win it consumes about 15%.)

Winners keep 60% of their peak; losers absorb ~85% of their trough. That is the
realised 1.23:1 asymmetry, and it is produced by the exit timing, not by the bracket
levels — which never trigger.

## A5. The exits are mitigating this, not causing it

The obvious reading — "the discretionary exits cut winners short" — is half right and
leads to the wrong action. Compare what actually happened against letting the bracket
run for 12h:

```
bracket left to run     -0.404% / trade
actually realised       -0.141% / trade
```

The RATCHET / LLM / OFI exits are **saving roughly 0.26% per trade** against the
configured bracket. Cutting winners at 60% of peak is still better than holding for a
4% target that arrives 7% of the time while a 2% stop arrives 35% of the time.

So the binding defect is upstream of the exits: a 2.00% / 4.00% bracket whose two legs
have touch probabilities of 35% and 7%. This is the same conclusion as the standing
*stop geometry is the binding defect* record, now measured on the deployed bracket with
the deployed exits rather than on a bare cap.

**No change proposed.** TP/SL geometry is the operator's call, and the one obvious
move — pulling the target in to ~2% — is a parameter change that would need its
decision rule fixed in advance and a replay validated against the real trades it
overlaps, per house practice. What is settled is the measurement: the current bracket
is not 2:1 in its favour, it is 4.75:1 against.
