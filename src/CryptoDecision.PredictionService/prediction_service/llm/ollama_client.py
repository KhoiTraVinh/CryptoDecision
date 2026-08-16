"""
Minimal Ollama HTTP client built on the standard library.

No new dependency: urllib is enough for a handful of JSON POSTs, and keeping the
Python image free of httpx/requests matters more than the ergonomics here.

Handles the three things that actually break in practice:
  * Cold start — the first generate after container boot pays the model load cost
    (several seconds for a 7B), so connect and read timeouts are separate and the
    read timeout is generous.
  * Structured output — Ollama gained JSON-schema `format` in 0.5. Older builds
    only accept the string "json". The client tries schema first and transparently
    downgrades once per process if the server rejects it.
  * Transient failure — retries with exponential backoff, then gives up so the
    caller can fall back to a quantitative model instead of stalling the cycle.
"""
from __future__ import annotations

import json
import socket
import time
import urllib.error
import urllib.request
from dataclasses import dataclass

import structlog

log = structlog.get_logger(__name__)


@dataclass(frozen=True)
class OllamaResponse:
    content: str
    prompt_tokens: int = 0
    completion_tokens: int = 0
    total_duration_ms: float = 0.0
    load_duration_ms: float = 0.0


class OllamaError(RuntimeError):
    """Raised when Ollama cannot serve a request after all retries."""


class OllamaClient:
    def __init__(
        self,
        base_url: str,
        model: str,
        *,
        connect_timeout: float = 5.0,
        read_timeout: float = 120.0,
        max_retries: int = 2,
        retry_backoff: float = 2.0,
    ) -> None:
        self.base_url = base_url.rstrip("/")
        self.model = model
        self.connect_timeout = connect_timeout
        self.read_timeout = read_timeout
        self.max_retries = max_retries
        self.retry_backoff = retry_backoff
        #: Set once the server rejects a JSON-schema `format`, so we stop trying.
        self._schema_unsupported = False

    # ── Low-level transport ───────────────────────────────────────────────────

    def _post(self, path: str, payload: dict, timeout: float) -> dict:
        body = json.dumps(payload).encode()
        req = urllib.request.Request(
            f"{self.base_url}{path}",
            data=body,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=timeout) as resp:  # noqa: S310
            return json.loads(resp.read().decode())

    def _get(self, path: str, timeout: float) -> dict:
        req = urllib.request.Request(f"{self.base_url}{path}", method="GET")
        with urllib.request.urlopen(req, timeout=timeout) as resp:  # noqa: S310
            return json.loads(resp.read().decode())

    # ── Health / readiness ────────────────────────────────────────────────────

    def is_up(self) -> bool:
        """True when the Ollama HTTP API answers at all."""
        try:
            self._get("/api/tags", timeout=self.connect_timeout)
            return True
        except (urllib.error.URLError, socket.timeout, OSError, ValueError):
            return False

    def has_model(self) -> bool:
        """True when the configured model is present in the local model store."""
        try:
            tags = self._get("/api/tags", timeout=self.connect_timeout)
        except (urllib.error.URLError, socket.timeout, OSError, ValueError):
            return False

        wanted = self.model.lower()
        for entry in tags.get("models") or []:
            name = str(entry.get("name") or "").lower()
            # "qwen2.5:7b" should match a server reporting "qwen2.5:7b" exactly,
            # and a bare "qwen2.5" config should match the ":latest" tag.
            if name == wanted or name.split(":")[0] == wanted.split(":")[0]:
                return True
        return False

    def warmup(self) -> bool:
        """
        Force the model into memory with a one-token generation.

        Called at startup so the first real prediction is not the one that pays
        the multi-second load cost.
        """
        try:
            self._post(
                "/api/generate",
                {
                    "model": self.model,
                    "prompt": "ok",
                    "stream": False,
                    "options": {"num_predict": 1},
                },
                timeout=self.read_timeout,
            )
            return True
        except Exception as exc:  # noqa: BLE001 - warmup is best-effort
            log.warning("ollama_warmup_failed", error=str(exc), model=self.model)
            return False

    # ── Chat completion ───────────────────────────────────────────────────────

    def chat(
        self,
        system: str,
        prompt: str,
        *,
        schema: dict | None = None,
        temperature: float = 0.2,
        num_predict: int = 512,
        num_ctx: int = 8192,
        seed: int | None = 42,
    ) -> OllamaResponse:
        """
        Send a single-turn chat request and return the assistant message.

        `schema` requests JSON-schema-constrained decoding. When the server is too
        old to support it the client falls back to plain JSON mode, which Qwen 2.5
        follows reliably given an explicit schema in the prompt.

        Raises OllamaError after exhausting retries.
        """
        options: dict = {
            "temperature": temperature,
            "num_predict": num_predict,
            "num_ctx": num_ctx,
        }
        if seed is not None:
            # A fixed seed makes runs reproducible, which matters when you are
            # trying to attribute a bad trade to a bad signal.
            options["seed"] = seed

        last_error: Exception | None = None

        for attempt in range(self.max_retries + 1):
            payload: dict = {
                "model": self.model,
                "messages": [
                    {"role": "system", "content": system},
                    {"role": "user", "content": prompt},
                ],
                "stream": False,
                "options": options,
            }

            if schema is not None and not self._schema_unsupported:
                payload["format"] = schema
            elif schema is not None:
                payload["format"] = "json"

            try:
                started = time.perf_counter()
                data = self._post("/api/chat", payload, timeout=self.read_timeout)
                elapsed_ms = (time.perf_counter() - started) * 1000.0

                content = (data.get("message") or {}).get("content") or ""
                if not content.strip():
                    raise OllamaError("Ollama returned an empty message")

                # Ollama reports durations in nanoseconds; fall back to the
                # wall-clock measurement if the server omitted them.
                reported_ms = float(data.get("total_duration") or 0) / 1e6

                return OllamaResponse(
                    content=content,
                    prompt_tokens=int(data.get("prompt_eval_count") or 0),
                    completion_tokens=int(data.get("eval_count") or 0),
                    total_duration_ms=reported_ms if reported_ms > 0 else elapsed_ms,
                    load_duration_ms=float(data.get("load_duration") or 0) / 1e6,
                )

            except urllib.error.HTTPError as exc:
                detail = ""
                try:
                    detail = exc.read().decode()[:400]
                except Exception:  # noqa: BLE001 - error body is best-effort
                    pass

                # A 400 while sending a JSON schema means this server predates
                # structured outputs. Downgrade once and retry immediately.
                if (
                    exc.code == 400
                    and schema is not None
                    and not self._schema_unsupported
                ):
                    self._schema_unsupported = True
                    log.warning(
                        "ollama_schema_unsupported_downgrading_to_json_mode",
                        detail=detail,
                    )
                    continue

                last_error = OllamaError(f"HTTP {exc.code}: {detail}")

            except (urllib.error.URLError, socket.timeout, OSError) as exc:
                last_error = OllamaError(f"transport failure: {exc}")

            except (ValueError, OllamaError) as exc:
                last_error = OllamaError(str(exc))

            if attempt < self.max_retries:
                delay = self.retry_backoff**attempt
                log.warning(
                    "ollama_request_retrying",
                    attempt=attempt + 1,
                    max_retries=self.max_retries,
                    delay_seconds=delay,
                    error=str(last_error),
                )
                time.sleep(delay)

        raise OllamaError(f"Ollama request failed after {self.max_retries + 1} attempts: {last_error}")
