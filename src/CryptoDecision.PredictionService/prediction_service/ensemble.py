"""
Weighted ensemble over the registered prediction models.

Combination rule
----------------
Each model's (direction, confidence) collapses to a signed score in [-1, +1]
(see ModelResult.signed_score). The ensemble score is the weighted mean over the
models that actually answered:

    score = Σ(wᵢ · sᵢ) / Σ(wᵢ)

Normalising by the participating weight rather than the configured total matters:
if Ollama is down, the LLM's absence must not drag the verdict toward NEUTRAL as
though it had voted NEUTRAL. A missing model abstains; it does not vote.

That renormalisation has a second-order effect worth naming, because it is not
obvious and it cost real money to notice: dividing the excess among the survivors
promotes them. With xgboost abstaining for want of a trained model.pkl, the
configured 0.35/0.35/0.30 quietly became llm 0.538 / heuristic 0.462 — a single 7B
model holding the majority and setting direction alone whenever the heuristic
disagreed. No operator chose that; it fell out of the arithmetic and the only
visible trace was a model_version string reading `ensemble-heuristic+llm`.

So a share ceiling (max_single_weight) is applied after renormalisation: no single
model outvotes the rest combined. Excess above the cap is redistributed in
proportion to the remaining shares, repeatedly, since absorbing excess can push
another model over. The cap binds only while models are missing — at the full
three-way split the largest share is 0.35 — so it lifts itself once xgboost trains
and there is nothing to remember to revert.

The raw score is then adjusted for agreement. Independent models landing on the
same side is genuine corroboration, so confidence is nudged up; models pulling in
opposite directions is a real warning, so it is damped. Both adjustments are
bounded and applied before the final clamp, so nothing can manufacture certainty
the individual models did not have.

Finally, scores inside the dead zone become NEUTRAL. Without it an ensemble
netting to +0.02 would emit an UP call that no model actually made.
"""
from __future__ import annotations

from dataclasses import dataclass, field

import structlog

from .context import MarketContext
from .models.base import (
    DOWN,
    NEUTRAL,
    UP,
    ModelResult,
    PredictionModel,
    clamp_confidence,
    score_to_direction,
)

log = structlog.get_logger(__name__)


@dataclass(frozen=True)
class EnsembleResult:
    direction: str
    confidence: float
    rationale: str
    signals: dict
    model_version: str
    contributors: tuple[ModelResult, ...] = field(default_factory=tuple)

    @property
    def used_models(self) -> tuple[str, ...]:
        return tuple(c.model_name for c in self.contributors)


