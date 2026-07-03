// Analyzer section and trend dump group rendering.
// Calls across modules (renderBlocks, buildHealthScorecard, etc.) are resolved
// by function hoisting in the inlined IIFE bundle.
import { el, sevCss } from './report.dom.js';
import { slugifyAnchor } from './report.renderers.shared.js';
import { ensureUniqueDomId } from './report.renderers.shared.js';

function extractCellDisplay(cellData) {
  if (cellData && cellData.display != null) return String(cellData.display);
  if (cellData != null) return String(cellData);
  return '';
}

function formatTypedMetricNumber(metricValue) {
  const numeric = Number(metricValue && metricValue.value);
  if (!Number.isFinite(numeric)) return '';

  const unit = String(metricValue.unit || '').toLowerCase();
  if (unit === 'bytes') {
    const abs = Math.abs(numeric);
    const sign = numeric < 0 ? '-' : '';
    if (abs >= 1099511627776) return sign + (abs / 1099511627776).toFixed(2) + ' TB';
    if (abs >= 1073741824) return sign + (abs / 1073741824).toFixed(2) + ' GB';
    if (abs >= 1048576) return sign + (abs / 1048576).toFixed(2) + ' MB';
    if (abs >= 1024) return sign + (abs / 1024).toFixed(1) + ' KB';
    return sign + abs.toFixed(0) + ' B';
  }

  if (unit === 'percent') return numeric.toFixed(1) + '%';
  if (unit === 'ratio') return numeric.toFixed(2) + 'x';
  if (unit === 'milliseconds') return formatMilliseconds(numeric);
  if (unit === 'custom' && metricValue && typeof metricValue.formatted === 'string' && metricValue.formatted.trim()) {
    return metricValue.formatted;
  }
  return Number.isInteger(numeric) ? numeric.toLocaleString('en-US') : numeric.toFixed(2);
}

function formatCompactNumericValue(numeric, meta) {
  if (!Number.isFinite(numeric)) return '';
  const format = String(meta && meta.format ? meta.format : '').toLowerCase();
  if (format === 'bytes') return formatBytes(numeric);
  if (format === 'percent') return numeric.toFixed(1) + '%';
  if (format === 'ratio') return numeric.toFixed(2) + 'x';
  if (format === 'permille') return numeric.toFixed(1) + '‰';
  if (format === 'milliseconds') return formatMilliseconds(numeric);
  return Number.isInteger(numeric) ? numeric.toLocaleString('en-US') : numeric.toFixed(2);
}

function formatMilliseconds(value) {
  const abs = Math.abs(value);
  if (abs < 1000) return value.toFixed(0) + ' ms';
  if (abs < 60000) return (value / 1000).toFixed(2) + ' s';
  if (abs < 3600000) return (value / 60000).toFixed(2) + ' min';
  return (value / 3600000).toFixed(2) + ' h';
}

function formatBytes(numeric) {
  if (!Number.isFinite(numeric)) return '';
  const abs = Math.abs(Number(numeric));
  const sign = numeric < 0 ? '-' : '';
  if (abs >= 1099511627776) return sign + (abs / 1099511627776).toFixed(2) + ' TB';
  if (abs >= 1073741824) return sign + (abs / 1073741824).toFixed(2) + ' GB';
  if (abs >= 1048576) return sign + (abs / 1048576).toFixed(2) + ' MB';
  if (abs >= 1024) return sign + (abs / 1024).toFixed(1) + ' KB';
  return sign + abs.toFixed(0) + ' B';
}

