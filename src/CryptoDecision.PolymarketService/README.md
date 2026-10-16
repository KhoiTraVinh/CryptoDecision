# CryptoDecision.PolymarketService

Bot đặt lệnh chờ mua kèo cửa dưới trên các trận esports của Polymarket. Nó **chỉ vào
lệnh** — không có logic thoát, không có DB, không có Kafka, không đụng gì tới phần SOL
perp của repo này. Nó chỉ nói chuyện ra ngoài với Gamma, CLOB và Data API.

Mặc định `DryRun=true`: quét, quyết định, in log, **không gửi lệnh nào**.

---

## 1. Chiến thuật, và nó dựa trên số đo nào

Đo trên toàn bộ lịch sử công khai của ví `@greenworld`
(`0x350dc6df635fe22471cddd678f82189b9fa71d30`) — **20.068 dòng activity, 01/01 → 13/10/2026**.
API chặn ở `offset=5000` nên phải quét theo cửa sổ thời gian mới lấy đủ.

Ví đó **bắt đầu chiến thuật 1¢ vào giữa tháng 5/2026**, thấy rõ ở tỷ lệ lệnh vào đúng 1¢
theo tháng: T1–T4 là 1–6%, **T5 nhảy lên 40%**, T6 89%, T7 96%, từ T8 là 100%. Mọi số
dưới đây chỉ tính cohort từ 01/05/2026 trở đi:

| | |
|---|---|
| Vị thế vào ở 1¢ | 7.305 trên 145 ngày (~50/ngày) |
| Chi ra | $9.624 |
| Thu về | $11.672 → **+21,3%** |
| Tỷ lệ trúng | 1,97% thô — nhưng đọc tiếp mục ngay dưới |

Chỉ 3 loại market: *Map N Winner*, *Game N Winner*, *Series BOx*. Không handicap, không
over/under.

### Toàn bộ lợi nhuận nằm ở market **ôm cả hai phe**

Tách 7.305 vị thế theo số phe mà ví đó kết thúc với trong cùng một market:

| | vị thế | chi ra | thu về | lãi/lỗ | ROI | trúng |
|---|---|---|---|---|---|---|
| **ôm cả hai phe @1¢** | 172 | $208 | $8.146 | **+$7.938** | +3.819% | 50,00% |
| **chỉ một phe** | 7.133 | $9.416 | $3.526 | **−$5.890** | −62,6% | 0,81% |

Ôm cả hai phe ở 1¢ không phải hedge và không phải cược đồng xu: đó là **cầm chắc token
thắng với giá ~2¢**. Tỷ lệ trúng đúng bằng 50,00% (86/172) vì mỗi market như vậy luôn có
đúng một chân thắng — số học, không phải may. Nhóm chỉ một phe trúng **0,81%**, nằm
**dưới** mức hòa vốn 1,00%, tức là nó lỗ, và lỗ ở **mọi** mức volume (0,94% ở ≥$50k,
0,83% ở ≥$200k).

Con số "1,74% · z = 3,36" ở bản trước đếm hai chân của một market ôm-hai-phe như hai lần
thử độc lập. Chúng không độc lập: một trong hai **không thể thua**. Vì vậy
`RestBothSides` giờ mặc định `true` — xem mục 3.

> ⚠️ Hai điều mẫu 5 tháng mới lộ ra:
>
> **Nhóm một phe đang tệ dần** — T5 1,47%, T6 1,20%, T7 1,36%, rồi T8 **0,36%**, T9 0,64%,
> T10 0,53%. **2/6 tháng tổng thể âm** (+$679, −$91, +$434, −$164, +$413, +$777). Đây là
> chiến thuật +EV nhưng có tháng lỗ; đừng tắt bot sau ba tuần xấu.
>
> **Tần suất market hai-phe** trôi từ 3,32% (T5) xuống ~1,1% và đứng ở đó. Cơ chế là số
> học, **tần suất thì không**. Nếu 1,1% đó không giữ được thì cả chiến thuật âm.

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

Nên bot đặt **GTC limit buy** nằm chờ ở 0,01, và **chỉ đặt khi ask hiện tại còn
cao hơn mức đó** — để lệnh nằm chờ chứ không khớp ngay thành lệnh thị trường.

### Vì sao **không** có lệnh thoát

23 vị thế được bán sớm thu $1.693,75. Tra kết quả thật của đúng 23 trận đó qua CLOB:
nếu giữ đến khi resolve thì được **$1.660,40**. Chênh 2%. Edge nằm ở lúc vào, không nằm
ở lúc ra.

