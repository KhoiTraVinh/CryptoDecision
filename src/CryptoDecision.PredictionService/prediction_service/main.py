"""
Entry point: python -m prediction_service.main

1. Configure structured logging
2. Start Prometheus metrics server on port 9091 (daemon thread)
3. Start health HTTP server in daemon thread
4. Warm up the Ollama model so the first prediction is not the cold one
5. Run one prediction cycle immediately
6. Report whether XGBoost has enough history to be trained
7. Run the prediction cycle every N seconds (default 150), measured from the start
   of each cycle so inference latency is absorbed rather than added
8. Schedule XGBoost retrain every Sunday at 02:00 UTC
9. Block in the deadline loop
"""
from __future__ import annotations

import time

import schedule
import structlog
import structlog.dev

from .config import settings
from .health import start_health_server
from .metrics import start_metrics_server
from .predictor import run_prediction_cycle
from .registry import warmup_llm


def _configure_logging() -> None:
    structlog.configure(
        processors=[
            structlog.stdlib.add_log_level,
            structlog.processors.TimeStamper(fmt="iso"),
            structlog.processors.StackInfoRenderer(),
            structlog.processors.format_exc_info,
            structlog.processors.JSONRenderer(),
        ],
        wrapper_class=structlog.make_filtering_bound_logger(20),  # INFO
        context_class=dict,
        logger_factory=structlog.PrintLoggerFactory(),
    )


def main() -> None:
    _configure_logging()
    log = structlog.get_logger(__name__)

    log.info(
        "prediction_service_starting",
        symbols=settings.symbols,
        interval_seconds=settings.prediction_interval_seconds,
        llm_enabled=settings.ollama_enabled,
        llm_model=settings.ollama_model if settings.ollama_enabled else None,
        ensemble_weights=settings.ensemble_weights(),
    )

    # Prometheus metrics on port 9091 — pull-based, daemon thread
    start_metrics_server()
    log.info("metrics_server_started", port=9091)

    start_health_server()
    log.info("health_server_started", port=settings.health_port)

    # Pull the 7B into Ollama's memory before the first real prediction, so the
    # cold-start load cost is paid here rather than inside a scheduled cycle.
    # Non-fatal: a failure here only means the first prediction is slower.
    if settings.ollama_enabled and settings.ollama_warmup:
        try:
            warmup_llm()
        except Exception:
            log.exception("llm_warmup_failed")

    # Run once at startup so predictions are available immediately
    try:
        run_prediction_cycle()
    except Exception:
        log.exception("startup_cycle_failed")

    # Report whether XGBoost can train at all, every boot.
    #
    # It abstains until a model.pkl exists, which renormalises its configured share
    # onto whoever is left — so a model that can never train is not a quiet gap, it
    # silently reweights the ensemble. The weekly retrain does log its refusal, but
    # only at 02:00 on a Sunday, which is nobody's idea of a place to look.
    _log_training_readiness(log)

    # Weekly retrain every Sunday at 02:00 UTC
    # Runs synchronously in the main thread — training takes ~1-2s for 180 rows,
    # so there is no need for a separate thread and zero RAM overhead at idle.
    schedule.every().sunday.at("02:00").do(_safe_retrain, log=log)

    interval = settings.prediction_interval_seconds

    log.info(
        "scheduler_started",
        interval_seconds=interval,
        retrain_schedule="every Sunday 02:00 UTC",
    )

    # The prediction cycle is driven from an explicit deadline rather than handed to
    # `schedule`, because schedule sets the next run to `now + interval` *after* the
    # job returns. With a 45s LLM generation inside a 150s interval that produced a
    # real cadence of ~195s: the configured number was a gap between runs, not a
    # period, and the achieved rate silently tracked LLM latency. Anchoring on the
    # intended start absorbs the job's duration into the interval instead.
    #
    # One interval out, not now: the startup cycle above has already run, and seeding
    # this at the current time fired a second identical cycle on the spot — two 45s
    # generations back to back for one prediction, which `schedule` had avoided by
    # waiting an interval before its first run.
    next_run = time.monotonic() + interval

    while True:
        # Weekly retrain still goes through `schedule` — a wall-clock day-and-time
        # rule is exactly what it is good at.
        schedule.run_pending()

        now = time.monotonic()
        if now >= next_run:
            started = now
            _safe_run(log=log)
            elapsed = time.monotonic() - started

            next_run = started + interval

            # A cycle that overran its own interval must not leave a backlog of
            # missed slots to burn through back-to-back; skip to the next future
            # one and say so, because it means the interval is set below what
            # inference actually costs.
            if next_run <= time.monotonic():
                log.warning(
                    "prediction_cycle_overran_interval",
                    elapsed_seconds=round(elapsed, 1),
                    interval_seconds=interval,
                )
                next_run = time.monotonic() + interval

        # One second, not ten: the old ten-second poll added up to 10s of jitter on
        # top of every cycle. A wakeup per second costs nothing measurable.
        time.sleep(1)


def _log_training_readiness(log) -> None:
    """Log whether the quantitative model has enough history to be trained."""
    try:
        from .train import readiness

        samples, required, symbols = readiness()
    except Exception as exc:  # noqa: BLE001 - diagnostics must not block startup
        log.warning("training_readiness_unknown", error=str(exc))
        return

    if samples >= required:
        log.info("xgboost_trainable", samples=samples, required=required)
        return

    # The wait is the shortfall divided by the daily rate, and the rate is the symbol
    # count — the training query pools every symbol, so three symbols accrue three
    # samples a day. Reporting the shortfall as days would have said 27 when the real
    # answer was 9.
    per_day = max(1, symbols)
    log.warning(
        "xgboost_cannot_train",
        samples=samples,
        required=required,
        samples_short=required - samples,
        symbols_contributing=symbols,
        est_days_until_trainable=-(-(required - samples) // per_day),  # ceil
        consequence="xgboost abstains; its configured weight renormalises onto the "
                    "models that do answer",
    )


def _safe_run(log: object) -> None:
    try:
        run_prediction_cycle()
    except Exception:
        structlog.get_logger(__name__).exception("prediction_cycle_failed")


def _safe_retrain(log: object) -> None:
    try:
        structlog.get_logger(__name__).info("weekly_retrain_starting")
        from .train import train
        train()
        structlog.get_logger(__name__).info("weekly_retrain_complete")
    except Exception:
        structlog.get_logger(__name__).exception("weekly_retrain_failed")


if __name__ == "__main__":
    main()
