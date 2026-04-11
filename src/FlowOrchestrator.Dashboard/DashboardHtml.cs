using System.Net;

namespace FlowOrchestrator.Dashboard;

internal static class DashboardHtml
{
    public static string Render(string basePath, FlowDashboardBrandingOptions? branding = null)
    {
        var bp = basePath.TrimEnd('/');
        var options = branding ?? new FlowDashboardBrandingOptions();
        var title = string.IsNullOrWhiteSpace(options.Title) ? "FlowOrchestrator Dashboard" : options.Title;
        var pageTitle = WebUtility.HtmlEncode(title);
        var subtitle = string.IsNullOrWhiteSpace(options.Subtitle) ? "Dashboard" : options.Subtitle;
        var sidebarSubtitle = WebUtility.HtmlEncode(subtitle);
        var logoHtml = BuildLogoHtml(options.LogoUrl);

        return Template
            .Replace("{{BASE_PATH}}", bp, StringComparison.Ordinal)
            .Replace("{{PAGE_TITLE}}", pageTitle, StringComparison.Ordinal)
            .Replace("{{BRAND_TITLE}}", pageTitle, StringComparison.Ordinal)
            .Replace("{{BRAND_SUBTITLE}}", sidebarSubtitle, StringComparison.Ordinal)
            .Replace("{{BRAND_LOGO}}", logoHtml, StringComparison.Ordinal);
    }

