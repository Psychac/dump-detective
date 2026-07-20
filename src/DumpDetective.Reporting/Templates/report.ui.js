import { renderSparklines, renderCharts } from './report.renderers.js';
import { nvl } from './report.dom.js';
import { filterTocList, setupActiveTocHighlighting } from './report.ui.toc.js';
import { runRenderIntegrityAudit } from './report.ui.integrity.js';
import { loadMotionPreference, setupMotionStagger } from './report.ui.motion.js';
import { setupSeverityFilter, setupT3RegressionFilter } from './report.ui.filters.js';
import { setupGlobalSearch } from './report.ui.search.js';
import { setupKeyboardShortcuts } from './report.ui.keyboard.js';
import { setupExportActions } from './report.ui.actions.js';
import { setupDetailTables } from './report.ui.tables.js';

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
  // Motion preference: combine system preference + optional user toggle
  const __canMotion = loadMotionPreference();
  // expose for other modules to query at runtime
  try { window.__DUMPDETECTIVE_CAN_MOTION__ = __canMotion; } catch (e) { }
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

  // Domain content visibility helpers removed — rely on CSS to hide .domain-body in incident mode

  // materializeForensicsSections removed — sections are rendered inline now.

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

    // Update body state and classes (CSS drives visual differences)
    try {
      document.body.dataset.readingMode = normalized;
      document.body.classList.toggle('reading-mode-incident', normalized === 'incident');
      document.body.classList.toggle('reading-mode-forensics', normalized === 'forensics');
    } catch (e) { /* ignore */ }

    const targets = collectModeTargets();
    const hideForIncident = normalized === 'incident';

    // Hide/show forensics-scoped nodes (analyzer sections, provenance, etc.)
    for (let i = 0; i < targets.forensics.length; i++) {
      const node = targets.forensics[i];
      if (!node) continue;
      node.hidden = hideForIncident;
    }

    // domain visibility is handled by CSS for reading modes

    // Incident-only elements (ribbons, packets)
    for (let i = 0; i < targets.incidentOnly.length; i++) {
      const node = targets.incidentOnly[i];
      if (!node) continue;
      node.hidden = normalized === 'forensics';
    }

    // Domain details: open in forensics, otherwise may open for critical domains
    for (let i = 0; i < targets.domainDetails.length; i++) {
      const detailsNode = targets.domainDetails[i];
      if (!detailsNode) continue;
      detailsNode.open = normalized === 'forensics' ? true : shouldOpenInIncident(detailsNode);
    }

    // Analyzer and provenance detail panels: open only in forensics (respect lock)
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

    // Appendix panels
    for (let i = 0; i < targets.appendixPanels.length; i++) {
      const panel = targets.appendixPanels[i];
      if (!panel) continue;
      panel.open = normalized === 'forensics' ? forensicsLockOpen : false;
    }

    // Defer one more pass to catch asynchronously appended nodes
    window.requestAnimationFrame(function () {
      const deferredTargets = collectModeTargets();
      for (let i = 0; i < deferredTargets.domainDetails.length; i++) {
        const detailsNode = deferredTargets.domainDetails[i];
        if (!detailsNode) continue;
        detailsNode.open = normalized === 'forensics' ? true : shouldOpenInIncident(detailsNode);
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
      // domain visibility is handled by CSS for reading modes
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
  document.addEventListener('dumpdetective:sections-rendered', function () { window.setTimeout(function () { setupT3RegressionFilter(doc); }, 0); });
  document.addEventListener('dumpdetective:domain-sections-appended', function () { window.setTimeout(function () { setupT3RegressionFilter(doc); }, 0); });

  runRenderIntegrityAudit();

  setupMotionStagger(isV2, __canMotion);

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
      const domains = hs && Array.isArray(hs.domains)
        ? hs.domains
        : (hs && hs.domains ? Object.entries(hs.domains).map(function ([k, v]) { return Object.assign({ domain: k }, v || {}); }) : []);
      for (let i = 0; i < domains.length; i++) {
        const d = domains[i] || {};
        critical += Number(nvl(nvl(d.crit, d.criticalCount), 0));
        warning += Number(nvl(nvl(d.warn, d.warningCount), 0));
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
  setupGlobalSearch(announce);

  // Keyboard shortcuts for pagination + critical-signal navigation
  setupKeyboardShortcuts(announce);

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
        try {
          const canMotionNow = (window.__DUMPDETECTIVE_CAN_MOTION__ !== undefined) ? window.__DUMPDETECTIVE_CAN_MOTION__ : true;
          if (canMotionNow) {
            target.classList.remove('anchor-flash');
            void target.offsetWidth;
            target.classList.add('anchor-flash');
          }
        } catch (e) { /* ignore */ }
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

  // Incident promote actions -> Forensics deep context
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
  setupActiveTocHighlighting();

  // Trend-jump handler
  document.addEventListener('click', function (e) { const a = e.target.closest && e.target.closest('.trend-jump'); if (!a) return; try { const href = a.getAttribute('href'); if (!href || !href.startsWith('#')) return; e.preventDefault(); const id = href.substring(1); const target = document.getElementById(id) || document.querySelector('[name="' + id + '"]'); if (target) { if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1'); target.focus({ preventScroll: true }); target.scrollIntoView({ behavior: 'smooth', block: 'center' }); if (announce) announce('Jumped to ' + (id.replace(/[-_]/g, ' '))); } } catch (err) { } });

  // Export actions: download JSON, export CSV, print, high-contrast toggle, clipboard copy
  setupExportActions(announce);

  // Severity filter behavior
  setupSeverityFilter();

  // Detail table controls: filter, show all/limited, cell clamping, and sortable tables
  setupDetailTables();

  // Initial sparkline rendering
  renderSparklines();
  renderCharts();
}
