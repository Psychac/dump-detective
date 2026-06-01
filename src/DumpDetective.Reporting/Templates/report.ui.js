import { renderSparklines, renderCharts } from './report.renderers.js';
import { filterTocList } from './report.ui.toc.js';
import { runRenderIntegrityAudit } from './report.ui.integrity.js';

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

export function setupInteractivity(doc, announce) {
  const styleVersion = String((doc && doc.reportStyleVersion) || 'v1').toLowerCase();
  const isV2 = styleVersion.startsWith('v2');
  const READING_MODE_KEY = 'dumpdetective:reading-mode';
  let activeReadingMode = 'incident';
  let forensicsLockOpen = false;

  function syncCollapsibleAria(container) {
    const root = container || document;
    root.querySelectorAll('details').forEach(function (d) {
      const s = d.querySelector(':scope > summary');
      if (!s) return;
      s.setAttribute('aria-expanded', String(!!d.open));
      if (!s.getAttribute('aria-label')) {
        const txt = String((s.textContent || '').trim() || 'section');
        s.setAttribute('aria-label', 'Toggle ' + txt);
      }
      if (!s.dataset.ariaBound) {
        s.dataset.ariaBound = '1';
        d.addEventListener('toggle', function () {
          s.setAttribute('aria-expanded', String(!!d.open));
        });
      }
    });
  }

  function collectModeTargets() {
    const forensics = Array.from(document.querySelectorAll('.analyzer-section, .provenance, #sec-appendix, #report-domains .domain-body, #forensics-rail'));
    const incidentOnly = Array.from(document.querySelectorAll('.incident-ribbon, .escalation-packet'));
    const domainDetails = Array.from(document.querySelectorAll('.report-domain__details'));
    const analyzerDetails = Array.from(document.querySelectorAll('.analyzer-section > details:not(.provenance)'));
    const provenanceDetails = Array.from(document.querySelectorAll('.analyzer-section > details.provenance'));
    const appendixPanels = Array.from(document.querySelectorAll('#sec-appendix .appendix-panel'));
    return {
      forensics: forensics,
      incidentOnly: incidentOnly,
      domainDetails: domainDetails,
      analyzerDetails: analyzerDetails,
      provenanceDetails: provenanceDetails,
      appendixPanels: appendixPanels
    };
  }

  function shouldOpenInIncident(detailsNode) {
    if (!detailsNode) return false;
    const host = detailsNode.closest && detailsNode.closest('.report-domain');
    if (!host) return false;
    const sev = String(host.dataset.leadSeverity || '').toLowerCase();
    return sev === 'critical';
  }

  function materializeForensicsSections() {
    let guard = 0;
    while (guard < 32) {
      const loadMoreButtons = Array.from(document.querySelectorAll('.domain-load-more'));
      if (!loadMoreButtons.length) break;
      for (let i = 0; i < loadMoreButtons.length; i++) {
        const btn = loadMoreButtons[i];
        if (!btn || btn.disabled) continue;
        btn.click();
      }
      guard++;
    }
  }

  function setToggleLabel(mode) {
    const btn = document.getElementById('reading-mode-toggle');
    if (!btn) return;
    const normalized = mode === 'forensics' ? 'forensics' : 'incident';
    const isIncident = normalized === 'incident';
    btn.textContent = isIncident ? '\u25CC\u202FMode: Incident' : '\u25CC\u202FMode: Forensics';
    btn.setAttribute('aria-label', 'Switch reading mode (currently ' + normalized + ')');
    btn.setAttribute('aria-pressed', isIncident ? 'false' : 'true');
    btn.setAttribute('aria-controls', 'main');
    btn.dataset.mode = normalized;
    btn.id = 'reading-mode-toggle';
  }

  function applyReadingMode(mode, options) {
    const opts = options || {};
    const normalized = mode === 'forensics' ? 'forensics' : 'incident';
    activeReadingMode = normalized;
    if (normalized === 'forensics') {
      materializeForensicsSections();
    }
    document.body.dataset.readingMode = normalized;
    document.body.classList.toggle('reading-mode-incident', normalized === 'incident');
    document.body.classList.toggle('reading-mode-forensics', normalized === 'forensics');
    const targets = collectModeTargets();
    const hideForIncident = normalized === 'incident';
    for (let i = 0; i < targets.forensics.length; i++) {
      const node = targets.forensics[i];
      if (!node) continue;
      node.hidden = hideForIncident;
    }

    for (let i = 0; i < targets.incidentOnly.length; i++) {
      const node = targets.incidentOnly[i];
      if (!node) continue;
      node.hidden = normalized === 'forensics';
    }

    for (let i = 0; i < targets.domainDetails.length; i++) {
      const detailsNode = targets.domainDetails[i];
      if (!detailsNode) continue;
      if (normalized === 'forensics') {
        detailsNode.open = true;
      } else {
        detailsNode.open = shouldOpenInIncident(detailsNode);
      }
    }

    for (let i = 0; i < targets.analyzerDetails.length; i++) {
      const detailsNode = targets.analyzerDetails[i];
      if (!detailsNode) continue;
      detailsNode.open = normalized === 'forensics' ? forensicsLockOpen : false;
    }

    for (let i = 0; i < targets.provenanceDetails.length; i++) {
      const detailsNode = targets.provenanceDetails[i];
      if (!detailsNode) continue;
      detailsNode.open = normalized === 'forensics' ? forensicsLockOpen : false;
    }

    for (let i = 0; i < targets.appendixPanels.length; i++) {
      const panel = targets.appendixPanels[i];
      if (!panel) continue;
      panel.open = normalized === 'forensics' ? forensicsLockOpen : false;
    }

    // Defer one more pass to cover asynchronously appended analyzer nodes.
    window.requestAnimationFrame(function () {
      const deferredTargets = collectModeTargets();
      for (let i = 0; i < deferredTargets.domainDetails.length; i++) {
        const detailsNode = deferredTargets.domainDetails[i];
        if (!detailsNode) continue;
        if (normalized === 'forensics') {
          detailsNode.open = true;
        } else {
          detailsNode.open = shouldOpenInIncident(detailsNode);
        }
      }
      for (let i = 0; i < deferredTargets.analyzerDetails.length; i++) {
        const detailsNode = deferredTargets.analyzerDetails[i];
        if (!detailsNode) continue;
        detailsNode.open = normalized === 'forensics' ? forensicsLockOpen : false;
      }
      for (let i = 0; i < deferredTargets.provenanceDetails.length; i++) {
        const detailsNode = deferredTargets.provenanceDetails[i];
        if (!detailsNode) continue;
        detailsNode.open = normalized === 'forensics' ? forensicsLockOpen : false;
      }
    });

    applyForensicsControls();

    setToggleLabel(normalized);
    if (!opts.silent && announce) announce('Reading mode: ' + normalized);
    if (!opts.skipPersist) {
      try { localStorage.setItem(READING_MODE_KEY, normalized); } catch (e) { }
    }
  }

  function syncReadingModeForDynamicContent() {
    syncCollapsibleAria(document);
    applyReadingMode(activeReadingMode, { silent: true, skipPersist: true });
    setupForensicsControls();
  }

  (function initReadingMode() {
    let initial = 'incident';
    try {
      const stored = localStorage.getItem(READING_MODE_KEY);
      if (stored === 'forensics' || stored === 'incident') initial = stored;
    } catch (e) { }
    applyReadingMode(initial, { silent: true });
    const btn = document.getElementById('reading-mode-toggle');
    if (!btn || btn.dataset.bound) return;
    btn.dataset.bound = '1';
    btn.addEventListener('click', function () {
      applyReadingMode(activeReadingMode === 'incident' ? 'forensics' : 'incident');
    });
    setupForensicsControls();
  })();

  document.addEventListener('dumpdetective:sections-rendered', function () {
    window.setTimeout(syncReadingModeForDynamicContent, 0);
  });
  document.addEventListener('dumpdetective:domain-sections-appended', function () {
    window.setTimeout(syncReadingModeForDynamicContent, 0);
  });

  // Setup T3 regression filter wiring
  function setupT3RegressionFilter() {
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
          // clear previous badge
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

      // initial badge population
      refreshButtonBadges();
    } catch (e) { /* ignore */ }
  }

  document.addEventListener('dumpdetective:sections-rendered', function () { window.setTimeout(setupT3RegressionFilter, 0); });
  document.addEventListener('dumpdetective:domain-sections-appended', function () { window.setTimeout(setupT3RegressionFilter, 0); });

  runRenderIntegrityAudit();

  if (isV2) {
    const staggerTargets = Array.from(document.querySelectorAll('#sec-header, #sec-health, #sec-exec, #sec-action-queue'));
    for (let i = 0; i < staggerTargets.length; i++) {
      const node = staggerTargets[i];
      node.classList.add('summary-stagger');
      node.style.setProperty('--stagger-delay', String(i * 120) + 'ms');
    }
  }

  (function setScreenReaderSummary() {
    const sr = document.getElementById('report-sr-summary');
    if (!sr) return;
    const hs = doc && doc.healthScorecard;
    const trend = hs && hs.trend ? hs.trend : null;
    let critical = 0;
    let warning = 0;
    if (trend) {
      // reflect net change in speech-friendly form
      critical = Number(trend.netCriticalChange || 0);
      warning = Number(trend.netWarningChange || 0);
    } else {
      const domains = hs && Array.isArray(hs.domains) ? hs.domains : [];
      for (let i = 0; i < domains.length; i++) {
        const d = domains[i] || {};
        critical += Number(d.criticalCount || 0);
        warning += Number(d.warningCount || 0);
      }
    }
    const actionsNode = document.getElementById('sec-action-queue');
    const actionCount = actionsNode ? actionsNode.querySelectorAll('tbody tr').length : 0;
    if (trend) {
      sr.textContent = 'Report summary. Domains regressed: ' + (trend.domainsRegressed || 0) + '. Domains improved: ' + (trend.domainsImproved || 0) + '. Net critical change: ' + (critical >= 0 ? '+' + critical : String(critical)) + '. Top actions: ' + actionCount + '.';
    } else {
      sr.textContent = 'Report summary. Critical findings: ' + critical + '. Warning findings: ' + warning + '. Top actions: ' + actionCount + '.';
    }
  })();

  syncCollapsibleAria(document);

  function revealTargetForHash(id) {
    if (!id) return null;
    const target = document.getElementById(id);
    if (!target) return null;

    if (activeReadingMode === 'incident') {
      const requiresForensics = target.closest && target.closest('.analyzer-section, .provenance, #sec-appendix, #report-domains .domain-body');
      if (requiresForensics) {
        applyReadingMode('forensics', { silent: true });
      }
    }

    const hostSection = target.closest && target.closest('.analyzer-section');
    if (hostSection) {
      const details = hostSection.querySelector('details');
      if (details) details.open = true;
    }

    const detailParent = target.closest && target.closest('details');
    if (detailParent) detailParent.open = true;
    return target;
  }

  function getAnalyzerSections() {
    return Array.from(document.querySelectorAll('#report-domains .analyzer-section'));
  }

  function getSectionDomainId(section) {
    const host = section && section.closest ? section.closest('.report-domain') : null;
    return host ? (host.id || '') : '';
  }

  function parseDurationMs(section) {
    const raw = section && section.dataset ? section.dataset.provenanceDurationMs : null;
    if (raw === null || raw === undefined || raw === '') return Number.MAX_SAFE_INTEGER;
    const n = Number(raw);
    return Number.isFinite(n) ? n : Number.MAX_SAFE_INTEGER;
  }

  function applyForensicsControls() {
    const scopeEl = document.getElementById('forensics-domain-scope');
    const searchEl = document.getElementById('forensics-domain-search');
    const sortEl = document.getElementById('forensics-sort-mode');
    const lowConfidenceEl = document.getElementById('forensics-low-confidence-only');
    const modeActive = activeReadingMode === 'forensics';

    const scopedDomain = scopeEl ? String(scopeEl.value || 'all') : 'all';
    const query = searchEl ? String(searchEl.value || '').trim().toLowerCase() : '';
    const sortMode = sortEl ? String(sortEl.value || 'default') : 'default';
    const lowOnly = !!(lowConfidenceEl && lowConfidenceEl.checked);

    if (!modeActive) {
      return;
    }

    const sections = getAnalyzerSections();
    const byDomain = new Map();
    for (let i = 0; i < sections.length; i++) {
      const section = sections[i];
      const domainId = getSectionDomainId(section) || 'unknown';
      if (!byDomain.has(domainId)) byDomain.set(domainId, []);
      byDomain.get(domainId).push(section);
    }

    byDomain.forEach(function (domainSections, domainId) {
      if (sortMode === 'provenance') {
        domainSections.sort(function (a, b) {
          return parseDurationMs(a) - parseDurationMs(b);
        });
      }
      const escapedDomainId = (window.CSS && typeof window.CSS.escape === 'function')
        ? window.CSS.escape(domainId)
        : String(domainId).replace(/[^a-zA-Z0-9_-]/g, '');
      const domainBody = document.querySelector('#' + escapedDomainId + ' .domain-body .domain-sections');
      if (domainBody) {
        for (let i = 0; i < domainSections.length; i++) {
          domainBody.appendChild(domainSections[i]);
        }
      }
    });

    for (let i = 0; i < sections.length; i++) {
      const section = sections[i];
      const domainId = getSectionDomainId(section);
      const inScope = scopedDomain === 'all' || scopedDomain === domainId;

      let visible = true;
      if (modeActive) {
        visible = inScope;
        if (visible && query) {
          const hay = (section.textContent || '').toLowerCase();
          visible = hay.indexOf(query) >= 0;
        }
        if (visible && lowOnly) {
          const leadConfidence = Number(section.dataset.leadConfidence || '1');
          visible = Number.isFinite(leadConfidence) ? leadConfidence < 0.65 : false;
        }
      }

      section.hidden = !visible;
      if (modeActive && forensicsLockOpen && visible) {
        const details = section.querySelectorAll('details');
        for (let d = 0; d < details.length; d++) details[d].open = true;
      }
    }
  }

  function setupForensicsControls() {
    const lockBtn = document.getElementById('forensics-lock-open-toggle');
    const scopeEl = document.getElementById('forensics-domain-scope');
    const searchEl = document.getElementById('forensics-domain-search');
    const sortEl = document.getElementById('forensics-sort-mode');
    const lowConfidenceEl = document.getElementById('forensics-low-confidence-only');

    if (lockBtn && !lockBtn.dataset.bound) {
      lockBtn.dataset.bound = '1';
      lockBtn.addEventListener('click', function () {
        forensicsLockOpen = !forensicsLockOpen;
        lockBtn.setAttribute('aria-pressed', forensicsLockOpen ? 'true' : 'false');
        lockBtn.textContent = forensicsLockOpen ? 'Unlock' : 'Lock Open';
        applyForensicsControls();
      });
    }

    if (scopeEl && !scopeEl.dataset.bound) {
      scopeEl.dataset.bound = '1';
      scopeEl.addEventListener('change', applyForensicsControls);
    }
    if (searchEl && !searchEl.dataset.bound) {
      searchEl.dataset.bound = '1';
      searchEl.addEventListener('input', applyForensicsControls);
    }
    if (sortEl && !sortEl.dataset.bound) {
      sortEl.dataset.bound = '1';
      sortEl.addEventListener('change', applyForensicsControls);
    }
    if (lowConfidenceEl && !lowConfidenceEl.dataset.bound) {
      lowConfidenceEl.dataset.bound = '1';
      lowConfidenceEl.addEventListener('change', applyForensicsControls);
    }
    applyForensicsControls();
  }

  // Global report search (cross-section, cross-finding)
  (function () {
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
  })();

  // Keyboard shortcuts for pagination + critical-signal navigation
  document.addEventListener('keydown', function (ev) {
    const active = document.activeElement;
    const tag = active && active.tagName ? active.tagName.toLowerCase() : '';
    const isEditing = tag === 'input' || tag === 'textarea' || tag === 'select' || (active && active.isContentEditable);

    try {
      if (active && active.closest && active.closest('.table-with-pagination')) {
        const container = active.closest('.table-with-pagination');
        if (container) {
          const prev = container.querySelector('.table-prev');
          const next = container.querySelector('.table-next');
          if (ev.key === 'ArrowLeft' && prev && !prev.disabled) { prev.click(); ev.preventDefault(); }
          if (ev.key === 'ArrowRight' && next && !next.disabled) { next.click(); ev.preventDefault(); }
        }
      }
    } catch (e) { }

    if (isEditing) return;

    // Shift+N: jump to next critical signal card/section.
    if (ev.shiftKey && !ev.ctrlKey && !ev.altKey && String(ev.key || '').toLowerCase() === 'n') {
      const criticalNodes = Array.from(document.querySelectorAll(
        '.analyzer-section[data-lead-severity="critical"]:not([hidden]), .health-domain-tile--critical:not([hidden])'
      ));
      if (criticalNodes.length) {
        const currentY = window.scrollY;
        let target = criticalNodes[0];
        for (let i = 0; i < criticalNodes.length; i++) {
          const rect = criticalNodes[i].getBoundingClientRect();
          const top = rect.top + window.scrollY;
          if (top > currentY + 12) {
            target = criticalNodes[i];
            break;
          }
        }
        target.scrollIntoView({ behavior: 'smooth', block: 'start' });
        try {
          if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1');
          target.focus({ preventScroll: true });
        } catch (e) { }
        if (announce) announce('Jumped to next critical signal');
      }
      ev.preventDefault();
      return;
    }

    // Shift+A: jump to action queue.
    if (ev.shiftKey && !ev.ctrlKey && !ev.altKey && String(ev.key || '').toLowerCase() === 'a') {
      const queue = document.getElementById('sec-action-queue');
      if (queue) {
        queue.scrollIntoView({ behavior: 'smooth', block: 'start' });
        try {
          if (!queue.hasAttribute('tabindex')) queue.setAttribute('tabindex', '-1');
          queue.focus({ preventScroll: true });
        } catch (e) { }
        if (announce) announce('Jumped to action queue');
      }
      ev.preventDefault();
      return;
    }
  });

  // Smooth scroll for TOC and permalinks
  document.addEventListener('click', function (e) {
    const a = e.target.closest && e.target.closest('.toc a, .permalink'); if (!a) return; const href = a.getAttribute('href'); if (!href || href.charAt(0) !== '#') return; e.preventDefault(); const id = href.substring(1);
    try {
      const tocLink = a.closest && a.closest('.toc'); if (tocLink) { const parentDet = a.closest && a.closest('details'); if (parentDet) parentDet.open = true; }
      const m = id.match(/^detail-(.+)-heading-(\d+)$/);
      if (m) {
        const sec = document.getElementById('detail-' + m[1]); if (sec) { const det = sec.querySelector('details'); if (det) det.open = true; }
      }
      const c = id.match(/^detail-(.+)-collapse-(\d+)$/);
      if (c) {
        const sec = document.getElementById('detail-' + c[1]); if (sec) { const det = sec.querySelector('details'); if (det) det.open = true; }
        const collapse = document.getElementById(id); if (collapse && collapse.tagName === 'DETAILS') collapse.open = true;
      }
    } catch (ex) { }
    const target = revealTargetForHash(id);
    if (target) {
      target.scrollIntoView({ behavior: 'smooth', block: 'start' });
      if (isV2) {
        target.classList.remove('anchor-flash');
        void target.offsetWidth;
        target.classList.add('anchor-flash');
      }
      try { if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1'); target.focus({ preventScroll: true }); } catch (ex) { }
      try { history.replaceState(null, '', '#' + id); } catch (ex) { }
    }
  });

  window.addEventListener('hashchange', function () {
    const id = (location.hash || '').replace(/^#/, '');
    if (!id) return;
    const target = revealTargetForHash(id);
    if (target) target.scrollIntoView({ behavior: 'smooth', block: 'start' });
  });

  if (location.hash && location.hash.length > 1) {
    const id = location.hash.substring(1);
    const target = revealTargetForHash(id);
    if (target) {
      window.setTimeout(function () {
        target.scrollIntoView({ behavior: 'auto', block: 'start' });
      }, 0);
    }
  }

  // Health scorecard domain tiles -> domain section scroll
  document.addEventListener('click', function (e) {
    const tile = e.target.closest && e.target.closest('[data-domain-target]');
    if (!tile) return;
    const targetId = tile.getAttribute('data-domain-target');
    if (!targetId) return;
    const target = document.getElementById(targetId);
    if (!target) return;
    e.preventDefault();
    const details = target.querySelector('details.report-domain__details');
    if (details) details.open = true;
    target.scrollIntoView({ behavior: 'smooth', block: 'start' });
    try { history.replaceState(null, '', '#' + targetId); } catch (err) { }
  });

  // Incident promote actions -> Forensics deep evidence
  document.addEventListener('click', function (e) {
    const btn = e.target.closest && e.target.closest('.incident-promote-link');
    if (!btn) return;
    const rawTarget = btn.getAttribute('data-promote-target') || (btn.dataset ? btn.dataset.promoteTarget : '') || '';
    if (!rawTarget) return;

    e.preventDefault();
    e.stopPropagation();
    const targetId = rawTarget.charAt(0) === '#' ? rawTarget.substring(1) : rawTarget;
    applyReadingMode('forensics', { silent: true });
    setupForensicsControls();
    const target = revealTargetForHash(targetId);
    if (target) {
      target.scrollIntoView({ behavior: 'smooth', block: 'start' });
      try { history.replaceState(null, '', '#' + targetId); } catch (err) { }
    }
    if (announce) announce('Promoted to forensics view');
  });

  // Active TOC highlighting
  (function () {
    const links = document.querySelectorAll('.toc a'); if (!links || !links.length) return; const idToLink = {}; links.forEach(function (l) { if (l.hash) idToLink[l.hash.substring(1)] = l; });
    const obs = new IntersectionObserver(function (entries) { entries.forEach(function (ent) { if (!ent.target || !ent.target.id) return; if (ent.isIntersecting) { document.querySelectorAll('.toc a.active').forEach(function (x) { x.classList.remove('active'); }); const link = idToLink[ent.target.id]; if (link) link.classList.add('active'); } }); }, { root: null, rootMargin: '-40% 0px -40% 0px', threshold: 0 });
    // Observe: analyzer sections (stable sectionId or detail-N), domain headers, and top-level sections
    const targets = document.querySelectorAll('#main .analyzer-section, #main [id^="domain-"], #sec-header, #sec-health, #sec-exec, #sec-appendix'); targets.forEach(function (t) { obs.observe(t); });
  })();

  // Copy to clipboard (delegated)
  const sr = document.getElementById('clipboard-status'); function flash(m) { if (sr) { sr.textContent = m; setTimeout(function () { sr.textContent = ''; }, 2000); } }
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
  document.addEventListener('click', function (e) { const btn = e.target.closest && e.target.closest('.copy-btn'); if (!btn) return; e.preventDefault(); e.stopPropagation(); if (navigator.clipboard) navigator.clipboard.writeText(btn.dataset.copy || '').then(function () { flash('Copied: ' + btn.dataset.copy); }); });

  // Trend-jump handler
  document.addEventListener('click', function (e) { const a = e.target.closest && e.target.closest('.trend-jump'); if (!a) return; try { const href = a.getAttribute('href'); if (!href || !href.startsWith('#')) return; e.preventDefault(); const id = href.substring(1); const target = document.getElementById(id) || document.querySelector('[name="' + id + '"]'); if (target) { if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1'); target.focus({ preventScroll: true }); target.scrollIntoView({ behavior: 'smooth', block: 'center' }); if (announce) announce('Jumped to ' + (id.replace(/[-_]/g, ' '))); } } catch (err) { } });

  // Download JSON
  const btnJson = document.getElementById('btn-download-json'); if (btnJson) btnJson.addEventListener('click', function () { try { const jsonEl = document.getElementById('report-json'); let payload = null; if (jsonEl && jsonEl.textContent && jsonEl.textContent.trim()) { try { payload = JSON.parse(jsonEl.textContent); } catch (e) { payload = window.__REPORT__ || null; } } else payload = window.__REPORT__ || null; const json = JSON.stringify(payload, null, 2); const blob = new Blob([json], { type: 'application/json' }); const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = (btnJson.dataset.filename || 'report') + '.json'; a.click(); URL.revokeObjectURL(a.href); } catch (e) { console.error(e); } });

  // Export CSV
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
        'Fingerprint',
        'Severity',
        'Category',
        'Title',
        'Evidence',
        'EvidenceItems',
        'Recommendation',
        'RecommendationItems',
        'Fix',
        'Analyzer',
        'Confidence',
        'Owner',
        'Effort',
        'Status',
        'Validation',
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
          f.fingerprint || '',
          f.severity,
          f.category,
          f.title,
          f.evidence,
          joinItems(f.evidenceItems),
          f.recommendation,
          joinItems(f.recommendationItems),
          f.fix,
          f.analyzer,
          f.confidenceScore != null ? Number(f.confidenceScore).toFixed(2) : '',
          f.suggestedOwner,
          f.effort,
          f.trackingStatus,
          f.validationStep,
          joinItems(f.tags)
        ].map(csvCell).join(','));
      });
      const csv = rows.join('\r\n');
      const blob = new Blob([csv], { type: 'text/csv' });
      const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = (btnCsv.dataset.filename || 'report') + '-findings.csv'; a.click(); URL.revokeObjectURL(a.href);
    } catch (e) { console.error(e); }
  });

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

  // Detail table controls: filter + show all/limited
  function applyTableCellClamp(scope) {
    const root = scope || document;
    const cells = root.querySelectorAll('td');
    for (let i = 0; i < cells.length; i++) {
      const td = cells[i];
      if (!td || td.dataset.clampReady === '1') continue;
      const text = String(td.textContent || '').trim();
      if (!text || text.length < 140) {
        td.dataset.clampReady = '1';
        continue;
      }
      if (td.querySelector('a, button, input, select, textarea')) {
        td.dataset.clampReady = '1';
        continue;
      }

      td.textContent = '';
      const content = document.createElement('span');
      content.className = 'table-cell-clamp__text is-clamped';
      content.textContent = text;
      td.appendChild(content);

      const toggle = document.createElement('button');
      toggle.type = 'button';
      toggle.className = 'table-cell-clamp__toggle';
      toggle.textContent = 'Expand';
      toggle.setAttribute('aria-expanded', 'false');
      toggle.addEventListener('click', function () {
        const expanded = content.classList.toggle('is-clamped');
        const isCollapsed = expanded;
        toggle.textContent = isCollapsed ? 'Expand' : 'Collapse';
        toggle.setAttribute('aria-expanded', isCollapsed ? 'false' : 'true');
      });
      td.appendChild(toggle);
      td.dataset.clampReady = '1';
    }
  }

  function applyManagedTableState(tbl) {
    if (!tbl) return;
    const limit = Number(tbl.dataset.limit || '0');
    const showAll = tbl.dataset.showAll === '1';
    const input = document.querySelector('.table-filter-input[data-target-table="' + tbl.id + '"]');
    const query = input ? input.value.trim().toLowerCase() : '';
    const rows = Array.from(tbl.querySelectorAll('tbody tr'));
    let matched = 0;
    let visible = 0;
    for (let i = 0; i < rows.length; i++) {
      const row = rows[i];
      const text = (row.textContent || '').toLowerCase();
      const isMatch = !query || text.includes(query);
      if (!isMatch) {
        row.hidden = true;
        continue;
      }
      matched++;
      if (!showAll && limit > 0 && matched > limit) {
        row.hidden = true;
      } else {
        row.hidden = false;
        visible++;
      }
    }

    const count = document.querySelector('[data-target-table-count="' + tbl.id + '"]');
    if (count) {
      count.textContent = query ? (visible + ' of ' + matched + ' matching rows') : (visible + ' rows shown');
    }

    const btn = document.querySelector('.table-show-all-btn[data-target-table="' + tbl.id + '"]');
    if (btn) {
      if (showAll) {
        btn.textContent = 'Show top ' + limit + ' rows';
      } else {
        const labelCount = query ? matched : rows.length;
        btn.textContent = 'Show all ' + labelCount + ' rows';
      }
      btn.disabled = limit <= 0 || matched <= limit;
    }

    applyTableCellClamp(tbl);
  }

  document.querySelectorAll('table.detail-filterable-table').forEach(function (tbl) {
    tbl.__applyManagedState = function () { applyManagedTableState(tbl); };
    applyManagedTableState(tbl);
  });

  applyTableCellClamp(document);

  document.querySelectorAll('.table-filter-input[data-target-table]').forEach(function (input) {
    input.addEventListener('input', function () {
      const tableId = input.getAttribute('data-target-table');
      const tbl = tableId ? document.getElementById(tableId) : null;
      applyManagedTableState(tbl);
    });
  });

  document.querySelectorAll('.table-show-all-btn[data-target-table]').forEach(function (btn) {
    btn.addEventListener('click', function () {
      const tableId = btn.getAttribute('data-target-table');
      const tbl = tableId ? document.getElementById(tableId) : null;
      if (!tbl) return;
      tbl.dataset.showAll = tbl.dataset.showAll === '1' ? '0' : '1';
      applyManagedTableState(tbl);
    });
  });

  // Sortable tables
  document.querySelectorAll('table').forEach(function (tbl) {
    const parseSortableNumber = function (cell) {
      if (!cell) return NaN;

      const raw = cell.dataset && cell.dataset.value;
      if (raw !== undefined && raw !== null && raw !== '') {
        const n = Number(String(raw).replace(/,/g, '').trim());
        if (!Number.isNaN(n)) return n;
      }

      const text = (cell.textContent || '').trim();

      // Parse byte values like "1.2 GB", "850 KB", or "42 B".
      const bytesMatch = text.match(/^([+-]?\d[\d,]*(?:\.\d+)?)\s*(B|KB|MB|GB|TB|PB|EB)$/i);
      if (bytesMatch) {
        const value = Number(bytesMatch[1].replace(/,/g, ''));
        if (!Number.isNaN(value)) {
          const unit = bytesMatch[2].toUpperCase();
          const power = unit === 'B' ? 0 :
            unit === 'KB' ? 1 :
            unit === 'MB' ? 2 :
            unit === 'GB' ? 3 :
            unit === 'TB' ? 4 :
            unit === 'PB' ? 5 : 6;
          return value * Math.pow(1024, power);
        }
      }

      // Parse plain numeric text like "12,345", "-10", "42.5", or "87%".
      if (/^[+-]?\d[\d,]*(?:\.\d+)?%?$/.test(text)) {
        const n = Number(text.replace(/,/g, '').replace(/%$/, ''));
        if (!Number.isNaN(n)) return n;
      }

      return NaN;
    };

    const ths = tbl.querySelectorAll('thead th'); ths.forEach(function (th, col) { th.classList.add('sortable'); th.setAttribute('tabindex', '0'); let dir = 0; function doSort() { const tb = tbl.querySelector('tbody'); if (!tb) return; const rows = Array.from(tb.querySelectorAll('tr')); if (dir === 0) { let numericColumn = false; for (let i = 0; i < rows.length; i++) { const n = parseSortableNumber(rows[i].cells[col]); if (!isNaN(n)) { numericColumn = true; break; } } dir = numericColumn ? -1 : 1; } rows.sort(function (a, b) { const ac = a.cells[col], bc = b.cells[col]; const av = parseSortableNumber(ac); const bv = parseSortableNumber(bc); if (!isNaN(av) && !isNaN(bv)) return dir * (av - bv); const at = (ac ? ac.textContent : '').toLowerCase(); const bt = (bc ? bc.textContent : '').toLowerCase(); return dir * (at < bt ? -1 : at > bt ? 1 : 0); }); rows.forEach(function (r) { tb.appendChild(r); }); if (typeof tbl.__applyManagedState === 'function') tbl.__applyManagedState(); ths.forEach(function (h) { h.removeAttribute('aria-sort'); }); th.setAttribute('aria-sort', dir > 0 ? 'ascending' : 'descending'); dir = -dir; }
      th.addEventListener('click', doSort); th.addEventListener('keydown', function (e) { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); doSort(); } });
    });
  });

  // Initial sparkline rendering
  renderSparklines();
  renderCharts();
}