function buildTopTypesTreemap(tbl) {
  const title = String(tbl.title || '').toLowerCase();
  if (!(title.includes('top') && title.includes('type'))) return null;
  if (!Array.isArray(tbl.headers) || !Array.isArray(tbl.rows)) return null;

  const headers = tbl.headers.map(function (h) { return String(h || '').toLowerCase(); });
  const labelIdx = headers.findIndex(function (h) { return h.includes('type') || h.includes('name'); });
  const sizeIdx = headers.findIndex(function (h) { return h.includes('size') || h.includes('bytes') || h.includes('retained'); });
  if (labelIdx < 0 || sizeIdx < 0) return null;

  const items = [];
  for (let ri = 0; ri < tbl.rows.length && items.length < 12; ri++) {
    const row = tbl.rows[ri];
    const cells = Array.isArray(row) ? row : (row && Array.isArray(row.cells) ? row.cells : []);
    if (!cells.length || sizeIdx >= cells.length || labelIdx >= cells.length) continue;
    const label = extractCellDisplay(cells[labelIdx]);
    const value = typeof cells[sizeIdx] === 'number'
      ? cells[sizeIdx]
      : (cells[sizeIdx] && typeof cells[sizeIdx] === 'object' && Number.isFinite(Number(cells[sizeIdx].rawValue))
        ? Number(cells[sizeIdx].rawValue)
        : Number(cells[sizeIdx]));
    if (!label || value == null || value <= 0) continue;
    items.push({ label: label, value: value });
  }

  if (items.length < 2) return null;
  const chart = el('div', 'detail-chart');
  chart.dataset.chartKind = 'treemap';
  chart.dataset.chartPayload = JSON.stringify({ items: items, caption: 'Top types by size' });
  const titleEl = el('div', 'detail-chart__title');
  titleEl.textContent = 'Top Types Treemap';
  chart.appendChild(titleEl);
  return chart;
}

function hasExplicitTopTypesChart(section) {
  const blocks = section && Array.isArray(section.blocks) ? section.blocks : [];
  for (let i = 0; i < blocks.length; i++) {
    const b = blocks[i];
    if (!b || String(b.type || '').toLowerCase() !== 'chart') continue;
    const title = String(b.title || '').toLowerCase();
    if (title.includes('top') && title.includes('type')) return true;
  }
  return false;
}

// ── Analyzer section renderer ─────────────────────────────────────────────────

