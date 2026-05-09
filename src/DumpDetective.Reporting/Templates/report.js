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
    s.onerror = function () { console.error('Failed to load report.main.js'); };
    (document.head || document.documentElement).appendChild(s);
  } catch (e) {
    console.error('Report loader error', e);
  }
})();

/* NOTE: Implementation moved to ES modules:
   - report.dom.js
   - report.renderers.js
   - report.ui.js
   - report.main.js

   This file intentionally contains only the tiny loader so the original
   monolithic IIFE was removed to keep the template compact and module-based.
*/
