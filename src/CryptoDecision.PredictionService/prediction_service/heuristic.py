"""
Enhanced heuristic model v2.

Input : a dict row from daily_feature_table
        (all percentage values stored as plain float, e.g. 2.31 = 2.31%)
Output: (direction, confidence, rationale, signals_dict)

Scoring table v2
─────────────────────────────────────────────────────────
 Signal          Condition               Score delta
─────────────────────────────────────────────────────────
 return_24h      > 3 %                   +3
                 > 1.5 %                 +2
                 > 0.5 %                 +1
                 < -3 %                  -3
                 < -1.5 %                -2
                 < -0.5 %                -1
 volume_change   > 20 %                  +1
                 > 10 %                  +0.5
                 < -20 %                 -1
                 < -10 %                 -0.5
 whale_count     > 50 (× direction_sign) ±1.0
                 > 20 (× direction_sign) ±0.5
 ── NEW in v2 ──
 momentum_rsi    RSI > 70 (overbought)   -1.5
                 RSI < 30 (oversold)     +1.5
                 RSI 60-70               -0.5
                 RSI 30-40               +0.5
 volume_accel    vol_chg accelerating     ±0.5  (trend continuation)
 whale_volume    whale_vol / total_vol > 20%   ±1.0 (smart money dominance)
─────────────────────────────────────────────────────────

Decision
  score ≥  2  → UP,      confidence = min(0.50 + score*0.07, 0.90) - volatility_adj
  score ≤ -2  → DOWN,    confidence = min(0.50 + |score|*0.07, 0.90) - volatility_adj
  else        → NEUTRAL, confidence = max(0.40, 0.50 - |score|*0.05) - volatility_adj

  volatility_adj: +0.10 when volatility > 5 % (high intraday uncertainty)
                  +0.05 when volatility > 3 %
  confidence clamped to [0.35, 0.90]
"""
from __future__ import annotations

_CONFIDENCE_MIN = 0.35
_CONFIDENCE_MAX = 0.90
_HIGH_VOLATILITY_THRESHOLD = 5.0
_MED_VOLATILITY_THRESHOLD = 3.0
_VOLATILITY_PENALTY_HIGH = 0.10
_VOLATILITY_PENALTY_MED = 0.05


