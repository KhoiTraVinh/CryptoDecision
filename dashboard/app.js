/* ── CryptoDecision — minimal dashboard ───────────────────────────────────────
   Scope is deliberately narrow: am I making money, what is at risk, where is the
   money flowing, why did the bot act, and can I stop it.

   Price charts, the seven-window volume breakdown and the alerts panel were
   dropped — none of them changed a decision. Buy/sell flow and whale trades
   stayed, because both feed the entry signal the bot actually trades on.      */

const SERVER = (typeof window !== 'undefined' && window.SERVER_URL) ? window.SERVER_URL : '';
const API = SERVER + '/api';
const HUB_URL = SERVER + '/hubs/market';

let activeSymbol = 'SOLUSDT';
let hubConn = null;
let botRunning = false;
let volumeData = null;
let activeWindow = '1h';

// The configuration the bot is actually running, fetched from /bot/config. The
// form used to show static HTML defaults regardless of it, which made the panel
// not merely wrong but destructive — Start posted those defaults back.
let loadedConfig = {};

// Whale trade ids already shown, so a poll that returns the same trade does not
// toast it twice. Bounded because this page is left open for hours.
const seenWhales = new Set();

// Round-trip taker fee, matching PaperOrderEngine. Used for the risk line.
const ROUND_TRIP_FEE = 0.002;

// ── Helpers ───────────────────────────────────────────────────────────────────

const el = id => document.getElementById(id);

function usd(v, decimals = 2) {
    if (v == null || isNaN(v)) return '--';
    const n = Number(v);
    return (n < 0 ? '-$' : '$') + Math.abs(n).toFixed(decimals);
}

/**
 * Compact currency for volume figures: $912,880 becomes $912.9K.
 *
 * Volume spans several orders of magnitude between a quiet minute and a busy
 * day, and the exact digits never matter — the magnitude does.
 */
function usdCompact(v) {
    if (v == null || isNaN(v)) return '--';
    const n = Number(v);
    const abs = Math.abs(n);
    const sign = n < 0 ? '-' : '';
    if (abs >= 1e9) return sign + '$' + (abs / 1e9).toFixed(2) + 'B';
    if (abs >= 1e6) return sign + '$' + (abs / 1e6).toFixed(2) + 'M';
    if (abs >= 1e3) return sign + '$' + (abs / 1e3).toFixed(1) + 'K';
    return sign + '$' + abs.toFixed(0);
}

function signed(v, decimals = 2) {
    if (v == null || isNaN(v)) return '--';
    const n = Number(v);
    return (n >= 0 ? '+' : '') + n.toFixed(decimals);
}

function pctText(v, decimals = 1) {
    if (v == null || isNaN(v)) return '--';
    return (Number(v) * 100).toFixed(decimals) + '%';
}

function toneClass(v) {
    const n = Number(v);
    if (n > 0) return 'up';
    if (n < 0) return 'down';
    return '';
}

