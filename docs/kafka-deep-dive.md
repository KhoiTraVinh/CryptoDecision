# Apache Kafka Deep Dive — CryptoDecision Project

> Tài liệu này giải thích chi tiết cách Apache Kafka được sử dụng trong hệ thống CryptoDecision.
> Mỗi section đều mapping trực tiếp vào source code thực tế để bạn có thể đọc song song.

---

## Mục lục

1. [Tổng quan kiến trúc](#1-tổng-quan-kiến-trúc)
2. [Kafka KRaft Mode — Không cần ZooKeeper](#2-kafka-kraft-mode--không-cần-zookeeper)
3. [Topic Design & Partitioning Strategy](#3-topic-design--partitioning-strategy)
4. [Producer — IngestionService (.NET)](#4-producer--ingestionservice-net)
5. [Backpressure & Micro-Batching với Channel](#5-backpressure--micro-batching-với-channel)
6. [Consumer — ProcessorService (.NET)](#6-consumer--processorservice-net)
7. [Delivery Guarantees — At-Least-Once vs Exactly-Once](#7-delivery-guarantees--at-least-once-vs-exactly-once)
8. [Serialization — Source-Generated JSON](#8-serialization--source-generated-json)
9. [Distributed Tracing qua Kafka Headers](#9-distributed-tracing-qua-kafka-headers)
10. [Python Producer — PredictionService](#10-python-producer--predictionservice)
11. [Error Handling & Retry Patterns](#11-error-handling--retry-patterns)
12. [Production Gotchas & Lessons Learned](#12-production-gotchas--lessons-learned)
13. [Kafka Interview Cheat Sheet](#13-kafka-interview-cheat-sheet)

---

## 1. Tổng quan kiến trúc

```
┌──────────────────────────────────────────────────────────────────────┐
│                        DATA FLOW OVERVIEW                            │
│                                                                      │
│  Binance WS ──┐                                                     │
│  OKX WS ──────┤                                                     │
│  Bybit WS ────┤──→ Normalizer ──→ Channel ──→ BatchPublisher ──┐   │
│  Coinbase WS ─┤        ▲                           │            │   │
│  Kraken WS ───┘   Adapter Pattern            micro-batch        │   │
│                  (anti-corruption)          (200 trades hoặc    │   │
│                                              100ms timeout)     │   │
│                                                                 ▼   │
│                                              ┌──────────────┐       │
│                                              │  KAFKA BROKER │       │
│                                              │   (KRaft)     │       │
│                                              └──────┬───────┘       │
│                                                     │               │
│                    ┌────────────────┬────────────────┤               │
│                    ▼                ▼                ▼               │
│            TradeProcessor    KlineProcessor    PredictionService     │
│            (COPY to PG)      (Upsert PG)       (Python consumer)    │
│                    │                │                │               │
│                    ▼                ▼                ▼               │
│              PostgreSQL        PostgreSQL      predictions.*        │
│           (trades table)    (klines_1m)      (publish to Kafka)     │
└──────────────────────────────────────────────────────────────────────┘
```

**3 vai trò chính của Kafka trong project:**

| Vai trò | Giải thích |
|---------|-----------|
| **Message Bus** | Decouple IngestionService (producer) khỏi ProcessorService (consumer). Hai service không biết nhau. |
| **Buffer** | Kafka giữ message 72h (trades) hoặc 7 ngày (klines/predictions). Nếu ProcessorService crash, không mất data — restart là consume lại từ last committed offset. |
| **Scaling point** | Trade topics có 3 partitions. Muốn scale consumer lên 3 instance → mỗi instance handle 1 partition (consumer group rebalancing). |

---

## 2. Kafka KRaft Mode — Không cần ZooKeeper

> **File:** `docker-compose.yml` → service `kafka`

Từ Kafka 3.3+, KRaft (Kafka Raft) thay thế ZooKeeper để quản lý metadata cluster. Project này dùng **Kafka 3.8.0 KRaft mode**.

### Cấu hình KRaft trong docker-compose:

```yaml
kafka:
  image: apache/kafka:3.8.0
  environment:
    KAFKA_NODE_ID:                       1
    KAFKA_PROCESS_ROLES:                 broker,controller    # Combined mode
    KAFKA_CONTROLLER_QUORUM_VOTERS:      1@kafka:9093
    KAFKA_CONTROLLER_LISTENER_NAMES:     CONTROLLER
    CLUSTER_ID:                          "CryptoDecisionKraftCluster1"
```

### Giải thích từng config:

| Config | Giá trị | Ý nghĩa |
|--------|---------|---------|
| `KAFKA_PROCESS_ROLES` | `broker,controller` | Node này vừa là broker (handle messages) vừa là controller (manage metadata). Trong production, tách riêng. |
| `KAFKA_NODE_ID` | `1` | ID duy nhất của node. KRaft dùng Raft consensus nên cần node ID. |
| `KAFKA_CONTROLLER_QUORUM_VOTERS` | `1@kafka:9093` | Danh sách controller voters. Format: `nodeId@host:port`. Chỉ có 1 node = single voter. |
| `CLUSTER_ID` | Hardcoded string | KRaft yêu cầu cluster ID cố định (ZooKeeper tự generate). Phải giống nhau trên mọi node. |
| `KAFKA_LISTENERS` | `PLAINTEXT://0.0.0.0:9092, CONTROLLER://0.0.0.0:9093` | 2 listener: 9092 cho client traffic, 9093 cho internal controller traffic. |
| `KAFKA_ADVERTISED_LISTENERS` | `PLAINTEXT://kafka:9092` | Host mà client dùng để connect. `kafka` là Docker DNS name. |

### Single-broker critical configs:

```yaml
KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR:          1
KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR:  1
KAFKA_TRANSACTION_STATE_LOG_MIN_ISR:             1
```

**Tại sao cần?** Internal topic `__consumer_offsets` default replication-factor = 3. Với 1 broker, Kafka không thể tạo topic có RF=3 → consumer groups không form được → **không consume được message nào**. Đây là bug phổ biến khi dev local.

### Data persistence:

```yaml
KAFKA_LOG_DIRS: /var/lib/kafka/data
volumes:
  - kafka_data:/var/lib/kafka/data
```

Không config `LOG_DIRS` → Kafka dùng `/tmp/kafka-logs` (ephemeral, mất khi container restart).

### Health check:

```yaml
healthcheck:
  test: ["CMD", "/opt/kafka/bin/kafka-broker-api-versions.sh", "--bootstrap-server", "localhost:9092"]
  interval: 15s
  start_period: 30s
```

`kafka-broker-api-versions.sh` kiểm tra broker có accept connections không. `start_period: 30s` cho broker thời gian khởi động.

---

## 3. Topic Design & Partitioning Strategy

> **File:** `docker-compose.yml` → service `kafka-init`

### Topic creation:

```bash
kafka-topics.sh --create --if-not-exists \
  --topic binance.trade.btcusdt \
  --partitions 3 \
  --replication-factor 1 \
  --config retention.ms=259200000   # 72 giờ
```

### Topic inventory:

| Topic | Partitions | Retention | Data |
|-------|-----------|-----------|------|
| `binance.trade.btcusdt` | 3 | 72h | BTC trades từ Binance |
| `binance.trade.ethusdt` | 3 | 72h | ETH trades từ Binance |
| `okx.trade.btcusdt` | 3 | 72h | BTC trades từ OKX |
| `okx.trade.ethusdt` | 3 | 72h | ETH trades từ OKX |
| `bybit.trade.btcusdt` | 3 | 72h | BTC trades từ Bybit |
| `bybit.trade.ethusdt` | 3 | 72h | ETH trades từ Bybit |
| `coinbase.trade.btcusdt` | 3 | 72h | BTC trades từ Coinbase |
| `coinbase.trade.ethusdt` | 3 | 72h | ETH trades từ Coinbase |
| `kraken.trade.btcusdt` | 3 | 72h | BTC trades từ Kraken |
| `kraken.trade.ethusdt` | 3 | 72h | ETH trades từ Kraken |
| `binance.kline.1m.btcusdt` | 1 | 7 ngày | 1-minute candles BTC |
| `binance.kline.1m.ethusdt` | 1 | 7 ngày | 1-minute candles ETH |
| `predictions.btcusdt` | 1 | 7 ngày | ML predictions BTC |
| `predictions.ethusdt` | 1 | 7 ngày | ML predictions ETH |
| `alerts.notifications` | 1 | 7 ngày | Triggered price alerts |

### Naming convention: `{exchange}.{data_type}.{symbol}`

Tên topic encode rõ ràng **source, loại data, và symbol**. Giúp:
- Dễ monitor (biết topic nào đang lag)
- Consumer subscribe chọn lọc (chỉ cần Binance trades? Subscribe `binance.trade.*`)
- Trong Grafana dashboard, filter theo exchange dễ dàng

### Tại sao trade topics có 3 partitions, kline/prediction chỉ có 1?

**Trade topics (3 partitions):**
- Volume cao: Binance BTC/USDT ~1000-5000 trades/phút
- 3 partitions cho phép scale lên 3 consumer instances
- Message key = symbol → tất cả BTCUSDT trades vào cùng partition → **đảm bảo ordering per symbol**

**Kline/Prediction topics (1 partition):**
- Volume thấp: 1 kline/phút, 1 prediction/5 phút
- Không cần parallelism → 1 partition đủ
- Giảm overhead (mỗi partition = 1 file trên disk, 1 replica thread, etc.)

### Partition key & Ordering guarantee:

```csharp
// KafkaProducerService.cs
await producer.PublishAsync(topic, key: symbol, batch, ct);
//                                 ^^^^^^^^^^
//                                 Message key = "BTCUSDT"
```

Kafka hash message key → partition assignment. Cùng key luôn vào cùng partition → **messages cho BTCUSDT luôn đúng thứ tự thời gian** trên 1 partition. Đây là Kafka's ordering guarantee: **per-partition ordering, not global ordering**.

### Retention config:

- **72h (trades):** Trades là high-volume data. Giữ 3 ngày đủ để recover từ downtime.
- **7 ngày (klines/predictions):** Low-volume, có giá trị analytical lâu hơn.
- **Log segment size:** `104857600` (100MB). Kafka split log file mỗi 100MB. File nhỏ hơn = cleanup nhanh hơn.

---

## 4. Producer — IngestionService (.NET)

> **File:** `src/CryptoDecision.IngestionService/Kafka/KafkaProducerService.cs`

### Producer Config chi tiết:

```csharp
var config = new ProducerConfig
{
    BootstrapServers      = cfg.BootstrapServers,    // "kafka:9092"
    EnableIdempotence     = true,
    Acks                  = Acks.All,
    CompressionType       = CompressionType.Lz4,
    LingerMs              = 10,
    MaxInFlight           = 5,
    MessageSendMaxRetries = 10,
    RetryBackoffMs        = 100,
    EnableDeliveryReports = true,
    BatchSize             = 131_072,  // 128KB
};
```

### Giải thích từng setting:

#### `EnableIdempotence = true` — Exactly-once delivery

**Vấn đề:** Khi producer gửi message, broker nhận và ghi log, nhưng ACK bị mất (network issue). Producer retry → broker nhận **duplicate message**.

**Giải pháp:** Idempotent producer. Kafka assign mỗi producer một **Producer ID (PID)** và mỗi message một **sequence number**. Broker reject duplicate `(PID, sequence)` pairs.

```
Producer gửi msg seq=5 → Broker nhận, ghi log, ACK mất
Producer retry msg seq=5 → Broker thấy seq=5 đã có → ignore duplicate, ACK lại
```

**Yêu cầu khi bật idempotence:**
- `Acks` phải = `All`
- `MaxInFlight` ≤ 5 (Kafka giới hạn)
- Kafka tự bật retry nội bộ

#### `Acks = Acks.All` — Strongest durability

| Acks | Ý nghĩa | Trade-off |
|------|---------|-----------|
| `0` | Fire-and-forget. Producer không chờ ACK. | Fastest, có thể mất data |
| `1` | Leader ACK. Leader ghi xong là ACK. | Nếu leader crash trước replicate → mất data |
| `All` (-1) | Tất cả In-Sync Replicas (ISR) phải ACK. | Chậm nhất, strongest durability |

Trong project này: single broker nên `All` = `1` (chỉ có 1 replica). Nhưng config đúng cho production multi-broker.

#### `CompressionType = Lz4` — Nén message

Kafka compress ở **batch level** (không phải per-message). Producer accumulate messages vào batch → compress cả batch → gửi.

| Codec | Compression Ratio | Speed | CPU |
|-------|-------------------|-------|-----|
| None | 1x | N/A | 0 |
| Gzip | ~5-8x | Slow | High |
| Snappy | ~2-3x | Fast | Low |
| **LZ4** | **~3-4x** | **Fastest** | **Low** |
| Zstd | ~5-8x | Fast | Medium |

**LZ4 là lựa chọn tốt nhất cho real-time streaming:** compression ratio tốt với CPU overhead thấp nhất. JSON trade data compress rất hiệu quả vì có nhiều field name lặp lại.

#### `LingerMs = 10` — Batching window

Producer **chờ 10ms** trước khi gửi batch. Trong 10ms đó, accumulate thêm messages vào batch.

```
Không có linger (LingerMs=0):
  msg1 → send immediately → 1 round-trip
  msg2 → send immediately → 1 round-trip
  msg3 → send immediately → 1 round-trip
  = 3 round-trips

LingerMs=10:
  msg1 ─┐
  msg2 ─┤ (within 10ms)
  msg3 ─┘
  batch{msg1,msg2,msg3} → send → 1 round-trip
  = 1 round-trip (3x less network overhead)
```

Trade-off: thêm 10ms latency nhưng giảm đáng kể network overhead. Với crypto trades (latency tolerance ~100ms), 10ms là acceptable.

#### `BatchSize = 131_072` (128KB) — Max batch size

Khi batch đạt 128KB (trước khi hết linger window), gửi ngay. Prevents memory bloat khi throughput cao.

#### `MaxInFlight = 5` — Concurrent requests

Producer có thể gửi 5 batches đồng thời mà không cần chờ ACK. Tăng throughput. An toàn khi dùng với idempotence (Kafka reorder bằng sequence numbers).

### Message Key cho partition affinity:

```csharp
public async Task PublishAsync<T>(string topic, string key, T payload, CancellationToken ct)
{
    var msg = new Message<string, string> { Key = key, Value = json, Headers = headers };
    var result = await _producer.ProduceAsync(topic, msg, ct);
}
```

`key = symbol` (ví dụ "BTCUSDT"). Kafka default partitioner: `murmur2(key) % num_partitions`. Tất cả BTCUSDT messages → cùng partition → **ordering guarantee cho mỗi symbol**.

### Graceful shutdown:

```csharp
public ValueTask DisposeAsync()
{
    _producer.Flush(TimeSpan.FromSeconds(15));  // chờ tối đa 15s để gửi hết
    _producer.Dispose();
    return ValueTask.CompletedTask;
}
```

`Flush()` block cho đến khi tất cả buffered messages được gửi hoặc timeout. Không flush = mất messages trong internal buffer.

---

## 5. Backpressure & Micro-Batching với Channel

> **Files:**
> - `src/CryptoDecision.IngestionService/Channels/IngestionChannels.cs`
> - `src/CryptoDecision.IngestionService/Workers/KafkaTradePublisherBase.cs`

### Tại sao cần Channel giữa WebSocket và Kafka?

WebSocket nhận data real-time, nhưng Kafka produce có latency (network round-trip, broker write). Nếu produce **synchronously** trong WebSocket handler:
- WebSocket handler bị block chờ Kafka
- Miss messages từ exchange (exchange disconnect client chậm)
- Không tận dụng được batching

**Channel** giải quyết bằng cách **decouple producer và writer**:

```
WebSocket Thread          Channel (Bounded Buffer)          Kafka Publisher Thread
     │                          │                                │
     │──write(trade)──────────→│                                │
     │──write(trade)──────────→│                                │
     │──write(trade)──────────→│  (accumulate)                  │
     │                          │──────────read batch──────────→│
     │                          │                     publish to Kafka
     │──write(trade)──────────→│                                │
```

### Bounded Channel — Backpressure mechanism:

```csharp
public class TradeChannel
{
    public TradeChannel(int capacity = 50_000)
    {
        _channel = Channel.CreateBounded<Trade>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,  // KEY: block writer when full
            SingleReader = false,
            SingleWriter = false
        });
    }
}
```

**`BoundedChannelFullMode.Wait`:** Khi channel đầy (50,000 items), `WriteAsync` **block** cho đến khi consumer đọc bớt. Tạo **natural backpressure**:

```
Normal flow:
  WebSocket → [Channel: 5,000/50,000] → Kafka Publisher
                    ↑ write ngay

Kafka chậm (broker overloaded):
  WebSocket → [Channel: 50,000/50,000 FULL] → Kafka Publisher (đang chờ ACK)
                    ↑ WriteAsync blocks!
                    WebSocket tự slow down

Kafka recover:
  WebSocket → [Channel: 30,000/50,000] → Kafka Publisher (bắt kịp)
                    ↑ write ngay trở lại
```

**Tại sao 50,000?** Mỗi `Trade` record ~200 bytes. 50,000 × 200 = ~10MB memory. Đủ buffer 5-10 giây peak Binance throughput.

### Micro-batching — Dual trigger flush:

```csharp
// KafkaTradePublisherBase.cs
lock (_lock)
{
    buf.Add(trade);
    shouldFlush = buf.Count >= _batch.BatchSize ||              // Trigger 1: 200 trades
                  DateTimeOffset.UtcNow >= _flushDue[trade.Symbol]; // Trigger 2: 100ms elapsed
}
```

**Tại sao dual trigger?**

| Scenario | Single trigger (size only) | Dual trigger |
|----------|--------------------------|--------------|
| High volume (1000 trades/s) | Flush mỗi 200 trades = 5 flushes/s ✓ | Same |
| Low volume (1 trade/s) | Chờ 200s mới flush! ✗ | Flush sau 100ms ✓ |

Dual trigger = **bounded latency** (max 100ms) + **efficient batching** (khi volume cao).

### Thread safety — Lock pattern:

```csharp
private readonly Dictionary<string, List<Trade>> _buffers = new();
private readonly object _lock = new();
```

Có **2 concurrent tasks** access `_buffers`:
1. `ReadAllAsync` loop — thêm trades vào buffer
2. `RunTimerFlushAsync` — periodic timer flush

Không lock → race condition: timer đọc buffer đang bị modify → crash hoặc data loss.

```csharp
// Thread 1 (ReadAllAsync)          // Thread 2 (Timer)
lock (_lock) {                      lock (_lock) {
    buf.Add(trade);                     snapshot = buf.ToList();
    shouldFlush = ...;                  buf.Clear();
}                                   }
```

### Graceful shutdown — Drain on cancellation:

```csharp
// After ReadAllAsync completes (channel closed):
string[] remaining;
lock (_lock) { remaining = _buffers.Keys.ToArray(); }
foreach (var symbol in remaining)
    await FlushSymbolAsync(symbol, CancellationToken.None);  // CancellationToken.None!
```

Quan trọng: dùng `CancellationToken.None` khi drain, vì `stoppingToken` đã cancelled. Nếu dùng `stoppingToken` → publish bị skip → mất data.

### Per-exchange isolated channels:

```csharp
public sealed class OkxTradeChannel()  : TradeChannel(50_000);
public sealed class BybitTradeChannel() : TradeChannel(50_000);
```

Mỗi exchange có channel riêng. Tại sao? Nếu dùng chung 1 channel:
- OKX chậm → channel đầy → **block cả Binance writer** (cross-exchange interference)
- Không track được exchange nào đang backpressure

Isolated channel = **fault isolation per exchange**.

---

## 6. Consumer — ProcessorService (.NET)

> **File:** `src/CryptoDecision.ProcessorService/Kafka/KafkaConsumerBase.cs`

### Consumer Config:

```csharp
var config = new ConsumerConfig
{
    BootstrapServers  = "kafka:9092",
    GroupId           = "processor-group",
    AutoOffsetReset   = AutoOffsetReset.Earliest,
    EnableAutoCommit  = false,
    MaxPollIntervalMs = 300_000,
    SessionTimeoutMs  = 45_000,
    FetchMinBytes     = 1,
    FetchWaitMaxMs    = 500
};
```

### Giải thích từng setting:

#### `GroupId = "processor-group"` — Consumer Group

Consumer Group là cách Kafka scale consumers. Mọi consumer cùng GroupId **chia nhau** partitions:

```
Topic: binance.trade.btcusdt (3 partitions)

1 consumer trong group:
  Consumer-1: [P0, P1, P2]    ← handle tất cả

2 consumers trong group:
  Consumer-1: [P0, P1]
  Consumer-2: [P2]

3 consumers trong group:
  Consumer-1: [P0]
  Consumer-2: [P1]
  Consumer-3: [P2]             ← optimal: 1:1 mapping

4 consumers trong group:
  Consumer-1: [P0]
  Consumer-2: [P1]
  Consumer-3: [P2]
  Consumer-4: []               ← IDLE! Thêm consumer > partitions = lãng phí
```

#### `EnableAutoCommit = false` — Manual offset commit

**Auto commit (default):** Kafka tự commit offset mỗi 5s. Vấn đề: nếu app crash **sau** auto-commit nhưng **trước** khi process xong → **message bị mất** (consumer group nghĩ đã xử lý xong).

**Manual commit:** App commit **chỉ sau khi** xử lý thành công:

```csharp
await ProcessAsync(message, cr.Topic, stoppingToken);
consumer.Commit(cr);  // ← commit SAU khi process xong
```

Nếu crash trước `Commit()` → restart → Kafka gửi lại message từ last committed offset → **at-least-once guarantee**.

#### `AutoOffsetReset = Earliest` — Bắt đầu từ đầu

Khi consumer group **mới** (chưa có committed offset), đọc từ **đầu topic** thay vì chỉ messages mới. Đảm bảo không miss data khi deploy lần đầu.

#### `MaxPollIntervalMs = 300_000` (5 phút)

Thời gian tối đa giữa 2 lần poll. Nếu consumer không poll trong 5 phút (vd: processing quá lâu), broker coi consumer là dead → **rebalance** partitions cho consumer khác.

#### `SessionTimeoutMs = 45_000` (45 giây)

Heartbeat timeout. Consumer gửi heartbeat mỗi `heartbeat.interval.ms` (default 3s). Nếu broker không nhận heartbeat trong 45s → consumer bị kick khỏi group.

#### `FetchMinBytes = 1, FetchWaitMaxMs = 500`

- `FetchMinBytes = 1`: Fetch ngay khi có ≥1 byte data (low latency)
- `FetchWaitMaxMs = 500`: Nếu không đủ data, chờ tối đa 500ms rồi return empty

### Consumer loop:

```csharp
while (!stoppingToken.IsCancellationRequested)
{
    var cr = consumer.Consume(TimeSpan.FromMilliseconds(500));
    if (cr is null) continue;     // no message within 500ms

    var message = _deserializer.Deserialize(cr.Message.Value);

    if (message is null)
    {
        consumer.Commit(cr);      // skip poison pill
        continue;
    }

    await ProcessAsync(message, cr.Topic, stoppingToken);
    consumer.Commit(cr);          // ← commit ONLY after success
    backoff = TimeSpan.FromSeconds(1);  // reset backoff
}
```

### Critical: `await Task.Yield()` ở đầu ExecuteAsync

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    await Task.Yield();  // ← CRITICAL
    // ... consumer loop
}
```

**Tại sao?** `consumer.Consume()` là **blocking call** (C library underneath). Nếu không yield, `Host.StartAsync()` gọi `ExecuteAsync` → block mãi → **các hosted service khác (HealthCheck, KlineProcessor) không bao giờ start**.

`Task.Yield()` trả control về thread pool ngay lập tức, cho phép `StartAsync` tiếp tục start các service khác. Consumer loop chạy trên background thread.

### Partition assignment handlers:

```csharp
.SetPartitionsAssignedHandler((_, p) =>
    _logger.LogInformation("Partitions assigned: {Partitions}", string.Join(",", p)))
.SetPartitionsRevokedHandler((_, p) =>
    _logger.LogInformation("Partitions revoked: {Partitions}", string.Join(",", p)))
```

Log khi partitions được assign/revoke. Hữu ích để debug rebalancing.

**Rebalancing xảy ra khi:**
- Consumer mới join group
- Consumer crash/disconnect
- Topic thêm partitions
- Consumer không poll trong `MaxPollIntervalMs`

---

## 7. Delivery Guarantees — At-Least-Once vs Exactly-Once

### So sánh 3 mức guarantee:

| Guarantee | Nghĩa | Cách đạt được | Trade-off |
|-----------|-------|---------------|-----------|
| **At-most-once** | Mỗi message xử lý 0 hoặc 1 lần | Auto-commit trước khi process | Nhanh nhất, có thể mất data |
| **At-least-once** | Mỗi message xử lý ≥1 lần | Commit sau khi process ✓ | Có thể duplicate |
| **Exactly-once** | Mỗi message xử lý đúng 1 lần | Kafka Transactions | Chậm nhất, phức tạp nhất |

### Project này dùng At-Least-Once. Tại sao?

**Producer side:** Idempotent producer (`EnableIdempotence=true`) = exactly-once delivery **to broker** (per producer session).

**Consumer side:** Manual commit sau khi DB write = **at-least-once processing**.

```
Timeline khi crash:

1. Consumer nhận message offset=42
2. Process: COPY to PostgreSQL ✓
3. App crash TRƯỚC khi commit
4. Restart → consumer đọc lại offset=42 (chưa commit)
5. Process lại: COPY to PostgreSQL → DUPLICATE!
```

### Xử lý duplicate — Idempotent consumer:

Project xử lý duplicate ở **database level**:

```sql
-- Unique constraint trên trades table
CREATE UNIQUE INDEX uq_trades_exchange_trade_id
    ON trades (exchange, trade_id, trade_time);
```

Nếu COPY insert duplicate `(exchange, trade_id, trade_time)` → PostgreSQL reject → **idempotent**.

Cho klines, dùng `INSERT ... ON CONFLICT DO UPDATE`:

```sql
INSERT INTO klines_1m (...) VALUES (...)
ON CONFLICT (symbol, open_time) DO UPDATE SET ...
```

**Pattern:** At-least-once delivery + idempotent consumer = **effectively exactly-once** (không cần Kafka Transactions, đơn giản hơn nhiều).

---

## 8. Serialization — Source-Generated JSON

> **Files:**
> - `src/CryptoDecision.IngestionService/Serialization/KafkaProducerJsonContext.cs`
> - `src/CryptoDecision.ProcessorService/Serialization/KafkaConsumerJsonContext.cs`

### Message format:

```json
{
  "exchange": "BINANCE",
  "symbol": "BTCUSDT",
  "batchTimestamp": "2026-03-21T10:30:00Z",
  "trades": [
    {
      "symbol": "BTCUSDT",
      "tradeId": 123456789,
      "price": 87234.50,
      "quantity": 0.01,
      "quoteQty": 872.345,
      "isBuyerMaker": false,
      "tradeTime": "2026-03-21T10:30:00.123Z"
    }
  ]
}
```

### Source-generated vs Reflection-based serialization:

```csharp
// Producer (IngestionService):
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TradeBatch))]
[JsonSerializable(typeof(KlineBatch))]
public partial class KafkaProducerJsonContext : JsonSerializerContext { }

// Consumer (ProcessorService):
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]    // ← tolerant reader
[JsonSerializable(typeof(TradeBatch))]
[JsonSerializable(typeof(KlineBatch))]
public partial class KafkaConsumerJsonContext : JsonSerializerContext { }
```

**Tại sao source-generated?**

| Aspect | Reflection-based | Source-generated |
|--------|-----------------|-----------------|
| Startup time | Slow (build metadata at runtime) | **Zero** (compiled at build time) |
| Memory | Allocates metadata objects | **Zero** extra allocation |
| AOT compatible | ✗ | **✓** (no reflection) |
| Throughput | ~300K ops/s | **~500K ops/s** (~60% faster) |

Với thousands of trades/second, source-generated JSON là critical cho performance.

### Pluggable Deserialization (OCP Pattern):

```csharp
// Interface
public interface IMessageDeserializer<TMessage> where TMessage : class
{
    TMessage? Deserialize(string json);
}

// Implementation
public sealed class TradeBatchDeserializer : IMessageDeserializer<TradeBatch>
{
    public TradeBatch? Deserialize(string json)
        => JsonSerializer.Deserialize(json, KafkaConsumerJsonContext.Default.TradeBatch);
}
```

**Tại sao interface?**
- **OCP (Open/Closed Principle):** Thêm message type mới = thêm 1 class implements `IMessageDeserializer<T>`. Zero thay đổi `KafkaConsumerBase`.
- **Testability:** Mock `IMessageDeserializer` trong unit test.
- **Trước refactor:** `KafkaConsumerBase` có `if (typeof(T) == typeof(TradeBatch))` — vi phạm OCP.

---

## 9. Distributed Tracing qua Kafka Headers

> **Files:**
> - `KafkaProducerService.cs` (inject header)
> - `KafkaConsumerBase.cs` (extract header)

### Vấn đề:

Khi request đi qua Kafka, trace bị **đứt**:

```
IngestionService → [Kafka] → ProcessorService
    Trace A              Trace B (mới, không liên quan!)
```

### Giải pháp: W3C traceparent header

**Producer inject:**

```csharp
using var activity = _activitySource.StartActivity($"kafka.publish {topic}", ActivityKind.Producer);

var headers = new Headers();
if (activity != null)
{
    var traceparent = $"00-{activity.TraceId}-{activity.SpanId}-01";
    headers.Add("traceparent", Encoding.UTF8.GetBytes(traceparent));
}

var msg = new Message<string, string> { Key = key, Value = json, Headers = headers };
```

**Consumer extract:**

```csharp
ActivityContext parentCtx = default;
if (cr.Message.Headers?.TryGetLastBytes("traceparent", out var tpBytes) == true)
{
    var tpValue = Encoding.UTF8.GetString(tpBytes);
    ActivityContext.TryParse(tpValue, null, out parentCtx);
}

using var activity = _activitySource.StartActivity(
    $"kafka.consume {cr.Topic}", ActivityKind.Consumer, parentCtx);
//                                                      ^^^^^^^^
//                                                      parent = producer's span
```

**Kết quả trong Tempo:**

```
Trace: abc123
├── kafka.publish binance.trade.btcusdt  (IngestionService, 2ms)
│   └── kafka.consume binance.trade.btcusdt  (ProcessorService, 15ms)
│       └── db.copy trades  (PostgreSQL COPY, 8ms)
```

Một trace liền mạch từ WebSocket → Kafka → PostgreSQL.

### W3C traceparent format:

```
00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01
^^  ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^  ^^^^^^^^^^^^^^^^  ^^
│           Trace ID (128-bit)          Span ID (64-bit)  │
Version                                               Flags (01=sampled)
```

---

## 10. Python Producer — PredictionService

> **File:** `src/CryptoDecision.PredictionService/prediction_service/kafka_producer.py`

### Python producer config:

```python
conf = {
    "bootstrap.servers": settings.kafka_bootstrap_servers,
    "enable.idempotence": True,
    "acks": "all",
    "compression.type": "lz4",
    "linger.ms": 10,
    "retries": 5,
    "retry.backoff.ms": 500,
}
```

Config giống .NET producer (idempotent, acks=all, lz4) — consistency across services.

### Lazy singleton pattern:

```python
_producer: Producer | None = None

def get_producer() -> Producer:
    global _producer
    if _producer is None:
        _producer = _make_producer()
    return _producer
```

Producer chỉ khởi tạo khi `publish()` được gọi lần đầu. Avoid connection overhead nếu prediction cycle chưa chạy.

### Synchronous flush:

```python
def publish(topic: str, key: str, payload: dict[str, Any]) -> None:
    producer = get_producer()
    value = json.dumps(payload, default=str).encode()
    producer.produce(topic=topic, key=key.encode(), value=value, callback=_delivery_report)
    producer.flush()  # ← BLOCK cho đến khi broker ACK
```

**Tại sao sync flush?** Predictions rất low volume (1 message mỗi 5 phút per symbol). Không cần async batching. `flush()` đảm bảo message đã được deliver trước khi function return.

### Delivery callback:

```python
def _delivery_report(err, msg):
    if err:
        log.error("kafka_delivery_failed", error=str(err))
    else:
        log.debug("kafka_delivered", topic=msg.topic())
```

Confluent Kafka Python gọi callback khi broker ACK (hoặc error). Log cho observability.

---

## 11. Error Handling & Retry Patterns

### Producer retry (IngestionService):

```csharp
const int maxAttempts = 5;
var backoff = TimeSpan.FromMilliseconds(100);

for (int attempt = 1; attempt <= maxAttempts; attempt++)
{
    try
    {
        var result = await _producer.ProduceAsync(topic, msg, ct);
        return;  // success
    }
    catch (ProduceException<string, string> ex)
        when (!ex.Error.IsFatal && attempt < maxAttempts)
    {
        await Task.Delay(backoff, ct);
        backoff *= 2;  // exponential: 100ms → 200ms → 400ms → 800ms → 1600ms
    }
}
```

**2 layers of retry:**
1. **Confluent.Kafka internal:** `MessageSendMaxRetries=10, RetryBackoffMs=100` — handle transient network errors
2. **Application-level:** 5 attempts with exponential backoff — handle higher-level failures (topic not ready, etc.)

**`!ex.Error.IsFatal`**: Chỉ retry **transient** errors. Fatal errors (authentication failure, invalid topic) = fail immediately (không retry vô nghĩa).

### Consumer error handling:

```csharp
catch (ConsumeException ex)
{
    // Kafka-level error (network, deserialization)
    _logger.LogError(ex, "Kafka consume exception — retrying in {Backoff}s", backoff.TotalSeconds);
    await Task.Delay(backoff, stoppingToken);
    backoff = Cap(backoff * 2);  // max 60s
}
catch (Exception ex)
{
    // Processing error (DB write failed, etc.)
    _logger.LogError(ex, "Processing failed — NOT committing");
    // Do NOT commit — message will be redelivered after restart
    await Task.Delay(backoff, stoppingToken);
    backoff = Cap(backoff * 2);
}
```

**Key insight:** Processing error → **KHÔNG commit**. Message sẽ được redelivery khi consumer restart. Đây là core của at-least-once guarantee.

### Poison pill handling:

```csharp
if (message is null)
{
    _logger.LogWarning("Null deserialization on {Topic}[{Partition}]@{Offset}",
        cr.Topic, cr.Partition.Value, cr.Offset.Value);
    consumer.Commit(cr);  // skip poison pill
    continue;
}
```

**Poison pill:** Message mà consumer không thể deserialize (corrupted, wrong schema). Nếu không handle → consumer stuck retry mãi mãi (infinite loop).

**Pattern:** Log warning + commit + skip. Trong production, gửi poison pills vào **Dead Letter Queue (DLQ)** để investigate sau.

### Backoff cap:

```csharp
private static TimeSpan Cap(TimeSpan t) =>
    t > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : t;
```

Exponential backoff capped ở 60s. Không cap → backoff tăng vô hạn (2^20 = 1M seconds ≈ 11.5 ngày).

---

## 12. Production Gotchas & Lessons Learned

### Bug 1: `consumer.Consume()` blocks `StartAsync`

**Symptom:** Health check endpoint không start, Kafka consumer chạy nhưng các hosted service khác không start.

**Root cause:** `Consume()` gọi C library blocking. Trong `BackgroundService.ExecuteAsync()`, nếu method không yield, `Host.StartAsync()` chờ mãi.

**Fix:** `await Task.Yield()` ở đầu `ExecuteAsync`.

### Bug 2: `__consumer_offsets` không tạo được

**Symptom:** Consumer subscribe thành công nhưng `Consume()` trả về null mãi. Không bao giờ nhận message.

**Root cause:** `__consumer_offsets` internal topic có default `replication-factor=3`. Với 1 broker → không đủ replicas → topic creation fails silently.

**Fix:** `KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR: 1`

### Bug 3: Race condition trong batch publisher

**Symptom:** Intermittent `KeyNotFoundException` hoặc missing trades.

**Root cause:** Timer task và ReadAllAsync loop đều access `_buffers` dictionary concurrently.

**Fix:** `private readonly object _lock = new()` — guard mọi access vào `_buffers` và `_flushDue`.

### Bug 4: `PropertyNameCaseInsensitive=true` crash Binance parsing

**Symptom:** Binance trade messages fail deserialization.

**Root cause:** Binance JSON có cả lowercase `"e"` (event type) và uppercase `"E"` (event time). Với `CaseInsensitive=true`, hai field này collide.

**Fix:** Producer dùng `CaseInsensitive=false` (strict). Consumer dùng `CaseInsensitive=true` (tolerant reader pattern).

### Bug 5: Kafka data lost on container restart

**Symptom:** Restart kafka container → all messages gone.

**Root cause:** Default `KAFKA_LOG_DIRS` = `/tmp/kafka-logs`. Docker container `/tmp` is ephemeral.

**Fix:** `KAFKA_LOG_DIRS: /var/lib/kafka/data` + Docker named volume `kafka_data`.

### Bug 6: docker-compose command word-splitting

**Symptom:** kafka-init chỉ tạo 1 topic (đầu tiên), còn lại bị ignore.

**Root cause:** YAML `command: >-` folded string bị Docker Compose word-split → chỉ first token làm `-c` script.

**Fix:** `command:` as YAML list with single `>-` element:
```yaml
command:
  - >-
    kafka-topics.sh ... && kafka-topics.sh ... && ...
```

---

## 13. Kafka Interview Cheat Sheet

### Concepts bạn cần giải thích được:

#### 1. "Kafka như một distributed commit log"

Kafka không phải message queue. Nó là **append-only log**. Messages không bị xóa sau khi consume (khác RabbitMQ). Consumers track position bằng **offset**.

#### 2. "Consumer Group cho horizontal scaling"

"Chúng tôi có 10 trade topics (5 exchanges × 2 symbols), mỗi topic 3 partitions. Với consumer group 'processor-group', mỗi consumer instance nhận một subset partitions. Scale từ 1 lên 3 instances = 3x throughput."

#### 3. "At-least-once delivery + idempotent consumer"

"Producer dùng `EnableIdempotence=true` + `Acks.All` cho exactly-once delivery tới broker. Consumer commit offset SAU khi write PostgreSQL. Nếu crash → duplicate delivery. Database unique constraint đảm bảo idempotency. Effectively exactly-once mà không cần Kafka Transactions (đơn giản hơn, nhanh hơn)."

#### 4. "Backpressure với Bounded Channels"

"WebSocket nhận ~5000 trades/s. Nếu Kafka chậm, Channel buffer (50K capacity) đầy → WebSocket writer block → natural backpressure. Không cần rate limiter hay complex flow control."

#### 5. "Partition key cho ordering"

"Message key = symbol (BTCUSDT). Kafka hash key → cùng partition. Mọi BTCUSDT trades đúng thứ tự trên 1 partition. Ordering guarantee là per-partition, không phải global."

#### 6. "Distributed tracing across Kafka"

"Producer inject W3C traceparent header vào Kafka message. Consumer extract → continue trace. Trong Tempo, thấy trace liền mạch: WebSocket → Kafka produce → Kafka consume → PostgreSQL COPY."

#### 7. "KRaft thay ZooKeeper"

"Từ Kafka 3.3+, metadata management chuyển sang Raft consensus (KRaft). Không cần ZooKeeper nữa. Giảm operational complexity: 1 process thay vì 2. CLUSTER_ID hardcoded (ZooKeeper tự generate)."

### System design answer template:

> **Q:** "How would you handle high-throughput event streaming from multiple data sources?"
>
> **A:** "Trong CryptoDecision, tôi thiết kế pipeline:
> 1. **Ingestion layer**: WebSocket clients nhận real-time data từ 5 exchanges (Binance, OKX, Bybit, Coinbase, Kraken)
> 2. **Buffering**: Bounded Channels (50K capacity) với backpressure (BoundedChannelFullMode.Wait)
> 3. **Micro-batching**: Accumulate 200 trades HOẶC 100ms timeout, whichever first → giảm Kafka round-trips
> 4. **Kafka**: Idempotent producer, LZ4 compression, 3 partitions per symbol cho parallel consumption
> 5. **Processing**: Consumer group với manual offset commit → at-least-once delivery. PostgreSQL COPY for bulk insert (30x faster than INSERT). Unique constraint cho deduplication.
> 6. **Observability**: W3C traceparent headers propagated qua Kafka → end-to-end distributed tracing trong Tempo/Grafana."

---

*Document generated from CryptoDecision source code. Last updated: 2026-03-21.*
