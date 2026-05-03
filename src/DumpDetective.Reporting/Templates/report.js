/* DumpDetective Report Renderer
 * Reads window.__REPORT__ (AnalysisReportDocument JSON) and builds the entire page DOM.
 * Security: all user-originated strings use textContent — never innerHTML.
 */
(function () {
  'use strict';

  const doc = window.__REPORT__;
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
    for (const row of (block.rows || [])) {
      const tr = el('tr');
      for (const cell of (row.cells || [])) {
        const td = document.createElement('td');
        td.textContent = cell.display || '';
        if (cell.rawValue != null) td.dataset.value = cell.rawValue;
        wrapAddresses(td);
        tr.appendChild(td);
      }
      tbody.appendChild(tr);
    }
    tbl.appendChild(tbody);
    return tbl;
  }

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
    const h2 = document.createElement('h2'); h2.textContent = f.title || ''; header.appendChild(h2);
    const cat = el('span', 'category'); cat.textContent = f.category || ''; header.appendChild(cat);
    sec.appendChild(header);

    const p = document.createElement('p'); p.className = 'summary'; p.textContent = f.evidence || '';
    sec.appendChild(p);

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
      const td2 = document.createElement('td'); td2.className = 'wrap'; td2.textContent = value || ''; wrapAddresses(td2); tr.appendChild(td2);
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
    return wrapper;
  }

  // ── Main render ───────────────────────────────────────────────────────────

  const main = document.getElementById('main');
  if (!main) return;

  main.appendChild(buildHeader());

  const exSec = buildExecutiveSummary();
  if (exSec) main.appendChild(exSec);

  const devSec = buildDevActionPlan();
  if (devSec) main.appendChild(devSec);

  const filterBar = buildFilterBar();
  if (filterBar) main.appendChild(filterBar);

  const findings = doc.findings || [];
  for (let i = 0; i < findings.length; i++) main.appendChild(buildFindingCard(findings[i], i));

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

})();
