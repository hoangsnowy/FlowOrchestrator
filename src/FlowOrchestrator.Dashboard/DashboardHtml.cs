namespace FlowOrchestrator.Dashboard;

internal static class DashboardHtml
{
    public static string Render(string basePath)
    {
        var bp = basePath.TrimEnd('/');
        return Template.Replace("{{BASE_PATH}}", bp);
    }

    private const string Template = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1.0"/>
<title>FlowOrchestrator Dashboard</title>
<style>
@import url('https://fonts.googleapis.com/css2?family=JetBrains+Mono:wght@400;600&family=Syne:wght@400;700;800&display=swap');
:root{--bg:#0b0d14;--surface:#12151f;--surface2:#181c2a;--border:#1e2435;--accent:#6c63ff;--accent2:#00e5c0;--warn:#ffb347;--danger:#ff5370;--success:#69ff94;--muted:#4a5175;--text:#c8cde8;--text-dim:#6a7199;--sidebar-w:220px;--radius:10px}
*{box-sizing:border-box;margin:0;padding:0}
body{background:var(--bg);color:var(--text);font-family:'Syne',sans-serif;min-height:100vh;display:flex}
a{color:inherit;text-decoration:none}
button{font-family:inherit;cursor:pointer;border:none;outline:none}
::-webkit-scrollbar{width:6px;height:6px}::-webkit-scrollbar-track{background:transparent}::-webkit-scrollbar-thumb{background:var(--border);border-radius:3px}

.sidebar{width:var(--sidebar-w);background:var(--surface);border-right:1px solid var(--border);display:flex;flex-direction:column;position:fixed;top:0;bottom:0;left:0;z-index:10}
.sidebar-brand{padding:20px 18px;display:flex;align-items:center;gap:12px;border-bottom:1px solid var(--border)}
.sidebar-brand .logo{width:34px;height:34px;background:linear-gradient(135deg,var(--accent),var(--accent2));border-radius:9px;display:flex;align-items:center;justify-content:center;font-size:16px;flex-shrink:0}
.sidebar-brand h1{font-size:14px;font-weight:800;letter-spacing:.3px;line-height:1.2}
.sidebar-brand span{display:block;font-size:10px;font-weight:400;color:var(--text-dim);margin-top:2px}
.sidebar-nav{flex:1;padding:12px 0}
.nav-item{display:flex;align-items:center;gap:10px;padding:10px 18px;font-size:13px;font-weight:600;color:var(--text-dim);transition:all .15s;border-left:3px solid transparent;cursor:pointer}
.nav-item:hover{color:var(--text);background:rgba(108,99,255,.06)}
.nav-item.active{color:var(--accent);border-left-color:var(--accent);background:rgba(108,99,255,.1)}
.nav-item svg{width:18px;height:18px;flex-shrink:0}
.sidebar-footer{padding:14px 18px;border-top:1px solid var(--border);display:flex;align-items:center;gap:8px;font-family:'JetBrains Mono',monospace;font-size:10px;color:var(--text-dim)}
.pulse-dot{width:7px;height:7px;border-radius:50%;background:var(--success);animation:pulse 2s infinite}
@keyframes pulse{0%,100%{opacity:1}50%{opacity:.4}}

.main-area{margin-left:var(--sidebar-w);flex:1;display:flex;flex-direction:column;min-height:100vh}
.page{display:none;flex:1;flex-direction:column;overflow:hidden}
.page.active{display:flex}
.page-header{padding:20px 28px;border-bottom:1px solid var(--border);display:flex;align-items:center;justify-content:space-between}
.page-title{font-size:18px;font-weight:800}
.page-content{flex:1;overflow-y:auto;padding:24px 28px}

/* Overview */
.stats-grid{display:grid;grid-template-columns:repeat(4,1fr);gap:16px;margin-bottom:28px}
.stat-card{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);padding:20px;transition:border-color .2s}
.stat-card:hover{border-color:var(--accent)}
.stat-card .val{font-size:32px;font-weight:800;line-height:1}
.stat-card .lbl{font-size:11px;color:var(--text-dim);margin-top:6px;text-transform:uppercase;letter-spacing:.8px}

.recent-section{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);overflow:hidden}
.recent-header{padding:14px 18px;border-bottom:1px solid var(--border);font-size:12px;font-weight:700;text-transform:uppercase;letter-spacing:1px;color:var(--text-dim)}
.recent-table{width:100%;border-collapse:collapse}
.recent-table th{text-align:left;padding:10px 14px;font-size:10px;color:var(--text-dim);text-transform:uppercase;letter-spacing:.5px;border-bottom:1px solid var(--border)}
.recent-table td{padding:10px 14px;font-size:12px;border-bottom:1px solid var(--border)}
.recent-table tr:last-child td{border-bottom:none}
.recent-table tr:hover td{background:rgba(108,99,255,.04)}

