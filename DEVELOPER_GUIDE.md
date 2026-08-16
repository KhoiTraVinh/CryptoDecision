# CRYPTODECISION – DEVELOPER GUIDE & ARCHITECTURE

Tài liệu này cung cấp cái nhìn sâu vào lõi hệ thống dành cho Kỹ sư Phát triển (Developer), vận hành và bảo trì dự án phần mềm giao dịch tự động CryptoDecision.

## 1. Kiến Trúc Hệ Thống (Architecture Blueprint)

Hệ thống được thiết kế theo mẫu **Microservices / Event-Driven Architecture**, kết nối dữ liệu thời gian thực từ **5 sàn giao dịch** (Binance, OKX, Bybit, Coinbase, Kraken).

### 1.1 Components Chính (6 Services)
*   **IngestionService (.NET 9 Worker):** Kết nối WebSocket tới 5 sàn, chuẩn hóa dữ liệu qua Adapter Pattern (`ITradeNormalizer<TRaw>`), đẩy qua Kafka bằng micro-batching (200 trades/100ms).
*   **Apache Kafka (KRaft):** 15 topics (10 trade + 2 kline + 2 prediction + 1 alert). Không ZooKeeper. 3 partitions cho trade topics.
*   **ProcessorService (.NET 9 Worker):** Kafka → PostgreSQL COPY binary (50K rows/sec). FeatureAggregation mỗi 5 phút.
*   **BotService (.NET 9 Worker):** Engine đa chiến thuật (Strategy Pattern): Grid, Momentum v2, RSI, AlwaysBuy. Hỗ trợ AI filter, trailing stop, dynamic TP/SL.
*   **AlertService (.NET 9 Worker):** Theo dõi giá qua Kafka, đánh giá alert rules từ cache in-memory, thông báo qua Kafka → SignalR.
*   **PredictionService (Python 3.12):** Heuristic v2 (5 signals) + XGBoost (Optuna tuned). Chạy mỗi 5 phút, retrain hàng tuần.
*   **ApiService (.NET 9 Web API):** Clean Architecture, CQRS (MediatR), FluentValidation, SignalR Hub cho real-time push.
*   **PostgreSQL 16:** Table Partitioning (Range theo ngày). 9 bảng chính.

---

## 2. Design Patterns

### 2.1 Template Method — WebSocket Base Class
```
ExchangeWebSocketClient (base)
├── BinanceWebSocketClient     wss://stream.binance.com:9443
├── OkxWebSocketClient         wss://ws.okx.com:8443
├── BybitWebSocketClient       wss://stream.bybit.com/v5
├── CoinbaseWebSocketClient    wss://advanced-trade-ws.coinbase.com
└── KrakenWebSocketClient      wss://ws.kraken.com/v2
```
Base class chứa logic bất biến: reconnect loop, exponential backoff (1s→60s), fragment reassembly, ping timer. Subclass chỉ override: URI, subscription message, message processing.

### 2.2 Adapter Pattern — Anti-Corruption Layer
Mỗi sàn có format JSON khác nhau. `ITradeNormalizer<TRaw>` chuẩn hóa về model `Trade` thống nhất:
- Symbol: `BTC-USDT` / `BTC/USDT` → `BTCUSDT`
- Side: `sell`/`SELL`/`Sell` → `IsBuyerMaker = true`
- Price/Qty: string → decimal
- Timestamp: ISO 8601 → DateTimeOffset

### 2.3 Strategy Pattern — Trading Bot
```csharp
public interface ITradingStrategy
{
    string Name { get; }
    Task<EntryDecision> EvaluateEntryAsync(StrategyContext ctx, CancellationToken ct);
    ExitDecision EvaluateExit(BotTrade trade, decimal currentPrice, BotOptions opts);
}
```
Thêm strategy mới = 1 class mới + register DI. Không sửa code cũ (OCP).

### 2.4 CQRS — ApiService
MediatR pipeline: Controller → ValidationBehavior → Handler → Repository.
Queries & Commands tách biệt hoàn toàn.

### 2.5 Observer — SignalR Broadcasting
5 Background Services đẩy data real-time qua MarketHub (SignalR):
- `MomentumBroadcastService` (5s), `VolumeAnalysisBroadcastService` (30s)
- `WhaleAlertBroadcastService` (on-demand), `DashboardBroadcastService` (20s)
- `AlertBroadcastService` (Kafka → SignalR)

---

## 3. Mô-đun Trading Bot (Multi-Strategy Engine)

