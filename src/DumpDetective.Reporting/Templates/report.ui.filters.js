export function setupSeverityFilter() {
  function applyFilter() {
    const fsi = document.getElementById('filter-search');
    const fbs = document.querySelectorAll('.filter-btn[data-sev]');
    const fco = document.getElementById('filter-count');
    const txt = fsi ? fsi.value.trim().toLowerCase() : '';
    let asev = 'all';
    fbs.forEach(function (b) { if (b.classList.contains('active')) asev = b.dataset.sev; });
    const cards = document.querySelectorAll('.section-card[data-severity]');
    let vis = 0;
    cards.forEach(function (c) {
      const s = (c.dataset.severity || '').toLowerCase();
      const ok = (asev === 'all' || s === asev) && (!txt || (c.dataset.title || '').toLowerCase().includes(txt) || (c.dataset.summary || '').toLowerCase().includes(txt));
      c.hidden = !ok;
      if (ok) vis++;
    });
    if (fco) fco.textContent = cards.length ? vis + ' of ' + cards.length + ' finding(s)' : '';
  }

  document.querySelectorAll('.filter-btn[data-sev]').forEach(function (b) {
    b.addEventListener('click', function () {
      document.querySelectorAll('.filter-btn[data-sev]').forEach(function (x) {
        x.classList.remove('active');
        x.setAttribute('aria-pressed', 'false');
      });
      b.classList.add('active');
      b.setAttribute('aria-pressed', 'true');
      applyFilter();
    });
  });

  const fsi = document.getElementById('filter-search');
  if (fsi) fsi.addEventListener('input', applyFilter);
  applyFilter();
}

export function setupT3RegressionFilter(doc) {
  try {
    const bar = document.getElementById('t3-regression-filter');
    if (!bar || bar.dataset.bound) return;
    bar.dataset.bound = '1';
    const buttons = Array.from(bar.querySelectorAll('.t3-filter-btn'));
    function computeCounts() {
      const counts = { '': 0, 'NewRisk': 0, 'AmplifiedRisk': 0, 'VolatileRisk': 0 };
      if (doc && Array.isArray(doc.findings)) {
        for (const f of doc.findings) {
          const k = String(f && f.regressionClass || '');
          if (k && counts.hasOwnProperty(k)) counts[k]++;
          counts['']++;
        }
      } else {
        const cards = Array.from(document.querySelectorAll('.finding-card'));
        for (const c of cards) {
          const k = String(c.dataset.regressionClass || '');
          if (k && counts.hasOwnProperty(k)) counts[k]++;
          counts['']++;
        }
      }
      return counts;
    }

    function refreshButtonBadges() {
      const counts = computeCounts();
      for (const btn of buttons) {
        const f = String(btn.dataset.filter || '');
        const prev = btn.querySelector('.t3-filter-count'); if (prev) prev.remove();
        const span = document.createElement('span'); span.className = 't3-filter-count'; span.textContent = ' ' + (counts[f] || 0);
        btn.appendChild(span);
      }
    }

    function applyFilter(filter) {
      const fnorm = String(filter || '').toLowerCase();
      const cards = Array.from(document.querySelectorAll('.finding-card'));
      for (const c of cards) {
        const rc = String(c.dataset.regressionClass || '').toLowerCase();
        const show = !fnorm || rc === fnorm;
        c.hidden = !show;
      }
    }

    buttons.forEach(function (btn) {
      btn.addEventListener('click', function () {
        buttons.forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        const f = String(btn.dataset.filter || '');
        applyFilter(f);
      });
    });

    refreshButtonBadges();
  } catch (e) { /* ignore */ }
}
