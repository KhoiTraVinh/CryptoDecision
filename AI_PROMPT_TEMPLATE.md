# AI Summary Prompt Template

Used by `OpenAiSummaryService` (ApiService) to generate structured market
decision summaries from daily feature data.

---

## System Prompt

```
You are a crypto market analyst providing structured, data-driven decision
support. You must be objective. Do not speculate beyond what the numbers show.
Return ONLY valid JSON — no markdown, no explanation, no preamble.
```

---

## User Prompt Template

```
Analyze the following daily market data for {symbol} on {date}:

  24h Return:       {return_24h:+0.00}%
  Volatility:       {volatility:0.00}%  (intra-day high-low range / open)
  Volume Change:    {volume_change:+0.00}%  vs prior day
  Whale Trades:     {whale_count}  (trades > $100,000 USDT each)
  VWAP:             ${vwap:N2}
  Open:             ${open_price:N2}
  Close:            ${close_price:N2}
  High:             ${high_price:N2}
  Low:              ${low_price:N2}

Provide a structured market decision summary in this EXACT JSON format:

{
  "sentiment":       "BULLISH" | "BEARISH" | "NEUTRAL",
  "confidence":      0.00,          // 0.0 (uncertain) – 1.0 (very certain)
  "key_insight":     "...",         // ≤ 20 words, most important signal
  "recommendation":  "BUY" | "SELL" | "HOLD" | "WATCH",
  "risk_level":      "LOW" | "MEDIUM" | "HIGH",
  "rationale":       "..."          // ≤ 40 words, brief reasoning
}
```

---

## Field Definitions

| Field           | Type    | Notes                                               |
|-----------------|---------|-----------------------------------------------------|
| `sentiment`     | enum    | Overall market bias based on return + volume        |
| `confidence`    | float   | Lower when signals conflict (e.g. +return, -volume) |
| `key_insight`   | string  | The single most important metric driving sentiment  |
| `recommendation`| enum    | Actionable signal for the dashboard user            |
| `risk_level`    | enum    | Based on volatility and whale trade concentration   |
| `rationale`     | string  | Brief explanation (used as tooltip in UI)           |

---

## Heuristic Guidance for the Model

| Signal                        | Suggest              |
|-------------------------------|----------------------|
| return_24h > +2%, vol_change > 0      | BULLISH, HIGH confidence |
| return_24h < -2%, whale_count > 20    | BEARISH, HIGH confidence |
| |return_24h| < 0.5%, vol_change < 0   | NEUTRAL, WATCH           |
| volatility > 5%                       | risk_level = HIGH        |
| volatility < 1%                       | risk_level = LOW         |
| whale_count > 50                      | increase confidence (institutional activity) |

---

## Implementation in C#

```csharp
// ApiService/Infrastructure/AI/OpenAiSummaryService.cs
var prompt = $"""
    Analyze the following daily market data for {feature.Symbol} on {feature.Date:yyyy-MM-dd}:

      24h Return:    {feature.Return24h:+0.00}%
      Volatility:    {feature.Volatility:0.00}%
      Volume Change: {feature.VolumeChange:+0.00}%
      Whale Trades:  {feature.WhaleCount}
      VWAP:          ${feature.Vwap:N2}

    Provide a structured market decision summary in this EXACT JSON format:
    {{
      "sentiment":      "BULLISH" | "BEARISH" | "NEUTRAL",
      "confidence":     0.00,
      "key_insight":    "...",
      "recommendation": "BUY" | "SELL" | "HOLD" | "WATCH",
      "risk_level":     "LOW" | "MEDIUM" | "HIGH",
      "rationale":      "..."
    }}
    """;

var completion = await client.GetChatClient("gpt-4o-mini")
    .CompleteChatAsync(
        new SystemChatMessage("You are a crypto market analyst. Return ONLY valid JSON."),
        new UserChatMessage(prompt));

var json = completion.Value.Content[0].Text;
var summary = JsonSerializer.Deserialize<AiSummary>(json);
```

---

## Example Output

**Input data:** BTCUSDT, return=+2.31%, volatility=3.8%, volume_change=+14%, whale_count=47

**Model output:**
```json
{
  "sentiment":      "BULLISH",
  "confidence":     0.76,
  "key_insight":    "Strong upward return with rising institutional volume",
  "recommendation": "BUY",
  "risk_level":     "MEDIUM",
  "rationale":      "Positive 24h return backed by 47 whale trades and 14% volume increase suggests sustained institutional interest."
}
```

---

## Caching Strategy

- Cache key: `ai_summary:{symbol}:{date:yyyy-MM-dd}`
- TTL: **60 minutes** (refresh once per hour; features update every 5 min)
- Rationale: OpenAI calls cost ~$0.0002/call for gpt-4o-mini; daily cost
  per symbol ≈ $0.005. Caching avoids re-calling when dashboard is polled
  every 30–60 seconds.
