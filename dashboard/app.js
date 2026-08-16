/* ── CryptoDecision Dashboard — app.js ───────────────────────────────────── */

const SERVER = (typeof window !== 'undefined' && window.SERVER_URL) ? window.SERVER_URL : '';
const API = SERVER + '/api';
const HUB_URL = SERVER + '/hubs/market';

// ── State ─────────────────────────────────────────────────────────────────────
let activeSymbol = 'BTCUSDT';
let activeExchange = 'ALL';
let gaugeChart = null;
let klineCombinedChart = null;
let volumeChart = null;
let equityChart = null;
let hubConn = null;
let volumeData = null;
let activeVolWindow = '24h';

// ── Chart default styling ──────────────────────────────────────────────────────
Chart.defaults.color = '#6e7681';
Chart.defaults.borderColor = 'rgba(255,255,255,0.06)';
Chart.defaults.font.family = "'Inter', system-ui, sans-serif";
Chart.defaults.font.size = 11;

// ── Helpers ────────────────────────────────────────────────────────────────────
function pct(v, decimals = 2) {
    if (v == null) return '--';
    const n = Number(v);
    return (n >= 0 ? '+' : '') + n.toFixed(decimals) + '%';
}

function usd(v) {
    if (v == null) return '--';
    const n = Number(v);
    const abs = Math.abs(n);
    const sign = n < 0 ? '-' : '';
    if (abs >= 1_000_000_000) return sign + '$' + (abs / 1_000_000_000).toFixed(2) + 'B';
    if (abs >= 1_000_000) return sign + '$' + (abs / 1_000_000).toFixed(2) + 'M';
    if (abs >= 1_000) return sign + '$' + (abs / 1_000).toFixed(1) + 'K';
    return sign + '$' + abs.toFixed(2);
}

function colorClass(v) {
    const n = Number(v);
    if (n > 0) return 'positive';
    if (n < 0) return 'negative';
    return '';
}

function signalBadgeClass(signal) {
    return 'badge-' + (signal || 'NEUTRAL');
}

function setRefreshTime() {
    const el = document.getElementById('last-refresh');
    if (el) el.textContent = 'Updated ' + new Date().toLocaleTimeString();
}

async function apiFetch(path) {
    const r = await fetch(API + path);
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    return r.json();
}

// ── Momentum UI ───────────────────────────────────────────────────────────────
function updateMomentumUI(d) {
    const score = d.score ?? 0;

    const sigEl = document.getElementById('mom-signal');
    if (sigEl) {
        sigEl.textContent = d.signal ?? '--';
        sigEl.className = 'badge ' + signalBadgeClass(d.signal);
    }

    const gaugeValEl = document.getElementById('gauge-val');
    if (gaugeValEl) gaugeValEl.textContent = (score > 0 ? '+' : '') + Number(score).toFixed(2);

    const scoreEl = document.getElementById('mom-score');
    if (scoreEl) scoreEl.textContent = (score > 0 ? '+' : '') + Number(score).toFixed(2);

    const el = name => document.getElementById(name);
    if (el('mom-total')) el('mom-total').textContent = d.totalTrades?.toLocaleString() ?? '--';
    if (el('mom-buy'))   el('mom-buy').textContent = d.buyCount?.toLocaleString() ?? '--';
    if (el('mom-sell'))  el('mom-sell').textContent = d.sellCount?.toLocaleString() ?? '--';
    if (el('mom-whale')) el('mom-whale').textContent = (d.whaleBuyCount ?? '--') + ' / ' + (d.whaleSellCount ?? '--');
    if (el('mom-vol'))   el('mom-vol').textContent = usd(d.volumeUsd);

    if (gaugeChart) {
        let fillPct = Math.max(0, Math.min(100, ((score + 3) / 6) * 100));
        let colorBg;
        if (d.signal?.includes('BUY'))  colorBg = 'rgba(63,185,80,0.8)';
        else if (d.signal?.includes('SELL')) colorBg = 'rgba(248,81,73,0.8)';
        else colorBg = 'rgba(210,153,34,0.8)';

        gaugeChart.data.datasets[0].data = [fillPct, 100 - fillPct];
        gaugeChart.data.datasets[0].backgroundColor[0] = colorBg;
        gaugeChart.update();
    }

    setRefreshTime();
}

// ── Volume Analysis UI ────────────────────────────────────────────────────────
function renderVolumeWindow(winKey) {
    if (!volumeData) return;
    const w = (volumeData.windows || []).find(x => x.window === winKey);
    if (!w) return;

    const el = name => document.getElementById(name);
    if (el('vol-trades'))      el('vol-trades').textContent = w.totalTrades.toLocaleString();
    if (el('vol-net'))         el('vol-net').textContent = usd(w.netVolumeUsd);
    if (el('vol-whale-buy'))   el('vol-whale-buy').textContent = w.whaleBuyCount.toLocaleString();
    if (el('vol-whale-sell'))  el('vol-whale-sell').textContent = w.whaleSellCount.toLocaleString();

    if (volumeChart) {
        volumeChart.data.datasets[0].data = [w.buyVolumeUsd];
        volumeChart.data.datasets[1].data = [w.sellVolumeUsd];
        volumeChart.update('none');
    }
}

async function loadVolumeAnalysis() {
    try {
        volumeData = await apiFetch(`/volume/${activeSymbol}?exchange=${activeExchange}`);
        renderVolumeWindow(activeVolWindow);
    } catch (e) { console.error('loadVolumeAnalysis:', e); }
}

