// Panels and auxiliary sections: appendix, incident context, dev action plan,
// action queue, global search bar, and filter bar.
import { el, t, formatBytes } from './report.dom.js';
import { findingAnchorId } from './report.renderers.shared.js';

// ── Appendix (analyzer run summary, memory diagnostics, known limitations) ───

export function buildAppendix(doc) {
  const appendix = doc.appendix;
  if (!appendix) return null;

  const sec = el('section', 'section-card report-appendix');
  sec.id = 'sec-appendix';

  // ── Z1. Analyzer Run Summary ─────────────────────────────────────────────
  const runs = appendix.analyzerRunSummary || [];
  if (runs.length) {
    const panel = document.createElement('details');
    panel.className = 'appendix-panel';
    panel.open = true;
    const summary = document.createElement('summary');

    const titleSpan = el('span', 'appendix-panel__title'); titleSpan.textContent = 'Analyzer Run Summary';
    summary.appendChild(titleSpan);

    const tally = { completed: 0, failed: 0, skipped: 0, timedout: 0 };
    for (const r of runs) {
      const s = (r.status || '').toLowerCase();
      if (s === 'completed' || s === 'success') tally.completed++;
      else if (s === 'failed') tally.failed++;
      else if (s.startsWith('skipped')) tally.skipped++;
      else if (s === 'timedout') tally.timedout++;
    }
    const metaSpan = el('span', 'appendix-panel__meta');
    metaSpan.textContent = runs.length + ' analyzers';
    summary.appendChild(metaSpan);
    panel.appendChild(summary);

    const body = el('div', 'appendix-panel__body');

    const tallyRow = el('div', 'appendix-tally');
    function chip(count, label, mod) {
      if (!count) return;
      const c = el('span', 'appendix-tally__chip appendix-tally__chip--' + mod);
      c.textContent = count + '\u00A0' + label; tallyRow.appendChild(c);
    }
    chip(tally.completed, 'Completed', 'completed');
    chip(tally.failed,    'Failed',    'failed');
    chip(tally.timedout,  'Timed out', 'timedout');
    chip(tally.skipped,   'Skipped',   'skipped');
    body.appendChild(tallyRow);

    const list = el('div', 'appendix-run-list');
    for (const run of runs) {
      const s = (run.status || 'unknown').toLowerCase();
      const sNorm = (s === 'success') ? 'completed' : s;
      const row = el('div', 'appendix-run-row appendix-run-row--' + sNorm);

      const name = el('div', 'appendix-run-row__name'); name.textContent = run.analyzerName || ''; row.appendChild(name);

      const stats = el('div', 'appendix-run-row__stats');
      const parts = [];
      if (run.findingCount) parts.push(run.findingCount + '\u00A0findings');
      if (run.objectScanCount) parts.push(Number(run.objectScanCount).toLocaleString('en-US') + '\u00A0objs scanned');
      if (run.cacheHits || run.cacheMisses) parts.push('cache\u00A0' + (run.cacheHits || 0) + '/' + ((run.cacheHits || 0) + (run.cacheMisses || 0)));
      stats.textContent = parts.join('\u2002\u00B7\u2002'); row.appendChild(stats);

      const dur = el('div', 'appendix-run-row__dur');
      dur.textContent = Number(run.durationMs || 0) >= 1000
        ? (Number(run.durationMs) / 1000).toFixed(2) + '\u00A0s'
        : Number(run.durationMs || 0).toFixed(0) + '\u00A0ms';
      row.appendChild(dur);

      const pill = el('span', 'appendix-run-row__pill appendix-run-row__pill--' + sNorm);
      pill.textContent = sNorm.charAt(0).toUpperCase() + sNorm.slice(1); row.appendChild(pill);

      const note = run.errorMessage || run.findingGeneratorError || run.skipReason || '';
      if (note) { const noteEl = el('div', 'appendix-run-row__note'); noteEl.textContent = note; row.appendChild(noteEl); }

      list.appendChild(row);
    }
    body.appendChild(list);
    panel.appendChild(body);
    sec.appendChild(panel);
  }

  // ── Z2. Memory Diagnostics ───────────────────────────────────────────────
  const memory = appendix.memoryDiagnostics || [];
  if (memory.length) {
    const panel = document.createElement('details');
    panel.className = 'appendix-panel';
    const summary = document.createElement('summary');
    const titleSpan = el('span', 'appendix-panel__title'); titleSpan.textContent = 'Memory Diagnostics';
    const metaSpan = el('span', 'appendix-panel__meta'); metaSpan.textContent = memory.length + ' analyzers';
    summary.appendChild(titleSpan); summary.appendChild(metaSpan);
    panel.appendChild(summary);

    const body = el('div', 'appendix-panel__body');
    const wrap = el('div', 'detail-block');
    wrap.appendChild(buildSimpleTable(
      ['Analyzer', 'WS Before', 'WS After', 'WS \u0394', 'Heap Before', 'Heap After', 'Heap \u0394'],
      memory.map(r => [
        r.analyzerName || '',
        formatBytes(Number(r.workingSetBefore || 0)),
        formatBytes(Number(r.workingSetAfter || 0)),
        (Number(r.workingSetDelta || 0) > 0 ? '+' : '') + formatBytes(Number(r.workingSetDelta || 0)),
        formatBytes(Number(r.managedHeapBefore || 0)),
        formatBytes(Number(r.managedHeapAfter || 0)),
        (Number(r.managedHeapDelta || 0) > 0 ? '+' : '') + formatBytes(Number(r.managedHeapDelta || 0))
      ])
    ));
    body.appendChild(wrap);
    panel.appendChild(body);
    sec.appendChild(panel);
  }

  // ── Z3. Known Limitations ────────────────────────────────────────────────
  const limitations = appendix.knownLimitations || [];
  if (limitations.length) {
    const panel = document.createElement('details');
    panel.className = 'appendix-panel';
    const summary = document.createElement('summary');
    const titleSpan = el('span', 'appendix-panel__title'); titleSpan.textContent = 'Known Limitations';
    const metaSpan = el('span', 'appendix-panel__meta'); metaSpan.textContent = limitations.length + ' notes';
    summary.appendChild(titleSpan); summary.appendChild(metaSpan);
    panel.appendChild(summary);

    const body = el('div', 'appendix-panel__body');
    const ul = el('ul', 'appendix-limitations');
    for (const item of limitations) { const li = document.createElement('li'); li.textContent = item; ul.appendChild(li); }
    body.appendChild(ul);
    panel.appendChild(body);
    sec.appendChild(panel);
  }

  return sec;
}

