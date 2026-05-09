import { el, t, sevCss, formatBytes, wrapAddresses, linkifyAnchors, indentClass } from './report.dom.js';

export function renderBlocks(blocks, container, announce) {
  if (!blocks || !blocks.length) return;
  const stack = [container];
  if (container && !container._headingCounter) container._headingCounter = 0;
  for (const block of blocks) {
    const top = stack[stack.length - 1];
    switch (block.type) {
      case 'heading': {
        const d = el('div', 'detail-subheading' + indentClass(block.indentLevel || 0));
        d.textContent = block.text || '';
        try {
          const sidx = container && container.dataset && container.dataset.sectionIndex;
          if (sidx != null) {
            const idx = Number(sidx);
            const counter = (container._headingCounter = (container._headingCounter || 0) + 1) - 1;
            d.id = `detail-${idx}-heading-${counter}`;
          }
        } catch (e) { }
        top.appendChild(d);
        break;
      }
      case 'metric': {
        const d = el('div', 'detail-line' + indentClass(block.indentLevel || 0));
        const k = el('span', 'detail-key'); k.textContent = (block.label || '') + ':';
        const v = el('span', 'detail-value wrap'); v.textContent = block.value || '';
        d.appendChild(k); d.appendChild(t(' ')); d.appendChild(v);
        top.appendChild(d);
        wrapAddresses(d);
        break;
      }
      case 'path': {
        const d = el('div', 'detail-line' + indentClass(block.indentLevel || 0));
        const k = el('span', 'detail-key'); k.textContent = (block.label || '') + ':';
        const v = el('span', 'detail-path wrap'); v.textContent = block.path || '';
        d.appendChild(k); d.appendChild(t(' ')); d.appendChild(v);
        top.appendChild(d);
        wrapAddresses(d);
        break;
      }
      case 'stackframe': {
        const d = el('div', 'detail-line' + indentClass(block.indentLevel || 0));
        const span = el('span', block.isFrameworkFrame ? 'detail-frame framework' : 'detail-frame');
        span.textContent = block.frame || '';
        d.appendChild(span);
        top.appendChild(d);
        wrapAddresses(d);
        break;
      }
      case 'text': {
        const d = el('div', 'detail-line' + indentClass(block.indentLevel || 0));
        d.textContent = block.text || '';
        top.appendChild(d);
        wrapAddresses(d);
        break;
      }
      case 'listItem': {
        const d = el('div', 'detail-line' + indentClass(block.indentLevel || 0));
        d.textContent = '\u2022 ' + (block.text || '');
        top.appendChild(d);
        break;
      }
      case 'divider': {
        top.appendChild(el('div', 'detail-divider'));
        break;
      }
      case 'blank': {
        top.appendChild(el('div', 'detail-gap'));
        break;
      }
      case 'table': {
        top.appendChild(buildDetailTable(block, announce));
        break;
      }
      case 'collapsibleBegin': {
        const details = el('details', 'detail-nested');
        const summary = el('summary'); summary.textContent = block.title || '';
        details.appendChild(summary);
        const content = el('div', 'detail-nested-content');
        details.appendChild(content);
        top.appendChild(details);
        stack.push(content);
        break;
      }
      case 'collapsibleEnd': {
        if (stack.length > 1) stack.pop();
        break;
      }
      default:
        break;
    }
  }
}

export function buildDetailTable(block, announce) {
  const container = el('div', 'table-with-pagination');
  const tbl = el('table');
  if (block.caption) { const cap = document.createElement('caption'); cap.textContent = block.caption; tbl.appendChild(cap); }
  const thead = el('thead'); const htr = el('tr');
  for (const h of (block.headers || [])) { const th = document.createElement('th'); th.scope = 'col'; th.textContent = h; htr.appendChild(th); }
  thead.appendChild(htr); tbl.appendChild(thead);
  const tbody = el('tbody');

  const rowElements = [];
  for (const row of (block.rows || [])) {
    const tr = el('tr');
    for (const cell of (row.cells || [])) {
      const td = document.createElement('td');
      const disp = cell.display || '';
      if (disp.startsWith('__SPARK__')) {
        const payload = disp.substring('__SPARK__'.length);
        td.setAttribute('data-sparkline', payload);
      } else {
        const linkMarker = '||__LINK__';
        const li = disp.indexOf(linkMarker);
        if (li >= 0) {
          const left = disp.substring(0, li);
          const target = disp.substring(li + linkMarker.length);
          td.textContent = left;
          const a = document.createElement('a'); a.className = 'trend-jump'; a.href = '#' + target; a.setAttribute('aria-label', 'Jump to snapshot'); a.textContent = ' ↳'; td.appendChild(a);
        } else {
          td.textContent = disp;
        }
      }
      if (cell.rawValue != null) td.dataset.value = cell.rawValue;
      wrapAddresses(td);
      tr.appendChild(td);
    }
    rowElements.push(tr);
  }

  tbl.appendChild(tbody);

  const controls = el('div', 'table-pagination-controls'); controls.setAttribute('role', 'group'); controls.setAttribute('aria-label', 'Table pagination');
  const prev = el('button', 'action-btn table-prev'); prev.type = 'button'; prev.textContent = '← Prev'; prev.setAttribute('aria-label', 'Previous rows');
  const next = el('button', 'action-btn table-next'); next.type = 'button'; next.textContent = 'Next →'; next.setAttribute('aria-label', 'Next rows');
  const info = el('span', 'page-info');
  const sizeSel = document.createElement('select'); sizeSel.setAttribute('aria-label', 'Rows per page');
  [[10,'10'],[20,'20'],[50,'50'],[0,'All']].forEach(function (opt) { const o = document.createElement('option'); o.value = String(opt[0]); o.text = String(opt[1]); sizeSel.appendChild(o); });
  controls.appendChild(prev); controls.appendChild(info); controls.appendChild(next); controls.appendChild(t(' ')); controls.appendChild(sizeSel);

  let pageSize = 10; let pageIndex = 0;
  function renderTablePage() {
    tbody.innerHTML = '';
    const total = rowElements.length;
    const start = pageSize === 0 ? 0 : pageIndex * pageSize;
    const end = pageSize === 0 ? total : Math.min(total, start + pageSize);
    for (let i = start; i < end; i++) tbody.appendChild(rowElements[i]);
    info.textContent = pageSize === 0 ? `${total} rows` : `${start + 1}-${end} of ${total}`;
    prev.disabled = (pageIndex === 0) || (pageSize === 0);
    next.disabled = (end >= total) || (pageSize === 0);
    renderSparklines();
    controls.style.display = (total <= pageSize || pageSize === 0) ? 'none' : '';
    if (pageSize !== 0 && announce) announce(`Showing ${start + 1} to ${end} of ${total} rows`);
  }
  prev.addEventListener('click', function () { if (pageSize === 0) return; if (pageIndex > 0) { pageIndex--; renderTablePage(); } });
  next.addEventListener('click', function () { if (pageSize === 0) return; pageIndex++; renderTablePage(); });
  sizeSel.addEventListener('change', function () { pageSize = parseInt(sizeSel.value, 10) || 0; pageIndex = 0; renderTablePage(); });
  sizeSel.value = String(pageSize);

  container.appendChild(controls); container.appendChild(tbl);
  renderTablePage();
  if (rowElements.length <= pageSize || pageSize === 0) controls.style.display = rowElements.length <= pageSize ? 'none' : '';
  return container;
}

