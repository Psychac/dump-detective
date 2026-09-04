// Analyzer section and trend dump group rendering.
// Calls across modules (renderBlocks, buildHealthScorecard, etc.) are resolved
// by function hoisting in the inlined IIFE bundle.
import { el, sevCss } from './report.dom.js';
import { slugifyAnchor } from './report.renderers.shared.js';
import { ensureUniqueDomId } from './report.renderers.shared.js';
import { buildTreeWidget } from './report.renderers.shared.js';
import { resolveStr } from './report.renderers.shared.js';

// Resolves pooled-string cells (see report.renderers.shared.js) back to their literal value.
// Only non-numeric columns can contain pooled indices — the producer never pools a cell in a
// number/bytes/formatted column, so leaving those untouched avoids misreading a legitimate
// small numeric value as a pool index.
function resolvePooledRow(values, headerMeta) {
  let resolved = values;
  for (let i = 0; i < values.length; i++) {
    const meta = headerMeta[i];
    const isNumericColumn = !!(meta && (meta.type === 'number' || meta.type === 'bytes' || meta.format));
    if (!isNumericColumn && typeof values[i] === 'number') {
      if (resolved === values) resolved = values.slice();
      resolved[i] = resolveStr(values[i]);
    }
  }
  return resolved;
}

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
  const type = String(meta && meta.type ? meta.type : '').toLowerCase();
  const format = String(meta && meta.format ? meta.format : '').toLowerCase();
  const kind = format || type;
  if (kind === 'bytes') return formatBytes(numeric);
  if (kind === 'percent') return numeric.toFixed(1) + '%';
  if (kind === 'ratio') return numeric.toFixed(2) + 'x';
  if (kind === 'permille') return numeric.toFixed(1) + '‰';
  if (kind === 'milliseconds') return formatMilliseconds(numeric);
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
    const rows = Array.isArray(ct.rows) ? ct.rows.map(function (r) {
      const values = Array.isArray(r.values) ? r.values : (Array.isArray(r) ? r : []);
      return resolvePooledRow(values, headerMeta);
    }) : [];
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

        const pageSizeOptions = [10, 20, 50, 100, 'all'];
        if (rowCount > pageSizeOptions[0]) {
          const pagination = el('div', 'table-pagination');
          pagination.setAttribute('data-target-table', tableId);

          const sizeLabel = document.createElement('label');
          sizeLabel.className = 'table-pagination__size-label';
          sizeLabel.textContent = 'Rows per page';
          const sizeSelect = document.createElement('select');
          sizeSelect.className = 'table-page-size-select';
          sizeSelect.setAttribute('data-target-table', tableId);
          sizeSelect.setAttribute('aria-label', 'Rows per page');
          for (let oi = 0; oi < pageSizeOptions.length; oi++) {
            const opt = document.createElement('option');
            opt.value = String(pageSizeOptions[oi]);
            opt.textContent = pageSizeOptions[oi] === 'all' ? 'All' : String(pageSizeOptions[oi]);
            if (pageSizeOptions[oi] === limit) opt.selected = true;
            sizeSelect.appendChild(opt);
          }
          sizeLabel.appendChild(sizeSelect);
          pagination.appendChild(sizeLabel);

          const prevBtn = document.createElement('button');
          prevBtn.type = 'button';
          prevBtn.className = 'action-btn table-page-prev-btn';
          prevBtn.setAttribute('data-target-table', tableId);
          prevBtn.textContent = 'Prev';
          pagination.appendChild(prevBtn);

          const pageIndicator = el('span', 'table-pagination__indicator');
          pageIndicator.setAttribute('data-target-table-page', tableId);
          pagination.appendChild(pageIndicator);

          const nextBtn = document.createElement('button');
          nextBtn.type = 'button';
          nextBtn.className = 'action-btn table-page-next-btn';
          nextBtn.setAttribute('data-target-table', tableId);
          nextBtn.textContent = 'Next';
          pagination.appendChild(nextBtn);

          tools.appendChild(pagination);

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
        tableEl.dataset.pageSize = String(limit > 0 ? limit : 'all');
        tableEl.dataset.page = '1';
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

  // ── StackTraces slot ──────────────────────────────────────────────────────
  const stackTraces = Array.isArray(section.stackTraces) ? section.stackTraces : [];
  if (stackTraces.length) {
    const stWrap = el('div', 'typed-slot typed-slot--stack-traces');
    const stHeader = el('div', 'typed-slot__header');
    stHeader.textContent = 'Thread Stack Traces';
    stWrap.appendChild(stHeader);

    for (let ti = 0; ti < stackTraces.length; ti++) {
      const trace = stackTraces[ti];
      const tDetails = el('details', 'stack-trace-card');
      tDetails.dataset.category = String(trace.category || 'sampled').toLowerCase();

      const tSummary = el('summary', 'stack-trace-card__summary');
      const catBadge = el('span', 'stack-trace-card__cat stack-trace-card__cat--' + String(trace.category || 'sampled').toLowerCase());
      catBadge.textContent = trace.category || 'Sampled';
      const tLabel = el('span', 'stack-trace-card__label');
      tLabel.textContent = trace.label || '';
      tSummary.appendChild(catBadge);
      tSummary.appendChild(tLabel);
      if (trace.truncated) {
        const trunc = el('span', 'stack-trace-card__truncated');
        trunc.textContent = ' (truncated)';
        tSummary.appendChild(trunc);
      }
      tDetails.appendChild(tSummary);

      const tBody = el('div', 'stack-trace-card__body');

      // Metadata key-value pairs
      const meta = trace.meta;
      if (meta && typeof meta === 'object' && Object.keys(meta).length) {
        const metaGrid = el('div', 'stack-trace-card__meta');
        for (const [k, v] of Object.entries(meta)) {
          const kEl = el('span', 'stack-trace-card__meta-key'); kEl.textContent = k + ':';
          const vEl = el('span', 'stack-trace-card__meta-val'); vEl.textContent = String(v || '');
          metaGrid.appendChild(kEl); metaGrid.appendChild(vEl);
        }
        tBody.appendChild(metaGrid);
      }

      // Frame list with toggle
      const frames = Array.isArray(trace.frames) ? trace.frames : [];
      if (frames.length) {
        const frameList = el('div', 'stack-trace-card__frames');
        const fwToggle = document.createElement('button');
        fwToggle.type = 'button';
        fwToggle.className = 'action-btn stack-trace-card__fw-toggle';
        fwToggle.textContent = 'Hide framework frames';
        fwToggle.dataset.hideFw = '0';
        fwToggle.addEventListener('click', function () {
          const hide = this.dataset.hideFw === '1';
          this.dataset.hideFw = hide ? '0' : '1';
          this.textContent = hide ? 'Hide framework frames' : 'Show framework frames';
          const fws = frameList.querySelectorAll('.stack-frame--fw');
          for (let fi = 0; fi < fws.length; fi++) fws[fi].hidden = !hide;
        });
        tBody.appendChild(fwToggle);

        for (let fi = 0; fi < frames.length; fi++) {
          const f = frames[fi];
          const fEl = el('div', 'stack-frame' + (f.isFramework ? ' stack-frame--fw' : ''));
          const idx = el('span', 'stack-frame__idx'); idx.textContent = String(f.index != null ? f.index : fi).padStart(2, ' ');
          const txt = el('span', 'stack-frame__text'); txt.textContent = f.text || '';
          fEl.appendChild(idx); fEl.appendChild(txt);
          frameList.appendChild(fEl);
        }
        tBody.appendChild(frameList);
      }

      tDetails.appendChild(tBody);
      stWrap.appendChild(tDetails);
    }
    content.appendChild(stWrap);
  }

  // ── RootPathGroups slot ───────────────────────────────────────────────────
  const rootPathGroups = Array.isArray(section.rootPathGroups) ? section.rootPathGroups : [];
  if (rootPathGroups.length) {
    const rpWrap = el('div', 'typed-slot typed-slot--root-paths');
    const rpOuter = el('details', 'root-path-outer');
    rpOuter.setAttribute('data-collapsible', 'root-paths');
    const rpOuterSum = el('summary', 'root-path-outer__summary');
    const capped = rootPathGroups.some(function (g) { return g.anyCapped; });
    rpOuterSum.textContent = 'Root paths by target type — ' + rootPathGroups.length + ' type(s)' + (capped ? '  ⚠ some paths truncated' : '');
    rpOuter.appendChild(rpOuterSum);

    for (let gi = 0; gi < rootPathGroups.length; gi++) {
      const group = rootPathGroups[gi];
      const gDetails = el('details', 'root-path-group');
      const gSum = el('summary', 'root-path-group__summary');
      const shortName = el('span', 'root-path-group__short'); shortName.textContent = group.targetTypeShort || group.targetType || '';
      const pathCount = el('span', 'root-path-group__count'); pathCount.textContent = ' (' + (group.totalPathCount || 0) + ' path' + (group.totalPathCount !== 1 ? 's' : '') + ')';
      gSum.appendChild(shortName); gSum.appendChild(pathCount);
      if (group.anyCapped) { const warn = el('span', 'root-path-group__warn'); warn.textContent = ' ⚠ truncated'; gSum.appendChild(warn); }
      gDetails.appendChild(gSum);

      const gBody = el('div', 'root-path-group__body');
      const fqn = el('div', 'root-path-group__fqn'); fqn.textContent = group.targetType || '';
      gBody.appendChild(fqn);

      const paths = Array.isArray(group.paths) ? group.paths : [];
      for (let pi = 0; pi < paths.length; pi++) {
        const path = paths[pi];
        if (pi > 0) gBody.appendChild(el('div', 'root-path__divider'));

        const pCard = el('div', 'root-path');
        const pMeta = el('div', 'root-path__meta');
        const mkv = function (k, v) {
          const r = el('span', 'root-path__kv');
          const kEl = el('span', 'root-path__kv-key'); kEl.textContent = k + ':';
          const vEl = el('span', 'root-path__kv-val'); vEl.textContent = v;
          r.appendChild(kEl); r.appendChild(vEl); return r;
        };
        pMeta.appendChild(mkv('Root Kind', path.rootKind || '—'));
        pMeta.appendChild(mkv('Target', path.targetAddress || '—'));
        const lengthLabel = path.wasCapped ? (path.pathLength + '+ (truncated)') : String(path.pathLength || 0);
        pMeta.appendChild(mkv('Length', lengthLabel));
        pCard.appendChild(pMeta);

        // Visual hop chain
        const hops = Array.isArray(path.hops) ? path.hops : [];
        if (hops.length) {
          const chain = el('div', 'root-path__chain');
          const rootHop = el('div', 'root-path__hop root-path__hop--root');
          rootHop.textContent = '[' + (path.rootKind || 'root') + ']';
          chain.appendChild(rootHop);
          for (let hi = 0; hi < hops.length; hi++) {
            const arrow = el('div', 'root-path__arrow'); arrow.textContent = '↓';
            const hop = el('div', 'root-path__hop' + (hi === hops.length - 1 ? ' root-path__hop--target' : ''));
            hop.textContent = hops[hi];
            chain.appendChild(arrow); chain.appendChild(hop);
          }
          if (path.wasCapped) {
            const arrow = el('div', 'root-path__arrow'); arrow.textContent = '↓';
            const more = el('div', 'root-path__hop root-path__hop--truncated'); more.textContent = '… (truncated)';
            chain.appendChild(arrow); chain.appendChild(more);
          }
          pCard.appendChild(chain);
        } else {
          const noRef = el('div', 'root-path__no-refs'); noRef.textContent = 'No intermediate references recorded.';
          pCard.appendChild(noRef);
        }

        gBody.appendChild(pCard);
      }

      gDetails.appendChild(gBody);
      rpOuter.appendChild(gDetails);
    }
    rpWrap.appendChild(rpOuter);
    content.appendChild(rpWrap);
  }

  // ── TypeTraces slot ───────────────────────────────────────────────────────
  const typeTraces = Array.isArray(section.typeTraces) ? section.typeTraces : [];
  if (typeTraces.length) {
    const ttWrap = el('div', 'typed-slot typed-slot--type-traces');
    const ttHeader = el('div', 'typed-slot__header');
    ttHeader.textContent = 'Type Sample Traces';
    ttWrap.appendChild(ttHeader);

    for (let ti = 0; ti < typeTraces.length; ti++) {
      const trace = typeTraces[ti];
      const tDetails = el('details', 'type-trace-card');
      const tSum = el('summary', 'type-trace-card__summary');

      const statusClass = trace.hasGcRoot ? 'rooted' : (trace.traversalLimited ? 'limited' : 'free');
      const statusBadge = el('span', 'type-trace-card__status type-trace-card__status--' + statusClass);
      statusBadge.textContent = trace.statusLabel || '';
      const tName = el('span', 'type-trace-card__name');
      tName.textContent = trace.typeName || '';
      const tSize = el('span', 'type-trace-card__size');
      if (trace.totalSizeBytes > 0) tSize.textContent = ' — ' + formatBytes(trace.totalSizeBytes);
      tSum.appendChild(statusBadge); tSum.appendChild(tName); tSum.appendChild(tSize);
      tDetails.appendChild(tSum);

      const tBody = el('div', 'type-trace-card__body');
      const metaGrid = el('div', 'type-trace-card__meta');
      const addMeta = function (k, v) {
        if (!v && v !== 0) return;
        const kEl = el('span', 'type-trace-card__meta-key'); kEl.textContent = k + ':';
        const vEl = el('span', 'type-trace-card__meta-val'); vEl.textContent = String(v);
        metaGrid.appendChild(kEl); metaGrid.appendChild(vEl);
      };
      addMeta('Count', trace.count != null ? Number(trace.count).toLocaleString('en-US') : null);
      addMeta('Total Size', trace.totalSizeBytes > 0 ? formatBytes(trace.totalSizeBytes) : null);
      addMeta('Sample Address', trace.sampleAddress || null);
      addMeta('Sample Size', trace.sampleObjectSize > 0 ? formatBytes(trace.sampleObjectSize) : null);
      tBody.appendChild(metaGrid);

      // Root hop chain
      const hops = Array.isArray(trace.rootHops) ? trace.rootHops : [];
      if (hops.length) {
        const chainLabel = el('div', 'type-trace-card__chain-label');
        chainLabel.textContent = trace.statusLabel === 'Reference chain' ? 'Reference chain:' : 'GC root chain:';
        tBody.appendChild(chainLabel);
        const chain = el('div', 'type-trace-card__chain');
        for (let hi = 0; hi < hops.length; hi++) {
          if (hi > 0) { const arr = el('div', 'root-path__arrow'); arr.textContent = '↓'; chain.appendChild(arr); }
          const hop = el('div', 'root-path__hop' + (hi === 0 ? ' root-path__hop--root' : hi === hops.length - 1 ? ' root-path__hop--target' : ''));
          hop.textContent = hops[hi];
          chain.appendChild(hop);
        }
        tBody.appendChild(chain);
      } else if (!trace.hasGcRoot && trace.sampleAddress) {
        const noRoot = el('div', 'type-trace-card__no-root');
        noRoot.textContent = trace.traversalLimited
          ? 'Search limit reached — GC root status inconclusive.'
          : 'No GC root found — object may be eligible for collection.';
        tBody.appendChild(noRoot);
      }

      tDetails.appendChild(tBody);
      ttWrap.appendChild(tDetails);
    }
    content.appendChild(ttWrap);
  }

  // ── LeakCandidateCards slot ───────────────────────────────────────────────
  const leakCandidateCards = Array.isArray(section.leakCandidateCards) ? section.leakCandidateCards : [];
  if (leakCandidateCards.length) {
    const lcWrap = el('div', 'typed-slot typed-slot--leak-candidates');
    const lcHeader = el('div', 'typed-slot__header'); lcHeader.textContent = 'Leak Candidate Detail'; lcWrap.appendChild(lcHeader);

    for (let ci = 0; ci < leakCandidateCards.length; ci++) {
      const card = leakCandidateCards[ci];
      const sev = String(card.severity || 'info').toLowerCase();
      const cDetails = el('details', 'leak-card leak-card--' + sev);
      const cSum = el('summary', 'leak-card__summary');

      const sevBadge = el('span', 'leak-card__sev leak-card__sev--' + sev); sevBadge.textContent = card.severity || 'Info';
      const impactBadge = el('span', 'leak-card__impact leak-card__impact--' + String(card.impactBand || 'low').toLowerCase()); impactBadge.textContent = card.impactBand || '';
      const cName = el('span', 'leak-card__name'); cName.textContent = card.typeName || '';
      const cScore = el('span', 'leak-card__score'); cScore.textContent = card.suspicionScore != null ? 'Score ' + Number(card.suspicionScore).toLocaleString('en-US') : '';
      cSum.appendChild(sevBadge); cSum.appendChild(impactBadge); cSum.appendChild(cName); cSum.appendChild(cScore);
      cDetails.appendChild(cSum);

      const cBody = el('div', 'leak-card__body');
      const metaGrid = el('div', 'leak-card__meta');
      const addM = function (k, v) {
        if (v == null || v === '') return;
        const kEl = el('span', 'leak-card__meta-key'); kEl.textContent = k + ':';
        const vEl = el('span', 'leak-card__meta-val'); vEl.textContent = String(v);
        metaGrid.appendChild(kEl); metaGrid.appendChild(vEl);
      };
      addM('Class',      card.classification);
      addM('Instances',  card.instanceCount != null ? Number(card.instanceCount).toLocaleString('en-US') : null);
      addM('Total Size', card.totalSize != null ? formatBytes(Number(card.totalSize)) : null);
      addM('Gen2%',      card.gen2Pct != null ? Number(card.gen2Pct).toFixed(1) + '%' : null);
      addM('Root Kind',  card.rootKind || null);
      addM('Finalizable', card.isFinalizable ? 'Yes' : null);
      addM('Container',  card.isContainer ? 'Yes' : null);
      addM('Ref Ratio',  card.referenceFieldRatio != null ? Number(card.referenceFieldRatio).toFixed(2) : null);
      cBody.appendChild(metaGrid);

      if (card.explanationText) {
        const expl = el('div', 'leak-card__explanation'); expl.textContent = card.explanationText; cBody.appendChild(expl);
      }
      if (card.gcImpactNote || card.lohImpactNote) {
        const impact = el('div', 'leak-card__impact-notes');
        if (card.gcImpactNote) { const n = el('div', 'leak-card__impact-note'); n.textContent = '⊙ ' + card.gcImpactNote; impact.appendChild(n); }
        if (card.lohImpactNote) { const n = el('div', 'leak-card__impact-note'); n.textContent = '⊙ ' + card.lohImpactNote; impact.appendChild(n); }
        cBody.appendChild(impact);
      }
      cDetails.appendChild(cBody);
      lcWrap.appendChild(cDetails);
    }
    content.appendChild(lcWrap);
  }

  // ── EventLeakGroupCards slot ──────────────────────────────────────────────
  const eventLeakGroupCards = Array.isArray(section.eventLeakGroupCards) ? section.eventLeakGroupCards : [];
  if (eventLeakGroupCards.length) {
    const egWrap = el('div', 'typed-slot typed-slot--event-leak-groups');
    const egHeader = el('div', 'typed-slot__header'); egHeader.textContent = 'Event Leak Group Detail'; egWrap.appendChild(egHeader);

    for (let gi = 0; gi < eventLeakGroupCards.length; gi++) {
      const group = eventLeakGroupCards[gi];
      const shape = group.isStatic ? 'STATIC' : 'INSTANCE';
      const gDetails = el('details', 'event-leak-card');
      const gSum = el('summary', 'event-leak-card__summary');
      const shapeBadge = el('span', 'event-leak-card__shape event-leak-card__shape--' + shape.toLowerCase()); shapeBadge.textContent = shape;
      const gTitle = el('span', 'event-leak-card__title'); gTitle.textContent = (group.publisherType || '') + '.' + (group.eventFieldName || '');
      const gSev = el('span', 'event-leak-card__sev'); gSev.textContent = 'Severity ' + (group.severityScore != null ? group.severityScore : '');
      gSum.appendChild(shapeBadge); gSum.appendChild(gTitle); gSum.appendChild(gSev);
      gDetails.appendChild(gSum);

      const gBody = el('div', 'event-leak-card__body');
      const metaGrid = el('div', 'event-leak-card__meta');
      const addM = function (k, v) {
        if (v == null || v === '') return;
        const kEl = el('span', 'event-leak-card__meta-key'); kEl.textContent = k + ':';
        const vEl = el('span', 'event-leak-card__meta-val'); vEl.textContent = String(v);
        metaGrid.appendChild(kEl); metaGrid.appendChild(vEl);
      };
      addM('Instances',       group.instanceCount != null ? Number(group.instanceCount).toLocaleString('en-US') : null);
      addM('Total Subs',      group.totalSubscribers != null ? Number(group.totalSubscribers).toLocaleString('en-US') : null);
      addM('Avg / Min / Max', group.averageSubscribers != null ? Number(group.averageSubscribers).toFixed(1) + ' / ' + group.minSubscribers + ' / ' + group.maxSubscribers : null);
      addM('Gen2 Publishers', group.gen2PublisherPercent != null ? Number(group.gen2PublisherPercent).toFixed(1) + '%' : null);
      addM('Est. Retained',   group.estimatedRetainedBytes > 0 ? formatBytes(Number(group.estimatedRetainedBytes)) : null);
      if (group.hasDuplicateSubscriptions) addM('Dup Subscriptions', 'Yes — same subscriber registered multiple times');
      if (group.hasLifetimeMismatch) addM('Lifetime Mismatch', 'Yes — Gen2 publisher retaining Gen0/Gen1 subscribers');
      if (group.orphanedSubscriberInstances > 0) addM('Orphaned Instances', Number(group.orphanedSubscriberInstances).toLocaleString('en-US') + ' dead-subscriber pattern');
      if (group.isTimerEvent) addM('Category', 'Timer — undisposed timers are a common source of process-lifetime leaks');
      else if (group.isPropertyChangedEvent) addM('Category', 'INotifyPropertyChanged — the highest-frequency MVVM event');
      gBody.appendChild(metaGrid);

      const subTypes = Array.isArray(group.topSubscriberTypes) ? group.topSubscriberTypes : [];
      if (subTypes.length) {
        const stLabel = el('div', 'event-leak-card__sub-label'); stLabel.textContent = 'Top Subscriber Types:'; gBody.appendChild(stLabel);
        const stList = el('div', 'event-leak-card__sub-list');
        for (let si = 0; si < subTypes.length; si++) {
          const row = el('div', 'event-leak-card__sub-row');
          const cnt = el('span', 'event-leak-card__sub-count'); cnt.textContent = Number(subTypes[si].count || 0).toLocaleString('en-US');
          const typ = el('span', 'event-leak-card__sub-type'); typ.textContent = subTypes[si].type || '';
          row.appendChild(cnt); row.appendChild(typ); stList.appendChild(row);
        }
        gBody.appendChild(stList);
      }
      gDetails.appendChild(gBody);
      egWrap.appendChild(gDetails);
    }
    content.appendChild(egWrap);
  }

  // ── EventLeakInstanceCards slot ───────────────────────────────────────────
  const eventLeakInstanceCards = Array.isArray(section.eventLeakInstanceCards) ? section.eventLeakInstanceCards : [];
  if (eventLeakInstanceCards.length) {
    const eiWrap = el('div', 'typed-slot typed-slot--event-leak-instances');
    const eiHeader = el('div', 'typed-slot__header'); eiHeader.textContent = 'Event Leak Instance Detail'; eiWrap.appendChild(eiHeader);

    for (let ii = 0; ii < eventLeakInstanceCards.length; ii++) {
      const inst = eventLeakInstanceCards[ii];
      const shape = inst.isStatic ? 'STATIC' : 'INSTANCE';
      const iDetails = el('details', 'event-leak-card');
      const iSum = el('summary', 'event-leak-card__summary');
      const shapeBadge = el('span', 'event-leak-card__shape event-leak-card__shape--' + shape.toLowerCase()); shapeBadge.textContent = shape;
      const iTitle = el('span', 'event-leak-card__title'); iTitle.textContent = (inst.publisherType || '') + '.' + (inst.eventFieldName || '');
      const iSubs = el('span', 'event-leak-card__sev'); iSubs.textContent = (inst.subscriberCount != null ? inst.subscriberCount : '') + ' subscriber(s)';
      iSum.appendChild(shapeBadge); iSum.appendChild(iTitle); iSum.appendChild(iSubs);
      iDetails.appendChild(iSum);

      const iBody = el('div', 'event-leak-card__body');
      const metaGrid = el('div', 'event-leak-card__meta');
      const addM = function (k, v) {
        if (v == null || v === '') return;
        const kEl = el('span', 'event-leak-card__meta-key'); kEl.textContent = k + ':';
        const vEl = el('span', 'event-leak-card__meta-val'); vEl.textContent = String(v);
        metaGrid.appendChild(kEl); metaGrid.appendChild(vEl);
      };
      addM('Publisher Addr',  inst.publisherAddress || null);
      addM('Severity Score',  inst.severityScore != null ? inst.severityScore : null);
      addM('Root Hint',       inst.rootHint || null);
      addM('Publisher Gen',   inst.publisherGeneration >= 0 ? 'Gen' + inst.publisherGeneration : null);
      addM('Dup Subscriptions', inst.duplicateSubscriptionCount > 0 ? inst.duplicateSubscriptionCount + ' extra registration(s)' : null);
      addM('Orphaned Subs',   inst.orphanedSubscriberCount > 0 ? inst.orphanedSubscriberCount + ' not independently GC-rooted' : null);
      if (inst.hasLifetimeMismatch) addM('Lifetime Mismatch', 'Yes — Gen2 publisher retaining Gen0/Gen1 subscribers');
      iBody.appendChild(metaGrid);

      // Elements are either an inline subscriber-detail object or an int index into
      // section.subscriberDetailPool (docs/refactor/report-payload-size-reduction-design.md, F4).
      const subscriberDetailPool = Array.isArray(section.subscriberDetailPool) ? section.subscriberDetailPool : null;
      const subDetails = (Array.isArray(inst.subscriberDetails) ? inst.subscriberDetails : []).map(function (d) {
        return (typeof d === 'number' && subscriberDetailPool) ? (subscriberDetailPool[d] || d) : d;
      });
      if (subDetails.length) {
        const sdLabel = el('div', 'event-leak-card__sub-label'); sdLabel.textContent = 'Subscriber Details:'; iBody.appendChild(sdLabel);
        const sdList = el('div', 'event-leak-card__sub-list');
        for (let si = 0; si < subDetails.length; si++) {
          const det = subDetails[si];
          const row = el('div', 'event-leak-card__sub-row');
          const cnt = el('span', 'event-leak-card__sub-count'); cnt.textContent = Number(det.count || 0).toLocaleString('en-US');
          const typ = el('span', 'event-leak-card__sub-type'); typ.textContent = det.type || '';
          const mth = el('span', 'event-leak-card__sub-method'); mth.textContent = det.methodName ? ' → ' + det.methodName : '';
          const sz  = el('span', 'event-leak-card__sub-size');  sz.textContent  = det.size > 0 ? ' ' + formatBytes(Number(det.size)) + (det.sizeIsExact ? ' (exact)' : ' (est.)') : '';
          row.appendChild(cnt); row.appendChild(typ); row.appendChild(mth); row.appendChild(sz);
          sdList.appendChild(row);
        }
        iBody.appendChild(sdList);
      }
      iDetails.appendChild(iBody);
      eiWrap.appendChild(iDetails);
    }
    content.appendChild(eiWrap);
  }

  // ── StackClusters slot ────────────────────────────────────────────────────
  const stackClusters = Array.isArray(section.stackClusters) ? section.stackClusters : [];
  if (stackClusters.length) {
    const scWrap = el('div', 'typed-slot typed-slot--stack-clusters');
    const scHeader = el('div', 'typed-slot__header'); scHeader.textContent = 'Thread Stack Signature Clusters'; scWrap.appendChild(scHeader);

    for (let sci = 0; sci < stackClusters.length; sci++) {
      const cluster = stackClusters[sci];
      const scDetails = el('details', 'stack-cluster-card');
      const scSum = el('summary', 'stack-cluster-card__summary');
      const cntBadge = el('span', 'stack-cluster-card__count'); cntBadge.textContent = cluster.threadCount + ' thread' + (cluster.threadCount !== 1 ? 's' : '');
      const osIds = Array.isArray(cluster.osThreadIds) && cluster.osThreadIds.length
        ? el('span', 'stack-cluster-card__osids')
        : null;
      if (osIds) osIds.textContent = 'OS: ' + cluster.osThreadIds.join(', ');
      scSum.appendChild(cntBadge);
      if (osIds) scSum.appendChild(osIds);
      if (cluster.truncated) { const tr = el('span', 'stack-cluster-card__truncated'); tr.textContent = ' (truncated)'; scSum.appendChild(tr); }
      if (cluster.frameworkPattern) { const fp = el('span', 'stack-cluster-card__pattern'); fp.textContent = cluster.frameworkPattern; scSum.appendChild(fp); }
      scDetails.appendChild(scSum);

      const sig = el('div', 'stack-cluster-card__sig'); sig.textContent = cluster.signature || '';
      scDetails.appendChild(sig);
      scWrap.appendChild(scDetails);
    }
    content.appendChild(scWrap);
  }

  // ── TreeWidgets slot ──────────────────────────────────────────────────────
  const treeWidgets = Array.isArray(section.treeWidgets) ? section.treeWidgets : [];
  if (treeWidgets.length) {
    const twWrap = el('div', 'typed-slot typed-slot--tree-widgets');
    for (let twi = 0; twi < treeWidgets.length; twi++) {
      const widget = treeWidgets[twi];
      const twOuter = el('details', 'tree-widget-outer');
      twOuter.setAttribute('data-collapsible', 'tree-widget');
      const twSum = el('summary', 'tree-widget-outer__summary');
      twSum.textContent = (widget.title || 'Tree') + (widget.anyTruncated ? '  ⚠ truncated' : '');
      twOuter.appendChild(twSum);
      const roots = Array.isArray(widget.roots) ? widget.roots : [];
      twOuter.appendChild(buildTreeWidget(roots, { widgetClass: 'thread-cluster-tree' }));
      twWrap.appendChild(twOuter);
    }
    content.appendChild(twWrap);
  }

  // ── Artifacts slot ────────────────────────────────────────────────────────
  const artifacts = Array.isArray(section.artifacts) ? section.artifacts : [];
  if (artifacts.length) {
    const artWrap = el('div', 'typed-slot typed-slot--artifacts');
    const artHeader = el('div', 'typed-slot__header'); artHeader.textContent = 'Analyzer Exports'; artWrap.appendChild(artHeader);
    const artNote = el('div', 'artifact-list__note'); artNote.textContent = 'These files were written to disk for deeper offline inspection.'; artWrap.appendChild(artNote);
    const artList = el('ul', 'artifact-list');
    for (let ai = 0; ai < artifacts.length; ai++) {
      const a = artifacts[ai];
      const li = document.createElement('li'); li.className = 'artifact-list__item';
      const fn = el('span', 'artifact-list__filename'); fn.textContent = a.fileName || '';
      const instr = el('span', 'artifact-list__instructions'); instr.textContent = a.instructions ? ' — ' + a.instructions : '';
      li.appendChild(fn); li.appendChild(instr); artList.appendChild(li);
    }
    artWrap.appendChild(artList);
    content.appendChild(artWrap);
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