export function buildAnalyzerSection(section, i) {
  const anchorScope = arguments.length > 2 ? arguments[2] : '';
  const scopedFallback = anchorScope ? ('detail-' + slugifyAnchor(anchorScope, 'scope') + '-' + i) : ('detail-' + i);
  const stableId = section.sectionId && section.sectionId.trim() ? section.sectionId.trim() : scopedFallback;
  const leadSev = String((section.leadFinding && section.leadFinding.severity) || 'info').toLowerCase();
  const sectionAnchorId = ensureUniqueDomId('section-' + stableId);
  const sectionIndexKey = anchorScope ? (slugifyAnchor(anchorScope, 'scope') + '-' + i) : String(i);
  const wrapper = el('section', 'section-card analyzer-section detail-color-' + (i % 6) + ' analyzer-section--' + leadSev);
  wrapper.id = sectionAnchorId;
  wrapper.dataset.sectionCardId = sectionAnchorId;
  wrapper.dataset.legacySectionId = stableId;
  wrapper.dataset.detailIndex = sectionIndexKey;
  wrapper.dataset.analyzerName = String(section.analyzerName || section.displayTitle || '');
  wrapper.dataset.leadSeverity = leadSev;
  wrapper.dataset.leadConfidence = String((section.leadFinding && section.leadFinding.confidence != null)
    ? Number(section.leadFinding.confidence)
    : 1);

  // Keep legacy section IDs addressable (e.g., #A1) while moving canonical
  // section anchors to section-{id} for v2 contract compliance.
  if (stableId !== sectionAnchorId) {
    const legacyAnchor = el('span', 'section-anchor-legacy');
    legacyAnchor.id = ensureUniqueDomId(stableId);
    legacyAnchor.setAttribute('aria-hidden', 'true');
    legacyAnchor.dataset.anchorAlias = stableId;
    legacyAnchor.setAttribute('data-anchoralias', stableId);
    wrapper.appendChild(legacyAnchor);
  }

  // ── Collapsible section shell ─────────────────────────────────────────────
  const details = el('details');
  details.setAttribute('data-collapsible', 'section');
  const summaryEl = el('summary'); summaryEl.id = 'detail-' + sectionIndexKey + '-summary';
  if (section.sectionId && section.sectionId.trim()) {
    const idBadge = el('span', 'detail-summary__section-id'); idBadge.textContent = section.sectionId.trim(); summaryEl.appendChild(idBadge);
  }
  const title = el('span', 'detail-summary__title'); title.textContent = section.displayTitle || section.analyzerName || '';
  const blocks = section.blocks || [];
  const explicitTopTypesChart = hasExplicitTopTypesChart(section);
  const summaryLeadSev = section.leadFinding ? (section.leadFinding.severity || '').toLowerCase() : '';
  if (summaryLeadSev && summaryLeadSev !== 'info') {
    const sevBadge = el('span', 'detail-summary__sev detail-summary__sev--' + summaryLeadSev);
    sevBadge.textContent = summaryLeadSev.charAt(0).toUpperCase() + summaryLeadSev.slice(1);
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
    const caveats = Array.isArray(lead.caveats) ? lead.caveats : [];
    const cautionCaveats = caveats.filter(function (c) {
      return /heuristic|approximate/i.test(String(c || ''));
    });

    if (cautionCaveats.length) {
      const caution = el('details', 'lead-caution');
      const cautionSummary = el('summary', 'lead-caution__summary');
      cautionSummary.textContent = 'Heuristic/Approximate signal';
      caution.appendChild(cautionSummary);
      const cautionBody = el('div', 'lead-caution__body');
      for (let ci = 0; ci < cautionCaveats.length; ci++) {
        const row = el('div', 'lead-caution__item');
        row.textContent = '\u26A0 ' + cautionCaveats[ci];
        cautionBody.appendChild(row);
      }
      caution.appendChild(cautionBody);
      content.appendChild(caution);
    }

    const lf = el('div', 'lead-finding lead-finding--' + sev + ' finding-card finding-card--' + sev);
    lf.id = ensureUniqueDomId('finding-' + stableId);
    lf.setAttribute('role', 'group');
    lf.setAttribute('aria-label', 'Lead finding for section ' + stableId);

    const lfHeader = el('div', 'lead-finding__header finding-card__header');
    const eyebrow = el('div', 'finding-card__eyebrow');
    const lfSev = el('span', 'severity-badge ' + sevCss(lead.severity));
    lfSev.textContent = lead.severity || 'Info';
    lfSev.setAttribute('aria-label', 'Severity: ' + (lead.severity || 'Info'));
    const lfType = el('span', 'category');
    lfType.textContent = 'Lead signal';
    eyebrow.appendChild(lfSev);
    eyebrow.appendChild(lfType);
    lfHeader.appendChild(eyebrow);

    const headerMeta = el('div', 'finding-card__header-meta');
    if (lead.confidence != null) {
      const conf = Number(lead.confidence);
      const band = conf >= 0.85 ? 'high' : conf >= 0.65 ? 'medhigh' : conf >= 0.45 ? 'medium' : 'low';
      const confChip = el('span', 'finding-card__confidence-chip finding-card__confidence-chip--' + band);
      const meter = el('span', 'finding-card__confidence-meter');
      const slots = Math.max(1, Math.min(4, Math.round(conf * 4)));
      for (let si = 0; si < 4; si++) {
        const slot = el('span', 'finding-card__confidence-slot' + (si < slots ? ' finding-card__confidence-slot--on' : ''));
        meter.appendChild(slot);
      }
      const score = el('span', 'finding-card__confidence-score');
      score.textContent = conf.toFixed(2);
      confChip.appendChild(meter);
      confChip.appendChild(score);
      headerMeta.appendChild(confChip);
    }
    if (headerMeta.childNodes.length) lfHeader.appendChild(headerMeta);
    lf.appendChild(lfHeader);

    const lfTitle = el('div', 'lead-finding__title finding-card__title');
    lfTitle.textContent = lead.title || '';
    lf.appendChild(lfTitle);

    const brief = el('div', 'finding-card__brief');
    if (lead.summary) {
      const issueRow = el('div', 'finding-card__brief-row finding-card__brief-row--issue');
      const issueLabel = el('span', 'finding-card__brief-label finding-card__brief-label--issue');
      issueLabel.textContent = '!';
      issueLabel.setAttribute('aria-label', 'Issue');
      issueLabel.title = 'Issue';
      const issueValue = el('span', 'finding-card__brief-value');
      issueValue.textContent = lead.summary;
      issueRow.appendChild(issueLabel);
      issueRow.appendChild(issueValue);
      brief.appendChild(issueRow);
    }

    if (lead.recommendation) {
      const recRow = el('div', 'finding-card__brief-row finding-card__brief-row--recommendation');
      const recLabel = el('span', 'finding-card__brief-label finding-card__brief-label--recommendation');
      recLabel.textContent = '\u2192';
      recLabel.setAttribute('aria-label', 'Recommendation');
      recLabel.title = 'Recommendation';
      const recValue = el('span', 'finding-card__brief-value');
      recValue.textContent = lead.recommendation;
      recRow.appendChild(recLabel);
      recRow.appendChild(recValue);
      brief.appendChild(recRow);
    }

    if (brief.childNodes.length) lf.appendChild(brief);

    if (lead.caveats && lead.caveats.length) {
      const lfCav = el('div', 'lead-finding__caveats finding-card__caveats');
      for (let ci = 0; ci < lead.caveats.length; ci++) {
        const c = el('div', 'lead-finding__caveat finding-card__caveat');
        c.textContent = '\u26A0 ' + lead.caveats[ci];
        lfCav.appendChild(c);
      }
      lf.appendChild(lfCav);
    }
    content.appendChild(lf);
  }

  // ── Key metrics strip ─────────────────────────────────────────────────────
  const metrics = section.keyMetrics;
  function humanizeKey(k) {
    if (!k) return '';
    const parts = String(k).split('_').filter(Boolean);
    for (let i = 0; i < parts.length; i++) parts[i] = parts[i].charAt(0).toUpperCase() + parts[i].slice(1);
    return parts.join(' ');
  }
  if (metrics && typeof metrics === 'object' && !Array.isArray(metrics)) {
    const strip = el('div', 'key-metrics');
    // If an explicit order is provided, prefer it
    const order = Array.isArray(section.keyMetricsOrder) ? section.keyMetricsOrder : Object.keys(metrics);
    for (let ki = 0; ki < order.length; ki++) {
      const k = order[ki];
      if (!Object.prototype.hasOwnProperty.call(metrics, k)) continue;
      const v = metrics[k];
      const chip = el('div', 'key-metric');
      const lbl = el('span', 'key-metric__label');
      const labelText = (v && v.label) ? v.label : humanizeKey(k);
      lbl.textContent = labelText;
      // expose full label on hover for truncated labels
      if (labelText && labelText.length > 0) lbl.title = labelText;
      const val = el('span', 'key-metric__value');
      const metricValue = v;
      if (metricValue && typeof metricValue === 'object') {
        if (metricValue.kind === 'number') {
          val.textContent = formatTypedMetricNumber(metricValue);
        } else if (metricValue.kind === 'enum' || metricValue.kind === 'text') {
          val.textContent = metricValue.value || '';
        } else {
          val.textContent = '';
        }
      } else {
        // Non-object metric values are unsupported in the modern contract; render empty
        val.textContent = '';
      }
      chip.appendChild(lbl); chip.appendChild(val); strip.appendChild(chip);
    }
    content.appendChild(strip);
  }

  // Narrative blocks from blocks.js
  renderBlocks(blocks, content);

  // ── Typed section tables (collapsed by default) ──────────────────────────
  const sectionTables = Array.isArray(section.compactTables) ? section.compactTables.map(function (ct) {
    const headers = Array.isArray(ct.headers) ? ct.headers.map(function (h) { return (h && h.name) ? String(h.name) : String(h || ''); }) : [];
    const headerMeta = Array.isArray(ct.headers) ? ct.headers.map(function (h) { return ({ type: h && h.type ? String(h.type) : 'string', format: h && h.format ? String(h.format) : null, sortable: (h && (h.sortable === undefined)) ? true : Boolean(h && h.sortable) }); }) : [];
    const rows = Array.isArray(ct.rows) ? ct.rows.map(function (r) { return Array.isArray(r.values) ? r.values : (Array.isArray(r) ? r : []); }) : [];
    return { title: ct.title, headers: headers, headerMeta: headerMeta, rows: rows, rowLimit: ct.rowLimit };
  }) : [];
  if (sectionTables && sectionTables.length) {
    for (let ti = 0; ti < sectionTables.length; ti++) {
      const tbl = sectionTables[ti];
      const rowCount = tbl.rows ? tbl.rows.length : 0;
      if (rowCount === 0) continue;
      const tblDetails = el('details', 'table-collapse');
      tblDetails.setAttribute('data-collapsible', 'table');
      const tblSummary = el('summary', 'table-collapse__summary');
      const limit = tbl.rowLimit || 20;
      tblSummary.textContent = (tbl.title || 'Table') + ' \u2014 ' + rowCount + ' row' + (rowCount !== 1 ? 's' : '');
      tblDetails.appendChild(tblSummary);
      if (tbl.headers && tbl.headers.length) {
        const tableId = 'detail-table-' + sectionIndexKey + '-' + ti;
        const shouldLazyHydrate = rowCount > 180;
        const tools = el('div', 'table-tools');
        const search = document.createElement('input');
        search.type = 'search';
        search.className = 'table-filter-input';
        search.placeholder = 'Filter table rows...';
        search.setAttribute('aria-label', 'Filter table rows');
        search.setAttribute('data-target-table', tableId);
        tools.appendChild(search);

        const count = el('span', 'table-tools__count');
        count.setAttribute('data-target-table-count', tableId);
        count.textContent = rowCount + ' rows';
        tools.appendChild(count);

        if (limit > 0 && rowCount > limit) {
          const showAll = document.createElement('button');
          showAll.type = 'button';
          showAll.className = 'action-btn table-show-all-btn';
          showAll.setAttribute('data-target-table', tableId);
          showAll.textContent = 'Show all ' + rowCount + ' rows';
          tools.appendChild(showAll);

          const printNote = el('div', 'table-print-note');
          printNote.textContent = 'Print/export summary: table body omitted. Showing summary only for ' + rowCount + ' rows.';
          tblDetails.appendChild(printNote);
        }
        tblDetails.appendChild(tools);

        if (!explicitTopTypesChart) {
          const treemap = buildTopTypesTreemap(tbl);
          if (treemap) tblDetails.appendChild(treemap);
        }

        const tblWrap = el('div', 'table-wrap');
        const tableEl = document.createElement('table');
        tableEl.id = tableId;
        tableEl.classList.add('detail-filterable-table');
        tableEl.dataset.responsiveStack = '1';
        tableEl.dataset.limit = String(limit > 0 ? limit : 0);
        tableEl.dataset.showAll = '0';
        tableEl.dataset.hydrated = '0';
        tableEl.dataset.lazyHydrate = shouldLazyHydrate ? '1' : '0';
        const thead = document.createElement('thead');
        const hrow = document.createElement('tr');
        for (let hi = 0; hi < tbl.headers.length; hi++) {
          const th = document.createElement('th'); th.textContent = tbl.headers[hi]; hrow.appendChild(th);
        }
        thead.appendChild(hrow); tableEl.appendChild(thead);
        const tbody = document.createElement('tbody');
        tbody.dataset.lazyBody = shouldLazyHydrate ? '1' : '0';
        // attach header metadata (if present) to the table element for use during hydration/sorting
        tableEl._headerMeta = tbl.headerMeta || null;
        const allRows = tbl.rows || [];

        function hydrateRows() {
          if (tableEl.dataset.hydrated === '1') return;
          for (let ri = 0; ri < allRows.length; ri++) {
            const dataRow = allRows[ri]; const tr = document.createElement('tr');
            tr.dataset.rowIndex = String(ri);
            const cells = Array.isArray(dataRow)
              ? dataRow
              : (dataRow && Array.isArray(dataRow.cells) ? dataRow.cells : []);
            for (let ci = 0; ci < cells.length; ci++) {
                const td = document.createElement('td');
                const cellData = cells[ci];
                td.dataset.colLabel = String(tbl.headers[ci] || ('Column ' + (ci + 1)));

                // Best-effort: compute numeric value for sorting/filtering and
                // normalize display for bytes-typed columns.
                try {
                  const meta = tableEl._headerMeta && tableEl._headerMeta[ci] ? tableEl._headerMeta[ci] : null;
                  let numeric = null;
                  // extract raw display first
                  let display = extractCellDisplay(cellData);
                  if (meta && (meta.type === 'number' || meta.type === 'bytes' || meta.format)) {
                    if (typeof cellData === 'number') numeric = cellData;
                    else if (cellData && typeof cellData === 'object' && Number.isFinite(Number(cellData.rawValue))) numeric = Number(cellData.rawValue);
                    else if (typeof cellData === 'string' && Number.isFinite(Number(String(cellData).replace(/,/g, '').replace(/%$/g, '')))) {
                      numeric = Number(String(cellData).replace(/,/g, '').replace(/%$/g, ''));
                    }

                    if (numeric != null && Number.isFinite(Number(numeric))) {
                      td.dataset.value = String(Number(numeric));
                      td.textContent = formatCompactNumericValue(Number(numeric), meta);
                    } else {
                      td.textContent = display;
                    }
                  } else {
                    td.textContent = display;
                  }
                } catch (e) {
                  td.textContent = extractCellDisplay(cellData);
                }
              tr.appendChild(td);
            }
            if (limit > 0 && rowCount > limit && ri >= limit) {
              tr.hidden = true;
            }
            tbody.appendChild(tr);
          }

          tableEl.dataset.hydrated = '1';
          if (typeof tableEl.__applyManagedState === 'function') {
            tableEl.__applyManagedState();
          }
        }

        if (shouldLazyHydrate && !tblDetails.open) {
          const hint = el('div', 'table-lazy-hint');
          hint.textContent = 'Rows render on expand to keep initial view responsive.';
          tblDetails.appendChild(hint);
          const onToggle = function () {
            if (!tblDetails.open) return;
            hydrateRows();
            hint.remove();
            tblDetails.removeEventListener('toggle', onToggle);
          };
          tblDetails.addEventListener('toggle', onToggle);
        } else {
          hydrateRows();
        }

        tableEl.appendChild(tbody);
        tblWrap.appendChild(tableEl); tblDetails.appendChild(tblWrap);
      }
      content.appendChild(tblDetails);
    }
  }

  details.appendChild(content); wrapper.appendChild(details);

  // ── Provenance footer ─────────────────────────────────────────────────────
  const prov = section.provenance;
  if (prov) {
    wrapper.dataset.provenanceDurationMs = String(Number(prov.durationMs || 0));
    const provDetails = el('details', 'provenance');
    provDetails.id = ensureUniqueDomId('provenance-' + stableId);
    provDetails.setAttribute('data-collapsible', 'provenance');
    const provSummary = el('summary', 'provenance__summary');
    const summaryStatus = (prov.status || '\u2014');
    const summaryDuration = (prov.durationMs != null ? prov.durationMs.toFixed(0) + ' ms' : '\u2014');
    provSummary.textContent = 'Provenance \u2014 ' + summaryStatus + ' \u00b7 ' + summaryDuration;
    provDetails.appendChild(provSummary);
    const provContent = el('div', 'provenance__content');
    const rows = [
      ['Objects scanned', prov.objectScanCount != null ? Number(prov.objectScanCount).toLocaleString() : null],
      ['Cache hits', prov.cacheHits != null ? Number(prov.cacheHits).toLocaleString() : null],
      ['Cache misses', prov.cacheMisses != null ? Number(prov.cacheMisses).toLocaleString() : null],
    ];
    let detailRowCount = 0;
    for (let ri = 0; ri < rows.length; ri++) {
      const r = rows[ri];
      if (r[1] == null) continue;
      const lbl = el('span', 'provenance__label'); lbl.textContent = r[0];
      const val = el('span', 'provenance__value'); val.textContent = r[1];
      provContent.appendChild(lbl); provContent.appendChild(val);
      detailRowCount++;
    }
    if (prov.cappingNotes && prov.cappingNotes.length) {
      for (let ni = 0; ni < prov.cappingNotes.length; ni++) {
        const note = el('div', 'provenance__note'); note.textContent = '\u26A0 ' + prov.cappingNotes[ni]; provContent.appendChild(note);
      }
    }
    if (detailRowCount === 0 && (!prov.cappingNotes || prov.cappingNotes.length === 0)) {
      const note = el('div', 'provenance__note');
      note.textContent = 'No additional provenance diagnostics.';
      provContent.appendChild(note);
    }
    provDetails.appendChild(provContent); wrapper.appendChild(provDetails);
  }

  return wrapper;
}

