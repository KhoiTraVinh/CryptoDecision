/* ── CryptoDecision Mobile — Logic ────────────────────────────────────────── */

// SERVER_URL is injected by config.js for mobile (e.g. "http://192.168.1.x:8080")
const SERVER = (typeof window !== 'undefined' && window.SERVER_URL) ? window.SERVER_URL : '';
const API = SERVER + '/api';
const HUB_URL = SERVER + '/hubs/market';

// ── State ─────────────────────────────────────────────────────────────────────
let activeSymbol = 'BTCUSDT';
let activeExchange = 'ALL';
let gaugeChart = null;
let klineCombinedChart = null;
let hubConn = null;
let volumeData = null;
let activeVolWindow = '24h';
let _botRunning = false;

// ── Chart Global Options ─────────────────────────────────────────────────────
Chart.defaults.color = 'rgba(255,255,255,0.5)';
Chart.defaults.borderColor = 'rgba(255,255,255,0.06)';
Chart.defaults.font.family = "-apple-system, 'SF Pro Display', system-ui, sans-serif";
Chart.defaults.font.size = 11;

// ── Helpers ────────────────────────────────────────────────────────────────────
function id(name) { return document.getElementById(name); }

function pct(v, decimals = 2) {
    if (v == null) return '—';
    const n = Number(v);
    return (n >= 0 ? '+' : '') + n.toFixed(decimals) + '%';
}

