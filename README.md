# CryptoDecision — Real-Time Crypto Intelligence Platform

A production-ready microservices system that ingests live trade data from **5 exchanges**,
processes it through a feature pipeline, runs AI predictions, and exposes decision-ready
REST + WebSocket endpoints.

---

## System Architecture

```
Binance WebSocket ─────┐
OKX WebSocket ─────────┤
Bybit WebSocket ───────┤   Template Method Pattern
Coinbase WebSocket ────┤   (ExchangeWebSocketClient base)
Kraken WebSocket ──────┘
         │
         │  ITradeNormalizer<TRaw>     Adapter Pattern
         │  (Anti-Corruption Layer)    (exchange format → unified Trade)
         ▼
┌─────────────────────────┐
│   IngestionService      │  .NET 9 Worker
│                         │
│  5× WebSocket Clients   │◄── auto-reconnect, exponential backoff
│         │               │
│  System.Threading       │
│  .Channels              │◄── bounded buffer (50k trades), backpressure
│         │               │
│  KafkaBatchPublisher    │◄── micro-batch: 200 trades OR 100ms flush
│  KlinePublisher         │◄── closed candles only
└─────────┬───────────────┘
          │
    ┌─────┴──────────────────────────────────────────────┐
    │                  Kafka (KRaft)                      │
    │                                                     │
    │  Trade Topics (5 exchanges × 2 symbols = 10 topics) │
    │  binance.trade.btcusdt    coinbase.trade.btcusdt    │
    │  binance.trade.ethusdt    coinbase.trade.ethusdt    │
    │  okx.trade.btcusdt        kraken.trade.btcusdt      │
    │  okx.trade.ethusdt        kraken.trade.ethusdt      │
    │  bybit.trade.btcusdt                                │
    │  bybit.trade.ethusdt                                │
    │                                                     │
    │  Kline Topics:  binance.kline.1m.btcusdt/ethusdt    │
    │  Prediction:    predictions.btcusdt/ethusdt          │
    │  Alerts:        alerts.notifications                 │
    └─────┬────────────────────────────┬─────────────────┘
          │                            │
┌─────────▼───────────────┐   ┌────────▼──────────────────┐
│   ProcessorService      │   │     AlertService          │
│   .NET 9 Worker         │   │     .NET 9 Worker         │
│                         │   │                            │
│  TradeProcessorWorker   │   │  AlertConsumerWorker      │
│  COPY binary protocol   │   │  AlertEngine (in-memory)  │
│  FeatureAggregation     │   │  → alerts.notifications   │
│  (every 5 min)          │   └────────────────────────────┘
└─────────┬───────────────┘
          │
┌─────────▼───────────────┐
│      PostgreSQL 16      │
│                         │
│  trades (partitioned)   │  RANGE by trade_time (daily)
│  klines_1m              │  UPSERT on conflict
│  daily_feature_table    │  return_24h, volatility, volume_change, whale_count
│  prediction_table       │  direction, confidence, model_version
│  bot_config             │  singleton (id=1)
│  bot_trades             │  paper/real trade history
│  price_alerts           │  user-defined alerts
│  app_users              │  mobile app users
└─────────┬───────┬───────┘
          │       │
          │       ▼
          │   ┌─────────────────────────┐
          │   │   PredictionService     │  Python 3.12
          │   │                         │
          │   │  Heuristic v2 (5 signals)│
          │   │  XGBoost (Optuna tuned) │
          │   │  → prediction_table     │
          │   │  → predictions.* topic  │
          │   └─────────────────────────┘
          │
          │   ┌─────────────────────────┐
          │   │      BotService         │  .NET 9 Worker
          │   │                         │
          │   │  Strategy Pattern       │◄── ITradingStrategy interface
          │   │  Grid / Momentum / RSI  │
          │   │  PaperOrderEngine       │◄── AI sizing, trailing stop
          │   └─────────────────────────┘
          ▼
┌─────────────────────────┐
│      ApiService         │  .NET 9 Web API
│                         │
│  Clean Architecture     │
│  MediatR + Validation   │
│  MarketHub (SignalR)    │◄── Push real-time data to clients
│  REST Endpoints         │
└─────────┬───────────────┘
          │
          ▼
┌─────────────────────────┐
│    Client/Frontend      │
│                         │
│  Dashboard (Web UI)     │  Nginx + HTML/JS + Chart.js
│  Mobile App             │  CapacitorJS (Android/iOS)
└─────────────────────────┘
```

