# CryptoDecision.PolymarketService

Bot đặt lệnh chờ mua kèo cửa dưới trên các trận esports của Polymarket. Nó **chỉ vào
lệnh** — không có logic thoát, không có DB, không có Kafka, không đụng gì tới phần SOL
perp của repo này. Nó chỉ nói chuyện ra ngoài với Gamma, CLOB và Data API.

Mặc định `DryRun=true`: quét, quyết định, in log, **không gửi lệnh nào**.

---

## 1. Chiến thuật, và nó dựa trên số đo nào

Đo trên toàn bộ lịch sử công khai của ví `@greenworld`
(`0x350dc6df635fe22471cddd678f82189b9fa71d30`), 5.500 dòng activity, 06/09 → 12/10/2026:

| | |
|---|---|
| Vị thế | 2.193 (~59/ngày) |
| Chi ra | $3.322,31 |
| Thu về | $4.499,50 → **+35,4%** |
| Giá vào | **94,2% số tiền vào ở đúng $0,01** |
| Tỷ lệ trúng | **1,74%** (hòa vốn cần 1,00%) |
| Kiểm định nhị thức | z = 3,36 · p = 0,0004 |

Chỉ 3 loại market: *Map N Winner*, *Game N Winner*, *Series BOx*. Không handicap, không
over/under — 2.169/2.169 market đều là kèo thắng thua.

### Vì sao là **lệnh chờ**, không phải lệnh thị trường

Đây là điểm dễ làm sai nhất và đã suýt làm sai:

* **99,0% vị thế có tổng share tròn** (100, 80, 250, 200, 150 share). Đó là kích thước
  một lệnh đặt sẵn, không phải kết quả quét sạch order book.
* **26,3% vị thế khớp làm nhiều miếng**, trung vị cách nhau 12 giây, p90 là 4,6 phút,
  tối đa 11 miếng. Đó là lệnh chờ bị sellers ăn dần.
* Chụp ảnh thị trường thật: trong 272 bên của 136 trận đang mở, 199 bên giá ≥ 0,30; 21
  bên dưới 0,005; và **đúng 1 bên nằm trong khoảng 1¢–5¢**. Giá nhảy thẳng qua vùng vào
  lệnh. Một bot "mua ngay" gần như không bao giờ khớp được ở 1¢ — và khi khớp được thì
  nó đang mua đúng nhóm đã chết, nhóm mà mọi lệnh dưới 1¢ trong mẫu đều thua.

Nên bot đặt **GTC limit buy** nằm chờ ở 0,03 và 0,01, và **chỉ đặt khi ask hiện tại còn
cao hơn mức đó** — để lệnh nằm chờ chứ không khớp ngay thành lệnh thị trường.

### Vì sao **không** có lệnh thoát

23 vị thế được bán sớm thu $1.693,75. Tra kết quả thật của đúng 23 trận đó qua CLOB:
nếu giữ đến khi resolve thì được **$1.660,40**. Chênh 2%. Edge nằm ở lúc vào, không nằm
ở lúc ra.

### Lọc volume

Mô phỏng đều tay $0,20/lệnh trên chính 2.187 lệnh đó:

| Lọc volume | lệnh/ngày | tỷ lệ trúng | ROI | chuỗi thua dài nhất |
|---|---|---|---|---|
| không lọc | 59,1 | 1,74% | +54% | 212 lệnh |
| ≥ $20k | 40,5 | 1,87% | +68% | 227 lệnh |
| **≥ $50k** (mặc định) | 29,5 | **2,20%** | **+96%** | 159 lệnh |
| ≥ $200k | 16,3 | 2,56% | +138% | 91 lệnh |
| ≥ $500k | 8,8 | 3,46% | +229% | 60 lệnh |

Dưới $20k thì **lỗ** (−23% ở nhóm $5k–20k, −100% ở nhóm <$1k).

