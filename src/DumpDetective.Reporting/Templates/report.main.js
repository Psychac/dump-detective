import * as Dom from './report.dom.js';
import * as R from './report.renderers.js';
import * as UI from './report.ui.js';

async function loadDoc() {
  try {
    const el = document.getElementById('report-json');
    if (el && el.textContent && el.textContent.trim()) {
      try {
        const parsed = JSON.parse(el.textContent);
        if (parsed && parsed._external) {
          const href = parsed._external;
          try { const resp = await fetch(href); if (resp.ok) return await resp.json(); } catch (e) { /* ignore */ }
        }
        return parsed;
      } catch (e) { /* fall through */ }
    }
  } catch (e) { }
  return window.__REPORT__ || null;
}

function loadPerDumpDocs() {
  try {
    const el = document.getElementById('per-dump-json');
    if (el && el.textContent && el.textContent.trim()) {
      return JSON.parse(el.textContent);
    }
  } catch (e) { /* ignore */ }
  return [];
}

async function bootstrap() {
  const doc = await loadDoc();
  if (!doc) return;
  const { announce } = Dom.createAriaLive();

  const main = document.getElementById('main');
  if (!main) return;

  main.appendChild(R.buildHeader(doc));

  const scorecard = R.buildHealthScorecard(doc);
  if (scorecard) main.appendChild(scorecard);

  const executive = R.buildExecutiveSummary(doc);
  if (executive) main.appendChild(executive);

  const actionQueue = R.buildActionQueuePanel(doc);
  if (actionQueue) main.appendChild(actionQueue);

  const globalSearch = R.buildGlobalSearchBar(doc);
  if (globalSearch) main.appendChild(globalSearch);

  const filterBar = R.buildFilterBar(doc);
  if (filterBar) main.appendChild(filterBar);

  const domains = R.buildDomains(doc);
  if (domains) main.appendChild(domains);

  const crossDomain = R.buildCrossDomainInsights(doc);
  if (crossDomain) main.appendChild(crossDomain);

  const isTrend = !!doc.isTrendReport || doc['$kind'] === 'trend';

  if (!domains) {
    const devSec = R.buildDevActionPlan(doc); if (devSec) main.appendChild(devSec);
  }

  const perDumpDocs = isTrend ? loadPerDumpDocs() : [];
  const toc = R.buildTOC(doc, perDumpDocs);

  UI.buildSidebar(toc, doc);

  const incident = R.buildIncidentContext(doc); if (incident) main.appendChild(incident);

  if (!domains) {
    const conf = R.buildConfidenceNotes(doc); if (conf) main.appendChild(conf);
  }

  // For trend reports use trendAnalyzerSections (serialized); for single-dump fall back to analyzerSections
  const sections = (isTrend ? (doc.trendAnalyzerSections || []) : (doc.analyzerSections || []));
  if (!domains || isTrend) {
    if (isTrend) {
      R.renderTrendDumpGroups(main, sections, perDumpDocs);
    } else {
    const chunkSize = 12;
    let index = 0;

    const renderChunk = function () {
      const end = Math.min(sections.length, index + chunkSize);
      for (; index < end; index++) {
        try {
          main.appendChild(R.buildAnalyzerSection(sections[index], index));
        } catch (err) {
          console.error('Failed to render analyzer section', sections[index], err);
        }
      }

      if (index < sections.length) {
        if (window.requestIdleCallback) {
          window.requestIdleCallback(renderChunk, { timeout: 120 });
        } else {
          window.setTimeout(renderChunk, 0);
        }
      }
    };

    renderChunk();
    }
  }

  const appendix = R.buildAppendix(doc);
  if (appendix) main.appendChild(appendix);

  // Ensure details aria-expanded sync
  document.querySelectorAll('.analyzer-section details').forEach(function (d) { const s = d.querySelector('summary'); if (s) s.setAttribute('aria-expanded', String(d.open)); d.addEventListener('toggle', function () { if (s) s.setAttribute('aria-expanded', String(d.open)); }); });

  UI.setupInteractivity(doc, announce);
}

bootstrap().catch(err => console.error('Report bootstrap failed', err));