## Project Structure

```
CryptoDecision/
├── src/
│   ├── CryptoDecision.IngestionService/
│   │   ├── Binance/           BinanceWebSocketClient, BinanceNormalizer, Models/
│   │   ├── OKX/               OkxWebSocketClient, OkxNormalizer, Models/
│   │   ├── Bybit/             BybitWebSocketClient, BybitNormalizer, Models/
│   │   ├── Coinbase/          CoinbaseWebSocketClient, CoinbaseNormalizer, Models/
│   │   ├── Kraken/            KrakenWebSocketClient, KrakenNormalizer, Models/
│   │   ├── WebSocket/         ExchangeWebSocketClient.cs (Template Method base)
│   │   ├── Normalization/     ITradeNormalizer<TRaw> (Adapter interface)
│   │   ├── Channels/          TradeChannel, KlineChannel, per-exchange channels
│   │   ├── Kafka/             KafkaProducerService (idempotent, lz4, retries)
│   │   ├── Workers/           Ingestion + BatchPublisher per exchange
│   │   ├── Health/            ChannelHealthCheck, HealthCheckHttpServer
│   │   ├── Telemetry/         IngestionMetrics (Prometheus)
│   │   ├── Serialization/     Source-generated JSON contexts
│   │   └── Models/            Trade, Kline, TradeBatch, KlineBatch
│   │
│   ├── CryptoDecision.ProcessorService/
│   │   ├── Kafka/             KafkaConsumerBase (manual commit, backoff)
│   │   ├── Persistence/       TradeRepository (COPY), FeatureRepository
│   │   ├── Workers/           TradeProcessor, KlineProcessor, FeatureAggregation
│   │   └── Models/            TradeBatch, KlineBatch DTOs
│   │
│   ├── CryptoDecision.ApiService/
│   │   ├── API/Controllers/   MarketController, AlertController, UsersController, BotController
│   │   ├── Application/       ApplicationLayer (queries, handlers, validators)
│   │   ├── Domain/Interfaces/ IRepositories
│   │   ├── Infrastructure/
│   │   │   ├── Persistence/   10+ repository implementations
│   │   │   ├── Hubs/          MarketHub (SignalR)
│   │   │   └── Services/      Broadcast services (momentum, volume, whale, dashboard, alert)
│   │   └── Middleware/        GlobalExceptionMiddleware
│   │
│   ├── CryptoDecision.BotService/
│   │   ├── Strategies/        ITradingStrategy, GridStrategy, MomentumStrategy, RsiStrategy, AlwaysBuyStrategy
│   │   ├── Bot/               TradingBotService, StrategyEvaluator, PaperOrderEngine, BotStateService
│   │   ├── Domain/            Entities (BotTrade, TimeframeMomentum, PredictionSnapshot)
│   │   └── Infrastructure/    Repositories (Feature, Momentum, Volume, Prediction)
│   │
│   ├── CryptoDecision.AlertService/
│   │   ├── Engine/            AlertEngine (in-memory cache + evaluation)
│   │   ├── Workers/           AlertConsumerWorker (Kafka consumer)
│   │   ├── Repository/        AlertRepository, AlertNotificationProducer
│   │   └── Models/            PriceAlert, AlertNotification
│   │
│   ├── CryptoDecision.PredictionService/     (Python 3.12)
│   │   └── prediction_service/
│   │       ├── main.py        Scheduler (5-min cycles + weekly retrain)
│   │       ├── predictor.py   Orchestrates prediction cycle
│   │       ├── heuristic.py   5-signal heuristic scoring
│   │       ├── ml_model.py    XGBoost model
│   │       ├── train.py       Optuna hyperparameter tuning
│   │       ├── kafka_producer.py
│   │       └── database.py, config.py, health.py, metrics.py
│   │
│   └── CryptoDecision.Shared/
│       └── Bot/               BotModels, BotRepository, BotConfigRepository
│
├── dashboard/                  Nginx Web Frontend (Chart.js + SignalR)
├── mobile/                     CapacitorJS Native Mobile App
├── sql/                        12 SQL migration files (001–011)
├── docker/                     prometheus.yml, tempo.yml, grafana/
├── docs/                       kafka-deep-dive.md
└── docker-compose.yml          14 services, 4 profiles
```

