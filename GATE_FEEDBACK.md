# Gate feedback loop

How the entry gate's decisions get measured, and what may be changed on the strength
of that measurement. Companion to `HYPOTHESES.md`, which governs parameter changes
made without proof; this file governs the machinery that produces the proof.

---

## 1. The audit that started this

Twenty-three hours of production log (2026-08-27 12:21 → 2026-08-28 11:52 UTC), which
was all that existed: `docker logs bot` holds one container's lifetime and every push
to `main` replaces the container. Fifteen distinct signals, each one put to the model.

| | signals | wins | losses | R under live constraints |
|---|---|---|---|---|
| Gate approved | 3 | 0 | 3 | **−3.00R** |
| Gate refused | 12 | 2 | 10 | — |
| Approve everything | 5 taken | 2 | 3 | **+1.00R** |

Replayed under the bot's real constraints — one position at a time, 900 s cooldown,
4 entries/day, 12 h max hold — so "approve everything" takes 5 of the 15, not all 15.
Outcomes come from OKX ticks: first print at or after the signal is the entry, stop
and target from the strategy's own percentages, whichever level is touched first wins,
nothing counted past the 12 h hold limit.

Five trades decides nothing. Two findings do not depend on the sample size:

**Refusals on premises the brief contradicted — 5 of 12.**

| when (UTC) | side | the gate said | the brief said |
|---|---|---|---|
| 08-27 16:45 | LONG | "venues excluded for thin data (0) **but** dispersion is wide (5.0 bps)" | ceiling is 25 bps |
| 08-27 17:45 | SHORT | "venues **were excluded** for thin data (the brief says so, count above zero)" | excluded = 0 |
| 08-27 18:00 | SHORT | same | excluded = 0 |
| 08-28 03:15 | SHORT | "**several positions are already open** in the same direction" | open = 0 |
| 08-28 09:15 | SHORT | same | open = 0 — and this one would have won |

The open-position claim cannot ever be true: `TradingBotService` only evaluates
entries while open positions are *below* the per-strategy limit, which is 1, so the
gate is only ever asked with zero open.

**"Dispersion is wide" cited at 2.8, 4.3, 5.0, 5.8, 6.5, 6.7, 6.9 and 13.2 bps** —
every one of them far under the 25 bps ceiling `FlowSignalOptions.MaxDispersionBps`
already enforces before a candidate can reach the gate. The model was re-judging a
check the code owns, with no scale in the brief to judge it against.

A third observation, which is the one that matters most and is *not* about the gate:
the raw signal won 3 of 15 against a breakeven rate near 33% at 2:1. A gate cannot
rescue a negative signal; it can only lose less of it.

---

## 2. What is implemented

| Piece | Where | What it does |
|---|---|---|
| `signal_outcomes` | `sql/029_signal_outcomes.sql` | One row per (symbol, side, 15-min bucket) actionable signal: features, gate verdict, verbatim reason, and the outcome the market delivered |
| Signal recording | `TradingBotService.SafeSignalAsync` | Writes the row when the signal fires — before the gate answers, and regardless of whether an order follows |
| Verdict stamping | `TradingBotService` → `SignalOutcomeRepository.StampGateAsync` | Attaches the decision, the model's own words, and the model name |
| Outcome labelling | `SignalOutcomeLabeler` (BotService, 30 min timer) | One idempotent `UPDATE` resolving unlabelled signals against OKX ticks, scored against `bot_config.max_hold_minutes` |
| Reason clustering | `gate_reason_cluster()` | Keyword clustering over the four grounds the prompt names, at read time so re-clustering never rewrites history |
| Premise check | `gate_premise_contradicted()` and `AiEntryGate.ContradictsBrief` | Flags refusals whose stated premise the brief contradicts — in SQL for the report, and at Warning level in the log as it happens |
| Report | `scripts/gate-report.sql` | Sections 0–6: sample sufficiency first, then decision value, reason pricing, contradicted premises, worst calls, raw-signal viability, dispersion buckets |
| Few-shot retrieval | `SignalOutcomeRepository.FindSimilarAsync` → `AiEntryGate` | The k nearest *already-resolved* past signals on the same side, with the neighbourhood's base rate stated |

Two design points worth restating because they are easy to undo by accident:

- **The labeler does not parse logs.** It was tempting — the log has everything — but
  logs are the thing that keeps vanishing. Signals are written to Postgres at emit
  time; the log is a convenience, not the record.
- **Retrieval has no lookahead.** `FindSimilarAsync` only returns cases whose own
  outcome resolved strictly before the signal being judged. Drop that bound and every
  backtest of this feature reads tomorrow's newspaper and looks excellent until it
  runs live.

---

## 3. Threshold tuning (design only — not implemented)

Walk-forward, proposing only, never applying.

```
tune(parameter, grid, data):
    require coverage.decided >= 200 and span >= 7 days      # else refuse to run
    folds = time_ordered_split(data, k=4)                   # never random: adjacent
                                                            # signals share a regime
    for fold in folds[:-1]:
        train = folds[0..fold]                              # expanding window
        test  = folds[fold+1]                               # strictly later, unseen
        best  = argmax_R(grid, train)                       # optimise in-sample
        record(best, R_in = R(best, train), R_out = R(best, test))

    # A value only counts if it wins out-of-sample in EVERY fold. One fold is a
    # coin flip; the point of walk-forward is that a parameter which only works in
    # the fold it was fitted on is exposed rather than averaged into a good number.
    if all(R_out > R_current for each fold) and median(R_out) - median(R_in) > -0.2:
        propose(parameter, best, evidence)
    else:
        report("no candidate survived out-of-sample")
```

