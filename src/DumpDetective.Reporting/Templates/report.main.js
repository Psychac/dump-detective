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

async function bootstrap() {
  const doc = await loadDoc();
  if (!doc) return;
  const { announce } = Dom.createAriaLive();

  const main = document.getElementById('main');
  if (!main) return;

  main.appendChild(R.buildHeader(doc));

  const devSec = R.buildDevActionPlan(doc); if (devSec) main.appendChild(devSec);
  const toc = R.buildTOC(doc);

  UI.buildSidebar(toc, doc);

  const incident = R.buildIncidentContext(doc); if (incident) main.appendChild(incident);

  const conf = R.buildConfidenceNotes(doc); if (conf) main.appendChild(conf);

  const sections = doc.analyzerSections || [];
  for (let i = 0; i < sections.length; i++) main.appendChild(R.buildAnalyzerSection(sections[i], i));

  // Ensure details aria-expanded sync
  document.querySelectorAll('.analyzer-section details').forEach(function (d) { const s = d.querySelector('summary'); if (s) s.setAttribute('aria-expanded', String(d.open)); d.addEventListener('toggle', function () { if (s) s.setAttribute('aria-expanded', String(d.open)); }); });

  UI.setupInteractivity(doc, announce);
}

bootstrap().catch(err => console.error('Report bootstrap failed', err));
