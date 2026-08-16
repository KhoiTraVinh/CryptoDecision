# LinkedIn Post — CryptoDecision Architecture

> Copy nội dung bên dưới để đăng LinkedIn. Kèm ảnh chụp từ `architecture-diagram.html`.

---

## Post Content

**Building a Real-Time Crypto Intelligence Platform from Scratch — Architecture Deep Dive**

After months of engineering, I want to share the architecture of **CryptoDecision** — a distributed system that ingests live trade data from 5 cryptocurrency exchanges, processes millions of trades through a feature pipeline, runs AI predictions, and powers a multi-strategy trading bot.

**The Problem:**
Crypto markets generate thousands of trades per second across fragmented exchanges. To make data-driven trading decisions, you need a system that collects, normalizes, and analyzes this data in real-time — with sub-second latency at scale.

**The Architecture (6 Microservices + Event-Driven):**

Data Sources (5 Exchanges):
- Binance, Bybit, OKX, Coinbase, Kraken
- ~8,000+ trades/sec combined via persistent WebSocket connections
- Each exchange has its own wire format — normalized through an Anti-Corruption Layer

Ingestion Layer (.NET 9 Worker):
- Template Method Pattern for WebSocket lifecycle (reconnect, backoff, ping/pong)
- Adapter Pattern (ITradeNormalizer<TRaw>) — 5 exchange formats unified into one Trade model
- BoundedChannel (50K capacity) for backpressure between WebSocket and Kafka
- Micro-batching: 200 trades OR 100ms flush interval

Event Broker (Apache Kafka KRaft):
- 15 topics, 3 partitions per trade topic
- No ZooKeeper — KRaft consensus
- Lz4 compression, idempotent producer, W3C traceparent propagation

Processing Layer (.NET 9 Worker):
- PostgreSQL COPY binary protocol — 50,000 rows/sec bulk insert
- RANGE partitioned trades table (daily partitions, auto-created)
- Feature aggregation every 5 minutes: return_24h, volatility, VWAP, whale detection (>100K USDT)

AI Prediction (Python 3.12):
- Heuristic v2 scoring (5 signals: return, volume, whale flow, RSI proxy, volume acceleration)
- XGBoost with Optuna hyperparameter tuning (20 trials, 3-fold CV)
- Prediction cycle every 5 min, weekly retrain

Multi-Strategy Trading Bot (.NET 9 Worker):
- Strategy Pattern with 4 pluggable strategies: Grid, Momentum v2, RSI, AlwaysBuy
- Momentum v2 composite scoring: 5m(25%) + 15m(25%) + 1h(20%) + Whale(15%) + AI(15%)
- AI integration: entry filter, confidence-based position sizing (0.5x-1.5x)
- Trailing stop, breakeven stop, dynamic TP/SL, daily loss limit

API Gateway (.NET 9 Web API):
- Clean Architecture + MediatR CQRS + FluentValidation
- 18 REST endpoints with response caching
- SignalR WebSocket hub — 8 real-time events pushed to clients
- Web Dashboard (Chart.js) + Mobile App (CapacitorJS)

Observability:
- OpenTelemetry distributed tracing (Tempo)
- Prometheus metrics scraping
- Grafana dashboards

**Design Patterns Applied (12):**
Strategy | Template Method | Adapter | Observer | CQRS | Repository | Clean Architecture | Backpressure | Micro-batching | Event-Driven | Consumer Group | Singleton

**Performance:**
- Total RAM: ~1.7 GB for all 6 services
- DB: 17M+ trades stored, partitioned
- API latency: ~5ms p99
- Runs 24/7 on a single machine via Docker Compose

**Tech Stack:**
.NET 9 | Python 3.12 | Apache Kafka (KRaft) | PostgreSQL 16 | SignalR | XGBoost | Docker Compose | Prometheus | Grafana | Tempo

The most interesting engineering challenge was designing the ingestion pipeline — 5 exchanges, each with completely different WebSocket protocols, message formats, and keepalive mechanisms. The Template Method + Adapter pattern combination made it possible to add a new exchange in under 2 hours with zero changes to existing code.

What architecture decisions would you have made differently? I'd love to hear your thoughts.

---

#SoftwareArchitecture #Microservices #DotNet #Kafka #PostgreSQL #MachineLearning #CryptoTrading #SystemDesign #DistributedSystems #EventDrivenArchitecture #CleanArchitecture #DesignPatterns #RealTimeData #WebSocket #SignalR #XGBoost #Docker #OpenTelemetry

---

## Vietnamese Version

