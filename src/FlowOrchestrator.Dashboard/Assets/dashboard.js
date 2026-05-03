const BASE = '{{BASE_PATH}}/api';
let currentPage = 'overview';
let allFlows = [];
let allRuns = [];
let runsTotal = 0;
let runsPage = 1;
const runsPageSizeStorageKey = 'flow-dashboard:runs-page-size';
const runsPageSizeAllowed = [10, 20, 50, 100];
let runsPageSize = (() => {
  // Persist Runs page size across reloads. URL param wins (set in restoreRunsFiltersFromRoute);
  // localStorage is the user's last sticky choice; default 20.
  try {
    const stored = parseInt(localStorage.getItem(runsPageSizeStorageKey) || '', 10);
    if (runsPageSizeAllowed.includes(stored)) return stored;
  } catch {}
  return 20;
})();
let runsSearchDebounceTimer = null;
let selectedRunId = null;
let selectedStepKey = null;
let currentRunView = 'timeline'; // 'timeline' | 'dag' | 'gantt' | 'events'
let currentRun = null; // cache of last loaded run for tab re-renders
let currentRunManifest = null; // cached resolved manifest for DAG/Gantt
let eventsDrawerOpen = false;
let runsView = 'list'; // 'list' | 'detail'
let selectedFlowDetail = null;
const autoRefreshStorageEnabledKey = 'flow-dashboard:auto-refresh-enabled';
// Fixed fallback cadence — the user-tunable interval dropdown was removed when SSE
// became primary. Polling now only runs as a fallback for stalled streams, so a
// single sensible default beats giving users a knob with no everyday effect.
const fallbackPollingSeconds = 5;
let autoRefreshEnabled = true;
let autoRefreshTimer = null;

// ── Realtime (SSE) state ─────────────────────────────────────────────
// Strategy: Server-Sent Events is the primary live channel. Polling
// (setInterval(refresh, fallbackPollingSeconds * 1000)) is the FALLBACK,
// activated only when the SSE stream goes silent for >20s or fails to
// reconnect 3+ times. On the next successful event, polling stops.
const sseStaleThresholdMs = 20000;
const sseDegradedFailureThreshold = 3;
let eventStream = null;
let sseLastEventAt = 0;
let sseFailedReconnects = 0;
let sseDegraded = false;
let sseWatchdogTimer = null;

function $(id) { return document.getElementById(id); }
async function fetchJSON(url, opts) {
  const r = await fetch(url, opts || undefined);
  if (!r.ok) {
    const err = new Error(r.statusText || ('HTTP ' + r.status));
    err.status = r.status;
    throw err;
  }
  return r.json();
}

// AbortController-aware page-scoped fetches. Reset on navigation / run select.
let pageController = null;
let runDetailController = null;

function newPageController() {
  if (pageController) { try { pageController.abort(); } catch {} }
  pageController = new AbortController();
  return pageController;
}

function newRunDetailController() {
  if (runDetailController) { try { runDetailController.abort(); } catch {} }
  runDetailController = new AbortController();
  return runDetailController;
}

// Robust: when controller.abort(reason) was called with a string reason, fetch
// rejects with that bare string — so e.name / e.code are undefined and the
// only signal is that the controller is now in `aborted` state. Check both
// shapes plus the literal "AbortError" DOMException.
function isAbortError(e) {
  if (e == null) return false;
  if (typeof e === 'string') return /abort|navigation|run-changed|unload/i.test(e);
  if (e.name === 'AbortError' || e.code === 20) return true;
  if ((pageController && pageController.signal && pageController.signal.aborted) ||
      (runDetailController && runDetailController.signal && runDetailController.signal.aborted)) {
    // Most fetches in flight at this moment ARE the aborted ones.
    return true;
  }
  if (typeof e.message === 'string' && /abort|aborted/i.test(e.message)) return true;
  return false;
}

// withBusy — disable + aria-busy a button while an async op runs. Idempotent.
async function withBusy(btn, fn) {
  if (!btn) return fn();
  if (btn.dataset.busy === '1') return; // de-dupe double-clicks
  const prevAriaBusy = btn.getAttribute('aria-busy');
  btn.dataset.busy = '1';
  btn.setAttribute('aria-busy', 'true');
  btn.disabled = true;
  try {
    return await fn();
  } finally {
    btn.dataset.busy = '';
    if (prevAriaBusy === null) btn.removeAttribute('aria-busy'); else btn.setAttribute('aria-busy', prevAriaBusy);
    btn.disabled = false;
  }
}
function fmt(iso) { if (!iso) return '\u2014'; return new Date(iso).toLocaleTimeString('en-US',{hour12:false}); }
function fmtDate(iso) { if (!iso) return '\u2014'; const d=new Date(iso); return d.toLocaleDateString('en-US',{month:'short',day:'numeric'})+' '+d.toLocaleTimeString('en-US',{hour12:false}); }
function duration(s,e) { if(!s) return ''; const ms=(e?new Date(e):new Date())-new Date(s); return ms<1000?ms+'ms':(ms/1000).toFixed(1)+'s'; }
function fmtDurationMs(ms) { if (ms == null) return '—'; if (ms < 1000) return Math.round(ms) + 'ms'; if (ms < 60000) return (ms/1000).toFixed(1) + 's'; return Math.round(ms/60000) + 'm'; }
function esc(s) { if(!s) return ''; const d=document.createElement('div'); d.textContent=s; return d.innerHTML; }
function prettyJson(raw) {
  if (raw === null || raw === undefined) return '\u2014';
  if (typeof raw === 'object') return esc(JSON.stringify(raw, null, 2));

  const text = String(raw);
  if (!text.trim()) return '\u2014';

  try {
    return esc(JSON.stringify(JSON.parse(text), null, 2));
  } catch {
    return esc(text);
  }
}
function renderDetailPanel(title, raw, openByDefault, variant) {
  if (raw === null || raw === undefined) return '';
  if (typeof raw === 'string' && !raw.trim()) return '';
  if (typeof raw === 'object' && !Array.isArray(raw) && Object.keys(raw).length === 0) return '';

  const cssClass = variant ? ' step-detail-' + variant : '';
  return '<div class="step-detail' + cssClass + '">'
    + '<div class="step-detail-title"><span>' + esc(title) + '</span><button class="copy-panel-btn" onclick="copyPanelContent(this)" title="Copy to clipboard"><svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="2" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg></button></div>'
    + '<div class="step-detail-body">' + prettyJson(raw) + '</div>'
    + '</div>';
}
function renderRunTriggerPanels(run) {
  const dataPanel = renderDetailPanel('Trigger Data', run.triggerDataJson, false, null);
  const headersPanel = renderDetailPanel('Trigger Headers', run.triggerHeaders, false, null);
  if (!dataPanel && !headersPanel) return '';

  return '<div style="margin-bottom:10px">' + dataPanel + headersPanel + '</div>';
}
function getStepAttemptCount(step) {
  if (typeof step.attemptCount === 'number' && Number.isFinite(step.attemptCount) && step.attemptCount > 0) {
    return step.attemptCount;
  }

  if (Array.isArray(step.attempts) && step.attempts.length > 0) {
    return step.attempts.length;
  }

  return 1;
}
function renderStepDebugPanels(step) {
  // Blocked steps never executed — no input/output/error panels to show
  if (step.status === 'Skipped') {
    const tracePanel = renderWhyWhenSkippedPanel(step);
    if (tracePanel) return tracePanel;
    return '<div style="margin-top:5px;font-size:11px;color:var(--skip-text);font-style:italic">'
      + 'Not executed \u2014 retry the failed step above to resume this flow.'
      + '</div>';
  }
  const attemptCount = getStepAttemptCount(step);
  const attemptsPanel = attemptCount > 1
    ? renderDetailPanel('Step Attempts', step.attempts, false, null)
    : '';
  const inputPanel = renderDetailPanel('Step Input', step.inputJson, false, null);
  const outputPanel = renderDetailPanel('Step Output', step.outputJson, false, null);
  const errorPanel = renderDetailPanel('Step Error', step.errorMessage, step.status === 'Failed', 'error');
  return attemptsPanel + inputPanel + outputPanel + errorPanel;
}
function renderWhyWhenSkippedPanel(step) {
  const traceJson = step && step.evaluationTraceJson;
  if (!traceJson) return '';
  let trace;
  try { trace = JSON.parse(traceJson); } catch { return ''; }
  if (!trace || typeof trace.expression !== 'string') return '';
  const expression = esc(trace.expression);
  const resolved = esc(trace.resolved || '');
  const result = trace.result === true ? 'true' : 'false';
  return '<div class="step-detail">'
    + '<div class="step-detail-title"><span>Why skipped</span></div>'
    + '<div class="step-detail-body" style="font-family:var(--font-mono);font-size:12px;line-height:1.6">'
    +   '<div style="color:var(--text-muted)">Expression</div>'
    +   '<div style="margin-bottom:6px">' + expression + '</div>'
    +   '<div style="color:var(--text-muted)">Resolved</div>'
    +   '<div>' + resolved + ' \u2192 <strong style="color:var(--error-text)">' + result + '</strong></div>'
    + '</div>'
    + '</div>';
}
function stepStatusLabel(s) { return s === 'Skipped' ? 'Blocked' : s; }
function statusBadge(s) { const label = s === 'Skipped' ? 'Blocked' : s; return '<span class="badge badge-'+s.toLowerCase()+'">'+label+'</span>'; }

function readAutoRefreshEnabled() {
  try {
    const stored = localStorage.getItem(autoRefreshStorageEnabledKey);
    if (stored === null) return true;
    return stored !== 'false';
  } catch {
    return true;
  }
}

function persistAutoRefreshSettings() {
  try {
    localStorage.setItem(autoRefreshStorageEnabledKey, autoRefreshEnabled ? 'true' : 'false');
  } catch {}
}

function updateAutoRefreshUI() {
  const enabledEl = $('auto-refresh-enabled');
  const statusEl = $('refresh-status');
  const pulseEl = $('refresh-pulse');

  if (enabledEl) enabledEl.checked = autoRefreshEnabled;
  if (statusEl) {
    if (!autoRefreshEnabled) {
      statusEl.textContent = 'Paused';
    } else if (sseDegraded) {
      statusEl.textContent = 'Polling';
    } else {
      statusEl.textContent = 'Live';
    }
  }
  if (pulseEl) {
    pulseEl.style.animationPlayState = autoRefreshEnabled ? 'running' : 'paused';
    pulseEl.style.opacity = autoRefreshEnabled ? '1' : '.35';
  }
}

// Starts the FALLBACK polling timer. Called by the SSE watchdog when the realtime
// stream is unavailable. On its own, this no longer runs — SSE is the primary path.
function startFallbackPolling() {
  if (autoRefreshTimer) return; // already running
  if (!autoRefreshEnabled) return;
  autoRefreshTimer = setInterval(refresh, fallbackPollingSeconds * 1000);
}

function stopFallbackPolling() {
  if (autoRefreshTimer) {
    clearInterval(autoRefreshTimer);
    autoRefreshTimer = null;
  }
}

// SSE event stream — owns the EventSource lifecycle, fan-out to page handlers,
// and the watchdog that flips us to polling fallback when the stream stalls.
const FlowEventStream = (function () {
  let es = null;

  function url() {
    return BASE + '/events/stream';
  }

  function dispatch(type, evt) {
    sseLastEventAt = Date.now();
    if (sseDegraded) exitDegraded();

    // Targeted refetch instead of full page re-render. The same state will
    // also be picked up by the next polling tick if we ever drop back, so
    // these handlers can fail silently without leaving the UI stale.
    try {
      if (type === 'run.started' || type === 'run.completed') {
        if (currentPage === 'overview') refresh({ silent: true });
        if (currentPage === 'runs' && runsView === 'list') refresh({ silent: true });
        if (currentPage === 'runs' && runsView === 'detail' && evt && evt.runId === selectedRunId) {
          selectRun(selectedRunId, true, selectedStepKey, currentRunView);
        }
      } else if (type === 'step.completed' || type === 'step.retried') {
        if (currentPage === 'runs' && runsView === 'detail' && evt && evt.runId === selectedRunId) {
          selectRun(selectedRunId, true, selectedStepKey, currentRunView);
        }
      }
    } catch (e) { console.error('sse.dispatch', e); }
  }

  function attachListeners() {
    if (!es) return;
    // Heartbeat comments fire onmessage with empty data — track liveness either way.
    es.onmessage = () => { sseLastEventAt = Date.now(); };
    es.onopen = () => { sseFailedReconnects = 0; sseLastEventAt = Date.now(); };
    es.onerror = () => {
      sseFailedReconnects++;
      // EventSource auto-reconnects; we only act if the stream stays stale long enough.
      if (sseFailedReconnects >= sseDegradedFailureThreshold) enterDegraded();
    };
    ['run.started', 'run.completed', 'step.completed', 'step.retried'].forEach((type) => {
      es.addEventListener(type, (ev) => {
        let payload = null;
        try { payload = JSON.parse(ev.data); } catch {}
        dispatch(type, payload);
      });
    });
  }

  function enterDegraded() {
    if (sseDegraded) return;
    sseDegraded = true;
    startFallbackPolling();
    updateAutoRefreshUI();
  }

  function exitDegraded() {
    if (!sseDegraded) return;
    sseDegraded = false;
    stopFallbackPolling();
    updateAutoRefreshUI();
  }

  function ensureWatchdog() {
    if (sseWatchdogTimer) return;
    sseWatchdogTimer = setInterval(() => {
      if (!autoRefreshEnabled) return;
      if (!es) return;
      const stale = sseLastEventAt > 0 && (Date.now() - sseLastEventAt) > sseStaleThresholdMs;
      if (stale) enterDegraded();
    }, 5000);
  }

  function stopWatchdog() {
    if (sseWatchdogTimer) {
      clearInterval(sseWatchdogTimer);
      sseWatchdogTimer = null;
    }
  }

  function connect() {
    if (es) return;
    if (!autoRefreshEnabled) return;
    if (typeof EventSource === 'undefined') {
      // Old browser — fall straight back to polling.
      enterDegraded();
      return;
    }
    try {
      es = new EventSource(url());
      sseLastEventAt = Date.now();
      attachListeners();
      ensureWatchdog();
    } catch (e) {
      console.error('sse.connect', e);
      enterDegraded();
    }
  }

  function close() {
    if (es) { try { es.close(); } catch {} es = null; }
    stopWatchdog();
    exitDegraded();
  }

  return { connect, close };
})();

function initAutoRefreshSettings() {
  autoRefreshEnabled = readAutoRefreshEnabled();
  updateAutoRefreshUI();
  if (autoRefreshEnabled) FlowEventStream.connect();
}

function onAutoRefreshEnabledChange() {
  const enabledEl = $('auto-refresh-enabled');
  autoRefreshEnabled = !!(enabledEl && enabledEl.checked);
  persistAutoRefreshSettings();
  updateAutoRefreshUI();
  if (autoRefreshEnabled) {
    FlowEventStream.connect();
    refresh();
  } else {
    FlowEventStream.close();
    stopFallbackPolling();
  }
}

function _navigate(page) {
  currentPage = page;
  document.querySelectorAll('.page').forEach(p => p.classList.remove('active'));
  document.querySelectorAll('.nav-item').forEach(n => {
    n.classList.remove('active');
    n.removeAttribute('aria-current');
  });
  $('page-'+page).classList.add('active');
  const navEl = document.querySelector('.nav-item[data-page="'+page+'"]');
  if (navEl) {
    navEl.classList.add('active');
    navEl.setAttribute('aria-current', 'page');
  }
  closeSidebar();
  refresh();
}

// ── Theme + density toggles ──────────────────────────────────────────
function _currentTheme() {
  return document.documentElement.getAttribute('data-theme') === 'dark' ? 'dark' : 'light';
}

function applyTheme(theme) {
  if (theme === 'dark') document.documentElement.setAttribute('data-theme', 'dark');
  else document.documentElement.removeAttribute('data-theme');
  try { localStorage.setItem('fo-theme', theme); } catch {}
  // Update toggle visual state.
  const btn = $('theme-toggle');
  if (btn) {
    btn.setAttribute('aria-pressed', theme === 'dark' ? 'true' : 'false');
    btn.setAttribute('title', theme === 'dark' ? 'Switch to light mode' : 'Switch to dark mode');
    btn.setAttribute('aria-label', theme === 'dark' ? 'Switch to light mode' : 'Switch to dark mode');
  }
}

