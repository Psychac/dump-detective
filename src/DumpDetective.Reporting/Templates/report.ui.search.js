export function setupGlobalSearch(announce) {
  const input = document.getElementById('global-search-input');
  const count = document.getElementById('global-search-count');
  const prev = document.getElementById('global-search-prev');
  const next = document.getElementById('global-search-next');
  const clear = document.getElementById('global-search-clear');
  if (!input || !count || !prev || !next || !clear) return;

  let matches = [];
  let activeIndex = -1;

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
    count.textContent = (activeIndex + 1) + ' / ' + matches.length + ' matches';
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
      count.textContent = '';
      prev.disabled = true;
      next.disabled = true;
      return;
    }

    matches = nodes.filter(function (node) {
      if (node.hidden) return false;
      const text = (node.textContent || '').toLowerCase();
      if (!text.includes(query)) return false;
      node.classList.add('global-search-match');
      return true;
    });

    prev.disabled = matches.length === 0;
    next.disabled = matches.length === 0;

    if (!matches.length) {
      count.textContent = 'No matches';
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

  applyGlobalSearch();
}
