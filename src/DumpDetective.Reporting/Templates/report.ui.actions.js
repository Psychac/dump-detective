// Download JSON should read the same as the report did before the string pool existed
// (docs/refactor/report-payload-size-reduction-design.md, F1) — resolve pooled cells back to
// their literal values on a clone so the shared window.__REPORT__ cache is never mutated.
function resolveStringPoolForDownload(payload) {
  const report = payload && payload.report ? payload.report : payload;
  const pool = report && Array.isArray(report.strings) ? report.strings : null;
  if (!pool) return payload;
  const clone = JSON.parse(JSON.stringify(payload));
  const clonedReport = clone && clone.report ? clone.report : clone;
  function resolveCell(v, meta) {
    const isNumericColumn = !!(meta && (meta.type === 'number' || meta.type === 'bytes' || meta.format));
    if (isNumericColumn || typeof v !== 'number') return v;
    const resolved = pool[v];
    return resolved !== undefined ? resolved : v;
  }
  function resolveSections(sections) {
    if (!Array.isArray(sections)) return;
    sections.forEach(function (section) {
      if (!Array.isArray(section.compactTables)) return;
      section.compactTables.forEach(function (ct) {
        const headers = Array.isArray(ct.headers) ? ct.headers : [];
        if (!Array.isArray(ct.rows)) return;
        ct.rows.forEach(function (r) {
          const values = Array.isArray(r) ? r : (r && Array.isArray(r.values) ? r.values : null);
          if (!values) return;
          for (let i = 0; i < values.length; i++) values[i] = resolveCell(values[i], headers[i]);
        });
      });
    });
  }
  if (clonedReport) {
    if (Array.isArray(clonedReport.domains)) clonedReport.domains.forEach(function (d) { resolveSections(d.sections); });
    resolveSections(clonedReport.trendAnalyzerSections);
    delete clonedReport.strings;
  }
  return clone;
}

export function setupExportActions(announce) {
  const btnJson = document.getElementById('btn-download-json');
  if (btnJson) btnJson.addEventListener('click', function () {
    try {
      const jsonEl = document.getElementById('report-json');
      let payload = null;
      if (jsonEl && jsonEl.textContent && jsonEl.textContent.trim()) {
        try { payload = JSON.parse(jsonEl.textContent); } catch (e) { payload = window.__REPORT__ || null; }
      } else payload = window.__REPORT__ || null;
      payload = resolveStringPoolForDownload(payload);
      const json = JSON.stringify(payload, null, 2);
      const blob = new Blob([json], { type: 'application/json' });
      const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = (btnJson.dataset.filename || 'report') + '.json'; a.click(); URL.revokeObjectURL(a.href);
    } catch (e) { console.error(e); }
  });

  const btnCsv = document.getElementById('btn-export-csv');
  if (btnCsv) btnCsv.addEventListener('click', function () {
    try {
      const jsonEl = document.getElementById('report-json');
      let payload = null;
      if (jsonEl && jsonEl.textContent && jsonEl.textContent.trim()) {
        try { payload = JSON.parse(jsonEl.textContent); } catch (e) { payload = window.__REPORT__ || null; }
      } else { payload = window.__REPORT__ || null; }
      const report = (payload && payload.report) ? payload.report : payload;
      const findings = (report && Array.isArray(report.findings)) ? report.findings : [];
      if (!findings.length) { alert('No findings to export.'); return; }
      const headers = [
        'ID',
        'Id',
        'Severity',
        'Category',
        'Title',
        'Details',
        'Recommendation',
        'Analyzer',
        'Confidence',
        'Caveats',
        'Tags'
      ];
      function csvCell(v) { const s = String(v == null ? '' : v); return '"' + s.replace(/"/g, '""') + '"'; }
      const rows = [headers.map(csvCell).join(',')];
      function joinItems(items) {
        return Array.isArray(items) ? items.filter(function (x) { return !!x; }).join(' | ') : '';
      }
      findings.forEach(function (f, i) {
        rows.push([
          i + 1,
          f.id || '',
          f.severity,
          f.category,
          f.title,
          joinItems(f.details),
          f.recommendation,
          f.analyzer,
          f.confidence != null ? Number(f.confidence).toFixed(2) : '',
          joinItems(f.caveats),
          joinItems(f.tags)
        ].map(csvCell).join(','));
      });
      const csv = rows.join('\r\n');
      const blob = new Blob([csv], { type: 'text/csv' });
      const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = (btnCsv.dataset.filename || 'report') + '-findings.csv'; a.click(); URL.revokeObjectURL(a.href);
    } catch (e) { console.error(e); }
  });

  const btnPrint = document.getElementById('btn-print');
  if (btnPrint) btnPrint.addEventListener('click', function () { window.print(); });

  const btnContrast = document.getElementById('btn-toggle-contrast');
  function applyContrast(on) {
    if (on) document.body.classList.add('high-contrast'); else document.body.classList.remove('high-contrast');
    try { localStorage.setItem('dumpdetective:high-contrast', on ? '1' : '0'); } catch (e) { }
  }
  if (btnContrast) btnContrast.addEventListener('click', function () { applyContrast(!document.body.classList.contains('high-contrast')); });
  try { if (localStorage.getItem('dumpdetective:high-contrast') === '1') applyContrast(true); } catch (e) { }

  const sr = document.getElementById('clipboard-status');
  function flash(m) { if (sr) { sr.textContent = m; setTimeout(function () { sr.textContent = ''; }, 2000); } }
  document.addEventListener('click', function (e) {
    const ticketBtn = e.target.closest && e.target.closest('.ticket-copy-btn');
    if (!ticketBtn) return;
    e.preventDefault();
    e.stopPropagation();
    const payload = ticketBtn.dataset.payload || '';
    const provider = (ticketBtn.dataset.provider || 'ticket').toUpperCase();
    if (navigator.clipboard) {
      navigator.clipboard.writeText(payload).then(function () {
        flash(provider + ' ticket template copied');
        if (announce) announce(provider + ' ticket template copied');
      });
    }
  });
  document.addEventListener('click', function (e) {
    const btn = e.target.closest && e.target.closest('.copy-btn');
    if (!btn) return;
    e.preventDefault();
    e.stopPropagation();
    if (navigator.clipboard) navigator.clipboard.writeText(btn.dataset.copy || '').then(function () { flash('Copied: ' + btn.dataset.copy); });
  });
}