### Lọc volume — mặc định $25k, và nó **không** làm cái người ta tưởng

Đo lại trên trọn 5 tháng (7.305 lệnh, 145 ngày), tách theo hai nhóm:

| ngưỡng | lệnh/ngày | nhóm **một phe** | nhóm **hai phe** | **tổng** | ROI |
|---|---|---|---|---|---|
| không lọc | 50,4 | −$5.890 | +$7.938 | **+$2.048** | 21,3% |
| ≥ $20k | 35,6 | −$4.076 | +$6.921 | **+$2.846** | 41,3% |
| **≥ $25k** (mặc định) | 33,6 | −$3.713 | +$6.817 | **+$3.104** | 47,9% |
| ≥ $50k (cũ) | 26,3 | −$2.821 | +$5.405 | **+$2.584** | 51,0% |
| ≥ $100k | 19,9 | −$2.103 | +$4.020 | +$1.917 | 50,7% |
| ≥ $500k | 8,9 | −$807 | +$2.812 | +$2.004 | 120,9% |

ROI tăng theo ngưỡng, nhưng **lãi tuyệt đối đạt đỉnh quanh $20–30k**. Lý do: ngưỡng cắt
lỗ của nhóm một phe và cắt lãi của nhóm hai phe **gần như cùng tốc độ**.

Và đây mới là điều quyết định — volume **không dự báo được** market nào sẽ thành hai phe,
tức là nhóm duy nhất sinh lời:

| dải volume | market | thành hai phe | tỷ lệ |
|---|---|---|---|
| $0–10k | 1.307 | 12 | 0,92% |
| $10–25k | 1.103 | 12 | 1,09% |
| $25–50k | 1.035 | 16 | 1,55% |
| $50–100k | 922 | 14 | 1,52% |
| $100–250k | 955 | 7 | 0,73% |
| ≥ $250k | 1.897 | 25 | 1,32% |

Tỷ lệ phẳng lì. Kiểm lại bằng cách khác: **53,5%** market hai-phe vượt mốc $50k, so với
**52,3%** của phần còn lại — không chọn lọc được gì cả.

Nên $25k được chọn **vì độ phủ, không phải vì đỉnh ROI trong mẫu**: nó giữ 77% số cơ hội
hai-phe thay vì 53% ở mốc $50k. Nhóm hai phe là động cơ duy nhất, nên nhìn thấy nhiều cơ
hội hơn đáng giá hơn một con số ROI đẹp trên ít lệnh.

> ⚠️ **Thiên lệch chưa khử được.** Mọi bảng trên dùng volume **cuối cùng** của market.
> Trận nào lội ngược dòng thì volume bùng nổ *sau* thời điểm vào lệnh, nên các mốc volume
> cao bị thổi phồng — nghĩa là thiên lệch này **ủng hộ hạ ngưỡng**, không phải nâng. Bot
> lọc theo volume **quan sát được tại thời điểm đặt lệnh** và ghi vào log, để sau vài
> tuần trả lời lại bằng dữ liệu sạch của chính nó.

### Mức 3¢ **đã tắt**

2.184/2.189 vị thế của mẫu vào ở đúng 1¢; mức 3¢ gần như không xuất hiện, nên không có
số đo nào cho nó ở cả hai đầu. Hòa vốn ở 3¢ cần tỷ lệ trúng **3,00%**, trong khi nhóm
một phe của chính ví đó chỉ đạt 0,75%. Mặc định giờ là một mức duy nhất `0.01`. Bật lại
bằng `POLY_ENTRY_PRICES=0.03,0.01` nếu muốn tự đo; mọi lệnh đều ghi mức giá vào log.

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
| `POLY_STAKE_USD` | `0.20` | Mỗi lệnh. Sàn từ chối lệnh dưới 5 share, nên ở 1¢ **đừng xuống dưới $0,05**. Ví đang chạy đặt $0,06 = 6 share |
| `POLY_MAX_DAILY_USD` | `15` | Trần tiền cam kết mỗi ngày UTC. Đếm trong RAM — restart là reset. **`0` = tắt trần**, lúc đó số dư ví là giới hạn duy nhất |
| `POLY_MIN_VOLUME_USD` | `25000` | Ngưỡng volume. Xem mục 1 — nó lọc lỗ vặt, **không** chọn được market tốt |
| `POLY_REST_BOTH_SIDES` | `true` | Nguồn lợi nhuận. Xem mục dưới |
| `POLY_ARM_BELOW_ASK` | `0.10` | Chỉ vào khi giá bên đó **đã rớt** xuống dưới mức này. `1.0` = vào ngay khi thấy trận. Bắt được 84,6% cơ hội — xem bảng ở mục 3 |

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