---

## Quick Start

### Prerequisites
- Docker & Docker Compose v2
- Internet access (all exchange WebSocket streams are public, no API keys needed)

### Run

```bash
# 1. Clone and start
git clone <repo>
cd CryptoDecision

# Headless mode (data collection only — no UI):
docker compose up --build -d

# Full mode (API + Web Dashboard):
docker compose --profile ui up --build -d

# Full mode + Observability (Grafana, Tempo, Prometheus):
docker compose --profile ui --profile observability up --build -d

# 2. Watch logs
docker compose logs -f ingestion    # 5 exchange WebSocket connections
docker compose logs -f processor    # COPY inserts + feature aggregation
docker compose logs -f bot          # Trading strategy evaluation
docker compose logs -f alert        # Price alert evaluation
```

### Verify

```bash
# Health checks
curl http://localhost:8081/health   # IngestionService
curl http://localhost:8080/health   # ApiService

# Wait ~5 minutes for features to aggregate, then:
curl http://localhost:8080/api/market-status/BTCUSDT | jq
curl http://localhost:8080/api/market-status/ETHUSDT | jq
curl "http://localhost:8080/api/dashboard/BTCUSDT?days=7" | jq

# Check trade counts by exchange
docker exec postgres psql -U crypto -d crypto -c \
  "SELECT exchange, COUNT(*) FROM trades WHERE trade_time > NOW() - INTERVAL '5 minutes' GROUP BY exchange;"
```

---

## Supported Exchanges

| Exchange | WebSocket URL | Symbols | Protocol |
|----------|---------------|---------|----------|
| Binance | `wss://stream.binance.com:9443` | BTC/USDT, ETH/USDT | Combined stream (trades + klines) |
| OKX | `wss://ws.okx.com:8443/ws/v5/public` | BTC-USDT, ETH-USDT | App-level ping/pong (25s) |
| Bybit | `wss://stream.bybit.com/v5/public/spot` | BTCUSDT, ETHUSDT | App-level ping (20s) |
| Coinbase | `wss://advanced-trade-ws.coinbase.com` | BTC-USDT, ETH-USDT | market_trades channel |
| Kraken | `wss://ws.kraken.com/v2` | BTC/USDT, ETH/USDT | v2 subscribe method |

---

## API Reference

### REST Endpoints

| Method | Endpoint | Cache | Description |
|--------|----------|-------|-------------|
| GET | `/api/market-status/{symbol}` | 30s | Today's features + AI prediction |
| GET | `/api/dashboard/{symbol}?days=30` | 60s | Historical features + prediction |
| GET | `/api/momentum/{symbol}` | 5s | 5-min buy/sell momentum + whale activity |
| GET | `/api/klines/{symbol}?limit=60&exchange=BINANCE` | 10s | Recent 1-min OHLCV candles |
| GET | `/api/volume/{symbol}` | 30s | Multi-timeframe volume analysis |
| GET | `/api/whales/{symbol}` | 5s | Recent whale trades (>100K USDT) |
| POST | `/api/alerts` | — | Create price alert |
| GET | `/api/alerts` | — | List active alerts |
| GET | `/api/alerts/history` | — | Triggered alert history |
| DELETE | `/api/alerts/{id}` | — | Deactivate alert |
| GET | `/api/bot/status` | — | Bot status + heartbeat |
| POST | `/api/bot/start` | — | Start bot with config |
| POST | `/api/bot/stop` | — | Stop bot |
| GET | `/api/bot/trades` | — | Recent bot trades |
| GET | `/api/bot/pnl` | — | P&L summary |
| GET | `/api/bot/debug` | — | Multi-position debug info |
| POST | `/api/users/register` | — | Register mobile user |
| GET | `/api/users/stats` | — | User statistics |

