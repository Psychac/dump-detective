// Analyzer section and trend dump group rendering.
// Calls across modules (renderBlocks, buildHealthScorecard, etc.) are resolved
// by function hoisting in the inlined IIFE bundle.
import { el } from './report.dom.js';
import { slugifyAnchor } from './report.renderers.shared.js';

// ── Analyzer section renderer ─────────────────────────────────────────────────

export function buildAnalyzerSection(section, i) {
  const anchorScope = arguments.length > 2 ? arguments[2] : '';
  const scopedFallback = anchorScope ? ('detail-' + slugifyAnchor(anchorScope, 'scope') + '-' + i) : ('detail-' + i);
  const stableId = section.sectionId && section.sectionId.trim() ? section.sectionId.trim() : scopedFallback;
  const sectionIndexKey = anchorScope ? (slugifyAnchor(anchorScope, 'scope') + '-' + i) : String(i);
  const wrapper = el('section', 'analyzer-section detail-color-' + (i % 6));
  wrapper.id = stableId;
  wrapper.dataset.detailIndex = sectionIndexKey;

  // ── Collapsible section shell ─────────────────────────────────────────────
  const details = el('details'); const summaryEl = el('summary'); summaryEl.id = 'detail-' + sectionIndexKey + '-summary';
  if (section.sectionId && section.sectionId.trim()) {
    const idBadge = el('span', 'detail-summary__section-id'); idBadge.textContent = section.sectionId.trim(); summaryEl.appendChild(idBadge);
  }
  const title = el('span', 'detail-summary__title'); title.textContent = section.displayTitle || section.analyzerName || '';
  const blocks = section.blocks || [];
  const leadSev = section.leadFinding ? (section.leadFinding.severity || '').toLowerCase() : '';
  if (leadSev && leadSev !== 'info') {
    const sevBadge = el('span', 'detail-summary__sev detail-summary__sev--' + leadSev);
    sevBadge.textContent = leadSev.charAt(0).toUpperCase() + leadSev.slice(1);
    summaryEl.appendChild(title); summaryEl.appendChild(sevBadge);
  } else {
    summaryEl.appendChild(title);
  }
  details.appendChild(summaryEl);

  const content = el('div', 'detail-block');
  content.setAttribute('role', 'region');
  content.setAttribute('aria-labelledby', summaryEl.id);
  content.dataset.sectionIndex = sectionIndexKey;

  // ── Lead finding panel ────────────────────────────────────────────────────
  const lead = section.leadFinding;
  if (lead) {
    const sev = (lead.severity || 'info').toLowerCase();
    const lf = el('div', 'lead-finding lead-finding--' + sev);
    const lfHeader = el('div', 'lead-finding__header');
    const lfSev = el('span', 'lead-finding__severity'); lfSev.textContent = lead.severity || 'Info';
    const lfTitle = el('span', 'lead-finding__title'); lfTitle.textContent = lead.title || '';
    const lfConf = el('span', 'lead-finding__confidence');
    const confScore = lead.confidenceScore || 0;
    const confBand = confScore >= 0.85 ? 'High' : confScore >= 0.65 ? 'Med-High' : confScore >= 0.45 ? 'Medium' : 'Low';
    lfConf.textContent = (lead.confidenceSymbol || '') + '\u2002' + confBand;
    lfHeader.appendChild(lfSev); lfHeader.appendChild(lfTitle); lfHeader.appendChild(lfConf);
    lf.appendChild(lfHeader);
    if (lead.evidence) { const lfEv = el('div', 'lead-finding__evidence'); lfEv.textContent = lead.evidence; lf.appendChild(lfEv); }
    if (lead.recommendation) { const lfRec = el('div', 'lead-finding__recommendation'); lfRec.textContent = '\u2192 ' + lead.recommendation; lf.appendChild(lfRec); }
    if (lead.caveats && lead.caveats.length) {
      const lfCav = el('div', 'lead-finding__caveats');
      for (let ci = 0; ci < lead.caveats.length; ci++) { const c = el('div', 'lead-finding__caveat'); c.textContent = '\u26A0 ' + lead.caveats[ci]; lfCav.appendChild(c); }
      lf.appendChild(lfCav);
    }
    content.appendChild(lf);
  }

  // ── Key metrics strip ─────────────────────────────────────────────────────
  const metrics = section.keyMetrics;
  if (metrics && metrics.length) {
    const strip = el('div', 'key-metrics');
    for (let mi = 0; mi < metrics.length; mi++) {
      const m = metrics[mi]; const chip = el('div', 'key-metric');
      const lbl = el('span', 'key-metric__label'); lbl.textContent = m.label || '';
      const val = el('span', 'key-metric__value'); val.textContent = m.value || '';
      chip.appendChild(lbl); chip.appendChild(val); strip.appendChild(chip);
    }
    content.appendChild(strip);
  }

  // Narrative blocks from blocks.js
  renderBlocks(blocks, content);

  // ── Typed section tables (collapsed by default) ──────────────────────────
  const sectionTables = section.tables;
  if (sectionTables && sectionTables.length) {
    for (let ti = 0; ti < sectionTables.length; ti++) {
      const tbl = sectionTables[ti];
      const rowCount = tbl.rows ? tbl.rows.length : 0;
      if (rowCount === 0) continue;
      const tblDetails = el('details', 'table-collapse');
      const tblSummary = el('summary', 'table-collapse__summary');
      const limit = tbl.rowLimit || 20;
      tblSummary.textContent = (tbl.title || 'Table') + ' \u2014 ' + rowCount + ' row' + (rowCount !== 1 ? 's' : '');
      tblDetails.appendChild(tblSummary);
      if (tbl.headers && tbl.headers.length) {
        const tblWrap = el('div', 'table-wrap');
        const tableEl = document.createElement('table');
        const thead = document.createElement('thead');
        const hrow = document.createElement('tr');
        for (let hi = 0; hi < tbl.headers.length; hi++) {
          const th = document.createElement('th'); th.textContent = tbl.headers[hi]; hrow.appendChild(th);
        }
        thead.appendChild(hrow); tableEl.appendChild(thead);
        const tbody = document.createElement('tbody');
        const displayRows = limit > 0 && rowCount > limit ? tbl.rows.slice(0, limit) : (tbl.rows || []);
        for (let ri = 0; ri < displayRows.length; ri++) {
          const dataRow = displayRows[ri]; const tr = document.createElement('tr');
          const cells = Array.isArray(dataRow)
            ? dataRow
            : (dataRow && Array.isArray(dataRow.cells) ? dataRow.cells : []);
          for (let ci = 0; ci < cells.length; ci++) {
            const td = document.createElement('td');
            const cellData = cells[ci];
            td.textContent = (cellData && cellData.display != null) ? cellData.display : (cellData != null ? String(cellData) : '');
            tr.appendChild(td);
          }
          tbody.appendChild(tr);
        }
        tableEl.appendChild(tbody);
        if (limit > 0 && rowCount > limit) {
          const tfoot = document.createElement('tfoot');
          const tfrow = document.createElement('tr');
          const tftd = document.createElement('td'); tftd.colSpan = tbl.headers.length;
          tftd.className = 'table-footer-note'; tftd.textContent = '\u2026 ' + (rowCount - limit) + ' more rows hidden (rowLimit=' + limit + ')';
          tfrow.appendChild(tftd); tfoot.appendChild(tfrow); tableEl.appendChild(tfoot);
        }
        tblWrap.appendChild(tableEl); tblDetails.appendChild(tblWrap);
      }
      content.appendChild(tblDetails);
    }
  }

  details.appendChild(content); wrapper.appendChild(details);

  // ── Provenance footer ─────────────────────────────────────────────────────
  const prov = section.provenance;
  if (prov) {
    const provDetails = el('details', 'provenance');
    const provSummary = el('summary', 'provenance__summary');
    provSummary.textContent = 'Provenance \u2014 ' + (prov.analyzer || '') + ' \u00b7 ' + (prov.status || '') + ' \u00b7 ' + (prov.durationMs != null ? prov.durationMs.toFixed(0) + ' ms' : '\u2014');
    provDetails.appendChild(provSummary);
    const provContent = el('div', 'provenance__content');
    const rows = [
      ['Analyzer', prov.analyzer],
      ['Status', prov.status],
      ['Duration', prov.durationMs != null ? prov.durationMs.toFixed(0) + ' ms' : '\u2014'],
      ['Objects scanned', prov.objectScanCount != null ? Number(prov.objectScanCount).toLocaleString() : '\u2014'],
      ['Cache hits', prov.cacheHits != null ? Number(prov.cacheHits).toLocaleString() : '\u2014'],
      ['Cache misses', prov.cacheMisses != null ? Number(prov.cacheMisses).toLocaleString() : '\u2014'],
    ];
    for (let ri = 0; ri < rows.length; ri++) {
      const r = rows[ri];
      const lbl = el('span', 'provenance__label'); lbl.textContent = r[0];
      const val = el('span', 'provenance__value'); val.textContent = r[1] || '\u2014';
      provContent.appendChild(lbl); provContent.appendChild(val);
    }
    if (prov.cappingNotes && prov.cappingNotes.length) {
      for (let ni = 0; ni < prov.cappingNotes.length; ni++) {
        const note = el('div', 'provenance__note'); note.textContent = '\u26A0 ' + prov.cappingNotes[ni]; provContent.appendChild(note);
      }
    }
    provDetails.appendChild(provContent); wrapper.appendChild(provDetails);
  }

  return wrapper;
}