/* Flows */
.flows-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(340px,1fr));gap:16px;margin-bottom:24px}
.flow-card{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);padding:18px;cursor:pointer;transition:all .2s}
.flow-card:hover{border-color:var(--accent);transform:translateY(-2px)}
.flow-card-header{display:flex;align-items:center;justify-content:space-between;margin-bottom:10px}
.flow-card-name{font-size:15px;font-weight:700}
.flow-card-version{font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--text-dim)}
.flow-card-meta{display:flex;gap:14px;font-size:11px;color:var(--text-dim);margin-bottom:10px}
.flow-card-footer{display:flex;align-items:center;justify-content:space-between}
.badge{font-size:10px;font-weight:700;padding:3px 10px;border-radius:20px;text-transform:uppercase;letter-spacing:.5px}
.badge-enabled{background:rgba(105,255,148,.12);color:var(--success)}
.badge-disabled{background:rgba(255,83,112,.12);color:var(--danger)}
.badge-running{background:rgba(255,179,71,.15);color:var(--warn)}
.badge-succeeded{background:rgba(105,255,148,.15);color:var(--success)}
.badge-failed{background:rgba(255,83,112,.15);color:var(--danger)}

.flow-detail-panel{display:none;flex-direction:column;flex:1;overflow:hidden}
.flow-detail-panel.show{display:flex}
.flow-list-panel{display:flex;flex-direction:column;flex:1}
.flow-list-panel.hide{display:none}
.back-btn{font-size:12px;color:var(--accent);cursor:pointer;display:flex;align-items:center;gap:6px;padding:4px 0;font-weight:600}
.back-btn:hover{text-decoration:underline}
.flow-actions{display:flex;gap:8px}
.btn{font-size:12px;font-weight:700;padding:8px 16px;border-radius:8px;transition:all .15s}
.btn-primary{background:var(--accent);color:#fff}.btn-primary:hover{background:#5b52ff}
.btn-success{background:rgba(105,255,148,.15);color:var(--success);border:1px solid rgba(105,255,148,.25)}.btn-success:hover{background:rgba(105,255,148,.25)}
.btn-danger{background:rgba(255,83,112,.15);color:var(--danger);border:1px solid rgba(255,83,112,.25)}.btn-danger:hover{background:rgba(255,83,112,.25)}
.btn-ghost{background:transparent;color:var(--text-dim);border:1px solid var(--border)}.btn-ghost:hover{border-color:var(--accent);color:var(--accent)}

.detail-tabs{display:flex;border-bottom:1px solid var(--border);padding:0 28px;gap:0}
.detail-tab{padding:12px 16px;font-size:12px;font-weight:700;color:var(--text-dim);cursor:pointer;border-bottom:2px solid transparent;transition:all .15s}
.detail-tab:hover{color:var(--text)}.detail-tab.active{color:var(--accent);border-bottom-color:var(--accent)}
.tab-content{display:none;flex:1;overflow-y:auto;padding:20px 28px}
.tab-content.active{display:block}

.manifest-table{width:100%;border-collapse:collapse;font-size:12px}
.manifest-table th{text-align:left;padding:8px 12px;background:var(--surface2);color:var(--text-dim);font-size:10px;text-transform:uppercase;letter-spacing:.5px}
.manifest-table td{padding:8px 12px;border-bottom:1px solid var(--border)}
.manifest-table td.mono{font-family:'JetBrains Mono',monospace;font-size:11px}

.dag-container{padding:20px;min-height:200px;position:relative}
.dag-node{display:inline-flex;align-items:center;gap:8px;background:var(--surface);border:1px solid var(--border);border-radius:8px;padding:10px 14px;font-size:12px;position:absolute;transition:border-color .2s}
.dag-node:hover{border-color:var(--accent)}
.dag-node .type-badge{font-family:'JetBrains Mono',monospace;font-size:10px;color:var(--accent);background:rgba(108,99,255,.12);padding:2px 6px;border-radius:4px}

.json-viewer{background:var(--surface2);border:1px solid var(--border);border-radius:var(--radius);padding:16px;font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--accent2);white-space:pre-wrap;word-break:break-all;max-height:400px;overflow-y:auto;line-height:1.6}