### SignalR Hub (`/hubs/market`)

**Emitted Events:**
| Event | Interval | Payload |
|-------|----------|---------|
| `ReceiveMarketStatus` | 20s | MarketStatusDto |
| `ReceiveKlines` | 60s | Kline arrays |
| `ReceiveMomentum` | 5s | MomentumDto |
| `ReceiveVolumeAnalysis` | 30s | VolumeAnalysisDto |
| `ReceiveWhaleAlert` | on-demand | WhaleAlertDto |
| `ReceiveBotStatus` | 15s | Bot status + trades |
| `ReceiveUserStats` | 60s | UserStatsDto |
| `ReceiveAlertTriggered` | on-demand | AlertTriggeredDto |

### Example Response: `GET /api/market-status/BTCUSDT`

```json
{
  "symbol":              "BTCUSDT",
  "return24h":           1.452300,
  "volatility":          3.812000,
  "volumeChange":        -12.330000,
  "whaleCount":          47,
  "vwap":                67234.15000000,
  "predictedDirection":  "UP",
  "confidence":          0.7823,
  "asOf":                "2026-03-22T14:30:00Z"
}
```

---

## Kafka Topics

| Topic | Partitions | Retention | Description |
|-------|-----------|-----------|-------------|
| `{exchange}.trade.btcusdt` | 3 | 3 days | BTCUSDT trade batches (×5 exchanges) |
| `{exchange}.trade.ethusdt` | 3 | 3 days | ETHUSDT trade batches (×5 exchanges) |
| `binance.kline.1m.btcusdt` | 1 | 7 days | BTCUSDT closed 1m candles |
| `binance.kline.1m.ethusdt` | 1 | 7 days | ETHUSDT closed 1m candles |
| `predictions.btcusdt` | 1 | 7 days | AI prediction results |
| `predictions.ethusdt` | 1 | 7 days | AI prediction results |
| `alerts.notifications` | 1 | 7 days | Triggered alert notifications |

**Consumer Groups:** `processor-group` (ProcessorService), `alert-group` (AlertService)

### Trade Batch Message Schema

```json
{
  "exchange":        "COINBASE",
  "symbol":          "BTCUSDT",
  "batchTimestamp":  "2026-03-22T14:30:00.123Z",
  "trades": [
    {
      "symbol":       "BTCUSDT",
      "tradeId":      3847291234,
      "price":        87234.15,
      "quantity":     1.52340000,
      "quoteQty":     102381.22,
      "isBuyerMaker": false,
      "tradeTime":    "2026-03-22T14:29:59.847Z"
    }
  ]
}
```

---

## Design Patterns

| Pattern | Location | Interview Talking Point |
|---------|----------|------------------------|
| **Strategy** | BotService/Strategies/ | Adding new strategy = 1 class + DI registration (OCP) |
| **Template Method** | ExchangeWebSocketClient | 5 exchanges share ~200 lines of reconnect/receive logic |
| **Adapter** | ITradeNormalizer<TRaw> | Anti-corruption layer between exchange APIs and domain |
| **Observer** | SignalR Hub + BroadcastServices | Event-driven real-time push to web/mobile clients |
| **CQRS** | ApiService MediatR | Read/write separation with FluentValidation pipeline |
| **Repository** | All services | Data access abstraction, enables testing with mocks |
| **Clean Architecture** | ApiService | Domain → Application → Infrastructure layering |
| **Backpressure** | BoundedChannel (50K) | Flow control between WebSocket and Kafka publisher |
| **Micro-batching** | KafkaTradePublisherBase | 200 records OR 100ms flush for optimal throughput |
| **Consumer Group** | Kafka consumers | Independent message copies per service |

---

## Trading Bot Strategies