// ── SignalR Hub ───────────────────────────────────────────────────────────────
function buildConnection() {
    return new signalR.HubConnectionBuilder()
        .withUrl(HUB_URL)
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .configureLogging(signalR.LogLevel.Warning)
        .build();
}

async function connectHub() {
    if (hubConn) { try { await hubConn.stop(); } catch (_) {} }
    hubConn = buildConnection();

    hubConn.on('ReceiveMomentum', data => {
        if (data.symbol === activeSymbol) updateMomentumUI(data);
    });
    hubConn.on('ReceiveVolumeAnalysis', data => {
        if (data.symbol === activeSymbol) { volumeData = data; renderVolumeWindow(activeVolWindow); }
    });
    hubConn.on('ReceiveWhaleAlert', data => {
        if (data.symbol === activeSymbol) data.whales.forEach(w => { showWhaleToast(w); addWhaleToFeed(w); });
    });
    hubConn.on('ReceiveMarketStatus', data => {
        if (data.symbol === activeSymbol) updateMarketStatusUI(data);
    });
    hubConn.on('ReceiveKlines', data => {
        if (data.symbol === activeSymbol) updateKlinesUI(data.charts);
    });
    hubConn.on('ReceiveBotStatus', data => {
        updateBotStatusUI(data.status, data.pnl, data.debug, data.trades);
    });
    hubConn.on('ReceiveUserStats', data => {
        updateUserStatsUI(data);
    });
    hubConn.on('ReceiveAlertTriggered', data => {
        console.log('[Alert] Triggered:', data);
        loadAlerts();
        showToast(`Alert: ${data.symbol} ${data.condition} $${Number(data.targetPrice).toLocaleString()}`,
            `Triggered at $${Number(data.actualPrice).toLocaleString()}${data.note ? ' - ' + data.note : ''}`,
            data.condition === 'ABOVE' ? 'whale-buy' : 'whale-sell');
    });

    hubConn.onreconnecting(() => {
        const el = document.getElementById('last-refresh');
        if (el) el.textContent = 'Reconnecting...';
    });
    hubConn.onreconnected(() => {
        hubConn.invoke('Subscribe', activeSymbol, activeExchange).catch(console.error);
        hubConn.invoke('SubscribeVolume', activeSymbol, activeExchange).catch(console.error);
    });
    hubConn.onclose(() => {
        const el = document.getElementById('last-refresh');
        if (el) el.textContent = 'Disconnected';
    });

    try {
        await hubConn.start();
        await hubConn.invoke('Subscribe', activeSymbol, activeExchange);
        await hubConn.invoke('SubscribeVolume', activeSymbol, activeExchange);
    } catch (err) {
        console.warn('[SignalR] Connect failed, polling fallback:', err);
        setInterval(() => loadMomentumHttp(), 5000);
        loadMomentumHttp();
    }
}

// ── Whale Toast ───────────────────────────────────────────────────────────────
function showWhaleToast(whale) {
    let container = document.getElementById('toast-container');
    if (!container) {
        container = document.createElement('div');
        container.id = 'toast-container';
        container.className = 'toast-container';
        document.body.appendChild(container);
    }

    const isBuy = !whale.isBuyerMaker;
    const typeClass = isBuy ? 'whale-buy' : 'whale-sell';
    const typeText = isBuy ? 'BUY' : 'SELL';
    const qtyText = usd(whale.quoteQty);

    const toast = document.createElement('div');
    toast.className = `toast-message ${typeClass}`;
    toast.innerHTML = `
        <div class="toast-header">
            <span>Whale ${whale.exchange}</span>
            <span>${new Date(whale.tradeTime).toLocaleTimeString()}</span>
        </div>
        <div class="toast-body">${typeText} ${qtyText} @ $${Number(whale.price).toLocaleString()}</div>
    `;
    container.appendChild(toast);
    setTimeout(() => { toast.classList.add('fade-out'); setTimeout(() => toast.remove(), 400); }, 5000);
}

function showToast(title, body, typeClass) {
    let container = document.getElementById('toast-container');
    if (!container) {
        container = document.createElement('div');
        container.id = 'toast-container';
        container.className = 'toast-container';
        document.body.appendChild(container);
    }
    const toast = document.createElement('div');
    toast.className = `toast-message ${typeClass || ''}`;
    toast.innerHTML = `
        <div class="toast-header"><span>${title}</span><span>${new Date().toLocaleTimeString()}</span></div>
        <div class="toast-body">${body}</div>`;
    container.appendChild(toast);
    setTimeout(() => { toast.classList.add('fade-out'); setTimeout(() => toast.remove(), 400); }, 6000);
}

// ── Whale Feed ────────────────────────────────────────────────────────────────
async function loadWhaleFeed() {
    try {
        const d = await apiFetch(`/whales/${activeSymbol}?limit=30&exchange=${activeExchange}`);
        const c = document.getElementById('whale-feed-container');
        if (!c) return;
        c.innerHTML = '';
        if (!d || d.length === 0) { c.innerHTML = '<span class="text-muted text-sm">No recent whales.</span>'; return; }
        d.forEach(w => addWhaleToFeed(w, true));
    } catch (e) { console.error('loadWhaleFeed:', e); }
}