> ⚠️ **Thiên lệch chưa khử được.** Bảng trên dùng volume **cuối cùng** của market. Trận
> nào cửa dưới lội ngược dòng thì volume bùng nổ *sau* thời điểm vào lệnh, nên các mốc
> volume cao gần như chắc chắn bị thổi phồng. Bot lọc theo volume **quan sát được tại
> thời điểm đặt lệnh** và ghi con số đó vào log, để sau vài tuần có thể trả lời lại câu
> hỏi này bằng dữ liệu sạch của chính nó.

### Mức 3¢ **chưa có bằng chứng**

94,2% tiền của mẫu vào ở 1¢; mức 3¢ gần như không xuất hiện. Hòa vốn ở 3¢ cần tỷ lệ
trúng 3,00% và **không có số liệu nào nói nó đạt được**. Mức này được bật vì được yêu
cầu; mọi lệnh đều ghi mức giá vào log để tách bạch về sau.

---

## 2. Cần chuẩn bị gì

| Thứ | Ghi chú |
|---|---|
| Ví self-custody | Ví **OKX Web3 Wallet** (không phải tài khoản sàn OKX — sàn không đưa private key nên không ký lệnh được) |
| `POLY_PRIVATE_KEY` | Private key export từ ví đó. **Đặt vào biến môi trường, đừng dán vào chat hay commit** |
| `POLY_FUNDER_ADDRESS` | Địa chỉ ví **Polymarket**, không phải địa chỉ ví OKX. Tra chắc chắn bằng `https://polymarket.com/api/profile/userData?address=<dia-chi-vi>` → field `proxyWallet` |
| `POLY_SIGNATURE_TYPE` | `3` cho **Deposit Wallet** (kiểu tài khoản Polymarket cấp hiện nay) · `2` Gnosis Safe proxy · `1` email/Magic · `0` EOA trực tiếp |
| Tiền trong ví Polymarket | Xem mục *Hai đường ray* bên dưới — có thể là USDC.e hoặc pUSD tuỳ tài khoản |
| Approval cho sàn | Thường đã có sẵn nếu bạn từng giao dịch trên web. Preflight sẽ nói rõ. Bot **không** set hộ được vì cần giao dịch on-chain |

> Preflight tự kiểm tra cả ba thứ và **từ chối chạy live** nếu sai, nên đừng đoán — chạy dry-run
> một lần rồi đọc dòng `Preflight:`. Nếu `holder` trùng `signer` thì bạn đã điền nhầm địa chỉ ví
> vào `POLY_FUNDER_ADDRESS`.

### Hai đường ray: USDC.e và pUSD

Polymarket đang chuyển hệ giữa chừng, và đây là thứ tốn nhiều thời gian nhất khi dựng bot này:

| | Đường ray cũ | Đường ray hiện tại |
|---|---|---|
| Exchange | `0x4bFb41d5…8982E` / `0xC5d563A3…0f80a` | `0xE1111800…B996B` / `0xe2222d27…310F59` |
| Collateral | USDC.e `0x2791Bca1…84174` | **pUSD** `0xC011a7E1…82DFB` ("Polymarket USD", 6 decimals) |
| EIP-712 domain version | `"1"` | **`"2"`** |
| Struct Order | có `taker`/`expiration`/`nonce`/`feeRateBps` | bỏ hết, thêm `timestamp`/`metadata`/`builder` |
| Kiểu tài khoản | EOA / proxy / Safe | thêm **Deposit Wallet** (ERC-1271, signatureType 3) |

`@polymarket/clob-client` **5.8.1 (bản mới nhất) vẫn trỏ vào đường ray cũ** và vẫn ký version
`"1"`. Ký version `"1"` vào contract mới thì CLOB trả về đúng câu:

```
HTTP 400: {"error":"invalid order version, please use the latest clob-client"}
```

Bot xử lý chuyện này bằng cách **hỏi chính contract** chứ không hardcode:

* `getCollateral()` → token nào là tiền cược, rồi kiểm tra số dư + allowance của đúng token đó;
* `eip712Domain()` (ERC-5267) → `name` và `version` của domain, dùng luôn để ký.