// ── T3b Correlation Timeline lane ─────────────────────────────────────────────

/**
 * Builds a compact horizontal timeline lane from doc.correlationEvents.
 * Each event card displays as a vertical marker positioned on the snapshot axis.
 * Cards link directly to the primary snapshot detail (#detail-{idx}).
 * Returns null when there are no events.
 */
export function buildCorrelationTimeline(doc) {
  const events = Array.isArray(doc && doc.correlationEvents) ? doc.correlationEvents : [];
  if (!events.length) return null;

  const wrapper = el('div', 'trend-correlation-timeline');
  wrapper.dataset.testid = 'correlation-timeline';

  const heading = el('div', 'trend-correlation-timeline__heading');
  heading.textContent = 'Correlation Timeline';
  wrapper.appendChild(heading);

  const desc = el('p', 'trend-correlation-timeline__desc');
  desc.textContent = 'Cross-domain coupling events detected across snapshots. Cards link to primary snapshot detail.';
  wrapper.appendChild(desc);

  const lane = el('div', 'trend-correlation-timeline__lane');
  lane.setAttribute('role', 'list');
  lane.setAttribute('aria-label', 'Correlation events timeline');

  for (let i = 0; i < events.length; i++) {
    const evt = events[i] || {};
    const snapshotIdx = evt.primarySnapshotIndex != null ? Number(evt.primarySnapshotIndex) : -1;
    const domainList = Array.isArray(evt.domains) ? evt.domains : [];
    const confNum = Number(evt.confidence) || 0;
    const confBand = confNum >= 0.85 ? 'high' : confNum >= 0.65 ? 'med' : 'low';

    const card = el('article', 'timeline-event timeline-event--' + confBand);
    card.setAttribute('role', 'listitem');
    card.dataset.correlationStrength = String(confNum.toFixed(2));
    if (snapshotIdx >= 0) card.dataset.snapshotIndex = String(snapshotIdx);
    // Make card keyboard-focusable and expose summary for screen readers
    card.tabIndex = 0;
    const ariaTitle = (evt.title || 'Correlation') + (snapshotIdx >= 0 ? (' — snapshot ' + snapshotIdx) : '');
    card.setAttribute('aria-label', ariaTitle);

    const titleEl = el('div', 'timeline-event__title');
    if (snapshotIdx >= 0) {
      const link = document.createElement('a');
      link.className = 'timeline-event__link';
      link.href = '#detail-' + snapshotIdx;
      link.textContent = evt.title || 'Correlation';
      link.title = 'Jump to snapshot ' + snapshotIdx + ' detail';
      titleEl.appendChild(link);
    } else {
      titleEl.textContent = evt.title || 'Correlation';
    }
    card.appendChild(titleEl);

    if (domainList.length) {
      const domains = el('div', 'timeline-event__domains');
      for (let d = 0; d < domainList.length; d++) {
        const chip = el('span', 'timeline-event__domain-chip');
        chip.textContent = String(domainList[d] || '');
        domains.appendChild(chip);
      }
      card.appendChild(domains);
    }

    const meta = el('div', 'timeline-event__meta');
    const confSpan = el('span', 'timeline-event__conf timeline-event__conf--' + confBand);
    confSpan.textContent = confNum.toFixed(2);
    confSpan.setAttribute('aria-label', 'Confidence ' + confNum.toFixed(2));
    meta.appendChild(confSpan);
    if (snapshotIdx >= 0) {
      const snapSpan = el('span', 'timeline-event__snap');
      snapSpan.textContent = '\u00b7 Snap ' + snapshotIdx;
      meta.appendChild(snapSpan);
    }
    card.appendChild(meta);

    if (evt.rationale) {
      const rationale = el('p', 'timeline-event__rationale');
      rationale.textContent = evt.rationale;
      card.appendChild(rationale);
    }

    lane.appendChild(card);
  }

  // Add keyboard navigation for the lane: ArrowLeft/ArrowRight/Home/End and Enter/Space to activate
  try {
    lane.addEventListener('keydown', function (ev) {
      const key = ev.key;
      if (!key) return;
      const cards = Array.from(lane.querySelectorAll('.timeline-event'));
      if (!cards.length) return;
      const active = document.activeElement;
      const idx = cards.indexOf(active instanceof Element ? active : null);
      if (key === 'ArrowRight') {
        ev.preventDefault();
        const next = (idx >= 0 && idx < cards.length - 1) ? cards[idx + 1] : cards[0];
        try { next.focus(); } catch (e) { }
        return;
      }
      if (key === 'ArrowLeft') {
        ev.preventDefault();
        const prev = (idx > 0) ? cards[idx - 1] : cards[cards.length - 1];
        try { prev.focus(); } catch (e) { }
        return;
      }
      if (key === 'Home') {
        ev.preventDefault();
        try { cards[0].focus(); } catch (e) { }
        return;
      }
      if (key === 'End') {
        ev.preventDefault();
        try { cards[cards.length - 1].focus(); } catch (e) { }
        return;
      }
      if (key === 'Enter' || key === ' ') {
        // Activate link inside the focused event if present
        if (active && active.classList && active.classList.contains('timeline-event')) {
          const a = active.querySelector && active.querySelector('a');
          if (a) { ev.preventDefault(); try { a.click(); } catch (e) { } }
        }
      }
    });
  } catch (e) { /* non-fatal */ }

  wrapper.appendChild(lane);
  return wrapper;
}