function addWhaleToFeed(whale, append = false) {
    const c = document.getElementById('whale-feed-container');
    if (!c) return;
    const ph = c.querySelector('.text-muted');
    if (ph) ph.remove();

    const isBuy = !whale.isBuyerMaker;
    const div = document.createElement('div');
    div.className = `whale-feed-item ${isBuy ? 'buy' : 'sell'}`;
    div.innerHTML = `
        <div class="whale-feed-left">
            <span class="whale-feed-qty">${usd(whale.quoteQty)}</span>
            <span class="whale-feed-price">$${Number(whale.price).toLocaleString()}</span>
        </div>
        <div class="whale-feed-right">
            <span class="whale-feed-time">${isBuy ? 'BUY' : 'SELL'}</span>
            <span class="whale-feed-time">${whale.exchange} ${new Date(whale.tradeTime).toLocaleTimeString()}</span>
        </div>
    `;

    if (append) c.appendChild(div);
    else { c.insertBefore(div, c.firstChild); if (c.children.length > 15) c.lastChild.remove(); }
}

// ── HTTP fallback ─────────────────────────────────────────────────────────────
async function loadMomentumHttp() {
    try {
        const d = await apiFetch(`/momentum/${activeSymbol}?exchange=${activeExchange}`);
        updateMomentumUI(d);
    } catch (e) { console.error('loadMomentumHttp:', e); }
}

// ── Market Status / AI ────────────────────────────────────────────────────────
function updateMarketStatusUI(d) {
    try {
        const dir = d.predictedDirection ?? 'NEUTRAL';
        const conf = d.confidence != null ? (d.confidence * 100).toFixed(1) + '%' : '--';

        const dirEl = document.getElementById('sig-direction');
        const badgeEl = document.getElementById('sig-badge');

        if (dirEl) {
            dirEl.className = 'ai-direction ' + dir;
            dirEl.textContent = dir === 'UP' ? '^ UP' : dir === 'DOWN' ? 'v DOWN' : '-- NEUTRAL';
        }

        if (document.getElementById('sig-confidence'))
            document.getElementById('sig-confidence').textContent = conf;

        if (badgeEl) {
            badgeEl.className = 'badge ' + signalBadgeClass(d.momentumSignal ?? dir);
            badgeEl.textContent = d.momentumSignal ?? dir;
        }

        if (document.getElementById('sig-rationale'))
            document.getElementById('sig-rationale').textContent = d.rationale || '--';

        const el = name => document.getElementById(name);
        if (el('today-return')) {
            el('today-return').textContent = pct(d.return24h);
            el('today-return').className = 'metric-value ' + colorClass(d.return24h);
        }
        if (el('today-vol')) el('today-vol').textContent = pct(d.volatility);
        if (el('today-volchg')) {
            el('today-volchg').textContent = pct(d.volumeChange);
            el('today-volchg').className = 'metric-value ' + colorClass(d.volumeChange);
        }
        if (el('today-vwap')) el('today-vwap').textContent = d.vwap != null ? '$' + Number(d.vwap).toLocaleString() : '--';
        if (el('today-whale')) el('today-whale').textContent = d.whaleCount ?? '--';

        // Compute and render AI signal breakdown bars from feature data
        const sigPanel = el('ai-signals');
        if (sigPanel && (d.return24h != null || d.volatility != null)) {
            // Replicate heuristic scoring logic client-side for live visualization
            const ret = d.return24h ?? 0;
            const volChg = d.volumeChange ?? 0;
            const whale = d.whaleCount ?? 0;
            const vol = d.volatility ?? 0;

            let retScore = 0;
            if (ret > 3) retScore = 3; else if (ret > 1.5) retScore = 2; else if (ret > 0.5) retScore = 1;
            else if (ret < -3) retScore = -3; else if (ret < -1.5) retScore = -2; else if (ret < -0.5) retScore = -1;

            let volScore = 0;
            if (volChg > 20) volScore = 1; else if (volChg > 10) volScore = 0.5;
            else if (volChg < -20) volScore = -1; else if (volChg < -10) volScore = -0.5;

            const dirSign = retScore >= 0 ? 1 : -1;
            let whaleScore = 0;
            if (whale > 50) whaleScore = 1.0 * dirSign; else if (whale > 20) whaleScore = 0.5 * dirSign;

            // RSI proxy
            const momentum = ret / Math.max(vol, 0.5);
            let rsiProxy = Math.max(10, Math.min(90, 50 + momentum * 10));
            let rsiScore = 0;
            if (rsiProxy > 70) rsiScore = -1.5; else if (rsiProxy > 60) rsiScore = -0.5;
            else if (rsiProxy < 30) rsiScore = 1.5; else if (rsiProxy < 40) rsiScore = 0.5;

            let volAccel = 0;
            if (Math.abs(volChg) > 15 && Math.abs(ret) > 1) {
                volAccel = ((volChg > 0 && ret > 0) || (volChg < 0 && ret < 0)) ? 0.5 * dirSign : -0.5 * dirSign;
            }

            const totalScore = retScore + volScore + whaleScore + rsiScore + volAccel;

            const s = { ret_score: retScore, vol_score: volScore, whale_score: whaleScore, rsi_score: rsiScore, vol_accel_score: volAccel, score: totalScore };
            const bars = [
                { label: 'Return',   val: s.ret_score,       max: 3 },
                { label: 'Volume',   val: s.vol_score,       max: 1 },
                { label: 'Whales',   val: s.whale_score,     max: 1 },
                { label: 'RSI',      val: s.rsi_score,       max: 1.5 },
                { label: 'VolAccel', val: s.vol_accel_score, max: 0.5 },
            ];
            sigPanel.innerHTML = bars.map(b => {
                const pct = Math.min(100, Math.abs(b.val / b.max) * 100);
                const color = b.val > 0 ? 'var(--green)' : b.val < 0 ? 'var(--red)' : 'var(--muted)';
                const sign = b.val > 0 ? '+' : '';
                return `<div class="ai-signal-row">
                    <span class="ai-signal-label">${b.label}</span>
                    <div class="ai-signal-bar-track">
                        <div class="ai-signal-bar-fill" style="width:${pct}%;background:${color};"></div>
                    </div>
                    <span class="ai-signal-val" style="color:${color}">${sign}${Number(b.val).toFixed(1)}</span>
                </div>`;
            }).join('') + `<div class="ai-signal-row" style="border-top:1px solid var(--border);padding-top:4px;margin-top:4px;">
                <span class="ai-signal-label" style="font-weight:700;">Total</span>
                <span></span>
                <span class="ai-signal-val" style="font-weight:700;color:${(s.score ?? 0) > 0 ? 'var(--green)' : (s.score ?? 0) < 0 ? 'var(--red)' : 'var(--text)'}">
                    ${(s.score ?? 0) > 0 ? '+' : ''}${Number(s.score ?? 0).toFixed(1)}
                </span>
            </div>`;
        }
    } catch (e) { console.error('updateMarketStatusUI:', e); }
}

