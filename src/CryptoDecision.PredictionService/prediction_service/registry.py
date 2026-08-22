"""
Composition root: builds the model set and the ensemble from settings.

Kept separate from predictor.py so the wiring is declared in one place and the
prediction cycle stays free of construction logic. main.py reaches in here for
warmup, and health.py for liveness reporting, rather than each rebuilding clients.
"""
from __future__ import annotations

import threading

import structlog

from .config import settings
from .ensemble import Ensemble
from .llm.ollama_client import OllamaClient
from .models import HeuristicModel, LlmModel, XgboostModel

log = structlog.get_logger(__name__)

_lock = threading.Lock()
_ensemble: Ensemble | None = None
_llm_model: LlmModel | None = None


def _build() -> tuple[Ensemble, LlmModel | None]:
    models: list = []
    llm: LlmModel | None = None

    if settings.ollama_enabled:
        client = OllamaClient(
            base_url=settings.ollama_base_url,
            model=settings.ollama_model,
            connect_timeout=settings.ollama_connect_timeout_seconds,
            read_timeout=settings.ollama_timeout_seconds,
            max_retries=settings.ollama_max_retries,
        )
        llm = LlmModel(
            client,
            temperature=settings.ollama_temperature,
            num_predict=settings.ollama_num_predict,
            num_ctx=settings.ollama_num_ctx,
            seed=settings.ollama_seed,
        )
        models.append(llm)
    else:
        log.info("llm_disabled_by_config")

    # Ordered quantitative models. XGBoost abstains until a model.pkl exists;
    # the heuristic always answers, so the ensemble can never come back empty.
    models.append(XgboostModel())
    models.append(HeuristicModel())

    ensemble = Ensemble(
        models=models,
        weights=settings.ensemble_weights(),
        dead_zone=settings.ensemble_dead_zone,
        agreement_bonus=settings.ensemble_agreement_bonus,
        conflict_penalty=settings.ensemble_conflict_penalty,
        max_single_weight=settings.ensemble_max_single_weight,
    )

    # available_at_build is not the same as "will vote": XgboostModel reports
    # unavailable until a model.pkl exists, so logging the model list alone made a
    # permanently-abstaining model look like a participant.
    log.info(
        "ensemble_built",
        models=[m.name for m in models],
        available=[m.name for m in models if m.is_available()],
        weights=settings.ensemble_weights(),
        dead_zone=settings.ensemble_dead_zone,
        max_single_weight=settings.ensemble_max_single_weight,
    )
    return ensemble, llm


def get_ensemble() -> Ensemble:
    """Process-wide ensemble singleton."""
    global _ensemble, _llm_model
    with _lock:
        if _ensemble is None:
            _ensemble, _llm_model = _build()
        return _ensemble


def get_llm_model() -> LlmModel | None:
    """The LLM model instance, or None when Ollama is disabled by config."""
    get_ensemble()  # ensure construction has happened
    return _llm_model


def warmup_llm() -> bool:
    """
    Preload the model into Ollama's memory.

    Best-effort and non-fatal: a cold first prediction is slower, not broken.
    """
    llm = get_llm_model()
    if llm is None:
        return False

    ok = llm.warmup()
    log.info(
        "llm_warmup_finished",
        ok=ok,
        model=settings.ollama_model,
        base_url=settings.ollama_base_url,
    )
    return ok


def llm_status() -> dict:
    """Snapshot for the health endpoint."""
    llm = get_llm_model()
    if llm is None:
        return {"enabled": False, "available": False, "model": None}

    available = llm.is_available()
    try:
        from .metrics import llm_available

        llm_available.set(1 if available else 0)
    except Exception:  # noqa: BLE001 - metrics must not break health checks
        pass

    return {
        "enabled": True,
        "available": available,
        "model": settings.ollama_model,
        "base_url": settings.ollama_base_url,
    }
