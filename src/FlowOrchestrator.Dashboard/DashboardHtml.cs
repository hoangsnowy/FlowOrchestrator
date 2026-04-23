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
@import url('https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=JetBrains+Mono:wght@400;600&display=swap');
:root{
  /* Exact tokens from Claude DESIGN.md */
  --bg:#f5f4ed;           /* Parchment — warm cream paper */
  --surface:#faf9f5;       /* Ivory — card surface */
  --surface2:#f0eee6;      /* Border Cream as secondary surface */
  --border:#f0eee6;        /* Border Cream — barely visible warm cream */
  --border-dark:#e8e6dc;   /* Border Warm — prominent borders */
  --accent:#c96442;        /* Terracotta Brand — the signature earthy CTA */
  --accent-hover:#a8502f;  /* Darker terracotta */
  --accent-light:#fdf3ee;  /* Whisper terracotta background */
  --warn:#c8803a;          /* Warm amber */
  --warn-bg:#fef4e8;
  --warn-border:#f6d8b3;
  --warn-text:#7a3f08;
  --danger:#b53333;        /* Error Crimson — spec exact */
  --danger-bg:#fdf0ee;
  --danger-border:#f5c6c0;
  --danger-text:#7a1e1e;
  --success:#2d6a4f;
  --success-bg:#eaf5ef;
  --success-border:#b5d9c5;
  --success-text:#1b4435;
  --skip:#87867f;          /* Stone Gray — intentional bypass */
  --skip-bg:#f5f4ed;       /* Parchment — warm neutral background */
  --skip-border:#d1cfc5;   /* Ring Warm */
  --skip-text:#5e5d59;     /* Olive Gray */
  --muted:#87867f;         /* Stone Gray */
  --text:#141413;          /* Anthropic Near Black */
  --text-dim:#5e5d59;      /* Olive Gray */
  --text-light:#87867f;    /* Stone Gray */
  --sidebar-bg:#141413;    /* Near Black for sidebar */
  --sidebar-surface:#30302e;/* Dark Surface */
  --sidebar-w:232px;
  --radius:8px;
  --ring:#d1cfc5;          /* Ring Warm — for button/card hover shadows */
  --shadow-whisper:rgba(0,0,0,0.05) 0px 4px 24px; /* Elevated content */
}
*{box-sizing:border-box;margin:0;padding:0}
body{background:var(--bg);color:var(--text);font-family:'Inter',-apple-system,'Segoe UI',sans-serif;font-size:14px;min-height:100vh;display:flex;line-height:1.5}
a{color:var(--accent);text-decoration:none}
a:hover{text-decoration:underline}
button{font-family:inherit;cursor:pointer;border:none;outline:none}
::-webkit-scrollbar{width:6px;height:6px}::-webkit-scrollbar-track{background:transparent}::-webkit-scrollbar-thumb{background:var(--border-dark);border-radius:3px}::-webkit-scrollbar-thumb:hover{background:var(--muted)}

