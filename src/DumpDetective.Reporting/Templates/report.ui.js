import { renderSparklines } from './report.renderers.js';

export function buildSidebar(tocNode) {
  if (!tocNode) return null;

  const aside = document.getElementById('report-navbar') || document.querySelector('.report-navbar') || document.createElement('header');
  if (!aside.id) {
    aside.id = 'report-navbar';
    aside.className = 'report-navbar';
    aside.setAttribute('role', 'navigation');
    aside.setAttribute('aria-label', 'Report navigation');
  }

  const content = aside.querySelector('.report-navbar__toc') || tocNode;
  if (content && !content.classList.contains('report-navbar__toc')) content.classList.add('report-navbar__toc');

  if (content && content !== tocNode) {
    content.replaceChildren(...Array.from(tocNode.childNodes));
  }

  let quickLinks = aside.querySelector('.report-navbar__links');
  if (!quickLinks) {
    quickLinks = document.createElement('nav');
    quickLinks.className = 'report-navbar__links';
    quickLinks.setAttribute('aria-label', 'Quick links');
    quickLinks.innerHTML = '<div class="report-navbar__links-title">Quick links</div>';
    const actionLink = document.createElement('a');
    actionLink.href = '#developer-action-plan';
    actionLink.textContent = 'Developer Action Plan';
    quickLinks.appendChild(actionLink);
    aside.insertBefore(quickLinks, aside.querySelector('.report-navbar__palette') || tocNode);
  }

  aside.classList.add('expanded');
  aside.classList.remove('collapsed', 'is-open');

  const search = document.getElementById('toc-search');
  if (search && !search.dataset.bound) {
    search.dataset.bound = '1';
    search.addEventListener('input', function () {
      const query = search.value.trim().toLowerCase();
      const items = aside.querySelectorAll('.toc a');
      let visibleCount = 0;
      items.forEach(function (item) {
        const text = item.textContent ? item.textContent.toLowerCase() : '';
        const match = !query || text.includes(query);
        item.closest('li')?.toggleAttribute('hidden', !match);
        if (match) visibleCount++;
      });
      const title = aside.querySelector('.toc-title');
      if (title) title.textContent = query ? ('Sections ' + '(' + visibleCount + ')') : 'Sections';
    });
    search.addEventListener('keydown', function (ev) {
      if (ev.key === 'Escape') {
        search.value = '';
        search.dispatchEvent(new Event('input', { bubbles: true }));
        ev.stopPropagation();
      }
    });
  }

  return aside;
}

