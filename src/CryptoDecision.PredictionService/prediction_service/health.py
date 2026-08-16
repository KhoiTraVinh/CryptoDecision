"""
Minimal HTTP health endpoint using only stdlib.
Runs in a background daemon thread — does not block the main loop.

/health  — liveness. Stays 200 even when Ollama is down: the service is still
           doing useful work via the quantitative models, and returning 503 would
           make Docker restart a container that is functioning as designed.
/ready   — readiness, including LLM reachability. Returns 503 when the LLM is
           enabled but unreachable, so orchestration and dashboards can tell the
           difference between "healthy" and "healthy but degraded".
"""
import json
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer

from .config import settings


def _llm_snapshot() -> dict:
    """Never let a health probe raise — report the failure as data instead."""
    try:
        from .registry import llm_status

        return llm_status()
    except Exception as exc:  # noqa: BLE001
        return {"enabled": settings.ollama_enabled, "available": False, "error": str(exc)}


class _HealthHandler(BaseHTTPRequestHandler):
    def _respond(self, status: int, body: dict) -> None:
        payload = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self) -> None:
        if self.path == "/health":
            self._respond(200, {"status": "healthy"})

        elif self.path == "/ready":
            llm = _llm_snapshot()
            degraded = bool(llm.get("enabled")) and not bool(llm.get("available"))
            self._respond(
                503 if degraded else 200,
                {
                    "status": "degraded" if degraded else "ready",
                    "llm": llm,
                    "symbols": list(settings.symbols),
                },
            )

        else:
            self.send_response(404)
            self.end_headers()

    # Suppress default request log lines to stdout
    def log_message(self, format: str, *args: object) -> None:  # noqa: A002
        pass


def start_health_server() -> None:
    """Start the health server in a daemon thread. Returns immediately."""
    server = HTTPServer(("0.0.0.0", settings.health_port), _HealthHandler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
