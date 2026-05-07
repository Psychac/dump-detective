/* DumpDetective Report Renderer
 * Reads window.__REPORT__ (AnalysisReportDocument JSON) and builds the entire page DOM.
 * Security: all user-originated strings use textContent — never innerHTML.
 */
(async function () {
  'use strict';

  // Async loader: prefer the JSON blob in <script id="report-json" type="application/json">.
  async function loadDoc() {
    try {
      const el = document.getElementById('report-json');
      if (el && el.textContent && el.textContent.trim()) {
        try {
          const parsed = JSON.parse(el.textContent);
          // marker for external JSON loader
          if (parsed && parsed._external) {
            const href = parsed._external;
            try {
              const r = await fetch(href);
              if (!r.ok) return null;
              return await r.json();
            } catch { return null; }
          }
          return parsed;
        } catch (e) { /* fall through to legacy */ }
      }
    } catch (e) { /* ignore */ }
    // Legacy fallback: window.__REPORT__ (some older reports still use this)
    return window.__REPORT__ || null;
  }

  const doc = await loadDoc();
  if (!doc) return;

  // ── DOM helpers ───────────────────────────────────────────────────────────

  function el(tag, className) {
    const e = document.createElement(tag);
    if (className) e.className = className;
    return e;
  }

  function t(text) {
    return document.createTextNode(String(text));
  }

  function sevCss(s) {
    const l = (s || '').toLowerCase();
    return l === 'critical' ? 'severity-critical' : l === 'warning' ? 'severity-warning' : 'severity-info';
  }

  function formatBytes(bytes) {
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let v = Number(bytes) || 0, u = 0;
    while (v >= 1024 && u < units.length - 1) { v /= 1024; u++; }
    return (Math.round(v * 100) / 100) + ' ' + units[u];
  }

  // Wraps hex addresses (0x…) in <span class="addr"> with a copy button,
  // operating purely through DOM text-node splitting — no innerHTML.
  function wrapAddresses(container) {
    const addrRe = /0x[0-9A-Fa-f]{6,}/g;
    const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT, null);
    const nodes = [];
    let n;
    while ((n = walker.nextNode())) {
      if (addrRe.test(n.textContent)) nodes.push(n);
      addrRe.lastIndex = 0;
    }
    for (const node of nodes) {
      const txt = node.textContent;
      addrRe.lastIndex = 0;
      const frag = document.createDocumentFragment();
      let last = 0, m;
      while ((m = addrRe.exec(txt)) !== null) {
        if (m.index > last) frag.appendChild(t(txt.slice(last, m.index)));
        const span = el('span', 'addr');
        span.appendChild(t(m[0]));
        const btn = el('button', 'copy-btn');
        btn.type = 'button';
        btn.setAttribute('aria-label', 'Copy ' + m[0]);
        btn.setAttribute('data-copy', m[0]);
        btn.title = 'Copy to clipboard';
        btn.textContent = '\u2398';
        span.appendChild(btn);
        frag.appendChild(span);
        last = m.index + m[0].length;
      }
      if (last < txt.length) frag.appendChild(t(txt.slice(last)));
      if (node.parentNode) node.parentNode.replaceChild(frag, node);
    }
  }

  // Convert textual hash references like "#detail-3" or "#finding-2" into real anchor elements.
  function linkifyAnchors(container) {
    const re = /#(?:detail|finding)-\d+/g;
    const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT, null);
    const nodes = [];
    let n;
    while ((n = walker.nextNode())) {
      if (re.test(n.textContent)) nodes.push(n);
      re.lastIndex = 0;
    }
    for (const node of nodes) {
      const txt = node.textContent;
      re.lastIndex = 0;
      const frag = document.createDocumentFragment();
      let last = 0, m;
      while ((m = re.exec(txt)) !== null) {
        if (m.index > last) frag.appendChild(t(txt.slice(last, m.index)));
        const a = document.createElement('a');
        a.href = m[0];
        a.textContent = m[0];
        a.className = 'intext-anchor';
        a.setAttribute('aria-label', 'Jump to ' + m[0].substring(1));
        frag.appendChild(a);
        last = m.index + m[0].length;
      }
      if (last < txt.length) frag.appendChild(t(txt.slice(last)));
      if (node.parentNode) node.parentNode.replaceChild(frag, node);
    }
  }

  function indentClass(level) {
    if (level >= 2) return ' detail-indent-2';
    if (level === 1) return ' detail-indent-1';
    return '';
  }

  // ── SectionBlock renderer ─────────────────────────────────────────────────
  // block.type values match [JsonDerivedType] discriminators on SectionBlock.

  function renderBlocks(blocks, container) {
    if (!blocks || !blocks.length) return;
    const stack = [container]; // stack tracks open collapsible containers

    for (const block of blocks) {
      const top = stack[stack.length - 1];

      switch (block.type) {
        case 'heading': {
          const d = el('div', 'detail-subheading' + indentClass(block.indentLevel || 0));
          d.textContent = block.text || '';
          top.appendChild(d);
          break;
        }
        case 'metric': {
          const d = el('div', 'detail-line' + indentClass(block.indentLevel || 0));
          const k = el('span', 'detail-key');
          k.textContent = (block.label || '') + ':';
          const v = el('span', 'detail-value wrap');
          v.textContent = block.value || '';
          d.appendChild(k);
          d.appendChild(t(' '));
          d.appendChild(v);
          top.appendChild(d);
          wrapAddresses(d);
          break;
        }
        case 'path': {
          const d = el('div', 'detail-line' + indentClass(block.indentLevel || 0));
          const k = el('span', 'detail-key');
          k.textContent = (block.label || '') + ':';
          const v = el('span', 'detail-path wrap');
          v.textContent = block.path || '';
          d.appendChild(k);
          d.appendChild(t(' '));
          d.appendChild(v);
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
          const summary = el('summary');
          summary.textContent = block.title || '';
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

  function buildDetailTable(block) {
    const container = el('div', 'table-with-pagination');
    const tbl = el('table');
    if (block.caption) {
      const cap = document.createElement('caption');
      cap.textContent = block.caption;
      tbl.appendChild(cap);
    }
    const thead = el('thead');
    const htr = el('tr');
    for (const h of (block.headers || [])) {
      const th = document.createElement('th');
      th.scope = 'col';
      th.textContent = h;
      htr.appendChild(th);
    }
    thead.appendChild(htr);
    tbl.appendChild(thead);
    const tbody = el('tbody');

    // Pre-build row elements so we can page them efficiently
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
            const a = document.createElement('a');
            a.className = 'trend-jump';
            a.href = '#' + target;
            a.setAttribute('aria-label', 'Jump to snapshot');
            a.textContent = ' ↳';
            td.appendChild(a);
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

    // Pagination controls for large tables
    const controls = el('div', 'table-pagination-controls');
    controls.setAttribute('role', 'group');
    controls.setAttribute('aria-label', 'Table pagination');
    const prev = el('button', 'action-btn table-prev'); prev.type = 'button'; prev.textContent = '← Prev'; prev.setAttribute('aria-label', 'Previous rows');
    const next = el('button', 'action-btn table-next'); next.type = 'button'; next.textContent = 'Next →'; next.setAttribute('aria-label', 'Next rows');
    const info = el('span', 'page-info');
    const sizeSel = document.createElement('select'); sizeSel.setAttribute('aria-label', 'Rows per page');
    [[10,'10'],[20,'20'],[50,'50'],[0,'All']].forEach(function (opt) { const o = document.createElement('option'); o.value = String(opt[0]); o.text = String(opt[1]); sizeSel.appendChild(o); });
    controls.appendChild(prev); controls.appendChild(info); controls.appendChild(next); controls.appendChild(t(' ')); controls.appendChild(sizeSel);

    let pageSize = 10;
    let pageIndex = 0;

    function renderTablePage() {
      tbody.innerHTML = '';
      const total = rowElements.length;
      const start = pageSize === 0 ? 0 : pageIndex * pageSize;
      const end = pageSize === 0 ? total : Math.min(total, start + pageSize);
      for (let i = start; i < end; i++) tbody.appendChild(rowElements[i]);
      info.textContent = pageSize === 0 ? `${total} rows` : `${start + 1}-${end} of ${total}`;
      prev.disabled = (pageIndex === 0) || (pageSize === 0);
      next.disabled = (end >= total) || (pageSize === 0);
      // After rendering new rows, (re)draw sparklines in current DOM
      renderSparklines();
      // Hide controls if only a single page
      controls.style.display = (total <= pageSize || pageSize === 0) ? 'none' : '';
    }

    prev.addEventListener('click', function () { if (pageSize === 0) return; if (pageIndex > 0) { pageIndex--; renderTablePage(); } });
    next.addEventListener('click', function () { if (pageSize === 0) return; pageIndex++; renderTablePage(); });
    sizeSel.addEventListener('change', function () { pageSize = parseInt(sizeSel.value, 10) || 0; pageIndex = 0; renderTablePage(); });
    sizeSel.value = String(pageSize);

    container.appendChild(controls);
    container.appendChild(tbl);
    // Initial render
    renderTablePage();
    // Hide controls if only single page
    if (rowElements.length <= pageSize || pageSize === 0) controls.style.display = rowElements.length <= pageSize ? 'none' : '';

    return container;
  }

  // Render sparkline SVGs for any table cells carrying data-sparkline
  function renderSparklines() {
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
        svg.setAttribute('width', String(w));
        svg.setAttribute('height', String(h));
        svg.classList.add('sparkline');
        const poly = document.createElementNS(ns, 'polyline');
        poly.setAttribute('fill', 'none');
        poly.setAttribute('stroke', '#6b6b6b');
        poly.setAttribute('stroke-width', '1');
        poly.setAttribute('points', points.join(' '));
        svg.appendChild(poly);
        // Clear cell and append svg
        td.textContent = '';
        td.appendChild(svg);
      } catch (e) { /* ignore malformed payloads */ }
    }
  }

  // Helper: check if element or its ancestor matches selector
  function isInside(el, selector) {
    while (el) {
      if (el.matches && el.matches(selector)) return true;
      el = el.parentElement;
    }
    return false;
  }

  // Global keyboard shortcuts for pagination while focused inside paginated areas
  document.addEventListener('keydown', function (ev) {
    const active = document.activeElement;
    if (!active) return;
    try {
      if (isInside(active, '.findings-paged')) {
        const container = active.closest('.findings-paged');
        if (!container) return;
        const prev = container.querySelector('.findings-prev');
        const next = container.querySelector('.findings-next');
        if (ev.key === 'ArrowLeft' && prev && !prev.disabled) { prev.click(); ev.preventDefault(); }
        if (ev.key === 'ArrowRight' && next && !next.disabled) { next.click(); ev.preventDefault(); }
      }
      if (isInside(active, '.table-with-pagination')) {
        const container = active.closest('.table-with-pagination');
        if (!container) return;
        const prev = container.querySelector('.table-prev');
        const next = container.querySelector('.table-next');
        if (ev.key === 'ArrowLeft' && prev && !prev.disabled) { prev.click(); ev.preventDefault(); }
        if (ev.key === 'ArrowRight' && next && !next.disabled) { next.click(); ev.preventDefault(); }
      }
    } catch (e) { /* ignore */ }
  });

  // ── Header card ───────────────────────────────────────────────────────────

  function buildHeader() {
    const isTrend    = !!doc.isTrendReport;
    const title      = isTrend ? 'DumpDetective Trend Analysis Report' : 'DumpDetective Analysis Report';
    const dumpLabel  = isTrend ? 'Latest dump' : 'Dump';
    const rawName    = (doc.dumpPath || 'report').replace(/\\/g, '/').split('/').pop() || 'report';
    const exportName = rawName.replace(/\.[^.]+$/, '') || 'report';

    const sec = el('section', 'header-card');
    const h1  = document.createElement('h1');
    h1.textContent = title;
    sec.appendChild(h1);

    const grid = el('div', 'meta-grid');
    function metaItem(label, value) {
      const d = el('div', 'meta-item');
      const s = el('span', 'meta-label'); s.textContent = label + ':';
      d.appendChild(s); d.appendChild(t(' ' + value));
      return d;
    }
    grid.appendChild(metaItem(dumpLabel, doc.dumpPath || ''));

    const genRaw = doc.generatedAtUtc;
    const genStr = genRaw ? (new Date(genRaw)).toISOString().replace('T', ' ').slice(0, 19) + ' UTC' : '';
    grid.appendChild(metaItem('Generated (UTC)', genStr));
    grid.appendChild(metaItem('Elapsed', ((doc.elapsedSeconds) || 0).toFixed(1) + 's'));
    grid.appendChild(metaItem('Schema', doc.schemaVersion || ''));
    sec.appendChild(grid);

    const dedup = doc.dedupDiagnostics;
    if (dedup) {
      const d = el('div', 'dedup-note');
      d.textContent = 'Dedup: merged ' + dedup.mergedSections + '/' + dedup.duplicateCandidates + ' candidate duplicates';
      sec.appendChild(d);
    }

    if (isTrend) {
      const td = el('div', 'dedup-note');
      td.textContent = 'Dumps analyzed: ' + (doc.trendDumpCount || 0);
      sec.appendChild(td);
      if (doc.trendDumpPaths && doc.trendDumpPaths.length) {
        const dp = el('div', 'dedup-note');
        const strong = document.createElement('strong'); strong.textContent = 'Analyzed dumps:';
        dp.appendChild(strong);
        for (const p of doc.trendDumpPaths) { dp.appendChild(document.createElement('br')); dp.appendChild(t('\u2022 ' + p)); }
        sec.appendChild(dp);
      }
    }

    const bar = el('div', 'action-bar');
    bar.setAttribute('role', 'toolbar');
    bar.setAttribute('aria-label', 'Report actions');
    function actionBtn(id, ariaLabel, txt) {
      const btn = el('button', 'action-btn');
      btn.type = 'button'; btn.id = id;
      btn.dataset.filename = exportName;
      btn.setAttribute('aria-label', ariaLabel);
      btn.textContent = txt;
      return btn;
    }
    bar.appendChild(actionBtn('btn-download-json', 'Download report as JSON', '\u2B07 JSON'));
    bar.appendChild(actionBtn('btn-export-csv',   'Export findings as CSV',   '\u2B07 CSV'));
    bar.appendChild(actionBtn('btn-print',         'Print this report',        '\u2399 Print'));
    // High-contrast toggle
    bar.appendChild(actionBtn('btn-toggle-contrast', 'Toggle high contrast mode', '\u263C Contrast'));
    sec.appendChild(bar);
    return sec;
  }

  // ── Executive summary ─────────────────────────────────────────────────────

  function buildExecutiveSummary() {
    const ex = doc.executiveSummary;
    if (!ex) return null;

    const sec = el('section', 'section-card');
    const h2  = document.createElement('h2'); h2.textContent = 'Executive Summary';
    sec.appendChild(h2);

    const tbl    = el('table');
    const thead  = el('thead');
    const htr    = el('tr');
    for (const col of ['Signal', 'Value']) {
      const th = document.createElement('th'); th.scope = 'col'; th.textContent = col; htr.appendChild(th);
    }
    thead.appendChild(htr); tbl.appendChild(thead);

    const tbody = el('tbody');
    const rows  = [
      ['Total Managed Bytes',   formatBytes(ex.totalManagedBytes || 0)],
      ['Leak Likelihood Score', (ex.leakLikelihoodScore   || 0) + '/100'],
      ['GC Pressure Score',     (ex.gcPressureScore       || 0) + '/100'],
      ['Thread Contention Score',(ex.threadContentionScore|| 0) + '/100'],
    ];
    for (const [label, value] of rows) {
      const tr = el('tr');
      const td1 = document.createElement('td'); td1.textContent = label; tr.appendChild(td1);
      const td2 = document.createElement('td'); td2.className = 'wrap'; td2.textContent = value; tr.appendChild(td2);
      tbody.appendChild(tr);
    }
    tbl.appendChild(tbody); sec.appendChild(tbl);

    const recs = ex.topRecommendations;
    if (recs && recs.length) {
      const h3 = document.createElement('h3'); h3.textContent = 'Top Recommendations'; sec.appendChild(h3);
      const ul = document.createElement('ul');
      for (const rec of recs) {
        const li = document.createElement('li');
        const badge = el('span', 'severity-badge ' + sevCss(rec.severity)); badge.textContent = rec.severity || '';
        li.appendChild(badge); li.appendChild(t(' ' + (rec.title || '')));
        ul.appendChild(li);
      }
      sec.appendChild(ul);
    }
    return sec;
  }

  // Compact executive metrics banner (TotalManagedBytes + risk scores)
  function buildExecutiveBanner() {
    const ex = doc.executiveSummary;
    if (!ex) return null;
    const banner = el('section', 'section-card exec-banner');
    const row = el('div', 'exec-row');

    function metric(label, value, hint) {
      const m = el('div', 'exec-metric');
      const v = el('div', 'exec-value'); v.textContent = value; m.appendChild(v);
      const l = el('div', 'exec-label'); l.textContent = label; if (hint) l.title = hint; m.appendChild(l);
      return m;
    }

    row.appendChild(metric('Total Managed', formatBytes(ex.totalManagedBytes || 0), 'Estimated total managed heap bytes'));
    row.appendChild(metric('Leak Score', (ex.leakLikelihoodScore || 0) + '/100', 'Leak likelihood'));
    row.appendChild(metric('GC Pressure', (ex.gcPressureScore || 0) + '/100', 'GC pressure'));
    row.appendChild(metric('Thread Contention', (ex.threadContentionScore || 0) + '/100', 'Thread contention'));

    banner.appendChild(row);
    return banner;
  }

  // ── Developer action plan ─────────────────────────────────────────────────

  function buildDevActionPlan() {
    const plan = doc.developerActionPlan;
    if (!plan || !plan.length) return null;

    const sec = el('section', 'section-card');
    const h2  = document.createElement('h2'); h2.textContent = 'Developer Action Plan'; sec.appendChild(h2);

    const tbl   = el('table');
    const thead = el('thead');
    const htr   = el('tr');
    for (const col of ['Priority', 'Title', 'Action', 'Impact']) {
      const th = document.createElement('th'); th.scope = 'col'; th.textContent = col; htr.appendChild(th);
    }
    thead.appendChild(htr); tbl.appendChild(thead);

    const tbody = el('tbody');
    for (const action of plan) {
      const tr = el('tr');
      [action.priority, action.title, action.action, action.impact].forEach(function (v, i) {
        const td = document.createElement('td');
        if (i >= 2) td.className = 'wrap';
        td.textContent = v || '';
        tr.appendChild(td);
      });
      tbody.appendChild(tr);
    }
    tbl.appendChild(tbody); sec.appendChild(tbl);
    return sec;
  }

  // ── Filter bar ────────────────────────────────────────────────────────────

  function buildFilterBar() {
    const findings = doc.findings || [];
    if (!findings.length) return null;

    let crit = 0, warn = 0, info = 0;
    for (const f of findings) {
      const s = (f.severity || '').toLowerCase();
      if (s === 'critical') crit++; else if (s === 'warning') warn++; else info++;
    }

    const bar = el('div', 'filter-bar');
    bar.id = 'filter-bar';
    bar.setAttribute('role', 'search');
    bar.setAttribute('aria-label', 'Filter findings');

    const group = el('div', 'filter-group');
    group.setAttribute('aria-label', 'Severity filter');

    function fbtn(sev, label, extra) {
      const btn = el('button', 'filter-btn' + (extra ? ' ' + extra : '') + (sev === 'all' ? ' active' : ''));
      btn.type = 'button';
      btn.dataset.sev = sev;
      btn.setAttribute('aria-pressed', sev === 'all' ? 'true' : 'false');
      btn.textContent = label;
      return btn;
    }
    group.appendChild(fbtn('all',      'All ('      + findings.length + ')'));
    if (crit) group.appendChild(fbtn('critical', 'Critical (' + crit + ')', 'filter-critical'));
    if (warn) group.appendChild(fbtn('warning',  'Warning ('  + warn + ')', 'filter-warning'));
    if (info) group.appendChild(fbtn('info',     'Info ('     + info + ')', 'filter-info'));
    bar.appendChild(group);

    const search     = document.createElement('input');
    search.type      = 'search';
    search.id        = 'filter-search';
    search.className = 'filter-search';
    search.placeholder = 'Search findings\u2026';
    search.setAttribute('aria-label', 'Search findings by title or evidence');
    bar.appendChild(search);

    const count = el('span', 'filter-count');
    count.id = 'filter-count';
    count.setAttribute('aria-live', 'polite');
    count.setAttribute('aria-atomic', 'true');
    bar.appendChild(count);
    return bar;
  }

  // ── Table of Contents ─────────────────────────────────────────────────────

  function buildTOC() {
    const findings = doc.findings || [];
    const sections = doc.analyzerSections || [];
    if ((!findings || !findings.length) && (!sections || !sections.length)) return null;

    const nav = el('nav', 'toc');
    nav.setAttribute('aria-label', 'Report table of contents');
    const title = el('div', 'toc-title'); title.textContent = 'Table of contents'; nav.appendChild(title);

    if (findings && findings.length) {
      const div = el('div', 'toc-section');
      const strong = document.createElement('strong'); strong.textContent = 'Findings';
      strong.setAttribute('role', 'button'); strong.setAttribute('tabindex', '0'); strong.setAttribute('aria-expanded', 'true');
      div.appendChild(strong);
      const ol = document.createElement('ol');
      for (let i = 0; i < findings.length; i++) {
        const a = document.createElement('a');
        a.href = '#finding-' + i;
        a.textContent = findings[i].title || ('Finding ' + i);
        const li = document.createElement('li'); li.appendChild(a); ol.appendChild(li);
      }
      div.appendChild(ol);
      nav.appendChild(div);
    }

    if (sections && sections.length) {
      const div = el('div', 'toc-section');
      const strong = document.createElement('strong'); strong.textContent = 'Analyzer sections';
      strong.setAttribute('role', 'button'); strong.setAttribute('tabindex', '0'); strong.setAttribute('aria-expanded', 'true');
      div.appendChild(strong);
      const ol = document.createElement('ol');
      for (let i = 0; i < sections.length; i++) {
        const a = document.createElement('a');
        a.href = '#detail-' + i;
        a.textContent = sections[i].displayTitle || sections[i].analyzerName || ('Section ' + i);
        const li = document.createElement('li'); li.appendChild(a); ol.appendChild(li);
      }
      div.appendChild(ol);
      nav.appendChild(div);
    }

    return nav;
  }

  // ── TOC + permalink UX improvements ───────────────────────────────────────

  // Smooth-scroll navigation for TOC links and permalink anchors
  document.addEventListener('click', function (e) {
    var a = e.target.closest('.toc a, .permalink');
    if (!a) return;
    var href = a.getAttribute('href');
    if (!href || href.charAt(0) !== '#') return;
    e.preventDefault();
    var id = href.substring(1);
    var target = document.getElementById(id);
    if (target) {
      target.scrollIntoView({ behavior: 'smooth', block: 'start' });
      // Ensure keyboard focus follows the jump for screen-reader users
      try {
        if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1');
        target.focus({ preventScroll: true });
      } catch (ex) { /* ignore */ }
      try { history.replaceState(null, '', '#' + id); } catch (ex) { /* ignore */ }
    }
  });

  // Collapsible TOC sections (toggle by clicking the strong header)
  document.addEventListener('click', function (e) {
    var s = e.target.closest('.toc .toc-section > strong');
    if (!s) return;
    var sec = s.parentElement;
    var collapsed = sec.classList.toggle('collapsed');
    s.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
  });
  // keyboard support for collapse/expand
  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Enter' && e.key !== ' ') return;
    var s = e.target.closest && e.target.closest('.toc .toc-section > strong');
    if (!s) return;
    e.preventDefault();
    var sec = s.parentElement;
    var collapsed = sec.classList.toggle('collapsed');
    s.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
  });

  // Active TOC highlighting using IntersectionObserver
  (function () {
    var links = document.querySelectorAll('.toc a');
    if (!links || !links.length) return;
    var idToLink = {};
    links.forEach(function (l) { if (l.hash) idToLink[l.hash.substring(1)] = l; });
    var obs = new IntersectionObserver(function (entries) {
      entries.forEach(function (ent) {
        if (!ent.target || !ent.target.id) return;
        if (ent.isIntersecting) {
          document.querySelectorAll('.toc a.active').forEach(function (x) { x.classList.remove('active'); });
          var link = idToLink[ent.target.id];
          if (link) link.classList.add('active');
        }
      });
    }, { root: null, rootMargin: '-40% 0px -40% 0px', threshold: 0 });
    var targets = document.querySelectorAll('#main [id^="finding-"], #main [id^="detail-"]');
    targets.forEach(function (t) { obs.observe(t); });
  })();

  // ── Finding cards ─────────────────────────────────────────────────────────

  function buildFindingCard(f, i) {
    const sec = el('section', 'section-card');
    sec.id = 'finding-' + i;
    sec.dataset.severity = (f.severity || 'info').toLowerCase();
    sec.dataset.title    = f.title    || '';
    sec.dataset.summary  = (f.evidence || '').substring(0, 200);

    const header = el('div', 'section-header');
    const badge  = el('span', 'severity-badge ' + sevCss(f.severity));
    badge.textContent = f.severity || 'Info';
    header.appendChild(badge);
    const h2 = document.createElement('h2'); h2.textContent = f.title || '';
    // Permalink anchor (navigates)
    const pa = document.createElement('a'); pa.className = 'permalink'; pa.href = '#finding-' + i; pa.setAttribute('aria-label', 'Permalink'); pa.textContent = '🔗';
    h2.appendChild(t(' ')); h2.appendChild(pa);
    // Copy-permalink button (uses delegated .copy-btn handler)
    const copyBtn = el('button', 'copy-btn');
    copyBtn.type = 'button';
    copyBtn.setAttribute('aria-label', 'Copy permalink');
    copyBtn.title = 'Copy permalink';
    copyBtn.dataset.copy = (location.href || '').split('#')[0] + '#finding-' + i;
    copyBtn.textContent = '\u2398';
    header.appendChild(h2);
    header.appendChild(copyBtn);
    const cat = el('span', 'category'); cat.textContent = f.category || ''; header.appendChild(cat);
    sec.appendChild(header);

    const p = document.createElement('p'); p.className = 'summary'; p.textContent = f.evidence || '';
    sec.appendChild(p);
    // convert textual inlined anchors (e.g. "#detail-3") into real links
    linkifyAnchors(p);

    const tbl   = el('table');
    const thead = el('thead');
    const htr   = el('tr');
    for (const col of ['Label', 'Value']) {
      const th = document.createElement('th'); th.scope = 'col'; th.textContent = col; htr.appendChild(th);
    }
    thead.appendChild(htr); tbl.appendChild(thead);

    const tbody = el('tbody');
    function evidenceRow(label, value) {
      const tr = el('tr');
      const td1 = document.createElement('td'); td1.textContent = label; tr.appendChild(td1);
      const td2 = document.createElement('td'); td2.className = 'wrap'; td2.textContent = value || ''; wrapAddresses(td2); linkifyAnchors(td2); tr.appendChild(td2);
      tbody.appendChild(tr);
    }
    evidenceRow('Evidence', f.evidence || '');
    if (f.recommendation) evidenceRow('Recommendation', f.recommendation);
    tbl.appendChild(tbody); sec.appendChild(tbl);
    return sec;
  }

  // ── Confidence notes ──────────────────────────────────────────────────────

  function buildConfidenceNotes() {
    const notes = doc.confidence;
    if (!notes || !notes.length) return null;

    const sec = el('section', 'section-card');
    const h2  = document.createElement('h2'); h2.textContent = 'Confidence Notes'; sec.appendChild(h2);
    const ul  = document.createElement('ul');
    for (const note of notes) {
      const li     = document.createElement('li');
      const strong = document.createElement('strong'); strong.textContent = '[' + note.analyzer + ']';
      li.appendChild(strong); li.appendChild(t(' ' + note.reason));
      ul.appendChild(li);
    }
    sec.appendChild(ul);
    return sec;
  }

  // ── Analyzer sections ─────────────────────────────────────────────────────

  function buildAnalyzerSection(section, i) {
    const wrapper = el('section', 'analyzer-section detail-color-' + (i % 6));
    wrapper.id = 'detail-' + i;

    const details = el('details');
    const summaryEl = el('summary');
    summaryEl.id = 'detail-' + i + '-summary';
    summaryEl.textContent = section.displayTitle || section.analyzerName || '';
    details.appendChild(summaryEl);

    const content = el('div', 'detail-block');
    content.setAttribute('role', 'region');
    content.setAttribute('aria-labelledby', summaryEl.id);
    renderBlocks(section.blocks || [], content);
    details.appendChild(content);
    wrapper.appendChild(details);
      // Add permalink and copy button for analyzer section summary
      const pa = document.createElement('a'); pa.className = 'permalink'; pa.href = '#detail-' + i; pa.setAttribute('aria-label', 'Permalink'); pa.textContent = '🔗';
      summaryEl.appendChild(t(' ')); summaryEl.appendChild(pa);
      const copyBtn = el('button', 'copy-btn');
      copyBtn.type = 'button';
      copyBtn.setAttribute('aria-label', 'Copy permalink');
      copyBtn.title = 'Copy permalink';
      copyBtn.dataset.copy = (location.href || '').split('#')[0] + '#detail-' + i;
      copyBtn.textContent = '\u2398';
      summaryEl.appendChild(copyBtn);
    return wrapper;
  }

  // ── Main render ───────────────────────────────────────────────────────────

  const main = document.getElementById('main');
  if (!main) return;

  main.appendChild(buildHeader());

  // Executive banner (compact metrics) + full executive summary section
  const execBanner = buildExecutiveBanner();
  if (execBanner) main.appendChild(execBanner);

  const exSec = buildExecutiveSummary();
  if (exSec) main.appendChild(exSec);

  const devSec = buildDevActionPlan();
  if (devSec) main.appendChild(devSec);

  const filterBar = buildFilterBar();
  if (filterBar) main.appendChild(filterBar);
  // Table of contents (generated from findings + analyzer sections)
  const toc = buildTOC();
  if (toc) main.appendChild(toc);

  // Render findings with pagination / chunked rendering to handle very large lists.
  function renderFindingsPaged() {
    const findings = doc.findings || [];
    if (!findings.length) return null;

    const container = el('div', 'findings-paged');
    const controls = el('div', 'pagination-controls');
    controls.setAttribute('role', 'region');
    controls.setAttribute('aria-label', 'Findings pagination');

    const prevBtn = el('button', 'action-btn findings-prev'); prevBtn.type = 'button'; prevBtn.setAttribute('aria-label', 'Previous page'); prevBtn.textContent = '← Prev';
    const nextBtn = el('button', 'action-btn findings-next'); nextBtn.type = 'button'; nextBtn.setAttribute('aria-label', 'Next page'); nextBtn.textContent = 'Next →';
    const pageInfo = el('span', 'page-info');
    const sizeSel = document.createElement('select'); sizeSel.setAttribute('aria-label', 'Findings per page');
    [[10,'10'],[20,'20'],[50,'50'],[100,'100'],[0,'All']].forEach(function (opt) { const o = document.createElement('option'); o.value = String(opt[0]); o.text = String(opt[1]); sizeSel.appendChild(o); });

    controls.appendChild(prevBtn); controls.appendChild(pageInfo); controls.appendChild(nextBtn); controls.appendChild(t(' ')); controls.appendChild(sizeSel);

    const list = el('div', 'findings-list');
    list.setAttribute('role', 'list');

    let pageSize = 10;
    let pageIndex = 0;

    function renderPage() {
      list.innerHTML = '';
      const total = findings.length;
      const start = pageSize === 0 ? 0 : pageIndex * pageSize;
      const end = pageSize === 0 ? total : Math.min(total, start + pageSize);
      for (let i = start; i < end; i++) list.appendChild(buildFindingCard(findings[i], i));
      pageInfo.textContent = pageSize === 0 ? `${total} findings` : `${start + 1}-${end} of ${total}`;
      prevBtn.disabled = (pageIndex === 0) || (pageSize === 0);
      nextBtn.disabled = (end >= total) || (pageSize === 0);
      // Hide controls if only a single page
      controls.style.display = (total <= pageSize || pageSize === 0) ? 'none' : '';
      // Re-apply focus handling for anchors inside current page
      // (no-op if nothing to focus)
    }

    prevBtn.addEventListener('click', function () { if (pageSize === 0) return; if (pageIndex > 0) { pageIndex--; renderPage(); } });
    nextBtn.addEventListener('click', function () { if (pageSize === 0) return; pageIndex++; renderPage(); });
    sizeSel.addEventListener('change', function () { pageSize = parseInt(sizeSel.value, 10) || 0; pageIndex = 0; renderPage(); });

    // Initialize selector default
    sizeSel.value = String(pageSize);
    container.appendChild(controls);
    container.appendChild(list);
    renderPage();
    return container;
  }

  const findingsPaged = renderFindingsPaged();
  if (findingsPaged) main.appendChild(findingsPaged);

  const confSec = buildConfidenceNotes();
  if (confSec) main.appendChild(confSec);

  const sections = doc.analyzerSections || [];
  for (let i = 0; i < sections.length; i++) main.appendChild(buildAnalyzerSection(sections[i], i));

  // ── Interactivity ─────────────────────────────────────────────────────────

  // aria-expanded sync on analyzer section <details>
  document.querySelectorAll('.analyzer-section details').forEach(function (d) {
    var s = d.querySelector('summary');
    if (s) s.setAttribute('aria-expanded', String(d.open));
    d.addEventListener('toggle', function () { if (s) s.setAttribute('aria-expanded', String(d.open)); });
  });

  // Copy to clipboard — event delegation so late-rendered buttons work
  var sr = document.getElementById('clipboard-status');
  function flash(m) { if (sr) { sr.textContent = m; setTimeout(function () { sr.textContent = ''; }, 2000); } }
  document.addEventListener('click', function (e) {
    var btn = e.target.closest('.copy-btn');
    if (!btn) return;
    e.preventDefault(); e.stopPropagation();
    if (navigator.clipboard) navigator.clipboard.writeText(btn.dataset.copy || '').then(function () { flash('Copied: ' + btn.dataset.copy); });
  });

  // Download JSON
  var btnJson = document.getElementById('btn-download-json');
  if (btnJson) btnJson.addEventListener('click', function () {
    var json = JSON.stringify(window.__REPORT__, null, 2);
    var blob = new Blob([json], { type: 'application/json' });
    var a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = (btnJson.dataset.filename || 'report') + '.json';
    a.click();
    URL.revokeObjectURL(a.href);
  });

  // Export CSV
  var btnCsv = document.getElementById('btn-export-csv');
  if (btnCsv) btnCsv.addEventListener('click', function () {
    var rows = [['ID', 'Severity', 'Category', 'Title', 'Evidence', 'Recommendation']];
    (doc.findings || []).forEach(function (f, i) {
      rows.push(['finding-' + i, f.severity || '', f.category || '', f.title || '', f.evidence || '', f.recommendation || '']);
    });
    var csv = rows.map(function (r) {
      return r.map(function (c) { return '"' + (c || '').replace(/"/g, '""') + '"'; }).join(',');
    }).join('\r\n');
    var blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' });
    var a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = (btnCsv.dataset.filename || 'findings') + '-findings.csv';
    a.click();
    URL.revokeObjectURL(a.href);
  });

  // Print
  var btnPrint = document.getElementById('btn-print');
  if (btnPrint) btnPrint.addEventListener('click', function () { window.print(); });

  // High-contrast toggle
  var btnContrast = document.getElementById('btn-toggle-contrast');
  function applyContrast(on) {
    if (on) document.body.classList.add('high-contrast'); else document.body.classList.remove('high-contrast');
    try { localStorage.setItem('dumpdetective:high-contrast', on ? '1' : '0'); } catch (e) { }
  }
  if (btnContrast) btnContrast.addEventListener('click', function () { applyContrast(!document.body.classList.contains('high-contrast')); });
  // Apply previously saved preference
  try { if (localStorage.getItem('dumpdetective:high-contrast') === '1') applyContrast(true); } catch (e) { }

  // Filter bar
  var fbs = document.querySelectorAll('.filter-btn[data-sev]');
  var fsi = document.getElementById('filter-search');
  var fco = document.getElementById('filter-count');

  function applyFilter() {
    var txt  = fsi ? fsi.value.trim().toLowerCase() : '';
    var asev = 'all';
    fbs.forEach(function (b) { if (b.classList.contains('active')) asev = b.dataset.sev; });
    var cards = document.querySelectorAll('.section-card[data-severity]');
    var vis = 0;
    cards.forEach(function (c) {
      var s  = (c.dataset.severity || '').toLowerCase();
      var ok = (asev === 'all' || s === asev) &&
               (!txt || (c.dataset.title   || '').toLowerCase().includes(txt) ||
                        (c.dataset.summary || '').toLowerCase().includes(txt));
      c.hidden = !ok;
      if (ok) vis++;
    });
    if (fco) fco.textContent = cards.length ? vis + ' of ' + cards.length + ' finding(s)' : '';
  }

  fbs.forEach(function (b) {
    b.addEventListener('click', function () {
      fbs.forEach(function (x) { x.classList.remove('active'); x.setAttribute('aria-pressed', 'false'); });
      b.classList.add('active'); b.setAttribute('aria-pressed', 'true');
      applyFilter();
    });
  });
  if (fsi) fsi.addEventListener('input', applyFilter);
  applyFilter();

  // Sortable table columns
  document.querySelectorAll('table').forEach(function (tbl) {
    var ths = tbl.querySelectorAll('thead th');
    ths.forEach(function (th, col) {
      th.classList.add('sortable');
      th.setAttribute('tabindex', '0');
      var dir = 1;
      function doSort() {
        var tb = tbl.querySelector('tbody');
        if (!tb) return;
        var rows = Array.from(tb.querySelectorAll('tr'));
        rows.sort(function (a, b) {
          var ac = a.cells[col], bc = b.cells[col];
          var av = ac && ac.dataset.value !== undefined && ac.dataset.value !== '' ? parseFloat(ac.dataset.value) : NaN;
          var bv = bc && bc.dataset.value !== undefined && bc.dataset.value !== '' ? parseFloat(bc.dataset.value) : NaN;
          if (!isNaN(av) && !isNaN(bv)) return dir * (av - bv);
          var at = (ac ? ac.textContent : '').toLowerCase();
          var bt = (bc ? bc.textContent : '').toLowerCase();
          return dir * (at < bt ? -1 : at > bt ? 1 : 0);
        });
        rows.forEach(function (r) { tb.appendChild(r); });
        ths.forEach(function (h) { h.removeAttribute('aria-sort'); });
        th.setAttribute('aria-sort', dir > 0 ? 'ascending' : 'descending');
        dir = -dir;
      }
      th.addEventListener('click', doSort);
      th.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); doSort(); }
      });
    });
  });

  // Render any sparklines once tables are present
  renderSparklines();

})();
