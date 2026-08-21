# CryptoDecision

A trading pipeline that collects live trade data from five exchanges, aggregates it in
PostgreSQL, feeds a structured market report to a locally-hosted Qwen 2.5 model, and
lets that model decide entries — inside risk limits it cannot override.

```
5 exchanges ──WebSocket──▶ Ingestion ──Kafka──▶ Processor ──▶ PostgreSQL
                                                                  │
                                          ┌───────────────────────┤
                                          ▼                       ▼
                                   Prediction                   Bot
                              (Qwen + XGBoost + heuristic)  (agent + risk engine)
                                          │                       │
                                          └────────▶ API ◀────────┘
                                                      │
                                                  Dashboard
```

## Services

| Service | Runtime | Role |
|---|---|---|
| **Ingestion** | .NET 9 worker | 5 exchange WebSockets → Channels → Kafka |
| **Processor** | .NET 9 worker | Kafka → PostgreSQL `COPY` → daily feature aggregation |
| **Prediction** | Python 3.12 | Features → ensemble (Qwen 2.5 / XGBoost / heuristic) → `prediction_table` |
| **Bot** | .NET 9 worker | Entry decisions + risk gate + paper order engine |
| **API** | .NET 9 web | REST + SignalR push |
| **Dashboard** | nginx | Static single-page UI |
| **Ollama** | ollama/ollama | Serves `qwen2.5:7b` locally |
| **Kafka** | KRaft, no ZooKeeper | Trade + kline transport |
| **PostgreSQL** | 16 | `trades` partitioned daily by `trade_time` |

Exchanges: Binance, OKX, Bybit, Coinbase, Kraken. Each has its own WebSocket client
and a normalizer that maps its format onto one internal trade shape.

## Running it

```bash
docker compose up -d
```

Add the web dashboard (http://localhost:8888):

```bash
docker compose --profile ui up -d
```

First boot downloads `qwen2.5:7b` (~4.7 GB) into a named volume. Nothing blocks on
it — the prediction service runs on the quantitative models until the pull finishes,
then picks the LLM up automatically.

**Resource note:** Ollama wants roughly 6–8 GB of RAM resident with the model loaded.
On a smaller host, set `OLLAMA_ENABLED=false` on the prediction service and the
ensemble runs on XGBoost + heuristic alone.

## The AI layer

**Prediction service** runs a weighted ensemble. Each model collapses its verdict to a
signed score in `[-1, +1]`; the ensemble takes a weighted mean over the models that
actually answered. A model that is unavailable *abstains* rather than voting neutral —
otherwise a missing model would drag every verdict toward the middle. `model_version`
records which models voted (`ensemble-heuristic+llm`), so a degraded run cannot
silently overwrite a full one.

**Bot agent** (optional, `use_ai_agent`) gives Qwen a bounded tool set —
`get_market_snapshot`, `get_open_positions`, `get_account_state`, `open_position`,
`close_position` — and lets it decide entries. It does **not** decide:

- **Position size** — owned by the risk engine; there is no size argument on the tool.
- **Exits** — stop loss, take profit, trailing and breakeven run deterministically
  every cycle. A model that takes 60s to answer must never sit between a losing
  position and its stop.
- **Risk limits** — `RiskEngine` refuses any order breaching exposure, drawdown or the
  daily loss limit, however confident the model sounds.

## Risk engine

`RiskEngine.Expectancy()` derives, net of the 0.2% round-trip fee, what a TP/SL pair
actually requires:

```
TP 2.0% / SL 1.5%  →  net +1.8% / -1.7%, 1.06:1, breakeven win rate 48.6%
TP 0.3% / SL 5.0%  →  net +0.1% / -5.2%, 0.02:1, breakeven win rate 98.1%
```

The bot refuses to start on the second one, and the dashboard shows the same
arithmetic live as you edit the numbers. This is not decoration: the second pair was
the shipped default, and it needed one loss to undo 52 wins.

Circuit breakers stop trading on the daily loss limit, a consecutive-loss streak, or
realised drawdown.

## Database

| Table | Contents |
|---|---|
| `trades` | RANGE-partitioned by `trade_time`, one partition per day |
| `klines_1m` | 1-minute OHLCV; feeds daily feature aggregation |
| `daily_feature_table` | return_24h, volatility, volume_change, whale_count, vwap |
| `prediction_table` | direction, confidence, rationale, `signals` JSONB breakdown |
| `bot_config` | Singleton row; the API writes commands, the worker polls it |
| `bot_trades` | Paper trade history with realised P&L |

A trade above 100,000 USDT notional is flagged `is_whale` by a generated column.

Migrations live in `sql/` and are mounted into PostgreSQL on first boot.
`ProcessorService.DatabaseInitializer` re-applies the additive ones at startup, so a
preserved volume self-heals rather than needing manual migration.

## API

| Endpoint | Returns |
|---|---|
| `GET /api/market-status/{symbol}` | Today's features + latest prediction |
| `GET /api/dashboard/{symbol}?days=N` | Feature history + prediction |
| `GET /api/volume/{symbol}` | Buy/sell volume across time windows |
| `GET /api/whales/{symbol}` | Recent trades above the whale threshold |
| `GET /api/klines/{symbol}` | 1-minute OHLCV |
| `GET /api/momentum/{symbol}` | 5-minute buy/sell pressure |
| `GET /api/bot/status` · `/pnl` · `/trades` · `/debug` | Bot state and results |
| `POST /api/bot/start` · `/stop` | Bot control |

SignalR hub at `/hubs/market` pushes `ReceiveMarketStatus`, `ReceiveVolumeAnalysis`,
`ReceiveWhaleAlert` and `ReceiveBotStatus`. That list is deliberately identical to what
the dashboard renders — a broadcast nobody listens to is a database query on a timer
for no one.

## Known constraint

**The API has no authentication.** Anything that can reach it can start or stop the
bot. That is acceptable on a private LAN and is not acceptable if you expose it — via
a tunnel or otherwise — without putting auth in front of it first.