export function setupInteractivity(doc, announce) {
  // Keyboard shortcuts for paginated areas
  document.addEventListener('keydown', function (ev) {
    const active = document.activeElement;
    if (!active) return;
    try {
      if (active.closest && active.closest('.findings-paged')) {
        const container = active.closest('.findings-paged'); if (!container) return; const prev = container.querySelector('.findings-prev'); const next = container.querySelector('.findings-next'); if (ev.key === 'ArrowLeft' && prev && !prev.disabled) { prev.click(); ev.preventDefault(); } if (ev.key === 'ArrowRight' && next && !next.disabled) { next.click(); ev.preventDefault(); }
      }
      if (active.closest && active.closest('.table-with-pagination')) {
        const container = active.closest('.table-with-pagination'); if (!container) return; const prev = container.querySelector('.table-prev'); const next = container.querySelector('.table-next'); if (ev.key === 'ArrowLeft' && prev && !prev.disabled) { prev.click(); ev.preventDefault(); } if (ev.key === 'ArrowRight' && next && !next.disabled) { next.click(); ev.preventDefault(); }
      }
    } catch (e) { }
  });

  // Smooth scroll for TOC and permalinks
  document.addEventListener('click', function (e) {
    const a = e.target.closest && e.target.closest('.toc a, .permalink'); if (!a) return; const href = a.getAttribute('href'); if (!href || href.charAt(0) !== '#') return; e.preventDefault(); const id = href.substring(1);
    try {
      const tocLink = a.closest && a.closest('.toc'); if (tocLink) { const parentDet = a.closest && a.closest('details'); if (parentDet) parentDet.open = true; }
      const m = id.match(/^detail-(\d+)-heading-(\d+)$/);
      if (m) {
        const sec = document.getElementById('detail-' + m[1]); if (sec) { const det = sec.querySelector('details'); if (det) det.open = true; }
      }
    } catch (ex) { }
    const target = document.getElementById(id);
    if (target) {
      target.scrollIntoView({ behavior: 'smooth', block: 'start' });
      try { if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1'); target.focus({ preventScroll: true }); } catch (ex) { }
      try { history.replaceState(null, '', '#' + id); } catch (ex) { }
    }
  });

  // Active TOC highlighting
  (function () {
    const links = document.querySelectorAll('.toc a'); if (!links || !links.length) return; const idToLink = {}; links.forEach(function (l) { if (l.hash) idToLink[l.hash.substring(1)] = l; });
    const obs = new IntersectionObserver(function (entries) { entries.forEach(function (ent) { if (!ent.target || !ent.target.id) return; if (ent.isIntersecting) { document.querySelectorAll('.toc a.active').forEach(function (x) { x.classList.remove('active'); }); const link = idToLink[ent.target.id]; if (link) link.classList.add('active'); } }); }, { root: null, rootMargin: '-40% 0px -40% 0px', threshold: 0 });
    const targets = document.querySelectorAll('#main [id^="finding-"], #main [id^="detail-"]'); targets.forEach(function (t) { obs.observe(t); });
  })();

  // Copy to clipboard (delegated)
  const sr = document.getElementById('clipboard-status'); function flash(m) { if (sr) { sr.textContent = m; setTimeout(function () { sr.textContent = ''; }, 2000); } }
  document.addEventListener('click', function (e) { const btn = e.target.closest && e.target.closest('.copy-btn'); if (!btn) return; e.preventDefault(); e.stopPropagation(); if (navigator.clipboard) navigator.clipboard.writeText(btn.dataset.copy || '').then(function () { flash('Copied: ' + btn.dataset.copy); }); });

  // Trend-jump handler
  document.addEventListener('click', function (e) { const a = e.target.closest && e.target.closest('.trend-jump'); if (!a) return; try { const href = a.getAttribute('href'); if (!href || !href.startsWith('#')) return; e.preventDefault(); const id = href.substring(1); const target = document.getElementById(id) || document.querySelector('[name="' + id + '"]'); if (target) { if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1'); target.focus({ preventScroll: true }); target.scrollIntoView({ behavior: 'smooth', block: 'center' }); if (announce) announce('Jumped to ' + (id.replace(/[-_]/g, ' '))); } } catch (err) { } });

  // Download JSON
  const btnJson = document.getElementById('btn-download-json'); if (btnJson) btnJson.addEventListener('click', function () { try { const jsonEl = document.getElementById('report-json'); let payload = null; if (jsonEl && jsonEl.textContent && jsonEl.textContent.trim()) { try { payload = JSON.parse(jsonEl.textContent); } catch (e) { payload = window.__REPORT__ || null; } } else payload = window.__REPORT__ || null; const json = JSON.stringify(payload, null, 2); const blob = new Blob([json], { type: 'application/json' }); const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = (btnJson.dataset.filename || 'report') + '.json'; a.click(); URL.revokeObjectURL(a.href); } catch (e) { console.error(e); } });

  // Export CSV
  const btnCsv = document.getElementById('btn-export-csv'); if (btnCsv) btnCsv.addEventListener('click', function () { try { const rows = [['ID', 'Severity', 'Category', 'Title', 'Evidence', 'Recommendation']]; (doc.findings || []).forEach(function (f, i) { rows.push(['finding-' + i, f.severity || '', f.category || '', f.title || '', f.evidence || '', f.recommendation || '']); }); const csv = rows.map(function (r) { return r.map(function (c) { return '"' + (c || '').replace(/"/g, '""') + '"'; }).join(','); }).join('\r\n'); const blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' }); const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = (btnCsv.dataset.filename || 'findings') + '-findings.csv'; a.click(); URL.revokeObjectURL(a.href); } catch (e) { console.error(e); } });

  // Print
  const btnPrint = document.getElementById('btn-print'); if (btnPrint) btnPrint.addEventListener('click', function () { window.print(); });

  // High-contrast toggle
  const btnContrast = document.getElementById('btn-toggle-contrast'); function applyContrast(on) { if (on) document.body.classList.add('high-contrast'); else document.body.classList.remove('high-contrast'); try { localStorage.setItem('dumpdetective:high-contrast', on ? '1' : '0'); } catch (e) { } }
  if (btnContrast) btnContrast.addEventListener('click', function () { applyContrast(!document.body.classList.contains('high-contrast')); }); try { if (localStorage.getItem('dumpdetective:high-contrast') === '1') applyContrast(true); } catch (e) { }

  // Filter behavior
  function applyFilter() {
    const fsi = document.getElementById('filter-search'); const fbs = document.querySelectorAll('.filter-btn[data-sev]'); const fco = document.getElementById('filter-count'); const txt = fsi ? fsi.value.trim().toLowerCase() : ''; let asev = 'all'; fbs.forEach(function (b) { if (b.classList.contains('active')) asev = b.dataset.sev; }); const cards = document.querySelectorAll('.section-card[data-severity]'); let vis = 0; cards.forEach(function (c) { const s = (c.dataset.severity || '').toLowerCase(); const ok = (asev === 'all' || s === asev) && (!txt || (c.dataset.title || '').toLowerCase().includes(txt) || (c.dataset.summary || '').toLowerCase().includes(txt)); c.hidden = !ok; if (ok) vis++; }); if (fco) fco.textContent = cards.length ? vis + ' of ' + cards.length + ' finding(s)' : ''; }
  document.querySelectorAll('.filter-btn[data-sev]').forEach(function (b) { b.addEventListener('click', function () { document.querySelectorAll('.filter-btn[data-sev]').forEach(function (x) { x.classList.remove('active'); x.setAttribute('aria-pressed', 'false'); }); b.classList.add('active'); b.setAttribute('aria-pressed', 'true'); applyFilter(); }); });
  const fsi = document.getElementById('filter-search'); if (fsi) fsi.addEventListener('input', applyFilter); applyFilter();

  // Sortable tables
  document.querySelectorAll('table').forEach(function (tbl) {
    const ths = tbl.querySelectorAll('thead th'); ths.forEach(function (th, col) { th.classList.add('sortable'); th.setAttribute('tabindex', '0'); let dir = 1; function doSort() { const tb = tbl.querySelector('tbody'); if (!tb) return; const rows = Array.from(tb.querySelectorAll('tr')); rows.sort(function (a, b) { const ac = a.cells[col], bc = b.cells[col]; const av = ac && ac.dataset.value !== undefined && ac.dataset.value !== '' ? parseFloat(ac.dataset.value) : NaN; const bv = bc && bc.dataset.value !== undefined && bc.dataset.value !== '' ? parseFloat(bc.dataset.value) : NaN; if (!isNaN(av) && !isNaN(bv)) return dir * (av - bv); const at = (ac ? ac.textContent : '').toLowerCase(); const bt = (bc ? bc.textContent : '').toLowerCase(); return dir * (at < bt ? -1 : at > bt ? 1 : 0); }); rows.forEach(function (r) { tb.appendChild(r); }); ths.forEach(function (h) { h.removeAttribute('aria-sort'); }); th.setAttribute('aria-sort', dir > 0 ? 'ascending' : 'descending'); dir = -dir; }
      th.addEventListener('click', doSort); th.addEventListener('keydown', function (e) { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); doSort(); } });
    });
  });

  // Initial sparkline rendering
  renderSparklines();
}