| Strategy | Entry Signal | Exit Signal |
|----------|-------------|-------------|
| **Grid** | Price drops by grid_step_pct from last entry | TP/SL percentage |
| **Momentum v2** | Composite score ≥62 (LONG) or ≤38 (SHORT) | TP/SL + trailing stop |
| **RSI** | Flow-based RSI <30 (LONG) or >70 (SHORT) | TP/SL |
| **AlwaysBuy** | Always passes (test baseline) | TP/SL |

**Momentum v2 Composite Scoring:**
- 5m momentum (25%) + 15m momentum (25%) + 1h momentum (20%) + whale flow (15%) + AI alignment (15%)
- AI filter can block conflicting entries
- AI confidence-based position sizing (0.5x–1.5x)

---

## PostgreSQL Schema

### trades (daily partitions)
```sql
-- PARTITION BY RANGE (trade_time), auto-created daily
trades (
    id              BIGSERIAL,
    symbol          VARCHAR(20),
    trade_id        BIGINT,
    price           NUMERIC(20, 8),
    quantity        NUMERIC(20, 8),
    quote_qty       NUMERIC(20, 8),
    is_buyer_maker  BOOLEAN,
    is_whale        BOOLEAN GENERATED,   -- true when quote_qty > 100,000
    trade_time      TIMESTAMPTZ,
    ingested_at     TIMESTAMPTZ,
    exchange        VARCHAR(20) DEFAULT 'BINANCE',
    PRIMARY KEY (id, trade_time)
)
```

### daily_feature_table
```sql
(symbol, date, return_24h, volatility, volume_change, whale_count, total_volume, vwap, computed_at)
-- Refreshed every 5 minutes by FeatureAggregationWorker
```

---

## Performance Notes

| Operation | Implementation | Throughput |
|-----------|---------------|------------|
| WebSocket receive | 5 × ClientWebSocket | ~8,000+ msgs/sec combined |
| Channel buffer | BoundedChannel (50k each) | lock-free FIFO |
| Kafka publish | Micro-batch (200/100ms) | ~1 batch/100ms per exchange |
| DB write | COPY binary protocol | ~50,000 rows/sec |
| Feature compute | SQL aggregation in-DB | ~50ms per symbol |
| API read | Raw Npgsql + index hit | ~5ms p99 |

---

## Docker Compose Ports

| Service | Health | Metrics | Host Port |
|---------|--------|---------|-----------|
| API | 8080 | /metrics | 8080 |
| Ingestion | 8081 | 9095 | 8081 |
| Processor | 8082 | 9096 | 8082 |
| Prediction | 8083 | 9094 | 8083 |
| Alert | 8084 | 9097 | 8084 |
| Dashboard | — | — | 8888 |
| Kafka | — | — | 9092 |
| PostgreSQL | — | — | 5432 |
| Tempo | — | — | 3200 (UI), 4317 (gRPC) |
| Prometheus | — | — | 9091 |
| Grafana | — | — | 3000 |

### Docker Profiles
- **(default)** — kafka, postgres, ingestion, processor, bot, alert, prediction
- **ui** — api, dashboard
- **observability** — tempo, prometheus, grafana
- **tunnel** — cloudflared (HTTPS tunnel)

---

## Operational Queries

```sql
-- Check partition sizes
SELECT * FROM v_partition_sizes;

-- Daily whale summary
SELECT * FROM v_whale_summary ORDER BY trade_date DESC LIMIT 10;

-- Trade counts by exchange (last 5 min)
SELECT exchange, COUNT(*) FROM trades
WHERE trade_time > NOW() - INTERVAL '5 minutes' GROUP BY exchange;

-- Drop partitions older than 90 days
SELECT drop_old_trade_partitions(90);

-- Latest features
SELECT * FROM daily_feature_table ORDER BY date DESC, symbol LIMIT 10;

-- Latest predictions
SELECT * FROM prediction_table ORDER BY date DESC, created_at DESC LIMIT 10;
```

---

## Stopping

```bash
docker compose down        # keep data volumes
docker compose down -v     # destroy everything including data
```