/* Runs */
.runs-filters{display:flex;gap:10px;margin-bottom:16px;flex-wrap:wrap}
.filter-select,.filter-input{background:var(--surface);border:1px solid var(--border);border-radius:8px;padding:8px 12px;color:var(--text);font-size:12px;font-family:'Syne',sans-serif;outline:none}
.filter-select:focus,.filter-input:focus{border-color:var(--accent)}
.filter-select option{background:var(--surface)}
.runs-split{display:flex;gap:0;flex:1;overflow:hidden;min-height:0}
.runs-list-col{width:400px;border-right:1px solid var(--border);overflow-y:auto;flex-shrink:0}
.runs-detail-col{flex:1;overflow-y:auto}
.run-item{padding:12px 16px;border-bottom:1px solid var(--border);cursor:pointer;display:flex;align-items:flex-start;gap:10px;transition:background .15s}
.run-item:hover{background:var(--surface)}.run-item.active{background:rgba(108,99,255,.1);border-left:3px solid var(--accent)}
.status-dot{width:10px;height:10px;border-radius:50%;margin-top:4px;flex-shrink:0}
.status-dot.Running{background:var(--warn);animation:pulse 1.5s infinite}.status-dot.Succeeded{background:var(--success)}.status-dot.Failed{background:var(--danger)}
.run-info{flex:1;min-width:0}
.run-id{font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--accent2);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.run-meta{font-size:11px;color:var(--text-dim);margin-top:2px}
.run-status-badge{font-size:10px;font-weight:700;padding:2px 7px;border-radius:4px;text-transform:uppercase;flex-shrink:0}

.detail-empty{display:flex;align-items:center;justify-content:center;flex-direction:column;gap:10px;color:var(--text-dim);min-height:300px}.detail-empty .icon{font-size:48px;opacity:.3}
.detail-header{padding:16px 20px;border-bottom:1px solid var(--border);background:var(--surface)}
.detail-runid{font-family:'JetBrains Mono',monospace;font-size:13px;color:var(--accent2)}

.timeline{display:flex;flex-direction:column}
.step-node{display:flex;align-items:stretch}
.step-connector{display:flex;flex-direction:column;align-items:center;width:32px;flex-shrink:0}
.step-circle{width:28px;height:28px;border-radius:50%;border:2px solid var(--border);display:flex;align-items:center;justify-content:center;font-size:11px;font-weight:700;flex-shrink:0;transition:all .3s}
.step-circle.Running{border-color:var(--warn);color:var(--warn);box-shadow:0 0 10px rgba(255,179,71,.4);animation:pulse 1.5s infinite}
.step-circle.Succeeded{border-color:var(--success);color:var(--success);background:rgba(105,255,148,.1)}
.step-circle.Failed{border-color:var(--danger);color:var(--danger);background:rgba(255,83,112,.1)}
.step-circle.Pending{border-color:var(--muted);color:var(--muted)}
.step-line{width:2px;flex:1;min-height:16px;background:var(--border)}.step-line.done{background:var(--success)}.step-line.last{display:none}
.step-card{flex:1;background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);padding:12px 16px;margin-bottom:12px;margin-left:10px;transition:border-color .2s}
.step-card.Running{border-color:var(--warn)}.step-card.Succeeded{border-color:rgba(105,255,148,.3)}.step-card.Failed{border-color:var(--danger)}
.step-card-header{display:flex;align-items:center;justify-content:space-between}
.step-key{font-family:'JetBrains Mono',monospace;font-size:13px;font-weight:600;color:#fff}.step-type{font-size:11px;color:var(--text-dim);margin-top:2px}
.step-badge{font-size:10px;font-weight:700;padding:3px 8px;border-radius:4px;text-transform:uppercase}
.step-badge.Running{background:rgba(255,179,71,.15);color:var(--warn)}.step-badge.Succeeded{background:rgba(105,255,148,.15);color:var(--success)}.step-badge.Failed{background:rgba(255,83,112,.15);color:var(--danger)}.step-badge.Pending{background:rgba(74,81,117,.2);color:var(--muted)}
.step-timing{margin-top:8px;font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--text-dim);display:flex;gap:16px;flex-wrap:wrap}
.step-error{margin-top:8px;padding:8px 10px;background:rgba(255,83,112,.08);border:1px solid rgba(255,83,112,.2);border-radius:6px;font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--danger);word-break:break-all}
.step-output{margin-top:8px;padding:8px 10px;background:rgba(0,0,0,.3);border:1px solid var(--border);border-radius:6px;font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--accent2);word-break:break-all;max-height:80px;overflow-y:auto}
.empty-msg{padding:32px;text-align:center;color:var(--text-dim);font-size:13px}

