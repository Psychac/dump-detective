// Panels and auxiliary sections: appendix, incident context, dev action plan,
// action queue, global search bar, and filter bar.
import { el, t, formatBytes, nvl } from './report.dom.js';
import { findingAnchorId } from './report.renderers.shared.js';

// ── Appendix (analyzer run summary, memory diagnostics, known limitations) ───

export function buildAppendix(doc) {
  const appendix = doc.appendix;
  if (!appendix) return null;

  const sec = el('section', 'section-card report-appendix');
  sec.id = 'sec-appendix';
  sec.setAttribute('data-component-id', 'appendix');

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
      memory.map(r => {
        const analyzer = String(r.an || '');
        const wsb = Number(r.wsB || 0);
        const wsa = Number(r.wsA || 0);
        const mhb = Number(r.mhB || 0);
        const mha = Number(r.mhA || 0);
        const wsDelta = wsa - wsb;
        const heapDelta = mha - mhb;
        const wsPct = (wsb > 0) ? (wsDelta / wsb) * 100 : null;
        const heapPct = (mhb > 0) ? (heapDelta / mhb) * 100 : null;
        const fmtDelta = (val, pct) => {
          const sign = val > 0 ? '+' : '';
          const pctText = (pct == null) ? '' : (' (' + (pct > 0 ? '+' : '') + pct.toFixed(1) + '%)');
          return sign + formatBytes(val) + pctText;
        };

        return [
          analyzer,
          formatBytes(wsb),
          formatBytes(wsa),
          { text: fmtDelta(wsDelta, wsPct), cls: (wsDelta > 0 ? 'delta-negative' : (wsDelta < 0 ? 'delta-positive' : 'delta-neutral')) },
          formatBytes(mhb),
          formatBytes(mha),
          { text: fmtDelta(heapDelta, heapPct), cls: (heapDelta > 0 ? 'delta-negative' : (heapDelta < 0 ? 'delta-positive' : 'delta-neutral')) }
        ];
      })
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
  table.dataset.responsiveStack = '1';
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
    for (let ci = 0; ci < row.length; ci++) {
      const cell = nvl(row[ci], '');
      tr.appendChild(tdText(cell, headers[ci] || ('Column ' + (ci + 1))));
    }
    tbody.appendChild(tr);
  }
  table.appendChild(tbody);
  return table;
}

function tdText(value, colLabel) {
  const td = document.createElement('td');
  td.dataset.colLabel = String(colLabel || '');
  if (value && typeof value === 'object') {
    const text = value.text != null ? String(value.text) : '';
    if (value.cls) td.className = String(value.cls);
    if (value.title) td.title = String(value.title);
    td.textContent = text;
  } else {
    td.textContent = String(value != null ? value : '');
  }
  return td;
}

// ── Incident context (runtime settings + snapshot list) ──────────────────────

