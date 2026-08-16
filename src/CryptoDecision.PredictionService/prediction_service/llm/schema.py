"""
The JSON contract between the LLM and the rest of the pipeline, plus a tolerant
parser for it.

Even with constrained decoding the output has to be treated as untrusted: schema
mode is unavailable on older Ollama builds, and in plain JSON mode a 7B model will
occasionally wrap its answer in a markdown fence or add a sentence of preamble.
parse_verdict() recovers from all of those, and raises VerdictParseError only when
there is genuinely no usable object — at which point the caller falls back to a
quantitative model.
"""
from __future__ import annotations

import json
from dataclasses import dataclass, field

# Passed to Ollama as `format` for schema-constrained decoding, and rendered into
# the prompt so the model still knows the shape when running in plain JSON mode.
VERDICT_SCHEMA: dict = {
    "type": "object",
    "properties": {
        "direction": {
            "type": "string",
            "enum": ["UP", "DOWN", "NEUTRAL"],
        },
        "confidence": {
            "type": "number",
            "minimum": 0.0,
            "maximum": 1.0,
        },
        "rationale": {
            "type": "string",
        },
        "key_factors": {
            "type": "array",
            "items": {"type": "string"},
        },
        "risks": {
            "type": "array",
            "items": {"type": "string"},
        },
    },
    "required": ["direction", "confidence", "rationale"],
}


class VerdictParseError(ValueError):
    """Raised when no usable JSON verdict could be recovered from model output."""


@dataclass(frozen=True)
class LlmVerdict:
    direction: str
    confidence: float
    rationale: str
    key_factors: tuple[str, ...] = field(default_factory=tuple)
    risks: tuple[str, ...] = field(default_factory=tuple)


def _extract_json_object(text: str) -> str:
    """
    Return the first balanced top-level JSON object in *text*.

    Brace counting is string-aware, so a `{` inside a rationale string does not
    throw off the depth count. This is what makes fenced or prose-wrapped output
    recoverable rather than fatal.
    """
    start = text.find("{")
    if start == -1:
        raise VerdictParseError("no '{' found in model output")

    depth = 0
    in_string = False
    escaped = False

    for i in range(start, len(text)):
        ch = text[i]

        if in_string:
            if escaped:
                escaped = False
            elif ch == "\\":
                escaped = True
            elif ch == '"':
                in_string = False
            continue

        if ch == '"':
            in_string = True
        elif ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return text[start : i + 1]

    raise VerdictParseError("unbalanced braces in model output")


def _as_str_tuple(value: object, limit: int = 6) -> tuple[str, ...]:
    """Coerce a model-supplied list field to a bounded tuple of clean strings."""
    if isinstance(value, str):
        value = [value]
    if not isinstance(value, (list, tuple)):
        return ()
    out: list[str] = []
    for item in value:
        text = str(item).strip()
        if text:
            out.append(text[:300])
        if len(out) >= limit:
            break
    return tuple(out)


def parse_verdict(raw: str) -> LlmVerdict:
    """
    Parse model output into an LlmVerdict.

    Field-level coercion is deliberately forgiving — a model that returns
    confidence as the string "0.72", or as 72 meaning percent, still produces a
    usable verdict. Direction is validated by the caller against the canonical
    vocabulary, so this only normalises case here.
    """
    text = (raw or "").strip()
    if not text:
        raise VerdictParseError("empty model output")

    # Strip a markdown fence if the model added one despite JSON mode.
    if text.startswith("```"):
        fence_end = text.find("\n")
        if fence_end != -1:
            text = text[fence_end + 1 :]
        if text.rstrip().endswith("```"):
            text = text.rstrip()[:-3]

    try:
        obj = json.loads(text)
    except json.JSONDecodeError:
        obj = json.loads(_extract_json_object(text))

    if not isinstance(obj, dict):
        raise VerdictParseError(f"expected a JSON object, got {type(obj).__name__}")

    if "direction" not in obj:
        raise VerdictParseError("model output is missing the required 'direction' field")

    # Confidence: accept float, numeric string, or a 0-100 percentage.
    raw_conf = obj.get("confidence", 0.5)
    try:
        confidence = float(raw_conf)
    except (TypeError, ValueError):
        confidence = 0.5
    if confidence != confidence:  # NaN
        confidence = 0.5
    if confidence > 1.0:
        confidence = confidence / 100.0 if confidence <= 100.0 else 1.0
    confidence = max(0.0, min(1.0, confidence))

    rationale = str(obj.get("rationale") or "").strip()
    if len(rationale) > 1200:
        rationale = rationale[:1197] + "..."

    return LlmVerdict(
        direction=str(obj.get("direction") or "").strip().upper(),
        confidence=confidence,
        rationale=rationale or "No rationale supplied by the model.",
        key_factors=_as_str_tuple(obj.get("key_factors")),
        risks=_as_str_tuple(obj.get("risks")),
    )
