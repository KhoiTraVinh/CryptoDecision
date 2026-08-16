"""Ollama transport, prompt construction and the LLM output contract."""
from .ollama_client import OllamaClient, OllamaError, OllamaResponse
from .prompt import build_messages, build_user_prompt
from .schema import VERDICT_SCHEMA, LlmVerdict, VerdictParseError, parse_verdict

__all__ = [
    "OllamaClient",
    "OllamaError",
    "OllamaResponse",
    "build_messages",
    "build_user_prompt",
    "VERDICT_SCHEMA",
    "LlmVerdict",
    "VerdictParseError",
    "parse_verdict",
]
