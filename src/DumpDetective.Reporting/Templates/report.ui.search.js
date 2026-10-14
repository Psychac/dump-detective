export function setupGlobalSearch(announce) {
  const input = document.getElementById('global-search-input');
  const count = document.getElementById('global-search-count');
  const prev = document.getElementById('global-search-prev');
  const next = document.getElementById('global-search-next');
  const clear = document.getElementById('global-search-clear');
  const searchAll = document.getElementById('global-search-all');
  if (!input || !count || !prev || !next || !clear) return;

  let matches = [];
  let activeIndex = -1;

  // Sections whose content has not been loaded yet cannot be searched; say how many, rather than reporting a result that may be incomplete.
  function pendingSections() {
    return document.querySelectorAll('.analyzer-section[data-lazy-body="pending"]').length;
  }

  function setCountText(text) {
    const pending = pendingSections();
    if (searchAll) searchAll.disabled = pending === 0;
    if (!text) { count.textContent = ''; return; }
    count.textContent = pending
      ? text + ' \u00b7 ' + pending + ' of ' + document.querySelectorAll('.analyzer-section').length + ' sections not opened'
      : text;
  }

  // A section matches on its rendered text, or on any row of a large table in it, since only one page of those rows is in the page.
  function sectionMatches(node, query) {
    if ((node.textContent || '').toLowerCase().includes(query)) return true;
    const tables = node.querySelectorAll('table');
    for (let i = 0; i < tables.length; i++) {
      if (typeof tables[i].__hydrateRows === 'function' && tables[i].dataset.hydrated !== '1') tables[i].__hydrateRows();
      if (pagedTableMatches(tables[i], query)) return true;
    }
    return false;
  }

  function setActive(index) {
    if (!matches.length) {
      activeIndex = -1;
      return;
    }

    matches.forEach(function (node) { node.classList.remove('global-search-match--active'); });
    activeIndex = ((index % matches.length) + matches.length) % matches.length;
    const target = matches[activeIndex];
    target.classList.add('global-search-match--active');
    target.scrollIntoView({ behavior: 'smooth', block: 'center' });
    if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1');
    try { target.focus({ preventScroll: true }); } catch (e) { }
    setCountText((activeIndex + 1) + ' / ' + matches.length + ' matches');
    if (announce) announce('Search result ' + (activeIndex + 1) + ' of ' + matches.length);
  }

  function applyGlobalSearch() {
    const query = input.value.trim().toLowerCase();
    const nodes = Array.from(document.querySelectorAll('#main .section-card, #main .analyzer-section'));

    nodes.forEach(function (node) {
      node.classList.remove('global-search-match');
      node.classList.remove('global-search-match--active');
    });

    if (!query) {
      matches = [];
      activeIndex = -1;
      setCountText('');
      prev.disabled = true;
      next.disabled = true;
      return;
    }

    matches = nodes.filter(function (node) {
      if (node.hidden) return false;
      if (!sectionMatches(node, query)) return false;
      node.classList.add('global-search-match');
      return true;
    });

    prev.disabled = matches.length === 0;
    next.disabled = matches.length === 0;

    if (!matches.length) {
      setCountText('No matches');
      if (announce) announce('No matches found');
      return;
    }

    setActive(0);
  }

  input.addEventListener('input', applyGlobalSearch);
  input.addEventListener('keydown', function (ev) {
    if (ev.key === 'Enter') {
      ev.preventDefault();
      if (!matches.length) return;
      if (ev.shiftKey) setActive(activeIndex - 1);
      else setActive(activeIndex + 1);
    }

    if (ev.key === 'Escape') {
      ev.preventDefault();
      input.value = '';
      applyGlobalSearch();
    }
  });

  prev.addEventListener('click', function () {
    if (!matches.length) return;
    setActive(activeIndex - 1);
  });

  next.addEventListener('click', function () {
    if (!matches.length) return;
    setActive(activeIndex + 1);
  });

  clear.addEventListener('click', function () {
    input.value = '';
    applyGlobalSearch();
    input.focus();
  });

  if (searchAll) {
    searchAll.addEventListener('click', async function () {
      const sections = Array.from(document.querySelectorAll('.analyzer-section[data-lazy-body="pending"]'));
      searchAll.disabled = true;
      for (let i = 0; i < sections.length; i++) {
        count.textContent = 'Loading sections\u2026 ' + (i + 1) + ' of ' + sections.length;
        if (typeof sections[i].__loadBody === 'function') await sections[i].__loadBody();
        await new Promise(function (resolve) { window.setTimeout(resolve, 0); });
      }
      applyGlobalSearch();
      if (announce) announce('All sections loaded');
    });
  }

  applyGlobalSearch();
}