export function renderSparklines() {
  const tds = document.querySelectorAll('td[data-sparkline]');
  for (const td of tds) {
    try {
      const payload = JSON.parse(td.getAttribute('data-sparkline'));
      const values = (payload && payload.values) || [];
      const w = 84, h = 20, pad = 2;
      const nums = values.map(v => isFinite(v) ? v : NaN);
      const valid = nums.filter(n => !Number.isNaN(n));
      const min = valid.length ? Math.min(...valid) : 0;
      const max = valid.length ? Math.max(...valid) : 1;
      const range = max - min || 1;
      const points = [];
      for (let i = 0; i < nums.length; i++) {
        const v = nums[i];
        const x = pad + (i * (w - pad*2) / Math.max(1, nums.length - 1));
        const y = Number.isNaN(v) ? h - pad : pad + (1 - (v - min) / range) * (h - pad*2);
        points.push([x.toFixed(1), y.toFixed(1)].join(','));
      }
      const ns = 'http://www.w3.org/2000/svg';
      const svg = document.createElementNS(ns, 'svg');
      svg.setAttribute('viewBox', `0 0 ${w} ${h}`);
      svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
      svg.classList.add('sparkline'); svg.style.width = '6.5em'; svg.style.height = '1.6em'; svg.setAttribute('role', 'img');
      const poly = document.createElementNS(ns, 'polyline'); poly.setAttribute('fill', 'none'); poly.setAttribute('stroke-width', '1.5'); poly.setAttribute('points', points.join(' '));
      let trend = 'flat';
      if (valid.length >= 2) {
        const first = valid[0]; const last = valid[valid.length - 1]; const diff = last - first;
        if (Math.abs(diff) < Math.max(1e-6, Math.abs(first) * 0.005)) trend = 'flat'; else trend = diff > 0 ? 'up' : 'down';
      }
      const strokeColor = (payload && payload.color) || (trend === 'up' ? '#059669' : (trend === 'down' ? '#b91c1c' : '#6b7280'));
      poly.setAttribute('stroke', strokeColor);
      svg.appendChild(poly);
      const minVal = (payload && payload.min != null) ? payload.min : (valid.length ? min : null);
      const maxVal = (payload && payload.max != null) ? payload.max : (valid.length ? max : null);
      const latest = (payload && payload.latest != null) ? payload.latest : (valid.length ? valid[valid.length - 1] : null);
      const tooltipParts = [];
      if (minVal != null) tooltipParts.push('min: ' + String(minVal));
      if (maxVal != null) tooltipParts.push('max: ' + String(maxVal));
      if (latest != null) tooltipParts.push('latest: ' + String(latest));
      const title = document.createElementNS(ns, 'title'); title.textContent = tooltipParts.join('; '); svg.appendChild(title);
      td.textContent = ''; td.appendChild(svg);
    } catch (e) { }
  }
}

export function buildHeader(doc) {
  const isTrend = !!doc.isTrendReport;
  const title = isTrend ? 'DumpDetective Trend Analysis Report' : 'DumpDetective Analysis Report';
  const dumpLabel = isTrend ? 'Latest dump' : 'Dump';
  const rawName = (doc.dumpPath || 'report').replace(/\\/g, '/').split('/').pop() || 'report';
  const exportName = rawName.replace(/\.[^.]+$/, '') || 'report';
  const sec = el('section', 'header-card'); const h1 = document.createElement('h1'); h1.textContent = title; sec.appendChild(h1);
  const grid = el('div', 'meta-grid');
  function metaItem(label, value) { const d = el('div', 'meta-item'); const s = el('span', 'meta-label'); s.textContent = label + ':'; d.appendChild(s); d.appendChild(t(' ' + value)); return d; }
  grid.appendChild(metaItem(dumpLabel, doc.dumpPath || ''));
  const genRaw = doc.generatedAtUtc; const genStr = genRaw ? (new Date(genRaw)).toISOString().replace('T', ' ').slice(0, 19) + ' UTC' : '';
  grid.appendChild(metaItem('Generated (UTC)', genStr)); grid.appendChild(metaItem('Elapsed', ((doc.elapsedSeconds) || 0).toFixed(1) + 's')); grid.appendChild(metaItem('Schema', doc.schemaVersion || ''));
  sec.appendChild(grid);
  const dedup = doc.dedupDiagnostics; if (dedup) { const d = el('div', 'dedup-note'); d.textContent = 'Dedup: merged ' + dedup.mergedSections + '/' + dedup.duplicateCandidates + ' candidate duplicates'; sec.appendChild(d); }
  if (isTrend) { const td = el('div', 'dedup-note'); td.textContent = 'Dumps analyzed: ' + (doc.trendDumpCount || 0); sec.appendChild(td); if (doc.trendDumpPaths && doc.trendDumpPaths.length) { const dp = el('div', 'dedup-note'); const strong = document.createElement('strong'); strong.textContent = 'Analyzed dumps:'; dp.appendChild(strong); for (const p of doc.trendDumpPaths) { dp.appendChild(document.createElement('br')); dp.appendChild(t('\u2022 ' + p)); } sec.appendChild(dp); } }
  const bar = el('div', 'action-bar'); bar.setAttribute('role', 'toolbar'); bar.setAttribute('aria-label', 'Report actions');
  function actionBtn(id, ariaLabel, txt) { const btn = el('button', 'action-btn'); btn.type = 'button'; btn.id = id; btn.dataset.filename = exportName; btn.setAttribute('aria-label', ariaLabel); btn.textContent = txt; return btn; }
  bar.appendChild(actionBtn('btn-download-json', 'Download report as JSON', '\u2B07 JSON'));
  bar.appendChild(actionBtn('btn-export-csv', 'Export findings as CSV', '\u2B07 CSV'));
  bar.appendChild(actionBtn('btn-print', 'Print this report', '\u2399 Print'));
  bar.appendChild(actionBtn('btn-toggle-contrast', 'Toggle high contrast mode', '\u263C Contrast'));
  sec.appendChild(bar);
  return sec;
}

export function buildIncidentContext(doc) {
  const ctx = doc.incidentContext; if (!ctx) return null;
  const sec = el('section', 'section-card'); const h2 = document.createElement('h2'); h2.textContent = 'Incident Context'; sec.appendChild(h2);
  const tbl = el('table'); const thead = el('thead'); const htr = el('tr'); for (const col of ['Field', 'Value']) { const th = document.createElement('th'); th.scope = 'col'; th.textContent = col; htr.appendChild(th); } thead.appendChild(htr); tbl.appendChild(thead);
  const tbody = el('tbody'); function row(label, value) { const tr = el('tr'); const td1 = document.createElement('td'); td1.textContent = label; tr.appendChild(td1); const td2 = document.createElement('td'); td2.className = 'wrap'; td2.textContent = value || ''; tr.appendChild(td2); tbody.appendChild(tr); }
  row('Mode', ctx.mode || ''); row('Dump Path', ctx.dumpPath || ''); if (ctx.baselineDumpPath) row('Baseline Dump', ctx.baselineDumpPath); row('Report', (ctx.reportFormat || '') + ' / ' + (ctx.reportAudience || '')); row('Config', (ctx.usedConfigFile ? 'config file' : 'command line') + (ctx.configPath ? ' (' + ctx.configPath + ')' : '')); row('Diagnostic Mode', ctx.diagnosticMode ? 'on' : 'off'); row('Index Prebuild', ctx.indexPrebuildMode || ''); row('Runtime', (ctx.runtimeFlavor || 'n/a') + (ctx.runtimeVersion ? ' ' + ctx.runtimeVersion : '')); row('GC Mode', ctx.gcMode || 'n/a'); row('Heap Count', ctx.heapCount != null ? String(ctx.heapCount) : 'n/a'); row('Heap Walkable', ctx.heapCanWalk ? 'yes' : 'no'); row('Active Analyzers', String(ctx.activeAnalyzerCount || 0)); row('Analysis Elapsed', (Number(ctx.analysisElapsedSeconds || 0)).toFixed(1) + 's');
  tbl.appendChild(tbody); sec.appendChild(tbl);
  if (ctx.trendSnapshots && ctx.trendSnapshots.length) {
    const h3 = document.createElement('h3'); h3.textContent = 'Snapshot Contexts'; sec.appendChild(h3);
    const stbl = el('table'); const sthead = el('thead'); const shtr = el('tr'); for (const col of ['Snapshot', 'Dump', 'Elapsed', 'Analyzers', 'Findings']) { const th = document.createElement('th'); th.scope = 'col'; th.textContent = col; shtr.appendChild(th); } sthead.appendChild(shtr); stbl.appendChild(sthead);
    const stbody = el('tbody'); for (const snap of ctx.trendSnapshots) { const tr = el('tr'); const label = snap.isBaseline ? 'Baseline' : (snap.isCurrent ? 'Current' : ('Snapshot ' + (snap.index + 1))); const values = [label, snap.dumpPath || '', (Number(snap.elapsedSeconds || 0)).toFixed(1) + 's', String(snap.analyzerCount || 0), String(snap.findingCount || 0)]; values.forEach(function (value) { const td = document.createElement('td'); td.className = 'wrap'; td.textContent = value; tr.appendChild(td); }); stbody.appendChild(tr); } stbl.appendChild(stbody); sec.appendChild(stbl);
  }
  return sec;
}

