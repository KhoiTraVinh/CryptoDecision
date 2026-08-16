"""
Prompt construction for the Qwen 2.5 market analyst.

Design notes
------------
The LLM is deliberately kept *independent* of the other models: it never sees the
heuristic score or the XGBoost probabilities. If it did, the ensemble would be
averaging two correlated opinions and double-counting the same evidence. The LLM
reasons from market data alone; ensemble.py does the combining.

Two failure modes dominate when a 7B model is asked to call market direction, and
the system prompt targets both explicitly:

  Overconfidence — small instruct models emit 0.9 confidence almost by reflex.
  The prompt defines what each confidence band means and ties the top band to
  evidence that rarely exists, which pulls the distribution back toward honest.

  Refusal to say "don't know" — models pattern-match "predict the direction" into
  always picking a side. NEUTRAL is stated as the correct answer for mixed
  evidence, and the base rate is spelled out so a coin-flip reads as a coin-flip.
"""
from __future__ import annotations

import json

from ..context import MarketContext
from .schema import VERDICT_SCHEMA

SYSTEM_PROMPT = """\
You are a quantitative market analyst. You are given a structured snapshot of \
cryptocurrency market data and must judge the likely direction of the next 24 hours.

HOW TO REASON
- Weigh the evidence you are given. Do not invent news, tweets, macro events, or \
price levels that are not in the snapshot. You have no information beyond it.
- Order flow (who is crossing the spread) and whale activity are usually more \
informative than headline return alone. A price rise on shrinking volume and \
selling flow is weak; a rise confirmed by buying flow and whale accumulation is \
strong.
- Read the order flow percentages against a 50% baseline. A figure like "18% of \
volume were aggressive buys" means the tape is dominated by SELLING, not buying — \
82% of the volume hit the bid. Each flow line names its dominant side; trust that \
label and check it against the percentage before describing the tape.
- Look for agreement across timeframes. When 5m, 15m and 1h flow all lean the same \
way, that is a real signal. When they conflict, that is noise.
- Consider mean reversion: a very large move on elevated volatility often retraces.

CALIBRATION - THIS MATTERS MORE THAN BEING DECISIVE
- Daily crypto direction is close to a coin flip. The honest base rate is ~50%.
- NEUTRAL is a correct and valuable answer when evidence is mixed or thin. Most \
snapshots deserve NEUTRAL. Do not force a side.
- Use these confidence bands and respect them:
    0.35-0.45  evidence is thin, conflicting, or the sample is small
    0.45-0.60  a mild lean, several indicators agree weakly
    0.60-0.75  a clear lean, most indicators agree and flow confirms
    0.75-0.90  reserved for strong multi-timeframe agreement with whale \
confirmation and a supporting volatility regime. This is rare.
- Never exceed 0.90. If you find yourself at the top of the range, ask what would \
have to be true for you to be wrong, and lower it.

OUTPUT
Reply with a single JSON object and nothing else. No markdown fence, no preamble.
"""


def _schema_hint() -> str:
    """
    Render the schema into the prompt.

    Redundant when the server supports schema-constrained decoding, essential when
    the client has downgraded to plain JSON mode.
    """
    return json.dumps(
        {
            "direction": "UP | DOWN | NEUTRAL",
            "confidence": "number between 0.35 and 0.90",
            "rationale": "two or three sentences citing the specific numbers that drove the call",
            "key_factors": ["short phrase", "short phrase"],
            "risks": ["what would invalidate this call"],
        },
        indent=2,
    )


def build_user_prompt(ctx: MarketContext) -> str:
    """Assemble the market brief plus task instructions for one symbol."""
    return f"""\
Analyse the following market snapshot and predict the direction over the next 24 hours.

===== MARKET SNAPSHOT =====
{ctx.describe()}
===== END SNAPSHOT =====

Respond with a JSON object in exactly this shape:
{_schema_hint()}

Rules:
- "direction" must be one of UP, DOWN, NEUTRAL.
- "confidence" must reflect the calibration bands you were given. Thin or \
conflicting evidence means a low number and probably NEUTRAL.
- "rationale" must cite specific figures from the snapshot above. Do not repeat \
the instructions back.
"""


def build_messages(ctx: MarketContext) -> tuple[str, str]:
    """Return (system, user) for OllamaClient.chat."""
    return SYSTEM_PROMPT, build_user_prompt(ctx)


__all__ = ["SYSTEM_PROMPT", "VERDICT_SCHEMA", "build_messages", "build_user_prompt"]