// ── Trend per-dump groups ─────────────────────────────────────────────────────

export function renderTrendDumpGroups(main, sections, perDumpDocs, doc) {
  if (!main) return;
  if (!Array.isArray(perDumpDocs)) perDumpDocs = [];

  // Render standalone trend sections (T2, T3, T4, T5, T7, etc.)
  if (Array.isArray(sections)) {
    for (let i = 0; i < sections.length; i++) {
      const built = buildAnalyzerSection(sections[i], i);
      main.appendChild(built);
      // If this is the T3 Regression Dashboard, inject the compact correlation timeline inside it.
      try {
        const sec = sections[i] || {};
        const sid = String(sec.sectionId || '').trim();
        if ((sid === 'T3' || String((sec.analyzerName || '')).indexOf('TrendRegressionDashboard') >= 0) && typeof buildCorrelationTimeline === 'function') {
          const lane = buildCorrelationTimeline(doc);
          if (lane) {
            // Place timeline inside the section's detail-block when possible.
            const content = built.querySelector('.detail-block');
            if (content) content.appendChild(lane);
            else built.appendChild(lane);
          }
        }
      } catch (e) { /* non-fatal; render without timeline */ }
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
    // Prefer explicit per-doc dumpPath when available; otherwise fall back to a generic label.
    const rawPath = subDoc && subDoc.dumpPath ? subDoc.dumpPath : '';
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