function usd(v) {
    if (v == null) return '—';
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
    const el = id('last-refresh');
    if (el) el.textContent = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

async function apiFetch(path) {
    const r = await fetch(API + path);
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    return r.json();
}

async function triggerAlert(title, body, isWhale = true) {
    // 1. OS Vibration API
    if (navigator.vibrate) navigator.vibrate([200, 100, 200]);

    // 2. Beep Sound (Web Audio API)
    try {
        const ctx = new (window.AudioContext || window.webkitAudioContext)();

        if (isWhale) {
            // Sonar ping
            const osc = ctx.createOscillator();
            const gain = ctx.createGain();
            osc.connect(gain);
            gain.connect(ctx.destination);
            osc.type = 'sine';
            osc.frequency.setValueAtTime(600, ctx.currentTime);
            osc.frequency.exponentialRampToValueAtTime(150, ctx.currentTime + 0.6);
            gain.gain.setValueAtTime(0, ctx.currentTime);
            gain.gain.linearRampToValueAtTime(0.3, ctx.currentTime + 0.05);
            gain.gain.exponentialRampToValueAtTime(0.01, ctx.currentTime + 0.6);
            osc.start(ctx.currentTime);
            osc.stop(ctx.currentTime + 0.6);
        } else {
            // Sweet chime (2 notes)
            const playNote = (freq, delay, dur) => {
                const o = ctx.createOscillator();
                const g = ctx.createGain();
                o.connect(g);
                g.connect(ctx.destination);
                o.type = 'sine';
                o.frequency.value = freq;
                g.gain.setValueAtTime(0, ctx.currentTime + delay);
                g.gain.linearRampToValueAtTime(0.2, ctx.currentTime + delay + 0.02);
                g.gain.exponentialRampToValueAtTime(0.01, ctx.currentTime + delay + dur);
                o.start(ctx.currentTime + delay);
                o.stop(ctx.currentTime + delay + dur);
            };
            playNote(987.77, 0, 0.2);     // B5
            playNote(1318.51, 0.1, 0.4);  // E6
        }
    } catch (e) { }

    // 3. Capacitor Native Built-in Notification Plugin
    if (window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.LocalNotifications) {
        try {
            const notif = window.Capacitor.Plugins.LocalNotifications;
            let perm = await notif.checkPermissions();
            if (perm.display !== 'granted') {
                perm = await notif.requestPermissions();
            }
            if (perm.display === 'granted') {
                await notif.schedule({
                    notifications: [{
                        title: title,
                        body: body,
                        id: Math.floor(Math.random() * 1000000),
                        schedule: { at: new Date(Date.now() + 100) }
                    }]
                });
            }
        } catch (e) {
            console.warn('Capacitor Notification Error:', e);
        }
    }
}

// ── UI Updates ─────────────────────────────────────────────────────────────

function updateMomentumUI(d) {
    const score = d.score ?? 0;
    const sigEl = id('mom-signal');
    if (sigEl) {
        sigEl.textContent = d.signal ?? '—';
        sigEl.className = 'momentum-signal ' + signalBadgeClass(d.signal);
    }

    const gaugeValEl = id('gauge-val');
    if (gaugeValEl) gaugeValEl.textContent = (score > 0 ? '+' : '') + Number(score).toFixed(2);

    const scoreEl = id('mom-score');
    if (scoreEl) scoreEl.textContent = (score > 0 ? '+' : '') + Number(score).toFixed(2);

    if (id('mom-total')) id('mom-total').textContent = d.totalTrades?.toLocaleString() ?? '—';
    if (id('mom-buy')) id('mom-buy').textContent = d.buyCount?.toLocaleString() ?? '—';
    if (id('mom-sell')) id('mom-sell').textContent = d.sellCount?.toLocaleString() ?? '—';
    if (id('mom-whale')) id('mom-whale').textContent = (d.whaleBuyCount ?? '—') + ' / ' + (d.whaleSellCount ?? '—');
    if (id('mom-vol')) id('mom-vol').textContent = usd(d.volumeUsd);

    if (gaugeChart) {
        let fillPct = Math.max(0, Math.min(100, ((score + 3) / 6) * 100));
        let colorBg;
        if (d.signal?.includes('BUY')) colorBg = 'rgba(63,185,80,0.8)';
        else if (d.signal?.includes('SELL')) colorBg = 'rgba(248,81,73,0.8)';
        else colorBg = 'rgba(210,153,34,0.8)';

        gaugeChart.data.datasets[0].data = [fillPct, 100 - fillPct];
        gaugeChart.data.datasets[0].backgroundColor[0] = colorBg;
        gaugeChart.update();
    }
    setRefreshTime();
}

function renderVolumeWindow(winKey) {
    if (!volumeData) return;
    const w = (volumeData.windows || []).find(x => x.window === winKey);
    if (!w) return;

    if (id('vol-trades')) id('vol-trades').textContent = w.totalTrades.toLocaleString();
    if (id('vol-net')) {
        id('vol-net').textContent = usd(w.netVolumeUsd);
        id('vol-net').className = 'value ' + colorClass(w.netVolumeUsd);
    }

    const totalVol = (w.buyVolumeUsd + w.sellVolumeUsd) || 1;
    const buyPct = (w.buyVolumeUsd / totalVol) * 100;
    const sellPct = (w.sellVolumeUsd / totalVol) * 100;

    if (id('vol-buy-bar')) id('vol-buy-bar').style.width = buyPct + '%';
    if (id('vol-sell-bar')) id('vol-sell-bar').style.width = sellPct + '%';
    if (id('vol-buy-pct')) id('vol-buy-pct').textContent = buyPct.toFixed(1) + '%';
    if (id('vol-sell-pct')) id('vol-sell-pct').textContent = sellPct.toFixed(1) + '%';
}

async function loadVolumeAnalysis() {
    try {
        volumeData = await apiFetch(`/volume/${activeSymbol}?exchange=${activeExchange}`);
        renderVolumeWindow(activeVolWindow);
    } catch (e) {
        console.error('loadVolumeAnalysis:', e);
    }
}

// ── SignalR Hub ──────────────────────────────────────────────────────────────

function buildConnection() {
    return new signalR.HubConnectionBuilder()
        .withUrl(HUB_URL)
        .withAutomaticReconnect([0, 2000, 5000, 10000, 20000])
        .configureLogging(signalR.LogLevel.Warning)
        .build();
}

async function connectHub() {
    if (hubConn) {
        try { await hubConn.stop(); } catch (_) { }
    }
    hubConn = buildConnection();
    const dot = id('status-dot');

    hubConn.on('ReceiveMomentum', data => {
        if (data.symbol === activeSymbol) updateMomentumUI(data);
    });

    hubConn.on('ReceiveVolumeAnalysis', data => {
        if (data.symbol === activeSymbol) {
            volumeData = data;
            renderVolumeWindow(activeVolWindow);
        }
    });

    hubConn.on('ReceiveWhaleAlert', data => {
        if (data.symbol === activeSymbol && data.whales && data.whales.length > 0) {
            const w0 = data.whales[0];
            const type = !w0.isBuyerMaker ? 'BUY' : 'SELL';
            triggerAlert(`🐋 Whale ${type} Alert`, `${usd(w0.quoteQty)} at ${w0.exchange}`, true);

            data.whales.forEach(w => {
                showWhaleToast(w);
                addWhaleToFeed(w);
            });
        }
    });

    hubConn.on('ReceiveMarketStatus', data => {
        if (data.symbol === activeSymbol) updateMarketStatusUI(data);
    });

    hubConn.on('ReceiveKlines', data => {
        if (data.symbol === activeSymbol) updateKlinesUI(data.charts);
    });

    let _lastBotPnl = null;
    hubConn.on('ReceiveBotStatus', data => {
        updateBotStatusUI(data.status, data.pnl, data.debug, data.trades);

        // Notify if strong shift in PNL or new trades are observed
        if (data.status && data.status.isRunning && _lastBotPnl !== null && data.pnl) {
            if (data.status.totalTrades > _lastBotPnl.totalTrades) {
                triggerAlert('🤖 Bot Trade Executed', `New trade on ${activeSymbol}`, false);
            }
        }
        if (data.pnl) _lastBotPnl = data.pnl;
    });

    hubConn.on('ReceiveAlertTriggered', data => {
        console.log('[Alert] Triggered:', data);
        triggerAlert(
            `Price Alert: ${data.symbol} ${data.condition}`,
            `Target $${Number(data.targetPrice).toLocaleString()} hit at $${Number(data.actualPrice).toLocaleString()}${data.note ? ' — ' + data.note : ''}`,
            false
        );
        mobileLoadAlerts();
    });

    hubConn.onreconnecting(() => {
        if (dot) { dot.className = 'status-dot reconnecting'; }
    });
    hubConn.onreconnected(() => {
        if (dot) { dot.className = 'status-dot live'; }
        hubConn.invoke('Subscribe', activeSymbol, activeExchange).catch(console.error);
        hubConn.invoke('SubscribeVolume', activeSymbol, activeExchange).catch(console.error);
    });
    hubConn.onclose(() => {
        if (dot) { dot.className = 'status-dot offline'; }
    });

    try {
        await hubConn.start();
        if (dot) { dot.className = 'status-dot live'; }
        await hubConn.invoke('Subscribe', activeSymbol, activeExchange);
        await hubConn.invoke('SubscribeVolume', activeSymbol, activeExchange);
    } catch (err) {
        console.warn('[SignalR] Connect failed, polling fallback:', err);
        if (dot) { dot.className = 'status-dot offline'; }
        setInterval(() => loadMomentumHttp(), 5000);
        loadMomentumHttp();
    }
}

// ── HTTP Fallback ────────────────────────────────────────────────────────────
async function loadMomentumHttp() {
    try {
        const d = await apiFetch(`/momentum/${activeSymbol}?exchange=${activeExchange}`);
        updateMomentumUI(d);
    } catch (e) { console.error('loadMomentumHttp:', e); }
}

// ── Toasts & Feeds ──────────────────────────────────────────────────────────

function showWhaleToast(whale) {
    let container = id('toast-container');
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
    setTimeout(() => {
        toast.classList.add('fade-out');
        setTimeout(() => toast.remove(), 400);
    }, 4000);
}

function createWhaleItemHTML(whale) {
    const isBuy = !whale.isBuyerMaker;
    const typeClass = isBuy ? 'buy' : 'sell';
    const qtyText = usd(whale.quoteQty);
    const priceText = `$${Number(whale.price).toLocaleString()}`;
    const timeText = new Date(whale.tradeTime).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

    const div = document.createElement('div');
    div.className = `whale-feed-item ${typeClass}`;
    div.innerHTML = `
        <div class="whale-feed-left">
            <span class="whale-feed-qty">${qtyText}</span>
            <span class="whale-feed-price">${priceText}</span>
        </div>
        <div class="whale-feed-right">
            <span class="whale-feed-time">${isBuy ? 'BUY' : 'SELL'} ${whale.exchange}</span>
            <span class="whale-feed-time">${timeText}</span>
        </div>
    `;
    return div;
}

function addWhaleToFeed(whale, append = false) {
    const containers = [id('whale-feed-home'), id('whale-feed-container')];
    containers.forEach(c => {
        if (!c) return;
        const ph = c.querySelector('.users-placeholder');
        if (ph) ph.remove();

        const div = createWhaleItemHTML(whale);
        if (append) c.appendChild(div);
        else {
            c.insertBefore(div, c.firstChild);
            if (c.children.length > 30) c.lastChild.remove();
        }
    });
}

async function loadWhaleFeed() {
    try {
        const d = await apiFetch(`/whales/${activeSymbol}?limit=30&exchange=${activeExchange}`);
        const containers = [id('whale-feed-home'), id('whale-feed-container')];
        containers.forEach(c => {
            if (!c) return;
            c.innerHTML = '';
            if (!d || d.length === 0) {
                c.innerHTML = '<span class="users-placeholder">Quiet in the deep...</span>';
                return;
            }
            d.forEach(w => {
                c.appendChild(createWhaleItemHTML(w));
            });
        });

        // Update whale summary
        if (d && d.length > 0) {
            let buyVol = 0, sellVol = 0, buyCount = 0, sellCount = 0;
            d.forEach(w => {
                const qty = Number(w.quoteQty) || 0;
                if (!w.isBuyerMaker) { buyVol += qty; buyCount++; }
                else { sellVol += qty; sellCount++; }
            });
            if (id('whale-total-count')) id('whale-total-count').textContent = d.length;
            if (id('whale-buy-vol')) id('whale-buy-vol').textContent = usd(buyVol);
            if (id('whale-sell-vol')) id('whale-sell-vol').textContent = usd(sellVol);
            if (id('whale-net-flow')) {
                const net = buyVol - sellVol;
                id('whale-net-flow').textContent = usd(net);
                id('whale-net-flow').className = 'whale-summary-val ' + colorClass(net);
            }
        }
    } catch (e) {
        console.error('loadWhaleFeed:', e);
    }
}

// ── Market Status & AI Prediction ───────────────────────────────────────────

function updateMarketStatusUI(d) {
    try {
        const dir = d.predictedDirection ?? 'NEUTRAL';
        const conf = d.confidence != null ? (d.confidence * 100).toFixed(1) + '%' : '—';

        const dirEl = id('sig-direction');
        const badgeEl = id('sig-badge');

        if (dirEl) {
            dirEl.className = 'signal-direction ' + dir;
            dirEl.textContent = dir === 'UP' ? '↑ UP' : dir === 'DOWN' ? '↓ DOWN' : '— NEUTRAL';
        }

        if (id('sig-confidence')) id('sig-confidence').textContent = conf;
        if (badgeEl) {
            badgeEl.className = 'signal-badge ' + signalBadgeClass(d.momentumSignal ?? dir);
            badgeEl.textContent = d.momentumSignal ?? dir;
        }
        if (id('sig-rationale')) id('sig-rationale').textContent = d.rationale || 'Awaiting AI rationale...';
        if (id('sig-model')) id('sig-model').textContent = d.modelVersion || '';

        if (id('today-return')) {
            id('today-return').textContent = pct(d.return24h);
            id('today-return').className = 'value ' + colorClass(d.return24h);
        }
        if (id('today-vol')) id('today-vol').textContent = pct(d.volatility);
        if (id('today-volchg')) {
            id('today-volchg').textContent = pct(d.volumeChange);
            id('today-volchg').className = 'value ' + colorClass(d.volumeChange);
        }
        if (id('today-vwap')) id('today-vwap').textContent = d.vwap != null ? '$' + Number(d.vwap).toLocaleString() : '—';
        if (id('today-whale')) id('today-whale').textContent = d.whaleCount ?? '—';
    } catch (e) { console.error('updateMarketStatusUI:', e); }
}

async function loadMarketStatus() {
    try {
        const d = await apiFetch(`/market-status/${activeSymbol}?exchange=${activeExchange}`);
        updateMarketStatusUI(d);
    } catch (e) { console.error('loadMarketStatus:', e); }
}

// ── Klines ──────────────────────────────────────────────────────────────────

function updateKlinesUI(chartsData) {
    try {
        if (!klineCombinedChart) return;
        let lastCloses = [];
        for (let i = 0; i < chartsData.length; i++) {
            const chart = chartsData[i];
            const data = chart.data;
            const closes = data.map(k => Number(k.close));
            if (closes.length > 0) lastCloses.push(`${chart.exchange}: $${closes.at(-1).toLocaleString()}`);

            if (i === 0) {
                klineCombinedChart.data.labels = data.map(k => {
                    const t = new Date(k.openTime);
                    return t.getHours() + ':' + t.getMinutes().toString().padStart(2, '0');
                });
            }
            if (klineCombinedChart.data.datasets.length > i) {
                klineCombinedChart.data.datasets[i].data = closes;
            }
        }
        klineCombinedChart.update('none');

        const titleEl = id('kline-combined-title');
        if (titleEl && lastCloses.length > 0) {
            titleEl.textContent = `Price (60m)  |  ${lastCloses.join('  |  ')}`;
        }
    } catch (e) { console.error('updateKlinesUI:', e); }
}

async function loadKlines() {
    try {
        const exchanges = ['BINANCE', 'BYBIT', 'OKX'];
        const chartsData = [];
        for (const ex of exchanges) {
            const data = await apiFetch(`/klines/${activeSymbol}?limit=60&exchange=${ex}`);
            chartsData.push({ exchange: ex, data: data });
        }
        updateKlinesUI(chartsData);
    } catch (e) { console.error('loadKlines:', e); }
}

// ── Bot Dashboard ────────────────────────────────────────────────────────────

function updateBotStatusUI(status, pnl, debug, trades) {
    if (!status) return;
    _botRunning = status.isRunning;

    const badge = id('bot-status-badge');
    const btn = id('bot-toggle-btn');
    if (badge) {
        let text = status.isRunning ? (status.paperMode ? 'PAPER ACTIVE' : 'LIVE') : 'STOPPED';
        if (status.isRunning && debug?.options?.activeStrategies) text += ' | ' + debug.options.activeStrategies.join('+');
        badge.textContent = text;
        badge.style.background = status.isRunning ? 'var(--green-bg)' : 'var(--surface2)';
        badge.style.color = status.isRunning ? 'var(--green)' : 'var(--muted)';
    }
    if (btn) {
        btn.textContent = status.isRunning ? 'Stop Bot' : 'Start Bot';
        btn.className = 'btn-bot ' + (status.isRunning ? 'btn-bot-stop' : 'btn-bot-start');
    }

    if (id('bot-pnl-usd')) {
        const val = status.totalPnlUsd ?? 0;
        id('bot-pnl-usd').textContent = (val >= 0 ? '+' : '') + '$' + val.toFixed(4);
        id('bot-pnl-usd').style.color = val >= 0 ? 'var(--green)' : 'var(--red)';
    }
    if (id('bot-winrate') && pnl) {
        id('bot-winrate').textContent = pnl.totalTrades > 0 ? (pnl.winRate * 100).toFixed(1) + '%' : '—';
    }
    if (id('bot-trades-count')) id('bot-trades-count').textContent = status.totalTrades ?? 0;
    if (id('bot-open-trade')) id('bot-open-trade').textContent = status.openTradeCount ?? 0;

    // Update symbol header
    if (id('bot-header-symbol') && debug?.options?.symbol) {
        id('bot-header-symbol').textContent = debug.options.symbol;
    }

    const condBox = id('bot-conditions-list');
    const entryHint = id('bot-will-enter');

    if (condBox) {
        if (!status.isRunning) {
            condBox.innerHTML = '<span style="color:var(--muted);font-size:12px;">Start bot to see live signals...</span>';
            if (entryHint) entryHint.textContent = '';
        } else if (debug?.conditions) {
            condBox.innerHTML = debug.conditions.map(c => {
                const icon = c.willEnter ? '🚀' : '⏳';
                const color = c.willEnter ? 'var(--green)' : 'var(--muted)';
                const sColor = c.strategy === 'GRID' ? 'var(--purple)' : 'var(--accent)';
                return `<div style="display:flex;align-items:center;justify-content:space-between;font-size:12px;padding:3px 0;">
                    <span style="color:${sColor};font-weight:700;letter-spacing:0.5px;">${c.strategy}</span>
                    <span style="color:var(--text2);">Slots ${c.slots}</span>
                    <span style="color:${color};font-weight:700;font-family:monospace;min-width:40px;text-align:right;">${icon} ${c.cooldown}</span>
                </div>`;
            }).join('');

            if (entryHint) {
                const price = debug.currentPrice ? ' · $' + Number(debug.currentPrice).toLocaleString() : '';
                entryHint.innerHTML = `<span style="color:var(--muted); font-size:11px;">Current Market Price${price}</span>`;
            }

            // Open Positions
            if (debug.openPositions && debug.openPositions.length > 0) {
                const listHtml = debug.openPositions.map(p => {
                    const isLong = p.side !== 'SHORT';
                    const sideClass = isLong ? 'position-long' : 'position-short';
                    const sideText = isLong ? 'LONG' : 'SHORT';
                    const pnlVal = parseFloat(p.pnl?.replace(/[^0-9.-]/g, '')) || 0;
                    const pnlColor = pnlVal >= 0 ? 'var(--green)' : 'var(--red)';
                    const sColor = p.strategy === 'GRID' ? 'var(--purple)' : 'var(--accent)';

                    const fillWidth = Math.min(Math.abs(pnlVal) * 5, 50);
                    const leftPos = pnlVal >= 0 ? 50 : 50 - fillWidth;

                    return `
                    <div class="position-card ${sideClass}">
                        <div class="position-header">
                            <div>
                                <span class="position-badge">${sideText}</span>
                                <span style="color:${sColor}; font-weight:700; margin-left:6px; font-size:10px;">${p.strategy}</span>
                                <span style="color:var(--muted);"> #${p.id}</span>
                            </div>
                            <div class="pnl-active" style="color:${pnlColor};">${p.pnl}</div>
                        </div>
                        <div style="display:flex; justify-content:space-between; font-size:10px; color:var(--muted);">
                            <span>Entry $${Number(p.entryPrice).toLocaleString()}</span>
                            <span>Age ${p.age}</span>
                        </div>
                        <div class="position-progress-bg">
                            <div class="position-progress-fill ${pnlVal >= 0 ? 'profit' : 'loss'}" style="width:${fillWidth}%; left:${leftPos}%;"></div>
                        </div>
                    </div>`;
                }).join('');

                condBox.innerHTML += `<div style="margin-top:14px;">
                    <div style="font-size:10px; color:var(--muted); text-transform:uppercase; margin-bottom:8px; letter-spacing:0.8px; font-weight:800;">Active Positions</div>
                    ${listHtml}
                </div>`;
            }
        } else if (debug?.error) {
            condBox.innerHTML = `<span style="color:var(--muted);font-size:12px;">${debug.error}</span>`;
        }
    }

    if (trades) renderBotTrades(trades);
    mUpdateBotConfigVisibility(status.isRunning);
    mRenderStratPerf(pnl);
}

function mUpdateBotConfigVisibility(running) {
    const panel = id('m-bot-config-panel');
    if (panel) {
        if (running) panel.classList.add('hidden');
        else panel.classList.remove('hidden');
    }
}

window.mToggleStrat = function (btn) {
    btn.classList.toggle('active');
};

function mGetSelectedStrategies() {
    return Array.from(document.querySelectorAll('.m-bot-strat-btn.active')).map(b => b.dataset.strat);
}

function mRenderStratPerf(pnl) {
    const container = id('m-strat-perf');
    if (!container || !pnl?.byStrategy?.length) { if (container) container.innerHTML = ''; return; }

    const colors = { GRID: 'var(--purple)', MOMENTUM: 'var(--accent)', RSI: 'var(--green)', ALWAYS_BUY: 'var(--muted)' };
    const maxCount = Math.max(...pnl.byStrategy.map(s => s.count), 1);

    container.innerHTML = pnl.byStrategy.map(s => {
        const pnlColor = s.pnl >= 0 ? 'var(--green)' : 'var(--red)';
        const barPct = Math.round((s.count / maxCount) * 100);
        const nameColor = colors[s.strategy] || 'var(--muted)';

        return `<div class="m-strat-perf-card">
            <div class="m-strat-perf-name" style="color:${nameColor}">${s.strategy}</div>
            <div class="m-strat-perf-row"><span class="lbl">Trades</span><span class="val">${s.count}</span></div>
            <div class="m-strat-perf-row"><span class="lbl">P&L</span><span class="val" style="color:${pnlColor}">${s.pnl >= 0 ? '+' : ''}$${s.pnl.toFixed(2)}</span></div>
            <div class="m-strat-perf-bar"><div class="m-strat-perf-bar-fill" style="width:${barPct}%;background:${nameColor};"></div></div>
        </div>`;
    }).join('');
}

function renderBotTrades(trades) {
    const tbody = id('bot-trade-rows');
    if (!tbody) return;
    if (!trades || trades.length === 0) {
        tbody.innerHTML = '<tr><td colspan="5" style="text-align:center;color:var(--muted);padding:16px;">Waiting for first trade...</td></tr>';
        return;
    }
    tbody.innerHTML = trades.map(t => {
        const pnl = t.pnlUsd ?? 0;
        const color = pnl >= 0 ? 'var(--green)' : 'var(--red)';
        const time = new Date(t.openedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        const exitStr = t.exitPrice ? '$' + Number(t.exitPrice).toLocaleString() : '—';
        const pnlStr = t.pnlUsd != null ? (pnl >= 0 ? '+' : '') + '$' + pnl.toFixed(4) : '—';
        const reasonBadge = t.closeReason
            ? `<span style="background:rgba(88,166,255,0.12);color:var(--accent);padding:2px 6px;border-radius:4px;font-size:9px;">${t.closeReason}</span>`
            : `<span style="color:var(--muted);">OPEN</span>`;
        const sColor = t.strategy === 'GRID' ? 'var(--purple)' : 'var(--accent)';
        const sideColor = t.side === 'SHORT' ? 'var(--red)' : 'var(--green)';

        return `<tr style="border-bottom:1px solid var(--border);">
            <td style="padding:10px 4px;">
                <div style="font-weight:700;">${time}</div>
                <div style="font-size:9px;color:${sColor};font-weight:600;">${t.strategy} <span style="color:${sideColor};">[${t.side || 'LONG'}]</span></div>
            </td>
            <td style="text-align:right;padding:6px 4px;">$${Number(t.entryPrice).toLocaleString()}</td>
            <td style="text-align:right;padding:6px 4px;">${exitStr}</td>
            <td style="text-align:right;padding:6px 4px;color:${color};font-weight:800;">${pnlStr}</td>
            <td style="text-align:center;padding:6px 4px;">${reasonBadge}</td>
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

async function toggleBot() {
    try {
        if (_botRunning) {
            await fetch(API + '/bot/stop', { method: 'POST' });
        } else {
            const strats = mGetSelectedStrategies();
            if (strats.length === 0) { triggerAlert('Error', 'Select at least one strategy', false); return; }

            const val = n => { const e = id(n); return e ? parseFloat(e.value) : 0; };
            const chk = n => { const e = id(n); return e ? e.checked : false; };

            await fetch(API + '/bot/start', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    paperMode: true,
                    symbol: activeSymbol,
                    activeStrategies: strats,
                    capitalUsd: val('m-cfg-capital') || 100,
                    maxOpenTradesPerStrategy: val('m-cfg-max-slots') || 5,
                    positionPct: (val('m-cfg-position-pct') || 10) / 100,
                    takeProfitPct: (val('m-cfg-tp') || 0.3) / 100,
                    stopLossPct: (val('m-cfg-sl') || 5) / 100,
                    gridStepPct: (val('m-cfg-grid-step') || 0.5) / 100,
                    cooldownSeconds: val('m-cfg-cooldown') || 120,
                    useTrailingStop: chk('m-cfg-trailing'),
                    trailingStopPct: (val('m-cfg-trailing-pct') || 1.5) / 100,
                    useBreakevenStop: chk('m-cfg-breakeven'),
                    breakevenTriggerPct: 0.005,
                    useDynamicTpSl: chk('m-cfg-dynamic')
                })
            });
        }
        await loadBotStatus();
    } catch (e) { console.error('[Bot] Toggle failed:', e); }
}

// ── Chart Initialization ──────────────────────────────────────────────────────

function initCharts() {
    gaugeChart = new Chart(id('gauge-chart'), {
        type: 'doughnut',
        data: {
            labels: ['Score', 'Remaining'],
            datasets: [{
                data: [50, 50],
                backgroundColor: ['rgba(210,153,34,0.8)', 'rgba(255,255,255,0.05)'],
                borderColor: ['transparent', 'transparent'],
                borderWidth: 0,
                cutout: '84%',
            }]
        },
        options: {
            responsive: true, maintainAspectRatio: false,
            circumference: 180, rotation: -90,
            plugins: { legend: { display: false }, tooltip: { enabled: false } },
            animation: { duration: 800, easing: 'easeOutQuart' },
        }
    });

    klineCombinedChart = new Chart(id('kline-combined-chart'), {
        type: 'line',
        data: {
            labels: [],
            datasets: [
                { label: 'Binance', data: [], borderColor: '#f5b041', borderWidth: 2, pointRadius: 0, tension: 0.4 },
                { label: 'Bybit', data: [], borderColor: '#58d68d', borderWidth: 2, pointRadius: 0, tension: 0.4 },
                { label: 'OKX', data: [], borderColor: '#e74c3c', borderWidth: 2, pointRadius: 0, tension: 0.4 }
            ]
        },
        options: {
            responsive: true, maintainAspectRatio: false,
            interaction: { intersect: false, mode: 'index' },
            plugins: { legend: { display: true, labels: { boxWidth: 10, padding: 10 } } },
            scales: {
                x: { grid: { display: false }, ticks: { maxTicksLimit: 6 } },
                y: { position: 'right', ticks: { callback: v => '$' + v.toLocaleString() }, grid: { color: 'rgba(255,255,255,0.03)' } }
            },
            animation: { duration: 400 },
        }
    });
}

// ── Symbol Switch ─────────────────────────────────────────────────────────────

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
    mobileLoadAlerts();
}

// ── Price Alerts ─────────────────────────────────────────────────────────────

let _selectedAlertCond = 'ABOVE';

window.selectAlertCond = function (btn) {
    document.querySelectorAll('.alert-cond-btn').forEach(b => b.classList.remove('active'));
    btn.classList.add('active');
    _selectedAlertCond = btn.dataset.cond;
};

window.mobileCreateAlert = async function () {
    const target = parseFloat(id('m-alert-target')?.value);
    const note = id('m-alert-note')?.value?.trim() || '';
    if (!target || target <= 0) {
        triggerAlert('Invalid Price', 'Please enter a valid target price.', false);
        return;
    }

    const btn = id('m-alert-create-btn');
    if (btn) { btn.disabled = true; btn.textContent = 'Creating...'; }

    try {
        const res = await fetch(API + '/alerts', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                symbol: activeSymbol,
                condition: _selectedAlertCond,
                targetPrice: target,
                note: note
            })
        });
        if (!res.ok) throw new Error('Failed');

        // Clear form
        if (id('m-alert-target')) id('m-alert-target').value = '';
        if (id('m-alert-note')) id('m-alert-note').value = '';

        triggerAlert('Alert Created', `${_selectedAlertCond} $${target.toLocaleString()} on ${activeSymbol}`, false);
        mobileLoadAlerts();
    } catch (err) {
        console.error('[Alerts] Create failed:', err);
        triggerAlert('Error', 'Could not create alert. Check connection.', false);
    } finally {
        if (btn) { btn.disabled = false; btn.textContent = 'Create Alert'; }
    }
};

async function mobileLoadAlerts() {
    try {
        const [alertsRes, historyRes] = await Promise.all([
            fetch(API + '/alerts?symbol=' + activeSymbol),
            fetch(API + '/alerts/history?symbol=' + activeSymbol)
        ]);

        if (alertsRes.ok) {
            const alerts = await alertsRes.json();
            renderMobileActiveAlerts(alerts);
        }
        if (historyRes.ok) {
            const history = await historyRes.json();
            renderMobileAlertHistory(history);
        }
    } catch (err) {
        console.error('[Alerts] Load failed:', err);
    }
}

function renderMobileActiveAlerts(alerts) {
    const container = id('m-alert-active-list');
    const badge = id('m-alert-count');
    if (!container) return;

    if (badge) badge.textContent = alerts.length;

    if (alerts.length === 0) {
        container.innerHTML = `
            <div class="alert-empty-state">
                <div class="alert-empty-icon">&#128276;</div>
                <span>No active alerts</span>
                <span class="alert-empty-hint">Create one above to get notified when price crosses your target</span>
            </div>`;
        return;
    }

    container.innerHTML = alerts.map(a => `
        <div class="alert-card-item">
            <div class="alert-card-left">
                <span class="alert-card-cond ${a.condition === 'ABOVE' ? 'above' : 'below'}">
                    ${a.condition === 'ABOVE' ? '&#8593;' : '&#8595;'} ${a.condition}
                </span>
                <span class="alert-card-price">$${Number(a.targetPrice).toLocaleString(undefined, { minimumFractionDigits: 2 })}</span>
                ${a.note ? `<span class="alert-card-note">${a.note}</span>` : ''}
            </div>
            <button class="alert-delete-btn" onclick="mobileDeleteAlert(${a.id})">&#10005;</button>
        </div>
    `).join('');
}

function renderMobileAlertHistory(notifications) {
    const container = id('m-alert-history-list');
    if (!container) return;

    if (notifications.length === 0) {
        container.innerHTML = `
            <div class="alert-empty-state">
                <div class="alert-empty-icon">&#9203;</div>
                <span>No triggered alerts yet</span>
            </div>`;
        return;
    }

    container.innerHTML = notifications.map(n => {
        const time = new Date(n.triggeredAt).toLocaleString();
        return `
            <div class="alert-history-item">
                <div class="alert-history-top">
                    <span class="alert-history-cond ${n.condition === 'ABOVE' ? 'above' : 'below'}">
                        ${n.condition === 'ABOVE' ? '&#8593;' : '&#8595;'} ${n.condition}
                    </span>
                    <span class="alert-history-time">${time}</span>
                </div>
                <div class="alert-history-bottom">
                    Target $${Number(n.targetPrice).toLocaleString(undefined, { minimumFractionDigits: 2 })}
                    &rarr; Hit at $${Number(n.actualPrice).toLocaleString(undefined, { minimumFractionDigits: 2 })}
                </div>
            </div>`;
    }).join('');
}

window.mobileDeleteAlert = async function (alertId) {
    try {
        const res = await fetch(API + '/alerts/' + alertId, { method: 'DELETE' });
        if (res.ok) {
            mobileLoadAlerts();
        }
    } catch (err) {
        console.error('[Alerts] Delete failed:', err);
    }
};

// ── Tab Management ────────────────────────────────────────────────────────────

window.switchTab = function (tabId, element) {
    document.querySelectorAll('.tab-content').forEach(tab => {
        tab.classList.remove('active');
    });
    document.querySelectorAll('.nav-item').forEach(nav => nav.classList.remove('active'));

    const target = id('tab-' + tabId);
    if (target) {
        setTimeout(() => target.classList.add('active'), 20);
    }
    if (element) element.classList.add('active');

    if (tabId === 'bot') loadBotStatus();
    if (tabId === 'whale') loadWhaleFeed();
    if (tabId === 'alerts') mobileLoadAlerts();
    if (tabId === 'settings') updateSettingsUI();
};

function updateSettingsUI() {
    if (id('settings-server-url')) id('settings-server-url').textContent = SERVER || '(local)';
    if (id('settings-symbol')) id('settings-symbol').textContent = activeSymbol;
    if (id('settings-username')) {
        const name = localStorage.getItem('cryptodec_user');
        id('settings-username').textContent = name || 'Not set';
    }
    if (id('settings-conn-status')) {
        const connected = hubConn?.state === signalR.HubConnectionState.Connected;
        id('settings-conn-status').textContent = connected ? 'Connected' : 'Disconnected';
        id('settings-conn-status').className = 'settings-val ' + (connected ? 'green' : 'red');
    }
}

// ── Bootstrap ───────────────────────────────────────────────────────────────

document.addEventListener('DOMContentLoaded', () => {
    initCharts();

    // Event Listeners
    document.querySelectorAll('.tab-btn').forEach(btn => {
        btn.addEventListener('click', () => switchSymbol(btn.dataset.symbol));
    });

    const volSelect = id('vol-period-select');
    if (volSelect) {
        volSelect.addEventListener('change', (e) => {
            activeVolWindow = e.target.value;
            renderVolumeWindow(activeVolWindow);
        });
    }

    // Load initial data
    connectHub();
    loadMarketStatus();
    loadKlines();
    loadVolumeAnalysis();
    loadWhaleFeed();
    loadBotStatus();
    mobileLoadAlerts();

    // Settings: Reset identity
    const logoutBtn = id('settings-logout');
    if (logoutBtn) {
        logoutBtn.addEventListener('click', () => {
            localStorage.removeItem('cryptodec_user');
            const welcome = id('welcome-overlay');
            if (welcome) welcome.classList.add('show');
            switchTab('home', document.querySelector('.nav-item'));
        });
    }

    // Welcome Overlay logic
    const welcome = id('welcome-overlay');
    const nameInput = id('welcome-name');
    const welcomeBtn = id('welcome-btn');

    const userKey = 'cryptodec_user';
    if (!localStorage.getItem(userKey)) {
        if (welcome) welcome.classList.add('show');
    }

    if (welcomeBtn) {
        welcomeBtn.addEventListener('click', () => {
            const name = nameInput.value.trim();
            if (name.length < 2) {
                id('welcome-error').textContent = 'Name too short.';
                return;
            }
            localStorage.setItem(userKey, name);
            welcome.classList.add('leaving');
            setTimeout(() => welcome.classList.remove('show'), 400);

            // Log user session to backend
            fetch(API + '/users/login', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name: name })
            }).catch(() => { });
        });
    }
});
