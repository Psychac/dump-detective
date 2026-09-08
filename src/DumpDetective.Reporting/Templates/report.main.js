import * as Dom from './report.dom.js';
import * as R from './report.renderers.js';
import * as UI from './report.ui.js';

// The embedded payload script can carry data-encoding="gzip-base64" for large reports
// (see docs/refactor/report-payload-size-reduction-design.md, F7). Plain "json" (or the
// attribute being absent, for older reports) is returned as-is.
async function decodePayloadText(el) {
  const text = el.textContent;
  if (!text || !text.trim()) return null;
  const encoding = el.dataset ? el.dataset.encoding : null;
  if (encoding !== 'gzip-base64') return text;
  if (typeof DecompressionStream === 'undefined') {
    throw new Error('This report\'s data is gzip-compressed and requires a browser with ' +
      'DecompressionStream support (current Chrome, Edge, or Firefox) to open.');
  }
  const binary = atob(text.trim());
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  const decompressed = new Blob([bytes]).stream().pipeThrough(new DecompressionStream('gzip'));
  return await new Response(decompressed).text();
}

async function loadPayload() {
  try {
    const el = document.getElementById('report-json');
    const text = el ? await decodePayloadText(el) : null;
    if (text && text.trim()) {
      const parsed = JSON.parse(text);
      window.__REPORT__ = parsed;
      const doc = (parsed && parsed.report) ? parsed.report : parsed;
      const perDumpDocs = (parsed && Array.isArray(parsed.perDumpDocs)) ? parsed.perDumpDocs : [];
      if (doc && doc._external) {
        const href = doc._external;
        try {
          const resp = await fetch(href);
          if (resp.ok) {
            const externalParsed = await resp.json();
            const externalDoc = (externalParsed && externalParsed.report) ? externalParsed.report : externalParsed;
            const externalPerDumpDocs = (externalParsed && Array.isArray(externalParsed.perDumpDocs))
              ? externalParsed.perDumpDocs
              : perDumpDocs;
            return { doc: externalDoc, perDumpDocs: externalPerDumpDocs };
          }
        } catch (e) { /* ignore */ }
      }
      return { doc: doc, perDumpDocs: perDumpDocs };
    }
  } catch (e) {
    if (!window.__REPORT__) throw e;
  }
  const fallback = window.__REPORT__ || null;
  if (!fallback) return null;
  return {
    doc: (fallback && fallback.report) ? fallback.report : fallback,
    perDumpDocs: (fallback && Array.isArray(fallback.perDumpDocs)) ? fallback.perDumpDocs : []
  };
}

function loadLegacyPerDumpDocs() {
  try {
    const el = document.getElementById('per-dump-json');
    if (el && el.textContent && el.textContent.trim()) {
      return JSON.parse(el.textContent);
    }
  } catch (e) { /* ignore */ }
  return [];
}