### Chiến thuật hiện có:
| Strategy | Thuật toán | Thời điểm vào lệnh |
|----------|-----------|---------------------|
| **Grid** | DCA cắt lớp | Giá giảm theo grid_step_pct |
| **Momentum v2** | Composite scoring 5 tín hiệu | Score ≥62 (LONG), ≤38 (SHORT) |
| **RSI** | Flow-based RSI | RSI <30 (LONG), >70 (SHORT) |
| **AlwaysBuy** | Baseline test | Luôn vào lệnh |

### Momentum v2 — Chi tiết:
```
Composite = 0.25 × M5 + 0.25 × M15 + 0.20 × M1h + 0.15 × Whale + 0.15 × AI
```
- M5/M15/M1h: Buy ratio trong cửa sổ 5 phút/15 phút/1 giờ
- Whale: Áp lực cá voi (buy vs sell whale count)
- AI: Alignment với prediction direction

### Tính năng nâng cao:
- **Trailing Stop** với tightening theo momentum
- **Breakeven Stop** (khi đạt trigger %, chuyển SL về entry)
- **Dynamic TP/SL** (volatility cao → threshold rộng hơn)
- **AI Filter** (chặn entry nếu conflict với AI prediction)
- **AI Sizing** (position size × confidence: 0.5x–1.5x)
- **Per-strategy cooldown** + max open trades
- **Daily loss limit** (dừng bot khi lỗ quá %)

---

## 4. SignalR Real-time Optimization

Thay vì HTTP Polling (gọi API liên tục), hệ thống sử dụng **Push-based architecture**:
- **DashboardBroadcastService:** Hosted Service định kỳ tổng hợp dữ liệu → Push qua SignalR.
- **MarketHub:** WebSocket Gateway. Client chỉ cần mở 1 kết nối, nhận tất cả events.
- **Client:** Hoàn toàn event-driven, không `setInterval`. Tiết kiệm pin mobile, giảm CPU/RAM server.

---

## 5. Khối Frontend (Presentation Layer)

### Web Dashboard (`dashboard/`)
- Nginx container, HTML/JS tĩnh + Chart.js
- SignalR client nhận real-time data
- Hiển thị: Price charts (5 sàn), Momentum gauge, AI signals, Volume analysis, Whale alerts, Bot status, Equity curve

### Mobile App (`mobile/`)
- CapacitorJS (Android/iOS)
- Chung UI core với `mobile/www/`
- Native notifications (Whale Alert, Bot trades)
- Build: `cd mobile/ && .\build_mobile.ps1`

---

## 6. Báo Cáo Chịu Tải (Production Benchmark)

| Service | CPU | RAM | Chức Năng |
|---------|-----|-----|-----------|
| `ingestion` | 5% | 64 MB | 5 WebSocket connections + micro-batching |
| `processor` | 3% | 56 MB | Kafka → PostgreSQL COPY |
| `bot` | <1% | 32 MB | Strategy evaluation mỗi 30s |
| `alert` | <1% | 28 MB | Price alert evaluation |
| `prediction` | <1% | 22 MB | Heuristic/XGBoost mỗi 5 phút |
| `api` | 2% | 48 MB | REST + SignalR hub |
| `postgres` | 7% | 633 MB | >17M+ trades, partitioned |
| `kafka` | 5% | 927 MB | KRaft broker, 15 topics |

**Tổng headless mode:** ~1.7GB RAM. Chạy êm treo 24/7.

---

## 7. Hướng dẫn Lập Trình (Onboarding)

### Yêu cầu:
- Docker Desktop / Docker Compose v2
- .NET 9 SDK (debug trực tiếp)
- Android Studio CMD Line Tools (build mobile APK)

### Docker Profiles:

```bash
# 1. Data Collector (treo máy 24/7, không UI):
docker compose up -d

# 2. Bật UI + API:
docker compose --profile ui up -d

# 3. Bật Observability (Grafana, Tempo, Prometheus):
docker compose --profile observability up -d

# 4. Tắt UI/Observability giải phóng RAM:
docker compose --profile ui --profile observability stop

# 5. Kiểm tra trade counts theo sàn:
docker exec postgres psql -U crypto -d crypto -c \
  "SELECT exchange, COUNT(*) FROM trades WHERE trade_time > NOW() - INTERVAL '5 minutes' GROUP BY exchange;"
```

### SQL Migrations:
```bash
# Khi giữ postgres_data volume cũ, cần apply migration thủ công:
cat sql/011_bot_ai_config.sql | docker exec -i postgres psql -U crypto -d crypto
```

> **Lưu ý:** Không can thiệp DB Schema qua Code C#. Dự án dùng `sql/00X_*.sql` để sinh cấu trúc bảng. Mở rộng cột cần chạy lệnh thủ công `docker exec postgres psql -c "ALTER TABLE..."`.