Nhờ vậy lần Polymarket đổi tiếp cũng không phải sửa code. Hai contract cũ không có ERC-5267 nên
bot tự lùi về domain `"1"` — vẫn đúng với chúng.

⚠️ **Đừng tin `/balance-allowance` của CLOB làm cổng chặn.** Nó báo đường ray cũ. Đo trên một tài
khoản thật đang có **$45,00 pUSD và allowance không giới hạn**, nó trả về
`{"balance":"0","allowances":{…toàn 0}}` — tài khoản đủ tiền trông y hệt tài khoản rỗng. Bot vẫn
in con số đó ra log để đối chiếu, nhưng quyết định dựa trên số đọc được on-chain.

### Vốn cần bao nhiêu

Mô phỏng 40.000 lần chạy lại, $0,20/lệnh, lọc ≥$50k:

| | 30 ngày (884 lệnh) | 90 ngày (2.651 lệnh) |
|---|---|---|
| Tổng tiền đưa ra cả kỳ | $177 | $530 |
| Sụt giảm sâu nhất, phân vị 50% | $31 | $43 |
| Sụt giảm sâu nhất, phân vị 99% | $70 | $88 |
| Tệ nhất trong 40.000 lần | $125 | $157 |
| Lãi cuối kỳ (p5 / p50 / p95) | $47 / $167 / $305 | $291 / $508 / $739 |

Nhưng các con số đó **giả định tỷ lệ trúng 2,20% là thật**. Trường hợp xấu nhất thành
thật là chiến thuật không có edge và mất toàn bộ tiền đã đặt. Con số trung thực:

> **$200 USDC cho một tháng chạy thử ở $0,20/lệnh** — đủ phủ kịch bản mất sạch
> ($177 đưa ra trong 30 ngày), chứ không chỉ phủ drawdown. Thêm **$60–90** nếu bật
> mức 3¢ (ước tính, chưa đo). Thêm vài đô POL để trả gas cho lần approve đầu.

Và phải chịu được chuỗi thua: trong mẫu đã có đoạn **159 lệnh liên tiếp không trúng gì
(~5 ngày)**, xác suất đi 300 lệnh (~10 ngày) không trúng vẫn là 0,13%.

---

## 3. Chạy

```bash
docker compose up -d --build polymarket && docker compose logs -f polymarket
```

Lần chạy đầu để nguyên `POLY_DRY_RUN=true`. Đọc dòng `Preflight:` — nó in địa chỉ ký,
địa chỉ giữ tiền, token tiền cược, số dư và allowance. **Chỉ khi cả ba con số đó đúng**
mới đặt `POLY_DRY_RUN=false`. Một lần chạy khoẻ mạnh trông như sau:

```
Exchange 0xE1111800…B996B declares EIP-712 domain name="Polymarket CTF Exchange" version="2"
Preflight: signer=0xCCd2…945e7 holder=0x80ce…2af5 signatureType=2 exchange=0xE1111800…B996B
           collateral=0xc011a7e1…2dfb (pUSD) balance=45.00 allowance=unlimited
           [clob reported 0.00/0.00]
```

Hai chỗ đáng để ý trong đó: `version="2"` (không phải `"1"` như mọi package công bố), và
`[clob reported 0.00/0.00]` — bằng chứng sống của chuyện endpoint CLOB không nhìn thấy
tiền trên đường ray mới.

Log bình thường, mỗi 20 giây một dòng:

```
Gamma scan: 7 markets kept; skipped 0 by kind, 260 under $50000 volume, 2 outside the time window
WOULD REST 20 x BNK FEARX @ 0.01 (ask now 0.07, $0.20) | LoL | vol $84239 | LoL: BNK FEARX vs JD Gaming - Game 3 Winner
Cycle: 7 markets in play, 7 with work left, 6 new resting orders, 0 resting in total, $1.20 committed today
```

Không có health endpoint và không có port. Dấu hiệu sống là dòng `Cycle:` — bot khỏe thì
in đều, dù có vào lệnh hay không.

### Các nút chỉnh