    private static string BuildLogoHtml(string? logoUrl)
    {
        if (string.IsNullOrWhiteSpace(logoUrl))
        {
            return "&#9889;";
        }

        var value = logoUrl.Trim();
        if (value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return "&#9889;";
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
        {
            var allowed = string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            if (!allowed)
            {
                return "&#9889;";
            }
        }
        else if (!Uri.TryCreate(value, UriKind.Relative, out _))
        {
            return "&#9889;";
        }

        return $"<img src=\"{WebUtility.HtmlEncode(value)}\" alt=\"logo\" onerror=\"this.onerror=null;this.parentElement.innerHTML='&#9889;';\" />";
    }

    private const string Template = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1.0"/>
<title>{{PAGE_TITLE}}</title>
<style>
@import url('https://fonts.googleapis.com/css2?family=JetBrains+Mono:wght@400;600&display=swap');
:root{--bg:#f4f4f5;--surface:#fff;--surface2:#f9fafb;--border:#e5e7eb;--border-dark:#d1d5db;--accent:#337ab7;--accent-hover:#286090;--accent-light:#eef4fa;--warn:#f0ad4e;--warn-bg:#fcf8e3;--warn-border:#faebcc;--warn-text:#8a6d3b;--danger:#d9534f;--danger-bg:#f2dede;--danger-border:#ebccd1;--danger-text:#a94442;--success:#5cb85c;--success-bg:#dff0d8;--success-border:#d6e9c6;--success-text:#3c763d;--muted:#6b7280;--text:#1f2937;--text-dim:#6b7280;--text-light:#9ca3af;--sidebar-w:220px;--radius:4px}
*{box-sizing:border-box;margin:0;padding:0}
body{background:var(--bg);color:var(--text);font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:14px;min-height:100vh;display:flex;line-height:1.5}
a{color:var(--accent);text-decoration:none}
a:hover{text-decoration:underline}
button{font-family:inherit;cursor:pointer;border:none;outline:none}
::-webkit-scrollbar{width:8px;height:8px}::-webkit-scrollbar-track{background:#f1f1f1}::-webkit-scrollbar-thumb{background:#ccc;border-radius:4px}::-webkit-scrollbar-thumb:hover{background:#aaa}

.sidebar{width:var(--sidebar-w);background:#2d3e50;display:flex;flex-direction:column;position:fixed;top:0;bottom:0;left:0;z-index:10}
.sidebar-brand{padding:16px 16px;display:flex;align-items:center;gap:10px;border-bottom:1px solid rgba(255,255,255,.1)}
.sidebar-brand .logo{width:30px;height:30px;background:var(--accent);border-radius:4px;display:flex;align-items:center;justify-content:center;font-size:14px;flex-shrink:0;color:#fff}
.sidebar-brand .logo img{width:100%;height:100%;object-fit:cover;border-radius:4px;display:block}
.sidebar-brand h1{font-size:13px;font-weight:700;color:#fff;line-height:1.3}
.sidebar-brand span{display:block;font-size:10px;font-weight:400;color:rgba(255,255,255,.5);margin-top:1px}
.sidebar-nav{flex:1;padding:8px 0}
.nav-item{display:flex;align-items:center;gap:10px;padding:10px 16px;font-size:13px;font-weight:500;color:rgba(255,255,255,.6);transition:all .15s;border-left:3px solid transparent;cursor:pointer}
.nav-item:hover{color:#fff;background:rgba(255,255,255,.05)}
.nav-item.active{color:#fff;border-left-color:var(--accent);background:rgba(255,255,255,.1)}
.nav-item svg{width:16px;height:16px;flex-shrink:0}
.sidebar-footer{padding:12px 16px;border-top:1px solid rgba(255,255,255,.1);display:flex;flex-direction:column;align-items:flex-start;gap:8px;font-family:'JetBrains Mono',monospace;font-size:10px;color:rgba(255,255,255,.4)}
.refresh-row{display:flex;align-items:center;gap:8px}
.refresh-label{color:rgba(255,255,255,.65)}
.refresh-toggle{display:flex;align-items:center;gap:6px;color:rgba(255,255,255,.75);cursor:pointer;user-select:none}
.refresh-toggle input{accent-color:var(--accent)}
.refresh-select{background:rgba(255,255,255,.08);border:1px solid rgba(255,255,255,.2);border-radius:4px;padding:2px 6px;color:#fff;font-size:10px;font-family:'JetBrains Mono',monospace;outline:none}
.refresh-select:disabled{opacity:.45;cursor:not-allowed}
.refresh-select option{background:#2d3e50;color:#fff}
.refresh-status{color:rgba(255,255,255,.45)}
.pulse-dot{width:6px;height:6px;border-radius:50%;background:var(--success);animation:pulse 2s infinite;transition:opacity .15s}
@keyframes pulse{0%,100%{opacity:1}50%{opacity:.4}}

.main-area{margin-left:var(--sidebar-w);flex:1;display:flex;flex-direction:column;min-height:100vh}
.page{display:none;flex:1;flex-direction:column;overflow:hidden}
.page.active{display:flex}
.page-header{padding:16px 24px;border-bottom:1px solid var(--border);background:var(--surface);display:flex;align-items:center;justify-content:space-between}
.page-title{font-size:18px;font-weight:600;color:var(--text)}
.page-content{flex:1;overflow-y:auto;padding:20px 24px}

/* Overview */
.stats-grid{display:grid;grid-template-columns:repeat(4,1fr);gap:16px;margin-bottom:24px}
.stat-card{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);padding:18px;transition:box-shadow .2s}
.stat-card:hover{box-shadow:0 1px 4px rgba(0,0,0,.08)}
.stat-card .val{font-size:28px;font-weight:700;line-height:1}
.stat-card .lbl{font-size:12px;color:var(--text-dim);margin-top:4px;text-transform:uppercase;letter-spacing:.5px}

.recent-section{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);overflow:hidden}
.recent-header{padding:12px 16px;border-bottom:1px solid var(--border);font-size:12px;font-weight:600;text-transform:uppercase;letter-spacing:.5px;color:var(--text-dim);background:var(--surface2)}
.recent-table{width:100%;border-collapse:collapse}
.recent-table th{text-align:left;padding:8px 12px;font-size:11px;color:var(--text-dim);text-transform:uppercase;letter-spacing:.5px;border-bottom:1px solid var(--border);background:var(--surface2)}
.recent-table td{padding:8px 12px;font-size:13px;border-bottom:1px solid var(--border)}
.recent-table tr:last-child td{border-bottom:none}
.recent-table tr:hover td{background:var(--accent-light)}

/* Flows */
.flows-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(320px,1fr));gap:16px;margin-bottom:20px}
.flow-card{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);padding:16px;cursor:pointer;transition:box-shadow .2s}
.flow-card:hover{box-shadow:0 2px 8px rgba(0,0,0,.1)}
.flow-card-header{display:flex;align-items:center;justify-content:space-between;margin-bottom:8px}
.flow-card-name{font-size:14px;font-weight:600;color:var(--text)}
.flow-card-version{font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--text-dim)}
.flow-card-meta{display:flex;gap:12px;font-size:12px;color:var(--text-dim);margin-bottom:8px}
.flow-card-footer{display:flex;align-items:center;justify-content:space-between}
.badge{font-size:11px;font-weight:600;padding:3px 8px;border-radius:3px;text-transform:uppercase;letter-spacing:.3px}
.badge-enabled{background:var(--success-bg);color:var(--success-text);border:1px solid var(--success-border)}
.badge-disabled{background:var(--danger-bg);color:var(--danger-text);border:1px solid var(--danger-border)}
.badge-running{background:var(--warn-bg);color:var(--warn-text);border:1px solid var(--warn-border)}
.badge-succeeded{background:var(--success-bg);color:var(--success-text);border:1px solid var(--success-border)}
.badge-failed{background:var(--danger-bg);color:var(--danger-text);border:1px solid var(--danger-border)}

.flow-detail-panel{display:none;flex-direction:column;flex:1;overflow:hidden}
.flow-detail-panel.show{display:flex}
.flow-list-panel{display:flex;flex-direction:column;flex:1}
.flow-list-panel.hide{display:none}
.back-btn{font-size:12px;color:var(--accent);cursor:pointer;display:flex;align-items:center;gap:4px;padding:2px 0;font-weight:600}
.back-btn:hover{text-decoration:underline}
.flow-actions{display:flex;gap:8px}
.btn{font-size:12px;font-weight:600;padding:6px 14px;border-radius:var(--radius);transition:all .15s;border:1px solid transparent}
.btn-primary{background:var(--accent);color:#fff;border-color:var(--accent)}.btn-primary:hover{background:var(--accent-hover)}
.btn-success{background:var(--success);color:#fff;border-color:var(--success)}.btn-success:hover{background:#4cae4c}
.btn-danger{background:var(--danger);color:#fff;border-color:var(--danger)}.btn-danger:hover{background:#c9302c}
.btn-ghost{background:var(--surface);color:var(--text-dim);border:1px solid var(--border)}.btn-ghost:hover{border-color:var(--accent);color:var(--accent)}
.btn-warning{background:var(--warn);color:#fff;border-color:var(--warn)}.btn-warning:hover{background:#ec971f}
.btn-retry{background:var(--warn);color:#fff;border-color:var(--warn);font-size:11px;padding:4px 10px;border-radius:3px;margin-left:8px;cursor:pointer;font-weight:600;border:1px solid var(--warn)}.btn-retry:hover{background:#ec971f}

.detail-tabs{display:flex;border-bottom:1px solid var(--border);padding:0 24px;gap:0;background:var(--surface)}
.detail-tab{padding:10px 14px;font-size:13px;font-weight:500;color:var(--text-dim);cursor:pointer;border-bottom:2px solid transparent;transition:all .15s}
.detail-tab:hover{color:var(--text)}.detail-tab.active{color:var(--accent);border-bottom-color:var(--accent)}
.tab-content{display:none;flex:1;overflow-y:auto;padding:16px 24px}
.tab-content.active{display:block}

.manifest-table{width:100%;border-collapse:collapse;font-size:13px}
.manifest-table th{text-align:left;padding:8px 12px;background:var(--surface2);color:var(--text-dim);font-size:11px;text-transform:uppercase;letter-spacing:.5px;border:1px solid var(--border)}
.manifest-table td{padding:8px 12px;border:1px solid var(--border)}
.manifest-table td.mono{font-family:'JetBrains Mono',monospace;font-size:12px}

.dag-container{padding:16px;min-height:200px;position:relative}
.dag-node{display:inline-flex;align-items:center;gap:8px;background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);padding:8px 12px;font-size:12px;position:absolute}
.dag-node:hover{box-shadow:0 1px 4px rgba(0,0,0,.1)}
.dag-node .type-badge{font-family:'JetBrains Mono',monospace;font-size:10px;color:var(--accent);background:var(--accent-light);padding:2px 6px;border-radius:3px}

.json-viewer{background:var(--surface2);border:1px solid var(--border);border-radius:var(--radius);padding:14px;font-family:'JetBrains Mono',monospace;font-size:12px;color:var(--text);white-space:pre-wrap;word-break:break-all;max-height:400px;overflow-y:auto;line-height:1.6}

/* Runs */
.runs-filters{display:flex;gap:8px;flex-wrap:wrap;align-items:center;justify-content:flex-end}
.filter-select,.filter-input{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);padding:6px 10px;color:var(--text);font-size:12px;font-family:inherit;outline:none}
.filter-select:focus,.filter-input:focus{border-color:var(--accent);box-shadow:0 0 0 2px rgba(51,122,183,.15)}
.filter-select option{background:var(--surface)}
.runs-search{width:340px;max-width:48vw;min-height:34px;padding:7px 12px;font-size:13px}
.runs-split{display:flex;gap:0;flex:1;overflow:hidden;min-height:0}
.runs-list-col{width:380px;border-right:1px solid var(--border);overflow:hidden;flex-shrink:0;background:var(--surface);display:flex;flex-direction:column}
.runs-list{flex:1;overflow-y:auto;min-height:0}
.runs-pagination{display:flex;align-items:center;justify-content:space-between;gap:8px;padding:10px 12px;border-top:1px solid var(--border);background:var(--surface2)}
.runs-page-info{font-size:11px;color:var(--text-dim)}
.runs-page-controls{display:flex;align-items:center;gap:6px}
.runs-page-index{font-size:11px;color:var(--text-dim);min-width:68px;text-align:center}
.btn-page{background:var(--surface);color:var(--text);border:1px solid var(--border);border-radius:4px;padding:4px 10px;font-size:11px;font-weight:600;cursor:pointer}
.btn-page:hover:not(:disabled){border-color:var(--accent);color:var(--accent)}
.btn-page:disabled{opacity:.45;cursor:not-allowed}
.runs-detail-col{flex:1;overflow-y:auto;background:var(--surface2)}
.run-item{padding:10px 14px;border-bottom:1px solid var(--border);cursor:pointer;display:flex;align-items:flex-start;gap:10px;transition:background .15s}
.run-item:hover{background:var(--accent-light)}.run-item.active{background:var(--accent-light);border-left:3px solid var(--accent)}
.status-dot{width:10px;height:10px;border-radius:50%;margin-top:4px;flex-shrink:0}
.status-dot.Running{background:var(--warn);animation:pulse 1.5s infinite}.status-dot.Succeeded{background:var(--success)}.status-dot.Failed{background:var(--danger)}
.run-info{flex:1;min-width:0}
.run-id{font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--accent);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.run-meta{font-size:12px;color:var(--text-dim);margin-top:2px}
.run-status-badge{font-size:10px;font-weight:600;padding:2px 6px;border-radius:3px;text-transform:uppercase;flex-shrink:0}

.detail-empty{display:flex;align-items:center;justify-content:center;flex-direction:column;gap:8px;color:var(--text-dim);min-height:300px}.detail-empty .icon{font-size:40px;opacity:.3}
.detail-header{padding:14px 18px;border-bottom:1px solid var(--border);background:var(--surface)}
.detail-runid{font-family:'JetBrains Mono',monospace;font-size:13px;color:var(--accent)}

.timeline{display:flex;flex-direction:column}
.step-node{display:flex;align-items:stretch}
.step-connector{display:flex;flex-direction:column;align-items:center;width:28px;flex-shrink:0}
.step-circle{width:24px;height:24px;border-radius:50%;border:2px solid var(--border);display:flex;align-items:center;justify-content:center;font-size:10px;font-weight:700;flex-shrink:0;transition:all .3s;background:var(--surface)}
.step-circle.Running{border-color:var(--warn);color:var(--warn);animation:pulse 1.5s infinite}
.step-circle.Succeeded{border-color:var(--success);color:var(--success);background:var(--success-bg)}
.step-circle.Failed{border-color:var(--danger);color:var(--danger);background:var(--danger-bg)}
.step-circle.Pending{border-color:var(--text-light);color:var(--text-light)}
.step-line{width:2px;flex:1;min-height:12px;background:var(--border)}.step-line.done{background:var(--success)}.step-line.last{display:none}
.step-card{flex:1;background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);padding:10px 14px;margin-bottom:10px;margin-left:8px}
.step-card.Running{border-left:3px solid var(--warn)}.step-card.Succeeded{border-left:3px solid var(--success)}.step-card.Failed{border-left:3px solid var(--danger)}
.step-card.step-target{outline:2px solid var(--accent);outline-offset:2px;background:color-mix(in srgb,var(--accent) 10%,transparent)}
.step-card-header{display:flex;align-items:center;justify-content:space-between}
.step-key{font-family:'JetBrains Mono',monospace;font-size:13px;font-weight:600;color:var(--text)}.step-type{font-size:12px;color:var(--text-dim);margin-top:1px}
.step-badge{font-size:10px;font-weight:600;padding:2px 8px;border-radius:3px;text-transform:uppercase}
.step-badge.Running{background:var(--warn-bg);color:var(--warn-text);border:1px solid var(--warn-border)}.step-badge.Succeeded{background:var(--success-bg);color:var(--success-text);border:1px solid var(--success-border)}.step-badge.Failed{background:var(--danger-bg);color:var(--danger-text);border:1px solid var(--danger-border)}.step-badge.Pending{background:#f3f4f6;color:var(--text-light);border:1px solid #e5e7eb}
.step-timing{margin-top:6px;font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--text-dim);display:flex;gap:14px;flex-wrap:wrap}
.step-error{margin-top:6px;padding:8px 10px;background:var(--danger-bg);border:1px solid var(--danger-border);border-radius:var(--radius);font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--danger-text);word-break:break-all}
.step-output{margin-top:6px;padding:8px 10px;background:var(--surface2);border:1px solid var(--border);border-radius:var(--radius);font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--text);word-break:break-all;max-height:80px;overflow-y:auto}
.step-detail{margin-top:8px;border:1px solid var(--border);border-radius:var(--radius);background:var(--surface2)}
.step-detail-title{font-size:11px;font-weight:600;color:var(--text);padding:7px 10px;text-transform:uppercase;letter-spacing:.3px;border-bottom:1px solid var(--border);background:var(--surface)}
.step-detail-body{padding:8px 10px;font-family:'JetBrains Mono',monospace;font-size:11px;white-space:pre-wrap;word-break:break-word;max-height:200px;overflow:auto;color:var(--text)}
.step-detail.step-detail-error .step-detail-body{background:var(--danger-bg);color:var(--danger-text)}
.step-actions{margin-top:6px;display:flex;gap:6px}
.badge-cron{background:#eef4fa;color:var(--accent);border:1px solid #c5ddf0;font-family:'JetBrains Mono',monospace;font-size:10px;font-weight:600;padding:2px 7px;border-radius:3px}
.badge-paused{background:#f3f4f6;color:var(--text-dim);border:1px solid #e5e7eb}
.badge-active{background:var(--success-bg);color:var(--success-text);border:1px solid var(--success-border)}
.empty-msg{padding:24px;text-align:center;color:var(--text-dim);font-size:13px}

/* Scheduled */
.schedule-table{width:100%;border-collapse:collapse;font-size:13px;background:var(--surface)}
.schedule-table th{text-align:left;padding:8px 12px;font-size:11px;color:var(--text-dim);text-transform:uppercase;letter-spacing:.5px;border-bottom:1px solid var(--border);background:var(--surface2)}
.schedule-table td{padding:8px 12px;border-bottom:1px solid var(--border);vertical-align:middle}
.schedule-table tr:last-child td{border-bottom:none}
.schedule-table tr:hover td{background:var(--accent-light)}
.schedule-actions{display:flex;gap:4px;flex-wrap:nowrap}
.btn-sm{font-size:11px;padding:3px 8px;border-radius:3px;font-weight:600;border:1px solid transparent;cursor:pointer}
.btn-sm-primary{background:var(--accent);color:#fff;border-color:var(--accent)}.btn-sm-primary:hover{background:var(--accent-hover)}
.btn-sm-warning{background:var(--warn);color:#fff;border-color:var(--warn)}.btn-sm-warning:hover{background:#ec971f}
.btn-sm-success{background:var(--success);color:#fff;border-color:var(--success)}.btn-sm-success:hover{background:#4cae4c}
.btn-sm-ghost{background:var(--surface);color:var(--text-dim);border:1px solid var(--border)}.btn-sm-ghost:hover{border-color:var(--accent);color:var(--accent)}
.btn-sm-trigger{background:var(--surface);color:#8b5cf6;border:1px solid #a78bfa}.btn-sm-trigger:hover{background:#f5f3ff;border-color:#8b5cf6}
.cron-input{font-family:'JetBrains Mono',monospace;font-size:12px;padding:4px 8px;border:1px solid var(--border);border-radius:3px;width:140px;outline:none}
.cron-input:focus{border-color:var(--accent);box-shadow:0 0 0 2px rgba(51,122,183,.15)}
.cron-cell{display:flex;align-items:center;gap:6px}
.schedule-section{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);overflow:hidden}

.dag-svg{width:100%;overflow:auto}
.dag-svg svg text{font-family:'JetBrains Mono',monospace;font-size:11px}
</style>
</head>
<body>
<!-- Sidebar -->
<aside class="sidebar">
  <div class="sidebar-brand">
    <div class="logo">{{BRAND_LOGO}}</div>
    <h1>{{BRAND_TITLE}}<span>{{BRAND_SUBTITLE}}</span></h1>
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
    <div class="nav-item" data-page="scheduled" onclick="navigate('scheduled')">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="4" width="18" height="18" rx="2" ry="2"/><line x1="16" y1="2" x2="16" y2="6"/><line x1="8" y1="2" x2="8" y2="6"/><line x1="3" y1="10" x2="21" y2="10"/></svg>
      Scheduled
    </div>
  </nav>
  <div class="sidebar-footer">
    <div class="refresh-row">
      <span class="pulse-dot" id="refresh-pulse"></span>
      <span class="refresh-label">Auto-refresh</span>
    </div>
    <div class="refresh-row">
      <label class="refresh-toggle" for="auto-refresh-enabled">
        <input id="auto-refresh-enabled" type="checkbox" checked onchange="onAutoRefreshEnabledChange()"/>
        On
      </label>
      <select class="refresh-select" id="auto-refresh-seconds" onchange="onAutoRefreshIntervalChange()">
        <option value="5">5s</option>
        <option value="10">10s</option>
        <option value="15">15s</option>
        <option value="30">30s</option>
        <option value="60">60s</option>
      </select>
    </div>
    <div class="refresh-status" id="refresh-status">Every 5s</div>
  </div>
</aside>

<div class="main-area">
  <!-- Overview Page -->
  <div class="page active" id="page-overview">
    <div class="page-header"><div class="page-title">Overview</div></div>
    <div class="page-content">
      <div class="stats-grid" style="grid-template-columns:repeat(5,1fr)">
        <div class="stat-card"><div class="val" style="color:var(--accent)" id="ov-flows">-</div><div class="lbl">Registered Flows</div></div>
        <div class="stat-card"><div class="val" style="color:var(--warn)" id="ov-active">-</div><div class="lbl">Active Runs</div></div>
        <div class="stat-card"><div class="val" style="color:var(--success)" id="ov-done">-</div><div class="lbl">Completed Today</div></div>
        <div class="stat-card"><div class="val" style="color:var(--danger)" id="ov-fail">-</div><div class="lbl">Failed Today</div></div>
        <div class="stat-card"><div class="val" style="color:#8b5cf6" id="ov-scheduled">-</div><div class="lbl">Scheduled Jobs</div></div>
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
        <div class="detail-tab" data-tab="fd-schedule" onclick="switchFlowTab('fd-schedule')">Schedule</div>
        <div class="detail-tab" data-tab="fd-dag" onclick="switchFlowTab('fd-dag')">DAG</div>
        <div class="detail-tab" data-tab="fd-json" onclick="switchFlowTab('fd-json')">Raw JSON</div>
      </div>
      <div class="tab-content active" id="fd-manifest"></div>
      <div class="tab-content" id="fd-steps"></div>
      <div class="tab-content" id="fd-triggers"></div>
      <div class="tab-content" id="fd-schedule"></div>
      <div class="tab-content" id="fd-dag"></div>
      <div class="tab-content" id="fd-json"></div>
    </div>
  </div>

  <!-- Runs Page -->
  <div class="page" id="page-runs">
    <div class="page-header">
      <div class="page-title">Runs</div>
      <div class="runs-filters">
        <input class="filter-input runs-search" id="runs-filter-search" type="search" placeholder="Search run, step, error, output..." oninput="onRunsSearchInput()" onkeydown="onRunsSearchKeydown(event)"/>
        <select class="filter-select" id="runs-filter-flow" onchange="onRunsFilterChange()"><option value="">All Flows</option></select>
        <select class="filter-select" id="runs-filter-status" onchange="onRunsFilterChange()">
          <option value="">All Statuses</option>
          <option value="Running">Running</option>
          <option value="Succeeded">Succeeded</option>
          <option value="Failed">Failed</option>
        </select>
      </div>
    </div>
    <div class="runs-split">
      <div class="runs-list-col">
        <div class="runs-list" id="runs-list"></div>
        <div class="runs-pagination" id="runs-pagination"></div>
      </div>
      <div class="runs-detail-col" id="runs-detail">
        <div class="detail-empty"><div class="icon">&#x2B21;</div><div>Select a run to see its steps</div></div>
      </div>
    </div>
  </div>

  <!-- Scheduled Page -->
  <div class="page" id="page-scheduled">
    <div class="page-header">
      <div class="page-title">Scheduled Jobs</div>
      <div style="font-size:12px;color:var(--text-dim)" id="scheduled-count-label">0 jobs</div>
    </div>
    <div class="page-content">
      <div class="schedule-section">
        <div id="scheduled-table"></div>
      </div>
    </div>
  </div>
</div>

<script>
const BASE = '{{BASE_PATH}}/api';
let currentPage = 'overview';
let allFlows = [];
let allRuns = [];
let runsTotal = 0;
let runsPage = 1;
const runsPageSize = 20;
let runsSearchDebounceTimer = null;
let selectedRunId = null;
let selectedStepKey = null;
let selectedFlowDetail = null;
const autoRefreshStorageEnabledKey = 'flow-dashboard:auto-refresh-enabled';
const autoRefreshStorageSecondsKey = 'flow-dashboard:auto-refresh-seconds';
const autoRefreshDefaultSeconds = 5;
const autoRefreshAllowedSeconds = [5, 10, 15, 30, 60];
let autoRefreshEnabled = true;
let autoRefreshSeconds = autoRefreshDefaultSeconds;
let autoRefreshTimer = null;

function $(id) { return document.getElementById(id); }
async function fetchJSON(url) { const r = await fetch(url); if (!r.ok) throw new Error(r.statusText); return r.json(); }
function fmt(iso) { if (!iso) return '\u2014'; return new Date(iso).toLocaleTimeString('en-US',{hour12:false}); }
function fmtDate(iso) { if (!iso) return '\u2014'; const d=new Date(iso); return d.toLocaleDateString('en-US',{month:'short',day:'numeric'})+' '+d.toLocaleTimeString('en-US',{hour12:false}); }
function duration(s,e) { if(!s) return ''; const ms=(e?new Date(e):new Date())-new Date(s); return ms<1000?ms+'ms':(ms/1000).toFixed(1)+'s'; }
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
    + '<div class="step-detail-title">' + esc(title) + '</div>'
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
  const attemptCount = getStepAttemptCount(step);
  const attemptsPanel = attemptCount > 1
    ? renderDetailPanel('Step Attempts', step.attempts, false, null)
    : '';
  const inputPanel = renderDetailPanel('Step Input', step.inputJson, false, null);
  const outputPanel = renderDetailPanel('Step Output', step.outputJson, false, null);
  const errorPanel = renderDetailPanel('Step Error', step.errorMessage, step.status === 'Failed', 'error');
  return attemptsPanel + inputPanel + outputPanel + errorPanel;
}
function statusBadge(s) { return '<span class="badge badge-'+s.toLowerCase()+'">'+s+'</span>'; }

function normalizeAutoRefreshSeconds(value) {
  const parsed = Number.parseInt(value, 10);
  if (!Number.isFinite(parsed)) return autoRefreshDefaultSeconds;
  return autoRefreshAllowedSeconds.includes(parsed) ? parsed : autoRefreshDefaultSeconds;
}

function readAutoRefreshEnabled() {
  try {
    const stored = localStorage.getItem(autoRefreshStorageEnabledKey);
    if (stored === null) return true;
    return stored !== 'false';
  } catch {
    return true;
  }
}

function readAutoRefreshSeconds() {
  try {
    const stored = localStorage.getItem(autoRefreshStorageSecondsKey);
    if (!stored) return autoRefreshDefaultSeconds;
    return normalizeAutoRefreshSeconds(stored);
  } catch {
    return autoRefreshDefaultSeconds;
  }
}

function persistAutoRefreshSettings() {
  try {
    localStorage.setItem(autoRefreshStorageEnabledKey, autoRefreshEnabled ? 'true' : 'false');
    localStorage.setItem(autoRefreshStorageSecondsKey, String(autoRefreshSeconds));
  } catch {}
}

function updateAutoRefreshUI() {
  const enabledEl = $('auto-refresh-enabled');
  const secondsEl = $('auto-refresh-seconds');
  const statusEl = $('refresh-status');
  const pulseEl = $('refresh-pulse');

  if (enabledEl) enabledEl.checked = autoRefreshEnabled;
  if (secondsEl) {
    secondsEl.value = String(autoRefreshSeconds);
    secondsEl.disabled = !autoRefreshEnabled;
  }
  if (statusEl) {
    statusEl.textContent = autoRefreshEnabled ? ('Every ' + autoRefreshSeconds + 's') : 'Paused';
  }
  if (pulseEl) {
    pulseEl.style.animationPlayState = autoRefreshEnabled ? 'running' : 'paused';
    pulseEl.style.opacity = autoRefreshEnabled ? '1' : '.35';
  }
}

function restartAutoRefreshTimer() {
  if (autoRefreshTimer) {
    clearInterval(autoRefreshTimer);
    autoRefreshTimer = null;
  }
  if (!autoRefreshEnabled) return;
  autoRefreshTimer = setInterval(refresh, autoRefreshSeconds * 1000);
}

function initAutoRefreshSettings() {
  autoRefreshEnabled = readAutoRefreshEnabled();
  autoRefreshSeconds = readAutoRefreshSeconds();
  updateAutoRefreshUI();
  restartAutoRefreshTimer();
}

function onAutoRefreshEnabledChange() {
  const enabledEl = $('auto-refresh-enabled');
  autoRefreshEnabled = !!(enabledEl && enabledEl.checked);
  persistAutoRefreshSettings();
  updateAutoRefreshUI();
  restartAutoRefreshTimer();
  if (autoRefreshEnabled) refresh();
}

function onAutoRefreshIntervalChange() {
  const secondsEl = $('auto-refresh-seconds');
  autoRefreshSeconds = normalizeAutoRefreshSeconds(secondsEl ? secondsEl.value : autoRefreshDefaultSeconds);
  persistAutoRefreshSettings();
  updateAutoRefreshUI();
  restartAutoRefreshTimer();
}

function _navigate(page) {
  currentPage = page;
  document.querySelectorAll('.page').forEach(p => p.classList.remove('active'));
  document.querySelectorAll('.nav-item').forEach(n => n.classList.remove('active'));
  $('page-'+page).classList.add('active');
  document.querySelector('[data-page="'+page+'"]').classList.add('active');
  refresh();
}

function navigate(page) {
  if (page !== 'runs') { selectedRunId = null; selectedStepKey = null; }
  if (page === 'runs' && !location.hash.startsWith('#/runs/')) {
    history.replaceState(null, '', '#/runs');
  } else if (page !== 'runs') {
    history.replaceState(null, '', '#/'+page);
  }
  _navigate(page);
}

// Overview
async function loadOverview() {
  try {
    const [stats, flows, runs, schedules] = await Promise.all([
      fetchJSON(BASE+'/runs/stats'),
      fetchJSON(BASE+'/flows'),
      fetchJSON(BASE+'/runs?take=10'),
      fetchJSON(BASE+'/schedules').catch(() => [])
    ]);
    $('ov-flows').textContent = flows.length;
    $('ov-active').textContent = stats.activeRuns ?? 0;
    $('ov-done').textContent = stats.completedToday ?? 0;
    $('ov-fail').textContent = stats.failedToday ?? 0;
    $('ov-scheduled').textContent = schedules.length;

    $('ov-flows-table').innerHTML = flows.length === 0
      ? '<div class="empty-msg">No flows registered yet.</div>'
      : '<table class="recent-table"><thead><tr><th>Name</th><th>Version</th><th>Status</th><th>Steps</th></tr></thead><tbody>'
        + flows.map(f => {
          const m = parseManifest(f.manifestJson);
          return '<tr><td style="font-weight:600">'+esc(f.name)+'</td><td style="font-family:\'JetBrains Mono\',monospace;font-size:11px">'+esc(f.version)+'</td>'
            +'<td>'+(f.isEnabled?'<span class="badge badge-enabled">Enabled</span>':'<span class="badge badge-disabled">Disabled</span>')+'</td>'
            +'<td style="font-family:\'JetBrains Mono\',monospace;font-size:11px">'+(m?Object.keys(m.steps||{}).length:'-')+'</td></tr>';
        }).join('')+'</tbody></table>';

    $('ov-runs-table').innerHTML = runs.length === 0
      ? '<div class="empty-msg">No runs recorded yet.</div>'
      : '<table class="recent-table"><thead><tr><th>Run</th><th>Flow</th><th>Status</th><th>Started</th></tr></thead><tbody>'
        + runs.map(r =>
          '<tr style="cursor:pointer" onclick="history.replaceState(null,\'\',\'#/runs/'+r.id+'\');applyRoute(parseHash())">'
          +'<td style="font-family:\'JetBrains Mono\',monospace;font-size:11px;color:var(--accent)">'+r.id.slice(0,8)+'\u2026</td>'
          +'<td>'+esc(r.flowName||'')+'</td><td>'+statusBadge(r.status)+'</td><td style="font-size:12px;color:var(--text-dim)">'+fmtDate(r.startedAt)+'</td></tr>'
        ).join('')+'</tbody></table>';
  } catch(e) { console.error('Overview load error', e); }
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
        const cronExpr = getCronExpression(m);
        return '<div class="flow-card" onclick="openFlowDetail(\''+f.id+'\')">'
          +'<div class="flow-card-header"><div class="flow-card-name">'+esc(f.name)+'</div><div class="flow-card-version">v'+esc(f.version)+'</div></div>'
          +'<div class="flow-card-meta"><span>'+stepCount+' step'+(stepCount!==1?'s':'')+'</span><span>'+triggerCount+' trigger'+(triggerCount!==1?'s':'')+'</span>'
          +(cronExpr?'<span class="badge-cron" title="Cron schedule">&#128339; '+esc(cronExpr)+'</span>':'')
          +'</div>'
          +'<div class="flow-card-footer">'+(f.isEnabled?'<span class="badge badge-enabled">Enabled</span>':'<span class="badge badge-disabled">Disabled</span>')
          +'<span style="font-size:11px;color:var(--text-dim)">'+fmtDate(f.updatedAt)+'</span></div></div>';
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
        urlCell = '<div class="webhook-url-cell"><code class="mono" style="font-size:11px">'+esc(urlByFlowId)+'</code><button class="btn btn-sm btn-sm-ghost" data-url="'+esc(urlByFlowId)+'" onclick="copyWebhookUrl(this.dataset.url)" title="Copy URL">Copy</button></div>';
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
  svg += '<defs><marker id="arrowhead" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><polygon points="0 0, 8 3, 0 6" fill="#9ca3af"/></marker></defs>';

  for (const [key, step] of Object.entries(steps)) {
    if (step.runAfter) {
      for (const dep of Object.keys(step.runAfter)) {
        if (positions[dep] && positions[key]) {
          const from = positions[dep], to = positions[key];
          svg += '<line x1="'+(from.x+nodeW)+'" y1="'+(from.y+nodeH/2)+'" x2="'+to.x+'" y2="'+(to.y+nodeH/2)+'" stroke="#9ca3af" stroke-width="1.5" marker-end="url(#arrowhead)"/>';
        }
      }
    }
  }

  for (const [key, step] of Object.entries(steps)) {
    const p = positions[key];
    svg += '<g transform="translate('+p.x+','+p.y+')">'
      +'<rect width="'+nodeW+'" height="'+nodeH+'" rx="4" fill="#fff" stroke="#d1d5db" stroke-width="1"/>'
      +'<text x="10" y="18" fill="#1f2937" font-weight="600" font-size="12">'+esc(key)+'</text>'
      +'<text x="10" y="34" fill="#337ab7" font-size="10">'+esc(step.type)+'</text>'
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

function copyWebhookUrl(url) {
  navigator.clipboard.writeText(url).then(() => alert('Webhook URL copied to clipboard.')).catch(() => alert('Copy failed.'));
}

function copyRunLink(runId) {
  const url = location.origin + location.pathname + '#/runs/' + runId;
  navigator.clipboard.writeText(url).then(() => showToast('Run link copied!')).catch(() => alert('Copy failed'));
}

function copyStepLink(runId, stepKey) {
  const url = location.origin + location.pathname + '#/runs/' + runId + '/steps/' + encodeURIComponent(stepKey);
  navigator.clipboard.writeText(url).then(() => showToast('Step link copied!')).catch(() => alert('Copy failed'));
}

function showToast(msg) {
  const t = document.createElement('div');
  t.textContent = msg;
  t.style.cssText = 'position:fixed;bottom:24px;left:50%;transform:translateX(-50%);background:#2d3e50;color:#fff;padding:8px 18px;border-radius:4px;font-size:13px;z-index:9999;transition:opacity .4s';
  document.body.appendChild(t);
  setTimeout(() => { t.style.opacity='0'; setTimeout(() => t.remove(), 400); }, 1800);
}

function scrollToStep(stepKey) {
  const node = document.querySelector('.step-node[data-step-key="'+stepKey.replace(/"/g, '&quot;')+'"]');
  if (!node) return;
  const card = node.querySelector('.step-card');
  if (card) card.classList.add('step-target');
  node.scrollIntoView({ behavior: 'smooth', block: 'center' });
  setTimeout(() => { if (card) card.classList.remove('step-target'); }, 4000);
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

function isRunsAutoRefreshBlocked() {
  if (runsPage > 1) return true;
  const list = $('runs-list');
  const detail = $('runs-detail');
  return (list && list.scrollTop > 24) || (detail && detail.scrollTop > 24);
}

function onRunsFilterChange() {
  runsPage = 1;
  selectedRunId = null;
  selectedStepKey = null;
  history.replaceState(null, '', '#/runs');
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
  selectedRunId = null;
  selectedStepKey = null;
  history.replaceState(null, '', '#/runs');
  renderRunDetailEmpty();
  await loadRuns();
}

function renderRunsPagination() {
  const maxPage = Math.max(1, Math.ceil(runsTotal / runsPageSize));
  const hasRuns = runsTotal > 0;
  const start = hasRuns ? ((runsPage - 1) * runsPageSize) + 1 : 0;
  const end = hasRuns ? Math.min(runsPage * runsPageSize, runsTotal) : 0;

  $('runs-pagination').innerHTML =
    '<div class="runs-page-info">'+(hasRuns ? ('Showing '+start+'-'+end+' of '+runsTotal) : 'No runs')+'</div>'
    +'<div class="runs-page-controls">'
    +'<button class="btn-page" onclick="changeRunsPage(-1)"'+(runsPage<=1?' disabled':'')+'>Prev</button>'
    +'<span class="runs-page-index">Page '+runsPage+'/'+maxPage+'</span>'
    +'<button class="btn-page" onclick="changeRunsPage(1)"'+(runsPage>=maxPage || !hasRuns?' disabled':'')+'>Next</button>'
    +'</div>';
}

function renderRuns(preserveScroll) {
  const runsListEl = $('runs-list');
  const listScrollTop = preserveScroll ? runsListEl.scrollTop : 0;

  runsListEl.innerHTML = allRuns.length === 0
    ? '<div class="empty-msg">No runs found.</div>'
    : allRuns.map(r =>
      '<div class="run-item '+(r.id===selectedRunId?'active':'')+'" onclick="selectRun(\''+r.id+'\')">'
      +'<div class="status-dot '+r.status+'"></div>'
      +'<div class="run-info"><div class="run-id">'+r.id.slice(0,8)+'\u2026</div>'
      +'<div class="run-meta">'+esc(r.flowName||'Unknown')+' \u00b7 '+fmtDate(r.startedAt)+'</div></div>'
      +'<span class="run-status-badge badge-'+r.status.toLowerCase()+'">'+r.status+'</span></div>'
    ).join('');

  if (preserveScroll) {
    runsListEl.scrollTop = listScrollTop;
  }

  renderRunsPagination();

  if (allRuns.length === 0) {
    renderRunDetailEmpty();
  }
}

async function loadRuns(preserveScroll) {
  try {
    const flowFilter = $('runs-filter-flow').value;
    const statusFilter = $('runs-filter-status').value;
    const searchFilter = $('runs-filter-search').value.trim();
    const skip = (runsPage - 1) * runsPageSize;
    let url = BASE+'/runs?includeTotal=true&take='+runsPageSize+'&skip='+skip;
    if (flowFilter) url += '&flowId='+encodeURIComponent(flowFilter);
    if (statusFilter) url += '&status='+encodeURIComponent(statusFilter);
    if (searchFilter) url += '&search='+encodeURIComponent(searchFilter);

    const page = await fetchJSON(url);
    allRuns = Array.isArray(page.items) ? page.items : [];
    runsTotal = typeof page.total === 'number' ? page.total : allRuns.length;

    const maxPage = Math.max(1, Math.ceil(runsTotal / runsPageSize));
    if (runsTotal > 0 && runsPage > maxPage) {
      runsPage = maxPage;
      await loadRuns(preserveScroll);
      return;
    }

    if (selectedRunId && !allRuns.some(r => r.id === selectedRunId)) {
      selectedRunId = null;
      renderRunDetailEmpty();
    }

    renderRuns(preserveScroll);
  } catch(e) { console.error('Runs load error', e); }
}

async function selectRun(id, preserveScroll, targetStepKey) {
  const newHash = targetStepKey
    ? '#/runs/'+id+'/steps/'+encodeURIComponent(targetStepKey)
    : '#/runs/'+id;
  if (location.hash !== newHash) history.replaceState(null, '', newHash);

  selectedRunId = id;
  selectedStepKey = targetStepKey || null;
  renderRuns(preserveScroll);
  const detailEl = $('runs-detail');
  const detailScrollTop = preserveScroll && detailEl ? detailEl.scrollTop : 0;
  try {
    const run = await fetchJSON(BASE+'/runs/'+id);
    const steps = run.steps || [];
    $('runs-detail').innerHTML =
      '<div class="detail-header">'
      +'<div style="font-size:11px;font-weight:600;text-transform:uppercase;letter-spacing:.5px;color:var(--text-dim);margin-bottom:4px;display:flex;align-items:center;justify-content:space-between">'
      +'<span>Run Detail \u00b7 '+statusBadge(run.status)+'</span>'
      +'<button class="btn btn-ghost" style="font-size:11px;padding:4px 10px" onclick="copyRunLink(\''+run.id+'\')" title="Copy shareable link">\uD83D\uDD17 Copy link</button>'
      +'</div>'
      +'<div class="detail-runid">'+run.id+'</div>'
      +'<div style="font-size:12px;color:var(--text-dim);margin-top:6px;display:flex;gap:14px;flex-wrap:wrap">'
      +'<span>Flow: <b style="color:var(--text)">'+esc(run.flowName||'\u2014')+'</b></span>'
      +'<span>Trigger: <b style="color:var(--text)">'+esc(run.triggerKey||'\u2014')+'</b></span>'
      +'<span>Started: <b style="color:var(--text)">'+fmtDate(run.startedAt)+'</b></span>'
      +'<span>Duration: <b style="color:var(--text)">'+duration(run.startedAt,run.completedAt)+'</b></span>'
      +'</div></div>'
      +'<div style="padding:16px 18px">'
      +renderRunTriggerPanels(run)
      +'<div style="font-size:11px;font-weight:600;text-transform:uppercase;letter-spacing:.5px;color:var(--text-dim);margin-bottom:10px">Step Timeline ('+steps.length+' step'+(steps.length!==1?'s':'')+')</div>'
      +(steps.length===0?'<div class="empty-msg">No steps recorded yet.</div>':renderTimeline(steps, run.id))
      +'</div>';
    if (preserveScroll) $('runs-detail').scrollTop = detailScrollTop;
    if (selectedStepKey) scrollToStep(selectedStepKey);
  } catch(e) { console.error('Run detail error', e); }
}

function renderTimeline(steps, runId) {
  let html = '<div class="timeline">';
  for (let i = 0; i < steps.length; i++) {
    const s = steps[i], last = i===steps.length-1;
    const icon = ({Succeeded:'\u2713',Failed:'\u2715',Running:'\u25cf'})[s.status]||'\u25cb';
    const attemptCount = getStepAttemptCount(s);
    html += '<div class="step-node" data-step-key="'+esc(s.stepKey)+'"><div class="step-connector">'
      +'<div class="step-circle '+s.status+'">'+icon+'</div>'
      +'<div class="step-line '+(s.status==='Succeeded'?'done':'')+' '+(last?'last':'')+'"></div></div>'
      +'<div class="step-card '+s.status+'">'
      +'<div class="step-card-header"><div><div class="step-key">'+esc(s.stepKey)+'</div><div class="step-type">'+esc(s.stepType)+'</div></div>'
      +'<div style="display:flex;align-items:center;gap:6px"><span class="step-badge '+s.status+'">'+s.status+'</span>'
      +(attemptCount>1?'<span class="step-badge Pending">x'+attemptCount+' attempts</span>':'')
      +(s.status==='Failed'?'<button class="btn-retry" onclick="retryStep(\''+runId+'\',\''+esc(s.stepKey)+'\')">&#8635; Retry</button>':'')
      +'<button class="btn btn-ghost" style="font-size:10px;padding:2px 8px" onclick="copyStepLink(\''+runId+'\',\''+esc(s.stepKey)+'\')" title="Copy step link">\uD83D\uDD17</button>'
      +'</div></div>'
      +'<div class="step-timing"><span>Start: '+fmt(s.startedAt)+'</span><span>End: '+fmt(s.completedAt)+'</span><span>Duration: '+duration(s.startedAt,s.completedAt)+'</span></div>'
      +renderStepDebugPanels(s)
      +'</div></div>';
  }
  return html + '</div>';
}

async function retryStep(runId, stepKey) {
  if (!confirm('Retry step "'+stepKey+'"?')) return;
  try {
    const res = await fetch(BASE+'/runs/'+runId+'/steps/'+encodeURIComponent(stepKey)+'/retry', {method:'POST'});
    const data = await res.json();
    if (data.success) {
      await selectRun(runId);
    } else {
      alert(data.error || 'Retry failed.');
    }
  } catch(e) { alert('Failed to retry step: '+e.message); }
}

// Scheduled Jobs
let allSchedules = [];

function scheduleBadge(state) {
  if (!state) return '<span class="badge badge-paused">\u2014</span>';
  const s = state.toLowerCase();
  if (s === 'succeeded') return '<span class="badge badge-succeeded">'+state+'</span>';
  if (s === 'failed') return '<span class="badge badge-failed">'+state+'</span>';
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
      +'<button class="btn-sm btn-sm-trigger" onclick="triggerScheduledJob(\''+esc(j.jobId)+'\')" title="Trigger now">&#9889;</button>'
      +(j.paused
        ?'<button class="btn-sm btn-sm-success" onclick="resumeScheduledJob(\''+esc(j.jobId)+'\')" title="Resume">&#9654;</button>'
        :'<button class="btn-sm btn-sm-warning" onclick="pauseScheduledJob(\''+esc(j.jobId)+'\')" title="Pause">&#10074;&#10074;</button>')
      +'</div></td></tr>';
  }
  return html + '</tbody></table>';
}

async function loadScheduled() {
  try {
    allSchedules = await fetchJSON(BASE+'/schedules');
    $('scheduled-count-label').textContent = allSchedules.length + ' job' + (allSchedules.length!==1?'s':'');
    $('scheduled-table').innerHTML = renderScheduleTable(allSchedules, false);
  } catch(e) { console.error('Scheduled load error', e); }
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

async function triggerScheduledJob(jobId) {
  if (!confirm('Trigger job "'+jobId+'" now?')) return;
  try {
    const res = await fetch(BASE+'/schedules/'+encodeURIComponent(jobId)+'/trigger', {method:'POST'});
    const data = await res.json();
    if (data.success) {
      await loadScheduled();
      if (selectedFlowDetail) await loadFlowSchedule();
    } else { alert(data.error || 'Trigger failed.'); }
  } catch(e) { alert('Failed to trigger: '+e.message); }
}

async function pauseScheduledJob(jobId) {
  if (!confirm('Pause scheduled job? You can resume it later.')) return;
  try {
    const res = await fetch(BASE+'/schedules/'+encodeURIComponent(jobId)+'/pause', {method:'POST'});
    const data = await res.json();
    if (data.success) {
      await loadScheduled();
      if (selectedFlowDetail) await loadFlowSchedule();
    } else { alert(data.error || 'Pause failed.'); }
  } catch(e) { alert('Failed to pause: '+e.message); }
}

async function resumeScheduledJob(jobId) {
  try {
    const res = await fetch(BASE+'/schedules/'+encodeURIComponent(jobId)+'/resume', {method:'POST'});
    const data = await res.json();
    if (data.success) {
      await loadScheduled();
      if (selectedFlowDetail) await loadFlowSchedule();
    } else { alert(data.error || 'Resume failed.'); }
  } catch(e) { alert('Failed to resume: '+e.message); }
}

// Refresh logic
async function refresh() {
  try {
    if (currentPage === 'overview') await loadOverview();
    if (currentPage === 'flows') await loadFlows();
    if (currentPage === 'runs') {
      if (isRunsAutoRefreshBlocked()) return;
      await loadRuns(true);
      if (selectedRunId) await selectRun(selectedRunId, true, selectedStepKey);
    }
    if (currentPage === 'scheduled') await loadScheduled();
  } catch(e) {}
}

function parseHash() {
  const hash = location.hash || '';
  if (!hash || hash === '#' || hash === '#/') return { page: 'overview', runId: null, stepKey: null };
  const path = hash.startsWith('#/') ? hash.slice(2) : hash.slice(1);
  const parts = path.split('/');
  const page = ['overview','flows','runs','scheduled'].includes(parts[0]) ? parts[0] : 'overview';
  if (page !== 'runs') return { page, runId: null, stepKey: null };
  const runId = parts[1] || null;
  const stepKey = (parts[2] === 'steps' && parts[3]) ? decodeURIComponent(parts[3]) : null;
  return { page, runId, stepKey };
}

async function applyRoute(route) {
  _navigate(route.page);
  if (route.page === 'runs' && route.runId) {
    await loadRuns(false);
    await selectRun(route.runId, false, route.stepKey);
  }
}

function initRouting() {
  window.addEventListener('hashchange', () => { applyRoute(parseHash()); });
  const r = parseHash();
  if (r.page !== 'overview' || r.runId) applyRoute(r);
}

initAutoRefreshSettings();
initRouting();
const _initialRoute = parseHash();
if (!_initialRoute.runId && _initialRoute.page === 'overview') refresh();
</script>
</body>
</html>
""";
}
