# DumpDetective Reporting — Visuals Guide

This document describes the visual template, integration steps, and recommended assets for improved HTML reports.

What was added
- `Templates/report-template.html` — a responsive accessible HTML template with KPIs, charts, and tables.
- `wwwroot/css/report.css` — single stylesheet with design tokens, dark mode and print rules.
- `wwwroot/js/report.js` — lightweight JS to initialize charts (Chart.js) and theme toggle.

How to integrate
1. Copy `Templates/report-template.html` and `wwwroot` folder into your reporting output directory when you generate a report.
2. Replace placeholder KPI values and table rows with real data from the analysis engine. Template uses semantic HTML and ARIA attributes.
3. Include `Chart.js` (CDN is in the template) or bundle it with your static assets. `report.js` expects Chart.js to be present.
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