| Biến | Mặc định | Ý nghĩa |
|---|---|---|
| `POLY_DRY_RUN` | `true` | Công tắc an toàn. Private key **một mình không** mở khóa gì |
| `POLY_STAKE_USD` | `0.20` | Mỗi lệnh. Dưới mức này sẽ không đủ 5 share tối thiểu ở 3¢ |
| `POLY_MAX_DAILY_USD` | `15` | Trần tiền cam kết mỗi ngày UTC. Đếm trong RAM — restart là reset |
| `POLY_MIN_VOLUME_USD` | `50000` | Ngưỡng volume |
| `POLY_REST_BOTH_SIDES` | `false` | Xem mục dưới |
| `POLY_ARM_BELOW_ASK` | `0.15` | Chỉ vào khi giá bên đó **đã rớt** xuống dưới mức này. `1.0` = vào ngay khi thấy trận |

### Vì sao không có chốt lời (TP)

Đã đo bằng lịch sử giá của 287 vị thế trong cohort chiến thuật. Hệ số thu về, so với giữ
đến khi có kết quả (2,202):

| TP | % thắng chạm TP | % thua chạm TP | Hệ số | So với không TP |
|---|---|---|---|---|
| 0.15 | 75,0% | 1,50% | 1,018 | −54% |
| 0.30 | 65,0% | 0,75% | 1,420 | −36% |
| 0.50 | 45,0% | 0,37% | 1,890 | −14% |
| 0.75 | 15,0% | 0,00% | 2,119 | −3,8% |

Bất đối xứng nằm ở hai cột giữa. Trường hợp TP cứu được — giá hồi rồi đội vẫn thua — chỉ
xảy ra với **0,37%–1,5%** số lệnh thua. Còn **45%–75%** số lệnh *thắng* đi xuyên qua mức
đó trên đường lên 1.00. Vào ở 1¢ thì thắng là 100x; bán ở 0.50 là 50x, để đổi lấy cơ hội
0,37%. Không đáng.

23 lệnh bán của ví tham chiếu trông có lãi vì chúng được **chọn bằng mắt** sau khi thấy
giá hồi, không phải lệnh chờ: **14 trong 17** vị thế thắng đo được đã **nhảy vọt qua 0.75**
thẳng lên kết quả. Market esports resolve ngay khi map kết thúc, nên giá hiếm khi thực sự
giao dịch ở vùng cao.

Đã đo thêm cả kiểu **chốt từng nấc** (lấy lại vốn rồi thả phần còn lại), trên 891 vị thế:

| Cách chốt | Bán | Hệ số | So với giữ | $/lệnh | Số lệnh với $45 | P(trắng tay) |
|---|---|---|---|---|---|---|
| **Không chốt gì** | — | **2,202** | — | $0,05 | **900** | **0%** |
| 10% @0.10 + 10% @0.20 | 20% | 1,971 | −10,5% | $0,51 | 88 | 14% |
| 10% @0.10 (lấy lại vốn) | 10% | 2,074 | −5,8% | $0,51 | 88 | 14% |
| 5% @0.20 (lấy lại vốn) | 5% | 2,150 | −2,3% | $1,01 | 44 | 38% |
| 2% @0.50 (lấy lại vốn) | 2% | 2,194 | −0,3% | $2,50 | 18 | 67% |

Nấc càng cao càng rẻ, vì chỉ cần bán ít share hơn để thu về cùng số vốn. Nhưng sàn từ chối
lệnh bán dưới 5 share, nên nấc cao buộc vị thế to hơn, và số lệnh sụt thẳng đứng. Kỳ vọng
gần như không đổi ($93–$99 trên $45); thứ đổi là **độ chắc chắn**.

Giữ đến kết quả thắng trên **cả ba cột cùng lúc** — kỳ vọng, số lệnh, và xác suất học được
điều gì đó. Không có đánh đổi nào để cân nhắc.