async function loadMarketStatus() {
    try {
        const d = await apiFetch(`/market-status/${activeSymbol}?exchange=${activeExchange}`);
        updateMarketStatusUI(d);
    } catch (e) { console.error('loadMarketStatus:', e); }
}

// ── Klines ────────────────────────────────────────────────────────────────────
function updateKlinesUI(chartsData) {
    try {
        let lastCloses = [];
        for (let i = 0; i < chartsData.length; i++) {
            const chart = chartsData[i];
            const data = chart.data;
            const labels = data.map(k => {
                const t = new Date(k.openTime);
                return t.getHours().toString().padStart(2, '0') + ':' + t.getMinutes().toString().padStart(2, '0');
            });
            const closes = data.map(k => Number(k.close));
            if (closes.length > 0) lastCloses.push(`${chart.exchange}: $${closes.at(-1).toLocaleString()}`);

            if (klineCombinedChart) {
                if (i === 0) klineCombinedChart.data.labels = labels;
                if (klineCombinedChart.data.datasets.length > i) klineCombinedChart.data.datasets[i].data = closes;
            }
        }
        if (klineCombinedChart) klineCombinedChart.update('none');

        const titleEl = document.getElementById('kline-combined-title');
        if (titleEl) titleEl.textContent = `Price -- last 60 min  |  ${lastCloses.join('  |  ')}`;
    } catch (e) { console.error('updateKlinesUI:', e); }
}

async function loadKlines() {
    try {
        const exchanges = ['BINANCE', 'BYBIT', 'OKX', 'COINBASE', 'KRAKEN'];
        const chartsData = [];
        for (const ex of exchanges) {
            const data = await apiFetch(`/klines/${activeSymbol}?limit=60&exchange=${ex}`);
            chartsData.push({ exchange: ex, data });
        }
        updateKlinesUI(chartsData);
    } catch (e) { console.error('loadKlines:', e); }
}

// ── User Stats ────────────────────────────────────────────────────────────────
function updateUserStatsUI(d) {
    try {
        const el = name => document.getElementById(name);
        if (el('users-total')) el('users-total').textContent = d.totalUsers ?? '--';
        if (el('users-today')) el('users-today').textContent = d.todayActive ?? '--';

        const recentEl = el('users-recent');
        if (recentEl) {
            const names = d.recentNames;
            if (!names || names.length === 0) {
                recentEl.innerHTML = '<span class="text-muted text-sm">No users yet</span>';
            } else {
                recentEl.innerHTML = names.map(name => `<span class="user-chip">${escHtml(name)}</span>`).join('');
            }
        }
    } catch (e) { console.error('updateUserStatsUI:', e); }
}

async function loadUserStats() {
    try { const d = await apiFetch('/users/stats'); updateUserStatsUI(d); } catch (e) { console.error('loadUserStats', e); }
}

