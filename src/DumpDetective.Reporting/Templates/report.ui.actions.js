export function setupExportActions(announce) {
  const btnJson = document.getElementById('btn-download-json');
  if (btnJson) btnJson.addEventListener('click', function () {
    try {
      const jsonEl = document.getElementById('report-json');
      let payload = null;
      if (jsonEl && jsonEl.textContent && jsonEl.textContent.trim()) {
        try { payload = JSON.parse(jsonEl.textContent); } catch (e) { payload = window.__REPORT__ || null; }
      } else payload = window.__REPORT__ || null;
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