class Ensemble:
    def __init__(
        self,
        models: list[PredictionModel],
        weights: dict[str, float],
        *,
        dead_zone: float = 0.15,
        agreement_bonus: float = 0.10,
        conflict_penalty: float = 0.25,
        degraded_weight_factor: float = 0.5,
        max_single_weight: float = 0.50,
    ) -> None:
        self._models = models
        self._weights = weights
        self._dead_zone = dead_zone
        self._agreement_bonus = agreement_bonus
        self._conflict_penalty = conflict_penalty
        self._degraded_weight_factor = degraded_weight_factor
        self._max_single_weight = max_single_weight

    def weight_for(self, model_name: str) -> float:
        return max(0.0, float(self._weights.get(model_name, 0.0)))

    # ── Weighting ─────────────────────────────────────────────────────────────

    def _effective_shares(
        self, contributors: list[ModelResult]
    ) -> tuple[dict[str, float], bool]:
        """
        Each contributor's final share of the vote, summing to 1.0.

        Configured weight → halved if the result is degraded → normalised over the
        models that answered → capped so no one model outvotes the rest.

        Returns the shares and whether the cap actually bound, so the caller can
        say so out loud instead of leaving a silent reweighting in the arithmetic.
        """
        raw: dict[str, float] = {}
        for result in contributors:
            weight = self.weight_for(result.model_name)
            if result.degraded:
                weight *= self._degraded_weight_factor
            raw[result.model_name] = weight

        total = sum(raw.values())

        # Every participating model has zero weight. An equal split is the honest
        # reading of "these all answered and none was told it mattered more".
        if total <= 0.0:
            return {name: 1.0 / len(raw) for name in raw}, False

        shares = {name: weight / total for name, weight in raw.items()}

        cap = self._max_single_weight
        # A ceiling below an equal split cannot be satisfied — one lone model must
        # hold the whole vote whatever the cap says. Leave the shares untouched
        # rather than manufacturing a split that does not exist.
        if cap <= 0.0 or cap * len(shares) < 1.0:
            return shares, False

        # Known consequence, accepted deliberately: with exactly two participants a
        # 0.50 cap forces 50/50, which erases both the configured weight difference
        # and the degraded halving above. Those requirements are contradictory at two
        # models — "neither may outvote the other" has only one solution — and of the
        # two, the one that stops a single model deciding direction alone is the one
        # worth keeping. Restoring a trained xgboost puts all of it back.

        capped = False
        # Models already pinned at the cap are excluded from further redistribution;
        # letting them absorb someone else's excess would lift them back over it.
        pinned: set[str] = set()

        # Bounded by the model count: each pass pins at least one model at the cap.
        for _ in range(len(shares)):
            over = [
                name for name, s in shares.items()
                if name not in pinned and s > cap + 1e-12
            ]
            if not over:
                break
            capped = True

            excess = sum(shares[name] - cap for name in over)
            for name in over:
                shares[name] = cap
                pinned.add(name)

            under = [name for name in shares if name not in pinned]
            if not under:
                break

            under_total = sum(shares[name] for name in under)
            for name in under:
                share = shares[name] / under_total if under_total > 0.0 else 1.0 / len(under)
                shares[name] += excess * share

        return shares, capped

    # ── Main entry point ──────────────────────────────────────────────────────

    def predict(self, ctx: MarketContext) -> EnsembleResult | None:
        """
        Poll every registered model and combine the results.

        Returns None only when no model answered at all — the caller then skips
        the symbol rather than writing a fabricated NEUTRAL row.
        """
        contributors: list[ModelResult] = []

        for model in self._models:
            if self.weight_for(model.name) <= 0.0:
                continue
            if not model.is_available():
                continue

            try:
                result = model.predict(ctx)
            except Exception as exc:  # noqa: BLE001 - one model must not sink the cycle
                log.warning(
                    "model_raised_during_predict",
                    model=model.name,
                    symbol=ctx.symbol,
                    error=str(exc),
                )
                continue

            if result is not None:
                contributors.append(result)

        if not contributors:
            log.warning("no_model_produced_a_result", symbol=ctx.symbol)
            return None

        return self._combine(ctx, contributors)

    # ── Combination ───────────────────────────────────────────────────────────

    def _combine(
        self, ctx: MarketContext, contributors: list[ModelResult]
    ) -> EnsembleResult:
        shares, weight_capped = self._effective_shares(contributors)

        # Shares already sum to 1.0, so this is the weighted mean outright.
        score = sum(shares[r.model_name] * r.signed_score for r in contributors)

        direction, confidence = score_to_direction(score, self._dead_zone)
        confidence, agreement = self._apply_agreement(
            direction, confidence, contributors
        )

        rationale = self._build_rationale(direction, contributors)
        signals = self._build_signals(
            ctx, score, agreement, contributors, shares, weight_capped
        )

        if weight_capped:
            # Said out loud because it means the ensemble is running degraded: the
            # configured split is not the split in force, and which models are
            # missing is the actual news.
            log.warning(
                "ensemble_weight_capped",
                symbol=ctx.symbol,
                cap=self._max_single_weight,
                configured={c.model_name: self.weight_for(c.model_name)
                            for c in contributors},
                effective={name: round(s, 4) for name, s in shares.items()},
                absent=[m.name for m in self._models
                        if m.name not in shares and self.weight_for(m.name) > 0.0],
            )

        log.info(
            "ensemble_verdict",
            symbol=ctx.symbol,
            direction=direction,
            confidence=round(confidence, 4),
            score=round(score, 4),
            agreement=agreement,
            models=[c.model_name for c in contributors],
            effective_weights={name: round(s, 4) for name, s in shares.items()},
            weight_capped=weight_capped,
        )

        return EnsembleResult(
            direction=direction,
            confidence=confidence,
            rationale=rationale,
            signals=signals,
            model_version=self._version_tag(contributors),
            contributors=tuple(contributors),
        )

    def _apply_agreement(
        self, direction: str, confidence: float, contributors: list[ModelResult]
    ) -> tuple[float, str]:
        """
        Adjust confidence for how much the models agreed.

        Only directional votes count — a NEUTRAL model neither corroborates nor
        contradicts a directional verdict, so it is excluded from the tally.
        """
        directional = [c.direction for c in contributors if c.direction in (UP, DOWN)]

        if len(directional) < 2:
            return clamp_confidence(confidence), "insufficient"

        ups = directional.count(UP)
        downs = directional.count(DOWN)

        if ups > 0 and downs > 0:
            return clamp_confidence(confidence * (1.0 - self._conflict_penalty)), "conflict"

        # Unanimous among those that took a side. Only reward it when the ensemble
        # actually followed them.
        if direction in (UP, DOWN):
            return clamp_confidence(confidence * (1.0 + self._agreement_bonus)), "unanimous"

        return clamp_confidence(confidence), "unanimous_but_neutral"

    def _build_rationale(self, direction: str, contributors: list[ModelResult]) -> str:
        """
        Compose the human-readable explanation.

        The LLM's rationale leads when present — it is the only one written in
        prose rather than assembled from thresholds — followed by a one-line tally
        of what each model said, so a reader can see whether the narrative was
        actually corroborated.
        """
        by_name = {c.model_name: c for c in contributors}
        parts: list[str] = []

        llm = by_name.get("llm")
        if llm is not None and llm.rationale:
            parts.append(llm.rationale)
        else:
            fallback = by_name.get("heuristic") or contributors[0]
            if fallback.rationale:
                parts.append(fallback.rationale)

        tally = ", ".join(
            f"{c.model_name} {c.direction} ({c.confidence:.0%})" for c in contributors
        )
        parts.append(f"Ensemble {direction} from: {tally}.")

        return " ".join(p for p in parts if p).strip()

    def _build_signals(
        self,
        ctx: MarketContext,
        score: float,
        agreement: str,
        contributors: list[ModelResult],
        shares: dict[str, float],
        weight_capped: bool,
    ) -> dict:
        """Everything an audit of this prediction would need, for Kafka and JSONB."""
        return {
            "ensemble_score": round(score, 4),
            "agreement": agreement,
            "dead_zone": self._dead_zone,
            # Which models were configured to vote but did not answer. Reading this
            # off a stored prediction is how you tell a full run from a degraded one
            # without having to still have the logs.
            "absent_models": sorted(
                m.name for m in self._models
                if m.name not in shares and self.weight_for(m.name) > 0.0
            ),
            "weight_capped": weight_capped,
            "max_single_weight": self._max_single_weight,
            "models": {
                c.model_name: {
                    "direction": c.direction,
                    "confidence": round(c.confidence, 4),
                    "weight": self.weight_for(c.model_name),
                    # What the model's vote was actually worth here, after
                    # abstentions were renormalised away and the cap applied.
                    "effective_weight": round(shares.get(c.model_name, 0.0), 4),
                    "signed_score": round(c.signed_score, 4),
                    "version": c.model_version,
                    "latency_ms": round(c.latency_ms, 1),
                    "degraded": c.degraded,
                }
                for c in contributors
            },
            "market": {
                "return_24h": round(ctx.return_24h, 4),
                "volatility": round(ctx.volatility, 4),
                "volume_change": round(ctx.volume_change, 4),
                "whale_count": ctx.whale_count,
                "volatility_regime": ctx.volatility_regime,
                "exchange_spread_bps": round(ctx.exchange_spread_bps, 2),
                "price_vs_vwap_pct": round(ctx.price_vs_vwap_pct, 4),
                "buy_ratio_5m": round(ctx.flow("5m").buy_ratio, 4),
                "buy_ratio_1h": round(ctx.flow("1h").buy_ratio, 4),
            },
            # Surfaced at the top level because the dashboard renders these directly.
            "key_factors": list(
                next((c.signals.get("key_factors") or [] for c in contributors
                      if c.model_name == "llm"), [])
            ),
            "risks": list(
                next((c.signals.get("risks") or [] for c in contributors
                      if c.model_name == "llm"), [])
            ),
        }

    def _version_tag(self, contributors: list[ModelResult]) -> str:
        """
        Version string written to prediction_table.model_version.

        Encodes which models actually voted, so the UNIQUE(symbol, date,
        model_version) constraint distinguishes a full-ensemble prediction from a
        heuristic-only one made while Ollama was down. Both are legitimate rows;
        conflating them would silently overwrite the better prediction with the
        degraded one.
        """
        names = sorted({c.model_name for c in contributors})
        return "ensemble-" + "+".join(names)


__all__ = ["Ensemble", "EnsembleResult"]