function escHtml(str) {
    return String(str).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// ── Charts ────────────────────────────────────────────────────────────────────
function initCharts() {
    gaugeChart = new Chart(document.getElementById('gauge-chart'), {
        type: 'doughnut',
        data: {
            labels: ['Score', 'Remaining'],
            datasets: [{
                data: [50, 50],
                backgroundColor: ['rgba(210,153,34,0.8)', 'rgba(255,255,255,0.05)'],
                borderColor: ['transparent', 'transparent'],
                borderWidth: 0,
            }]
        },
        options: {
            responsive: true, maintainAspectRatio: false,
            circumference: 180, rotation: -90, cutout: '80%',
            plugins: { legend: { display: false }, tooltip: { enabled: false } },
            animation: { duration: 600, easing: 'easeOutQuart' },
        }
    });

    klineCombinedChart = new Chart(document.getElementById('kline-combined-chart'), {
        type: 'line',
        data: {
            labels: [],
            datasets: [
                { label: 'Binance',  data: [], borderColor: '#f5b041', backgroundColor: 'transparent', borderWidth: 1.5, pointRadius: 0, tension: 0.3 },
                { label: 'Bybit',    data: [], borderColor: '#58d68d', backgroundColor: 'transparent', borderWidth: 1.5, pointRadius: 0, tension: 0.3 },
                { label: 'OKX',      data: [], borderColor: '#e74c3c', backgroundColor: 'transparent', borderWidth: 1.5, pointRadius: 0, tension: 0.3 },
                { label: 'Coinbase', data: [], borderColor: '#3498db', backgroundColor: 'transparent', borderWidth: 1.5, pointRadius: 0, tension: 0.3 },
                { label: 'Kraken',   data: [], borderColor: '#9b59b6', backgroundColor: 'transparent', borderWidth: 1.5, pointRadius: 0, tension: 0.3 }
            ]
        },
        options: {
            responsive: true, maintainAspectRatio: false,
            interaction: { intersect: false, mode: 'index' },
            plugins: { legend: { display: true, labels: { boxWidth: 10, padding: 10 } } },
            scales: {
                x: { grid: { display: false }, ticks: { maxTicksLimit: 12, maxRotation: 0 } },
                y: { position: 'right', ticks: { callback: v => '$' + v.toLocaleString() }, grid: { color: 'rgba(255,255,255,0.03)' } }
            },
            animation: { duration: 400 },
        }
    });

    volumeChart = new Chart(document.getElementById('volume-chart'), {
        type: 'bar',
        data: {
            labels: ['Volume (USD)'],
            datasets: [
                { label: 'Buy',  data: [0], backgroundColor: 'rgba(63,185,80,0.7)',  borderColor: '#3fb950', borderWidth: 1, borderRadius: 4 },
                { label: 'Sell', data: [0], backgroundColor: 'rgba(248,81,73,0.7)', borderColor: '#f85149', borderWidth: 1, borderRadius: 4 }
            ]
        },
        options: {
            indexAxis: 'y', responsive: true, maintainAspectRatio: false,
            plugins: {
                legend: { display: true, position: 'bottom', labels: { boxWidth: 10, padding: 10 } },
                tooltip: { callbacks: { label: ctx => ` ${ctx.dataset.label}: ${usd(ctx.parsed.x)}` } }
            },
            scales: {
                x: { ticks: { callback: v => usd(v) }, grid: { display: false } },
                y: { grid: { display: false } }
            },
            animation: { duration: 300 },
        }
    });
}

// ── Symbol switch ─────────────────────────────────────────────────────────────
async function switchSymbol(symbol) {
    const prev = activeSymbol;
    activeSymbol = symbol;
    document.querySelectorAll('.tab-btn').forEach(b => b.classList.toggle('active', b.dataset.symbol === symbol));

    if (hubConn?.state === signalR.HubConnectionState.Connected) {
        try {
            await hubConn.invoke('Unsubscribe', prev, activeExchange);
            await hubConn.invoke('Subscribe', symbol, activeExchange);
            await hubConn.invoke('UnsubscribeVolume', prev, activeExchange);
            await hubConn.invoke('SubscribeVolume', symbol, activeExchange);
        } catch (err) { console.error('[SignalR] Resub failed:', err); }
    }

    volumeData = null;
    loadMarketStatus();
    loadKlines();
    loadVolumeAnalysis();
    loadWhaleFeed();
    loadAlerts();
}

// ── Bot Dashboard ─────────────────────────────────────────────────────────────
let _botRunning = false;

function updateBotStatusUI(status, pnl, debug, trades) {
    if (!status) return;
    _botRunning = status.isRunning;

    const badge = document.getElementById('bot-status-badge');
    const btn = document.getElementById('bot-toggle-btn');

    if (badge) {
        let text = status.isRunning ? (status.paperMode ? 'PAPER' : 'LIVE') : 'STOPPED';
        if (status.isRunning && debug?.options?.activeStrategies) text += ' | ' + debug.options.activeStrategies.join('+');
        badge.textContent = text;
        badge.className = status.isRunning ? 'badge badge-BUY' : 'badge badge-NEUTRAL';
    }
    if (btn) {
        btn.textContent = status.isRunning ? 'Stop' : 'Start';
        btn.className = 'btn ' + (status.isRunning ? 'btn-danger' : 'btn-primary');
    }

    const el = name => document.getElementById(name);
    const pnlUsd = status.totalPnlUsd ?? 0;
    if (el('bot-pnl-usd')) {
        el('bot-pnl-usd').textContent = (pnlUsd >= 0 ? '+' : '') + '$' + pnlUsd.toFixed(4);
        el('bot-pnl-usd').style.color = pnlUsd >= 0 ? 'var(--green)' : 'var(--red)';
    }
    if (el('bot-winrate') && pnl) {
        el('bot-winrate').textContent = pnl.totalTrades > 0 ? (pnl.winRate * 100).toFixed(1) + '%' : '--';
    }
    if (el('bot-trades-count')) el('bot-trades-count').textContent = status.totalTrades ?? 0;
    if (el('bot-open-trade')) el('bot-open-trade').textContent = status.openTradeCount ?? 0;

    const condList = el('bot-conditions-list');
    const willEnterEl = el('bot-will-enter');

    if (condList) {
        if (!status.isRunning) {
            condList.innerHTML = '<span class="text-muted text-sm">Start bot to see live conditions...</span>';
            if (willEnterEl) willEnterEl.textContent = '';
        } else if (debug?.conditions) {
            condList.innerHTML = debug.conditions.map(c => {
                const icon = c.willEnter ? '>' : '-';
                const color = c.willEnter ? 'var(--green)' : 'var(--muted)';
                const sColor = c.strategy === 'GRID' ? 'var(--purple)' : 'var(--accent)';
                return `<div style="display:flex;align-items:center;justify-content:space-between;font-size:12px;padding:3px 0;">
                    <span style="color:${sColor};font-weight:700;">[${c.strategy}]</span>
                    <span>Slots: ${c.slots}</span>
                    <span style="color:${color};font-weight:600;font-family:monospace;">${icon} ${c.cooldown}</span>
                </div>`;
            }).join('');

            if (willEnterEl) {
                const price = debug.currentPrice ? '$' + Number(debug.currentPrice).toLocaleString() : '';
                willEnterEl.innerHTML = `<span class="text-muted text-sm">Market: ${price}</span>`;
            }

            if (debug.openPositions?.length > 0) {
                const listHtml = debug.openPositions.map(p => {
                    const color = p.pnl?.startsWith('-') ? 'var(--red)' : 'var(--green)';
                    const sColor = p.strategy === 'GRID' ? 'var(--purple)' : 'var(--accent)';
                    const sideColor = p.side === 'SHORT' ? 'var(--red)' : 'var(--green)';
                    return `<div style="display:flex;align-items:center;justify-content:space-between;font-size:11px;padding:3px 0;border-top:1px solid var(--border);">
                        <span><span style="color:${sColor};">[${p.strategy}]</span> <span style="color:${sideColor};">[${p.side || 'LONG'}]</span> #${p.id} (${p.age})</span>
                        <span style="color:${color};font-weight:700;">${p.pnl}</span>
                    </div>`;
                }).join('');
                condList.innerHTML += `<div style="margin-top:8px;padding-top:6px;border-top:2px solid var(--border);">
                    <div style="font-size:9px;color:var(--muted);text-transform:uppercase;margin-bottom:4px;letter-spacing:0.5px;font-weight:700;">Open Positions</div>
                    ${listHtml}
                </div>`;
            }
        } else if (debug?.error) {
            condList.innerHTML = `<span class="text-muted text-sm">${debug.error}</span>`;
        }
    }

    if (trades) {
        renderBotTrades(trades);
        renderEquityCurve(trades);
    }
    updateBotConfigVisibility(status.isRunning);
    renderStrategyPerformance(pnl);
}

function renderBotTrades(trades) {
    const tbody = document.getElementById('bot-trade-rows');
    if (!tbody) return;
    if (!trades || trades.length === 0) {
        tbody.innerHTML = '<tr><td colspan="5" class="text-center text-muted" style="padding:20px;">No trades yet</td></tr>';
        return;
    }
    tbody.innerHTML = trades.map(t => {
        const pnl = t.pnlUsd ?? 0;
        const color = pnl >= 0 ? 'var(--green)' : 'var(--red)';
        const time = new Date(t.openedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        const exitStr = t.exitPrice ? '$' + Number(t.exitPrice).toLocaleString() : '--';
        const pnlStr = t.pnlUsd != null ? (pnl >= 0 ? '+' : '') + '$' + pnl.toFixed(4) : '--';
        const reason = t.closeReason
            ? `<span style="background:rgba(88,166,255,0.12);color:var(--accent);padding:2px 6px;border-radius:4px;font-size:9px;">${t.closeReason}</span>`
            : '<span class="text-muted">OPEN</span>';
        const sColor = t.strategy === 'GRID' ? 'var(--purple)' : 'var(--accent)';
        const sideColor = t.side === 'SHORT' ? 'var(--red)' : 'var(--green)';
        return `<tr>
            <td style="padding:8px 6px;">
                <div style="font-weight:600;">${time}</div>
                <div style="font-size:9px;color:${sColor};font-weight:600;">${t.strategy} <span style="color:${sideColor};">[${t.side || 'LONG'}]</span></div>
            </td>
            <td class="text-right" style="padding:8px 6px;">$${Number(t.entryPrice).toLocaleString()}</td>
            <td class="text-right" style="padding:8px 6px;">${exitStr}</td>
            <td class="text-right" style="padding:8px 6px;color:${color};font-weight:700;">${pnlStr}</td>
            <td class="text-center" style="padding:8px 6px;">${reason}</td>
        </tr>`;
    }).join('');
}

async function loadBotStatus() {
    try {
        const [status, pnl, debug, trades] = await Promise.all([
            fetch(API + '/bot/status').then(r => r.ok ? r.json() : null),
            fetch(API + '/bot/pnl').then(r => r.ok ? r.json() : null),
            fetch(API + '/bot/debug').then(r => r.ok ? r.json() : null),
            fetch(API + '/bot/trades?limit=20').then(r => r.ok ? r.json() : null)
        ]);
        updateBotStatusUI(status, pnl, debug, trades);
    } catch (e) { console.warn('[Bot]', e.message); }
}

function toggleStratBtn(btn) {
    btn.classList.toggle('active');
}

function getSelectedStrategies() {
    return Array.from(document.querySelectorAll('.bot-strat-btn.active')).map(b => b.dataset.strat);
}

function updateBotConfigVisibility(running) {
    const panel = document.getElementById('bot-config-panel');
    if (panel) {
        if (running) panel.classList.add('hidden');
        else panel.classList.remove('hidden');
    }
}

function renderStrategyPerformance(pnl) {
    const container = document.getElementById('bot-strat-perf');
    if (!container || !pnl?.byStrategy?.length) { if (container) container.innerHTML = ''; return; }

    const colors = { GRID: 'grid', MOMENTUM: 'momentum', RSI: 'rsi', ALWAYS_BUY: 'always_buy' };
    const barColors = { GRID: 'var(--purple)', MOMENTUM: 'var(--accent)', RSI: 'var(--green)', ALWAYS_BUY: 'var(--muted)' };
    const maxCount = Math.max(...pnl.byStrategy.map(s => s.count), 1);

    container.innerHTML = pnl.byStrategy.map(s => {
        const winCount = pnl.byStrategy.find(x => x.strategy === s.strategy)?.count || 0;
        const pnlColor = s.pnl >= 0 ? 'var(--green)' : 'var(--red)';
        const barPct = Math.round((s.count / maxCount) * 100);
        const nameClass = colors[s.strategy] || '';
        const barColor = barColors[s.strategy] || 'var(--muted)';

        return `<div class="strat-perf-card">
            <div class="strat-perf-name ${nameClass}">${s.strategy}</div>
            <div class="strat-perf-row"><span class="label">Trades</span><span class="value">${s.count}</span></div>
            <div class="strat-perf-row"><span class="label">P&L</span><span class="value" style="color:${pnlColor}">${s.pnl >= 0 ? '+' : ''}$${s.pnl.toFixed(4)}</span></div>
            <div class="strat-perf-bar"><div class="strat-perf-bar-fill" style="width:${barPct}%;background:${barColor};"></div></div>
        </div>`;
    }).join('');

    // Also render by-reason breakdown
    if (pnl.byReason?.length) {
        const reasonIcons = { TP: '+', SL: '-', TRAILING_STOP: '~', TIMEOUT: '*', BREAKEVEN: '=' };
        container.innerHTML += `<div class="strat-perf-card" style="grid-column: 1 / -1;">
            <div class="strat-perf-name" style="color:var(--text);">Exit Reasons</div>
            ${pnl.byReason.map(r => {
                const icon = reasonIcons[r.reason] || '?';
                const color = r.pnl >= 0 ? 'var(--green)' : 'var(--red)';
                return `<div class="strat-perf-row">
                    <span class="label">${icon} ${r.reason}</span>
                    <span class="value">${r.count}x <span style="color:${color}">${r.pnl >= 0 ? '+' : ''}$${r.pnl.toFixed(4)}</span></span>
                </div>`;
            }).join('')}
        </div>`;
    }
}

function renderEquityCurve(trades) {
    const wrap = document.getElementById('bot-equity-wrap');
    if (!wrap) return;

    const closed = (trades || []).filter(t => t.status === 'CLOSED' && t.pnlUsd != null)
        .sort((a, b) => new Date(a.closedAt) - new Date(b.closedAt));

    if (closed.length < 2) { wrap.style.display = 'none'; return; }
    wrap.style.display = '';

    let cumPnl = 0;
    const labels = [];
    const data = [];
    closed.forEach(t => {
        cumPnl += t.pnlUsd;
        const d = new Date(t.closedAt);
        labels.push(d.getHours().toString().padStart(2, '0') + ':' + d.getMinutes().toString().padStart(2, '0'));
        data.push(parseFloat(cumPnl.toFixed(4)));
    });

    const canvas = document.getElementById('equity-chart');
    if (!canvas) return;

    if (equityChart) {
        equityChart.data.labels = labels;
        equityChart.data.datasets[0].data = data;
        equityChart.update('none');
    } else {
        const lastVal = data[data.length - 1] || 0;
        const lineColor = lastVal >= 0 ? '#3fb950' : '#f85149';
        const bgColor = lastVal >= 0 ? 'rgba(63,185,80,0.1)' : 'rgba(248,81,73,0.1)';

        equityChart = new Chart(canvas, {
            type: 'line',
            data: {
                labels,
                datasets: [{
                    label: 'Cumulative P&L',
                    data,
                    borderColor: lineColor,
                    backgroundColor: bgColor,
                    borderWidth: 2,
                    pointRadius: 2,
                    pointHoverRadius: 4,
                    tension: 0.3,
                    fill: true,
                }]
            },
            options: {
                responsive: true, maintainAspectRatio: false,
                plugins: {
                    legend: { display: false },
                    tooltip: { callbacks: { label: ctx => `P&L: ${ctx.parsed.y >= 0 ? '+' : ''}$${ctx.parsed.y.toFixed(4)}` } }
                },
                scales: {
                    x: { grid: { display: false }, ticks: { maxTicksLimit: 8, maxRotation: 0, font: { size: 9 } } },
                    y: { grid: { color: 'rgba(255,255,255,0.03)' }, ticks: { callback: v => '$' + v.toFixed(2), font: { size: 9 } } }
                },
                animation: { duration: 300 },
            }
        });
    }
}

async function toggleBot() {
    try {
        if (_botRunning) {
            await fetch(API + '/bot/stop', { method: 'POST' });
        } else {
            const strats = getSelectedStrategies();
            if (strats.length === 0) { alert('Select at least one strategy'); return; }

            const val = id => { const e = document.getElementById(id); return e ? parseFloat(e.value) : 0; };
            const chk = id => { const e = document.getElementById(id); return e ? e.checked : false; };

            await fetch(API + '/bot/start', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    paperMode: true,
                    symbol: activeSymbol,
                    activeStrategies: strats,
                    capitalUsd: val('cfg-capital') || 100,
                    maxOpenTradesPerStrategy: val('cfg-max-slots') || 5,
                    positionPct: (val('cfg-position-pct') || 10) / 100,
                    takeProfitPct: (val('cfg-tp') || 0.3) / 100,
                    stopLossPct: (val('cfg-sl') || 5) / 100,
                    gridStepPct: (val('cfg-grid-step') || 0.5) / 100,
                    cooldownSeconds: val('cfg-cooldown') || 120,
                    useTrailingStop: chk('cfg-trailing'),
                    trailingStopPct: (val('cfg-trailing-pct') || 1.5) / 100,
                    useBreakevenStop: chk('cfg-breakeven'),
                    breakevenTriggerPct: 0.005,
                    useDynamicTpSl: chk('cfg-dynamic-tpsl'),
                    useAiFilter: chk('cfg-ai-filter'),
                    minAiConfidence: 0.50,
                    useAiSizing: chk('cfg-ai-sizing')
                })
            });
        }
        await loadBotStatus();
    } catch (e) { console.error('[Bot] toggleBot:', e); }
}