**Xây dựng nền tảng phân tích Crypto real-time từ đầu — Chi tiết kiến trúc hệ thống**

Sau nhiều tháng phát triển, mình muốn chia sẻ kiến trúc của **CryptoDecision** — một hệ thống phân tán thu thập dữ liệu giao dịch real-time từ 5 sàn crypto, xử lý hàng triệu trades qua pipeline, chạy AI prediction, và vận hành trading bot đa chiến thuật.

**Bài toán:**
Thị trường crypto sinh ra hàng nghìn giao dịch mỗi giây trên nhiều sàn phân mảnh. Để ra quyết định dựa trên data, cần một hệ thống thu thập, chuẩn hóa và phân tích dữ liệu real-time — với độ trễ dưới 1 giây ở quy mô lớn.

**Kiến trúc (6 Microservices + Event-Driven):**

Nguồn dữ liệu (5 Sàn):
- Binance, Bybit, OKX, Coinbase, Kraken
- ~8,000+ trades/giây qua WebSocket persistent connections
- Mỗi sàn có format riêng — chuẩn hóa qua Anti-Corruption Layer

Tầng Thu thập (.NET 9 Worker):
- Template Method Pattern cho vòng đời WebSocket (reconnect, backoff, ping/pong)
- Adapter Pattern (ITradeNormalizer<TRaw>) — 5 format sàn hợp nhất thành 1 Trade model
- BoundedChannel (50K capacity) cho backpressure giữa WebSocket và Kafka
- Micro-batching: 200 trades HOẶC 100ms flush

Event Broker (Apache Kafka KRaft):
- 15 topics, 3 partitions mỗi trade topic
- Không ZooKeeper — KRaft consensus
- Nén Lz4, idempotent producer, W3C traceparent propagation

Tầng Xử lý (.NET 9 Worker):
- PostgreSQL COPY binary protocol — 50,000 rows/giây bulk insert
- Bảng trades phân vùng RANGE (partition theo ngày, tự tạo)
- Feature aggregation mỗi 5 phút: return_24h, volatility, VWAP, whale detection (>100K USDT)

AI Prediction (Python 3.12):
- Heuristic v2 (5 tín hiệu: return, volume, whale flow, RSI proxy, volume acceleration)
- XGBoost + Optuna tuning (20 trials, 3-fold CV)
- Prediction mỗi 5 phút, retrain hàng tuần

Trading Bot đa chiến thuật (.NET 9 Worker):
- Strategy Pattern với 4 strategy: Grid, Momentum v2, RSI, AlwaysBuy
- Momentum v2 composite: 5m(25%) + 15m(25%) + 1h(20%) + Whale(15%) + AI(15%)
- Tích hợp AI: entry filter, position sizing theo confidence (0.5x-1.5x)
- Trailing stop, breakeven stop, dynamic TP/SL, daily loss limit

API Gateway (.NET 9 Web API):
- Clean Architecture + MediatR CQRS + FluentValidation
- 18 REST endpoints + response caching
- SignalR WebSocket hub — 8 real-time events push tới clients
- Web Dashboard (Chart.js) + Mobile App (CapacitorJS)

**12 Design Patterns:**
Strategy | Template Method | Adapter | Observer | CQRS | Repository | Clean Architecture | Backpressure | Micro-batching | Event-Driven | Consumer Group | Singleton

**Hiệu năng:**
- Tổng RAM: ~1.7 GB cho 6 services
- Database: 17M+ trades, phân vùng theo ngày
- API latency: ~5ms p99
- Chạy 24/7 trên 1 máy duy nhất qua Docker Compose

**Stack:**
.NET 9 | Python 3.12 | Apache Kafka (KRaft) | PostgreSQL 16 | SignalR | XGBoost | Docker Compose | Prometheus | Grafana | Tempo

Thử thách kỹ thuật thú vị nhất là thiết kế pipeline thu thập — 5 sàn, mỗi sàn protocol WebSocket khác nhau, format message khác nhau, cơ chế keepalive khác nhau. Kết hợp Template Method + Adapter Pattern giúp thêm sàn mới trong chưa tới 2 tiếng mà không sửa code cũ.

Nếu là bạn, bạn sẽ thiết kế khác ở điểm nào? Rất mong nhận được góp ý.

---

#KiếnTrúcPhầnMềm #Microservices #DotNet #Kafka #PostgreSQL #MachineLearning #CryptoTrading #SystemDesign #HệThốngPhânTán #EventDriven #CleanArchitecture #DesignPatterns #RealTime #WebSocket #SignalR #XGBoost #Docker