// Private table helpers

function buildSimpleTable(headers, rows) {
  const table = el('table');
  const thead = el('thead');
  const htr = el('tr');
  for (const h of headers) {
    const th = document.createElement('th');
    th.scope = 'col';
    th.textContent = h;
    htr.appendChild(th);
  }
  thead.appendChild(htr);
  table.appendChild(thead);

  const tbody = el('tbody');
  for (const row of rows) {
    const tr = el('tr');
    for (const cell of row) tr.appendChild(tdText(String(cell ?? '')));
    tbody.appendChild(tr);
  }
  table.appendChild(tbody);
  return table;
}

function tdText(text) {
  const td = document.createElement('td');
  td.textContent = text;
  return td;
}

// ── Incident context (runtime settings + snapshot list) ──────────────────────

export function buildIncidentContext(doc) {
  const ctx = doc.incidentContext; if (!ctx) return null;
  const sec = el('section', 'section-card incident-context-card');
  const h2 = document.createElement('h2'); h2.textContent = 'Incident Context'; sec.appendChild(h2);
  const subtitle = document.createElement('p'); subtitle.className = 'incident-context__subtitle'; subtitle.textContent = 'Runtime settings and snapshot context used to generate this report.'; sec.appendChild(subtitle);

  const summary = el('div', 'incident-context__summary');
  function stat(label, value) {
    const item = el('div', 'incident-context__stat');
    const statLabel = el('div', 'incident-context__stat-label'); statLabel.textContent = label;
    const statValue = el('div', 'incident-context__stat-value'); statValue.textContent = value || '\u2014';
    item.appendChild(statLabel); item.appendChild(statValue); summary.appendChild(item);
  }
  stat('Mode', ctx.mode || '');
  stat('Report', ((ctx.reportFormat || '') + ' / ' + (ctx.reportAudience || '')).trim().replace(/^\s*\/\s*|\s*\/\s*$/g, '').replace(/^\s*$/, '\u2014'));
  stat('Runtime', ((ctx.runtimeFlavor || 'n/a') + (ctx.runtimeVersion ? ' ' + ctx.runtimeVersion : '')).trim());
  stat('GC Mode', ctx.gcMode || 'n/a');
  stat('Heap Count', ctx.heapCount != null ? String(ctx.heapCount) : 'n/a');
  stat('Active Analyzers', String(ctx.activeAnalyzerCount || 0));
  sec.appendChild(summary);

  const details = el('div', 'incident-context__details');
  function detail(label, value) {
    const item = el('div', 'incident-context__detail');
    const detailLabel = el('div', 'incident-context__detail-label'); detailLabel.textContent = label;
    const detailValue = el('div', 'incident-context__detail-value'); detailValue.textContent = value || '\u2014';
    item.appendChild(detailLabel); item.appendChild(detailValue); details.appendChild(item);
  }
  detail('Dump Path', ctx.dumpPath || '');
  if (ctx.baselineDumpPath) detail('Baseline Dump', ctx.baselineDumpPath);
  detail('Config', (ctx.usedConfigFile ? 'config file' : 'command line') + (ctx.configPath ? ' (' + ctx.configPath + ')' : ''));
  detail('Diagnostic Mode', ctx.diagnosticMode ? 'on' : 'off');
  detail('Index Prebuild', ctx.indexPrebuildMode || '');
  detail('Heap Walkable', ctx.heapCanWalk ? 'yes' : 'no');
  detail('Analysis Elapsed', (Number(ctx.analysisElapsedSeconds || 0)).toFixed(1) + 's');
  sec.appendChild(details);

  if (ctx.trendSnapshots && ctx.trendSnapshots.length) {
    const h3 = document.createElement('h3'); h3.textContent = 'Snapshot Contexts'; sec.appendChild(h3);
    const snaps = el('div', 'incident-context__snapshots');
    for (const snap of ctx.trendSnapshots) {
      const card = el('div', 'incident-context__snapshot');
      const label = snap.isBaseline ? 'Baseline' : (snap.isCurrent ? 'Current' : ('Snapshot ' + (snap.index + 1)));
      const title = el('div', 'incident-context__snapshot-title'); title.textContent = label;
      const path = el('div', 'incident-context__snapshot-path'); path.textContent = snap.dumpPath || '\u2014';
      const meta = el('div', 'incident-context__snapshot-meta'); meta.textContent = (Number(snap.elapsedSeconds || 0)).toFixed(1) + 's \u2022 ' + String(snap.analyzerCount || 0) + ' analyzers \u2022 ' + String(snap.findingCount || 0) + ' findings';
      card.appendChild(title); card.appendChild(path); card.appendChild(meta); snaps.appendChild(card);
    }
    sec.appendChild(snaps);
  }
  return sec;
}

