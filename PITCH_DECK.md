# CHÀO BÁN DỰ ÁN: CRYPTODECISION STARDUST

*Nền tảng Giao dịch Tiền điện tử Đa Chiến Thuật Ứng Dụng Trí Tuệ Nhân Tạo & Dữ Liệu Thời Gian Thực*

---

## TỔNG QUAN

**CryptoDecision** là một AI Trader toàn thời gian chạy trên nền tảng vi dịch vụ siêu chịu tải. Hệ thống kết nối trực tiếp tới **5 sàn giao dịch lớn nhất thế giới** (Binance, OKX, Bybit, Coinbase, Kraken), thu thập hàng chục ngàn giao dịch mỗi giây. Quyết định được đưa ra dựa trên thuật toán Máy học (AI Prediction), Khối lượng Cá mập (Whale Tracking), và phân tích Đa Khung Thời Gian (Multi-Timeframe Momentum).

Engine Đa Chiến Thuật (Multi-Strategy Engine) cho phép chạy song song Grid DCA, Momentum Scalping, RSI, với AI Filter tự động chặn entry xấu và AI Sizing điều chỉnh khối lượng lệnh theo độ tin cậy.

## ĐIỂM NỔI BẬT

1. **Dữ Liệu 5 Sàn Real-time**
   - Kết nối WebSocket trực tiếp tới Binance, OKX, Bybit, Coinbase, Kraken. Mỗi giao dịch được chuẩn hóa và phân tích trong vài mili-giây.

2. **Theo Dấu Cá Voi (Whale Tracking)**
   - Tự động phát hiện giao dịch >$100,000 USDT từ hàng triệu giao dịch. Phân tích áp lực mua/bán cá voi theo nhiều khung thời gian.

3. **Trí Tuệ Nhân Tạo Đa Tín Hiệu**
   - Heuristic v2 (5 signals: Return, Volume, Whale, RSI, Volume Acceleration) + XGBoost (Optuna tuned).
   - Bot tích hợp AI: Filter chặn lệnh sai hướng, Sizing điều chỉnh vốn theo confidence.

4. **Đa Chiến Thuật Tự Hành**
   - **Grid DCA** gánh đỡ thị trường trượt giá. **Momentum v2** phục kích khi momentum tốt. **RSI** bắt đáy/đỉnh. Mỗi strategy độc lập với cooldown, max trades, trailing stop riêng.

5. **6 Microservices Production-Ready**
   - 4 .NET 9 Workers + 1 Python AI + 1 Web API. Apache Kafka (KRaft) + PostgreSQL 16 (partitioned). Grafana + Tempo + Prometheus cho observability.

6. **Giao Diện Real-time**
   - Dashboard web với Chart.js + SignalR push (không HTTP polling). Mobile app CapacitorJS (Android/iOS). Hiển thị price charts từ 5 sàn, AI signals, equity curve, whale alerts.

## TIỀM NĂNG THƯƠNG MẠI HÓA

Sản phẩm được đóng gói **Production-Ready** qua Docker:
- **Retail (SaaS):** Thu phí duy trì hằng tháng cho người dùng đăng ký Bot.
- **B2B White-label:** Đóng gói cho sàn giao dịch nhỏ, quỹ đầu tư gia đình (Family Offices).
- **Data-as-a-Service:** Cung cấp dữ liệu whale tracking + AI predictions cho third-party.

## SỐ LIỆU KỸ THUẬT

| Chỉ số | Giá trị |
|--------|---------|
| Sàn kết nối | 5 (Binance, OKX, Bybit, Coinbase, Kraken) |
| Kafka topics | 15 |
| Database tables | 9 |
| API endpoints | 18 REST + 8 SignalR events |
| Design patterns | 10+ (Strategy, Template Method, Adapter, CQRS, Observer, ...) |
| Trading strategies | 4 (Grid, Momentum v2, RSI, AlwaysBuy) |
| RAM headless mode | ~1.7 GB |
| Throughput DB write | 50,000 rows/sec (COPY binary) |

> *CryptoDecision biến dữ liệu hỗn loạn của thế giới Crypto thành lợi nhuận có thể đong đếm được.*