Đã viết thử rồi gỡ bỏ sau khi đo. Bot giữ vị thế đến khi trận có kết quả.
| `POLY_EXCHANGE_ADDRESS` | `0xE1111800…B996B` | Contract ký lệnh. Đổi nếu tài khoản nằm trên đường ray khác |
| `POLY_NEGRISK_EXCHANGE_ADDRESS` | `0xe2222d27…310F59` | Như trên, cho market neg-risk. Esports đều `negRisk=false` nên gần như không dùng tới |

### `RestBothSides`

Mẫu đo được trung bình **1,01 vị thế mỗi market** trên 2.169 market, và chỉ 24 market
có cả hai bên — đúng hình dạng của việc đặt chờ cả hai bên rồi chỉ bên sụp đổ mới khớp.
Đặt cả hai bên **không phải là hedge**: nếu hiếm hoi cả hai cùng khớp thì bạn trả $0,40
cho tấm vé chắc chắn trúng dù đội nào thắng.

Mặc định vẫn `false` vì yêu cầu là "vô một phía thôi". Khi `false`, bot chọn **bên đang
rẻ hơn** — bên nhiều khả năng chạm tới mức giá vào nhất. Bật `true` sẽ tăng gấp đôi số
lệnh và số tiền mỗi ngày.

---

## 4. Vì sao không cần DB

Trạng thái duy nhất cần nhớ là "đã vào kèo này chưa", và cả hai nguồn sự thật đều nằm
ngoài tiến trình:

* **Vị thế đang giữ** → Data API, on-chain, sống sót qua restart.
* **Lệnh đang treo** → `GET /data/orders` trên CLOB.

Mỗi `PositionRefreshSeconds` (mặc định 300s) bot đọc lại cả hai và dựng lại bức tranh.
Khi đọc lỗi, nó **giữ nguyên** bức tranh cũ thay vì coi như "không giữ gì" — vì nhầm
lẫn đó sẽ khiến nó mua lại toàn bộ lần thứ hai.

Bot cũng tự huỷ lệnh treo trên các trận đã kết thúc (market rời khỏi danh sách hợp lệ),
để không còn lệnh sống trên trận không ai theo dõi nữa.

---

## 5. Ghi chú kỹ thuật: ký lệnh

Polymarket đòi ba thứ, đều nằm trong `Polymarket/PolymarketSigner.cs`:

1. **L1** — chữ ký EIP-712 `ClobAuth`, dùng một lần để lấy API credentials. Domain
   **không có** `verifyingContract` (chỉ 3 trường).
2. **L2** — HMAC-SHA256 trên `timestamp + method + path + body`. Secret là base64**url**
   (`-`/`_`), `Convert.FromBase64String` của .NET từ chối nó nên phải đổi về `+`/`/`
   trước khi decode. Path ký là path trần, **không kèm query string**.
3. **Order** — chữ ký EIP-712 `Order`. `verifyingContract` khác nhau giữa market thường
   và market neg-risk; ký nhầm contract cho ra chữ ký hợp lệ cho contract sai và bị từ chối.
   `name` và `version` của domain **đọc từ contract bằng ERC-5267**, không hardcode.

Struct `Order` và tên domain lấy trực tiếp từ `@polymarket/order-utils`, và cả hai chữ ký
EIP-712 đã được **đối chiếu byte-for-byte với `ethers` v5 `_signTypedData`** trên cùng một
test key trước khi tin dùng. Domain separator của hai contract cũ cũng đã đối chiếu với giá
trị `domainSeparator()` đọc on-chain — khớp.

Nếu đổi tên type, thứ tự trường hay chuỗi domain thì sự tương đương đó mất, và CLOB chỉ
recover ra một địa chỉ khác rồi từ chối mà không báo lỗi gì hữu ích.

Cách truy khi nghi struct đổi: quét hằng số 32-byte (`PUSH32`, opcode `0x7f`) trong bytecode
contract và so với `keccak256` của chuỗi type. Contract cũ chứa typehash chuẩn
`0xa852566c…9767c`; contract mới thì không, nhưng lại chứa `"Polymarket CTF Exchange"` nằm
cạnh ký tự `"2"` — đó là cách tìm ra version đã đổi, trước khi `eip712Domain()` xác nhận.