async function bootstrap() {
  const payload = await loadPayload();
  if (!payload || !payload.doc) return;
  const doc = payload.doc;
  R.setStringPool(doc.strings);
  if ((!doc.perDumpDocs || !doc.perDumpDocs.length) && Array.isArray(payload.perDumpDocs) && payload.perDumpDocs.length) {
    doc.perDumpDocs = payload.perDumpDocs;
  }
  const { announce } = Dom.createAriaLive();
  const styleVersion = String((doc && doc.reportStyleVersion) || 'v1').toLowerCase();
  const isV2 = styleVersion.startsWith('v2');
  document.body.dataset.reportStyleVersion = isV2 ? styleVersion : 'v1';
  document.body.classList.toggle('report-style-v2', isV2);
  const printFooter = document.getElementById('report-print-footer');
  if (printFooter) {
    const gen = doc && doc.generatedAtUtc ? new Date(doc.generatedAtUtc).toLocaleString() : 'n/a';
    const version = doc && doc.analyzerVersion ? String(doc.analyzerVersion) : 'n/a';
    const limitationCount = doc && doc.appendix && Array.isArray(doc.appendix.knownLimitations)
      ? doc.appendix.knownLimitations.length
      : 0;
    printFooter.textContent = 'Generated: ' + gen + ' | Analyzer version: ' + version + ' | Known limitations: ' + limitationCount;
  }

  const main = document.getElementById('main');
  if (!main) return;
  const rightRail = document.getElementById('report-right-rail-content');
  const rightRailHost = document.getElementById('report-right-rail');
  if (rightRailHost) rightRailHost.hidden = true;
  document.body.classList.remove('has-right-rail');
  const renderMode = String((doc && doc.renderMode) || '').toLowerCase();
  const reportContent = document.getElementById('report-content');
  const hasPreRenderedContent = renderMode === 'prerendered'
    || !!(reportContent && reportContent.children && reportContent.children.length > 0);
  const hasPreRenderedDomains = !!document.getElementById('report-domains');

  const headerNode = R.buildHeader(doc);
  if (headerNode) {
    if (main.firstChild) main.insertBefore(headerNode, main.firstChild);
    else main.appendChild(headerNode);
  }

  if (!hasPreRenderedContent) {
    const scorecard = R.buildHealthScorecard(doc);
    if (scorecard) main.appendChild(scorecard);
  }

  const executive = R.buildExecutiveSummary(doc);
  if (executive) {
    if (hasPreRenderedContent && reportContent) {
      const preRenderedScorecard = reportContent.querySelector('#health-scorecard, .health-scorecard');
      if (preRenderedScorecard && preRenderedScorecard.parentElement === reportContent) {
        reportContent.insertBefore(executive, preRenderedScorecard.nextSibling);
      } else {
        reportContent.insertBefore(executive, reportContent.firstChild);
      }
    } else {
      main.appendChild(executive);
    }
  }

  const actionQueue = R.buildActionQueuePanel(doc);
  if (actionQueue) {
    main.appendChild(actionQueue);
  }

  const forensicsRail = R.buildForensicsRailPanel(doc);
  if (forensicsRail) {
    main.appendChild(forensicsRail);
  }

  const globalSearch = R.buildGlobalSearchBar(doc);
  if (globalSearch) main.appendChild(globalSearch);

  const filterBar = R.buildFilterBar(doc);
  if (filterBar) main.appendChild(filterBar);

  let domains = null;
  if (!hasPreRenderedDomains) {
    domains = R.buildDomains(doc);
    if (domains) main.appendChild(domains);
  }

  if (!hasPreRenderedDomains) {
    const crossDomain = R.buildCrossDomainInsights(doc);
    if (crossDomain) main.appendChild(crossDomain);
  }

  const isTrend = !!doc.isTrendReport || doc['$kind'] === 'trend';

  if (!domains && !hasPreRenderedContent) {
    const devSec = R.buildDevActionPlan(doc); if (devSec) main.appendChild(devSec);
  }

  const perDumpDocs = isTrend
    ? ((payload.perDumpDocs && payload.perDumpDocs.length) ? payload.perDumpDocs : loadLegacyPerDumpDocs())
    : [];
  const toc = R.buildTOC(doc, perDumpDocs);

  UI.buildSidebar(toc, doc);

  if (!domains && !hasPreRenderedContent) {
    const conf = R.buildConfidenceNotes(doc); if (conf) main.appendChild(conf);
  }

  // For trend reports use trendAnalyzerSections (serialized); for single-dump fall back to analyzerSections
  const sections = (isTrend ? (doc.trendAnalyzerSections || []) : (doc.analyzerSections || []));
  if ((!domains || isTrend) && !hasPreRenderedContent) {
    if (isTrend) {
      R.renderTrendDumpGroups(main, sections, perDumpDocs, doc);
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
      } else {
        try {
          document.dispatchEvent(new CustomEvent('dumpdetective:sections-rendered', {
            detail: {
              totalSections: sections.length,
              mode: isTrend ? 'trend' : 'single'
            }
          }));
        } catch (e) { }
      }
    };

    renderChunk();
    }
  }

  const incident = R.buildIncidentContext(doc); if (incident) main.appendChild(incident);

  const appendix = R.buildAppendix(doc);
  if (appendix) main.appendChild(appendix);

  // Ensure details aria-expanded sync
  document.querySelectorAll('.analyzer-section details').forEach(function (d) { const s = d.querySelector('summary'); if (s) s.setAttribute('aria-expanded', String(d.open)); d.addEventListener('toggle', function () { if (s) s.setAttribute('aria-expanded', String(d.open)); }); });

  UI.setupInteractivity(doc, announce);
}

bootstrap().catch(err => {
  console.error('Report bootstrap failed', err);
  const main = document.getElementById('main');
  if (main) {
    const banner = document.createElement('div');
    banner.setAttribute('role', 'alert');
    banner.style.cssText = 'margin:2rem;padding:1rem;border:1px solid #c00;background:#fee;color:#900;font:14px sans-serif;';
    banner.textContent = 'Failed to load this report: ' + (err && err.message ? err.message : String(err));
    main.prepend(banner);
  }
});