function esc(s) {
    return String(s ?? '').replace(/[&<>"']/g, c =>
        ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

async function apiGet(path) {
    const r = await fetch(API + path);
    if (!r.ok) throw new Error('HTTP ' + r.status);
    return r.json();
}

function setLive(state) {
    const dot = el('live-dot');
    if (dot) dot.className = 'live ' + state;   // '', 'on', 'off'
}

/**
 * Write text into an element, flashing it only when the value actually changed.
 *
 * The page repaints on every poll, so animating unconditionally would keep the
 * whole screen twitching and make real movement harder to spot, not easier.
 */
function setText(id, text, direction) {
    const node = el(id);
    if (!node || node.textContent === text) return;

    node.textContent = text;
    if (!direction) return;

    node.classList.remove('flash-up', 'flash-down');
    void node.offsetWidth;   // reflow, so the animation restarts on a repeat change
    node.classList.add(direction > 0 ? 'flash-up' : 'flash-down');
}

// ── P&L ───────────────────────────────────────────────────────────────────────

let lastPnl = null;

function renderPnl(pnl, status) {
    const total = pnl?.totalPnlUsd ?? status?.totalPnlUsd ?? 0;
    const totalEl = el('pnl-total');
    if (totalEl) {
        const text = (total >= 0 ? '+' : '') + usd(total);
        if (totalEl.textContent !== text) {
            totalEl.textContent = text;
            // Flash the direction of the *change*, not the sign of the balance —
            // a loss shrinking is good news and should not read as red.
            if (lastPnl !== null && total !== lastPnl) {
                totalEl.classList.remove('flash-up', 'flash-down');
                void totalEl.offsetWidth;
                totalEl.classList.add(total > lastPnl ? 'flash-up' : 'flash-down');
            }
        }
        totalEl.classList.remove('up', 'down');
        if (toneClass(total)) totalEl.classList.add(toneClass(total));
        lastPnl = total;
    }

    const capital = status?.capitalUsd ?? 0;
    const pctOfCapital = pnl?.totalPnlPct ?? status?.totalPnlPct ?? 0;
    const sub = el('pnl-pct');
    if (sub) {
        sub.textContent = capital > 0
            ? `${signed(pctOfCapital * 100)}% on ${usd(capital, 0)} capital`
            : 'no capital configured';
    }

    // Win rate is only meaningful once trades have actually closed; showing 0%
    // before then reads as "losing" rather than "no data".
    const closed = pnl?.totalTrades ?? 0;
    if (el('stat-winrate')) el('stat-winrate').textContent = closed > 0 ? pctText(pnl.winRate) : '--';
    if (el('stat-trades'))  el('stat-trades').textContent  = closed;
    if (el('stat-open'))    el('stat-open').textContent    = status?.openTradeCount ?? 0;
}

// ── Volume flow ───────────────────────────────────────────────────────────────

function renderVolume() {
    const w = (volumeData?.windows || []).find(x => x.window === activeWindow);

    const buyBar = el('flow-buy');
    const sellBar = el('flow-sell');

    if (!w || w.totalTrades === 0) {
        if (buyBar) buyBar.style.width = '50%';
        if (sellBar) sellBar.style.width = '50%';
        setText('flow-buy-label', '--');
        setText('flow-sell-label', '--');
        setText('flow-ratio', 'no trades in this window');
        setText('vol-net', '--');
        setText('vol-trades', '--');
        setText('vol-whales', '--');
        return;
    }

    const total = w.buyVolumeUsd + w.sellVolumeUsd;
    const buyShare = total > 0 ? (w.buyVolumeUsd / total) : 0.5;

    if (buyBar) buyBar.style.width = (buyShare * 100).toFixed(1) + '%';
    if (sellBar) sellBar.style.width = ((1 - buyShare) * 100).toFixed(1) + '%';

    setText('flow-buy-label', usdCompact(w.buyVolumeUsd) + ' buy');
    setText('flow-sell-label', 'sell ' + usdCompact(w.sellVolumeUsd));
    setText('flow-ratio', (buyShare * 100).toFixed(0) + '% buy-side');

    const net = Number(w.netVolumeUsd);
    setText('vol-net', (net >= 0 ? '+' : '') + usdCompact(net), Math.sign(net));
    const netEl = el('vol-net');
    if (netEl) netEl.classList.toggle('up', net > 0), netEl.classList.toggle('down', net < 0);

    setText('vol-trades', w.totalTrades.toLocaleString());
    setText('vol-whales', `${w.whaleBuyCount}/${w.whaleSellCount}`);
}

async function refreshVolume() {
    try {
        volumeData = await apiGet(`/volume/${activeSymbol}?exchange=ALL`);
        renderVolume();
    } catch (e) {
        console.error('refreshVolume:', e);
    }
}

// ── Whale toasts ──────────────────────────────────────────────────────────────

function showWhaleToast(whale) {
    const host = el('toasts');
    if (!host) return;

    // is_buyer_maker false means the taker bought — an aggressive buy.
    const isBuy = !whale.isBuyerMaker;
    const key = `${whale.exchange}:${whale.tradeTime}:${whale.quoteQty}`;
    if (seenWhales.has(key)) return;
    seenWhales.add(key);
    if (seenWhales.size > 500) seenWhales.clear();

    const node = document.createElement('div');
    node.className = 'toast ' + (isBuy ? 'buy' : 'sell');
    node.innerHTML = `
        <div class="toast-title ${isBuy ? 'up' : 'down'}">
          ${isBuy ? 'Whale buy' : 'Whale sell'} ${esc(usdCompact(whale.quoteQty))}
        </div>
        <div class="toast-meta">${esc(whale.exchange)} @ ${Number(whale.price).toFixed(2)}</div>`;

    host.appendChild(node);

    // Cap the stack; an unattended page should not accumulate a wall of toasts.
    while (host.children.length > 4) host.removeChild(host.firstChild);

    setTimeout(() => {
        node.classList.add('leaving');
        setTimeout(() => node.remove(), 260);
    }, 6000);
}

// ── Equity curve (inline SVG) ─────────────────────────────────────────────────

function renderEquity(trades) {
    const svg = el('equity');
    if (!svg) return;

    // Oldest first, cumulative realised P&L.
    const closed = (trades || [])
        .filter(t => t.status === 'CLOSED' || t.status === 'STOPPED')
        .slice()
        .reverse();

    if (closed.length < 2) {
        svg.innerHTML = '';
        return;
    }

    let running = 0;
    const points = closed.map(t => (running += Number(t.pnlUsd ?? 0)));
    points.unshift(0);   // start the curve at breakeven

    const W = 300, H = 60, PAD = 4;
    const min = Math.min(...points, 0);
    const max = Math.max(...points, 0);
    const span = (max - min) || 1;

    const x = i => (i / (points.length - 1)) * W;
    const y = v => H - PAD - ((v - min) / span) * (H - PAD * 2);

    const line = points.map((v, i) => `${i === 0 ? 'M' : 'L'}${x(i).toFixed(1)},${y(v).toFixed(1)}`).join(' ');
    const area = `${line} L${W},${H} L0,${H} Z`;
    const last = points[points.length - 1];
    const tone = last >= 0 ? 'var(--up)' : 'var(--down)';
    const zeroY = y(0).toFixed(1);

    svg.innerHTML = `
        <line x1="0" y1="${zeroY}" x2="${W}" y2="${zeroY}" stroke="var(--line)" stroke-width="1" stroke-dasharray="3 3" />
        <path d="${area}" fill="${tone}" opacity="0.12" />
        <path d="${line}" fill="none" stroke="${tone}" stroke-width="2"
              stroke-linejoin="round" stroke-linecap="round" vector-effect="non-scaling-stroke" />`;
}

// ── Risk line ─────────────────────────────────────────────────────────────────

/**
 * Mirrors RiskEngine.Expectancy on the server so the operator sees the same
 * arithmetic the bot will enforce, before pressing Start rather than after it
 * refuses.
 */
function renderRiskLine() {
    // From the running configuration, not from form fields — there are none now.
    const tp = loadedConfig.takeProfitPct ?? 0;
    const sl = loadedConfig.stopLossPct   ?? 0;
    if (!tp && !sl) return;
    const line = el('risk-line');
    if (!line) return;

    const netWin = tp - ROUND_TRIP_FEE;
    const netLoss = sl + ROUND_TRIP_FEE;

    if (netWin <= 0) {
        line.className = 'risk-line bad';
        line.textContent =
            `Take profit ${(tp * 100).toFixed(2)}% does not cover the ${(ROUND_TRIP_FEE * 100).toFixed(1)}% ` +
            `round-trip fee — every winning trade still loses money. The bot will refuse to start.`;
        return;
    }

    const breakeven = netLoss / (netWin + netLoss);
    const rr = netWin / netLoss;
    const winsPerLoss = netLoss / netWin;

    line.textContent =
        `Net ${signed(netWin * 100)}% win vs ${signed(-netLoss * 100)}% loss after fees — ` +
        `reward:risk ${rr.toFixed(2)}:1, needs a ${(breakeven * 100).toFixed(1)}% win rate to break even ` +
        `(one loss undoes ${winsPerLoss.toFixed(1)} wins).`;

    line.className = 'risk-line ' + (breakeven >= 0.70 ? 'bad' : breakeven >= 0.60 ? 'warn' : 'ok');
}

// ── Bot status ────────────────────────────────────────────────────────────────

function renderStatus(status) {
    botRunning = !!status?.isRunning;

    const badge = el('bot-badge');
    if (badge) {
        badge.textContent = botRunning ? 'RUNNING' : 'STOPPED';
        badge.className = 'badge ' + (botRunning ? 'on' : '');
    }

    const btn = el('bot-btn');
    if (btn) {
        btn.textContent = botRunning ? 'Stop' : 'Start';
        btn.className = 'btn ' + (botRunning ? 'danger' : '');
    }

    const cfg = el('config');
    if (cfg && botRunning) cfg.open = false;

    const hb = el('bot-heartbeat');
    if (hb) {
        hb.textContent = status?.lastEvalAt
            ? 'last check ' + new Date(status.lastEvalAt).toLocaleTimeString()
            : '';
    }

    renderSizingLine(status);
    renderRefusalLine(status);
    renderVerdict(status);
}

// ── Sizing ────────────────────────────────────────────────────────────────────

// Both numbers, because they answer different questions. The API re-derives what
// sizing asks for through the same PositionSizer the bot calls; the note is what
// the last real order came out as after the venue's lot grid took its cut. Showing
// only the first hides the grid; only the second hides why.
function renderSizingLine(status) {
    const line = el('sizing-line');
    if (!line) return;

    const notional = status?.sizingNotionalUsd;
    if (notional == null) { line.textContent = ''; return; }

    const vol = status.volatility;
    const scalar = status.volatilityScalar;
    const pct = loadedConfig.positionPctOfCapital;
    const cap = status.capitalUsd;

    let text = `Next order ${usd(notional)}`;
    if (pct != null && cap != null) text += ` — ${(pct * 100).toFixed(0)}% of ${usd(cap, 0)}`;

    // Only worth explaining when it actually moved the number.
    if (scalar != null && scalar < 0.999 && vol != null) {
        text += `, cut to ${(scalar * 100).toFixed(0)}% by ${Number(vol).toFixed(1)}% volatility`;
        // The scalar floors at 0.5, and the daily high-low range it reads only ever
        // widens until 00:00 UTC — so at the floor the size cannot recover today.
        if (scalar <= 0.501) text += ' (at the floor — no lower, and it holds until 00:00 UTC)';
    } else if (vol != null) {
        text += ` at ${Number(vol).toFixed(1)}% volatility`;
    }
    text += '.';

    if (status.lastSizingNote) text += ` Last order: ${status.lastSizingNote}.`;

    line.textContent = text;
    line.className = 'sizing-line' + (scalar != null && scalar <= 0.501 ? ' warn' : '');
}

// ── Refusals ──────────────────────────────────────────────────────────────────

function renderRefusalLine(status) {
    const line = el('refusal-line');
    if (!line) return;

    const count = status?.refusalsToday || 0;
    const reason = status?.lastRefusalReason;

    // Nothing refused today and nothing on record — stay out of the way.
    if (!count && !reason) { line.hidden = true; return; }

    const when = status.lastRefusalAt
        ? new Date(status.lastRefusalAt).toLocaleTimeString()
        : null;

    let text = count === 1 ? '1 entry refused today' : `${count} entries refused today`;
    if (!count && reason) text = 'No entries refused today; last refusal';
    if (when) text += ` · ${when}`;
    if (reason) text += ` — ${reason}`;

    line.textContent = text;
    line.hidden = false;
    // Refusals in a run are the shape worth reacting to: one is a short signal on a
    // spot account, twenty in a row is the bot unable to trade at all.
    line.className = 'refusal-line' + (count >= 3 ? ' bad' : count > 0 ? ' warn' : '');
}

// Short relative age, for values that are routinely minutes old and read wrong
// without it — the prediction cycle is 150s while the bot evaluates every 30s.
function ago(iso) {
    if (!iso) return null;
    const secs = Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 1000));
    if (secs < 60) return `${secs}s ago`;
    if (secs < 3600) return `${Math.floor(secs / 60)}m ago`;
    return `${Math.floor(secs / 3600)}h ago`;
}

// ── Open positions ────────────────────────────────────────────────────────────

function renderPositions(debug) {
    const host = el('positions');
    if (!host) return;

    const list = debug?.openPositions || [];
    if (list.length === 0) {
        host.innerHTML = '<p class="muted small">None.</p>';
        return;
    }

    host.innerHTML = list.map(p => {
        // The API pre-formats unrealised P&L as a string like "+$1.2345".
        const raw = String(p.pnl ?? '');
        const tone = raw.startsWith('-') ? 'down' : raw.startsWith('+') ? 'up' : '';
        return `
        <div class="position">
          <div>
            <span class="side ${esc(p.side)}">${esc(p.side)}</span>
            <span class="muted small">@ ${esc(p.entryPrice)} · ${esc(p.age)} · ${esc(p.strategy)}</span>
          </div>
          <span class="${tone}">${esc(p.pnl)}</span>
        </div>`;
    }).join('');
}

// ── Verdict ───────────────────────────────────────────────────────────────────

// Fed from bot status, not from a market-status call. The strategy writes its
// verdict to bot_config every cycle precisely because the log is throttled — once
// per code change and then every 120th repeat — which left the state 33 minutes
// stale during a 2.7% move.
function renderVerdict(status) {
    const code = status?.lastVerdictCode;
    const z    = status?.lastVerdictZ;

    // ACTIONABLE means the scorer proposed an entry; anything else is a named
    // abstain code, and which one it is says where the binding constraint sat.
    const actionable = code === 'ACTIONABLE';
    const tone = actionable ? 'LONG' : (code ? 'NEUTRAL' : 'NEUTRAL');

    const dirEl = el('sig-direction');
    if (dirEl) {
        dirEl.textContent = code || '--';
        dirEl.className = 'direction ' + tone;
    }

    // The z next to its own threshold, because "z = -0.98" only means something
    // against the ±1.00 band it has to clear.
    const conf = el('sig-confidence');
    if (conf) conf.textContent = z != null ? 'z = ' + Number(z).toFixed(2) : '';

    const rat = el('sig-rationale');
    if (rat) rat.textContent = status?.lastVerdictDetail || 'No verdict recorded yet.';

    const model = el('sig-model');
    if (model) {
        const bits = [];
        const agree = status?.lastVerdictAgree;
        const venues = status?.lastVerdictVenues;
        if (agree != null && venues != null) bits.push(agree + ' of ' + venues + ' venues agree');
        const age = ago(status?.lastVerdictAt);
        if (age) bits.push(age);
        model.textContent = bits.join(' · ');
    }

    // A verdict that has stopped advancing while no position is open means cycles
    // are running without reaching the strategy — the price fetch failing is the
    // known way that happens, and it is silent.
    const warn = el('sig-warn');
    if (warn) {
        const at = status?.lastVerdictAt ? new Date(status.lastVerdictAt).getTime() : null;
        const staleMs = at ? Date.now() - at : null;
        const open = status?.openTradeCount || 0;

        if (staleMs != null && staleMs > 300000 && open === 0) {
            warn.textContent =
                'Verdict is ' + Math.round(staleMs / 60000) + ' minutes old with no position open — ' +
                'cycles may be running without reaching the strategy.';
            warn.hidden = false;
        } else if (staleMs != null && staleMs > 300000 && open > 0) {
            warn.textContent =
                'Not advancing because ' + open + ' position(s) are open: at the per-strategy ' +
                'limit the loop stops evaluating entries. Expected.';
            warn.hidden = false;
        } else {
            warn.hidden = true;
        }
    }
}

// ── Trades ────────────────────────────────────────────────────────────────────

function renderTrades(trades) {
    const body = document.querySelector('#trades-table tbody');
    if (!body) return;

    const closed = (trades || []).filter(t => t.status === 'CLOSED' || t.status === 'STOPPED');
    if (closed.length === 0) {
        body.innerHTML = '<tr><td colspan="6" class="muted small">No trades yet.</td></tr>';
        return;
    }

    body.innerHTML = closed.slice(0, 25).map(t => {
        const pnl = Number(t.pnlUsd ?? 0);
        const when = t.closedAt ? new Date(t.closedAt).toLocaleTimeString() : '--';
        return `
        <tr>
          <td class="muted">${esc(when)}</td>
          <td><span class="side ${esc(t.side)}">${esc(t.side)}</span></td>
          <td>${Number(t.entryPrice).toFixed(2)}</td>
          <td>${t.exitPrice != null ? Number(t.exitPrice).toFixed(2) : '--'}</td>
          <td class="${toneClass(pnl)}">${(pnl >= 0 ? '+' : '') + usd(pnl, 4)}</td>
          <td class="muted small">${esc(t.closeReason || '--')}</td>
        </tr>`;
    }).join('');
}

// ── Loading ───────────────────────────────────────────────────────────────────

async function refreshBot() {
    try {
        const [status, pnl, trades] = await Promise.all([
            apiGet('/bot/status'),
            apiGet('/bot/pnl').catch(() => null),
            apiGet('/bot/trades?limit=50').catch(() => []),
        ]);

        renderStatus(status);
        renderPnl(pnl, status);
        renderTrades(trades);
        renderEquity(trades);

        // /bot/debug returns an error object rather than positions when stopped.
        if (status?.isRunning) {
            renderPositions(await apiGet('/bot/debug').catch(() => null));
        } else {
            renderPositions(null);
        }
    } catch (e) {
        console.error('refreshBot:', e);
    }
}

async function refreshSignal() {
    try {
    } catch (e) {
        console.error('refreshSignal:', e);
    }
}

function refreshAll() {
    refreshBot();
    refreshSignal();
    refreshVolume();
}

// ── Bot control ───────────────────────────────────────────────────────────────

async function toggleBot() {
    const btn = el('bot-btn');
    if (btn) { btn.disabled = true; btn.textContent = botRunning ? 'Stopping...' : 'Starting...'; }

    try {
        if (botRunning) {
            await fetch(`${API}/bot/stop`, { method: 'POST' });
        } else {
            const res = await fetch(`${API}/bot/start`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                // Execution mode and venue are carried over from the loaded config,
                // never taken from the page. paperMode was hardcoded true here, so
                // pressing Start on a live bot silently moved it to simulation while
                // the header still read RUNNING. Whether real money is at stake is
                // not a decision a dashboard button should be able to make by
                // accident — it stays an explicit API call.
                body: JSON.stringify({
                    paperMode: loadedConfig.paperMode ?? true,
                    exchange:  loadedConfig.exchange  ?? 'BINANCE',
                    symbol: activeSymbol,
                    // Carried over from the loaded config, never hardcoded. This said
                    // ['MOMENTUM'] for weeks after that strategy was deleted, so pressing
                    // Start would have written a strategy name nothing resolves:
                    // StrategyEvaluator logs "Unknown strategy" and returns no entry, the
                    // heartbeat keeps beating, and the dashboard reads RUNNING forever
                    // while the bot never trades again.
                    activeStrategies: loadedConfig.activeStrategies ?? ['XVENUE_FLOW'],
                    capitalUsd:               loadedConfig.capitalUsd,
                    maxOpenTradesPerStrategy: loadedConfig.maxOpenTradesPerStrategy,
                    positionPct:              loadedConfig.positionPctOfCapital,
                    takeProfitPct:            loadedConfig.takeProfitPct,
                    stopLossPct:              loadedConfig.stopLossPct,
                    trailingStopPct:          loadedConfig.trailingStopPct,
                    cooldownSeconds:          loadedConfig.cooldownSeconds,
                    useTrailingStop:          loadedConfig.useTrailingStop,
                    useBreakevenStop:         loadedConfig.useBreakevenStop,
                    breakevenTriggerPct:      loadedConfig.breakevenTriggerPct,
                    useAiFilter:              loadedConfig.useAiFilter,
                    minAiConfidence:          loadedConfig.minAiConfidence,
                    useAiSizing:              loadedConfig.useAiSizing,
                    useDynamicTpSl:           loadedConfig.useDynamicTpSl,
                    useAiAgent:               loadedConfig.useAiAgent,
                }),
            });
            if (!res.ok) throw new Error('start failed: HTTP ' + res.status);
        }
    } catch (e) {
        console.error('toggleBot:', e);
    } finally {
        if (btn) btn.disabled = false;
        // The worker polls bot_config, so its state lags the request by a cycle.
        setTimeout(refreshBot, 800);
    }
}