function toggleTheme() {
  const next = _currentTheme() === 'dark' ? 'light' : 'dark';
  applyTheme(next);
  showToast(next === 'dark' ? 'Dark mode' : 'Light mode', 'info');
}

function setDensity(density) {
  const allowed = ['compact','cozy','comfortable'];
  if (!allowed.includes(density)) density = 'cozy';
  if (density === 'cozy') document.documentElement.removeAttribute('data-density');
  else document.documentElement.setAttribute('data-density', density);
  try { localStorage.setItem('fo-density', density); } catch {}
  document.querySelectorAll('.density-group .icon-btn').forEach(b => {
    b.setAttribute('aria-pressed', b.dataset.density === density ? 'true' : 'false');
  });
}

function _initThemeAndDensityUI() {
  applyTheme(_currentTheme());
  const density = document.documentElement.getAttribute('data-density') || 'cozy';
  setDensity(density);
}

// ── Command palette (Cmd / Ctrl + K) ─────────────────────────────────
let _cmdkOpen = false;
let _cmdkResults = [];
let _cmdkActiveIdx = 0;
let _cmdkReturnFocus = null;

function openCmdK() {
  if (_cmdkOpen) return;
  _cmdkOpen = true;
  _cmdkReturnFocus = document.activeElement;
  $('cmdk-backdrop').classList.add('open');
  const dlg = $('cmdk'); dlg.classList.add('open'); dlg.removeAttribute('hidden');
  const input = $('cmdk-input'); input.value = ''; input.focus();
  renderCmdK('');
}

function closeCmdK() {
  if (!_cmdkOpen) return;
  _cmdkOpen = false;
  $('cmdk-backdrop').classList.remove('open');
  const dlg = $('cmdk'); dlg.classList.remove('open'); dlg.setAttribute('hidden', '');
  if (_cmdkReturnFocus && typeof _cmdkReturnFocus.focus === 'function') {
    try { _cmdkReturnFocus.focus(); } catch {}
  }
  _cmdkReturnFocus = null;
}

function _cmdkScore(haystack, needle) {
  if (!needle) return 1;
  const h = (haystack || '').toLowerCase();
  const n = needle.toLowerCase();
  if (h === n) return 1000;
  if (h.startsWith(n)) return 500;
  const i = h.indexOf(n);
  if (i >= 0) return 200 - i;
  // Subsequence match.
  let idx = 0;
  for (const c of n) {
    idx = h.indexOf(c, idx);
    if (idx === -1) return 0;
    idx++;
  }
  return 50;
}

function _cmdkBuildItems() {
  const items = [];
  for (const f of (allFlows || [])) {
    items.push({ kind: 'flow', label: f.name, sub: 'Flow · v' + f.version, action: () => { closeCmdK(); navigate('flows'); openFlowDetail(f.id); } });
  }
  for (const r of (allRuns || [])) {
    items.push({ kind: 'run', label: r.id.slice(0, 8) + '… ' + (r.flowName || ''), sub: 'Run · ' + r.status, action: () => { closeCmdK(); navigate('runs'); selectRun(r.id); } });
  }
  items.push({ kind: 'action', label: 'Toggle dark mode', sub: 'Theme', action: () => { closeCmdK(); toggleTheme(); } });
  items.push({ kind: 'action', label: 'Density: Cozy', sub: 'Display', action: () => { closeCmdK(); setDensity('cozy'); } });
  items.push({ kind: 'action', label: 'Density: Compact', sub: 'Display', action: () => { closeCmdK(); setDensity('compact'); } });
  items.push({ kind: 'action', label: 'Density: Comfortable', sub: 'Display', action: () => { closeCmdK(); setDensity('comfortable'); } });
  items.push({ kind: 'action', label: 'Go to Overview', sub: 'Navigation', action: () => { closeCmdK(); navigate('overview'); } });
  items.push({ kind: 'action', label: 'Go to Flows', sub: 'Navigation', action: () => { closeCmdK(); navigate('flows'); } });
  items.push({ kind: 'action', label: 'Go to Runs', sub: 'Navigation', action: () => { closeCmdK(); navigate('runs'); } });
  items.push({ kind: 'action', label: 'Go to Scheduled', sub: 'Navigation', action: () => { closeCmdK(); navigate('scheduled'); } });
  return items;
}

function renderCmdK(query) {
  const list = $('cmdk-list');
  const items = _cmdkBuildItems();
  const q = (query || '').trim();
  const scored = items
    .map(it => ({ it, score: _cmdkScore(it.label, q) + _cmdkScore(it.sub, q) * 0.3 }))
    .filter(x => x.score > 0)
    .sort((a, b) => b.score - a.score)
    .slice(0, 30);
  _cmdkResults = scored.map(x => x.it);
  _cmdkActiveIdx = 0;
  if (_cmdkResults.length === 0) {
    list.innerHTML = '<div class="cmdk-empty">No matches</div>';
    return;
  }
  list.innerHTML = _cmdkResults.map((it, i) => {
    const icon = it.kind === 'flow' ? '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><polyline points="22 12 18 12 15 21 9 3 6 12 2 12"/></svg>'
              : it.kind === 'run'  ? '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/></svg>'
              :                       '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><polyline points="9 11 12 14 22 4"/><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11"/></svg>';
    return '<button type="button" role="option" id="cmdk-item-' + i + '" data-idx="' + i + '" class="cmdk-item' + (i === 0 ? ' cmdk-item-active' : '') + '" onclick="_cmdkActivate(' + i + ')">'
      + '<span class="cmdk-item-icon">' + icon + '</span>'
      + '<span class="cmdk-item-label"><span>' + esc(it.label) + '</span><span class="cmdk-item-sub">' + esc(it.sub) + '</span></span>'
      + '</button>';
  }).join('');
  const inp = $('cmdk-input'); if (inp) inp.setAttribute('aria-activedescendant', 'cmdk-item-0');
}

function _cmdkSetActive(idx) {
  if (idx < 0) idx = _cmdkResults.length - 1;
  if (idx >= _cmdkResults.length) idx = 0;
  _cmdkActiveIdx = idx;
  document.querySelectorAll('#cmdk-list .cmdk-item').forEach((el, i) => {
    el.classList.toggle('cmdk-item-active', i === idx);
  });
  const active = $('cmdk-item-' + idx);
  if (active) active.scrollIntoView({ block: 'nearest' });
  const inp = $('cmdk-input'); if (inp) inp.setAttribute('aria-activedescendant', 'cmdk-item-' + idx);
}

function _cmdkActivate(idx) {
  const it = _cmdkResults[idx];
  if (it && typeof it.action === 'function') it.action();
}

document.addEventListener('keydown', (e) => {
  // Open palette on Cmd/Ctrl+K from anywhere.
  if ((e.metaKey || e.ctrlKey) && (e.key === 'k' || e.key === 'K')) {
    e.preventDefault();
    if (_cmdkOpen) closeCmdK(); else openCmdK();
    return;
  }
  if (!_cmdkOpen) return;
  if (e.key === 'Escape') { e.preventDefault(); closeCmdK(); return; }
  if (e.key === 'ArrowDown') { e.preventDefault(); _cmdkSetActive(_cmdkActiveIdx + 1); return; }
  if (e.key === 'ArrowUp')   { e.preventDefault(); _cmdkSetActive(_cmdkActiveIdx - 1); return; }
  if (e.key === 'Enter')     { e.preventDefault(); _cmdkActivate(_cmdkActiveIdx); return; }
});

// Bind input listener once on DOM ready (script runs after body so element exists).
(function bindCmdK(){
  const input = document.getElementById('cmdk-input');
  if (input) input.addEventListener('input', () => renderCmdK(input.value));
})();

// ── Mobile sidebar (≤991px) ──────────────────────────────────────────
function toggleSidebar() {
  const sidebar = $('sidebar');
  const backdrop = $('sidebar-backdrop');
  if (!sidebar || !backdrop) return;
  const willOpen = !sidebar.classList.contains('open');
  sidebar.classList.toggle('open', willOpen);
  backdrop.classList.toggle('open', willOpen);
  document.querySelectorAll('.hamburger').forEach(h => h.setAttribute('aria-expanded', willOpen ? 'true' : 'false'));
  if (willOpen) {
    // Focus first nav item for keyboard users.
    const firstNav = sidebar.querySelector('.nav-item');
    if (firstNav) firstNav.focus();
  }
}

function closeSidebar() {
  const sidebar = $('sidebar');
  const backdrop = $('sidebar-backdrop');
  if (sidebar) sidebar.classList.remove('open');
  if (backdrop) backdrop.classList.remove('open');
  document.querySelectorAll('.hamburger').forEach(h => h.setAttribute('aria-expanded', 'false'));
}

// Escape closes sidebar drawer (mobile)
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    const sidebar = $('sidebar');
    if (sidebar && sidebar.classList.contains('open')) {
      closeSidebar();
      const hamburger = document.querySelector('.page.active .hamburger');
      if (hamburger) hamburger.focus();
    }
  }
});

function navigate(page) {
  if (page !== 'runs') { selectedRunId = null; selectedStepKey = null; }
  if (page === 'runs') {
    selectedRunId = null;
    selectedStepKey = null;
    showRunsListView();
    history.replaceState(null, '', '#/runs');
  } else {
    history.replaceState(null, '', '#/'+page);
  }
  _navigate(page);
}

// Overview
async function loadOverview(opts) {
  const silent = !!(opts && opts.silent);
  const flowsTbl = $('ov-flows-table');
  const runsTbl = $('ov-runs-table');
  // First-paint skeleton \u2014 only when caller is not auto-refreshing in-place.
  if (!silent && flowsTbl && !flowsTbl.innerHTML.trim()) {
    flowsTbl.innerHTML = skeletonRows(4, 4);
    runsTbl.innerHTML = skeletonRows(5, 4);
  }
  const signal = newPageController().signal;
  try {
    const [stats, flows, runs, schedules, hourly, daily] = await Promise.all([
      fetchJSON(BASE+'/runs/stats', { signal }),
      fetchJSON(BASE+'/flows', { signal }),
      fetchJSON(BASE+'/runs?take=10', { signal }),
      fetchJSON(BASE+'/schedules', { signal }).catch(() => []),
      // Server-side aggregation: 24×1h buckets with status counts + p50/p95 per bucket.
      fetchJSON(BASE+'/runs/timeseries?bucket=hour&hours=24', { signal }).catch(() => ({ buckets: [] })),
      // Server-side aggregation: 30×1d buckets for the calendar heatmap.
      fetchJSON(BASE+'/runs/timeseries?bucket=day&days=30', { signal }).catch(() => ({ buckets: [] }))
    ]);
    $('ov-flows').textContent = flows.length;
    $('ov-active').textContent = stats.activeRuns ?? 0;
    $('ov-done').textContent = stats.completedToday ?? 0;
    $('ov-fail').textContent = stats.failedToday ?? 0;
    $('ov-scheduled').textContent = schedules.length;

    renderActivityHeatmap(bucketsFromTimeseries(hourly && hourly.buckets));
    renderActivityCalendar(daily && daily.buckets);

    flowsTbl.innerHTML = flows.length === 0
      ? '<div class="empty-msg">No flows registered yet.</div>'
      : '<table class="recent-table"><thead><tr><th>Name</th><th>Version</th><th>Status</th><th>Steps</th></tr></thead><tbody>'
        + flows.map(f => {
          const m = parseManifest(f.manifestJson);
          return '<tr><td style="font-weight:600">'+esc(f.name)+'</td><td style="font-family:\'JetBrains Mono\',monospace;font-size:11px">'+esc(f.version)+'</td>'
            +'<td>'+(f.isEnabled?'<span class="badge badge-enabled">Enabled</span>':'<span class="badge badge-disabled">Disabled</span>')+'</td>'
            +'<td style="font-family:\'JetBrains Mono\',monospace;font-size:11px">'+(m?Object.keys(m.steps||{}).length:'-')+'</td></tr>';
        }).join('')+'</tbody></table>';

    runsTbl.innerHTML = runs.length === 0
      ? '<div class="empty-msg">No runs recorded yet.</div>'
      : '<table class="recent-table"><thead><tr><th>Run</th><th>Flow</th><th>Status</th><th>Started</th></tr></thead><tbody>'
        + runs.map(r =>
          '<tr style="cursor:pointer" onclick="Router.go(\'runs\');setRunRoute(\''+r.id+'\');applyRoute(Router.parse())">'
          +'<td style="font-family:\'JetBrains Mono\',monospace;font-size:11px;color:var(--accent)">'+r.id.slice(0,8)+'\u2026</td>'
          +'<td>'+esc(r.flowName||'')+'</td><td>'+statusBadge(r.status)+'</td><td style="font-size:12px;color:var(--text-dim)">'+fmtDate(r.startedAt)+'</td></tr>'
        ).join('')+'</tbody></table>';
  } catch(e) {
    if (isAbortError(e)) return;
    console.error('Overview load error', e);
    if (!silent) {
      flowsTbl.innerHTML = '<div class="empty-msg empty-msg--error">Failed to load. <button class="btn-retry" onclick="loadOverview()">Retry</button></div>';
      runsTbl.innerHTML = '';
      showError('Failed to load overview', e.message);
    }
  }
}

// Flows
function parseManifest(json) { try { return json ? JSON.parse(json) : null; } catch { return null; } }
function getCronExpression(manifest) {
  if (!manifest || !manifest.triggers) return null;
  for (const t of Object.values(manifest.triggers)) {
    if (t.type && t.type.toLowerCase() === 'cron' && t.inputs && t.inputs.cronExpression) return t.inputs.cronExpression;
  }
  return null;
}

