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
    ) -> None:
        self._models = models
        self._weights = weights
        self._dead_zone = dead_zone
        self._agreement_bonus = agreement_bonus
        self._conflict_penalty = conflict_penalty
        self._degraded_weight_factor = degraded_weight_factor

    def weight_for(self, model_name: str) -> float:
        return max(0.0, float(self._weights.get(model_name, 0.0)))

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
        weighted_sum = 0.0
        weight_total = 0.0

        for result in contributors:
            weight = self.weight_for(result.model_name)
            if result.degraded:
                weight *= self._degraded_weight_factor
            weighted_sum += weight * result.signed_score
            weight_total += weight

        # Guard against a configuration where every participating model has zero
        # weight; fall back to an unweighted mean rather than dividing by zero.
        if weight_total <= 0.0:
            score = sum(r.signed_score for r in contributors) / len(contributors)
        else:
            score = weighted_sum / weight_total

        direction, confidence = score_to_direction(score, self._dead_zone)
        confidence, agreement = self._apply_agreement(
            direction, confidence, contributors
        )

        rationale = self._build_rationale(direction, contributors)
        signals = self._build_signals(ctx, score, agreement, contributors)

        log.info(
            "ensemble_verdict",
            symbol=ctx.symbol,
            direction=direction,
            confidence=round(confidence, 4),
            score=round(score, 4),
            agreement=agreement,
            models=[c.model_name for c in contributors],
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
    ) -> dict:
        """Everything an audit of this prediction would need, for Kafka and JSONB."""
        return {
            "ensemble_score": round(score, 4),
            "agreement": agreement,
            "dead_zone": self._dead_zone,
            "models": {
                c.model_name: {
                    "direction": c.direction,
                    "confidence": round(c.confidence, 4),
                    "weight": self.weight_for(c.model_name),
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