// ── Price Alerts ──────────────────────────────────────────────────────────────
async function createAlert() {
    const condition = document.getElementById('alert-condition').value;
    const targetPrice = parseFloat(document.getElementById('alert-target').value);
    const note = document.getElementById('alert-note').value || null;

    if (!targetPrice || targetPrice <= 0) {
        alert('Please enter a valid target price');
        return;
    }

    const btn = document.getElementById('alert-create-btn');
    btn.disabled = true;
    btn.textContent = 'Creating...';

    try {
        const resp = await fetch(API + '/alerts', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                symbol: activeSymbol,
                condition,
                targetPrice,
                note
            })
        });

        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);

        document.getElementById('alert-target').value = '';
        document.getElementById('alert-note').value = '';
        await loadAlerts();
    } catch (e) {
        console.error('[Alert] Create failed:', e);
    } finally {
        btn.disabled = false;
        btn.textContent = 'Create Alert';
    }
}

async function deleteAlert(id) {
    try {
        await fetch(API + '/alerts/' + id, { method: 'DELETE' });
        await loadAlerts();
    } catch (e) {
        console.error('[Alert] Delete failed:', e);
    }
}

async function loadAlerts() {
    try {
        const [active, history] = await Promise.all([
            apiFetch('/alerts?symbol=' + activeSymbol),
            apiFetch('/alerts/history?symbol=' + activeSymbol + '&limit=20')
        ]);
        renderActiveAlerts(active);
        renderAlertHistory(history);
    } catch (e) {
        console.warn('[Alert] Load failed:', e.message);
    }
}