export function buildIncidentContext(doc) {
  const ctx = doc.incidentContext; if (!ctx) return null;
  const isTrend = !!(doc['$kind'] === 'trend' || doc.isTrendReport);
  const snapshots = Array.isArray(ctx.trendSnapshots) ? ctx.trendSnapshots : [];
  function fmtWhen(utc) {
    if (!utc) return '\u2014';
    const dt = new Date(utc);
    if (Number.isNaN(dt.getTime())) return '\u2014';
    return dt.toLocaleString(undefined, {
      month: 'short',
      day: 'numeric',
      year: 'numeric',
      hour: '2-digit',
      minute: '2-digit'
    });
  }

  function fmtSpan(firstUtc, lastUtc) {
    if (!firstUtc || !lastUtc) return '\u2014';
    const a = new Date(firstUtc).getTime();
    const b = new Date(lastUtc).getTime();
    if (Number.isNaN(a) || Number.isNaN(b)) return '\u2014';
    const days = Math.round(Math.abs(b - a) / 86400000);
    if (days < 1) return 'same day';
    if (days === 1) return '1 day';
    if (days < 30) return days + ' days';
    const months = Math.round(days / 30.44);
    if (months < 12) return months + (months === 1 ? ' month' : ' months');
    const years = (days / 365.25).toFixed(1).replace(/\.0$/, '');
    return years + (years === '1' ? ' year' : ' years');
  }

  const sec = el('section', 'section-card incident-context-card');
  const h2 = document.createElement('h2'); h2.textContent = 'Incident Context'; sec.appendChild(h2);
  const subtitle = document.createElement('p');
  subtitle.className = 'incident-context__subtitle';
  subtitle.textContent = isTrend
    ? 'Runtime settings and trend snapshot context used to generate this report.'
    : 'Runtime settings and snapshot context used to generate this report.';
  sec.appendChild(subtitle);

  const summary = el('div', 'incident-context__summary');
  function stat(label, value) {
    const item = el('div', 'incident-context__stat');
    const statLabel = el('div', 'incident-context__stat-label'); statLabel.textContent = label;
    const statValue = el('div', 'incident-context__stat-value'); statValue.textContent = value || '\u2014';
    item.appendChild(statLabel); item.appendChild(statValue); summary.appendChild(item);
  }
  stat('Mode', ctx.mode || '');
  stat('Report', ((ctx.reportFormat || '')).trim().replace(/^\s*$/, '\u2014'));
  stat('Runtime', ((ctx.runtimeFlavor || 'n/a') + (ctx.runtimeVersion ? ' ' + ctx.runtimeVersion : '')).trim());
  stat('GC Mode', ctx.gcMode || 'n/a');
  stat('Heap Count', ctx.heapCount != null ? String(ctx.heapCount) : 'n/a');
  stat('Active Analyzers', String(ctx.activeAnalyzerCount || 0));
  if (isTrend) stat('Snapshots', String(doc.trendDumpCount || snapshots.length || 0));
  sec.appendChild(summary);

  if (isTrend) {
    const baselinePath = snapshots.length ? (snapshots[0].dumpPath || ctx.baselineDumpPath || '') : (ctx.baselineDumpPath || '');
    const currentPath = snapshots.length ? (snapshots[snapshots.length - 1].dumpPath || ctx.dumpPath || '') : (ctx.dumpPath || '');
    const baselineSnap = snapshots.length ? snapshots[0] : null;
    const currentSnap = snapshots.length ? snapshots[snapshots.length - 1] : null;
    const firstUtc = baselineSnap ? (baselineSnap.dumpCapturedAtUtc || baselineSnap.generatedAtUtc) : null;
    const lastUtc = currentSnap ? (currentSnap.dumpCapturedAtUtc || currentSnap.generatedAtUtc) : null;

    const overview = el('div', 'incident-context__trend-overview');
    function trendCard(label, value, mod) {
      const item = el('div', 'incident-context__trend-card' + (mod ? (' incident-context__trend-card--' + mod) : ''));
      const detailLabel = el('div', 'incident-context__trend-card-label'); detailLabel.textContent = label;
      const detailValue = el('div', 'incident-context__trend-card-value'); detailValue.textContent = value || '\u2014';
      item.appendChild(detailLabel); item.appendChild(detailValue); overview.appendChild(item);
    }
    trendCard('Baseline Dump', baselinePath, 'baseline');
    trendCard('Current Dump', currentPath, 'current');
    trendCard('Snapshot Window', fmtWhen(firstUtc) + ' -> ' + fmtWhen(lastUtc), 'window');
    trendCard('Span', fmtSpan(firstUtc, lastUtc), 'meta');
    sec.appendChild(overview);
  } else {
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
  }

  if (isTrend && snapshots.length) {
    const h3 = document.createElement('h3'); h3.textContent = 'Snapshot Runtime Context'; sec.appendChild(h3);
    const tableWrap = el('div', 'detail-block');

    const rows = snapshots.map(function (snap) {
      const role = snap.isBaseline ? 'Baseline' : (snap.isCurrent ? 'Current' : ('Snapshot ' + (Number(snap.index || 0) + 1)));
      const usedCaptured = !!snap.dumpCapturedAtUtc;
      const when = fmtWhen(snap.dumpCapturedAtUtc || snap.generatedAtUtc);
      const size = snap.dumpFileSizeBytes != null && Number(snap.dumpFileSizeBytes) > 0
        ? formatBytes(Number(snap.dumpFileSizeBytes))
        : '\u2014';
      const elapsed = Number(snap.elapsedSeconds || 0).toFixed(1) + 's';
      const source = usedCaptured ? 'CapturedAtUtc' : (snap.generatedAtUtc ? 'GeneratedAtUtc' : '\u2014');
      return [
        role,
        snap.dumpPath || '\u2014',
        when,
        size,
        elapsed,
        source
      ];
    });

    tableWrap.appendChild(buildSimpleTable(
      ['Role', 'Dump Path', 'Captured/Generated', 'Dump Size', 'Snapshot Elapsed', 'Timestamp Source'],
      rows
    ));
    const matrixTable = tableWrap.querySelector('table');
    if (matrixTable) {
      matrixTable.classList.add('incident-context__matrix');
      const bodyRows = matrixTable.querySelectorAll('tbody tr');
      bodyRows.forEach(function (tr) {
        const roleCell = tr.cells && tr.cells.length ? tr.cells[0] : null;
        const roleText = roleCell ? String(roleCell.textContent || '').toLowerCase() : '';
        if (roleCell) {
          const rawRole = String(roleCell.textContent || '').trim();
          roleCell.textContent = '';
          const badge = el('span', 'incident-context__role-badge');
          badge.textContent = rawRole || '\u2014';
          if (roleText === 'baseline') badge.classList.add('incident-context__role-badge--baseline');
          else if (roleText === 'current') badge.classList.add('incident-context__role-badge--current');
          else badge.classList.add('incident-context__role-badge--snapshot');
          roleCell.appendChild(badge);
        }
        if (tr.cells && tr.cells.length >= 6) {
          tr.cells[1].classList.add('incident-context__matrix-cell--path');
          tr.cells[2].classList.add('incident-context__matrix-cell--date');
          tr.cells[3].classList.add('incident-context__matrix-cell--numeric');
          tr.cells[4].classList.add('incident-context__matrix-cell--numeric');
        }
        if (roleText === 'baseline') tr.classList.add('incident-context__matrix-row--baseline');
        if (roleText === 'current') tr.classList.add('incident-context__matrix-row--current');
      });
    }
    sec.appendChild(tableWrap);
  } else if (snapshots.length) {
    const h3 = document.createElement('h3'); h3.textContent = 'Snapshot Contexts'; sec.appendChild(h3);
    const snaps = el('div', 'incident-context__snapshots');
    for (const snap of snapshots) {
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
  const summary = doc && doc.executiveSummary ? doc.executiveSummary : null;
  const topActions = summary && Array.isArray(summary.topActions) ? summary.topActions : [];
  const findings = Array.isArray(doc.findings) ? doc.findings : [];
  let ticketMenuIdAssigned = false;
  // Derive canonical dump paths for trend reports or per-dump docs.
  const paths = Array.isArray(doc.trendDumpPaths) && doc.trendDumpPaths.length
    ? doc.trendDumpPaths
    : (Array.isArray(doc.perDumpDocs) && doc.perDumpDocs.length
      ? doc.perDumpDocs.map(d => d && d.dumpPath ? d.dumpPath : '').filter(p => !!p)
      : []);

  function buildTicketPayload(provider, actionLike, priority) {
    const incidentTitle = String((paths && paths.length ? paths[paths.length - 1] : (doc && doc.dumpPath)) || 'DumpDetective incident').replace(/\\/g, '/').split('/').pop();
    const header = provider === 'ado'
      ? 'Azure DevOps Work Item Draft'
      : provider === 'jira'
        ? 'Jira Issue Draft'
        : 'GitHub Issue Draft';
    const title = String((actionLike && actionLike.title) || ('Finding ' + priority));
    const whyNow = String((actionLike && actionLike.whyNow) || 'Risk requires follow-up.');
    const validation = String((actionLike && actionLike.validation) || 'Re-run dump and verify the signal drops.');
    const limitationList = (doc && doc.appendix && Array.isArray(doc.appendix.knownLimitations)) ? doc.appendix.knownLimitations : [];
    const limitations = limitationList.slice(0, 3).join(' | ');
    const anchor = resolveFindingAnchor(actionLike, 'queue-ticket-' + priority);
    const href = (location.href || '').split('#')[0] + anchor;

    return [
      header,
      '',
      'Incident: ' + (incidentTitle || 'Unknown dump'),
      'Priority: ' + priority,
      'Action: ' + title,
      '',
      'Why now:',
      whyNow,
      '',
      'Validation:',
      validation,
      '',
      'Details:',
      href,
      '',
      'Known limitations:',
      limitations || 'None recorded'
    ].join('\n');
  }

  function buildTicketMenu(actionLike, priority) {
    const wrap = el('div', 'action-queue-card__ticket-menu');
    if (!ticketMenuIdAssigned) {
      wrap.id = 'ticket-template-menu';
      ticketMenuIdAssigned = true;
    }
    wrap.setAttribute('role', 'group');
    wrap.setAttribute('aria-label', 'Ticket templates');

    function iconSvg(provider) {
      if (provider === 'ado') {
        return '<svg viewBox="0 0 24 24" focusable="false" aria-hidden="true"><path fill="#0078D4" d="M2 8.25l6.3-2.2v11.9L2 15.75v-7.5zm7.7-2.63L22 2v20l-12.3-3.62V5.62zm1.8 2.57v7.62l7.8 2.28V5.91l-7.8 2.28z"/></svg>';
      }
      if (provider === 'jira') {
        return '<svg viewBox="0 0 24 24" focusable="false" aria-hidden="true"><path fill="#0052CC" d="M11.96 2L6.1 7.83l2.93 2.92 2.93-2.92L14.9 10.75 6.1 19.5 9.03 22.4 17.83 13.65l2.93 2.93 2.94-2.93L11.96 2z"/></svg>';
      }
      return '<svg viewBox="0 0 24 24" focusable="false" aria-hidden="true"><path fill="currentColor" d="M12 .5A11.5 11.5 0 00.5 12a11.5 11.5 0 007.86 10.92c.58.1.79-.25.79-.56v-2.02c-3.2.7-3.88-1.36-3.88-1.36-.52-1.33-1.28-1.68-1.28-1.68-1.05-.72.08-.7.08-.7 1.16.08 1.77 1.19 1.77 1.19 1.03 1.75 2.7 1.25 3.36.95.1-.75.4-1.25.73-1.54-2.55-.29-5.24-1.28-5.24-5.68 0-1.25.45-2.28 1.18-3.08-.12-.29-.51-1.45.11-3.03 0 0 .96-.31 3.14 1.18a10.9 10.9 0 015.72 0c2.18-1.49 3.14-1.18 3.14-1.18.62 1.58.23 2.74.11 3.03.73.8 1.18 1.83 1.18 3.08 0 4.42-2.7 5.38-5.27 5.66.42.36.79 1.06.79 2.14v3.17c0 .31.21.67.8.56A11.5 11.5 0 0023.5 12 11.5 11.5 0 0012 .5z"/></svg>';
    }

    function ticketBtn(label, provider, iconText) {
      const btn = el('button', 'action-btn ticket-copy-btn ticket-copy-btn--icon ticket-copy-btn--' + provider);
      btn.type = 'button';
      btn.dataset.payload = buildTicketPayload(provider, actionLike, priority);
      btn.dataset.provider = provider;
      btn.setAttribute('aria-label', 'Copy ' + label + ' ticket template');
      btn.title = label;
      const icon = el('span', 'ticket-copy-btn__icon');
      icon.innerHTML = iconSvg(provider);
      btn.appendChild(icon);

      const sr = el('span', 'sr-only');
      sr.textContent = iconText;
      btn.appendChild(sr);
      return btn;
    }

    wrap.appendChild(ticketBtn('Copy ADO', 'ado', 'A'));
    wrap.appendChild(ticketBtn('Copy Jira', 'jira', 'J'));
    wrap.appendChild(ticketBtn('Copy GitHub', 'github', 'GH'));
    return wrap;
  }

  function parseCompositeScore(actionLike) {
    const candidates = [
      actionLike && actionLike.impact,
      actionLike && actionLike.whyNow,
      actionLike && actionLike.action
    ];

    for (let i = 0; i < candidates.length; i++) {
      const text = String(candidates[i] || '').trim();
      if (!text) continue;

      const m = text.match(/composite\s+score\s+(\d+)(?:\s*\(([^)]*)\))?/i);
      if (!m) continue;

      const score = Number(m[1]);
      if (!Number.isFinite(score)) continue;

      return {
        score,
        source: text,
        breakdown: m[2] || ''
      };
    }

    return null;
  }

  function resolveFindingAnchor(actionLike, fallbackKey) {
    const fingerprint = String((actionLike && (actionLike.findingFingerprint || actionLike.id)) || '').trim();
    const analyzer = String((actionLike && actionLike.analyzer) || '').trim().toLowerCase();
    const title = String((actionLike && actionLike.title) || '').trim().toLowerCase();

    let match = null;
    if (fingerprint) {
      match = findings.find(function (f) {
        return String((f && f.id) || '').trim() === fingerprint;
      }) || null;
    }

    if (!match && title) {
      match = findings.find(function (f) {
        const fTitle = String((f && f.title) || '').trim().toLowerCase();
        if (fTitle !== title) return false;
        if (!analyzer) return true;
        return String((f && f.analyzer) || '').trim().toLowerCase() === analyzer;
      }) || null;
    }

    if (match) return '#' + findingAnchorId(match, fallbackKey);
    return '#' + findingAnchorId(actionLike, fallbackKey);
  }

  function formatSignalLabel(signalKey) {
    const raw = String(signalKey || '');
    const idx = raw.indexOf(':');
    const token = (idx >= 0 ? raw.slice(idx + 1) : raw).trim().toLowerCase();
    if (!token) return '';

    const pretty = token
      .replace(/[_-]+/g, ' ')
      .replace(/\s+/g, ' ')
      .trim();

    if (pretty === 'thread pool') return 'thread pool pressure';
    if (pretty === 'gc handle') return 'GC handle retention';
    if (pretty === 'connection pool') return 'connection pool pressure';
    return pretty;
  }

  if (topActions.length) {
    const sec = el('section', 'section-card action-queue-card');
    sec.id = 'top-actions';
    sec.setAttribute('data-component-id', 'top-actions');
    sec.setAttribute('aria-label', 'Top actions');
    const queueLegacyAnchor = el('span', 'section-anchor-legacy');
    queueLegacyAnchor.id = 'sec-action-queue';
    queueLegacyAnchor.setAttribute('aria-hidden', 'true');
    sec.appendChild(queueLegacyAnchor);

    const h2 = document.createElement('h2');
    h2.textContent = 'Action Queue';
    sec.appendChild(h2);

    const subtitle = el('p', 'action-queue-card__subtitle');
    const modelVersion = summary.actionScoringModelVersion ? String(summary.actionScoringModelVersion) : 'n/a';
    subtitle.textContent = 'Deterministic ranked actions (model ' + modelVersion + ').';
    sec.appendChild(subtitle);

    const lanesHost = el('div', 'action-triage-lanes');
    const laneDefs = [
      { key: 'now', label: 'Now', maxPriority: 3 },
      { key: 'next', label: 'Next', maxPriority: 7 },
      { key: 'watch', label: 'Watch', maxPriority: Number.MAX_SAFE_INTEGER }
    ];

    function laneKeyForPriority(priority) {
      if (priority <= 3) return 'now';
      if (priority <= 7) return 'next';
      return 'watch';
    }

    // Pre-count items per lane so the collapsed Watch title shows the count.
    const laneCounts = { now: 0, next: 0, watch: 0 };
    const laneRows = Math.min(topActions.length, 20);
    for (let i = 0; i < laneRows; i++) {
      const p = Number((topActions[i] || {}).priority || (i + 1));
      laneCounts[laneKeyForPriority(p)]++;
    }

    const laneBodies = {};
    for (let li = 0; li < laneDefs.length; li++) {
      const lane = laneDefs[li];
      const count = laneCounts[lane.key] || 0;
      const laneEl = document.createElement('details');
      laneEl.className = 'action-triage-lane action-triage-lane--' + lane.key;
      laneEl.open = lane.key !== 'watch';

      const laneTitle = el('summary', 'action-triage-lane__title');
      const labelSpan = el('span', 'action-triage-lane__label');
      labelSpan.textContent = lane.label;
      const countChip = el('span', 'action-triage-lane__count action-triage-lane__count--' + lane.key);
      countChip.textContent = String(count);
      countChip.setAttribute('aria-label', count + ' items');
      laneTitle.appendChild(labelSpan);
      laneTitle.appendChild(countChip);
      laneEl.appendChild(laneTitle);

      const laneBody = el('div', 'action-triage-lane__body');
      laneEl.appendChild(laneBody);
      lanesHost.appendChild(laneEl);
      laneBodies[lane.key] = laneBody;
    }

    for (let i = 0; i < laneRows; i++) {
      const action = topActions[i] || {};
      const priority = Number(action.priority || (i + 1));
      const laneKey = laneKeyForPriority(priority);
      const body = laneBodies[laneKey];
      if (!body) continue;

      const card = el('article', 'action-triage-card action-triage-card--' + laneKey);
      const header = el('div', 'action-triage-card__header');
      const priorityBadge = el('span', 'action-triage-card__priority');
      priorityBadge.textContent = 'P' + priority;
      priorityBadge.setAttribute('aria-label', 'Priority ' + priority);
      header.appendChild(priorityBadge);

      const composite = parseCompositeScore(action);
      if (composite) {
        const scoreBadge = el('span', 'action-triage-card__score-badge');
        scoreBadge.textContent = 'Score ' + composite.score;
        const tooltip = composite.breakdown
          ? 'Composite score breakdown: ' + composite.breakdown
          : 'Composite score: ' + composite.score;
        scoreBadge.title = tooltip;
        scoreBadge.setAttribute('aria-label', tooltip);
        header.appendChild(scoreBadge);
      }
      card.appendChild(header);

      const titleLink = document.createElement('a');
      const targetAnchor = resolveFindingAnchor({
        id: action.findingFingerprint,
        analyzer: action.analyzer,
        title: action.title,
        severity: 'Warning'
      }, 'lane-' + i);
      titleLink.className = 'action-triage-card__title incident-promote-link';
      titleLink.href = targetAnchor;
      titleLink.setAttribute('data-promote-target', targetAnchor);
      titleLink.textContent = String(action.title || ('Finding ' + priority));
      card.appendChild(titleLink);

      const actions = el('div', 'action-triage-card__actions');
      actions.appendChild(buildTicketMenu(action, priority));
      card.appendChild(actions);

      body.appendChild(card);
    }

    sec.appendChild(lanesHost);

    // Correlation events are rendered centrally in the T3 section via the compact timeline lane.

    return sec;
  }

  if (!findings.length) return null;

  function sevWeight(sev) {
    const s = String(sev || '').toLowerCase();
    if (s === 'critical') return 3;
    if (s === 'warning') return 2;
    return 1;
  }

  const actionable = findings.filter(function (f) {
    return !!(f.recommendation || (Array.isArray(f.details) && f.details.length));
  }).sort(function (a, b) {
    const sevCmp = sevWeight(b.severity) - sevWeight(a.severity);
    if (sevCmp !== 0) return sevCmp;
    const confA = Number(a.confidence || 0);
    const confB = Number(b.confidence || 0);
    if (confB !== confA) return confB - confA;
    return String(a.title || '').localeCompare(String(b.title || ''));
  });

  if (!actionable.length) return null;

  const sec = el('section', 'section-card action-queue-card');
  sec.id = 'top-actions';
  sec.setAttribute('data-component-id', 'top-actions');
  sec.setAttribute('aria-label', 'Top actions');
  const queueLegacyAnchor = el('span', 'section-anchor-legacy');
  queueLegacyAnchor.id = 'sec-action-queue';
  queueLegacyAnchor.setAttribute('aria-hidden', 'true');
  sec.appendChild(queueLegacyAnchor);
  const h2 = document.createElement('h2');
  h2.textContent = 'Action Queue';
  sec.appendChild(h2);

  const subtitle = el('p', 'action-queue-card__subtitle');
  subtitle.textContent = 'Prioritized workflow view from high-impact findings.';
  sec.appendChild(subtitle);

  const tbl = el('table');
  tbl.dataset.responsiveStack = '1';
  const thead = el('thead');
  const htr = el('tr');
  ['Priority', 'Finding', 'Validation'].forEach(function (col) {
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
    anchor.href = resolveFindingAnchor(finding, 'queue-' + i);
    anchor.textContent = finding.title || ('Finding ' + (i + 1));
    tdFinding.appendChild(anchor);
    const recText = finding.recommendation || '';
    if (recText) {
      const note = el('div', 'action-queue-card__note');
      note.textContent = recText;
      tdFinding.appendChild(note);
    }
    tdFinding.appendChild(buildTicketMenu(finding, i + 1));
    tr.appendChild(tdFinding);

    const tdValidation = document.createElement('td');
    tdValidation.textContent = '-';
    tdValidation.className = 'wrap';
    tr.appendChild(tdValidation);

    tbody.appendChild(tr);
  }

  tbl.appendChild(tbody);
  sec.appendChild(tbl);
  return sec;
}

// ── Forensics rail (metadata + deep-dive controls) ─────────────────────────

export function buildForensicsRailPanel(doc) {
  const ctx = doc && doc.incidentContext ? doc.incidentContext : {};
  const domains = Array.isArray(doc && doc.domains) ? doc.domains : [];

  const sec = el('section', 'section-card forensics-rail-card');
  sec.id = 'forensics-rail';
  sec.setAttribute('data-component-id', 'forensics-rail');

  const h2 = document.createElement('h2');
  h2.textContent = 'Forensics Workbench';
  sec.appendChild(h2);

  const subtitle = el('p', 'forensics-rail-card__subtitle');
  subtitle.textContent = 'Runtime context, provenance controls, and detail filters for root-cause analysis.';
  sec.appendChild(subtitle);

  const meta = el('div', 'forensics-rail-meta');
  function metaRow(label, value) {
    const row = el('div', 'forensics-rail-meta__row');
    const l = el('span', 'forensics-rail-meta__label');
    l.textContent = label;
    const v = el('span', 'forensics-rail-meta__value');
    v.textContent = value;
    row.appendChild(l);
    row.appendChild(v);
    return row;
  }

  meta.appendChild(metaRow('Runtime', String(ctx.runtimeVersion || 'n/a')));
  meta.appendChild(metaRow('GC Mode', String(ctx.gcMode || 'n/a')));
  meta.appendChild(metaRow('Flavor', String(ctx.runtimeFlavor || 'n/a')));
  meta.appendChild(metaRow('Active Analyzers', String(ctx.activeAnalyzerCount || 0)));
  sec.appendChild(meta);

  const controls = el('div', 'forensics-rail-controls');

  const lockBtn = document.createElement('button');
  lockBtn.type = 'button';
  lockBtn.id = 'forensics-lock-open-toggle';
  lockBtn.className = 'action-btn';
  lockBtn.textContent = 'Lock Open';
  lockBtn.setAttribute('aria-pressed', 'false');
  controls.appendChild(lockBtn);

  const scopeLabel = el('label', 'forensics-rail-controls__label');
  scopeLabel.setAttribute('for', 'forensics-domain-scope');
  scopeLabel.textContent = 'Domain';
  controls.appendChild(scopeLabel);

  const scope = document.createElement('select');
  scope.id = 'forensics-domain-scope';
  scope.className = 'forensics-rail-controls__select';
  const optAll = document.createElement('option');
  optAll.value = 'all';
  optAll.textContent = 'All domains';
  scope.appendChild(optAll);
  for (let i = 0; i < domains.length; i++) {
    const d = domains[i] || {};
    const opt = document.createElement('option');
    opt.value = 'domain-' + String((d.domain || 'domain-' + i)).toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
    opt.textContent = String(d.domain || ('Domain ' + (i + 1)));
    scope.appendChild(opt);
  }
  controls.appendChild(scope);

  const search = document.createElement('input');
  search.type = 'search';
  search.id = 'forensics-domain-search';
  search.className = 'forensics-rail-controls__search';
  search.placeholder = 'Filter details in selected domain...';
  controls.appendChild(search);

  const sortLabel = el('label', 'forensics-rail-controls__label');
  sortLabel.setAttribute('for', 'forensics-sort-mode');
  sortLabel.textContent = 'Sort';
  controls.appendChild(sortLabel);

  const sort = document.createElement('select');
  sort.id = 'forensics-sort-mode';
  sort.className = 'forensics-rail-controls__select';
  const sortDefault = document.createElement('option');
  sortDefault.value = 'default';
  sortDefault.textContent = 'Default order';
  sort.appendChild(sortDefault);
  const sortProv = document.createElement('option');
  sortProv.value = 'provenance';
  sortProv.textContent = 'Provenance-first';
  sort.appendChild(sortProv);
  controls.appendChild(sort);

  const lowConfWrap = el('label', 'forensics-rail-controls__check');
  const lowConf = document.createElement('input');
  lowConf.type = 'checkbox';
  lowConf.id = 'forensics-low-confidence-only';
  lowConfWrap.appendChild(lowConf);
  const lowConfText = document.createElement('span');
  lowConfText.textContent = 'Show low-confidence only';
  lowConfWrap.appendChild(lowConfText);
  controls.appendChild(lowConfWrap);

  sec.appendChild(controls);
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
  search.setAttribute('aria-label', 'Search findings by title or details');
  bar.appendChild(search);
  const count = el('span', 'filter-count'); count.id = 'filter-count';
  count.setAttribute('aria-live', 'polite'); count.setAttribute('aria-atomic', 'true');
  bar.appendChild(count);
  return bar;
}
