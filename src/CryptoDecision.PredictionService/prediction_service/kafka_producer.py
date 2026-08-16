"""
Thin Confluent Kafka producer wrapper.

Features:
  - Idempotent producer (exactly-once delivery semantics at broker level)
  - acks=all, lz4 compression, 10 ms linger
  - Lazy init — only connects when first publish() call is made
  - Module-level singleton via get_producer()
"""
from __future__ import annotations

import json
from typing import Any

import structlog
from confluent_kafka import Producer

from .config import settings

log = structlog.get_logger(__name__)

_producer: Producer | None = None


def _make_producer() -> Producer:
    conf = {
        "bootstrap.servers": settings.kafka_bootstrap_servers,
        "enable.idempotence": True,
        "acks": "all",
        "compression.type": "lz4",
        "linger.ms": 10,
        "retries": 5,
        "retry.backoff.ms": 500,
    }
    return Producer(conf)


def get_producer() -> Producer:
    global _producer  # noqa: PLW0603
    if _producer is None:
        _producer = _make_producer()
    return _producer


def _delivery_report(err: object, msg: object) -> None:
    if err:
        log.error("kafka_delivery_failed", error=str(err))
    else:
        log.debug("kafka_delivered", topic=getattr(msg, "topic", lambda: "?")())


def publish(topic: str, key: str, payload: dict[str, Any]) -> None:
    """
    Serialize *payload* as JSON and produce to *topic*.
    Calls flush() to ensure the message is delivered before returning.
    """
    producer = get_producer()
    value = json.dumps(payload, default=str).encode()
    producer.produce(
        topic=topic,
        key=key.encode(),
        value=value,
        callback=_delivery_report,
    )
    producer.flush()
