// Lightweight module loader: dynamically load the modularized report implementation.
(function () {
  try {
    const script = document.currentScript;
    const base = script && script.src ? script.src.replace(/[^/]+$/, '') : (location.href.replace(/[^/]+$/, ''));
    const moduleSrc = base + 'report.main.js';
    const s = document.createElement('script');
    s.type = 'module';
    s.src = moduleSrc;
    s.async = true;
    s.onload = function () { /* loaded */ };
    s.onerror = function () { console.error('Failed to load report.main.js'); fallbackRender(); };
    (document.head || document.documentElement).appendChild(s);
  } catch (e) {
    console.error('Report loader error', e);
    // If the module-based renderer cannot be loaded, attempt a safe fallback
    // renderer that uses the embedded JSON to produce a minimal but readable
    // report. This prevents the report from appearing blank when build
    // artifacts (ES modules) are not present.
    try { fallbackRender(); } catch (__) { /* swallow */ }
  }
})();

  function escapeHtml(s) {
    if (s === null || s === undefined) return '';
    return String(s)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  function fallbackRender() {
    try {
      const jsonEl = document.getElementById('report-json');
      if (!jsonEl) return;
      const doc = JSON.parse(jsonEl.textContent || jsonEl.innerText || '{}');
      const main = document.getElementById('main') || document.body;
      let html = '';

      if (Array.isArray(doc.findings) && doc.findings.length) {
        html += '<section class="fallback-findings"><h2>Findings</h2><ul>';
        for (const f of doc.findings) {
          const title = escapeHtml(f.title || f.id || 'Finding');
          const summary = escapeHtml(f.summary || f.description || '');
          html += `<li><strong>${title}</strong>: ${summary}</li>`;
        }
        html += '</ul></section>';
      }

      if (Array.isArray(doc.analyzerSections) && doc.analyzerSections.length) {
        html += '<section class="fallback-analyzers"><h2>Analyzer Sections</h2>';
        for (const s of doc.analyzerSections) {
          const title = escapeHtml(s.title || s.name || 'Analyzer');
          html += `<article><h3>${title}</h3>`;
          if (Array.isArray(s.blocks) && s.blocks.length) {
            for (const b of s.blocks) {
              const content = escapeHtml(b.text || (b.html ? stripTags(b.html) : ''));
              html += `<div>${content}</div>`;
            }
          }
          html += '</article>';
        }
        html += '</section>';
      }

      if (!html) html = '<p>No findings or analyzer sections to display.</p>';
      main.innerHTML = html;
    } catch (err) {
      console.error('Fallback renderer error', err);
    }
  }

  function stripTags(s) {
    return String(s).replace(/<[^>]*>/g, '');
  }

/* NOTE: Implementation moved to ES modules:
   - report.dom.js
   - report.renderers.js
   - report.ui.js
   - report.main.js

   This file intentionally contains only the tiny loader so the original
   monolithic IIFE was removed to keep the template compact and module-based.
*/
