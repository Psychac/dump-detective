function findAnchorTarget(id) {
  if (!id) return null;
  const direct = document.getElementById(id);
  if (direct) return direct;
  try {
    if (window.CSS && typeof window.CSS.escape === 'function') {
      const escaped = window.CSS.escape(id);
      return document.querySelector('[data-anchoralias~="' + escaped + '"]')
        || document.querySelector('[data-anchor-alias~="' + escaped + '"]');
    }
  } catch (e) { }
  return null;
}

export function runRenderIntegrityAudit() {
  const allWithIds = Array.from(document.querySelectorAll('[id]'));
  const seen = new Map();

  function refreshBrokenAnchors() {
    const brokenLinks = Array.from(document.querySelectorAll('a[data-broken-anchor]'));
    for (let i = 0; i < brokenLinks.length; i++) {
      const link = brokenLinks[i];
      const brokenId = link.getAttribute('data-broken-anchor') || '';
      if (!brokenId) continue;
      const recovered = findAnchorTarget(brokenId);
      if (!recovered) continue;

      const resolvedId = recovered.id || brokenId;
      link.setAttribute('href', '#' + resolvedId);
      link.removeAttribute('aria-disabled');
      link.removeAttribute('data-broken-anchor');
    }
  }

  const duplicates = [];
  for (let i = 0; i < allWithIds.length; i++) {
    const id = allWithIds[i].id;
    if (!id) continue;
    const next = (seen.get(id) || 0) + 1;
    seen.set(id, next);
    if (next === 2) duplicates.push(id);
  }

  const hashLinks = Array.from(document.querySelectorAll('a[href^="#"]'));
  const missing = [];
  for (let i = 0; i < hashLinks.length; i++) {
    const href = hashLinks[i].getAttribute('href') || '';
    if (!href || href === '#') continue;
    const id = decodeURIComponent(href.substring(1));
    if (!id) continue;
    const target = findAnchorTarget(id);
    if (target) {
      if (target.id && target.id !== id) {
        hashLinks[i].setAttribute('href', '#' + target.id);
      }
      continue;
    }

    if (id.indexOf('finding-') === 0) {
      const fallback = document.getElementById('sec-action-queue') || document.getElementById('sec-exec');
      if (fallback) {
        hashLinks[i].setAttribute('href', '#' + fallback.id);
        continue;
      }
    }

    missing.push({ id: id, text: String(hashLinks[i].textContent || '').trim() });
    hashLinks[i].setAttribute('aria-disabled', 'true');
    hashLinks[i].setAttribute('data-broken-anchor', id);
  }

  refreshBrokenAnchors();

  let report = document.getElementById('render-integrity-report');
  if (!report) {
    report = document.createElement('div');
    report.id = 'render-integrity-report';
    report.className = 'sr-only';
    document.body.appendChild(report);
  }

  report.dataset.duplicateIdCount = String(duplicates.length);
  const activeBrokenAnchors = document.querySelectorAll('a[data-broken-anchor]').length;
  report.dataset.brokenAnchorCount = String(activeBrokenAnchors);
  report.textContent = 'Render integrity: duplicate IDs=' + duplicates.length + ', broken anchors=' + activeBrokenAnchors + '.';

  if (duplicates.length || activeBrokenAnchors) {
    try {
      console.warn('DumpDetective render integrity issues', {
        duplicateIds: duplicates,
        brokenAnchors: missing.slice(0, 25)
      });
    } catch (e) { }
  }

  document.addEventListener('dumpdetective:sections-rendered', function () {
    refreshBrokenAnchors();
  });
  document.addEventListener('dumpdetective:domain-sections-appended', function () {
    refreshBrokenAnchors();
  });
}