Candidate parameters, in the order they are worth testing:

1. `FlowSignalOptions.MaxDispersionBps` (25) — the gate's most-used reason has never
   been checked against outcomes. Section 6 of the report is the direct test: if the
   high buckets do not lose more, the ceiling is the only dispersion check needed and
   the LLM ground should be deleted rather than tuned.
2. `EnterZ` (1.0) — already under `HYPOTHESES.md` H1; the tuner must not touch it
   while that hypothesis is open, or the hypothesis becomes unattributable.
3. `MinAgreeingVenues` (2) — in the audited window the 3/3 signals lost and the 2/3
   ones were mixed, which is the opposite of the expected direction and therefore
   worth a real test rather than a tweak.
4. Signal age at entry — see §5.

`propose()` writes a row to `gate_tuning_log` and stops. Nothing in this repository
reads that table to change behaviour, and nothing writes to it automatically. The
`sample_size >= 200` CHECK constraint means a proposal from a small sample cannot even
be recorded, let alone applied.

---

## 4. Prompt refinement (design only — not implemented)

Weekly, human-in-the-loop, output is a document not a deployment.

```
weekly_report():
    misses = signal_outcomes where gate_decision='REFUSED' and outcome='WIN'
                               order by outcome_r desc limit 10
    bad    = signal_outcomes where gate_decision like 'APPROVED%' and outcome='LOSS'
                               order by outcome_r asc limit 10
    defects = signal_outcomes where gate_premise_contradicted(...)   # always first:
                                                                    # a defect is not
                                                                    # a calibration
    render(defects, misses, bad, with full features and the verbatim reason)
    # A separate reviewing model may draft a prompt diff. It may not apply one.
```

Rules that make this safe rather than a slow-motion overfit:

- One prompt change at a time, recorded in `gate_tuning_log` with `parameter =
  'system_prompt'` and the diff in `rationale`, same as a threshold.
- A prompt change invalidates the comparability of everything before it. Rows carry
  `gate_model`; a `gate_prompt_version` column belongs there too before the second
  change is made.
- Never add a ground for refusing without an outcome-backed reason to. The four
  current grounds were written from first principles and one of them (concentration)
  is unreachable in production, which is what happens when grounds are added by
  imagination rather than measurement.

---

## 5. Features worth adding to the brief

The gate's stated model of the world is "wide dispersion means the move is already
underway and this entry is late". It cannot check that, because nothing in the brief
describes the move's age or extent. The 08-27 16:30 LONG it refused as "late" went on
to reach its target 348 minutes later — the trend had barely started.

Candidates, cheap first:

- **Signal age** — `signal_at − bucket_start`, and how many consecutive buckets this
  side has been actionable. A signal in its fourth consecutive bucket is late in a way
  a first-bucket signal is not, and this is the one measure of "late" that is actually
  available.
- **Move extent so far** — price change since the flow z crossed the threshold,
  expressed in units of the stop. "The move is already 1.8 stops old" is checkable;
  "dispersion is wide" is not.
- **Trend context** — position of price within the last N hours' range, or distance
  from a slow moving average, so "trend continuing" and "trend exhausted" are
  different numbers rather than the same one.
- **Realised vs implied follow-through** — the historical base rate of continuation
  after this z and this agreement, which is exactly what §2's retrieval already
  computes; promoting it from examples to a stated number is a small step once the
  sample supports it.

Each is a change to the *brief*, not to the gate's authority. The gate still only
refuses.

---

## 6. Safety constraints

These are not conventions; each has a mechanism.

| Constraint | Mechanism |
|---|---|
| No automatic threshold deployment | Nothing reads `gate_tuning_log`; tuner writes and stops |
| Risk parameters excluded from the loop | `MaxOrderNotionalUsd`, `risk_pct_per_trade`, `daily_loss_limit_pct`, `max_open_trades_per_strategy` are never proposed by the tuner and are not features of it |
| Every change recorded | `gate_tuning_log`: old/new value, sample size, window, in/out-of-sample R, rationale, approver |
| Small samples cannot produce changes | `CHECK (sample_size >= 200)`; the labeler logs a WARNING every pass below the floor; report section 0 says INSUFFICIENT before any number is shown |
| The research path cannot affect trading | Labeler is a separate hosted service, all failures caught; signal recording failures return null and never block an entry; retrieval failure decides on the brief alone |
| Re-runnable after a crash | Recording is `ON CONFLICT DO NOTHING` on the natural key; labelling only touches unresolved rows; both are pure re-run safe |
| No lookahead in anything the gate reads | `FindSimilarAsync` bounds on resolution time, not recording time |

One constraint that is not in the table because it belongs to the operator: **the gate
is not the strategy.** Everything here measures whether the veto is well aimed. If
section 5 of the report keeps saying the unfiltered signal is negative, the honest
answer is not a better gate.