.dag-svg{width:100%;overflow:auto}
.dag-svg svg text{font-family:'JetBrains Mono',monospace;font-size:11px}
</style>
</head>
<body>
<!-- Sidebar -->
<aside class="sidebar">
  <div class="sidebar-brand">
    <div class="logo">&#9889;</div>
    <h1>FlowOrchestrator<span>Dashboard</span></h1>
  </div>
  <nav class="sidebar-nav">
    <div class="nav-item active" data-page="overview" onclick="navigate('overview')">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/></svg>
      Overview
    </div>
    <div class="nav-item" data-page="flows" onclick="navigate('flows')">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="22 12 18 12 15 21 9 3 6 12 2 12"/></svg>
      Flows
    </div>
    <div class="nav-item" data-page="runs" onclick="navigate('runs')">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/></svg>
      Runs
    </div>
  </nav>
  <div class="sidebar-footer"><span class="pulse-dot"></span> Auto-refresh 3s</div>
</aside>

<div class="main-area">
  <!-- Overview Page -->
  <div class="page active" id="page-overview">
    <div class="page-header"><div class="page-title">Overview</div></div>
    <div class="page-content">
      <div class="stats-grid">
        <div class="stat-card"><div class="val" id="ov-flows" style="color:var(--accent)">-</div><div class="lbl">Registered Flows</div></div>
        <div class="stat-card"><div class="val" id="ov-active" style="color:var(--warn)">-</div><div class="lbl">Active Runs</div></div>
        <div class="stat-card"><div class="val" id="ov-done" style="color:var(--success)">-</div><div class="lbl">Completed Today</div></div>
        <div class="stat-card"><div class="val" id="ov-fail" style="color:var(--danger)">-</div><div class="lbl">Failed Today</div></div>
      </div>
      <div class="stats-grid" style="grid-template-columns:repeat(2,1fr)">
        <div class="recent-section">
          <div class="recent-header">Registered Flows</div>
          <div id="ov-flows-table"></div>
        </div>
        <div class="recent-section">
          <div class="recent-header">Recent Runs</div>
          <div id="ov-runs-table"></div>
        </div>
      </div>
    </div>
  </div>

  <!-- Flows Page -->
  <div class="page" id="page-flows">
    <div class="flow-list-panel" id="flow-list-panel">
      <div class="page-header">
        <div class="page-title">Flows</div>
        <div style="font-size:12px;color:var(--text-dim)" id="flow-count-label">0 flows</div>
      </div>
      <div class="page-content">
        <div class="flows-grid" id="flows-grid"></div>
      </div>
    </div>
    <div class="flow-detail-panel" id="flow-detail-panel">
      <div class="page-header">
        <div>
          <div class="back-btn" onclick="closeFlowDetail()">&#8592; Back to Flows</div>
          <div class="page-title" id="fd-name">Flow Name</div>
        </div>
        <div class="flow-actions" id="fd-actions"></div>
      </div>
      <div class="detail-tabs">
        <div class="detail-tab active" data-tab="fd-manifest" onclick="switchFlowTab('fd-manifest')">Manifest</div>
        <div class="detail-tab" data-tab="fd-steps" onclick="switchFlowTab('fd-steps')">Steps</div>
        <div class="detail-tab" data-tab="fd-triggers" onclick="switchFlowTab('fd-triggers')">Triggers</div>
        <div class="detail-tab" data-tab="fd-dag" onclick="switchFlowTab('fd-dag')">DAG</div>
        <div class="detail-tab" data-tab="fd-json" onclick="switchFlowTab('fd-json')">Raw JSON</div>
      </div>
      <div class="tab-content active" id="fd-manifest"></div>
      <div class="tab-content" id="fd-steps"></div>
      <div class="tab-content" id="fd-triggers"></div>
      <div class="tab-content" id="fd-dag"></div>
      <div class="tab-content" id="fd-json"></div>
    </div>
  </div>

  <!-- Runs Page -->
  <div class="page" id="page-runs">
    <div class="page-header">
      <div class="page-title">Runs</div>
      <div class="runs-filters">
        <select class="filter-select" id="runs-filter-flow" onchange="loadRuns()"><option value="">All Flows</option></select>
        <select class="filter-select" id="runs-filter-status" onchange="filterRuns()">
          <option value="">All Statuses</option>
          <option value="Running">Running</option>
          <option value="Succeeded">Succeeded</option>
          <option value="Failed">Failed</option>
        </select>
      </div>
    </div>
    <div class="runs-split">
      <div class="runs-list-col" id="runs-list"></div>
      <div class="runs-detail-col" id="runs-detail">
        <div class="detail-empty"><div class="icon">&#x2B21;</div><div>Select a run to see its steps</div></div>
      </div>
    </div>
  </div>