.sidebar{width:var(--sidebar-w);background:var(--sidebar-bg);display:flex;flex-direction:column;position:fixed;top:0;bottom:0;left:0;z-index:10}
.sidebar-brand{padding:20px 18px;display:flex;align-items:center;gap:10px;border-bottom:1px solid var(--sidebar-surface)}
.sidebar-brand .logo{width:32px;height:32px;background:var(--accent);border-radius:8px;display:flex;align-items:center;justify-content:center;font-size:15px;flex-shrink:0;color:#faf9f5}
.sidebar-brand .logo img{width:100%;height:100%;object-fit:cover;border-radius:8px;display:block}
.sidebar-brand h1{font-size:13px;font-weight:600;color:#b0aea5;line-height:1.3}
.sidebar-brand span{display:block;font-size:10px;font-weight:400;color:#5e5d59;margin-top:1px}
.sidebar-nav{flex:1;padding:10px 0}
.nav-item{display:flex;align-items:center;gap:9px;padding:9px 18px;font-size:13px;font-weight:500;color:#5e5d59;transition:all .15s;border-left:2px solid transparent;cursor:pointer}
.nav-item:hover{color:#b0aea5;background:rgba(255,255,255,.04)}
.nav-item.active{color:#d97757;border-left-color:#d97757;background:rgba(201,100,66,.12)}
.nav-item svg{width:16px;height:16px;flex-shrink:0}
.sidebar-footer{padding:14px 18px;border-top:1px solid var(--sidebar-surface);display:flex;flex-direction:column;align-items:flex-start;gap:8px;font-family:'JetBrains Mono',monospace;font-size:10px;color:#3d3d3a}
.refresh-row{display:flex;align-items:center;gap:8px}
.refresh-label{color:#5e5d59}
.refresh-toggle{display:flex;align-items:center;gap:6px;color:#5e5d59;cursor:pointer;user-select:none}
.refresh-toggle input{accent-color:var(--accent)}
.refresh-select{background:rgba(255,255,255,.05);border:1px solid var(--sidebar-surface);border-radius:5px;padding:2px 6px;color:#87867f;font-size:10px;font-family:'JetBrains Mono',monospace;outline:none}
.refresh-select:disabled{opacity:.4;cursor:not-allowed}
.refresh-select option{background:var(--sidebar-bg);color:#87867f}
.refresh-status{color:#3d3d3a}
.pulse-dot{width:6px;height:6px;border-radius:50%;background:var(--success);animation:pulse 2s infinite;transition:opacity .15s}
@keyframes pulse{0%,100%{opacity:1}50%{opacity:.35}}

.main-area{margin-left:var(--sidebar-w);flex:1;display:flex;flex-direction:column;height:100vh;overflow:hidden}
.page{display:none;flex:1;flex-direction:column;overflow:hidden}
.page.active{display:flex}
.page-header{padding:18px 28px;border-bottom:1px solid var(--border);background:var(--surface);display:flex;align-items:center;justify-content:space-between}
.page-title{font-size:18px;font-weight:500;color:var(--text);letter-spacing:-.2px;font-family:Georgia,'Anthropic Serif',serif}
.page-content{flex:1;overflow-y:auto;padding:24px 28px}

/* Overview */
.stats-grid{display:grid;grid-template-columns:repeat(4,1fr);gap:16px;margin-bottom:24px}
.stat-card{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);padding:20px;transition:box-shadow .2s,border-color .2s;box-shadow:var(--shadow-whisper)}
.stat-card:hover{box-shadow:var(--surface) 0px 0px 0px 0px,var(--ring) 0px 0px 0px 1px,var(--shadow-whisper)}
.stat-card .val{font-size:32px;font-weight:500;line-height:1;letter-spacing:-.4px;font-family:Georgia,'Anthropic Serif',serif}
.stat-card .lbl{font-size:11px;color:var(--text-dim);margin-top:6px;text-transform:uppercase;letter-spacing:.6px;font-weight:600}

.recent-section{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);overflow:hidden}
.recent-header{padding:12px 16px;border-bottom:1px solid var(--border);font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.6px;color:var(--text-dim);background:var(--surface2)}
.recent-table{width:100%;border-collapse:collapse}
.recent-table th{text-align:left;padding:9px 14px;font-size:11px;color:var(--text-dim);text-transform:uppercase;letter-spacing:.5px;border-bottom:1px solid var(--border);background:var(--surface2);font-weight:700}
.recent-table td{padding:9px 14px;font-size:13px;border-bottom:1px solid var(--border)}
.recent-table tr:last-child td{border-bottom:none}
.recent-table tr:hover td{background:var(--accent-light)}

/* Flows */
.flows-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(320px,1fr));gap:16px;margin-bottom:20px}
.flow-card{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);padding:18px;cursor:pointer;transition:box-shadow .2s;box-shadow:var(--shadow-whisper)}
.flow-card:hover{box-shadow:var(--surface) 0px 0px 0px 0px,var(--ring) 0px 0px 0px 1px,var(--shadow-whisper)}
.flow-card-header{display:flex;align-items:center;justify-content:space-between;margin-bottom:8px}
.flow-card-name{font-size:14px;font-weight:500;color:var(--text);font-family:Georgia,'Anthropic Serif',serif}
.flow-card-version{font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--text-dim)}
.flow-card-meta{display:flex;gap:12px;font-size:12px;color:var(--text-dim);margin-bottom:10px}
.flow-card-footer{display:flex;align-items:center;justify-content:space-between}
.badge{font-size:11px;font-weight:700;padding:3px 9px;border-radius:100px;text-transform:uppercase;letter-spacing:.4px}
.badge-enabled{background:var(--success-bg);color:var(--success-text);border:1px solid var(--success-border)}
.badge-disabled{background:var(--danger-bg);color:var(--danger-text);border:1px solid var(--danger-border)}
.badge-running{background:var(--warn-bg);color:var(--warn-text);border:1px solid var(--warn-border)}
.badge-succeeded{background:var(--success-bg);color:var(--success-text);border:1px solid var(--success-border)}
.badge-failed{background:var(--danger-bg);color:var(--danger-text);border:1px solid var(--danger-border)}
.badge-skipped{background:var(--skip-bg);color:var(--skip-text);border:1px solid var(--skip-border)}

.flow-detail-panel{display:none;flex-direction:column;flex:1;overflow:hidden}
.flow-detail-panel.show{display:flex}
.flow-list-panel{display:flex;flex-direction:column;flex:1}
.flow-list-panel.hide{display:none}
.back-btn{font-size:12px;color:var(--accent);cursor:pointer;display:flex;align-items:center;gap:4px;padding:2px 0;font-weight:600}
.back-btn:hover{text-decoration:underline}
.flow-actions{display:flex;gap:8px}
.btn{font-size:13px;font-weight:500;padding:7px 16px;border-radius:8px;transition:all .15s;border:1px solid transparent}
.btn-primary{background:var(--accent);color:#faf9f5;border-radius:12px;box-shadow:var(--accent) 0px 0px 0px 0px,var(--accent) 0px 0px 0px 1px}.btn-primary:hover{background:var(--accent-hover);box-shadow:var(--accent-hover) 0px 0px 0px 0px,var(--accent-hover) 0px 0px 0px 1px}
.btn-success{background:var(--success);color:#faf9f5;border-radius:12px}.btn-success:hover{filter:brightness(1.1)}
.btn-danger{background:var(--danger);color:#faf9f5;border-radius:12px}.btn-danger:hover{filter:brightness(1.1)}
.btn-ghost{background:var(--surface);color:var(--text-dim);border:1px solid var(--border-dark);box-shadow:var(--surface) 0px 0px 0px 0px,var(--ring) 0px 0px 0px 1px}.btn-ghost:hover{border-color:var(--border-dark);color:var(--accent);box-shadow:var(--surface) 0px 0px 0px 0px,var(--accent) 0px 0px 0px 1px}
.btn-warning{background:var(--warn);color:#faf9f5;border-radius:12px}.btn-warning:hover{filter:brightness(1.05)}
.btn-retry{background:var(--warn);color:#faf9f5;font-size:11px;padding:4px 10px;border-radius:6px;margin-left:8px;cursor:pointer;font-weight:600;border:none}.btn-retry:hover{filter:brightness(1.05)}

.detail-tabs{display:flex;border-bottom:1px solid var(--border);padding:0 28px;gap:0;background:var(--surface)}
.detail-tab{padding:11px 14px;font-size:13px;font-weight:500;color:var(--text-dim);cursor:pointer;border-bottom:2px solid transparent;transition:all .15s}
.detail-tab:hover{color:var(--text)}.detail-tab.active{color:var(--accent);border-bottom-color:var(--accent);font-weight:600}
.tab-content{display:none;flex:1;overflow-y:auto;padding:20px 28px}
.tab-content.active{display:block}

.manifest-table{width:100%;border-collapse:collapse;font-size:13px}
.manifest-table th{text-align:left;padding:9px 14px;background:var(--surface2);color:var(--text-dim);font-size:11px;text-transform:uppercase;letter-spacing:.5px;border:1px solid var(--border);font-weight:700}
.manifest-table td{padding:9px 14px;border:1px solid var(--border)}
.manifest-table td.mono{font-family:'JetBrains Mono',monospace;font-size:12px}

.dag-container{padding:16px;min-height:200px;position:relative}
.dag-node{display:inline-flex;align-items:center;gap:8px;background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);padding:8px 12px;font-size:12px;position:absolute}
.dag-node:hover{box-shadow:0 2px 8px rgba(0,0,0,.08)}
.dag-node .type-badge{font-family:'JetBrains Mono',monospace;font-size:10px;color:var(--accent);background:var(--accent-light);padding:2px 6px;border-radius:4px}

.json-viewer{background:var(--surface2);border:1px solid var(--border);border-radius:var(--radius);padding:16px;font-family:'JetBrains Mono',monospace;font-size:12px;color:var(--text);white-space:pre-wrap;word-break:break-all;max-height:400px;overflow-y:auto;line-height:1.6}

/* Runs */
.runs-filters{display:flex;gap:8px;flex-wrap:wrap;align-items:center;justify-content:flex-end}
.filter-select,.filter-input{background:var(--surface);border:1px solid var(--border);border-radius:7px;padding:7px 12px;color:var(--text);font-size:13px;font-family:inherit;outline:none}
.filter-select:focus,.filter-input:focus{border-color:#3898ec;box-shadow:0 0 0 3px rgba(56,152,236,.15)}
.filter-select option{background:var(--surface)}
.runs-search{width:340px;max-width:48vw;min-height:36px}
.runs-list-panel{display:flex;flex-direction:column;flex:1;overflow:hidden;min-height:0}
.runs-list-panel.hide{display:none}
.runs-detail-panel{display:none;flex-direction:column;flex:1;overflow:hidden;min-height:0}
.runs-detail-panel.show{display:flex}
.runs-detail-back{padding:10px 20px;border-bottom:1px solid var(--border);background:var(--surface);flex-shrink:0}
.runs-detail-panel #runs-detail{flex:1;overflow-y:auto;background:var(--surface2)}
.runs-list{flex:1;overflow-y:auto;min-height:0}
.runs-pagination{display:flex;align-items:center;justify-content:space-between;gap:8px;padding:10px 14px;border-top:1px solid var(--border);background:var(--surface2)}
.runs-page-info{font-size:11px;color:var(--text-dim)}
.runs-page-controls{display:flex;align-items:center;gap:6px}
.runs-page-index{font-size:11px;color:var(--text-dim);min-width:68px;text-align:center}
.btn-page{background:var(--surface);color:var(--text);border:1px solid var(--border);border-radius:6px;padding:5px 12px;font-size:11px;font-weight:600;cursor:pointer}
.btn-page:hover:not(:disabled){border-color:var(--accent);color:var(--accent)}
.btn-page:disabled{opacity:.4;cursor:not-allowed}
.runs-table{width:100%;border-collapse:collapse;background:var(--surface)}
.runs-table th{text-align:left;padding:9px 16px;font-size:11px;color:var(--text-dim);text-transform:uppercase;letter-spacing:.5px;border-bottom:2px solid var(--border);background:var(--surface2);white-space:nowrap;font-weight:700}
.runs-table td{padding:11px 16px;font-size:13px;border-bottom:1px solid var(--border);vertical-align:middle}
.runs-table tr:hover td{background:var(--accent-light);cursor:pointer}
.runs-table tr.run-row-active td{background:var(--accent-light)}
.runs-table tr.run-row-active td:first-child{border-left:3px solid var(--accent)}
.run-id-cell{font-family:'JetBrains Mono',monospace;font-size:12px;color:var(--accent);white-space:nowrap}
.run-duration-cell{font-family:'JetBrains Mono',monospace;font-size:12px;color:var(--text-dim);white-space:nowrap}
.run-trigger-cell{font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--text-dim);max-width:160px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.run-status-badge{font-size:10px;font-weight:700;padding:3px 8px;border-radius:100px;text-transform:uppercase;flex-shrink:0;letter-spacing:.3px}
.status-dot{width:8px;height:8px;border-radius:50%;display:inline-block;flex-shrink:0;vertical-align:middle;margin-right:6px}
.status-dot.Running{background:var(--warn);animation:pulse 1.5s infinite}.status-dot.Succeeded{background:var(--success)}.status-dot.Failed{background:var(--danger)}.status-dot.Skipped{background:var(--skip)}

.detail-empty{display:flex;align-items:center;justify-content:center;flex-direction:column;gap:8px;color:var(--text-dim);min-height:300px}.detail-empty .icon{font-size:40px;opacity:.25}
.detail-header{padding:16px 20px;border-bottom:1px solid var(--border);background:var(--surface)}
.detail-runid{font-family:'JetBrains Mono',monospace;font-size:13px;color:var(--accent)}

.timeline{display:flex;flex-direction:column}
.step-node{display:flex;align-items:stretch}
.step-connector{display:flex;flex-direction:column;align-items:center;width:28px;flex-shrink:0}
.step-circle{width:24px;height:24px;border-radius:50%;border:2px solid var(--border);display:flex;align-items:center;justify-content:center;font-size:10px;font-weight:700;flex-shrink:0;transition:all .3s;background:var(--surface)}
.step-circle.Running{border-color:var(--warn);color:var(--warn);animation:pulse 1.5s infinite}
.step-circle.Succeeded{border-color:var(--success);color:var(--success);background:var(--success-bg)}
.step-circle.Failed{border-color:var(--danger);color:var(--danger);background:var(--danger-bg)}
.step-circle.Pending{border-color:var(--text-light);color:var(--text-light)}.step-circle.Skipped{border-color:var(--skip);color:var(--skip);background:var(--skip-bg)}
.step-line{width:2px;flex:1;min-height:12px;background:var(--border)}.step-line.done{background:var(--success)}.step-line.last{display:none}
.step-card{flex:1;background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);padding:12px 16px;margin-bottom:10px;margin-left:8px}
.step-card.Running{border-left:3px solid var(--warn)}.step-card.Succeeded{border-left:3px solid var(--success)}.step-card.Failed{border-left:3px solid var(--danger)}.step-card.Skipped{border-left:2px dashed var(--border-dark);opacity:.6;background:transparent}
.step-card.step-target{outline:2px solid var(--accent);outline-offset:2px;background:var(--accent-light)}
.step-card-header{display:flex;align-items:center;justify-content:space-between}
.step-key{font-family:'JetBrains Mono',monospace;font-size:13px;font-weight:600;color:var(--text)}.step-type{font-size:12px;color:var(--text-dim);margin-top:1px}
.step-badge{font-size:10px;font-weight:700;padding:2px 8px;border-radius:100px;text-transform:uppercase;letter-spacing:.3px}
.step-badge.Running{background:var(--warn-bg);color:var(--warn-text);border:1px solid var(--warn-border)}.step-badge.Succeeded{background:var(--success-bg);color:var(--success-text);border:1px solid var(--success-border)}.step-badge.Failed{background:var(--danger-bg);color:var(--danger-text);border:1px solid var(--danger-border)}.step-badge.Pending{background:var(--surface2);color:var(--text-light);border:1px solid var(--border)}.step-badge.Skipped{background:var(--skip-bg);color:var(--skip-text);border:1px solid var(--skip-border)}
.step-timing{margin-top:6px;font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--text-dim);display:flex;gap:14px;flex-wrap:wrap}
.step-error{margin-top:6px;padding:8px 10px;background:var(--danger-bg);border:1px solid var(--danger-border);border-radius:var(--radius);font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--danger-text);word-break:break-all}
.step-output{margin-top:6px;padding:8px 10px;background:var(--surface2);border:1px solid var(--border);border-radius:var(--radius);font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--text);word-break:break-all;max-height:80px;overflow-y:auto}
.step-detail{margin-top:8px;border:1px solid var(--border);border-radius:var(--radius);background:var(--surface2)}
.step-detail-title{font-size:11px;font-weight:700;color:var(--text);padding:7px 12px;text-transform:uppercase;letter-spacing:.4px;border-bottom:1px solid var(--border);background:var(--surface);display:flex;align-items:center;justify-content:space-between}
.copy-panel-btn{background:transparent;border:none;cursor:pointer;width:24px;height:24px;border-radius:5px;display:inline-flex;align-items:center;justify-content:center;color:var(--text-light);transition:all .15s;flex-shrink:0;padding:0}
.copy-panel-btn:hover{background:var(--accent-light);color:var(--accent)}
.btn-copy{display:inline-flex;align-items:center;gap:5px;font-size:11px;font-weight:500;padding:4px 10px;border-radius:6px;background:transparent;color:var(--text-dim);border:1px solid var(--border-dark);cursor:pointer;transition:all .15s;white-space:nowrap}
.btn-copy:hover{background:var(--accent-light);color:var(--accent);border-color:var(--accent)}
.btn-copy svg{flex-shrink:0}
.btn-copy-icon{background:transparent;border:none;cursor:pointer;width:24px;height:24px;border-radius:5px;display:inline-flex;align-items:center;justify-content:center;color:var(--text-light);transition:all .15s;flex-shrink:0;padding:0}
.btn-copy-icon:hover{background:var(--accent-light);color:var(--accent)}
.step-detail-body{padding:10px 12px;font-family:'JetBrains Mono',monospace;font-size:11px;white-space:pre-wrap;word-break:break-word;max-height:200px;overflow:auto;color:var(--text)}
.step-detail.step-detail-error .step-detail-body{background:var(--danger-bg);color:var(--danger-text)}
.step-actions{margin-top:6px;display:flex;gap:6px}
.badge-cron{background:var(--accent-light);color:var(--accent);border:1px solid #F0C5B5;font-family:'JetBrains Mono',monospace;font-size:10px;font-weight:700;padding:2px 7px;border-radius:4px}
.badge-paused{background:var(--surface2);color:var(--text-dim);border:1px solid var(--border)}
.badge-active{background:var(--success-bg);color:var(--success-text);border:1px solid var(--success-border)}
.empty-msg{padding:28px;text-align:center;color:var(--text-dim);font-size:13px}

/* Scheduled */
.schedule-table{width:100%;border-collapse:collapse;font-size:13px;background:var(--surface)}
.schedule-table th{text-align:left;padding:9px 14px;font-size:11px;color:var(--text-dim);text-transform:uppercase;letter-spacing:.5px;border-bottom:1px solid var(--border);background:var(--surface2);font-weight:700}
.schedule-table td{padding:9px 14px;border-bottom:1px solid var(--border);vertical-align:middle}
.schedule-table tr:last-child td{border-bottom:none}
.schedule-table tr:hover td{background:var(--accent-light)}
.schedule-actions{display:flex;gap:4px;flex-wrap:nowrap}
.btn-sm{font-size:11px;padding:4px 10px;border-radius:5px;font-weight:600;border:1px solid transparent;cursor:pointer}
.btn-sm-primary{background:var(--accent);color:#fff;border-color:var(--accent)}.btn-sm-primary:hover{background:var(--accent-hover)}
.btn-sm-warning{background:var(--warn);color:#fff;border-color:var(--warn)}.btn-sm-warning:hover{filter:brightness(1.05)}
.btn-sm-success{background:var(--success);color:#fff;border-color:var(--success)}.btn-sm-success:hover{filter:brightness(1.1)}
.btn-sm-ghost{background:var(--surface);color:var(--text-dim);border:1px solid var(--border)}.btn-sm-ghost:hover{border-color:var(--accent);color:var(--accent)}
.btn-sm-trigger{background:var(--surface);color:#7C3AED;border:1px solid #C4B5FD}.btn-sm-trigger:hover{background:#F5F3FF;border-color:#7C3AED}
.cron-input{font-family:'JetBrains Mono',monospace;font-size:12px;padding:4px 8px;border:1px solid var(--border);border-radius:5px;width:140px;outline:none}
.cron-input:focus{border-color:#3898ec;box-shadow:0 0 0 3px rgba(56,152,236,.15)}
.cron-cell{display:flex;align-items:center;gap:6px}
.schedule-section{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);overflow:hidden}

.dag-svg{width:100%;overflow:auto}
.dag-svg svg text{font-family:'JetBrains Mono',monospace;font-size:11px}

/* Run detail — Timeline / DAG toggle */
.run-view-toggle{display:flex;align-items:center;gap:0;padding:10px 18px;border-bottom:1px solid var(--border);background:var(--surface)}
.run-view-btn{font-size:11px;font-weight:600;padding:5px 16px;border:1px solid var(--border-dark);background:transparent;color:var(--text-dim);cursor:pointer;transition:all .15s;line-height:1.5}
.run-view-btn:first-child{border-radius:6px 0 0 6px}
.run-view-btn:last-child{border-radius:0 6px 6px 0;margin-left:-1px}
.run-view-btn.active{background:var(--accent);color:#faf9f5;border-color:var(--accent);position:relative;z-index:1}
.run-view-btn:not(.active):hover{border-color:var(--accent);color:var(--accent)}

/* Live DAG in run detail */
.run-dag-wrap{padding:16px 18px;overflow:auto}
@keyframes dag-pulse{0%,100%{opacity:1}50%{opacity:.45}}
.dag-node-running rect{animation:dag-pulse 1.5s infinite}
.dag-node-selected rect.dag-node-bg{stroke:var(--accent) !important;stroke-width:2.5 !important;filter:drop-shadow(0 0 0 2px rgba(201,100,66,.25))}
.dag-node-blocked rect.dag-node-bg{stroke-dasharray:4 3;fill-opacity:.6}

/* Run detail — Tab strip (replaces run-view-toggle pattern) */
.tab-strip{display:flex;align-items:center;gap:2px;padding:8px 18px 0;border-bottom:1px solid var(--border);background:var(--surface)}
.tab-strip .tab{font-size:12px;font-weight:600;padding:8px 16px;border:none;background:transparent;color:var(--text-dim);cursor:pointer;transition:all .15s;border-bottom:2px solid transparent;border-radius:6px 6px 0 0;font-family:inherit}
.tab-strip .tab:hover{color:var(--accent);background:var(--accent-light)}
.tab-strip .tab.active{color:var(--accent);border-bottom-color:var(--accent);background:var(--surface)}

/* Run header — progress, live dot, action row */
.run-header-actions{display:flex;align-items:center;gap:8px;margin-top:10px;flex-wrap:wrap}
.progress-bar{flex:1;min-width:120px;max-width:260px;height:6px;background:var(--surface2);border:1px solid var(--border);border-radius:100px;overflow:hidden;position:relative}
.progress-fill{height:100%;background:var(--accent);transition:width .4s ease}
.progress-label{font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--text-dim);white-space:nowrap}
.live-dot{display:inline-flex;align-items:center;gap:6px;font-size:11px;font-weight:600;color:var(--warn-text);text-transform:uppercase;letter-spacing:.5px}
.live-dot::before{content:"";width:8px;height:8px;border-radius:50%;background:var(--warn);animation:pulse 1.5s infinite}
.btn-action{display:inline-flex;align-items:center;gap:5px;font-size:11px;font-weight:600;padding:6px 12px;border-radius:6px;background:var(--surface);color:var(--text);border:1px solid var(--border-dark);cursor:pointer;transition:all .15s;font-family:inherit}
.btn-action:hover:not(:disabled){border-color:var(--accent);color:var(--accent)}
.btn-action:disabled{opacity:.4;cursor:not-allowed}
.btn-action.danger{color:var(--danger-text);border-color:var(--danger-border)}
.btn-action.danger:hover:not(:disabled){background:var(--danger-bg);border-color:var(--danger);color:var(--danger)}
.btn-action.primary{background:var(--accent);color:#faf9f5;border-color:var(--accent)}
.btn-action.primary:hover:not(:disabled){background:var(--accent-hover);border-color:var(--accent-hover);color:#faf9f5}

/* Timeline — selected card ring */
.step-card.selected{box-shadow:0 0 0 2px var(--accent),0 0 0 6px rgba(201,100,66,.12);border-color:var(--accent)}

/* Gantt */
.gantt-wrap{padding:16px 18px;overflow:auto}
.gantt-svg text{font-family:'JetBrains Mono',monospace;font-size:11px;fill:var(--text-dim)}
.gantt-svg .gantt-gutter-label{fill:var(--text);font-weight:600}
.gantt-svg .gantt-axis line{stroke:var(--border)}
.gantt-svg .gantt-axis-tick{fill:var(--text-light);font-size:10px}
.gantt-svg .gantt-bar{cursor:pointer;transition:opacity .15s}
.gantt-svg .gantt-bar:hover{opacity:.82}
.gantt-svg .gantt-bar-selected{stroke:var(--accent);stroke-width:2.5}
.gantt-svg .gantt-duration{fill:var(--text-light);font-size:10px}
.gantt-empty{padding:32px;text-align:center;color:var(--text-dim);font-size:13px}

/* Events drawer */
.events-drawer{position:fixed;top:0;right:0;width:460px;max-width:100vw;height:100vh;background:var(--surface);border-left:1px solid var(--border);box-shadow:-4px 0 16px rgba(0,0,0,.06);transform:translateX(100%);transition:transform .25s ease;z-index:900;display:flex;flex-direction:column}
.events-drawer.open{transform:translateX(0)}
.events-drawer-header{padding:14px 18px;border-bottom:1px solid var(--border);background:var(--surface2);display:flex;align-items:center;justify-content:space-between;flex-shrink:0}
.events-drawer-title{font-size:13px;font-weight:700;color:var(--text);text-transform:uppercase;letter-spacing:.5px}
.events-drawer-close{background:transparent;border:none;cursor:pointer;color:var(--text-dim);font-size:18px;width:28px;height:28px;border-radius:6px;display:inline-flex;align-items:center;justify-content:center}
.events-drawer-close:hover{background:var(--accent-light);color:var(--accent)}
.events-drawer-body{flex:1;overflow-y:auto;padding:12px 18px}
.event-group{margin-bottom:14px}
.event-group-title{font-family:'JetBrains Mono',monospace;font-size:12px;font-weight:600;color:var(--accent);padding:6px 0;border-bottom:1px solid var(--border);margin-bottom:6px}
.event-item{display:flex;gap:8px;padding:5px 0;font-size:12px;border-bottom:1px dashed var(--border)}
.event-item:last-child{border-bottom:none}
.event-time{font-family:'JetBrains Mono',monospace;font-size:10px;color:var(--text-light);flex-shrink:0;width:72px}
.event-kind{font-weight:600;color:var(--text);flex-shrink:0;width:130px;text-transform:uppercase;letter-spacing:.3px;font-size:10px}
.event-detail{font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--text-dim);word-break:break-word;flex:1}

/* Inspector strip shared by DAG/Gantt */
.step-inspector{margin-top:16px;padding:14px 16px;background:var(--surface);border:1px solid var(--border);border-radius:var(--radius)}
.step-inspector-empty{padding:20px;text-align:center;color:var(--text-dim);font-size:12px;border:1px dashed var(--border);border-radius:var(--radius);margin-top:16px}
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
    <div id="runs-list-panel" class="runs-list-panel">
      <div class="page-header">
        <div class="page-title">Runs</div>
        <div class="runs-filters">
          <input class="filter-input runs-search" id="runs-filter-search" type="search" placeholder="Search run, step, error, output..." oninput="onRunsSearchInput()" onkeydown="onRunsSearchKeydown(event)"/>
          <select class="filter-select" id="runs-filter-flow" onchange="onRunsFilterChange()"><option value="">All Flows</option></select>
          <select class="filter-select" id="runs-filter-status" onchange="onRunsFilterChange()">
            <option value="">All Statuses</option>
            <option value="Running">Running</option>
            <option value="Succeeded">Succeeded</option>
            <option value="Skipped">Blocked</option>
            <option value="Failed">Failed</option>
          </select>
        </div>
      </div>
      <div class="runs-list" id="runs-list"></div>
      <div class="runs-pagination" id="runs-pagination"></div>
    </div>
    <div id="runs-detail-panel" class="runs-detail-panel">
      <div class="runs-detail-back">
        <div class="back-btn" onclick="backToRunsList()">&#8592; Back to Runs</div>
      </div>
      <div id="runs-detail">
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
let currentRunView = 'timeline'; // 'timeline' | 'dag' | 'gantt' | 'events'
let currentRun = null; // cache of last loaded run for tab re-renders
let currentRunManifest = null; // cached resolved manifest for DAG/Gantt
let eventsDrawerOpen = false;
let runsView = 'list'; // 'list' | 'detail'
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
function stepStatusLabel(s) { return s === 'Skipped' ? 'Blocked' : s; }
function statusBadge(s) { const label = s === 'Skipped' ? 'Blocked' : s; return '<span class="badge badge-'+s.toLowerCase()+'">'+label+'</span>'; }

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

function copyPanelContent(btn) {
  const body = btn.closest('.step-detail').querySelector('.step-detail-body');
  const text = body ? body.textContent : '';
  navigator.clipboard.writeText(text).then(() => showToast('Copied!')).catch(() => alert('Copy failed'));
}

function showToast(msg) {
  const t = document.createElement('div');
  t.textContent = msg;
  t.style.cssText = 'position:fixed;bottom:24px;left:50%;transform:translateX(-50%);background:#141413;color:#b0aea5;padding:8px 20px;border-radius:12px;font-size:13px;z-index:9999;transition:opacity .4s;font-family:Inter,sans-serif;border:1px solid #30302e';
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

function switchRunView(view) {
  if (!['timeline','dag','gantt','events'].includes(view)) view = 'timeline';
  currentRunView = view;
  if (selectedRunId) setRunRoute(selectedRunId, selectedStepKey, view);
  const tabsEl = $('run-tabs');
  if (tabsEl) tabsEl.innerHTML = renderTabs(view);
  const bodyEl = $('run-tab-body');
  if (bodyEl && currentRun) {
    bodyEl.innerHTML = renderActiveTab(currentRun, currentRunManifest, view);
    if (view === 'timeline' && selectedStepKey) scrollToStep(selectedStepKey);
  }
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
    .concat(isRunning ? ['<button class="btn-action danger" onclick="cancelRun(\''+runId+'\')">Cancel run</button>'] : [])
    .concat(failedCount > 0 ? ['<button class="btn-action" onclick="retryAllFailed(\''+runId+'\')">Retry failed steps ('+failedCount+')</button>'] : [])
    .concat(['<button class="btn-action primary" onclick="rerunAll(\''+runId+'\')">Re-run all</button>']);
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
    +'</div>'
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

async function cancelRun(runId) {
  if (!confirm('Cancel run "'+runId+'"?')) return;
  try {
    const res = await fetch(BASE+'/runs/'+runId+'/cancel', { method:'POST' });
    const data = await res.json();
    if (!res.ok || data.accepted === false) alert(data.message || data.error || 'Cancel request rejected.');
    await selectRun(runId, true, selectedStepKey, currentRunView);
  } catch(e) { alert('Failed to cancel run: '+e.message); }
}

async function retryAllFailed(runId) {
  if (!currentRun) return;
  const failed = (currentRun.steps || []).filter(s => s.status === 'Failed');
  if (failed.length === 0) { alert('No failed steps to retry.'); return; }
  if (!confirm('Retry '+failed.length+' failed step'+(failed.length!==1?'s':'')+'?')) return;
  for (const s of failed) {
    try {
      await fetch(BASE+'/runs/'+runId+'/steps/'+encodeURIComponent(s.stepKey)+'/retry', { method:'POST' });
    } catch(e) { console.error('Retry failed for', s.stepKey, e); }
  }
  await selectRun(runId, true, selectedStepKey, currentRunView);
}

async function rerunAll(runId) {
  if (!confirm('Re-run this flow with the original trigger payload?')) return;
  try {
    const res = await fetch(BASE+'/runs/'+runId+'/rerun', { method:'POST' });
    const data = await res.json();
    if (!res.ok || !data.runId) { alert(data.error || 'Re-run failed.'); return; }
    await selectRun(data.runId, false, null, 'timeline');
  } catch(e) { alert('Failed to re-run: '+e.message); }
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
    : '<table class="runs-table"><thead><tr>'
      +'<th>Status</th><th>Run ID</th><th>Flow</th><th>Trigger</th><th>Started</th><th>Duration</th>'
      +'</tr></thead><tbody>'
      + allRuns.map(r => {
        const active = r.id === selectedRunId ? 'run-row-active' : '';
        const trigger = r.triggerKey ? esc(r.triggerKey) : '<span style="color:var(--text-light)">—</span>';
        const dur = duration(r.startedAt, r.completedAt);
        return '<tr class="'+active+'" onclick="selectRun(\''+r.id+'\')">'
          +'<td><span class="run-status-badge badge-'+r.status.toLowerCase()+'">'+r.status+'</span></td>'
          +'<td class="run-id-cell">'+r.id.slice(0,8)+'\u2026</td>'
          +'<td>'+esc(r.flowName||'Unknown')+'</td>'
          +'<td class="run-trigger-cell" title="'+esc(r.triggerKey||'')+'">'+trigger+'</td>'
          +'<td style="font-size:12px;color:var(--text-dim);white-space:nowrap">'+fmtDate(r.startedAt)+'</td>'
          +'<td class="run-duration-cell">'+(dur||'—')+'</td>'
          +'</tr>';
      }).join('')
      +'</tbody></table>';

  if (preserveScroll) {
    runsListEl.scrollTop = listScrollTop;
  }

  renderRunsPagination();
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

    renderRuns(preserveScroll);
  } catch(e) { console.error('Runs load error', e); }
}

async function selectRun(id, preserveScroll, targetStepKey, view) {
  selectedRunId = id;
  selectedStepKey = targetStepKey || null;
  if (view && ['timeline','dag','gantt','events'].includes(view)) currentRunView = view;
  setRunRoute(id, selectedStepKey, currentRunView);

  showRunsDetailView();
  const detailEl = $('runs-detail');
  const detailScrollTop = preserveScroll && detailEl ? detailEl.scrollTop : 0;
  try {
    const run = await fetchJSON(BASE+'/runs/'+id);
    currentRun = run;
    const steps = run.steps || [];
    if (currentRunView === 'dag' || currentRunView === 'gantt') {
      currentRunManifest = await resolveFlowManifest(run);
    } else {
      currentRunManifest = currentRunManifest || null;
    }

    $('runs-detail').innerHTML =
      renderRunHeader(run, steps)
      +'<div class="tab-strip" id="run-tabs">'+renderTabs(currentRunView)+'</div>'
      +'<div id="run-tab-body">'+renderActiveTab(run, currentRunManifest, currentRunView)+'</div>';

    if (preserveScroll) $('runs-detail').scrollTop = detailScrollTop;
    if (currentRunView === 'timeline' && selectedStepKey) scrollToStep(selectedStepKey);
    if (currentRunView === 'events') openEventsDrawer(id);
    else closeEventsDrawer();
  } catch(e) { console.error('Run detail error', e); }
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
      +(s.status==='Failed'?'<button class="btn-retry" onclick="retryStep(\''+runId+'\',\''+esc(s.stepKey)+'\')">&#8635; Retry</button>':'')
      +(s.status!=='Skipped'?'<button class="btn-copy-icon" onclick="copyStepLink(\''+runId+'\',\''+esc(s.stepKey)+'\')" title="Copy step link"><svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="2" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg></button>':'')
      +'</div></div>'
      +(s.status==='Skipped'?'':('<div class="step-timing"><span>Start: '+fmt(s.startedAt)+'</span><span>End: '+fmt(s.completedAt)+'</span><span>Duration: '+duration(s.startedAt,s.completedAt)+'</span></div>'))
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
      if (runsView === 'detail' && selectedRunId) {
        await selectRun(selectedRunId, true, selectedStepKey, currentRunView);
      } else if (!isRunsAutoRefreshBlocked()) {
        await loadRuns(true);
      }
    }
    if (currentPage === 'scheduled') await loadScheduled();
  } catch(e) {}
}

function parseHash() {
  const hash = location.hash || '';
  if (!hash || hash === '#' || hash === '#/') return { page: 'overview', runId: null, stepKey: null, view: null };
  const raw = hash.startsWith('#/') ? hash.slice(2) : hash.slice(1);
  const qIdx = raw.indexOf('?');
  const path = qIdx >= 0 ? raw.slice(0, qIdx) : raw;
  const query = qIdx >= 0 ? raw.slice(qIdx + 1) : '';
  let view = null;
  if (query) {
    const params = new URLSearchParams(query);
    const v = params.get('view');
    if (['timeline','dag','gantt','events'].includes(v)) view = v;
  }
  const parts = path.split('/');
  const page = ['overview','flows','runs','scheduled'].includes(parts[0]) ? parts[0] : 'overview';
  if (page !== 'runs') return { page, runId: null, stepKey: null, view: null };
  const runId = parts[1] || null;
  const stepKey = (parts[2] === 'steps' && parts[3]) ? decodeURIComponent(parts[3]) : null;
  return { page, runId, stepKey, view };
}

function setRunRoute(runId, stepKey, view) {
  let hash = '#/runs/' + runId;
  if (stepKey) hash += '/steps/' + encodeURIComponent(stepKey);
  if (view && view !== 'timeline') hash += '?view=' + view;
  if (location.hash !== hash) history.replaceState(null, '', hash);
}

async function applyRoute(route) {
  _navigate(route.page);
  if (route.page === 'runs') {
    if (route.runId) {
      await selectRun(route.runId, false, route.stepKey, route.view);
    } else {
      showRunsListView();
      await loadRuns(false);
    }
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