async function loadFlows(opts) {
  const silent = !!(opts && opts.silent);
  const grid = $('flows-grid');
  if (!silent && grid && !grid.innerHTML.trim()) {
    grid.innerHTML = skeletonCards(4);
  }
  const signal = newPageController().signal;
  try {
    // Fetch flows + per-flow timeseries + last-run snapshot in parallel.
    // The 24h timeseries (server-aggregated) feeds sparkline + health badge per card.
    // The light /runs?take=N call gives us "last run timestamp" without scanning 500 rows client-side.
    const [flows, hourly, lastRunsList] = await Promise.all([
      fetchJSON(BASE+'/flows', { signal }),
      fetchJSON(BASE+'/runs/timeseries?bucket=hour&hours=24', { signal }).catch(() => ({ buckets: [] })),
      fetchJSON(BASE+'/runs?take=200', { signal }).catch(() => [])
    ]);
    allFlows = flows;
    $('flow-count-label').innerHTML = '<span class="num">' + allFlows.length + '</span>flow' + (allFlows.length!==1?'s':'');

    const sel = $('runs-filter-flow');
    const curVal = sel.value;
    sel.innerHTML = '<option value="">All Flows</option>' + allFlows.map(f => '<option value="'+f.id+'"'+(f.id===curVal?' selected':'')+'>'+esc(f.name)+'</option>').join('');

    // The aggregate /timeseries doesn't carry flowId per bucket, so for cards we issue a per-flow
    // timeseries fetch in parallel. Cap at 16 concurrent requests; the dashboard fetches 1 per card.
    const flowSeries = await Promise.all(allFlows.map(f =>
      fetchJSON(BASE+'/runs/timeseries?bucket=hour&hours=24&flowId=' + encodeURIComponent(f.id), { signal })
        .catch(() => ({ buckets: [] }))
    ));
    const seriesByFlow = {};
    allFlows.forEach((f, i) => { seriesByFlow[f.id] = (flowSeries[i] && flowSeries[i].buckets) || []; });

    // Map last run per flow from the lightweight runs list — used for relative-time pill.
    const lastRunsItems = Array.isArray(lastRunsList) ? lastRunsList : (lastRunsList && lastRunsList.items) || [];
    const lastRunByFlow = {};
    for (const r of lastRunsItems) {
      const fid = r.flowId || (allFlows.find(f => f.name === r.flowName) || {}).id;
      if (!fid || lastRunByFlow[fid]) continue; // list is already started-desc, first wins
      lastRunByFlow[fid] = r;
    }

    grid.innerHTML = allFlows.length === 0
      ? '<div class="empty-msg">No flows registered. Use <code>options.AddFlow&lt;T&gt;()</code> to register flows.</div>'
      : allFlows.map(f => {
        const m = parseManifest(f.manifestJson);
        const stepCount = m ? Object.keys(m.steps||{}).length : 0;
        const triggerCount = m ? Object.keys(m.triggers||{}).length : 0;
        const cronExpr = getCronExpression(m);
        const buckets = seriesByFlow[f.id] || [];
        const totals = buckets.map(b => b.total || 0);
        const sumTotal = totals.reduce((a, b) => a + b, 0);
        const sumOk = buckets.reduce((a, b) => a + (b.succeeded || 0), 0);
        const sumFail = buckets.reduce((a, b) => a + (b.failed || 0), 0);
        const sumCancelled = buckets.reduce((a, b) => a + (b.cancelled || 0), 0);
        const sumRunning = buckets.reduce((a, b) => a + (b.running || 0), 0);
        const completed = sumOk + sumFail + sumCancelled;
        const p95s = buckets.map(b => b.p95DurationMs).filter(v => typeof v === 'number');
        const p95 = p95s.length ? Math.max(...p95s) : null;
        // Success rate is meaningful only over completed runs — 30 runs all still running ≠ 0% ok.
        const successRate = completed > 0 ? Math.round((sumOk / completed) * 100) : null;
        const lastRun = lastRunByFlow[f.id];
        const lastStatus = lastRun ? lastRun.status : 'None';
        const lastRel = lastRun ? relativeTime(lastRun.startedAt) : 'no recent runs';

        let healthBadge;
        if (sumTotal === 0) {
          healthBadge = '<span class="health-badge health-idle">Idle</span>';
        } else if (completed === 0) {
          // All in-flight, nothing has finished yet.
          healthBadge = '<span class="health-badge health-warn" title="' + sumRunning + ' run' + (sumRunning!==1?'s':'') + ' in flight, none completed">'
            + sumRunning + ' running</span>';
        } else {
          const healthClass = sumFail === 0 ? 'health-ok' : (successRate >= 90 ? 'health-warn' : 'health-bad');
          healthBadge = '<span class="health-badge ' + healthClass + '" title="Last 24h: ' + sumOk + ' ok, ' + sumFail + ' failed' + (sumRunning ? ', ' + sumRunning + ' running' : '') + '">'
            + successRate + '% ok</span>';
        }
        if (p95 !== null) {
          healthBadge += '<span class="health-stat" title="p95 duration last 24h">p95 ' + fmtDurationMs(p95) + '</span>';
        }
        healthBadge += '<span class="health-stat" title="Total runs last 24h">' + sumTotal + ' runs</span>';
        const sparkSvg = totals.some(v => v > 0)
          ? sparklinePath(totals, 280, 24, { cls: 'flow-card-spark' })
          : '<svg class="flow-card-spark" viewBox="0 0 280 24" aria-hidden="true"><text x="140" y="14" class="spark-empty" text-anchor="middle">no activity</text></svg>';
        return '<div class="flow-card" tabindex="0" role="button" onclick="openFlowDetail(\''+f.id+'\')" onkeydown="if(event.key===\'Enter\'||event.key===\' \'){event.preventDefault();openFlowDetail(\''+f.id+'\')}">'
          +'<div class="flow-card-header"><div class="flow-card-name">'+esc(f.name)+'</div><div class="flow-card-version">v'+esc(f.version)+'</div></div>'
          +'<div class="flow-card-meta"><span>'+stepCount+' step'+(stepCount!==1?'s':'')+'</span><span>'+triggerCount+' trigger'+(triggerCount!==1?'s':'')+'</span>'
          +(cronExpr?'<span class="badge-cron" title="Cron schedule">&#128339; '+esc(cronExpr)+'</span>':'')
          +'</div>'
          +'<div class="flow-health-row">' + healthBadge + '</div>'
          +'<div class="flow-last-run"><span class="flow-last-run-dot ' + esc(lastStatus) + '"></span>' + (lastRun ? (esc(lastStatus) + ' · ' + esc(lastRel)) : 'No recent runs') + '</div>'
          +sparkSvg
          +'<div class="flow-card-footer">'+(f.isEnabled?'<span class="badge badge-enabled">Enabled</span>':'<span class="badge badge-disabled">Disabled</span>')
          +'<span style="font-size:11px;color:var(--text-dim)">'+fmtDate(f.updatedAt)+'</span></div></div>';
      }).join('');
  } catch(e) {
    if (isAbortError(e)) return;
    console.error('Flows load error', e);
    if (!silent) {
      grid.innerHTML = '<div class="empty-msg empty-msg--error">Failed to load flows. <button class="btn-retry" onclick="loadFlows()">Retry</button></div>';
      showError('Failed to load flows', e.message);
    }
  }
}

function relativeTime(iso) {
  if (!iso) return '';
  const ms = Date.now() - new Date(iso).getTime();
  if (ms < 0) return 'in the future';
  const sec = Math.round(ms / 1000);
  if (sec < 60) return sec + 's ago';
  const min = Math.round(sec / 60);
  if (min < 60) return min + 'm ago';
  const hr = Math.round(min / 60);
  if (hr < 48) return hr + 'h ago';
  const day = Math.round(hr / 24);
  return day + 'd ago';
}

let _flowDetailReturnFocus = null;

async function openFlowDetail(id) {
  selectedFlowDetail = allFlows.find(f => f.id === id);
  if (!selectedFlowDetail) return;
  _flowDetailReturnFocus = document.activeElement;
  $('flow-list-panel').classList.add('hide');
  const panel = $('flow-detail-panel');
  panel.classList.add('show');
  renderFlowDetail();
  // Focus first tab so keyboard users land inside the panel.
  const firstTab = panel.querySelector('[role="tab"][aria-selected="true"]') || panel.querySelector('[role="tab"]');
  if (firstTab) firstTab.focus();
}

function closeFlowDetail() {
  selectedFlowDetail = null;
  $('flow-list-panel').classList.remove('hide');
  $('flow-detail-panel').classList.remove('show');
  if (_flowDetailReturnFocus && typeof _flowDetailReturnFocus.focus === 'function') {
    try { _flowDetailReturnFocus.focus(); } catch {}
  }
  _flowDetailReturnFocus = null;
}

function switchFlowTab(tabId) {
  document.querySelectorAll('#flow-detail-panel .detail-tab').forEach(t => {
    t.classList.remove('active');
    t.setAttribute('aria-selected', 'false');
    t.setAttribute('tabindex', '-1');
  });
  document.querySelectorAll('#flow-detail-panel .tab-content').forEach(t => t.classList.remove('active'));
  const btn = document.querySelector('#flow-detail-panel .detail-tab[data-tab="'+tabId+'"]');
  if (btn) {
    btn.classList.add('active');
    btn.setAttribute('aria-selected', 'true');
    btn.setAttribute('tabindex', '0');
  }
  const panel = $(tabId);
  if (panel) panel.classList.add('active');
}

// Tablist arrow-key navigation (W3C ARIA Authoring Practices pattern)
document.addEventListener('keydown', (e) => {
  const target = e.target;
  if (!target || target.getAttribute('role') !== 'tab') return;
  const tablist = target.closest('[role="tablist"]');
  if (!tablist) return;
  const tabs = Array.from(tablist.querySelectorAll('[role="tab"]'));
  const idx = tabs.indexOf(target);
  if (idx < 0) return;
  let next = -1;
  if (e.key === 'ArrowRight') next = (idx + 1) % tabs.length;
  else if (e.key === 'ArrowLeft') next = (idx - 1 + tabs.length) % tabs.length;
  else if (e.key === 'Home') next = 0;
  else if (e.key === 'End') next = tabs.length - 1;
  if (next >= 0) {
    e.preventDefault();
    tabs[next].focus();
    tabs[next].click();
  }
});

function renderFlowDetail() {
  const f = selectedFlowDetail;
  if (!f) return;
  const m = parseManifest(f.manifestJson);

  $('fd-name').textContent = f.name;
  $('fd-actions').innerHTML =
    (f.isEnabled
      ? '<button class="btn btn-danger" onclick="toggleFlow(\''+f.id+'\',false, event)">Disable</button>'
      : '<button class="btn btn-success" onclick="toggleFlow(\''+f.id+'\',true, event)">Enable</button>')
    + '<button class="btn btn-primary" onclick="triggerFlow(\''+f.id+'\', event)">&#9654; Trigger</button>';

  // Manifest tab
  $('fd-manifest').innerHTML = '<table class="manifest-table"><tbody>'
    +'<tr><td style="color:var(--text-dim);width:140px">ID</td><td class="mono">'+f.id+'</td></tr>'
    +'<tr><td style="color:var(--text-dim)">Name</td><td><b>'+esc(f.name)+'</b></td></tr>'
    +'<tr><td style="color:var(--text-dim)">Version</td><td class="mono">'+esc(f.version)+'</td></tr>'
    +'<tr><td style="color:var(--text-dim)">Status</td><td>'+(f.isEnabled?'<span class="badge badge-enabled">Enabled</span>':'<span class="badge badge-disabled">Disabled</span>')+'</td></tr>'
    +'<tr><td style="color:var(--text-dim)">Steps</td><td class="mono">'+(m?Object.keys(m.steps||{}).length:'-')+'</td></tr>'
    +'<tr><td style="color:var(--text-dim)">Triggers</td><td class="mono">'+(m?Object.keys(m.triggers||{}).length:'-')+'</td></tr>'
    +'<tr><td style="color:var(--text-dim)">Created</td><td>'+fmtDate(f.createdAt)+'</td></tr>'
    +'<tr><td style="color:var(--text-dim)">Updated</td><td>'+fmtDate(f.updatedAt)+'</td></tr>'
    +'</tbody></table>';

  // Steps tab
  if (m && m.steps && Object.keys(m.steps).length > 0) {
    let rows = '';
    for (const [key, step] of Object.entries(m.steps)) {
      const ra = step.runAfter ? Object.keys(step.runAfter).join(', ') : '\u2014';
      const inputs = step.inputs ? Object.entries(step.inputs).map(([k,v]) => esc(k)+': '+esc(JSON.stringify(v))).join('<br>') : '\u2014';
      rows += '<tr><td class="mono" style="color:var(--accent)">'+esc(key)+'</td><td><span class="badge" style="background:var(--accent-light);color:var(--accent)">'+esc(step.type)+'</span></td><td class="mono" style="font-size:11px">'+inputs+'</td><td class="mono" style="font-size:11px">'+esc(ra)+'</td></tr>';
    }
    $('fd-steps').innerHTML = '<table class="manifest-table"><thead><tr><th>Key</th><th>Type</th><th>Inputs</th><th>RunAfter</th></tr></thead><tbody>'+rows+'</tbody></table>';
  } else {
    $('fd-steps').innerHTML = '<div class="empty-msg">No steps defined in manifest.</div>';
  }

  // Triggers tab - only Webhook trigger type has external URL for integration
  if (m && m.triggers && Object.keys(m.triggers).length > 0) {
    let rows = '';
    let triggerRowIdx = 0;
    const hasWebhook = Object.values(m.triggers).some(t => { const ty = (t.type||'').toLowerCase(); return ty === 'webhook'; });
    for (const [key, trigger] of Object.entries(m.triggers)) {
      const inputParts = trigger.inputs ? Object.entries(trigger.inputs).map(([k, v]) => {
        if (k === 'webhookSecret' && typeof v === 'string') {
          const id = 'ws-' + triggerRowIdx;
          return esc(k) + ': <span id="' + id + '" data-secret="' + esc(v) + '">\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022</span> <button class="btn-sm btn-sm-ghost" onclick="toggleWebhookSecret(\'' + id + '\', this)">Show</button>';
        }
        return esc(k) + ': ' + esc(JSON.stringify(v));
      }) : [];
      const inputs = inputParts.length ? inputParts.join('<br>') : '\u2014';
      triggerRowIdx++;
      const isWebhook = trigger.type && trigger.type.toLowerCase() === 'webhook';
      let urlCell = '';
      if (isWebhook) {
        const webhookBase = window.location.origin + '{{BASE_PATH}}' + '/api/webhook/';
        const urlByFlowId = webhookBase + selectedFlowDetail.id;
        urlCell = '<div class="webhook-url-cell" style="display:flex;align-items:center;gap:8px"><code class="mono" style="font-size:11px">'+esc(urlByFlowId)+'</code><button class="btn-copy" data-url="'+esc(urlByFlowId)+'" onclick="copyWebhookUrl(this.dataset.url)" title="Copy URL"><svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="2" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>Copy</button></div>';
      }
      rows += '<tr><td class="mono" style="color:var(--accent)">'+esc(key)+'</td><td><span class="badge '+(isWebhook?'badge-cron':'')+'" style="'+(isWebhook?'background:#eef4fa;color:var(--accent);border:1px solid #c5ddf0':'background:var(--success-bg);color:var(--success-text)')+'">'+esc(trigger.type)+'</span></td><td class="mono" style="font-size:11px">'+inputs+'</td><td>'+urlCell+'</td></tr>';
    }
    let webhookSection = '';
    if (hasWebhook) {
      const webhookBase = window.location.origin + '{{BASE_PATH}}' + '/api/webhook/';
      const urlByFlowId = webhookBase + selectedFlowDetail.id;
      webhookSection = '<div class="webhook-section" style="margin-top:16px;padding:12px;background:var(--surface2);border:1px solid var(--border);border-radius:var(--radius)"><div style="font-size:12px;font-weight:600;margin-bottom:8px;color:var(--text-dim)">Webhook URL (for external clients)</div><div class="cron-cell" style="display:flex;align-items:center;gap:8px;flex-wrap:wrap"><code class="mono" style="font-size:12px;flex:1;min-width:200px;word-break:break-all">'+esc(urlByFlowId)+'</code><button class="btn btn-sm btn-sm-primary" data-url="'+esc(urlByFlowId)+'" onclick="copyWebhookUrl(this.dataset.url)">Copy URL</button></div><div style="font-size:11px;color:var(--text-dim);margin-top:6px">POST JSON body to trigger. Use <code>X-Webhook-Key</code> header if webhookSecret is configured.</div></div>';
    }
    $('fd-triggers').innerHTML = '<table class="manifest-table"><thead><tr><th>Key</th><th>Type</th><th>Inputs</th><th>Webhook URL</th></tr></thead><tbody>'+rows+'</tbody></table>'+webhookSection;
  } else {
    $('fd-triggers').innerHTML = '<div class="empty-msg">No triggers defined in manifest.</div>';
  }

  // Schedule tab
  loadFlowSchedule();

  // DAG tab
  renderDAG(m);

  // Raw JSON tab
  $('fd-json').innerHTML = '<div class="json-viewer">'+(m ? esc(JSON.stringify(m, null, 2)) : 'No manifest data.')+'</div>';

  // Mermaid tab — fetched lazily from /api/flows/{id}/mermaid
  renderMermaidTab(f.id);
}