</div>

<script>
const BASE = '{{BASE_PATH}}/api';
let currentPage = 'overview';
let allFlows = [];
let allRuns = [];
let selectedRunId = null;
let selectedFlowDetail = null;

function $(id) { return document.getElementById(id); }
async function fetchJSON(url) { const r = await fetch(url); if (!r.ok) throw new Error(r.statusText); return r.json(); }
function fmt(iso) { if (!iso) return '\u2014'; return new Date(iso).toLocaleTimeString('en-US',{hour12:false}); }
function fmtDate(iso) { if (!iso) return '\u2014'; const d=new Date(iso); return d.toLocaleDateString('en-US',{month:'short',day:'numeric'})+' '+d.toLocaleTimeString('en-US',{hour12:false}); }
function duration(s,e) { if(!s) return ''; const ms=(e?new Date(e):new Date())-new Date(s); return ms<1000?ms+'ms':(ms/1000).toFixed(1)+'s'; }
function esc(s) { if(!s) return ''; const d=document.createElement('div'); d.textContent=s; return d.innerHTML; }
function statusBadge(s) { return '<span class="badge badge-'+s.toLowerCase()+'">'+s+'</span>'; }

function navigate(page) {
  currentPage = page;
  document.querySelectorAll('.page').forEach(p => p.classList.remove('active'));
  document.querySelectorAll('.nav-item').forEach(n => n.classList.remove('active'));
  $('page-'+page).classList.add('active');
  document.querySelector('[data-page="'+page+'"]').classList.add('active');
  refresh();
}

// Overview
async function loadOverview() {
  try {
    const [stats, flows, runs] = await Promise.all([
      fetchJSON(BASE+'/runs/stats'),
      fetchJSON(BASE+'/flows'),
      fetchJSON(BASE+'/runs?take=10')
    ]);
    $('ov-flows').textContent = flows.length;
    $('ov-active').textContent = stats.activeRuns ?? 0;
    $('ov-done').textContent = stats.completedToday ?? 0;
    $('ov-fail').textContent = stats.failedToday ?? 0;

    $('ov-flows-table').innerHTML = flows.length === 0
      ? '<div class="empty-msg">No flows registered yet.</div>'
      : '<table class="recent-table"><thead><tr><th>Name</th><th>Version</th><th>Status</th><th>Steps</th></tr></thead><tbody>'
        + flows.map(f => {
          const m = parseManifest(f.manifestJson);
          return '<tr><td style="font-weight:700">'+esc(f.name)+'</td><td style="font-family:\'JetBrains Mono\',monospace;font-size:11px">'+esc(f.version)+'</td>'
            +'<td>'+(f.isEnabled?'<span class="badge badge-enabled">Enabled</span>':'<span class="badge badge-disabled">Disabled</span>')+'</td>'
            +'<td style="font-family:\'JetBrains Mono\',monospace;font-size:11px">'+(m?Object.keys(m.steps||{}).length:'-')+'</td></tr>';
        }).join('')+'</tbody></table>';

    $('ov-runs-table').innerHTML = runs.length === 0
      ? '<div class="empty-msg">No runs recorded yet.</div>'
      : '<table class="recent-table"><thead><tr><th>Run</th><th>Flow</th><th>Status</th><th>Started</th></tr></thead><tbody>'
        + runs.map(r =>
          '<tr style="cursor:pointer" onclick="navigate(\'runs\');setTimeout(function(){selectRun(\''+r.id+'\')},100)">'
          +'<td style="font-family:\'JetBrains Mono\',monospace;font-size:11px;color:var(--accent2)">'+r.id.slice(0,8)+'\u2026</td>'
          +'<td>'+esc(r.flowName||'')+'</td><td>'+statusBadge(r.status)+'</td><td style="font-size:11px;color:var(--text-dim)">'+fmtDate(r.startedAt)+'</td></tr>'
        ).join('')+'</tbody></table>';
  } catch(e) { console.error('Overview load error', e); }
}