export function buildExecutiveSummary(doc) {
  const ex = doc.executiveSummary; if (!ex) return null; const sec = el('section', 'section-card'); const h2 = document.createElement('h2'); h2.textContent = 'Executive Summary'; sec.appendChild(h2);
  const tbl = el('table'); const thead = el('thead'); const htr = el('tr'); for (const col of ['Signal', 'Value']) { const th = document.createElement('th'); th.scope = 'col'; th.textContent = col; htr.appendChild(th); } thead.appendChild(htr); tbl.appendChild(thead);
  const tbody = el('tbody'); const rows = [['Total Managed Bytes', formatBytes(ex.totalManagedBytes || 0)], ['Leak Likelihood Score', (ex.leakLikelihoodScore || 0) + '/100'], ['GC Pressure Score', (ex.gcPressureScore || 0) + '/100'], ['Thread Contention Score', (ex.threadContentionScore || 0) + '/100']]; for (const [label, value] of rows) { const tr = el('tr'); const td1 = document.createElement('td'); td1.textContent = label; tr.appendChild(td1); const td2 = document.createElement('td'); td2.className = 'wrap'; td2.textContent = value; tr.appendChild(td2); tbody.appendChild(tr); } tbl.appendChild(tbody); sec.appendChild(tbl);
  const recs = ex.topRecommendations; if (recs && recs.length) { const h3 = document.createElement('h3'); h3.textContent = 'Top Recommendations'; sec.appendChild(h3); const ul = document.createElement('ul'); for (const rec of recs) { const li = document.createElement('li'); const badge = el('span', 'severity-badge ' + sevCss(rec.severity)); badge.textContent = rec.severity || ''; li.appendChild(badge); li.appendChild(t(' ' + (rec.title || ''))); ul.appendChild(li); } sec.appendChild(ul); }
  return sec;
}