// ── Developer Action Plan (legacy flat-list mode only) ────────────────────────

export function buildDevActionPlan(doc) {
  if (Array.isArray(doc.domains) && doc.domains.length) return null;
  const plan = doc.developerActionPlan;
  if (!plan || !plan.length) return null;
  const sec = el('section', 'section-card');
  const h2 = document.createElement('h2'); h2.textContent = 'Developer Action Plan'; sec.appendChild(h2);
  const tbl = el('table');
  const thead = el('thead'); const htr = el('tr');
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

// ── Action Queue (prioritized actionable findings table) ─────────────────────

export function buildActionQueuePanel(doc) {
  const findings = Array.isArray(doc.findings) ? doc.findings : [];
  if (!findings.length) return null;

  function sevWeight(sev) {
    const s = String(sev || '').toLowerCase();
    if (s === 'critical') return 3;
    if (s === 'warning') return 2;
    return 1;
  }

  const actionable = findings.filter(function (f) {
    return !!(f.recommendation || f.fix || (Array.isArray(f.recommendationItems) && f.recommendationItems.length));
  }).sort(function (a, b) {
    const sevCmp = sevWeight(b.severity) - sevWeight(a.severity);
    if (sevCmp !== 0) return sevCmp;
    const confA = Number(a.confidenceScore || 0);
    const confB = Number(b.confidenceScore || 0);
    if (confB !== confA) return confB - confA;
    return String(a.title || '').localeCompare(String(b.title || ''));
  });

  if (!actionable.length) return null;

  const sec = el('section', 'section-card action-queue-card');
  sec.id = 'sec-action-queue';
  const h2 = document.createElement('h2');
  h2.textContent = 'Action Queue';
  sec.appendChild(h2);

  const subtitle = el('p', 'action-queue-card__subtitle');
  subtitle.textContent = 'Prioritized workflow view from high-impact findings.';
  sec.appendChild(subtitle);

  const tbl = el('table');
  const thead = el('thead');
  const htr = el('tr');
  ['Priority', 'Finding', 'Owner', 'Effort', 'Status', 'Validation'].forEach(function (col) {
    const th = document.createElement('th');
    th.scope = 'col';
    th.textContent = col;
    htr.appendChild(th);
  });
  thead.appendChild(htr);
  tbl.appendChild(thead);

  const tbody = el('tbody');
  const maxRows = 20;
  for (let i = 0; i < actionable.length && i < maxRows; i++) {
    const finding = actionable[i];
    const tr = el('tr');

    const tdPri = document.createElement('td');
    tdPri.textContent = String(i + 1);
    tr.appendChild(tdPri);

    const tdFinding = document.createElement('td');
    const anchor = document.createElement('a');
    anchor.href = '#' + findingAnchorId(finding, 'queue-' + i);
    anchor.textContent = finding.title || ('Finding ' + (i + 1));
    tdFinding.appendChild(anchor);
    const recText = finding.fix || finding.recommendation || ((Array.isArray(finding.recommendationItems) && finding.recommendationItems.length) ? finding.recommendationItems[0] : '');
    if (recText) {
      const note = el('div', 'action-queue-card__note');
      note.textContent = recText;
      tdFinding.appendChild(note);
    }
    tr.appendChild(tdFinding);

    const tdOwner = document.createElement('td');
    tdOwner.textContent = finding.suggestedOwner || '-';
    tr.appendChild(tdOwner);

    const tdEffort = document.createElement('td');
    tdEffort.textContent = finding.effort || '-';
    tr.appendChild(tdEffort);

    const tdStatus = document.createElement('td');
    tdStatus.textContent = finding.trackingStatus || 'Open';
    tr.appendChild(tdStatus);

    const tdValidation = document.createElement('td');
    tdValidation.textContent = finding.validationStep || '-';
    tdValidation.className = 'wrap';
    tr.appendChild(tdValidation);

    tbody.appendChild(tr);
  }

  tbl.appendChild(tbody);
  sec.appendChild(tbl);
  return sec;
}

// ── Global search bar ─────────────────────────────────────────────────────────

export function buildGlobalSearchBar(doc) {
  const bar = el('div', 'filter-bar global-search-bar');
  bar.id = 'global-search-bar';
  bar.setAttribute('role', 'search');
  bar.setAttribute('aria-label', 'Search across full report');

  const label = el('span', 'global-search-label');
  label.textContent = 'Global search';
  bar.appendChild(label);

  const input = document.createElement('input');
  input.type = 'search';
  input.id = 'global-search-input';
  input.className = 'filter-search global-search-input';
  input.placeholder = 'Search across all sections, findings, and tables\u2026';
  input.setAttribute('aria-label', 'Search entire report');
  bar.appendChild(input);

  const prev = el('button', 'action-btn global-search-nav');
  prev.type = 'button';
  prev.id = 'global-search-prev';
  prev.textContent = 'Prev';
  prev.setAttribute('aria-label', 'Previous search result');
  bar.appendChild(prev);

  const next = el('button', 'action-btn global-search-nav');
  next.type = 'button';
  next.id = 'global-search-next';
  next.textContent = 'Next';
  next.setAttribute('aria-label', 'Next search result');
  bar.appendChild(next);

  const clear = el('button', 'action-btn global-search-clear');
  clear.type = 'button';
  clear.id = 'global-search-clear';
  clear.textContent = 'Clear';
  clear.setAttribute('aria-label', 'Clear report search');
  bar.appendChild(clear);

  const count = el('span', 'filter-count global-search-count');
  count.id = 'global-search-count';
  count.setAttribute('aria-live', 'polite');
  count.setAttribute('aria-atomic', 'true');
  bar.appendChild(count);

  return bar;
}

// ── Filter bar (severity + text search for findings list) ─────────────────────

export function buildFilterBar(doc) {
  const findings = doc.findings || [];
  if (!findings.length) return null;
  let crit = 0, warn = 0, info = 0;
  for (const f of findings) {
    const s = (f.severity || '').toLowerCase();
    if (s === 'critical') crit++; else if (s === 'warning') warn++; else info++;
  }
  const bar = el('div', 'filter-bar'); bar.id = 'filter-bar';
  bar.setAttribute('role', 'search'); bar.setAttribute('aria-label', 'Filter findings');
  const group = el('div', 'filter-group'); group.setAttribute('aria-label', 'Severity filter');
  function fbtn(sev, label, extra) {
    const btn = el('button', 'filter-btn' + (extra ? ' ' + extra : '') + (sev === 'all' ? ' active' : ''));
    btn.type = 'button'; btn.dataset.sev = sev;
    btn.setAttribute('aria-pressed', sev === 'all' ? 'true' : 'false');
    btn.textContent = label; return btn;
  }
  group.appendChild(fbtn('all', 'All (' + findings.length + ')'));
  if (crit) group.appendChild(fbtn('critical', 'Critical (' + crit + ')', 'filter-critical'));
  if (warn) group.appendChild(fbtn('warning', 'Warning (' + warn + ')', 'filter-warning'));
  if (info) group.appendChild(fbtn('info', 'Info (' + info + ')', 'filter-info'));
  bar.appendChild(group);
  const search = document.createElement('input'); search.type = 'search'; search.id = 'filter-search';
  search.className = 'filter-search'; search.placeholder = 'Search findings\u2026';
  search.setAttribute('aria-label', 'Search findings by title or evidence');
  bar.appendChild(search);
  const count = el('span', 'filter-count'); count.id = 'filter-count';
  count.setAttribute('aria-live', 'polite'); count.setAttribute('aria-atomic', 'true');
  bar.appendChild(count);
  return bar;
}
