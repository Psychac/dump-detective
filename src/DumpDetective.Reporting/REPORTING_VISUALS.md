# DumpDetective Reporting — Visuals Guide

This document describes the visual template, integration steps, and recommended assets for improved HTML reports.

What was added
- `Templates/report.html` — an embedded single-file report template used by the renderer for standalone reports.
- `wwwroot/css/report.css` — single stylesheet with design tokens, dark mode and print rules.
- `wwwroot/js/report.js` — lightweight JS to initialize charts (Chart.js) and theme toggle.

How to integrate
1. Copy the `wwwroot` folder into your reporting output directory when you generate a report (static assets like CSS/JS live there).
2. Use the built-in single-file renderer (produces an embedded `report.html`) for self-contained reports that work under `file://`.
3. If you prefer a separate server-hosted visual, serve the `wwwroot` assets and adapt the HTML to consume `report.json` externally.
4. For PDF export, prefer server-side rendering (Puppeteer/wkhtmltopdf) for consistent results. A client-side `window.print()` fallback is provided.

Accessibility
- Uses landmarks (`<main>`, `<header>`, `<footer>`), table captions, and ARIA where appropriate.
- Ensure color choices meet WCAG contrast for your data; variables in `report.css` make swapping palettes straightforward.

Customization
- Tweak CSS variables at the top of `report.css` to match branding.
- Swap Chart.js for another renderer if you need treemap or flamegraph components — leave the markup and ARIA hooks intact.

Testing
- A smoke test was added to `tests/DumpDetective.Tests` to ensure the template is present in the tree. For visual regression, integrate Percy/Backstop/Cypress+Snapshots.

Performance
- This initial implementation focuses on visuals and accessibility. Consider deferred/lazy loading and streaming for very large reports; left as a future optimization.