// ── SignalR ───────────────────────────────────────────────────────────────────

async function connectHub() {
    hubConn = new signalR.HubConnectionBuilder()
        .withUrl(HUB_URL)
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .configureLogging(signalR.LogLevel.Warning)
        .build();

    hubConn.on('ReceiveBotStatus', data => {
        renderStatus(data.status);
        renderPnl(data.pnl, data.status);
        if (data.trades) { renderTrades(data.trades); renderEquity(data.trades); }
        renderPositions(data.debug);
    });

    hubConn.on('ReceiveMarketStatus', data => {
        if (data.symbol === activeSymbol) { /* feature metrics only; the verdict
           arrives with bot status */ }
    });

    hubConn.on('ReceiveVolumeAnalysis', data => {
        if (data.symbol === activeSymbol) { volumeData = data; renderVolume(); }
    });

    hubConn.on('ReceiveWhaleAlert', data => {
        if (data.symbol === activeSymbol) (data.whales || []).forEach(showWhaleToast);
    });

    const subscribe = () => Promise.all([
        hubConn.invoke('Subscribe', activeSymbol, 'ALL').catch(() => {}),
        hubConn.invoke('SubscribeVolume', activeSymbol, 'ALL').catch(() => {}),
    ]);

    hubConn.onreconnecting(() => setLive('off'));
    hubConn.onreconnected(() => { setLive('on'); subscribe(); });
    hubConn.onclose(() => setLive('off'));

    try {
        await hubConn.start();
        await subscribe();
        setLive('on');
    } catch (err) {
        // Polling below keeps the page correct without the hub, so this is not fatal.
        console.warn('[SignalR] connect failed, falling back to polling:', err);
        setLive('off');
    }
}

