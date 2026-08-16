"""
LLM prediction model backed by Ollama (default: qwen2.5:7b).

What this model is good for, and what it is not
-----------------------------------------------
A 7B language model is not a numerical forecaster and will not out-predict a
gradient-boosted tree on tabular features. What it does add is synthesis: it reads
order flow, whale behaviour, volatility regime and cross-venue dispersion together
and produces a stated, auditable reason for its call. That reason is what surfaces
on the dashboard, and it is what makes a losing signal diagnosable afterwards.

It is therefore one voice in an ensemble, not a replacement for the quantitative
models. See ensemble.py for how the votes are combined.

Availability is checked cheaply before every call and cached briefly, so a stopped
Ollama container degrades the pipeline to heuristic-only within one cycle instead
of stalling it.
"""
from __future__ import annotations

import time

import structlog

from ..context import MarketContext
from ..llm.ollama_client import OllamaClient, OllamaError
from ..llm.prompt import build_messages
from ..llm.schema import VERDICT_SCHEMA, VerdictParseError, parse_verdict
from .base import PredictionModel, ModelResult, clamp_confidence, normalize_direction

log = structlog.get_logger(__name__)


class LlmModel(PredictionModel):
    name = "llm"

    def __init__(
        self,
        client: OllamaClient,
        *,
        temperature: float = 0.2,
        num_predict: int = 512,
        num_ctx: int = 8192,
        seed: int | None = 42,
        availability_ttl_seconds: float = 60.0,
    ) -> None:
        self._client = client
        self._temperature = temperature
        self._num_predict = num_predict
        self._num_ctx = num_ctx
        self._seed = seed

        self._availability_ttl = availability_ttl_seconds
        self._available: bool | None = None
        self._available_checked_at: float = 0.0

    @property
    def version(self) -> str:
        # Tagging with the concrete model name keeps prediction_table rows
        # attributable after a model swap.
        return f"ollama-{self._client.model}"

    # ── Availability ──────────────────────────────────────────────────────────

    def is_available(self) -> bool:
        """
        Whether Ollama is reachable and holds the configured model.

        Cached for availability_ttl_seconds: this runs on every prediction cycle
        and an HTTP round trip per symbol per cycle is pure waste when the answer
        changes about as often as a container restart.
        """
        now = time.monotonic()
        if (
            self._available is not None
            and (now - self._available_checked_at) < self._availability_ttl
        ):
            return self._available

        available = self._client.is_up() and self._client.has_model()

        if self._available is True and not available:
            log.warning("llm_became_unavailable", model=self._client.model)
        elif self._available is False and available:
            log.info("llm_became_available", model=self._client.model)

        self._available = available
        self._available_checked_at = now
        return available

    def invalidate_availability(self) -> None:
        """Force the next is_available() call to re-probe."""
        self._available = None
        self._available_checked_at = 0.0

    def warmup(self) -> bool:
        """Preload the model so the first prediction does not pay the load cost."""
        if not self.is_available():
            return False
        return self._client.warmup()

    # ── Inference ─────────────────────────────────────────────────────────────

    def predict(self, ctx: MarketContext) -> ModelResult | None:
        if not self.is_available():
            log.debug("llm_skipped_unavailable", symbol=ctx.symbol)
            return None

        system, user = build_messages(ctx)
        started = time.perf_counter()

        try:
            response = self._client.chat(
                system=system,
                prompt=user,
                schema=VERDICT_SCHEMA,
                temperature=self._temperature,
                num_predict=self._num_predict,
                num_ctx=self._num_ctx,
                seed=self._seed,
            )
        except OllamaError as exc:
            # Force a re-probe: this is usually the container going away.
            self.invalidate_availability()
            log.warning("llm_request_failed", symbol=ctx.symbol, error=str(exc))
            _record_failure("request")
            return None

        latency_ms = (time.perf_counter() - started) * 1000.0

        try:
            verdict = parse_verdict(response.content)
        except (VerdictParseError, ValueError) as exc:
            log.warning(
                "llm_parse_failed",
                symbol=ctx.symbol,
                error=str(exc),
                raw_preview=response.content[:300],
            )
            _record_failure("parse")
            return None

        direction = normalize_direction(verdict.direction)
        confidence = clamp_confidence(verdict.confidence)

        signals = {
            "model": "llm",
            "ollama_model": self._client.model,
            "raw_direction": verdict.direction,
            "raw_confidence": round(verdict.confidence, 4),
            "key_factors": list(verdict.key_factors),
            "risks": list(verdict.risks),
            "prompt_tokens": response.prompt_tokens,
            "completion_tokens": response.completion_tokens,
            "latency_ms": round(latency_ms, 1),
        }

        _record_success(
            latency_ms=latency_ms,
            prompt_tokens=response.prompt_tokens,
            completion_tokens=response.completion_tokens,
        )

        log.info(
            "llm_prediction",
            symbol=ctx.symbol,
            direction=direction,
            confidence=round(confidence, 4),
            latency_ms=round(latency_ms, 1),
            completion_tokens=response.completion_tokens,
        )

        return ModelResult(
            direction=direction,
            confidence=confidence,
            rationale=verdict.rationale,
            signals=signals,
            model_name=self.name,
            model_version=self.version,
            latency_ms=latency_ms,
        )


# ── Metrics plumbing ──────────────────────────────────────────────────────────
# Imported lazily so this module stays importable (and unit-testable) without a
# Prometheus registry present.


def _record_success(latency_ms: float, prompt_tokens: int, completion_tokens: int) -> None:
    try:
        from ..metrics import llm_latency_seconds, llm_requests_total, llm_tokens_total

        llm_latency_seconds.observe(latency_ms / 1000.0)
        llm_requests_total.labels(outcome="success").inc()
        llm_tokens_total.labels(kind="prompt").inc(prompt_tokens)
        llm_tokens_total.labels(kind="completion").inc(completion_tokens)
    except Exception:  # noqa: BLE001 - metrics must never break inference
        pass


def _record_failure(kind: str) -> None:
    try:
        from ..metrics import llm_requests_total

        llm_requests_total.labels(outcome=f"failure_{kind}").inc()
    except Exception:  # noqa: BLE001
        pass
