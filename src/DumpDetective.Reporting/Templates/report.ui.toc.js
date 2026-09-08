export function filterTocList(list, query) {
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

export function setupActiveTocHighlighting() {
  const links = document.querySelectorAll('.toc a'); if (!links || !links.length) return; const idToLink = {}; links.forEach(function (l) { if (l.hash) idToLink[l.hash.substring(1)] = l; });
  const obs = new IntersectionObserver(function (entries) { entries.forEach(function (ent) { if (!ent.target || !ent.target.id) return; if (ent.isIntersecting) { document.querySelectorAll('.toc a.active').forEach(function (x) { x.classList.remove('active'); }); const link = idToLink[ent.target.id]; if (link) link.classList.add('active'); } }); }, { root: null, rootMargin: '-40% 0px -40% 0px', threshold: 0 });
  // Observe: analyzer sections (stable sectionId or detail-N), domain headers, and top-level sections
  const targets = document.querySelectorAll('#main .analyzer-section, #main [id^="domain-"], #sec-header, #sec-health, #sec-exec, #sec-appendix'); targets.forEach(function (t) { obs.observe(t); });
}