// ── Init ──────────────────────────────────────────────────────────────────────

// Pull the running configuration into the form, so what is on screen is what the
// bot is doing. Execution mode and venue are shown but not editable — those are
// deliberate decisions, not form fields, and a dashboard that can flip a live bot
// to simulation with one click is a worse hazard than one that cannot.
async function loadConfig() {
    try {
        const res = await fetch(`${API}/bot/config`);
        if (!res.ok) return;

        loadedConfig = await res.json();

        const c = loadedConfig;
        const pct = v => v == null ? '—' : `${round2(v * 100)}%`;
        const onOff = v => v ? 'on' : 'off';

        const rows = [
            ['Symbol',          c.symbol ?? '—'],
            ['Venue',           c.exchange ?? '—'],
            ['Capital',         c.capitalUsd == null ? '—' : `$${c.capitalUsd}`],
            ['Per position',    `${pct(c.positionPctOfCapital)} → $${round2((c.capitalUsd ?? 0) * (c.positionPctOfCapital ?? 0))}`],
            ['Max positions',   c.maxOpenTradesPerStrategy ?? '—'],
            ['Take profit',     pct(c.takeProfitPct)],
            ['Stop loss',       pct(c.stopLossPct)],
            ['Trailing stop',   c.useTrailingStop ? pct(c.trailingStopPct) : 'off'],
            ['Breakeven stop',  c.useBreakevenStop ? `arms at ${pct(c.breakevenTriggerPct)}` : 'off'],
            ['Max hold',        c.maxHoldMinutes == null ? '—' : `${c.maxHoldMinutes} min`],
            ['Cooldown',        c.cooldownSeconds == null ? '—' : `${c.cooldownSeconds}s`],
            ['Daily loss limit', c.dailyLossLimitPct == null ? '—'
                : `${pct(c.dailyLossLimitPct)} → $${round2((c.capitalUsd ?? 0) * c.dailyLossLimitPct)}`],
            ['Eval interval',   c.evalIntervalSeconds == null ? '—' : `${c.evalIntervalSeconds}s`],
            ['Strategies',      (c.activeStrategies ?? []).join(', ') || '—'],
            ['AI agent',        onOff(c.useAiAgent)],
            ['AI filter',       c.useAiFilter ? `on, min confidence ${pct(c.minAiConfidence)}` : 'off'],
            ['AI sizing',       onOff(c.useAiSizing)],
            ['Dynamic TP/SL',   onOff(c.useDynamicTpSl)],
        ];

        const view = el('config-view');
        if (view)
            view.innerHTML = rows
                .map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`)
                .join('');

        const mode = el('cfg-mode');
        if (mode) {
            const live = loadedConfig.paperMode === false;
            mode.textContent = live
                ? `LIVE — ${loadedConfig.exchange} · real funds`
                : `PAPER — simulated, priced from ${loadedConfig.exchange}`;
            mode.className = live ? 'mode-live' : 'mode-paper';
        }

        if (loadedConfig.symbol) activeSymbol = loadedConfig.symbol;

        renderRiskLine();
    } catch (e) {
        console.error('loadConfig:', e);
    }
}

const round2 = n => Math.round(n * 100) / 100;

document.addEventListener('DOMContentLoaded', () => {
    el('bot-btn')?.addEventListener('click', toggleBot);

    renderRiskLine();
    loadConfig();

    document.querySelectorAll('#symbol-tabs .tab').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('#symbol-tabs .tab').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            activeSymbol = btn.dataset.symbol;
            hubConn?.invoke('Subscribe', activeSymbol, 'ALL').catch(() => {});
            hubConn?.invoke('SubscribeVolume', activeSymbol, 'ALL').catch(() => {});
            refreshAll();
        });
    });

    // Window tabs re-render from the payload already held; no refetch needed.
    document.querySelectorAll('#vol-tabs .tab').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('#vol-tabs .tab').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            activeWindow = btn.dataset.window;
            renderVolume();
        });
    });

    connectHub();
    refreshAll();

    // Backstop for a dropped hub connection.
    setInterval(refreshAll, 15000);
});