// Flows
function parseManifest(json) { try { return json ? JSON.parse(json) : null; } catch { return null; } }

async function loadFlows() {
  try {
    allFlows = await fetchJSON(BASE+'/flows');
    $('flow-count-label').textContent = allFlows.length + ' flow' + (allFlows.length!==1?'s':'');

    const sel = $('runs-filter-flow');
    const curVal = sel.value;
    sel.innerHTML = '<option value="">All Flows</option>' + allFlows.map(f => '<option value="'+f.id+'"'+(f.id===curVal?' selected':'')+'>'+esc(f.name)+'</option>').join('');

    $('flows-grid').innerHTML = allFlows.length === 0
      ? '<div class="empty-msg">No flows registered. Use <code>options.AddFlow&lt;T&gt;()</code> to register flows.</div>'
      : allFlows.map(f => {
        const m = parseManifest(f.manifestJson);
        const stepCount = m ? Object.keys(m.steps||{}).length : 0;
        const triggerCount = m ? Object.keys(m.triggers||{}).length : 0;
        return '<div class="flow-card" onclick="openFlowDetail(\''+f.id+'\')">'
          +'<div class="flow-card-header"><div class="flow-card-name">'+esc(f.name)+'</div><div class="flow-card-version">v'+esc(f.version)+'</div></div>'
          +'<div class="flow-card-meta"><span>'+stepCount+' step'+(stepCount!==1?'s':'')+'</span><span>'+triggerCount+' trigger'+(triggerCount!==1?'s':'')+'</span></div>'
          +'<div class="flow-card-footer">'+(f.isEnabled?'<span class="badge badge-enabled">Enabled</span>':'<span class="badge badge-disabled">Disabled</span>')
          +'<span style="font-size:10px;color:var(--text-dim)">'+fmtDate(f.updatedAt)+'</span></div></div>';
      }).join('');
  } catch(e) { console.error('Flows load error', e); }
}

async function openFlowDetail(id) {
  selectedFlowDetail = allFlows.find(f => f.id === id);
  if (!selectedFlowDetail) return;
  $('flow-list-panel').classList.add('hide');
  $('flow-detail-panel').classList.add('show');
  renderFlowDetail();
}

function closeFlowDetail() {
  selectedFlowDetail = null;
  $('flow-list-panel').classList.remove('hide');
  $('flow-detail-panel').classList.remove('show');
}

function switchFlowTab(tabId) {
  document.querySelectorAll('#flow-detail-panel .detail-tab').forEach(t => t.classList.remove('active'));
  document.querySelectorAll('#flow-detail-panel .tab-content').forEach(t => t.classList.remove('active'));
  document.querySelector('[data-tab="'+tabId+'"]').classList.add('active');
  $(tabId).classList.add('active');
}