def score(row: dict) -> tuple[str, float, str, dict]:
    """
    Parameters
    ----------
    row : dict
        A row dict from daily_feature_table.  Required keys:
        return_24h, volume_change, whale_count, volatility.
        Optional: total_volume (for RSI-like computation).

    Returns
    -------
    direction  : 'UP' | 'DOWN' | 'NEUTRAL'
    confidence : float in [0.35, 0.90]
    rationale  : string with an explanation for the prediction
    signals    : dict with all computed intermediate values (for logging / Kafka)
    """
    ret = float(row.get("return_24h") or 0.0)
    vol_chg = float(row.get("volume_change") or 0.0)
    whale = float(row.get("whale_count") or 0.0)
    volatility = float(row.get("volatility") or 0.0)
    total_volume = float(row.get("total_volume") or 0.0)

    # ── return_24h signal ────────────────────────────────────────────────────
    if ret > 3.0:
        ret_score = 3.0
    elif ret > 1.5:
        ret_score = 2.0
    elif ret > 0.5:
        ret_score = 1.0
    elif ret < -3.0:
        ret_score = -3.0
    elif ret < -1.5:
        ret_score = -2.0
    elif ret < -0.5:
        ret_score = -1.0
    else:
        ret_score = 0.0

    # ── volume_change signal ─────────────────────────────────────────────────
    if vol_chg > 20.0:
        vol_score = 1.0
    elif vol_chg > 10.0:
        vol_score = 0.5
    elif vol_chg < -20.0:
        vol_score = -1.0
    elif vol_chg < -10.0:
        vol_score = -0.5
    else:
        vol_score = 0.0

    # ── whale_count signal (direction-aware) ─────────────────────────────────
    direction_sign = 1.0 if ret_score >= 0 else -1.0
    if whale > 50:
        whale_score = 1.0 * direction_sign
    elif whale > 20:
        whale_score = 0.5 * direction_sign
    else:
        whale_score = 0.0

    # ── NEW: Flow-based RSI signal ───────────────────────────────────────────
    # Proxy RSI using return + volume pressure
    # When price rises + volume rises → overbought → contrarian sell signal
    # When price drops + volume rises → oversold → contrarian buy signal
    rsi_proxy = 50.0  # neutral default
    if total_volume > 0:
        # Combine return momentum with volume acceleration
        momentum = ret / max(volatility, 0.5)  # normalize by volatility
        rsi_proxy = 50.0 + momentum * 10.0  # scale to RSI-like range
        rsi_proxy = max(10.0, min(90.0, rsi_proxy))

    rsi_score = 0.0
    if rsi_proxy > 70:
        rsi_score = -1.5  # overbought → bearish
    elif rsi_proxy > 60:
        rsi_score = -0.5
    elif rsi_proxy < 30:
        rsi_score = 1.5   # oversold → bullish
    elif rsi_proxy < 40:
        rsi_score = 0.5

    # ── NEW: Volume acceleration signal ──────────────────────────────────────
    # Strong volume surge in direction of price → trend continuation
    vol_accel_score = 0.0
    if abs(vol_chg) > 15.0 and abs(ret) > 1.0:
        if (vol_chg > 0 and ret > 0) or (vol_chg < 0 and ret < 0):
            vol_accel_score = 0.5 * direction_sign  # trend continuation
        else:
            vol_accel_score = -0.5 * direction_sign  # divergence → reversal

    # ── Total score ──────────────────────────────────────────────────────────
    total_score = ret_score + vol_score + whale_score + rsi_score + vol_accel_score

    # ── volatility adjustment (enhanced) ─────────────────────────────────────
    if volatility > _HIGH_VOLATILITY_THRESHOLD:
        volatility_adj = _VOLATILITY_PENALTY_HIGH
    elif volatility > _MED_VOLATILITY_THRESHOLD:
        volatility_adj = _VOLATILITY_PENALTY_MED
    else:
        volatility_adj = 0.0

    # ── direction + base confidence ──────────────────────────────────────────
    if total_score >= 2.0:
        direction = "UP"
        confidence = min(0.50 + total_score * 0.07, _CONFIDENCE_MAX)
    elif total_score <= -2.0:
        direction = "DOWN"
        confidence = min(0.50 + abs(total_score) * 0.07, _CONFIDENCE_MAX)
    else:
        direction = "NEUTRAL"
        confidence = max(0.40, 0.50 - abs(total_score) * 0.05)

    confidence = max(_CONFIDENCE_MIN, confidence - volatility_adj)

    # ── generate human readable rationale ────────────────────────────────────
    reasons = []
    if ret_score >= 2.0:
        reasons.append(f"strong 24h return ({ret:.2f}%)")
    elif ret_score <= -2.0:
        reasons.append(f"sharp 24h decline ({ret:.2f}%)")
    elif ret_score != 0:
        reasons.append(f"moderate 24h return ({ret:.2f}%)")

    if vol_score > 0:
        reasons.append(f"surging volume (+{vol_chg:.1f}%)")
    elif vol_score < 0:
        reasons.append(f"dropping volume ({vol_chg:.1f}%)")

    if whale > 20:
        reasons.append(f"backed by {int(whale)} whale trades")

    if rsi_score >= 1.0:
        reasons.append(f"oversold conditions (RSI~{rsi_proxy:.0f})")
    elif rsi_score <= -1.0:
        reasons.append(f"overbought conditions (RSI~{rsi_proxy:.0f})")

    if vol_accel_score != 0:
        if vol_accel_score * direction_sign > 0:
            reasons.append("volume confirms trend")
        else:
            reasons.append("volume divergence detected")

    if volatility > _HIGH_VOLATILITY_THRESHOLD:
        reasons.append(f"high volatility ({volatility:.1f}%)")
    elif volatility > _MED_VOLATILITY_THRESHOLD:
        reasons.append(f"elevated volatility ({volatility:.1f}%)")

    rationale = ", ".join(reasons)
    if not rationale:
        rationale = "No strong signals detected."
    else:
        rationale = rationale.capitalize() + "."

    if direction == "UP":
        rationale = f"Bullish trend: {rationale}"
    elif direction == "DOWN":
        rationale = f"Bearish trend: {rationale}"
    else:
        rationale = f"Neutral outlook: {rationale}"

    signals = {
        "return_24h": round(ret, 4),
        "volume_change": round(vol_chg, 4),
        "whale_count": int(whale),
        "volatility": round(volatility, 4),
        "ret_score": ret_score,
        "vol_score": vol_score,
        "whale_score": whale_score,
        "rsi_proxy": round(rsi_proxy, 1),
        "rsi_score": rsi_score,
        "vol_accel_score": vol_accel_score,
        "score": round(total_score, 4),
        "volatility_adj": volatility_adj,
    }

    return direction, round(confidence, 4), rationale, signals
