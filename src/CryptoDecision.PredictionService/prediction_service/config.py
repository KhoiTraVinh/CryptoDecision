"""
Settings loaded from environment variables.
All defaults match the docker-compose stack.
No external dependencies — plain os.environ.
"""
import os
from dataclasses import dataclass, field


def _env_float(name: str, default: float) -> float:
    try:
        return float(os.environ.get(name, default))
    except (TypeError, ValueError):
        return default


def _env_int(name: str, default: int) -> int:
    try:
        return int(os.environ.get(name, default))
    except (TypeError, ValueError):
        return default


def _env_bool(name: str, default: bool) -> bool:
    raw = os.environ.get(name)
    if raw is None:
        return default
    return raw.strip().lower() in ("1", "true", "yes", "on")


@dataclass(frozen=True)
class Settings:
    # ── PostgreSQL ────────────────────────────────────────────────────────────
    postgres_url: str = field(
        default_factory=lambda: os.environ.get(
            "POSTGRES_URL",
            "postgresql://crypto:crypto@postgres:5432/crypto"
        )
    )

    # ── Kafka ─────────────────────────────────────────────────────────────────
    kafka_bootstrap_servers: str = field(
        default_factory=lambda: os.environ.get(
            "KAFKA_BOOTSTRAP_SERVERS", "kafka:9092"
        )
    )
    kafka_topic_prefix: str = field(
        default_factory=lambda: os.environ.get(
            "KAFKA_TOPIC_PREFIX", "predictions"
        )
    )

    # ── Prediction settings ───────────────────────────────────────────────────
    symbols: tuple[str, ...] = field(
        default_factory=lambda: tuple(
            s.strip().upper()
            for s in os.environ.get("SYMBOLS", "BTCUSDT,ETHUSDT").split(",")
            if s.strip()
        )
    )
    prediction_interval_seconds: int = field(
        default_factory=lambda: _env_int("PREDICTION_INTERVAL_SECONDS", 300)
    )
    model_version: str = field(
        default_factory=lambda: os.environ.get("MODEL_VERSION", "heuristic-v1.0")
    )

    #: Days of daily history included in the LLM market brief.
    context_history_days: int = field(
        default_factory=lambda: _env_int("CONTEXT_HISTORY_DAYS", 7)
    )

    # ── Ollama / LLM ──────────────────────────────────────────────────────────
    ollama_enabled: bool = field(
        default_factory=lambda: _env_bool("OLLAMA_ENABLED", True)
    )
    ollama_base_url: str = field(
        default_factory=lambda: os.environ.get("OLLAMA_BASE_URL", "http://ollama:11434")
    )
    ollama_model: str = field(
        default_factory=lambda: os.environ.get("OLLAMA_MODEL", "qwen2.5:7b")
    )
    #: Read timeout. A 7B on CPU can take 30-60s per generation, so this is generous
    #: on purpose; the prediction cycle runs every 5 minutes and can afford to wait.
    ollama_timeout_seconds: float = field(
        default_factory=lambda: _env_float("OLLAMA_TIMEOUT_SECONDS", 120.0)
    )
    ollama_connect_timeout_seconds: float = field(
        default_factory=lambda: _env_float("OLLAMA_CONNECT_TIMEOUT_SECONDS", 5.0)
    )
    ollama_max_retries: int = field(
        default_factory=lambda: _env_int("OLLAMA_MAX_RETRIES", 2)
    )
    #: Low but non-zero: some sampling diversity avoids the model latching onto a
    #: single phrasing, while staying close to deterministic for auditability.
    ollama_temperature: float = field(
        default_factory=lambda: _env_float("OLLAMA_TEMPERATURE", 0.2)
    )
    ollama_num_predict: int = field(
        default_factory=lambda: _env_int("OLLAMA_NUM_PREDICT", 512)
    )
    #: The market brief runs ~1-2k tokens; 8k leaves comfortable headroom.
    ollama_num_ctx: int = field(
        default_factory=lambda: _env_int("OLLAMA_NUM_CTX", 8192)
    )
    ollama_seed: int = field(
        default_factory=lambda: _env_int("OLLAMA_SEED", 42)
    )
    #: Preload the model at startup so the first prediction does not pay load cost.
    ollama_warmup: bool = field(
        default_factory=lambda: _env_bool("OLLAMA_WARMUP", True)
    )

    # ── Ensemble ──────────────────────────────────────────────────────────────
    weight_llm: float = field(
        default_factory=lambda: _env_float("ENSEMBLE_WEIGHT_LLM", 0.35)
    )
    weight_xgboost: float = field(
        default_factory=lambda: _env_float("ENSEMBLE_WEIGHT_XGBOOST", 0.35)
    )
    weight_heuristic: float = field(
        default_factory=lambda: _env_float("ENSEMBLE_WEIGHT_HEURISTIC", 0.30)
    )
    #: |score| below this becomes NEUTRAL. Raise it to make the service pickier.
    ensemble_dead_zone: float = field(
        default_factory=lambda: _env_float("ENSEMBLE_DEAD_ZONE", 0.15)
    )
    ensemble_agreement_bonus: float = field(
        default_factory=lambda: _env_float("ENSEMBLE_AGREEMENT_BONUS", 0.10)
    )
    ensemble_conflict_penalty: float = field(
        default_factory=lambda: _env_float("ENSEMBLE_CONFLICT_PENALTY", 0.25)
    )
    #: Ceiling on any one model's share of the vote after abstentions are
    #: renormalised away.
    #:
    #: Renormalising over the models that answered is correct — an absent model
    #: must not drag the verdict toward NEUTRAL — but it silently promotes whoever
    #: is left. With xgboost abstaining for want of a trained model.pkl, the
    #: configured 0.35/0.35/0.30 became llm 0.538 / heuristic 0.462: a single 7B
    #: model holding the majority and deciding direction on its own whenever the
    #: heuristic disagreed. Nobody chose that; it fell out of the arithmetic.
    #:
    #: The cap makes the intent explicit instead — no single model outvotes the
    #: rest combined. It binds only while models are missing: at the configured
    #: three-way split the largest share is 0.35, well under the ceiling, so a
    #: trained xgboost lifts the cap on its own with nothing to revert.
    ensemble_max_single_weight: float = field(
        default_factory=lambda: _env_float("ENSEMBLE_MAX_SINGLE_WEIGHT", 0.50)
    )

    # ── Health server ─────────────────────────────────────────────────────────
    health_port: int = field(
        default_factory=lambda: _env_int("HEALTH_PORT", 8080)
    )

    def kafka_topic(self, symbol: str) -> str:
        """e.g. 'predictions.btcusdt'"""
        return f"{self.kafka_topic_prefix}.{symbol.lower()}"

    def ensemble_weights(self) -> dict[str, float]:
        """Model name → weight, matching PredictionModel.name values."""
        return {
            "llm": self.weight_llm if self.ollama_enabled else 0.0,
            "xgboost": self.weight_xgboost,
            "heuristic": self.weight_heuristic,
        }


# Module-level singleton — import from anywhere
settings = Settings()