function renderFlowDetail() {
  const f = selectedFlowDetail;
  if (!f) return;
  const m = parseManifest(f.manifestJson);

  $('fd-name').textContent = f.name;
  $('fd-actions').innerHTML =
    (f.isEnabled
      ? '<button class="btn btn-danger" onclick="toggleFlow(\''+f.id+'\',false)">Disable</button>'
      : '<button class="btn btn-success" onclick="toggleFlow(\''+f.id+'\',true)">Enable</button>')
    + '<button class="btn btn-primary" onclick="triggerFlow(\''+f.id+'\')">&#9654; Trigger</button>';

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
      rows += '<tr><td class="mono" style="color:var(--accent2)">'+esc(key)+'</td><td><span class="badge" style="background:rgba(108,99,255,.12);color:var(--accent)">'+esc(step.type)+'</span></td><td class="mono" style="font-size:11px">'+inputs+'</td><td class="mono" style="font-size:11px">'+esc(ra)+'</td></tr>';
    }
    $('fd-steps').innerHTML = '<table class="manifest-table"><thead><tr><th>Key</th><th>Type</th><th>Inputs</th><th>RunAfter</th></tr></thead><tbody>'+rows+'</tbody></table>';
  } else {
    $('fd-steps').innerHTML = '<div class="empty-msg">No steps defined in manifest.</div>';
  }

  // Triggers tab
  if (m && m.triggers && Object.keys(m.triggers).length > 0) {
    let rows = '';
    for (const [key, trigger] of Object.entries(m.triggers)) {
      const inputs = trigger.inputs ? Object.entries(trigger.inputs).map(([k,v]) => esc(k)+': '+esc(JSON.stringify(v))).join('<br>') : '\u2014';
      rows += '<tr><td class="mono" style="color:var(--accent2)">'+esc(key)+'</td><td><span class="badge" style="background:rgba(0,229,192,.12);color:var(--accent2)">'+esc(trigger.type)+'</span></td><td class="mono" style="font-size:11px">'+inputs+'</td></tr>';
    }
    $('fd-triggers').innerHTML = '<table class="manifest-table"><thead><tr><th>Key</th><th>Type</th><th>Inputs</th></tr></thead><tbody>'+rows+'</tbody></table>';
  } else {
    $('fd-triggers').innerHTML = '<div class="empty-msg">No triggers defined in manifest.</div>';
  }

  // DAG tab
  renderDAG(m);

  // Raw JSON tab
  $('fd-json').innerHTML = '<div class="json-viewer">'+(m ? esc(JSON.stringify(m, null, 2)) : 'No manifest data.')+'</div>';
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
  svg += '<defs><marker id="arrowhead" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><polygon points="0 0, 8 3, 0 6" fill="#4a5175"/></marker></defs>';

  for (const [key, step] of Object.entries(steps)) {
    if (step.runAfter) {
      for (const dep of Object.keys(step.runAfter)) {
        if (positions[dep] && positions[key]) {
          const from = positions[dep], to = positions[key];
          svg += '<line x1="'+(from.x+nodeW)+'" y1="'+(from.y+nodeH/2)+'" x2="'+to.x+'" y2="'+(to.y+nodeH/2)+'" stroke="#4a5175" stroke-width="1.5" marker-end="url(#arrowhead)"/>';
        }
      }
    }
  }

  for (const [key, step] of Object.entries(steps)) {
    const p = positions[key];
    svg += '<g transform="translate('+p.x+','+p.y+')">'
      +'<rect width="'+nodeW+'" height="'+nodeH+'" rx="8" fill="#12151f" stroke="#1e2435" stroke-width="1"/>'
      +'<text x="10" y="18" fill="#c8cde8" font-weight="600" font-size="12">'+esc(key)+'</text>'
      +'<text x="10" y="34" fill="#6c63ff" font-size="10">'+esc(step.type)+'</text>'
      +'</g>';
  }

  svg += '</svg></div>';
  container.innerHTML = svg;
}

async function toggleFlow(id, enable) {
  try {
    await fetch(BASE+'/flows/'+id+'/'+(enable?'enable':'disable'), {method:'POST'});
    await loadFlows();
    selectedFlowDetail = allFlows.find(f => f.id === id);
    renderFlowDetail();
  } catch(e) { alert('Failed to toggle flow: '+e.message); }
}

async function triggerFlow(id) {
  try {
    const res = await fetch(BASE+'/flows/'+id+'/trigger', {method:'POST', headers:{'Content-Type':'application/json'}, body:'{}'});
    const data = await res.json();
    if (data.runId) {
      alert('Flow triggered! Run ID: '+data.runId);
      navigate('runs');
    } else {
      alert(data.error || 'Trigger failed.');
    }
  } catch(e) { alert('Failed to trigger flow: '+e.message); }
}

// Runs
async function loadRuns() {
  try {
    const flowFilter = $('runs-filter-flow').value;
    let url = BASE+'/runs?take=100';
    if (flowFilter) url += '&flowId='+flowFilter;
    allRuns = await fetchJSON(url);
    filterRuns();
  } catch(e) { console.error('Runs load error', e); }
}

