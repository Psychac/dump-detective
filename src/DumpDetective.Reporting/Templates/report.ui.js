import { renderSparklines, renderCharts } from './report.renderers.js';

export function buildSidebar(tocNode, doc) {
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

  aside.classList.add('expanded');
  aside.classList.remove('collapsed', 'is-open');

  const search = document.getElementById('toc-search');
  if (search && !search.dataset.bound) {
    search.dataset.bound = '1';
    search.setAttribute('aria-label', 'Search sections');
    if (!search.getAttribute('placeholder')) search.setAttribute('placeholder', 'Search sections');
    search.setAttribute('spellcheck', 'false');
    search.addEventListener('input', function () {
      const query = search.value.trim().toLowerCase();
      const sections = aside.querySelectorAll('.toc-section > details');
      sections.forEach(function (section) {
        const summaryLink = section.querySelector(':scope > summary');
        const rootList = section.querySelector(':scope > ol');
        const selfMatch = !query || (summaryLink && summaryLink.textContent && summaryLink.textContent.toLowerCase().includes(query));
        const childMatch = rootList ? filterTocList(rootList, query) : false;
        const visible = selfMatch || childMatch;
        section.hidden = !visible;
        if (query && visible)
          section.open = true;
      });
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

function filterTocList(list, query) {
  let anyVisible = false;
  const items = Array.from(list.children).filter(function (child) { return child.tagName === 'LI'; });

  for (const item of items) {
    const link = item.firstElementChild;
    const nestedList = Array.from(item.children).find(function (child) { return child.tagName === 'OL'; }) || null;
    const selfText = link && link.textContent ? link.textContent.toLowerCase() : '';
    const selfMatch = !query || selfText.includes(query);
    const childMatch = nestedList ? filterTocList(nestedList, query) : false;
    const visible = selfMatch || childMatch;
    item.hidden = !visible;
    anyVisible = anyVisible || visible;
  }

  return anyVisible;
}

export function setupInteractivity(doc, announce) {
  // Keyboard shortcuts for paginated areas
  document.addEventListener('keydown', function (ev) {
    const active = document.activeElement;
    if (!active) return;
    try {
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
      const c = id.match(/^detail-(\d+)-collapse-(\d+)$/);
      if (c) {
        const sec = document.getElementById('detail-' + c[1]); if (sec) { const det = sec.querySelector('details'); if (det) det.open = true; }
        const collapse = document.getElementById(id); if (collapse && collapse.tagName === 'DETAILS') collapse.open = true;
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
    const targets = document.querySelectorAll('#main [id^="detail-"]'); targets.forEach(function (t) { obs.observe(t); });
  })();

  // Copy to clipboard (delegated)
  const sr = document.getElementById('clipboard-status'); function flash(m) { if (sr) { sr.textContent = m; setTimeout(function () { sr.textContent = ''; }, 2000); } }
  document.addEventListener('click', function (e) { const btn = e.target.closest && e.target.closest('.copy-btn'); if (!btn) return; e.preventDefault(); e.stopPropagation(); if (navigator.clipboard) navigator.clipboard.writeText(btn.dataset.copy || '').then(function () { flash('Copied: ' + btn.dataset.copy); }); });

  // Trend-jump handler
  document.addEventListener('click', function (e) { const a = e.target.closest && e.target.closest('.trend-jump'); if (!a) return; try { const href = a.getAttribute('href'); if (!href || !href.startsWith('#')) return; e.preventDefault(); const id = href.substring(1); const target = document.getElementById(id) || document.querySelector('[name="' + id + '"]'); if (target) { if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1'); target.focus({ preventScroll: true }); target.scrollIntoView({ behavior: 'smooth', block: 'center' }); if (announce) announce('Jumped to ' + (id.replace(/[-_]/g, ' '))); } } catch (err) { } });

  // Download JSON
  const btnJson = document.getElementById('btn-download-json'); if (btnJson) btnJson.addEventListener('click', function () { try { const jsonEl = document.getElementById('report-json'); let payload = null; if (jsonEl && jsonEl.textContent && jsonEl.textContent.trim()) { try { payload = JSON.parse(jsonEl.textContent); } catch (e) { payload = window.__REPORT__ || null; } } else payload = window.__REPORT__ || null; const json = JSON.stringify(payload, null, 2); const blob = new Blob([json], { type: 'application/json' }); const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = (btnJson.dataset.filename || 'report') + '.json'; a.click(); URL.revokeObjectURL(a.href); } catch (e) { console.error(e); } });

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
  renderCharts();
}