function renderActiveAlerts(alerts) {
    const container = document.getElementById('alert-active-list');
    if (!container) return;

    if (!alerts || alerts.length === 0) {
        container.innerHTML = '<span class="text-muted text-sm">No active alerts</span>';
        return;
    }

    container.innerHTML = alerts.map(a => {
        const cls = a.condition.toLowerCase();
        const icon = a.condition === 'ABOVE' ? '&#8593;' : '&#8595;';
        const noteHtml = a.note ? `<div class="alert-item-note">${a.note}</div>` : '';
        const time = new Date(a.createdAt).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
        return `<div class="alert-item ${cls}">
            <div class="alert-item-info">
                <div class="alert-item-condition">${icon} ${a.symbol} ${a.condition} $${Number(a.targetPrice).toLocaleString()}</div>
                ${noteHtml}
                <div class="alert-item-time">Created ${time}</div>
            </div>
            <button class="alert-delete-btn" onclick="deleteAlert(${a.id})">Delete</button>
        </div>`;
    }).join('');
}

function renderAlertHistory(notifications) {
    const container = document.getElementById('alert-history-list');
    if (!container) return;

    if (!notifications || notifications.length === 0) {
        container.innerHTML = '<span class="text-muted text-sm">No triggered alerts yet</span>';
        return;
    }

    container.innerHTML = notifications.map(n => {
        const icon = n.condition === 'ABOVE' ? '&#8593;' : '&#8595;';
        const time = new Date(n.triggeredAt).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
        return `<div class="alert-history-item">
            <div class="alert-item-info">
                <div class="alert-item-condition">${icon} ${n.symbol} ${n.condition} $${Number(n.targetPrice).toLocaleString()}</div>
                <div class="alert-item-time">Triggered at $${Number(n.actualPrice).toLocaleString()} - ${time}</div>
            </div>
        </div>`;
    }).join('');
}

// ── Bootstrap ─────────────────────────────────────────────────────────────────
document.addEventListener('DOMContentLoaded', () => {
    initCharts();

    document.querySelectorAll('.tab-btn').forEach(btn => {
        btn.addEventListener('click', () => switchSymbol(btn.dataset.symbol));
    });

    document.querySelectorAll('.vol-tab').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('.vol-tab').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            activeVolWindow = btn.dataset.window;
            renderVolumeWindow(activeVolWindow);
        });
    });

    connectHub();
    loadMarketStatus();
    loadKlines();
    loadUserStats();
    loadVolumeAnalysis();
    loadWhaleFeed();
    loadAlerts();
});