function filterRuns() {
  const statusFilter = $('runs-filter-status').value;
  let filtered = allRuns;
  if (statusFilter) filtered = filtered.filter(r => r.status === statusFilter);

  $('runs-list').innerHTML = filtered.length === 0
    ? '<div class="empty-msg">No runs found.</div>'
    : filtered.map(r =>
      '<div class="run-item '+(r.id===selectedRunId?'active':'')+'" onclick="selectRun(\''+r.id+'\')">'
      +'<div class="status-dot '+r.status+'"></div>'
      +'<div class="run-info"><div class="run-id">'+r.id.slice(0,8)+'\u2026</div>'
      +'<div class="run-meta">'+esc(r.flowName||'Unknown')+' \u00b7 '+fmtDate(r.startedAt)+'</div></div>'
      +'<span class="run-status-badge badge-'+r.status.toLowerCase()+'">'+r.status+'</span></div>'
    ).join('');
}

async function selectRun(id) {
  selectedRunId = id;
  filterRuns();
  try {
    const run = await fetchJSON(BASE+'/runs/'+id);
    const steps = run.steps || [];
    $('runs-detail').innerHTML =
      '<div class="detail-header">'
      +'<div style="font-size:12px;font-weight:700;text-transform:uppercase;letter-spacing:1px;color:var(--text-dim);margin-bottom:4px">Run Detail \u00b7 '+statusBadge(run.status)+'</div>'
      +'<div class="detail-runid">'+run.id+'</div>'
      +'<div style="font-size:11px;color:var(--text-dim);margin-top:6px;display:flex;gap:16px;flex-wrap:wrap">'
      +'<span>Flow: <b style="color:var(--text)">'+esc(run.flowName||'\u2014')+'</b></span>'
      +'<span>Trigger: <b style="color:var(--text)">'+esc(run.triggerKey||'\u2014')+'</b></span>'
      +'<span>Started: <b style="color:var(--text)">'+fmtDate(run.startedAt)+'</b></span>'
      +'<span>Duration: <b style="color:var(--text)">'+duration(run.startedAt,run.completedAt)+'</b></span>'
      +'</div></div>'
      +'<div style="padding:20px">'
      +'<div style="font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:1px;color:var(--text-dim);margin-bottom:12px">Step Timeline ('+steps.length+' step'+(steps.length!==1?'s':'')+')</div>'
      +(steps.length===0?'<div class="empty-msg">No steps recorded yet.</div>':renderTimeline(steps))
      +'</div>';
  } catch(e) { console.error('Run detail error', e); }
}

function renderTimeline(steps) {
  let html = '<div class="timeline">';
  for (let i = 0; i < steps.length; i++) {
    const s = steps[i], last = i===steps.length-1;
    const icon = ({Succeeded:'\u2713',Failed:'\u2715',Running:'\u25cf'})[s.status]||'\u25cb';
    html += '<div class="step-node"><div class="step-connector">'
      +'<div class="step-circle '+s.status+'">'+icon+'</div>'
      +'<div class="step-line '+(s.status==='Succeeded'?'done':'')+' '+(last?'last':'')+'"></div></div>'
      +'<div class="step-card '+s.status+'">'
      +'<div class="step-card-header"><div><div class="step-key">'+esc(s.stepKey)+'</div><div class="step-type">'+esc(s.stepType)+'</div></div>'
      +'<span class="step-badge '+s.status+'">'+s.status+'</span></div>'
      +'<div class="step-timing"><span>Start: '+fmt(s.startedAt)+'</span><span>End: '+fmt(s.completedAt)+'</span><span>Duration: '+duration(s.startedAt,s.completedAt)+'</span></div>'
      +(s.errorMessage?'<div class="step-error">\u26a0 '+esc(s.errorMessage)+'</div>':'')
      +(s.outputJson?'<div class="step-output">\u2192 '+esc(s.outputJson)+'</div>':'')
      +'</div></div>';
  }
  return html + '</div>';
}

// Refresh logic
async function refresh() {
  try {
    if (currentPage === 'overview') await loadOverview();
    if (currentPage === 'flows') await loadFlows();
    if (currentPage === 'runs') {
      await loadRuns();
      if (selectedRunId) await selectRun(selectedRunId);
    }
  } catch(e) {}
}

setInterval(refresh, 3000);
refresh();
</script>
</body>
</html>
""";
}