function renderMermaidTab(flowId) {
  const host = $('fd-mermaid');
  host.innerHTML = '<div class="empty-msg">Loading Mermaid…</div>';
  fetch('{{BASE_PATH}}/api/flows/'+flowId+'/mermaid', { headers: { 'Accept': 'text/plain' } })
    .then(r => r.ok ? r.text() : Promise.reject(r.statusText))
    .then(text => {
      const safe = esc(text);
      host.innerHTML =
        '<div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:8px">'
        +'<div style="font-size:12px;color:var(--text-dim)">Paste into any Markdown surface that renders Mermaid (GitHub, Notion, Confluence, …).</div>'
        +'<button class="btn-copy" onclick="copyMermaid(this)" data-mermaid="'+safe.replace(/"/g,'&quot;')+'">'
          +'<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="2" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>'
          +'Copy Mermaid'
        +'</button>'
        +'</div>'
        +'<div class="json-viewer">'+safe+'</div>';
    })
    .catch(err => { host.innerHTML = '<div class="empty-msg">Failed to load Mermaid: '+esc(String(err))+'</div>'; });
}

function copyMermaid(btn) {
  const tmp = document.createElement('textarea');
  tmp.innerHTML = btn.dataset.mermaid;
  const text = tmp.value;
  navigator.clipboard.writeText(text).then(() => showSuccess('Mermaid copied!')).catch(() => showError('Copy failed'));
}

function renderDAG(manifest) {
  const container = $('fd-dag');
  if (!manifest || !manifest.steps || Object.keys(manifest.steps).length === 0) {
    container.innerHTML = '<div class="empty-msg">No steps to visualize.</div>';
    return;
  }

  const steps = manifest.steps;
  const keys = Object.keys(steps);
  const nodeW = 180, nodeH = 44, padX = 60, padY = 30;

  const levels = {};
  const placed = new Set();

  function getLevel(key) {
    if (levels[key] !== undefined) return levels[key];
    const step = steps[key];
    if (!step.runAfter || Object.keys(step.runAfter).length === 0) {
      levels[key] = 0;
      return 0;
    }
    let maxParent = 0;
    for (const dep of Object.keys(step.runAfter)) {
      if (steps[dep]) maxParent = Math.max(maxParent, getLevel(dep) + 1);
    }
    levels[key] = maxParent;
    return maxParent;
  }

  keys.forEach(k => getLevel(k));

  const byLevel = {};
  keys.forEach(k => { const l = levels[k]; if (!byLevel[l]) byLevel[l] = []; byLevel[l].push(k); });
  const maxLevel = Math.max(...Object.keys(byLevel).map(Number));

  const positions = {};
  for (let l = 0; l <= maxLevel; l++) {
    const group = byLevel[l] || [];
    group.forEach((k, i) => {
      positions[k] = {
        x: l * (nodeW + padX) + 20,
        y: i * (nodeH + padY) + 20
      };
    });
  }

  const svgW = (maxLevel + 1) * (nodeW + padX) + 40;
  const maxNodesInLevel = Math.max(...Object.values(byLevel).map(g => g.length));
  const svgH = maxNodesInLevel * (nodeH + padY) + 40;

  let svg = '<div class="dag-svg" style="overflow:auto"><svg width="'+svgW+'" height="'+svgH+'" xmlns="http://www.w3.org/2000/svg">';
  svg += '<defs><marker id="arrowhead" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><polygon points="0 0, 8 3, 0 6" fill="#b0aea5"/></marker></defs>';

  for (const [key, step] of Object.entries(steps)) {
    if (step.runAfter) {
      for (const dep of Object.keys(step.runAfter)) {
        if (positions[dep] && positions[key]) {
          const from = positions[dep], to = positions[key];
          svg += '<line x1="'+(from.x+nodeW)+'" y1="'+(from.y+nodeH/2)+'" x2="'+to.x+'" y2="'+(to.y+nodeH/2)+'" stroke="#b0aea5" stroke-width="1.5" marker-end="url(#arrowhead)"/>';
        }
      }
    }
  }

  for (const [key, step] of Object.entries(steps)) {
    const p = positions[key];
    svg += '<g transform="translate('+p.x+','+p.y+')">'
      +'<rect width="'+nodeW+'" height="'+nodeH+'" rx="8" fill="#faf9f5" stroke="#e8e6dc" stroke-width="1"/>'
      +'<text x="10" y="18" fill="#141413" font-weight="500" font-size="12">'+esc(key)+'</text>'
      +'<text x="10" y="34" fill="#c96442" font-size="10">'+esc(step.type)+'</text>'
      +'</g>';
  }

  svg += '</svg></div>';
  container.innerHTML = svg;
}

function renderRunDAG(steps, manifest, selectedKey) {
  if (!manifest || !manifest.steps || Object.keys(manifest.steps).length === 0) {
    return '<div class="empty-msg">No DAG available — flow manifest not loaded.</div>';
  }

  // Build status lookup from runtime steps
  const statusMap = {};
  const stepMap = {};
  for (const s of steps) { statusMap[s.stepKey] = s.status; stepMap[s.stepKey] = s; }

  const mSteps = manifest.steps;
  const keys = Object.keys(mSteps);
  const nodeW = 220, nodeH = 72, padX = 90, padY = 22;

  // Compute DAG levels (same algorithm as renderDAG)
  const levels = {};
  function getLevel(key) {
    if (levels[key] !== undefined) return levels[key];
    const step = mSteps[key];
    if (!step.runAfter || Object.keys(step.runAfter).length === 0) return (levels[key] = 0);
    let max = 0;
    for (const dep of Object.keys(step.runAfter)) {
      if (mSteps[dep]) max = Math.max(max, getLevel(dep) + 1);
    }
    return (levels[key] = max);
  }
  keys.forEach(k => getLevel(k));

  const byLevel = {};
  keys.forEach(k => { const l = levels[k]; (byLevel[l] = byLevel[l] || []).push(k); });
  const maxLevel = Math.max(...Object.keys(byLevel).map(Number));

  const positions = {};
  for (let l = 0; l <= maxLevel; l++) {
    (byLevel[l] || []).forEach((k, i) => {
      positions[k] = { x: l * (nodeW + padX) + 20, y: i * (nodeH + padY) + 20 };
    });
  }

  const svgW = (maxLevel + 1) * (nodeW + padX) + 40;
  const maxNodes = Math.max(...Object.values(byLevel).map(g => g.length));
  const svgH = maxNodes * (nodeH + padY) + 40;

  // Status → visual tokens
  function nodeStyle(status) {
    switch (status) {
      case 'Succeeded': return { fill:'#eaf5ef', stroke:'#2d6a4f', text:'#1b4435', dimText:'#2d6a4f', label:'✓ Succeeded', cls:'' };
      case 'Failed':    return { fill:'#fdf0ee', stroke:'#b53333', text:'#7a1e1e', dimText:'#b53333', label:'✗ Failed',    cls:'' };
      case 'Running':   return { fill:'#fef4e8', stroke:'#c8803a', text:'#7a3f08', dimText:'#c8803a', label:'◉ Running',   cls:'dag-node-running' };
      case 'Skipped':   return { fill:'#f5f4ed', stroke:'#87867f', text:'#5e5d59', dimText:'#87867f', label:'⊘ Blocked',   cls:'' };
      case 'Pending':   return { fill:'#faf9f5', stroke:'#d1cfc5', text:'#87867f', dimText:'#b0aea5', label:'○ Pending',   cls:'' };
      default:          return { fill:'#faf9f5', stroke:'#e8e6dc', text:'#87867f', dimText:'#b0aea5', label:'○ Pending',   cls:'' };
    }
  }

  // Edge color based on source status
  function edgeStroke(status) {
    switch (status) {
      case 'Succeeded': return { color:'#b5d9c5', marker:'dag-arr-ok'   };
      case 'Failed':    return { color:'#f5c6c0', marker:'dag-arr-fail' };
      case 'Running':   return { color:'#f6d8b3', marker:'dag-arr-run'  };
      default:          return { color:'#d1cfc5', marker:'dag-arr-def'  };
    }
  }

  let svg = '<div class="dag-svg" style="min-height:160px"><svg width="'+svgW+'" height="'+svgH+'" xmlns="http://www.w3.org/2000/svg">';

  // Arrow markers
  svg += '<defs>';
  [['dag-arr-ok','#b5d9c5'],['dag-arr-fail','#f5c6c0'],['dag-arr-run','#f6d8b3'],['dag-arr-def','#d1cfc5']].forEach(([id,col]) => {
    svg += '<marker id="'+id+'" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><polygon points="0 0, 8 3, 0 6" fill="'+col+'"/></marker>';
  });
  svg += '</defs>';

  // Draw edges
  for (const [key, step] of Object.entries(mSteps)) {
    if (!step.runAfter) continue;
    for (const dep of Object.keys(step.runAfter)) {
      if (!positions[dep] || !positions[key]) continue;
      const from = positions[dep], to = positions[key];
      const { color, marker } = edgeStroke(statusMap[dep]);
      svg += '<line'
        +' x1="'+(from.x+nodeW)+'" y1="'+(from.y+nodeH/2)+'"'
        +' x2="'+to.x+'" y2="'+(to.y+nodeH/2)+'"'
        +' stroke="'+color+'" stroke-width="2"'
        +' marker-end="url(#'+marker+')"/>';
    }
  }

  // Find blocked-reason helper
  function blockedReason(key) {
    const step = mSteps[key];
    if (!step || !step.runAfter) return null;
    for (const dep of Object.keys(step.runAfter)) {
      const depStatus = statusMap[dep];
      if (depStatus && depStatus !== 'Succeeded') return dep;
    }
    return null;
  }

  // Draw nodes — clicking selects the step (updates URL + inspector)
  for (const [key, step] of Object.entries(mSteps)) {
    if (!positions[key]) continue;
    const p = positions[key];
    const status = statusMap[key] || 'Pending';
    const ns = nodeStyle(status);
    const safeKey = key.replace(/'/g, "\\'");
    const runtimeStep = stepMap[key];
    const attemptCount = runtimeStep ? getStepAttemptCount(runtimeStep) : 1;
    const dur = runtimeStep && runtimeStep.startedAt ? duration(runtimeStep.startedAt, runtimeStep.completedAt) : '\u2014';
    const metaText = dur + (attemptCount > 1 ? '  \u00b7  \u00d7'+attemptCount+' attempts' : '');
    const classList = [ns.cls, selectedKey===key ? 'dag-node-selected' : '', status==='Skipped' ? 'dag-node-blocked' : ''].filter(Boolean).join(' ');
    const blockedOn = status==='Skipped' ? blockedReason(key) : null;
    const tooltip = esc(key)+'\n'+(step.type||'')+(attemptCount>1?'\n\u00d7'+attemptCount+' attempts':'')
      + (blockedOn ? '\nBlocked because "'+blockedOn+'" did not succeed.' : '');
    svg += '<g class="'+classList+'" transform="translate('+p.x+','+p.y+')"'
      +' onclick="selectStep(\''+safeKey+'\')"'
      +' style="cursor:pointer">'
      +'<title>'+tooltip+'</title>'
      +'<rect class="dag-node-bg" width="'+nodeW+'" height="'+nodeH+'" rx="8"'
      +' fill="'+ns.fill+'" stroke="'+ns.stroke+'" stroke-width="1.5"/>'
      // step key (monospace, top)
      +'<text x="10" y="20" fill="'+ns.text+'"'
      +' font-weight="600" font-size="12"'
      +' font-family="JetBrains Mono,monospace">'+esc(key.length>24?key.slice(0,23)+'\u2026':key)+'</text>'
      // step type (terracotta)
      +'<text x="10" y="36" fill="#c96442"'
      +' font-size="10" font-family="Inter,sans-serif">'+esc((step.type||'').slice(0,28))+'</text>'
      // duration · attempts (grey)
      +'<text x="10" y="52" fill="'+ns.dimText+'"'
      +' font-size="10" font-family="JetBrains Mono,monospace">'+esc(metaText)+'</text>'
      // status label (bottom-right pill)
      +'<text x="'+(nodeW-8)+'" y="'+(nodeH-8)+'"'
      +' fill="'+ns.dimText+'" font-size="9" font-weight="700"'
      +' text-anchor="end" font-family="Inter,sans-serif"'
      +' letter-spacing="0.4">'+ns.label+'</text>'
      +'</g>';
  }

  svg += '</svg></div>';
  return svg;
}

async function toggleFlow(id, enable, ev) {
  await withBusy(ev && ev.currentTarget, async () => {
    try {
      const res = await fetch(BASE+'/flows/'+id+'/'+(enable?'enable':'disable'), {method:'POST'});
      if (!res.ok) { showError('Failed to ' + (enable?'enable':'disable') + ' flow', 'HTTP ' + res.status); return; }
      await loadFlows();
      selectedFlowDetail = allFlows.find(f => f.id === id);
      renderFlowDetail();
      showSuccess(enable ? 'Flow enabled' : 'Flow disabled');
    } catch(e) { showError('Failed to toggle flow', e.message); }
  });
}

async function triggerFlow(id, ev) {
  // Prompt for an optional JSON body so flows that read @triggerBody().xxx in
  // their When clauses or step inputs can be exercised from the dashboard.
  const raw = prompt(
    'Trigger body (JSON). Leave blank for an empty payload.\nExample: {"amount": 1500}',
    '{}'
  );
  if (raw === null) return; // user cancelled
  let body = '{}';
  if (raw.trim().length > 0) {
    try {
      JSON.parse(raw); // validate
      body = raw;
    } catch (e) {
      showError('Invalid JSON', e.message);
      return;
    }
  }
  await withBusy(ev && ev.currentTarget, async () => {
    try {
      const res = await fetch(BASE+'/flows/'+id+'/trigger', {method:'POST', headers:{'Content-Type':'application/json'}, body});
      const data = await res.json();
      if (data.runId) {
        showSuccess('Flow triggered (' + data.runId.slice(0, 8) + '…)');
        navigate('runs');
      } else {
        showError(data.error || 'Trigger failed');
      }
    } catch(e) { showError('Failed to trigger flow', e.message); }
  });
}

function copyWebhookUrl(url) {
  navigator.clipboard.writeText(url).then(() => showSuccess('Webhook URL copied')).catch(() => showError('Copy failed'));
}

function copyRunLink(runId) {
  const url = location.origin + location.pathname + '#/runs/' + runId;
  navigator.clipboard.writeText(url).then(() => showSuccess('Run link copied!')).catch(() => showError('Copy failed'));
}

function copyStepLink(runId, stepKey) {
  const url = location.origin + location.pathname + '#/runs/' + runId + '/steps/' + encodeURIComponent(stepKey);
  navigator.clipboard.writeText(url).then(() => showSuccess('Step link copied!')).catch(() => showError('Copy failed'));
}

function copyPanelContent(btn) {
  const body = btn.closest('.step-detail').querySelector('.step-detail-body');
  const text = body ? body.textContent : '';
  navigator.clipboard.writeText(text).then(() => showSuccess('Copied!')).catch(() => showError('Copy failed'));
}

// ── Central toast / error feedback system ────────────────────────────
// kind: 'info' | 'success' | 'warning' | 'error'
function showToast(msg, kind, opts) {
  const region = $('toast-region') || document.body;
  // Cap stacked toasts at 5 to prevent runaway accumulation.
  const existing = region.querySelectorAll('.toast');
  if (existing.length >= 5) existing[0].remove();
  const k = ['info','success','warning','error'].includes(kind) ? kind : 'info';
  const t = document.createElement('div');
  t.className = 'toast toast-' + k;
  t.setAttribute('role', k === 'error' ? 'alert' : 'status');
  t.textContent = msg;
  region.appendChild(t);
  const dwell = (opts && opts.dwell) || (k === 'error' ? 6000 : 2400);
  setTimeout(() => { t.classList.add('toast-out'); setTimeout(() => t.remove(), 400); }, dwell);
  return t;
}

function showError(msg, detail) {
  const text = detail ? (msg + ' — ' + detail) : msg;
  return showToast(text, 'error');
}

function showSuccess(msg) { return showToast(msg, 'success'); }

// ── Skeleton state helpers ───────────────────────────────────────────
function skeletonRows(rows, cols) {
  const r = Math.max(1, rows | 0);
  const c = Math.max(1, cols | 0);
  let html = '<table class="recent-table" aria-hidden="true"><tbody>';
  for (let i = 0; i < r; i++) {
    html += '<tr>';
    for (let j = 0; j < c; j++) {
      html += '<td><span class="skeleton skeleton-line" style="width:' + (50 + ((i + j) * 13) % 40) + '%"></span></td>';
    }
    html += '</tr>';
  }
  return html + '</tbody></table>';
}

function skeletonCards(n) {
  let html = '<div class="flows-grid">';
  for (let i = 0; i < n; i++) {
    html += '<div class="flow-card skeleton-card" aria-hidden="true">'
      + '<span class="skeleton skeleton-line" style="width:60%;height:18px"></span>'
      + '<span class="skeleton skeleton-line" style="width:35%;height:12px;margin-top:8px"></span>'
      + '<span class="skeleton skeleton-line" style="width:80%;height:12px;margin-top:14px"></span>'
      + '</div>';
  }
  return html + '</div>';
}

function skeletonStats(n) {
  let html = '<div class="stats-grid stats-grid--' + (n === 5 ? '5col' : 'auto') + '">';
  for (let i = 0; i < n; i++) {
    html += '<div class="stat-card skeleton-card" aria-hidden="true">'
      + '<span class="skeleton skeleton-line" style="width:30%;height:32px"></span>'
      + '<span class="skeleton skeleton-line" style="width:60%;height:11px;margin-top:8px"></span>'
      + '</div>';
  }
  return html + '</div>';
}

// ── Sparkline + heatmap visualisation helpers ────────────────────────
// `points` is a numeric array. Renders an SVG path (line + optional fill)
// sized to fit a given width/height with min/max normalised across values.
function sparklinePath(points, w, h, opts) {
  if (!points || points.length === 0) return '';
  const max = Math.max(1, ...points);
  const min = 0;
  const range = max - min || 1;
  const stepX = points.length > 1 ? w / (points.length - 1) : w;
  let d = '';
  let fill = '';
  for (let i = 0; i < points.length; i++) {
    const x = (i * stepX).toFixed(2);
    const y = (h - ((points[i] - min) / range) * h).toFixed(2);
    d += (i === 0 ? 'M' : 'L') + x + ',' + y + ' ';
  }
  // Build a closed area beneath the line for the fill underlay.
  fill = d + 'L' + w.toFixed(2) + ',' + h + ' L0,' + h + ' Z';
  const stroke = (opts && opts.color) || 'currentColor';
  return '<svg class="' + ((opts && opts.cls) || 'stat-card-spark') + '" viewBox="0 0 ' + w + ' ' + h + '" preserveAspectRatio="none" aria-hidden="true">'
    + '<path class="spark-fill" d="' + fill + '" fill="' + stroke + '"/>'
    + '<path d="' + d.trim() + '" stroke="' + stroke + '"/>'
    + '</svg>';
}

// Adapt server-side timeseries response to the existing { hour, success, failed, skipped, running, total }
// shape used by renderActivityHeatmap. Skipped maps to Cancelled per UI conventions.
function bucketsFromTimeseries(serverBuckets) {
  return (serverBuckets || []).map(b => ({
    hour: new Date(b.timestamp),
    end: new Date(new Date(b.timestamp).getTime() + 3600000),
    total: b.total || 0,
    success: b.succeeded || 0,
    failed: b.failed || 0,
    skipped: b.cancelled || 0,
    running: b.running || 0,
    p50: b.p50DurationMs,
    p95: b.p95DurationMs
  }));
}

// Group an array of runs into 24 hourly buckets ending now. Returns an
// array of { hour: Date, success, failed, skipped, running, total }.
// Kept as a fallback for environments that don't expose /api/runs/timeseries (default-impl returns []).
function bucketRunsByHour(runs, now) {
  now = now || new Date();
  const buckets = [];
  const HOUR_MS = 3600000;
  // Anchor the latest bucket at the current hour.
  const latestBucketStart = new Date(now.getFullYear(), now.getMonth(), now.getDate(), now.getHours());
  for (let i = 23; i >= 0; i--) {
    const start = new Date(latestBucketStart.getTime() - i * HOUR_MS);
    const end = new Date(start.getTime() + HOUR_MS);
    buckets.push({ hour: start, end, success: 0, failed: 0, skipped: 0, running: 0, total: 0 });
  }
  for (const r of (runs || [])) {
    const t = r.startedAt ? new Date(r.startedAt).getTime() : 0;
    if (!t) continue;
    const idx = buckets.findIndex(b => t >= b.hour.getTime() && t < b.end.getTime());
    if (idx < 0) continue;
    const b = buckets[idx];
    b.total++;
    const s = (r.status || '').toLowerCase();
    if (s === 'succeeded') b.success++;
    else if (s === 'failed') b.failed++;
    else if (s === 'skipped') b.skipped++;
    else if (s === 'running') b.running++;
  }
  return buckets;
}

// Render the 24h activity heatmap into #ov-heatmap and update the summary line.
function renderActivityHeatmap(buckets) {
  const el = $('ov-heatmap');
  if (!el) return;
  const max = Math.max(1, ...buckets.map(b => b.total));
  let html = '';
  for (const b of buckets) {
    const hour = b.hour.getHours();
    if (b.total === 0) {
      html += '<div class="ov-bar ov-bar-empty" title="' + hour + ':00 — no runs"></div>';
      continue;
    }
    const ratio = b.total / max;
    const heightPct = Math.max(8, Math.round(ratio * 100));
    const segs = ['success','failed','skipped','running'].map(k => {
      const n = b[k]; if (!n) return '';
      const pct = (n / b.total) * heightPct;
      return '<span class="ov-bar-segment ' + k + '" style="height:' + pct.toFixed(2) + '%"></span>';
    }).join('');
    const tip = hour + ':00 — ' + b.total + ' run' + (b.total !== 1 ? 's' : '')
      + (b.success ? ', ' + b.success + ' succeeded' : '')
      + (b.failed ? ', ' + b.failed + ' failed' : '')
      + (b.skipped ? ', ' + b.skipped + ' blocked' : '');
    html += '<div class="ov-bar" title="' + tip + '" aria-label="' + tip + '">' + segs + '</div>';
  }
  el.innerHTML = html;
  // Summary footer.
  const total = buckets.reduce((a, b) => a + b.total, 0);
  const success = buckets.reduce((a, b) => a + b.success, 0);
  const failed = buckets.reduce((a, b) => a + b.failed, 0);
  const summary = $('ov-heatmap-summary');
  if (summary) {
    summary.textContent = total === 0
      ? '0 runs'
      : total + ' runs · ' + success + ' ok · ' + failed + ' failed · ' + (Math.round((success / Math.max(1, total)) * 100)) + '% success';
  }
}

// Render a 30-day GitHub-style calendar from a server timeseries (bucket=day).
// Cells are coloured by run volume (5 quantile levels) and stamped with a
// failure-coral underline when the day had any failed runs.
function renderActivityCalendar(serverBuckets) {
  const el = $('ov-calendar');
  if (!el) return;
  const days = (serverBuckets || []).map(b => ({
    ts: new Date(b.timestamp),
    total: b.total || 0,
    failed: b.failed || 0,
    succeeded: b.succeeded || 0,
    cancelled: b.cancelled || 0
  }));
  if (days.length === 0) {
    el.innerHTML = '<div class="empty-msg">No activity in the last 30 days.</div>';
    const sum = $('ov-calendar-summary');
    if (sum) sum.textContent = '0 runs';
    return;
  }
  // Quantile thresholds across non-zero days so colour scale is data-relative.
  const totals = days.map(d => d.total).filter(n => n > 0).sort((a, b) => a - b);
  const q = (p) => totals.length ? totals[Math.floor((totals.length - 1) * p)] : 0;
  const t1 = q(0.25), t2 = q(0.5), t3 = q(0.75), t4 = q(0.95);
  function level(n) {
    if (n === 0) return 0;
    if (n <= t1) return 1;
    if (n <= t2) return 2;
    if (n <= t3) return 3;
    if (n <= t4) return 4;
    return 4;
  }

  // Pad leading cells so the first column starts on Monday (ISO week, dow=1..7).
  const first = days[0].ts;
  const firstDow = ((first.getDay() + 6) % 7); // Mon=0 .. Sun=6
  let html = '';
  for (let i = 0; i < firstDow; i++) {
    html += '<div class="cal-cell cal-pad" aria-hidden="true"></div>';
  }
  for (const d of days) {
    const lvl = level(d.total);
    const dateLabel = d.ts.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
    const tip = dateLabel + ' — ' + d.total + ' run' + (d.total !== 1 ? 's' : '')
      + (d.succeeded ? ', ' + d.succeeded + ' ok' : '')
      + (d.failed ? ', ' + d.failed + ' failed' : '')
      + (d.cancelled ? ', ' + d.cancelled + ' cancelled' : '');
    const failClass = d.failed > 0 ? ' cal-failure' : '';
    html += '<div class="cal-cell cal-l' + lvl + failClass + '" title="' + tip + '" aria-label="' + tip + '"></div>';
  }
  el.innerHTML = html;

  const totalRuns = days.reduce((a, b) => a + b.total, 0);
  const totalFailed = days.reduce((a, b) => a + b.failed, 0);
  const totalOk = days.reduce((a, b) => a + b.succeeded, 0);
  const activeDays = days.filter(d => d.total > 0).length;
  const sum = $('ov-calendar-summary');
  if (sum) {
    sum.textContent = totalRuns === 0
      ? '0 runs'
      : totalRuns + ' runs · ' + activeDays + '/' + days.length + ' active days · ' + totalOk + ' ok · ' + totalFailed + ' failed';
  }
}

// ── Filter chips (Runs page) ─────────────────────────────────────────
function renderFilterChips() {
  const container = $('runs-filter-chips');
  if (!container) return;
  const params = (Router.parse() || {}).params || {};
  const flow = params.flow || $('runs-filter-flow').value;
  const status = params.status || $('runs-filter-status').value;
  const q = params.q || $('runs-filter-search').value;
  const chips = [];
  if (flow) {
    const flowMeta = (allFlows || []).find(f => f.id === flow);
    chips.push({ key: 'flow', label: 'Flow: ' + (flowMeta ? flowMeta.name : flow.slice(0, 8) + '…') });
  }
  if (status) chips.push({ key: 'status', label: 'Status: ' + (status === 'Skipped' ? 'Blocked' : status) });
  if (q) chips.push({ key: 'q', label: 'Search: "' + (q.length > 24 ? q.slice(0, 23) + '…' : q) + '"' });
  if (chips.length === 0) { container.innerHTML = ''; return; }
  container.innerHTML = chips.map(c =>
    '<span class="filter-chip">' + esc(c.label)
    + '<button class="filter-chip-remove" type="button" onclick="removeRunsFilter(\'' + c.key + '\')" aria-label="Remove filter ' + esc(c.label) + '">×</button>'
    + '</span>'
  ).join('') + '<button class="filter-chip-clear-all" type="button" onclick="clearAllRunsFilters()">Clear all</button>';
}

function removeRunsFilter(key) {
  if (key === 'flow') { $('runs-filter-flow').value = ''; }
  else if (key === 'status') { $('runs-filter-status').value = ''; }
  else if (key === 'q') { $('runs-filter-search').value = ''; }
  onRunsFilterChange();
}

function clearAllRunsFilters() {
  $('runs-filter-flow').value = '';
  $('runs-filter-status').value = '';
  $('runs-filter-search').value = '';
  onRunsFilterChange();
}

function scrollToStep(stepKey) {
  const node = document.querySelector('.step-node[data-step-key="'+stepKey.replace(/"/g, '&quot;')+'"]');
  if (!node) return;
  const card = node.querySelector('.step-card');
  if (card) card.classList.add('step-target');
  node.scrollIntoView({ behavior: 'smooth', block: 'center' });
  setTimeout(() => { if (card) card.classList.remove('step-target'); }, 4000);
}

async function resolveFlowManifest(run) {
  // Try allFlows cache first
  let flow = allFlows.find(f => f.name === run.flowName || f.id === run.flowId);
  if (flow) return parseManifest(flow.manifestJson);
  // Cache miss (e.g. navigated directly to run URL) — fetch on demand
  try {
    const flows = await fetchJSON(BASE + '/flows');
    flow = flows.find(f => f.name === run.flowName || f.id === run.flowId);
    if (flow) { allFlows = flows; return parseManifest(flow.manifestJson); }
  } catch {}
  return null;
}

async function switchRunView(view) {
  if (!['timeline','dag','gantt','events'].includes(view)) view = 'timeline';
  currentRunView = view;
  if (selectedRunId) setRunRoute(selectedRunId, selectedStepKey, view);
  const tabsEl = $('run-tabs');
  if (tabsEl) tabsEl.innerHTML = renderTabs(view);
  const bodyEl = $('run-tab-body');
  if (!bodyEl || !currentRun) return;
  // Lazy-fetch manifest the first time DAG or Gantt is opened.
  if ((view === 'dag' || view === 'gantt') && !currentRunManifest) {
    bodyEl.innerHTML = '<div style="display:flex;align-items:center;justify-content:center;min-height:180px;gap:10px;color:var(--text-dim)">'
      + '<div style="width:18px;height:18px;border:2px solid var(--border-dark);border-top-color:var(--accent);border-radius:50%;animation:spin .55s linear infinite;flex-shrink:0"></div>'
      + '<span style="font-size:12px">Loading DAG…</span>'
      + '</div>';
    currentRunManifest = await resolveFlowManifest(currentRun);
  }
  bodyEl.innerHTML = renderActiveTab(currentRun, currentRunManifest, view);
  if (view === 'timeline' && selectedStepKey) scrollToStep(selectedStepKey);
  if (view === 'events') openEventsDrawer(selectedRunId);
  else closeEventsDrawer();
}

function renderTabs(activeView) {
  const tabs = [
    { key:'timeline', label:'Timeline' },
    { key:'dag',      label:'DAG' },
    { key:'gantt',    label:'Gantt' },
    { key:'events',   label:'Events' }
  ];
  return tabs.map(t =>
    '<button class="tab'+(activeView===t.key?' active':'')+'" onclick="switchRunView(\''+t.key+'\')">'+t.label+'</button>'
  ).join('');
}

function renderActiveTab(run, manifest, view) {
  const steps = run.steps || [];
  switch (view) {
    case 'dag':
      return '<div class="run-dag-wrap">'
        +renderRunDAG(steps, manifest, selectedStepKey)
        +renderStepInspector(findStep(steps, selectedStepKey))
        +'</div>';
    case 'gantt':
      return '<div class="gantt-wrap">'
        +renderGantt(steps, manifest, selectedStepKey)
        +renderStepInspector(findStep(steps, selectedStepKey))
        +'</div>';
    case 'events':
      return '<div style="padding:16px 18px">'
        +'<div style="font-size:11px;font-weight:600;text-transform:uppercase;letter-spacing:.5px;color:var(--text-dim);margin-bottom:10px">Step Timeline ('+steps.length+' step'+(steps.length!==1?'s':'')+')</div>'
        +(steps.length===0?'<div class="empty-msg">No steps recorded yet.</div>':renderTimeline(steps, run.id))
        +'</div>';
    case 'timeline':
    default: {
      const triggerPanels = renderRunTriggerPanels(run);
      return '<div style="padding:16px 18px">'+triggerPanels
        +'<div style="font-size:11px;font-weight:600;text-transform:uppercase;letter-spacing:.5px;color:var(--text-dim);margin-bottom:10px">Step Timeline ('+steps.length+' step'+(steps.length!==1?'s':'')+')</div>'
        +(steps.length===0?'<div class="empty-msg">No steps recorded yet.</div>':renderTimeline(steps, run.id))
        +'</div>';
    }
  }
}

function findStep(steps, key) {
  if (!key || !steps) return null;
  return steps.find(s => s.stepKey === key) || null;
}

function isTerminalStatus(s) { return s === 'Succeeded' || s === 'Failed' || s === 'Cancelled'; }

function renderRunHeader(run, steps) {
  const total = steps.length;
  const completed = steps.filter(s => isTerminalStatus(s.status) || s.status === 'Skipped').length;
  const pct = total ? Math.round((completed / total) * 100) : 0;
  const isRunning = run.status === 'Running';
  const failedCount = steps.filter(s => s.status === 'Failed').length;
  const runId = run.id;
  const progress = isRunning && total > 0
    ? '<div class="progress-bar"><div class="progress-fill" style="width:'+pct+'%"></div></div>'
      +'<span class="progress-label">'+completed+' of '+total+' complete</span>'
    : '';
  const liveDot = isRunning ? '<span class="live-dot">Live</span>' : '';
  const actions = []
    .concat(isRunning ? ['<button class="btn-action danger" onclick="cancelRun(\''+runId+'\', event)">Cancel run</button>'] : [])
    .concat(failedCount > 0 ? ['<button class="btn-action" onclick="retryAllFailed(\''+runId+'\', event)">Retry failed steps ('+failedCount+')</button>'] : [])
    .concat(['<button class="btn-action primary" onclick="rerunAll(\''+runId+'\', event)">Re-run all</button>']);
  return '<div class="detail-header">'
    +'<div style="font-size:11px;font-weight:600;text-transform:uppercase;letter-spacing:.5px;color:var(--text-dim);margin-bottom:4px;display:flex;align-items:center;justify-content:space-between">'
    +'<span>Run Detail \u00b7 '+statusBadge(run.status)+'</span>'
    +'<button class="btn-copy" onclick="copyRunLink(\''+runId+'\')" title="Copy shareable link"><svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="2" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>Copy link</button>'
    +'</div>'
    +'<div class="detail-runid">'+runId+'</div>'
    +'<div style="font-size:12px;color:var(--text-dim);margin-top:6px;display:flex;gap:14px;flex-wrap:wrap">'
    +'<span>Flow: <b style="color:var(--text)">'+esc(run.flowName||'\u2014')+'</b></span>'
    +'<span>Trigger: <b style="color:var(--text)">'+esc(run.triggerKey||'\u2014')+'</b></span>'
    +'<span>Started: <b style="color:var(--text)">'+fmtDate(run.startedAt)+'</b></span>'
    +'<span>Duration: <b style="color:var(--text)">'+duration(run.startedAt,run.completedAt)+'</b></span>'
    +(run.sourceRunId ? '<span>Re-run of: <a href="#/runs/'+run.sourceRunId+'" style="color:var(--accent);font-family:var(--font-mono);text-decoration:underline">'+run.sourceRunId.substring(0,8)+'\u2026</a></span>' : '')
    +'</div>'
    +'<div id="lineage-derived" style="font-size:12px;color:var(--text-dim);margin-top:4px"></div>'
    +(progress || liveDot || actions.length
      ? '<div class="run-header-actions">'+liveDot+progress+actions.join('')+'</div>'
      : '')
    +'</div>';
}

function renderStepInspector(step) {
  if (!step) return '<div class="step-inspector-empty">Select a step above to see its details.</div>';
  const attemptCount = getStepAttemptCount(step);
  const timing = step.status === 'Skipped' || step.status === 'Pending'
    ? ''
    : '<div class="step-timing"><span>Start: '+fmt(step.startedAt)+'</span><span>End: '+fmt(step.completedAt)+'</span><span>Duration: '+duration(step.startedAt,step.completedAt)+'</span></div>';
  return '<div class="step-inspector">'
    +'<div class="step-card-header"><div><div class="step-key">'+esc(step.stepKey)+'</div>'
    +(step.status!=='Skipped'?'<div class="step-type">'+esc(step.stepType||'')+'</div>':'')+'</div>'
    +'<div style="display:flex;align-items:center;gap:6px"><span class="step-badge '+step.status+'">'+stepStatusLabel(step.status)+'</span>'
    +(attemptCount>1?'<span class="step-badge Pending">x'+attemptCount+' attempts</span>':'')
    +'</div></div>'
    +timing
    +renderStepDebugPanels(step)
    +'</div>';
}

function selectStep(stepKey) {
  selectedStepKey = stepKey || null;
  if (selectedRunId) setRunRoute(selectedRunId, selectedStepKey, currentRunView);
  if (currentRun) {
    const bodyEl = $('run-tab-body');
    if (bodyEl) bodyEl.innerHTML = renderActiveTab(currentRun, currentRunManifest, currentRunView);
    if (currentRunView === 'timeline' && selectedStepKey) scrollToStep(selectedStepKey);
  }
}

async function cancelRun(runId, ev) {
  if (!confirm('Cancel run "'+runId+'"?')) return;
  await withBusy(ev && ev.currentTarget, async () => {
    try {
      const res = await fetch(BASE+'/runs/'+runId+'/cancel', { method:'POST' });
      const data = await res.json();
      if (!res.ok || data.accepted === false) {
        showError(data.message || data.error || 'Cancel request rejected');
      } else {
        showSuccess('Cancel requested');
      }
      await selectRun(runId, true, selectedStepKey, currentRunView);
    } catch(e) { showError('Failed to cancel run', e.message); }
  });
}

async function retryAllFailed(runId, ev) {
  if (!currentRun) return;
  const failed = (currentRun.steps || []).filter(s => s.status === 'Failed');
  if (failed.length === 0) { showToast('No failed steps to retry', 'warning'); return; }
  if (!confirm('Retry '+failed.length+' failed step'+(failed.length!==1?'s':'')+'?')) return;
  await withBusy(ev && ev.currentTarget, async () => {
    let okCount = 0;
    for (const s of failed) {
      try {
        const res = await fetch(BASE+'/runs/'+runId+'/steps/'+encodeURIComponent(s.stepKey)+'/retry', { method:'POST' });
        if (res.ok) okCount++;
      } catch(e) { console.error('Retry failed for', s.stepKey, e); }
    }
    if (okCount > 0) showSuccess('Retried ' + okCount + ' step' + (okCount !== 1 ? 's' : ''));
    else showError('Retry failed for all steps');
    await selectRun(runId, true, selectedStepKey, currentRunView);
  });
}

async function rerunAll(runId, ev) {
  if (!confirm('Re-run this flow with the original trigger payload?')) return;
  await withBusy(ev && ev.currentTarget, async () => {
    try {
      const res = await fetch(BASE+'/runs/'+runId+'/rerun', { method:'POST' });
      const data = await res.json();
      if (!res.ok || !data.runId) { showError(data.error || 'Re-run failed'); return; }
      showSuccess('Re-run started');
      await selectRun(data.runId, false, null, 'timeline');
    } catch(e) { showError('Failed to re-run', e.message); }
  });
}

function openEventsDrawer(runId) {
  if (!runId) return;
  eventsDrawerOpen = true;
  let drawer = $('events-drawer');
  if (!drawer) {
    drawer = document.createElement('div');
    drawer.id = 'events-drawer';
    drawer.className = 'events-drawer';
    document.body.appendChild(drawer);
  }
  drawer.innerHTML =
    '<div class="events-drawer-header">'
    +'<span class="events-drawer-title">Event Stream'+(selectedStepKey?' \u00b7 <span style="color:var(--accent);font-family:\'JetBrains Mono\',monospace;font-size:12px">'+esc(selectedStepKey)+'</span>':'')+'</span>'
    +'<button class="events-drawer-close" onclick="switchRunView(\'timeline\')">\u2715</button>'
    +'</div>'
    +'<div class="events-drawer-body" id="events-drawer-body">Loading events\u2026</div>';
  drawer.classList.add('open');
  loadEventsIntoDrawer(runId);
}

function closeEventsDrawer() {
  eventsDrawerOpen = false;
  const drawer = $('events-drawer');
  if (drawer) drawer.classList.remove('open');
}

async function loadEventsIntoDrawer(runId) {
  const body = $('events-drawer-body');
  if (!body) return;
  try {
    const events = await fetchJSON(BASE+'/runs/'+runId+'/events?take=500');
    if (!events || events.length === 0) {
      body.innerHTML = '<div class="step-inspector-empty">Event stream not enabled on this backend. Configure <code>IFlowEventReader</code> to see the raw event log.</div>';
      return;
    }
    const filtered = selectedStepKey ? events.filter(e => e.stepKey === selectedStepKey) : events;
    if (filtered.length === 0) {
      body.innerHTML = '<div class="step-inspector-empty">No events for step '+esc(selectedStepKey||'')+'.</div>';
      return;
    }
    const groups = {};
    for (const e of filtered) {
      const k = e.stepKey || '(run)';
      (groups[k] = groups[k] || []).push(e);
    }
    let html = '';
    for (const k of Object.keys(groups)) {
      html += '<div class="event-group"><div class="event-group-title">'+esc(k)+'</div>';
      for (const e of groups[k]) {
        html += '<div class="event-item">'
          +'<span class="event-time">'+fmt(e.timestamp)+'</span>'
          +'<span class="event-kind">'+esc(e.type||'event')+'</span>'
          +'<span class="event-detail">'+esc(e.message||'')+'</span>'
          +'</div>';
      }
      html += '</div>';
    }
    body.innerHTML = html;
  } catch(err) {
    body.innerHTML = '<div class="step-inspector-empty">Failed to load events: '+esc(err.message||'unknown')+'</div>';
  }
}

function renderGantt(steps, manifest, selectedKey) {
  if (!steps || steps.length === 0) return '<div class="gantt-empty">No steps to chart.</div>';
  const executed = steps.filter(s => s.startedAt);
  if (executed.length === 0) return '<div class="gantt-empty">No step has started yet.</div>';
  const t0 = Math.min(...executed.map(s => new Date(s.startedAt).getTime()));
  const now = Date.now();
  const t1 = Math.max(...executed.map(s => s.completedAt ? new Date(s.completedAt).getTime() : now), t0 + 1);
  const span = Math.max(t1 - t0, 1);

  // Order by manifest level when available, else by startedAt
  let ordered = steps.slice();
  if (manifest && manifest.steps) {
    const mSteps = manifest.steps;
    const levels = {};
    function getLevel(key) {
      if (levels[key] !== undefined) return levels[key];
      const step = mSteps[key];
      if (!step || !step.runAfter || Object.keys(step.runAfter).length === 0) return (levels[key] = 0);
      let max = 0;
      for (const dep of Object.keys(step.runAfter)) if (mSteps[dep]) max = Math.max(max, getLevel(dep) + 1);
      return (levels[key] = max);
    }
    Object.keys(mSteps).forEach(k => getLevel(k));
    ordered.sort((a, b) => (levels[a.stepKey] ?? 99) - (levels[b.stepKey] ?? 99) || new Date(a.startedAt||0) - new Date(b.startedAt||0));
  } else {
    ordered.sort((a, b) => new Date(a.startedAt||0) - new Date(b.startedAt||0));
  }

  const rowH = 26, pad = 6, gutterW = 200, chartW = 640, axisH = 28, bottomPad = 12;
  const svgW = gutterW + chartW + 24;
  const svgH = axisH + ordered.length * (rowH + pad) + bottomPad;

  function barColor(status) {
    switch(status) {
      case 'Succeeded': return '#7bbf9a';
      case 'Failed':    return '#d97b6f';
      case 'Running':   return '#e8a769';
      case 'Skipped':   return '#c8c6bd';
      case 'Pending':   return '#d8d6cd';
      default:          return '#c8c6bd';
    }
  }

  let svg = '<div class="gantt-svg"><svg width="'+svgW+'" height="'+svgH+'" xmlns="http://www.w3.org/2000/svg">';
  svg += '<defs><pattern id="gantt-running-hatch" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="6" height="6" fill="#e8a769"/><line x1="0" y1="0" x2="0" y2="6" stroke="#faf9f5" stroke-width="2"/></pattern></defs>';

  // Axis ticks
  for (let i = 0; i <= 5; i++) {
    const x = gutterW + (i / 5) * chartW;
    const secs = ((span / 5) * i) / 1000;
    svg += '<g class="gantt-axis">'
      +'<line x1="'+x+'" y1="'+axisH+'" x2="'+x+'" y2="'+svgH+'" stroke-dasharray="2 3"/>'
      +'<text class="gantt-axis-tick" x="'+x+'" y="'+(axisH-8)+'" text-anchor="middle">+'+secs.toFixed(secs<10?1:0)+'s</text>'
      +'</g>';
  }

  ordered.forEach((s, idx) => {
    const y = axisH + idx * (rowH + pad);
    const started = s.startedAt ? new Date(s.startedAt).getTime() : t0;
    const ended   = s.completedAt ? new Date(s.completedAt).getTime() : (s.status === 'Running' ? now : started);
    let x = gutterW + ((started - t0) / span) * chartW;
    let w = Math.max(((ended - started) / span) * chartW, 2);
    let fill = barColor(s.status);
    let opacity = (s.status === 'Pending' || s.status === 'Skipped') ? 0.45 : 1;
    if (s.status === 'Pending' || s.status === 'Skipped') { x = gutterW; w = Math.max(chartW * 0.02, 6); }
    if (s.status === 'Running') fill = 'url(#gantt-running-hatch)';
    const selected = s.stepKey === selectedKey;
    const safeKey = s.stepKey.replace(/'/g, "\\'");
    svg += '<g onclick="selectStep(\''+safeKey+'\')" style="cursor:pointer">'
      +'<text class="gantt-gutter-label" x="10" y="'+(y+rowH*0.65)+'">'+esc(s.stepKey)+'</text>'
      +'<rect class="gantt-bar'+(selected?' gantt-bar-selected':'')+'" x="'+x+'" y="'+y+'" width="'+w+'" height="'+rowH+'"'
      +' rx="4" fill="'+fill+'" opacity="'+opacity+'"'
      +(selected?'':' stroke="rgba(0,0,0,0.04)"')+'>'
      +'<title>'+esc(s.stepKey)+' \u2014 '+stepStatusLabel(s.status)+' \u00b7 '+duration(s.startedAt, s.completedAt)+'</title>'
      +'</rect>'
      +'<text class="gantt-duration" x="'+(x+w+6)+'" y="'+(y+rowH*0.65)+'">'+duration(s.startedAt, s.completedAt)+'</text>'
      +'</g>';
  });

  svg += '</svg></div>';
  return svg;
}

function toggleWebhookSecret(cellId, btn) {
  const cell = document.getElementById(cellId);
  if (!cell || !btn) return;
  const secret = cell.getAttribute('data-secret');
  if (cell.dataset.visible === 'true') {
    cell.textContent = '\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022';
    cell.dataset.visible = 'false';
    btn.textContent = 'Show';
  } else {
    cell.textContent = secret;
    cell.dataset.visible = 'true';
    btn.textContent = 'Hide';
  }
}

// Runs
function renderRunDetailEmpty() {
  $('runs-detail').innerHTML = '<div class="detail-empty"><div class="icon">&#x2B21;</div><div>Select a run to see its steps</div></div>';
}

function showRunsListView() {
  runsView = 'list';
  $('runs-list-panel').classList.remove('hide');
  $('runs-detail-panel').classList.remove('show');
}

function showRunsDetailView() {
  runsView = 'detail';
  $('runs-list-panel').classList.add('hide');
  $('runs-detail-panel').classList.add('show');
}

async function backToRunsList() {
  selectedRunId = null;
  selectedStepKey = null;
  currentRun = null;
  currentRunManifest = null;
  closeEventsDrawer();
  history.replaceState(null, '', '#/runs');
  showRunsListView();
  if (allRuns.length === 0) await loadRuns();
  else renderRuns();
}

function isRunsAutoRefreshBlocked() {
  const list = $('runs-list');
  return (list && list.scrollTop > 24);
}

function onRunsFilterChange() {
  runsPage = 1;
  selectedRunId = null;
  selectedStepKey = null;
  // Persist filter state into the URL so refresh + back/forward retain it.
  const flow = $('runs-filter-flow').value;
  const status = $('runs-filter-status').value;
  const q = $('runs-filter-search').value.trim();
  Router.replaceParams({ flow, status, q, page: '1' });
  renderRunDetailEmpty();
  loadRuns();
}

function onRunsSearchInput() {
  if (runsSearchDebounceTimer) {
    clearTimeout(runsSearchDebounceTimer);
  }

  runsSearchDebounceTimer = setTimeout(() => {
    onRunsFilterChange();
  }, 300);
}

function onRunsSearchKeydown(event) {
  if (event.key !== 'Enter') return;
  if (runsSearchDebounceTimer) {
    clearTimeout(runsSearchDebounceTimer);
  }
  onRunsFilterChange();
}

async function changeRunsPage(delta) {
  const maxPage = Math.max(1, Math.ceil(runsTotal / runsPageSize));
  const nextPage = runsPage + delta;
  if (nextPage < 1 || nextPage > maxPage) return;

  runsPage = nextPage;
  Router.replaceParams({ page: String(runsPage) });
  await loadRuns();
}

async function gotoRunsPage(page) {
  const maxPage = Math.max(1, Math.ceil(runsTotal / runsPageSize));
  const target = Math.min(maxPage, Math.max(1, parseInt(page, 10) || 1));
  if (target === runsPage) return;
  runsPage = target;
  Router.replaceParams({ page: String(runsPage) });
  await loadRuns();
}

async function changeRunsPageSize(size) {
  const next = parseInt(size, 10);
  if (!runsPageSizeAllowed.includes(next) || next === runsPageSize) return;
  // Anchor on the first item of the current page so the user keeps the row they were looking at.
  const firstItemIdx = (runsPage - 1) * runsPageSize;
  runsPageSize = next;
  try { localStorage.setItem(runsPageSizeStorageKey, String(next)); } catch {}
  runsPage = Math.max(1, Math.floor(firstItemIdx / runsPageSize) + 1);
  Router.replaceParams({ size: String(runsPageSize), page: String(runsPage) });
  await loadRuns();
}

function onJumpToRunsPage(event) {
  if (event.key !== 'Enter') return;
  event.preventDefault();
  gotoRunsPage(event.target.value);
}

function buildPageNumbers(current, max) {
  // Numbered window with first/last anchors and ellipses. Always shows ≤ 7 entries.
  // Examples: 1 . 4 [5] 6 . 50    or    [1] 2 3 4 5 . 50    or    1 . 46 47 48 49 [50]
  if (max <= 7) {
    return Array.from({ length: max }, (_, i) => i + 1);
  }
  const pages = [1];
  if (current > 4) pages.push('...');
  const start = Math.max(2, current - 1);
  const end = Math.min(max - 1, current + 1);
  for (let i = start; i <= end; i++) pages.push(i);
  if (current < max - 3) pages.push('...');
  pages.push(max);
  return pages;
}

function renderRunsPagination() {
  const maxPage = Math.max(1, Math.ceil(runsTotal / runsPageSize));
  const hasRuns = runsTotal > 0;
  const start = hasRuns ? ((runsPage - 1) * runsPageSize) + 1 : 0;
  const end = hasRuns ? Math.min(runsPage * runsPageSize, runsTotal) : 0;

  const sizeOpts = runsPageSizeAllowed
    .map(n => '<option value="'+n+'"'+(n===runsPageSize?' selected':'')+'>'+n+'</option>')
    .join('');

  const pages = buildPageNumbers(runsPage, maxPage);
  const numberedHtml = pages.map(p => {
    if (p === '...') return '<span class="page-ellipsis" aria-hidden="true">…</span>';
    const active = p === runsPage ? ' page-num-active' : '';
    const aria = p === runsPage ? ' aria-current="page"' : '';
    return '<button class="page-num'+active+'" onclick="gotoRunsPage('+p+')"'+aria+'>'+p+'</button>';
  }).join('');

  const showJump = maxPage >= 8;
  const jumpHtml = showJump
    ? '<span class="runs-page-jump"><label class="runs-page-jump-label" for="runs-page-jump-input">Go to</label>'
      + '<input id="runs-page-jump-input" class="runs-page-jump-input" type="number" inputmode="numeric" min="1" max="'+maxPage+'" placeholder="'+runsPage+'" onkeydown="onJumpToRunsPage(event)" aria-label="Jump to page (1 to '+maxPage+')"></span>'
    : '';

  $('runs-pagination').innerHTML =
    '<div class="runs-page-left">'
    + '<div class="runs-page-info">'+(hasRuns ? ('Showing <b>'+start+'</b>–<b>'+end+'</b> of <b>'+runsTotal+'</b>') : 'No runs')+'</div>'
    + '<label class="runs-page-size"><span>Rows</span>'
    +   '<select class="filter-select runs-page-size-select" onchange="changeRunsPageSize(this.value)" aria-label="Rows per page">'+sizeOpts+'</select>'
    + '</label>'
    + '</div>'
    + '<nav class="runs-page-controls" aria-label="Pagination" role="navigation" onkeydown="onPaginationKeydown(event)">'
    +   '<button class="btn-page" onclick="changeRunsPage(-1)" aria-label="Previous page"'+(runsPage<=1?' disabled':'')+'>'
    +     '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><polyline points="15 18 9 12 15 6"/></svg>'
    +     '<span>Prev</span>'
    +   '</button>'
    +   '<div class="page-num-list" role="group" aria-label="Page numbers">'+numberedHtml+'</div>'
    +   '<button class="btn-page" onclick="changeRunsPage(1)" aria-label="Next page"'+(runsPage>=maxPage || !hasRuns?' disabled':'')+'>'
    +     '<span>Next</span>'
    +     '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><polyline points="9 18 15 12 9 6"/></svg>'
    +   '</button>'
    +   jumpHtml
    + '</nav>';
}

function onPaginationKeydown(event) {
  // Don't hijack arrow keys while typing in the jump-to-page input.
  const t = event.target;
  if (t && (t.tagName === 'INPUT' || t.tagName === 'SELECT' || t.tagName === 'TEXTAREA')) return;
  if (event.key === 'ArrowLeft') { event.preventDefault(); changeRunsPage(-1); }
  else if (event.key === 'ArrowRight') { event.preventDefault(); changeRunsPage(1); }
  else if (event.key === 'Home') { event.preventDefault(); gotoRunsPage(1); }
  else if (event.key === 'End') { event.preventDefault(); gotoRunsPage(Math.ceil(runsTotal / runsPageSize)); }
}

function renderRuns(preserveScroll) {
  const runsListEl = $('runs-list');
  const listScrollTop = preserveScroll ? runsListEl.scrollTop : 0;

  runsListEl.innerHTML = allRuns.length === 0
    ? '<div class="empty-msg">No runs found.</div>'
    : '<table class="runs-table"><thead><tr>'
      +'<th>Status</th><th>Run ID</th><th>Flow</th><th>Trigger</th><th>Started</th><th>Duration</th><th style="width:90px"></th>'
      +'</tr></thead><tbody>'
      + allRuns.map(r => {
        const active = r.id === selectedRunId ? 'run-row-active' : '';
        const trigger = r.triggerKey ? esc(r.triggerKey) : '<span style="color:var(--text-light)">—</span>';
        const dur = duration(r.startedAt, r.completedAt);
        const isRunning = r.status === 'Running';
        const hasFailed = r.status === 'Failed';
        const actions = '<div class="run-actions">'
          + (isRunning ? '<button class="run-action-btn danger" title="Cancel run" aria-label="Cancel run" onclick="event.stopPropagation();cancelRun(\''+r.id+'\', event)"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" aria-hidden="true"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg></button>' : '')
          + (hasFailed ? '<button class="run-action-btn" title="Re-run" aria-label="Re-run" onclick="event.stopPropagation();rerunAll(\''+r.id+'\', event)"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><polyline points="23 4 23 10 17 10"/><polyline points="1 20 1 14 7 14"/><path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15"/></svg></button>' : '')
          + '<button class="run-action-btn" title="Copy link" aria-label="Copy run link" onclick="event.stopPropagation();copyRunLink(\''+r.id+'\')"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/></svg></button>'
          + '</div>';
        return '<tr class="'+active+'" onclick="selectRun(\''+r.id+'\')">'
          +'<td><span class="run-status-badge badge-'+r.status.toLowerCase()+'">'+r.status+'</span></td>'
          +'<td class="run-id-cell">'+r.id.slice(0,8)+'…</td>'
          +'<td>'+esc(r.flowName||'Unknown')+'</td>'
          +'<td class="run-trigger-cell" title="'+esc(r.triggerKey||'')+'">'+trigger+'</td>'
          +'<td style="font-size:12px;color:var(--text-dim);white-space:nowrap">'+fmtDate(r.startedAt)+'</td>'
          +'<td class="run-duration-cell">'+(dur||'—')+'</td>'
          +'<td style="text-align:right;padding-right:14px">'+actions+'</td>'
          +'</tr>';
      }).join('')
      +'</tbody></table>';

  if (preserveScroll) {
    runsListEl.scrollTop = listScrollTop;
  }

  renderFilterChips();
  renderRunsPagination();
}

async function loadRuns(preserveScroll) {
  const listEl = $('runs-list');
  if (listEl) listEl.setAttribute('aria-busy', 'true');
  // Render skeleton on first paint (empty list and not preserving scroll position).
  if (listEl && !preserveScroll && !listEl.innerHTML.trim()) {
    listEl.innerHTML = '<table class="runs-table" aria-hidden="true"><thead><tr><th>Status</th><th>Run ID</th><th>Flow</th><th>Trigger</th><th>Started</th><th>Duration</th></tr></thead><tbody>'
      + Array.from({ length: 8 }).map(() =>
        '<tr><td><span class="skeleton skeleton-line" style="width:60px;height:14px"></span></td>'
        + '<td><span class="skeleton skeleton-line" style="width:80px"></span></td>'
        + '<td><span class="skeleton skeleton-line" style="width:120px"></span></td>'
        + '<td><span class="skeleton skeleton-line" style="width:80px"></span></td>'
        + '<td><span class="skeleton skeleton-line" style="width:100px"></span></td>'
        + '<td><span class="skeleton skeleton-line" style="width:50px"></span></td></tr>'
      ).join('') + '</tbody></table>';
  }
  const signal = newPageController().signal;
  try {
    const flowFilter = $('runs-filter-flow').value;
    const statusFilter = $('runs-filter-status').value;
    const searchFilter = $('runs-filter-search').value.trim();
    const skip = (runsPage - 1) * runsPageSize;
    let url = BASE+'/runs?includeTotal=true&take='+runsPageSize+'&skip='+skip;
    if (flowFilter) url += '&flowId='+encodeURIComponent(flowFilter);
    if (statusFilter) url += '&status='+encodeURIComponent(statusFilter);
    if (searchFilter) url += '&search='+encodeURIComponent(searchFilter);

    const page = await fetchJSON(url, { signal });
    allRuns = Array.isArray(page.items) ? page.items : [];
    runsTotal = typeof page.total === 'number' ? page.total : allRuns.length;

    const maxPage = Math.max(1, Math.ceil(runsTotal / runsPageSize));
    if (runsTotal > 0 && runsPage > maxPage) {
      runsPage = maxPage;
      Router.replaceParams({ page: String(runsPage) });
      await loadRuns(preserveScroll);
      return;
    }

    renderRuns(preserveScroll);
    if (listEl) listEl.setAttribute('aria-busy', 'false');
  } catch(e) {
    if (isAbortError(e)) return;
    console.error('Runs load error', e);
    if (listEl) {
      listEl.setAttribute('aria-busy', 'false');
      if (!preserveScroll) {
        listEl.innerHTML = '<div class="empty-msg empty-msg--error">Failed to load runs. <button class="btn-retry" onclick="loadRuns()">Retry</button></div>';
        showError('Failed to load runs', e.message);
      }
    }
  }
}

async function selectRun(id, preserveScroll, targetStepKey, view) {
  selectedRunId = id;
  selectedStepKey = targetStepKey || null;
  if (view && ['timeline','dag','gantt','events'].includes(view)) currentRunView = view;
  setRunRoute(id, selectedStepKey, currentRunView);

  showRunsDetailView();
  const detailEl = $('runs-detail');
  const detailScrollTop = preserveScroll && detailEl ? detailEl.scrollTop : 0;
  // Show immediate loading skeleton so the user sees feedback right away.
  if (!preserveScroll && detailEl) {
    detailEl.innerHTML = '<div style="display:flex;align-items:center;justify-content:center;min-height:260px;gap:10px;color:var(--text-dim)" aria-live="polite" aria-busy="true">'
      + '<div style="width:20px;height:20px;border:2px solid var(--border-dark);border-top-color:var(--accent);border-radius:50%;animation:spin .55s linear infinite;flex-shrink:0"></div>'
      + '<span style="font-size:13px">Loading run…</span>'
      + '</div>';
  }
  // Cancel any in-flight detail fetch — fixes race when user clicks a different run mid-load.
  const signal = newRunDetailController().signal;
  try {
    const run = await fetchJSON(BASE+'/runs/'+id, { signal });
    if (selectedRunId !== id) return; // user navigated away mid-fetch
    currentRun = run;
    const steps = run.steps || [];
    if (currentRunView === 'dag' || currentRunView === 'gantt') {
      currentRunManifest = await resolveFlowManifest(run);
    } else {
      currentRunManifest = currentRunManifest || null;
    }

    $('runs-detail').innerHTML =
      renderRunHeader(run, steps)
      +'<div class="tab-strip" id="run-tabs" role="tablist" aria-label="Run views">'+renderTabs(currentRunView)+'</div>'
      +'<div id="run-tab-body">'+renderActiveTab(run, currentRunManifest, currentRunView)+'</div>';

    if (preserveScroll) $('runs-detail').scrollTop = detailScrollTop;
    if (currentRunView === 'timeline' && selectedStepKey) scrollToStep(selectedStepKey);
    if (currentRunView === 'events') openEventsDrawer(id);
    else closeEventsDrawer();

    // Lineage — fire-and-forget; "Re-run of" link is in the header from server-side
    // sourceRunId; this fills in "Re-run as" descendants asynchronously.
    loadLineage(id, signal);
  } catch(e) {
    if (isAbortError(e)) return;
    console.error('Run detail error', e);
    if (detailEl && !preserveScroll) {
      detailEl.innerHTML = '<div class="empty-msg empty-msg--error">Failed to load run. <button class="btn-retry" onclick="selectRun(\''+id+'\')">Retry</button></div>';
      showError('Failed to load run', e.message);
    }
  }
}

async function loadLineage(runId, signal) {
  try {
    const lineage = await fetchJSON(BASE+'/runs/'+runId+'/lineage', signal ? { signal } : undefined);
    if (selectedRunId !== runId) return; // user navigated away
    const target = $('lineage-derived');
    if (!target) return;
    if (!lineage || !lineage.derived || lineage.derived.length === 0) {
      target.innerHTML = '';
      return;
    }
    const items = lineage.derived.map(d =>
      '<a href="#/runs/'+d.id+'" style="color:var(--accent);font-family:var(--font-mono);text-decoration:underline;margin-right:10px" title="'+esc(d.flowName||'')+' — '+esc(d.status)+'">'
      + d.id.substring(0,8) + '… <span style="color:var(--text-dim)">('+esc(d.status)+')</span>'
      + '</a>'
    ).join('');
    target.innerHTML = '<span>Re-run as: </span>' + items;
  } catch(e) { /* lineage is optional and might be aborted on navigation */ }
}

function renderTimeline(steps, runId) {
  let html = '<div class="timeline">';
  for (let i = 0; i < steps.length; i++) {
    const s = steps[i], last = i===steps.length-1;
    const icon = ({Succeeded:'\u2713',Failed:'\u2715',Running:'\u25cf',Skipped:'\u2298'})[s.status]||'\u25cb';
    const attemptCount = getStepAttemptCount(s);
    const sel = selectedStepKey === s.stepKey ? ' selected' : '';
    html += '<div class="step-node" data-step-key="'+esc(s.stepKey)+'"><div class="step-connector">'
      +'<div class="step-circle '+s.status+'">'+icon+'</div>'
      +'<div class="step-line '+(s.status==='Succeeded'?'done':'')+' '+(last?'last':'')+'"></div></div>'
      +'<div class="step-card '+s.status+sel+'">'
      +'<div class="step-card-header"><div><div class="step-key">'+esc(s.stepKey)+'</div>'+(s.status!=='Skipped'?'<div class="step-type">'+esc(s.stepType)+'</div>':'')+'</div>'
      +'<div style="display:flex;align-items:center;gap:6px"><span class="step-badge '+s.status+'">'+stepStatusLabel(s.status)+'</span>'
      +(attemptCount>1?'<span class="step-badge Pending">x'+attemptCount+' attempts</span>':'')
      +(s.status==='Failed'?'<button class="btn-retry" onclick="retryStep(\''+runId+'\',\''+esc(s.stepKey)+'\', event)">&#8635; Retry</button>':'')
      +(s.status==='Pending' && s.stepType==='WaitForSignal'?'<button class="btn-retry" onclick="sendSignal(\''+runId+'\',\''+esc(s.stepKey)+'\', event)">Send Signal</button>':'')
      +(s.status!=='Skipped'?'<button class="btn-copy-icon" onclick="copyStepLink(\''+runId+'\',\''+esc(s.stepKey)+'\')" title="Copy step link"><svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="2" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg></button>':'')
      +'</div></div>'
      +(s.status==='Skipped'?'':('<div class="step-timing"><span>Start: '+fmt(s.startedAt)+'</span><span>End: '+fmt(s.completedAt)+'</span><span>Duration: '+duration(s.startedAt,s.completedAt)+'</span></div>'))
      +renderStepDebugPanels(s)
      +'</div></div>';
  }
  return html + '</div>';
}

async function retryStep(runId, stepKey, ev) {
  if (!confirm('Retry step "'+stepKey+'"?')) return;
  await withBusy(ev && ev.currentTarget, async () => {
    try {
      const res = await fetch(BASE+'/runs/'+runId+'/steps/'+encodeURIComponent(stepKey)+'/retry', {method:'POST'});
      const data = await res.json();
      if (data.success) {
        showSuccess('Step retried');
        await selectRun(runId, true);
      } else {
        showError(data.error || 'Retry failed');
      }
    } catch(e) { showError('Failed to retry step', e.message); }
  });
}

async function sendSignal(runId, stepKey, ev) {
  // Resolve the configured signalName from the step input so the user does not have to know it.
  // BASE already ends in '/api' — do NOT prefix '/api/' again.
  let signalName = stepKey;
  try {
    const detail = await (await fetch(BASE+'/runs/'+runId)).json();
    const step = (detail.steps||[]).find(x=>x.stepKey===stepKey);
    if (step && step.inputJson) {
      try { const inputs = JSON.parse(step.inputJson); if (inputs && inputs.signalName) signalName = inputs.signalName; }
      catch {}
    }
  } catch {}

  const raw = prompt('Payload JSON for signal "'+signalName+'":', '{"approved":true}');
  if (raw === null) return;

  let body = raw && raw.trim() ? raw : '{}';
  try { JSON.parse(body); }
  catch (e) { showError('Invalid JSON', e.message); return; }

  await withBusy(ev && ev.currentTarget, async () => {
    try {
      const res = await fetch(BASE+'/runs/'+runId+'/signals/'+encodeURIComponent(signalName), {
        method: 'POST',
        headers: {'Content-Type':'application/json'},
        body: body
      });
      const data = await res.json();
      if (res.ok && data.delivered) {
        showSuccess('Signal delivered');
        await selectRun(runId, true);
      } else if (res.status === 409) {
        showToast('Signal already delivered to step "'+(data.stepKey||stepKey)+'"', 'warning');
      } else {
        showError(data.error || ('Signal delivery failed (status '+res.status+')'));
      }
    } catch(e) { showError('Failed to send signal', e.message); }
  });
}

// Scheduled Jobs
let allSchedules = [];

function scheduleBadge(state) {
  if (!state) return '<span class="badge badge-paused">\u2014</span>';
  const s = state.toLowerCase();
  if (s === 'succeeded') return '<span class="badge badge-succeeded">'+state+'</span>';
  if (s === 'failed') return '<span class="badge badge-failed">'+state+'</span>';
  if (s === 'skipped') return '<span class="badge badge-skipped">'+state+'</span>';
  if (s === 'processing' || s === 'enqueued') return '<span class="badge badge-running">'+state+'</span>';
  return '<span class="badge badge-paused">'+state+'</span>';
}

function renderScheduleTable(jobs, forFlow) {
  if (jobs.length === 0) return '<div class="empty-msg">No scheduled jobs'+(forFlow?' for this flow':'')+'.</div>';
  let html = '<table class="schedule-table"><thead><tr>';
  if (!forFlow) html += '<th>Flow</th>';
  html += '<th>Trigger</th><th>Cron Expression</th><th>Next Run</th><th>Last Run</th><th>Last Status</th><th>Actions</th></tr></thead><tbody>';
  for (const j of jobs) {
    html += '<tr>';
    if (!forFlow) html += '<td style="font-weight:600">'+esc(j.flowName)+'</td>';
    html += '<td class="mono" style="font-family:\'JetBrains Mono\',monospace;font-size:12px;color:var(--accent)">'+esc(j.triggerKey)+'</td>'
      +'<td><span class="badge-cron">'+esc(j.cron)+'</span></td>'
      +'<td style="font-size:12px;color:var(--text-dim)">'+fmtDate(j.nextExecution)+'</td>'
      +'<td style="font-size:12px;color:var(--text-dim)">'+fmtDate(j.lastExecution)+'</td>'
      +'<td>'+scheduleBadge(j.lastJobState)+'</td>'
      +'<td><div class="schedule-actions">'
      +'<button class="btn-sm btn-sm-trigger" onclick="triggerScheduledJob(\''+esc(j.jobId)+'\', event)" title="Trigger now">&#9889;</button>'
      +(j.paused
        ?'<button class="btn-sm btn-sm-success" onclick="resumeScheduledJob(\''+esc(j.jobId)+'\', event)" title="Resume">&#9654;</button>'
        :'<button class="btn-sm btn-sm-warning" onclick="pauseScheduledJob(\''+esc(j.jobId)+'\', event)" title="Pause">&#10074;&#10074;</button>')
      +'</div></td></tr>';
  }
  return html + '</tbody></table>';
}

async function loadScheduled(opts) {
  const silent = !!(opts && opts.silent);
  const tableEl = $('scheduled-table');
  if (!silent && tableEl && !tableEl.innerHTML.trim()) {
    tableEl.innerHTML = skeletonRows(5, 6);
  }
  const signal = newPageController().signal;
  try {
    allSchedules = await fetchJSON(BASE+'/schedules', { signal });
    $('scheduled-count-label').innerHTML = '<span class="num">' + allSchedules.length + '</span>job' + (allSchedules.length!==1?'s':'');
    tableEl.innerHTML = renderScheduleTable(allSchedules, false);
  } catch(e) {
    if (isAbortError(e)) return;
    console.error('Scheduled load error', e);
    if (!silent && tableEl) {
      tableEl.innerHTML = '<div class="empty-msg empty-msg--error">Failed to load scheduled jobs. <button class="btn-retry" onclick="loadScheduled()">Retry</button></div>';
      showError('Failed to load scheduled jobs', e.message);
    }
  }
}

async function loadFlowSchedule() {
  if (!selectedFlowDetail) return;
  try {
    const schedules = await fetchJSON(BASE+'/schedules');
    const flowJobs = schedules.filter(j => j.flowId === selectedFlowDetail.id);
    $('fd-schedule').innerHTML = flowJobs.length === 0
      ? '<div class="empty-msg">No scheduled jobs for this flow. Add a Cron trigger to enable scheduling.</div>'
      : renderScheduleTable(flowJobs, true);
  } catch(e) {
    $('fd-schedule').innerHTML = '<div class="empty-msg">Failed to load schedule data.</div>';
  }
}

async function triggerScheduledJob(jobId, ev) {
  if (!confirm('Trigger job "'+jobId+'" now?')) return;
  await withBusy(ev && ev.currentTarget, async () => {
    try {
      const res = await fetch(BASE+'/schedules/'+encodeURIComponent(jobId)+'/trigger', {method:'POST'});
      const data = await res.json();
      if (data.success) {
        showSuccess('Job triggered');
        await loadScheduled();
        if (selectedFlowDetail) await loadFlowSchedule();
      } else { showError(data.error || 'Trigger failed'); }
    } catch(e) { showError('Failed to trigger', e.message); }
  });
}

async function pauseScheduledJob(jobId, ev) {
  if (!confirm('Pause scheduled job? You can resume it later.')) return;
  await withBusy(ev && ev.currentTarget, async () => {
    try {
      const res = await fetch(BASE+'/schedules/'+encodeURIComponent(jobId)+'/pause', {method:'POST'});
      const data = await res.json();
      if (data.success) {
        showSuccess('Schedule paused');
        await loadScheduled();
        if (selectedFlowDetail) await loadFlowSchedule();
      } else { showError(data.error || 'Pause failed'); }
    } catch(e) { showError('Failed to pause', e.message); }
  });
}

async function resumeScheduledJob(jobId, ev) {
  await withBusy(ev && ev.currentTarget, async () => {
    try {
      const res = await fetch(BASE+'/schedules/'+encodeURIComponent(jobId)+'/resume', {method:'POST'});
      const data = await res.json();
      if (data.success) {
        showSuccess('Schedule resumed');
        await loadScheduled();
        if (selectedFlowDetail) await loadFlowSchedule();
      } else { showError(data.error || 'Resume failed'); }
    } catch(e) { showError('Failed to resume', e.message); }
  });
}

// ── Smart auto-refresh pause ──────────────────────────────────────────
// Replaces the old scrollTop-only heuristic. Now we pause when:
//   • the tab is hidden (Page Visibility API),
//   • the user has focus inside an editable form control,
//   • the user is mid-scroll in the runs list (preserves position).
function isAutoRefreshBlocked() {
  if (document.visibilityState === 'hidden') return true;
  const ae = document.activeElement;
  if (ae) {
    const tag = ae.tagName;
    if (tag === 'INPUT' && !['checkbox','radio','range','submit','button','reset'].includes(ae.type)) return true;
    if (tag === 'TEXTAREA') return true;
    if (ae.isContentEditable) return true;
  }
  if (currentPage === 'runs' && runsView === 'list') {
    const list = $('runs-list');
    if (list && list.scrollTop > 24) return true;
  }
  return false;
}
// Keep legacy alias so any third-party caller of the old name still works.
function isRunsAutoRefreshBlocked() { return isAutoRefreshBlocked(); }

// Refresh logic — silent (no skeleton flash) for auto-refresh ticks.
// `opts.force === true` bypasses the pause heuristic; used by bootstrap and
// by the visibilitychange handler so initial paint + tab-resume don't get
// gated behind document.visibilityState === 'hidden' (true during headless
// automation and some iframe / background-tab scenarios).
async function refresh(opts) {
  const force = !!(opts && opts.force);
  const silent = opts && opts.silent !== undefined ? !!opts.silent : !force;
  if (!force && isAutoRefreshBlocked()) return;
  try {
    if (currentPage === 'overview') await loadOverview({ silent });
    if (currentPage === 'flows') await loadFlows({ silent });
    if (currentPage === 'runs') {
      if (runsView === 'detail' && selectedRunId) {
        await selectRun(selectedRunId, true, selectedStepKey, currentRunView);
      } else {
        await loadRuns(true);
      }
    }
    if (currentPage === 'scheduled') await loadScheduled({ silent });
  } catch(e) { if (!isAbortError(e)) console.error('refresh', e); }
}

document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'hidden') {
    // Drop the SSE connection while the tab is hidden — no point keeping a server
    // resource open for a viewer who can't see updates. Reopened on visibility return.
    FlowEventStream.close();
    stopFallbackPolling();
    return;
  }
  if (autoRefreshEnabled) {
    FlowEventStream.connect();
    // Single snapshot refresh so the user immediately sees the current state; SSE
    // delivers deltas from this point forward.
    refresh({ force: true });
  }
});

// Window focus / pageshow fire when the user alt-tabs back to the browser, or returns
// from BFCache. SSE may have been killed by the OS during sleep; reconnecting here is cheap.
window.addEventListener('focus', () => {
  if (autoRefreshEnabled) {
    FlowEventStream.connect();
    refresh({ force: true });
  }
});
window.addEventListener('pageshow', (e) => {
  if (e.persisted && autoRefreshEnabled) {
    FlowEventStream.connect();
    refresh({ force: true });
  }
});

// ── Router — single IIFE owning hash routing, query params, subscribers ──
const Router = (function () {
  const subscribers = [];
  let _suppressNext = false;

  function parse() {
    const hash = location.hash || '';
    if (!hash || hash === '#' || hash === '#/') {
      return { page: 'overview', runId: null, stepKey: null, view: null, params: {} };
    }
    const raw = hash.startsWith('#/') ? hash.slice(2) : hash.slice(1);
    const qIdx = raw.indexOf('?');
    const path = qIdx >= 0 ? raw.slice(0, qIdx) : raw;
    const query = qIdx >= 0 ? raw.slice(qIdx + 1) : '';
    const sp = new URLSearchParams(query);
    const params = {};
    for (const [k, v] of sp.entries()) params[k] = v;
    let view = null;
    if (['timeline','dag','gantt','events'].includes(params.view)) view = params.view;
    const parts = path.split('/');
    const page = ['overview','flows','runs','scheduled'].includes(parts[0]) ? parts[0] : 'overview';
    if (page !== 'runs') return { page, runId: null, stepKey: null, view: null, params };
    const runId = parts[1] || null;
    const stepKey = (parts[2] === 'steps' && parts[3]) ? decodeURIComponent(parts[3]) : null;
    return { page, runId, stepKey, view, params };
  }

  function _build(route) {
    let hash = '#/' + (route.page || 'overview');
    if (route.page === 'runs' && route.runId) {
      hash = '#/runs/' + route.runId;
      if (route.stepKey) hash += '/steps/' + encodeURIComponent(route.stepKey);
    }
    const params = route.params || {};
    const sp = new URLSearchParams();
    for (const [k, v] of Object.entries(params)) {
      if (v === null || v === undefined || v === '') continue;
      sp.set(k, String(v));
    }
    if (route.view && route.view !== 'timeline') sp.set('view', route.view);
    const qs = sp.toString();
    if (qs) hash += '?' + qs;
    return hash;
  }

  // Replace the URL without firing the change subscribers — for filter/page state.
  function replaceParams(patch) {
    const cur = parse();
    const merged = Object.assign({}, cur.params, patch);
    // Strip empty / null entries.
    for (const k of Object.keys(merged)) {
      if (merged[k] === null || merged[k] === undefined || merged[k] === '') delete merged[k];
    }
    const next = _build(Object.assign({}, cur, { params: merged }));
    if (location.hash !== next) {
      _suppressNext = true;
      history.replaceState(null, '', next);
    }
  }

  // Set the run-detail route (run id + step key + view). Uses replaceState so it
  // does not pollute history with every step click.
  function setRunRoute(runId, stepKey, view) {
    const cur = parse();
    const next = _build({ page: 'runs', runId, stepKey, view, params: cur.params });
    if (location.hash !== next) {
      _suppressNext = true;
      history.replaceState(null, '', next);
    }
  }

  function go(page) {
    const next = _build({ page, params: {} });
    if (location.hash !== next) {
      history.pushState(null, '', next);
      _emit();
    }
  }

  function on(cb) { subscribers.push(cb); }

  function _emit() {
    const r = parse();
    for (const cb of subscribers) {
      try { cb(r); } catch (e) { console.error('Router subscriber threw', e); }
    }
  }

  window.addEventListener('hashchange', () => {
    if (_suppressNext) { _suppressNext = false; return; }
    _emit();
  });

  return { parse, go, on, replaceParams, setRunRoute };
})();

// Back-compat shim — older inline helpers still call setRunRoute / parseHash.
function setRunRoute(runId, stepKey, view) { Router.setRunRoute(runId, stepKey, view); }
function parseHash() { return Router.parse(); }

async function applyRoute(route) {
  _navigate(route.page);
  if (route.page === 'runs') {
    if (route.runId) {
      await selectRun(route.runId, false, route.stepKey, route.view);
    } else {
      showRunsListView();
      // Pre-fill filter inputs from URL params before loadRuns reads them.
      restoreRunsFiltersFromRoute(route.params || {});
      await loadRuns(false);
    }
    return;
  }
  // For non-runs pages, fire an immediate refresh so the user sees data right away
  // instead of waiting up to one auto-refresh interval (5s by default). Pre-1.22.1 the
  // page would render blank until the next refresh tick because applyRoute did not
  // call any per-page loader for overview / flows / scheduled.
  await refresh({ force: true });
}

function restoreRunsFiltersFromRoute(params) {
  const flow = params.flow || '';
  const status = params.status || '';
  const q = params.q || '';
  const page = parseInt(params.page || '1', 10);
  const sizeParam = parseInt(params.size || '', 10);
  if (runsPageSizeAllowed.includes(sizeParam)) {
    runsPageSize = sizeParam;
    try { localStorage.setItem(runsPageSizeStorageKey, String(sizeParam)); } catch {}
  }
  const flowEl = $('runs-filter-flow');
  const statusEl = $('runs-filter-status');
  const searchEl = $('runs-filter-search');
  if (flowEl && flowEl.value !== flow) flowEl.value = flow;
  if (statusEl && statusEl.value !== status) statusEl.value = status;
  if (searchEl && searchEl.value !== q) searchEl.value = q;
  if (Number.isFinite(page) && page >= 1) runsPage = page;
}

Router.on(applyRoute);

// Cleanup: kill the polling timer on tab close so it doesn't fire during unload.
window.addEventListener('beforeunload', () => {
  if (autoRefreshTimer) { clearInterval(autoRefreshTimer); autoRefreshTimer = null; }
  if (pageController) { try { pageController.abort(); } catch {} }
  if (runDetailController) { try { runDetailController.abort(); } catch {} }
});

_initThemeAndDensityUI();
initAutoRefreshSettings();
const _initialRoute = Router.parse();
if (_initialRoute.page !== 'overview' || _initialRoute.runId) {
  applyRoute(_initialRoute);
}
// First paint must always happen — bypass the visibility / focus pause
// heuristic so the page renders immediately on load, even in background tabs.
if (!_initialRoute.runId && _initialRoute.page === 'overview') refresh({ force: true });