**Mặc định `true`.** Đây là nơi toàn bộ lợi nhuận của mẫu nằm: 25 market ôm cả hai phe
trả về +$3.243, trong khi 2.139 market chỉ một phe lỗ $1.962 (bảng ở mục 1).

Nó **không** đặt hai lệnh cùng lúc. Hai phe không bao giờ cùng nằm dưới `ArmBelowAsk`,
nên mỗi chu kỳ vẫn chỉ arm được một phe. Phe thứ hai chỉ được arm **nếu trận lật kèo** và
phe kia sụp đổ tới lượt nó — đúng 25 market nói trên. Market bình thường vẫn chỉ tốn một
lệnh, nên chi phí thực tế tăng ít hơn nhiều so với "gấp đôi".

Khi `false`, market bị khoá vào phe đầu tiên ngay lúc đặt lệnh (`Commit`), nên bot
**không bao giờ** vào được 25 market đó — nó chỉ nhân bản đúng nửa lỗ −59,6%.

Lưu ý đi kèm: lý do hạ `ArmBelowAsk` xuống thấp là để đỡ khoá nhầm phe. Khi đã ôm cả hai
phe thì "nhầm phe" không còn là lỗi, mà là nửa đầu của thế ôm hai phe. Thứ còn lại chỉ là
**độ bắt được**, và độ bắt được thì bảo nâng lên — xem bảng ngay dưới.

### `ArmBelowAsk` — đo trực tiếp, không còn ước lượng

Lấy 474 token mà ví mẫu đã mua ở 1¢, kéo price history của CLOB ở độ phân giải 1 phút,
đếm số phút giá nằm trong khoảng (1¢, ngưỡng] **trước khi** bên đó chết hẳn. Bot quét
20 giây/lần nên cần ít nhất một phút như vậy mới có cơ hội đặt lệnh:

| ngưỡng | số phút trong vùng (trung vị) | % số lần **không có phút nào** | bắt được |
|---|---|---|---|
| 0.03 | 0 | 50,6% | 49,4% |
| 0.05 | 1 | 34,4% | 65,6% |
| 0.07 | 3 | 22,8% | 77,2% |
| **0.10** (hiện tại) | 4 | 15,4% | **84,6%** |
| 0.11 | 5 | 14,1% | 85,9% |
| 0.15 | 7 | 5,3% | **94,7%** |
| 0.20 | 9 | 2,1% | 97,9% |
| 0.30 | 21 | 0,2% | 99,8% |
| 1.00 | 240 | 0,0% | 100% |

Giá **gap thẳng xuống không phải là chuyện của kèo vol thấp** — cần biết điều này trước
khi đổ lỗi cho `MinVolumeUsd` mỗi lần miss. Độ bắt được ở 0.07 tách theo volume market:
76,1% dưới $25k, 73,9% ở $25–100k, 84,4% ở $100–500k, 77,0% trên $500k. Phẳng.

**Luật "bên kia lên 0.90 thì gài bên còn lại" chính là ngưỡng này.** Trên book thật, tổng
hai ask của một market hai cửa nằm trong 1,010–1,030, nên luật đó tương đương ngưỡng
~0.11 — cùng một thứ, không phải cơ chế mới. Và **đừng** triển khai nó bằng cách canh
**ask** của bên mạnh: book bên đó rỗng đúng lúc trận kết thúc (14/25 market đang mở không
có một lệnh bán nào ở bên mạnh, trong khi bên yếu nằm ở 0.001). Đại lượng bền là **bid**
của bên mạnh — nhưng không đáng viết thêm code: cũng trong 25 market đó, bên yếu **luôn**
có ask, nên nhánh dự phòng ấy sẽ chạy đúng 0 lần.

Bảng này thay cho số đo cũ "24,6% ở 0.05 / 55,5% ở 0.15" (đo trên 171 market); bản cũ để
lại trong git history chứ không cố hoà giải hai cách đo.

Lưu ý nó **tương tác với `MinVolumeUsd`**: market chưa giao dịch đủ $25k thì không đủ điều
kiện dù ngưỡng arm có cao tới đâu, nên độ bắt được thực tế bị chặn thêm bởi tốc độ volume
tích luỹ, không chỉ bởi ngưỡng này.

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