// ── Trend per-dump groups ─────────────────────────────────────────────────────

export function renderTrendDumpGroups(main, sections, perDumpDocs) {
  if (!main) return;
  if (!Array.isArray(perDumpDocs)) perDumpDocs = [];

  // Render standalone trend sections (T2, T3, T4, T5, T7, etc.)
  if (Array.isArray(sections)) {
    for (let i = 0; i < sections.length; i++) {
      main.appendChild(buildAnalyzerSection(sections[i], i));
    }
  }

  // Render each per-dump document as a full single-dump report in a collapsible group
  for (let dumpIndex = 0; dumpIndex < perDumpDocs.length; dumpIndex++) {
    const subDoc = perDumpDocs[dumpIndex];
    if (!subDoc) continue;

    const groupSection = el('section', 'analyzer-section trend-dump-group detail-color-' + (dumpIndex % 6));
    groupSection.id = 'dump-detail-' + dumpIndex;
    groupSection.dataset.trendDumpIndex = String(dumpIndex);

    const details = el('details');
    const summaryEl = el('summary');
    const rawPath = subDoc.dumpPath || '';
    summaryEl.textContent = rawPath ? rawPath.replace(/^.*[\\/]/, '') : ('Dump ' + (dumpIndex + 1));
    details.appendChild(summaryEl);

    const content = el('div', 'detail-block trend-dump-group__content');

    const scorecard = buildHealthScorecard(subDoc);
    if (scorecard) content.appendChild(scorecard);

    const executive = buildExecutiveSummary(subDoc);
    if (executive) content.appendChild(executive);

    const actionQueue = buildActionQueuePanel(subDoc);
    if (actionQueue) content.appendChild(actionQueue);

    const domainsEl = buildDomains(subDoc);
    if (domainsEl) content.appendChild(domainsEl);

    const crossDomain = buildCrossDomainInsights(subDoc);
    if (crossDomain) content.appendChild(crossDomain);

    const incident = buildIncidentContext(subDoc);
    if (incident) content.appendChild(incident);

    const appendix = buildAppendix(subDoc);
    if (appendix) content.appendChild(appendix);

    details.appendChild(content);
    groupSection.appendChild(details);
    main.appendChild(groupSection);
  }
}