export function buildExecutiveBanner(doc) {
  const ex = doc.executiveSummary; if (!ex) return null; const banner = el('section', 'section-card exec-banner'); const row = el('div', 'exec-row');
  function splitValueUnit(s) { if (!s) return { value: '', unit: '' }; if (s.endsWith('%')) return { value: s.slice(0, -1).trim(), unit: '%' }; const parts = String(s).trim().split(/\s+/); if (parts.length >= 2 && /[A-Za-z%]+/.test(parts[parts.length - 1])) { const unit = parts.pop(); return { value: parts.join(' '), unit: unit }; } const m = s.match(/^(\d+)(\/\d+)$/); if (m) return { value: m[1], unit: m[2] }; return { value: s, unit: '' }; }
  function findTrendForKey(key) { if (!ex) return null; if (ex.trends && ex.trends[key]) return ex.trends[key]; const candidates = [key + 'Change', key + 'Delta', key + 'DeltaPct', key + 'PercentChange', key + 'Pct']; for (const c of candidates) { if (Object.prototype.hasOwnProperty.call(ex, c)) { return { raw: ex[c] }; } } return null; }
  function metric(label, valueStr, hint, key) { const m = el('div', 'exec-metric'); const vwrap = el('div', 'exec-meta'); const parts = splitValueUnit(valueStr || ''); const v = el('div', 'exec-value'); v.textContent = parts.value || ''; vwrap.appendChild(v); if (parts.unit) { const u = el('div', 'exec-unit'); u.textContent = parts.unit; vwrap.appendChild(u); } m.appendChild(vwrap);
    if (key) { const tr = findTrendForKey(key); if (tr) { let txt = ''; let display; if (typeof tr === 'object' && (tr.pct != null || tr.delta != null || tr.raw != null)) { const val = tr.pct != null ? tr.pct : (tr.delta != null ? tr.delta : tr.raw); const sign = (Number(val) > 0) ? '\u25B2' : (Number(val) < 0 ? '\u25BC' : '\u25AC'); display = String(val).toString(); txt = sign + ' ' + display; } else if (typeof tr === 'number') { display = String(tr); txt = display; } if (txt) { const tspan = el('div', 'exec-trend'); const displayStr = String(display || '').trim(); const numeric = Number(displayStr.replace(/[^0-9.-]+/g, '')); let cls = 'trend-flat'; if (!Number.isNaN(numeric)) { cls = numeric > 0 ? 'trend-up' : (numeric < 0 ? 'trend-down' : 'trend-flat'); } tspan.className = 'exec-trend ' + cls; tspan.textContent = txt; tspan.title = label + ' trend: ' + txt; const ariaPrefix = (!Number.isNaN(numeric) ? (numeric > 0 ? 'Increased ' : (numeric < 0 ? 'Decreased ' : 'No change ')) : 'Trend: '); tspan.setAttribute('aria-label', ariaPrefix + txt + (tr && tr.period ? (' over ' + tr.period) : '')); m.appendChild(tspan); } } const l = el('div', 'exec-label'); l.textContent = label; if (hint) l.title = hint; m.appendChild(l); return m; }
  row.appendChild(metric('Total Managed', formatBytes(ex.totalManagedBytes || 0), 'Estimated total managed heap bytes', 'totalManagedBytes'));
  row.appendChild(metric('Leak Score', (ex.leakLikelihoodScore || 0) + '/100', 'Leak likelihood', 'leakLikelihoodScore'));
  row.appendChild(metric('GC Pressure', (ex.gcPressureScore || 0) + '/100', 'GC pressure', 'gcPressureScore'));
  row.appendChild(metric('Thread Contention', (ex.threadContentionScore || 0) + '/100', 'Thread contention', 'threadContentionScore'));
  banner.appendChild(row); return banner;
}

export function buildDevActionPlan(doc) {
  const plan = doc.developerActionPlan; if (!plan || !plan.length) return null; const sec = el('section', 'section-card'); const h2 = document.createElement('h2'); h2.textContent = 'Developer Action Plan'; sec.appendChild(h2); const tbl = el('table'); const thead = el('thead'); const htr = el('tr'); for (const col of ['Priority', 'Title', 'Action', 'Impact']) { const th = document.createElement('th'); th.scope = 'col'; th.textContent = col; htr.appendChild(th); } thead.appendChild(htr); tbl.appendChild(thead); const tbody = el('tbody'); for (const action of plan) { const tr = el('tr'); [action.priority, action.title, action.action, action.impact].forEach(function (v, i) { const td = document.createElement('td'); if (i >= 2) td.className = 'wrap'; td.textContent = v || ''; tr.appendChild(td); }); tbody.appendChild(tr); } tbl.appendChild(tbody); sec.appendChild(tbl); return sec; }

export function buildFilterBar(doc) {
  const findings = doc.findings || []; if (!findings.length) return null; let crit = 0, warn = 0, info = 0; for (const f of findings) { const s = (f.severity || '').toLowerCase(); if (s === 'critical') crit++; else if (s === 'warning') warn++; else info++; }
  const bar = el('div', 'filter-bar'); bar.id = 'filter-bar'; bar.setAttribute('role', 'search'); bar.setAttribute('aria-label', 'Filter findings'); const group = el('div', 'filter-group'); group.setAttribute('aria-label', 'Severity filter'); function fbtn(sev, label, extra) { const btn = el('button', 'filter-btn' + (extra ? ' ' + extra : '') + (sev === 'all' ? ' active' : '')); btn.type = 'button'; btn.dataset.sev = sev; btn.setAttribute('aria-pressed', sev === 'all' ? 'true' : 'false'); btn.textContent = label; return btn; }
  group.appendChild(fbtn('all', 'All (' + findings.length + ')')); if (crit) group.appendChild(fbtn('critical', 'Critical (' + crit + ')', 'filter-critical')); if (warn) group.appendChild(fbtn('warning', 'Warning (' + warn + ')', 'filter-warning')); if (info) group.appendChild(fbtn('info', 'Info (' + info + ')', 'filter-info'));
  bar.appendChild(group);
  const search = document.createElement('input'); search.type = 'search'; search.id = 'filter-search'; search.className = 'filter-search'; search.placeholder = 'Search findings\u2026'; search.setAttribute('aria-label', 'Search findings by title or evidence'); bar.appendChild(search);
  const count = el('span', 'filter-count'); count.id = 'filter-count'; count.setAttribute('aria-live', 'polite'); count.setAttribute('aria-atomic', 'true'); bar.appendChild(count);
  return bar;
}

export function buildTOC(doc) {
  const findings = doc.findings || []; const sections = doc.analyzerSections || []; if ((!findings || !findings.length) && (!sections || !sections.length)) return null; const nav = el('nav', 'toc'); nav.setAttribute('aria-label', 'Report table of contents'); const title = el('div', 'toc-title'); title.textContent = 'Table of contents'; nav.appendChild(title);
  if (findings && findings.length) { const det = document.createElement('details'); det.open = true; const summ = document.createElement('summary'); summ.textContent = 'Findings (' + findings.length + ')'; det.appendChild(summ); const ol = document.createElement('ol'); for (let i = 0; i < findings.length; i++) { const a = document.createElement('a'); a.href = '#finding-' + i; a.textContent = findings[i].title || ('Finding ' + i); const li = document.createElement('li'); li.appendChild(a); ol.appendChild(li); } det.appendChild(ol); nav.appendChild(det); }
  if (sections && sections.length) { const container = el('div', 'toc-section'); for (let i = 0; i < sections.length; i++) { const sec = sections[i]; const det = document.createElement('details'); det.open = false; const summ = document.createElement('summary'); const sa = document.createElement('a'); sa.href = '#detail-' + i; sa.textContent = sec.displayTitle || sec.analyzerName || ('Section ' + i); summ.appendChild(sa); det.appendChild(summ); const headings = []; if (sec.blocks && sec.blocks.length) { for (let b = 0; b < sec.blocks.length; b++) { const blk = sec.blocks[b]; if (blk && blk.type === 'heading') headings.push({ text: blk.text, index: b }); } } if (headings.length) { const ol = document.createElement('ol'); for (let h = 0; h < headings.length; h++) { const ha = document.createElement('a'); ha.href = `#detail-${i}-heading-${h}`; ha.textContent = headings[h].text || `Heading ${h+1}`; const li = document.createElement('li'); li.appendChild(ha); ol.appendChild(li); } det.appendChild(ol); } container.appendChild(det); } nav.appendChild(container); }
  return nav;
}

export function buildFindingCard(f, i) {
  const sec = el('section', 'section-card'); sec.id = 'finding-' + i; sec.dataset.severity = (f.severity || 'info').toLowerCase(); sec.dataset.title = f.title || ''; sec.dataset.summary = (f.evidence || '').substring(0, 200);
  const header = el('div', 'section-header'); const badge = el('span', 'severity-badge ' + sevCss(f.severity)); badge.textContent = f.severity || 'Info'; header.appendChild(badge);
  const h2 = document.createElement('h2'); h2.textContent = f.title || '';
  const pa = document.createElement('a'); pa.className = 'permalink'; pa.href = '#finding-' + i; pa.setAttribute('aria-label', 'Permalink'); pa.textContent = '🔗'; h2.appendChild(t(' ')); h2.appendChild(pa);
  const copyBtn = el('button', 'copy-btn'); copyBtn.type = 'button'; copyBtn.setAttribute('aria-label', 'Copy permalink'); copyBtn.title = 'Copy permalink'; copyBtn.dataset.copy = (location.href || '').split('#')[0] + '#finding-' + i; copyBtn.textContent = '\u2398'; header.appendChild(h2); header.appendChild(copyBtn);
  const cat = el('span', 'category'); cat.textContent = f.category || ''; header.appendChild(cat); sec.appendChild(header);
  const p = document.createElement('p'); p.className = 'summary'; p.textContent = f.evidence || ''; sec.appendChild(p); linkifyAnchors(p);
  const tbl = el('table'); const thead = el('thead'); const htr = el('tr'); for (const col of ['Label', 'Value']) { const th = document.createElement('th'); th.scope = 'col'; th.textContent = col; htr.appendChild(th); } thead.appendChild(htr); tbl.appendChild(thead);
  const tbody = el('tbody'); function evidenceRow(label, value) { const tr = el('tr'); const td1 = document.createElement('td'); td1.textContent = label; tr.appendChild(td1); const td2 = document.createElement('td'); td2.className = 'wrap'; td2.textContent = value || ''; wrapAddresses(td2); linkifyAnchors(td2); tr.appendChild(td2); tbody.appendChild(tr); }
  evidenceRow('Evidence', f.evidence || ''); if (f.recommendation) evidenceRow('Recommendation', f.recommendation); tbl.appendChild(tbody); sec.appendChild(tbl); return sec;
}

export function buildConfidenceNotes(doc) {
  const notes = doc.confidence; if (!notes || !notes.length) return null; const sec = el('section', 'section-card'); const h2 = document.createElement('h2'); h2.textContent = 'Confidence Notes'; sec.appendChild(h2); const ul = document.createElement('ul'); for (const note of notes) { const li = document.createElement('li'); const strong = document.createElement('strong'); strong.textContent = '[' + note.analyzer + ']'; li.appendChild(strong); li.appendChild(t(' ' + note.reason)); ul.appendChild(li); } sec.appendChild(ul); return sec; }

export function buildAnalyzerSection(section, i) {
  const wrapper = el('section', 'analyzer-section detail-color-' + (i % 6)); wrapper.id = 'detail-' + i; const details = el('details'); const summaryEl = el('summary'); summaryEl.id = 'detail-' + i + '-summary'; summaryEl.textContent = section.displayTitle || section.analyzerName || ''; details.appendChild(summaryEl);
  const content = el('div', 'detail-block'); content.setAttribute('role', 'region'); content.setAttribute('aria-labelledby', summaryEl.id); content.dataset.sectionIndex = String(i); renderBlocks(section.blocks || [], content); details.appendChild(content); wrapper.appendChild(details);
  const pa = document.createElement('a'); pa.className = 'permalink'; pa.href = '#detail-' + i; pa.setAttribute('aria-label', 'Permalink'); pa.textContent = '🔗'; summaryEl.appendChild(t(' ')); summaryEl.appendChild(pa);
  const copyBtn = el('button', 'copy-btn'); copyBtn.type = 'button'; copyBtn.setAttribute('aria-label', 'Copy permalink'); copyBtn.title = 'Copy permalink'; copyBtn.dataset.copy = (location.href || '').split('#')[0] + '#detail-' + i; copyBtn.textContent = '\u2398'; summaryEl.appendChild(copyBtn);
  return wrapper;
}

export function renderFindingsPaged(doc, announce) {
  const findings = doc.findings || []; if (!findings.length) return null; const container = el('div', 'findings-paged'); const controls = el('div', 'pagination-controls'); controls.setAttribute('role', 'region'); controls.setAttribute('aria-label', 'Findings pagination'); const prevBtn = el('button', 'action-btn findings-prev'); prevBtn.type = 'button'; prevBtn.setAttribute('aria-label', 'Previous page'); prevBtn.textContent = '← Prev'; const nextBtn = el('button', 'action-btn findings-next'); nextBtn.type = 'button'; nextBtn.setAttribute('aria-label', 'Next page'); nextBtn.textContent = 'Next →'; const pageInfo = el('span', 'page-info'); const sizeSel = document.createElement('select'); sizeSel.setAttribute('aria-label', 'Findings per page'); [[10,'10'],[20,'20'],[50,'50'],[100,'100'],[0,'All']].forEach(function (opt) { const o = document.createElement('option'); o.value = String(opt[0]); o.text = String(opt[1]); sizeSel.appendChild(o); }); controls.appendChild(prevBtn); controls.appendChild(pageInfo); controls.appendChild(nextBtn); controls.appendChild(t(' ')); controls.appendChild(sizeSel);
  const list = el('div', 'findings-list'); list.setAttribute('role', 'list'); let pageSize = 10; let pageIndex = 0; function renderPage() { list.innerHTML = ''; const total = findings.length; const start = pageSize === 0 ? 0 : pageIndex * pageSize; const end = pageSize === 0 ? total : Math.min(total, start + pageSize); for (let i = start; i < end; i++) list.appendChild(buildFindingCard(findings[i], i)); pageInfo.textContent = pageSize === 0 ? `${total} findings` : `${start + 1}-${end} of ${total}`; prevBtn.disabled = (pageIndex === 0) || (pageSize === 0); nextBtn.disabled = (end >= total) || (pageSize === 0); controls.style.display = (total <= pageSize || pageSize === 0) ? 'none' : ''; if (pageSize !== 0 && announce) announce(`Showing ${start + 1} to ${end} of ${total} findings`); }
  prevBtn.addEventListener('click', function () { if (pageSize === 0) return; if (pageIndex > 0) { pageIndex--; renderPage(); } }); nextBtn.addEventListener('click', function () { if (pageSize === 0) return; pageIndex++; renderPage(); }); sizeSel.addEventListener('change', function () { pageSize = parseInt(sizeSel.value, 10) || 0; pageIndex = 0; renderPage(); }); sizeSel.value = String(pageSize); container.appendChild(controls); container.appendChild(list); renderPage(); return container; }
import { el, t, wrapAddresses, linkifyAnchors, indentClass, formatBytes, sevCss } from './report.dom.js';

// Render a sequence of SectionBlock JSON objects into a container element.
export function renderBlocks(blocks, container, buildDetailTable) {
  if (!blocks || !blocks.length) return;
  const stack = [container];
  if (container && !container._headingCounter) container._headingCounter = 0;

  for (const block of blocks) {
    const top = stack[stack.length - 1];
    switch (block.type) {
      case 'heading': {
        const d = el('div', 'detail-subheading' + indentClass(block.indentLevel || 0));
        d.textContent = block.text || '';
        try {
          const sidx = container && container.dataset && container.dataset.sectionIndex;
          if (sidx != null) {
            const idx = Number(sidx);
            const hIndex = (container._headingCounter++ || 0);
            const id = `detail-${idx}-heading-${hIndex}`;
            d.id = id;
          }
        } catch (e) { }
        top.appendChild(d);
        break;
      }
      case 'metric': {
        const d = el('div', 'detail-line' + indentClass(block.indentLevel || 0));
        const k = el('span', 'detail-key'); k.textContent = (block.label || '') + ':';
        const v = el('span', 'detail-value wrap'); v.textContent = block.value || '';
        d.appendChild(k); d.appendChild(t(' ')); d.appendChild(v);
        top.appendChild(d);
        wrapAddresses(d);
        break;
      }
      case 'path': {
        const d = el('div', 'detail-line' + indentClass(block.indentLevel || 0));
        const k = el('span', 'detail-key'); k.textContent = (block.label || '') + ':';
        const v = el('span', 'detail-path wrap'); v.textContent = block.path || '';
        d.appendChild(k); d.appendChild(t(' ')); d.appendChild(v);
        top.appendChild(d);
        wrapAddresses(d);
        break;
      }
      case 'stackframe': {
        const d = el('div', 'detail-line' + indentClass(block.indentLevel || 0));
        const span = el('span', block.isFrameworkFrame ? 'detail-frame framework' : 'detail-frame');
        span.textContent = block.frame || '';
        d.appendChild(span);
        top.appendChild(d);
        wrapAddresses(d);
        break;
      }
      case 'text': {
        const d = el('div', 'detail-line' + indentClass(block.indentLevel || 0));
        d.textContent = block.text || '';
        top.appendChild(d);
        wrapAddresses(d);
        break;
      }
      case 'listItem': {
        const d = el('div', 'detail-line' + indentClass(block.indentLevel || 0));
        d.textContent = '\u2022 ' + (block.text || '');
        top.appendChild(d);
        break;
      }
      case 'divider': {
        top.appendChild(el('div', 'detail-divider'));
        break;
      }
      case 'blank': {
        top.appendChild(el('div', 'detail-gap'));
        break;
      }
      case 'table': {
        top.appendChild(buildDetailTable(block));
        break;
      }
      case 'collapsibleBegin': {
        const details = el('details', 'detail-nested');
        const summary = el('summary'); summary.textContent = block.title || '';
        details.appendChild(summary);
        const content = el('div', 'detail-nested-content'); details.appendChild(content);
        top.appendChild(details);
        stack.push(content);
        break;
      }
      case 'collapsibleEnd': {
        if (stack.length > 1) stack.pop();
        break;
      }
      default:
        break;
    }
  }
}

export function buildDetailTable(block, announce) {
  const container = el('div', 'table-with-pagination');
  const tbl = el('table');
  if (block.caption) {
    const cap = document.createElement('caption'); cap.textContent = block.caption; tbl.appendChild(cap);
  }
  const thead = el('thead'); const htr = el('tr');
  for (const h of (block.headers || [])) { const th = document.createElement('th'); th.scope = 'col'; th.textContent = h; htr.appendChild(th); }
  thead.appendChild(htr); tbl.appendChild(thead);
  const tbody = el('tbody');

  const rowElements = [];
  for (const row of (block.rows || [])) {
    const tr = el('tr');
    for (const cell of (row.cells || [])) {
      const td = document.createElement('td');
      const disp = cell.display || '';
      if (disp.startsWith('__SPARK__')) {
        const payload = disp.substring('__SPARK__'.length);
        td.setAttribute('data-sparkline', payload);
      } else {
        const linkMarker = '||__LINK__';
        const li = disp.indexOf(linkMarker);
        if (li >= 0) {
          const left = disp.substring(0, li);
          const target = disp.substring(li + linkMarker.length);
          td.textContent = left;
          const a = document.createElement('a'); a.className = 'trend-jump'; a.href = '#' + target; a.setAttribute('aria-label', 'Jump to snapshot'); a.textContent = ' ↳'; td.appendChild(a);
        } else {
          td.textContent = disp;
        }
      }
      if (cell.rawValue != null) td.dataset.value = cell.rawValue;
      wrapAddresses(td);
      tr.appendChild(td);
    }
    rowElements.push(tr);
  }

  tbl.appendChild(tbody);
  const controls = el('div', 'table-pagination-controls'); controls.setAttribute('role', 'group'); controls.setAttribute('aria-label', 'Table pagination');
  const prev = el('button', 'action-btn table-prev'); prev.type = 'button'; prev.textContent = '← Prev'; prev.setAttribute('aria-label', 'Previous rows');
  const next = el('button', 'action-btn table-next'); next.type = 'button'; next.textContent = 'Next →'; next.setAttribute('aria-label', 'Next rows');
  const info = el('span', 'page-info');
  const sizeSel = document.createElement('select'); sizeSel.setAttribute('aria-label', 'Rows per page');
  [[10,'10'],[20,'20'],[50,'50'],[0,'All']].forEach(function (opt) { const o = document.createElement('option'); o.value = String(opt[0]); o.text = String(opt[1]); sizeSel.appendChild(o); });
  controls.appendChild(prev); controls.appendChild(info); controls.appendChild(next); controls.appendChild(t(' ')); controls.appendChild(sizeSel);

  let pageSize = 10; let pageIndex = 0;
  function renderTablePage() {
    tbody.innerHTML = '';
    const total = rowElements.length;
    const start = pageSize === 0 ? 0 : pageIndex * pageSize;
    const end = pageSize === 0 ? total : Math.min(total, start + pageSize);
    for (let i = start; i < end; i++) tbody.appendChild(rowElements[i]);
    info.textContent = pageSize === 0 ? `${total} rows` : `${start + 1}-${end} of ${total}`;
    prev.disabled = (pageIndex === 0) || (pageSize === 0);
    next.disabled = (end >= total) || (pageSize === 0);
    renderSparklines();
    controls.style.display = (total <= pageSize || pageSize === 0) ? 'none' : '';
    if (pageSize !== 0 && typeof announce === 'function') announce(`Showing ${start + 1} to ${end} of ${total} rows`);
  }
  prev.addEventListener('click', function () { if (pageSize === 0) return; if (pageIndex > 0) { pageIndex--; renderTablePage(); } });
  next.addEventListener('click', function () { if (pageSize === 0) return; pageIndex++; renderTablePage(); });
  sizeSel.addEventListener('change', function () { pageSize = parseInt(sizeSel.value, 10) || 0; pageIndex = 0; renderTablePage(); });
  sizeSel.value = String(pageSize);
  container.appendChild(controls); container.appendChild(tbl); renderTablePage();
  if (rowElements.length <= pageSize || pageSize === 0) controls.style.display = rowElements.length <= pageSize ? 'none' : '';
  return container;
}

export function renderSparklines() {
  const tds = document.querySelectorAll('td[data-sparkline]');
  for (const td of tds) {
    try {
      const payload = JSON.parse(td.getAttribute('data-sparkline'));
      const values = (payload && payload.values) || [];
      const w = 84, h = 20, pad = 2;
      const nums = values.map(v => isFinite(v) ? v : NaN);
      const valid = nums.filter(n => !Number.isNaN(n));
      const min = valid.length ? Math.min(...valid) : 0;
      const max = valid.length ? Math.max(...valid) : 1;
      const range = max - min || 1;
      const points = [];
      for (let i = 0; i < nums.length; i++) {
        const v = nums[i];
        const x = pad + (i * (w - pad*2) / Math.max(1, nums.length - 1));
        const y = Number.isNaN(v) ? h - pad : pad + (1 - (v - min) / range) * (h - pad*2);
        points.push([x.toFixed(1), y.toFixed(1)].join(','));
      }
      const ns = 'http://www.w3.org/2000/svg';
      const svg = document.createElementNS(ns, 'svg');
      svg.setAttribute('viewBox', `0 0 ${w} ${h}`);
      svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
      svg.classList.add('sparkline');
      svg.style.width = '6.5em'; svg.style.height = '1.6em'; svg.setAttribute('role', 'img');
      const poly = document.createElementNS(ns, 'polyline'); poly.setAttribute('fill', 'none'); poly.setAttribute('stroke-width', '1.5'); poly.setAttribute('points', points.join(' '));
      let trend = 'flat';
      if (valid.length >= 2) {
        const first = valid[0]; const last = valid[valid.length - 1]; const diff = last - first;
        if (Math.abs(diff) < Math.max(1e-6, Math.abs(first) * 0.005)) trend = 'flat'; else trend = diff > 0 ? 'up' : 'down';
      }
      const strokeColor = (payload && payload.color) || (trend === 'up' ? '#059669' : (trend === 'down' ? '#b91c1c' : '#6b7280'));
      poly.setAttribute('stroke', strokeColor);
      svg.appendChild(poly);
      const minVal = (payload && payload.min != null) ? payload.min : (valid.length ? min : null);
      const maxVal = (payload && payload.max != null) ? payload.max : (valid.length ? max : null);
      const latest = (payload && payload.latest != null) ? payload.latest : (valid.length ? valid[valid.length - 1] : null);
      const tooltipParts = [];
      if (minVal != null) tooltipParts.push('min: ' + String(minVal));
      if (maxVal != null) tooltipParts.push('max: ' + String(maxVal));
      if (latest != null) tooltipParts.push('latest: ' + String(latest));
      const title = document.createElementNS(ns, 'title'); title.textContent = tooltipParts.join('; '); svg.appendChild(title);
      td.textContent = ''; td.appendChild(svg);
    } catch (e) { /* ignore malformed payloads */ }
  }
}

export function buildFindingCard(f, i) {
  const sec = el('section', 'section-card');
  sec.id = 'finding-' + i;
  sec.dataset.severity = (f.severity || 'info').toLowerCase();
  sec.dataset.title = f.title || '';
  sec.dataset.summary = (f.evidence || '').substring(0, 200);
  const header = el('div', 'section-header');
  const badge = el('span', 'severity-badge ' + sevCss(f.severity)); badge.textContent = f.severity || 'Info'; header.appendChild(badge);
  const h2 = document.createElement('h2'); h2.textContent = f.title || '';
  const pa = document.createElement('a'); pa.className = 'permalink'; pa.href = '#finding-' + i; pa.setAttribute('aria-label', 'Permalink'); pa.textContent = '🔗';
  h2.appendChild(t(' ')); h2.appendChild(pa);
  const copyBtn = el('button', 'copy-btn'); copyBtn.type = 'button'; copyBtn.setAttribute('aria-label', 'Copy permalink'); copyBtn.title = 'Copy permalink'; copyBtn.dataset.copy = (location.href || '').split('#')[0] + '#finding-' + i; copyBtn.textContent = '\u2398';
  header.appendChild(h2); header.appendChild(copyBtn);
  const cat = el('span', 'category'); cat.textContent = f.category || ''; header.appendChild(cat);
  sec.appendChild(header);
  const p = document.createElement('p'); p.className = 'summary'; p.textContent = f.evidence || ''; sec.appendChild(p); linkifyAnchors(p);
  const tbl = el('table'); const thead = el('thead'); const htr = el('tr'); for (const col of ['Label', 'Value']) { const th = document.createElement('th'); th.scope = 'col'; th.textContent = col; htr.appendChild(th); }
  thead.appendChild(htr); tbl.appendChild(thead);
  const tbody = el('tbody'); function evidenceRow(label, value) { const tr = el('tr'); const td1 = document.createElement('td'); td1.textContent = label; tr.appendChild(td1); const td2 = document.createElement('td'); td2.className = 'wrap'; td2.textContent = value || ''; wrapAddresses(td2); linkifyAnchors(td2); tr.appendChild(td2); tbody.appendChild(tr); }
  evidenceRow('Evidence', f.evidence || ''); if (f.recommendation) evidenceRow('Recommendation', f.recommendation); tbl.appendChild(tbody); sec.appendChild(tbl);
  return sec;
}

export function buildAnalyzerSection(section, i, renderBlocksFn) {
  const wrapper = el('section', 'analyzer-section detail-color-' + (i % 6)); wrapper.id = 'detail-' + i;
  const details = el('details'); const summaryEl = el('summary'); summaryEl.id = 'detail-' + i + '-summary'; summaryEl.textContent = section.displayTitle || section.analyzerName || ''; details.appendChild(summaryEl);
  const content = el('div', 'detail-block'); content.setAttribute('role', 'region'); content.setAttribute('aria-labelledby', summaryEl.id); content.dataset.sectionIndex = String(i);
  renderBlocksFn(section.blocks || [], content);
  details.appendChild(content); wrapper.appendChild(details);
  const pa = document.createElement('a'); pa.className = 'permalink'; pa.href = '#detail-' + i; pa.setAttribute('aria-label', 'Permalink'); pa.textContent = '🔗'; summaryEl.appendChild(t(' ')); summaryEl.appendChild(pa);
  const copyBtn = el('button', 'copy-btn'); copyBtn.type = 'button'; copyBtn.setAttribute('aria-label', 'Copy permalink'); copyBtn.title = 'Copy permalink'; copyBtn.dataset.copy = (location.href || '').split('#')[0] + '#detail-' + i; copyBtn.textContent = '\u2398'; summaryEl.appendChild(copyBtn);
  return wrapper;
}

export function buildHeader(doc) {
  const isTrend = !!doc.isTrendReport; const title = isTrend ? 'DumpDetective Trend Analysis Report' : 'DumpDetective Analysis Report'; const dumpLabel = isTrend ? 'Latest dump' : 'Dump';
  const rawName = (doc.dumpPath || 'report').replace(/\\/g, '/').split('/').pop() || 'report'; const exportName = rawName.replace(/\.[^.]+$/, '') || 'report';
  const sec = el('section', 'header-card'); const h1 = document.createElement('h1'); h1.textContent = title; sec.appendChild(h1);
  const grid = el('div', 'meta-grid'); function metaItem(label, value) { const d = el('div', 'meta-item'); const s = el('span', 'meta-label'); s.textContent = label + ':'; d.appendChild(s); d.appendChild(t(' ' + value)); return d; }
  grid.appendChild(metaItem(dumpLabel, doc.dumpPath || ''));
  const genRaw = doc.generatedAtUtc; const genStr = genRaw ? (new Date(genRaw)).toISOString().replace('T', ' ').slice(0, 19) + ' UTC' : '';
  grid.appendChild(metaItem('Generated (UTC)', genStr)); grid.appendChild(metaItem('Elapsed', ((doc.elapsedSeconds) || 0).toFixed(1) + 's')); grid.appendChild(metaItem('Schema', doc.schemaVersion || ''));
  sec.appendChild(grid);
  const dedup = doc.dedupDiagnostics; if (dedup) { const d = el('div', 'dedup-note'); d.textContent = 'Dedup: merged ' + dedup.mergedSections + '/' + dedup.duplicateCandidates + ' candidate duplicates'; sec.appendChild(d); }
  if (isTrend) { const td = el('div', 'dedup-note'); td.textContent = 'Dumps analyzed: ' + (doc.trendDumpCount || 0); sec.appendChild(td); if (doc.trendDumpPaths && doc.trendDumpPaths.length) { const dp = el('div', 'dedup-note'); const strong = document.createElement('strong'); strong.textContent = 'Analyzed dumps:'; dp.appendChild(strong); for (const p of doc.trendDumpPaths) { dp.appendChild(document.createElement('br')); dp.appendChild(t('\u2022 ' + p)); } sec.appendChild(dp); } }
  const bar = el('div', 'action-bar'); bar.setAttribute('role', 'toolbar'); bar.setAttribute('aria-label', 'Report actions'); function actionBtn(id, ariaLabel, txt) { const btn = el('button', 'action-btn'); btn.type = 'button'; btn.id = id; btn.dataset.filename = exportName; btn.setAttribute('aria-label', ariaLabel); btn.textContent = txt; return btn; }
  bar.appendChild(actionBtn('btn-download-json', 'Download report as JSON', '\u2B07 JSON'));
  bar.appendChild(actionBtn('btn-export-csv', 'Export findings as CSV', '\u2B07 CSV'));
  bar.appendChild(actionBtn('btn-print', 'Print this report', '\u2399 Print'));
  bar.appendChild(actionBtn('btn-toggle-contrast', 'Toggle high contrast mode', '\u263C Contrast'));
  sec.appendChild(bar);
  return sec;
}

export function buildIncidentContext(doc) {
  const ctx = doc.incidentContext; if (!ctx) return null;
  const sec = el('section', 'section-card'); const h2 = document.createElement('h2'); h2.textContent = 'Incident Context'; sec.appendChild(h2);
  const tbl = el('table'); const thead = el('thead'); const htr = el('tr'); for (const col of ['Field', 'Value']) { const th = document.createElement('th'); th.scope = 'col'; th.textContent = col; htr.appendChild(th); }
  thead.appendChild(htr); tbl.appendChild(thead); const tbody = el('tbody');
  function row(label, value) { const tr = el('tr'); const td1 = document.createElement('td'); td1.textContent = label; tr.appendChild(td1); const td2 = document.createElement('td'); td2.className = 'wrap'; td2.textContent = value || ''; tr.appendChild(td2); tbody.appendChild(tr); }
  row('Mode', ctx.mode || ''); row('Dump Path', ctx.dumpPath || ''); if (ctx.baselineDumpPath) row('Baseline Dump', ctx.baselineDumpPath); row('Report', (ctx.reportFormat || '') + ' / ' + (ctx.reportAudience || '')); row('Config', (ctx.usedConfigFile ? 'config file' : 'command line') + (ctx.configPath ? ' (' + ctx.configPath + ')' : '')); row('Diagnostic Mode', ctx.diagnosticMode ? 'on' : 'off'); row('Index Prebuild', ctx.indexPrebuildMode || ''); row('Runtime', (ctx.runtimeFlavor || 'n/a') + (ctx.runtimeVersion ? ' ' + ctx.runtimeVersion : '')); row('GC Mode', ctx.gcMode || 'n/a'); row('Heap Count', ctx.heapCount != null ? String(ctx.heapCount) : 'n/a'); row('Heap Walkable', ctx.heapCanWalk ? 'yes' : 'no'); row('Active Analyzers', String(ctx.activeAnalyzerCount || 0)); row('Analysis Elapsed', (Number(ctx.analysisElapsedSeconds || 0)).toFixed(1) + 's');
  tbl.appendChild(tbody); sec.appendChild(tbl);
  if (ctx.trendSnapshots && ctx.trendSnapshots.length) {
    const h3 = document.createElement('h3'); h3.textContent = 'Snapshot Contexts'; sec.appendChild(h3);
    const stbl = el('table'); const sthead = el('thead'); const shtr = el('tr'); for (const col of ['Snapshot', 'Dump', 'Elapsed', 'Analyzers', 'Findings']) { const th = document.createElement('th'); th.scope = 'col'; th.textContent = col; shtr.appendChild(th); }
    sthead.appendChild(shtr); stbl.appendChild(sthead); const stbody = el('tbody'); for (const snap of ctx.trendSnapshots) { const tr = el('tr'); const label = snap.isBaseline ? 'Baseline' : (snap.isCurrent ? 'Current' : ('Snapshot ' + (snap.index + 1))); const values = [label, snap.dumpPath || '', (Number(snap.elapsedSeconds || 0)).toFixed(1) + 's', String(snap.analyzerCount || 0), String(snap.findingCount || 0)]; values.forEach(function (value) { const td = document.createElement('td'); td.className = 'wrap'; td.textContent = value; tr.appendChild(td); }); stbody.appendChild(tr); }
    stbl.appendChild(stbody); sec.appendChild(stbl);
  }
  return sec;
}

export function buildExecutiveSummary(doc) {
  const ex = doc.executiveSummary; if (!ex) return null;
  const sec = el('section', 'section-card'); const h2 = document.createElement('h2'); h2.textContent = 'Executive Summary'; sec.appendChild(h2);
  const tbl = el('table'); const thead = el('thead'); const htr = el('tr'); for (const col of ['Signal', 'Value']) { const th = document.createElement('th'); th.scope = 'col'; th.textContent = col; htr.appendChild(th); }
  thead.appendChild(htr); tbl.appendChild(thead);
  const tbody = el('tbody'); const rows = [['Total Managed Bytes', formatBytes(ex.totalManagedBytes || 0)], ['Leak Likelihood Score', (ex.leakLikelihoodScore || 0) + '/100'], ['GC Pressure Score', (ex.gcPressureScore || 0) + '/100'], ['Thread Contention Score', (ex.threadContentionScore || 0) + '/100']];
  for (const [label, value] of rows) { const tr = el('tr'); const td1 = document.createElement('td'); td1.textContent = label; tr.appendChild(td1); const td2 = document.createElement('td'); td2.className = 'wrap'; td2.textContent = value; tr.appendChild(td2); tbody.appendChild(tr); }
  tbl.appendChild(tbody); sec.appendChild(tbl);
  const recs = ex.topRecommendations; if (recs && recs.length) { const h3 = document.createElement('h3'); h3.textContent = 'Top Recommendations'; sec.appendChild(h3); const ul = document.createElement('ul'); for (const rec of recs) { const li = document.createElement('li'); const badge = el('span', 'severity-badge ' + sevCss(rec.severity)); badge.textContent = rec.severity || ''; li.appendChild(badge); li.appendChild(t(' ' + (rec.title || ''))); ul.appendChild(li); } sec.appendChild(ul); }
  return sec;
}

export function buildExecutiveBanner(doc) {
  const ex = doc.executiveSummary; if (!ex) return null;
  const banner = el('section', 'section-card exec-banner'); const row = el('div', 'exec-row');
  function splitValueUnit(s) { if (!s) return { value: '', unit: '' }; if (s.endsWith('%')) return { value: s.slice(0, -1).trim(), unit: '%' }; const parts = String(s).trim().split(/\s+/); if (parts.length >= 2 && /[A-Za-z%]+/.test(parts[parts.length - 1])) { const unit = parts.pop(); return { value: parts.join(' '), unit: unit }; } const m = s.match(/^(\d+)(\/\d+)$/); if (m) return { value: m[1], unit: m[2] }; return { value: s, unit: '' }; }
  function findTrendForKey(key) { if (!ex) return null; if (ex.trends && ex.trends[key]) return ex.trends[key]; const candidates = [key + 'Change', key + 'Delta', key + 'DeltaPct', key + 'PercentChange', key + 'Pct']; for (const c of candidates) { if (Object.prototype.hasOwnProperty.call(ex, c)) { return { raw: ex[c] }; } } return null; }
  function metric(label, valueStr, hint, key) { const m = el('div', 'exec-metric'); const vwrap = el('div', 'exec-meta'); const parts = splitValueUnit(valueStr || ''); const v = el('div', 'exec-value'); v.textContent = parts.value || ''; vwrap.appendChild(v); if (parts.unit) { const u = el('div', 'exec-unit'); u.textContent = parts.unit; vwrap.appendChild(u); } m.appendChild(vwrap);
    if (key) { const tr = findTrendForKey(key); if (tr) { let txt = ''; let display; if (typeof tr === 'object' && (tr.pct != null || tr.delta != null || tr.raw != null)) { const val = tr.pct != null ? tr.pct : (tr.delta != null ? tr.delta : tr.raw); const sign = (Number(val) > 0) ? '\u25B2' : (Number(val) < 0 ? '\u25BC' : '\u25AC'); display = String(val).toString(); txt = sign + ' ' + display; } else if (typeof tr === 'number') { display = String(tr); txt = display; } if (txt) { const tspan = el('div', 'exec-trend'); const displayStr = String(display || '').trim(); const numeric = Number(displayStr.replace(/[^0-9.-]+/g, '')); let cls = 'trend-flat'; if (!Number.isNaN(numeric)) { cls = numeric > 0 ? 'trend-up' : (numeric < 0 ? 'trend-down' : 'trend-flat'); } tspan.className = 'exec-trend ' + cls; tspan.textContent = txt; tspan.title = label + ' trend: ' + txt; const ariaPrefix = (!Number.isNaN(numeric) ? (numeric > 0 ? 'Increased ' : (numeric < 0 ? 'Decreased ' : 'No change ')) : 'Trend: '); tspan.setAttribute('aria-label', ariaPrefix + txt + (tr && tr.period ? (' over ' + tr.period) : '')); m.appendChild(tspan); } } const l = el('div', 'exec-label'); l.textContent = label; if (hint) l.title = hint; m.appendChild(l); return m; }
  row.appendChild(metric('Total Managed', formatBytes(ex.totalManagedBytes || 0), 'Estimated total managed heap bytes', 'totalManagedBytes'));
  row.appendChild(metric('Leak Score', (ex.leakLikelihoodScore || 0) + '/100', 'Leak likelihood', 'leakLikelihoodScore'));
  row.appendChild(metric('GC Pressure', (ex.gcPressureScore || 0) + '/100', 'GC pressure', 'gcPressureScore'));
  row.appendChild(metric('Thread Contention', (ex.threadContentionScore || 0) + '/100', 'Thread contention', 'threadContentionScore'));
  banner.appendChild(row); return banner;
}

export function buildDevActionPlan(doc) {
  const plan = doc.developerActionPlan; if (!plan || !plan.length) return null; const sec = el('section', 'section-card'); const h2 = document.createElement('h2'); h2.textContent = 'Developer Action Plan'; sec.appendChild(h2);
  const tbl = el('table'); const thead = el('thead'); const htr = el('tr'); for (const col of ['Priority', 'Title', 'Action', 'Impact']) { const th = document.createElement('th'); th.scope = 'col'; th.textContent = col; htr.appendChild(th); } thead.appendChild(htr); tbl.appendChild(thead);
  const tbody = el('tbody'); for (const action of plan) { const tr = el('tr'); [action.priority, action.title, action.action, action.impact].forEach(function (v, i) { const td = document.createElement('td'); if (i >= 2) td.className = 'wrap'; td.textContent = v || ''; tr.appendChild(td); }); tbody.appendChild(tr); }
  tbl.appendChild(tbody); sec.appendChild(tbl); return sec;
}

export function buildConfidenceNotes(doc) { const notes = doc.confidence; if (!notes || !notes.length) return null; const sec = el('section', 'section-card'); const h2 = document.createElement('h2'); h2.textContent = 'Confidence Notes'; sec.appendChild(h2); const ul = document.createElement('ul'); for (const note of notes) { const li = document.createElement('li'); const strong = document.createElement('strong'); strong.textContent = '[' + note.analyzer + ']'; li.appendChild(strong); li.appendChild(t(' ' + note.reason)); ul.appendChild(li); } sec.appendChild(ul); return sec; }

export function renderFindingsPaged(doc, announce) {
  const findings = doc.findings || [];
  if (!findings.length) return null;
  const container = el('div', 'findings-paged');
  const controls = el('div', 'pagination-controls'); controls.setAttribute('role', 'region'); controls.setAttribute('aria-label', 'Findings pagination');
  const prevBtn = el('button', 'action-btn findings-prev'); prevBtn.type = 'button'; prevBtn.setAttribute('aria-label', 'Previous page'); prevBtn.textContent = '← Prev';
  const nextBtn = el('button', 'action-btn findings-next'); nextBtn.type = 'button'; nextBtn.setAttribute('aria-label', 'Next page'); nextBtn.textContent = 'Next →';
  const pageInfo = el('span', 'page-info');
  const sizeSel = document.createElement('select'); sizeSel.setAttribute('aria-label', 'Findings per page'); [[10,'10'],[20,'20'],[50,'50'],[100,'100'],[0,'All']].forEach(function (opt) { const o = document.createElement('option'); o.value = String(opt[0]); o.text = String(opt[1]); sizeSel.appendChild(o); });
  controls.appendChild(prevBtn); controls.appendChild(pageInfo); controls.appendChild(nextBtn); controls.appendChild(t(' ')); controls.appendChild(sizeSel);
  const list = el('div', 'findings-list'); list.setAttribute('role', 'list');
  let pageSize = 10; let pageIndex = 0;
  function renderPage() { list.innerHTML = ''; const total = findings.length; const start = pageSize === 0 ? 0 : pageIndex * pageSize; const end = pageSize === 0 ? total : Math.min(total, start + pageSize); for (let i = start; i < end; i++) list.appendChild(buildFindingCard(findings[i], i)); pageInfo.textContent = pageSize === 0 ? `${total} findings` : `${start + 1}-${end} of ${total}`; prevBtn.disabled = (pageIndex === 0) || (pageSize === 0); nextBtn.disabled = (end >= total) || (pageSize === 0); controls.style.display = (total <= pageSize || pageSize === 0) ? 'none' : ''; if (pageSize !== 0 && typeof announce === 'function') announce(`Showing ${start + 1} to ${end} of ${total} findings`); }
  prevBtn.addEventListener('click', function () { if (pageSize === 0) return; if (pageIndex > 0) { pageIndex--; renderPage(); } });
  nextBtn.addEventListener('click', function () { if (pageSize === 0) return; pageIndex++; renderPage(); });
  sizeSel.addEventListener('change', function () { pageSize = parseInt(sizeSel.value, 10) || 0; pageIndex = 0; renderPage(); });
  sizeSel.value = String(pageSize);
  container.appendChild(controls); container.appendChild(list); renderPage(); return container;
}
