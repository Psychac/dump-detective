function applyTableCellClamp(scope) {
  const root = scope || document;
  const cells = root.querySelectorAll('td');
  for (let i = 0; i < cells.length; i++) {
    const td = cells[i];
    if (!td || td.dataset.clampReady === '1') continue;
    const text = String(td.textContent || '').trim();
    if (!text || text.length < 140) {
      td.dataset.clampReady = '1';
      continue;
    }
    if (td.querySelector('a, button, input, select, textarea')) {
      td.dataset.clampReady = '1';
      continue;
    }

    td.textContent = '';
    const content = document.createElement('span');
    content.className = 'table-cell-clamp__text is-clamped';
    content.textContent = text;
    td.appendChild(content);

    const toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'table-cell-clamp__toggle';
    toggle.textContent = 'Expand';
    toggle.setAttribute('aria-expanded', 'false');
    toggle.addEventListener('click', function () {
      const expanded = content.classList.toggle('is-clamped');
      const isCollapsed = expanded;
      toggle.textContent = isCollapsed ? 'Expand' : 'Collapse';
      toggle.setAttribute('aria-expanded', isCollapsed ? 'false' : 'true');
    });
    td.appendChild(toggle);
    td.dataset.clampReady = '1';
  }
}

function applyManagedTableState(tbl) {
  if (!tbl) return;
  if (tbl.__paged) { applyPagedTableState(tbl); return; }
  const pageSizeRaw = tbl.dataset.pageSize || '20';
  const pageSize = pageSizeRaw === 'all' ? 0 : Number(pageSizeRaw);
  const input = document.querySelector('.table-filter-input[data-target-table="' + tbl.id + '"]');
  const query = input ? input.value.trim().toLowerCase() : '';
  const rows = Array.from(tbl.querySelectorAll('tbody tr'));
  const matchedRows = [];
  for (let i = 0; i < rows.length; i++) {
    const row = rows[i];
    const text = (row.textContent || '').toLowerCase();
    if (!query || text.includes(query)) {
      matchedRows.push(row);
    } else {
      row.hidden = true;
    }
  }

  const window_ = pageWindow(tbl, matchedRows.length, pageSize);
  for (let i = 0; i < matchedRows.length; i++) {
    matchedRows[i].hidden = !(i >= window_.start && i < window_.end);
  }
  updatePagerUi(tbl, matchedRows.length, window_, query, pageSizeRaw);
  applyTableCellClamp(tbl);
}

// The page a table is on and the row range it shows, with the page clamped to the pages that exist.
function pageWindow(tbl, matchedCount, pageSize) {
  const totalPages = pageSize > 0 ? Math.max(1, Math.ceil(matchedCount / pageSize)) : 1;
  let page = Number(tbl.dataset.page || '1');
  if (!Number.isFinite(page) || page < 1) page = 1;
  if (page > totalPages) page = totalPages;
  tbl.dataset.page = String(page);
  const start = pageSize > 0 ? (page - 1) * pageSize : 0;
  const end = pageSize > 0 ? start + pageSize : matchedCount;
  return { page: page, totalPages: totalPages, start: start, end: end };
}

function updatePagerUi(tbl, matchedCount, window_, query, pageSizeRaw) {
  const count = document.querySelector('[data-target-table-count="' + tbl.id + '"]');
  if (count) {
    if (matchedCount === 0) {
      count.textContent = query ? '0 matching rows' : '0 rows';
    } else {
      const rangeText = (window_.start + 1) + '–' + Math.min(window_.end, matchedCount);
      count.textContent = query
        ? (rangeText + ' of ' + matchedCount + ' matching rows')
        : (rangeText + ' of ' + matchedCount + ' rows');
    }
  }

  const sizeSelect = document.querySelector('.table-page-size-select[data-target-table="' + tbl.id + '"]');
  if (sizeSelect && sizeSelect.value !== pageSizeRaw) sizeSelect.value = pageSizeRaw;

  const indicator = document.querySelector('[data-target-table-page="' + tbl.id + '"]');
  if (indicator) indicator.textContent = 'Page ' + window_.page + ' of ' + window_.totalPages;

  const prevBtn = document.querySelector('.table-page-prev-btn[data-target-table="' + tbl.id + '"]');
  if (prevBtn) prevBtn.disabled = window_.page <= 1;

  const nextBtn = document.querySelector('.table-page-next-btn[data-target-table="' + tbl.id + '"]');
  if (nextBtn) nextBtn.disabled = window_.page >= window_.totalPages;
}

// A large table (m4-payload-plan.md, D1a): its rows stay as data in `model.rows`, `order` is their current sort order and the DOM holds only the
// page on screen. Filtering matches the same text a row would show, built once on first use.
export function attachPagedTable(tableEl, tbody, model) {
  tableEl.__paged = { model: model, tbody: tbody, order: null, searchText: null };
  tableEl.__applyManagedState = function () { applyManagedTableState(tableEl); };
  tableEl.__applyManagedState();
}

function pagedOrder(paged) {
  if (!paged.order) {
    const rows = paged.model.rows;
    paged.order = new Array(rows.length);
    for (let i = 0; i < rows.length; i++) paged.order[i] = i;
  }
  return paged.order;
}

function pagedSearchText(paged) {
  if (!paged.searchText) {
    const model = paged.model;
    paged.searchText = model.rows.map(function (row) {
      const cells = Array.isArray(row) ? row : (row && Array.isArray(row.cells) ? row.cells : []);
      let text = '';
      for (let c = 0; c < cells.length; c++) text += model.cellText(cells[c], model.headerMeta[c] || null);
      return text.toLowerCase();
    });
  }
  return paged.searchText;
}

// Whether any row of a large table contains the text, for a search that the page on screen cannot answer.
// The rows an export covers: every row matching the table's filter, in its current order, as cell text, not just the page on screen.
function matchedRowsAsText(tbl) {
  const input = document.querySelector('.table-filter-input[data-target-table="' + tbl.id + '"]');
  const query = input ? input.value.trim().toLowerCase() : '';
  const out = [];
  if (tbl.__paged) {
    const paged = tbl.__paged, model = paged.model;
    const searchText = query ? pagedSearchText(paged) : null;
    const order = pagedOrder(paged);
    for (let i = 0; i < order.length; i++) {
      if (searchText && !searchText[order[i]].includes(query)) continue;
      const row = model.rows[order[i]];
      const cells = Array.isArray(row) ? row : (row && Array.isArray(row.cells) ? row.cells : []);
      out.push(cells.map(function (cell, c) { return model.cellText(cell, model.headerMeta[c] || null); }));
    }
    return out;
  }
  tbl.querySelectorAll('tbody tr').forEach(function (tr) {
    if (query && !(tr.textContent || '').toLowerCase().includes(query)) return;
    out.push(Array.from(tr.cells).map(function (td) { return td.textContent || ''; }));
  });
  return out;
}

// Cells are text a person or a dump chose, so one that a spreadsheet would read as a formula is written as text.
function csvCell(text) {
  let value = String(text == null ? '' : text);
  if (/^[=@\t\r]/.test(value) || /^[+-](?![\d.])/.test(value)) value = "'" + value;
  return '"' + value.replace(/"/g, '""') + '"';
}

function exportTableCsv(tbl, title) {
  const headers = Array.from(tbl.querySelectorAll('thead th')).map(function (th) { return th.textContent || ''; });
  const lines = [headers.map(csvCell).join(',')];
  const rows = matchedRowsAsText(tbl);
  for (let i = 0; i < rows.length; i++) lines.push(rows[i].map(csvCell).join(','));
  const blob = new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = (title || 'table').replace(/[^A-Za-z0-9._-]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 80) + '.csv';
  a.click();
  URL.revokeObjectURL(a.href);
  return rows.length;
}

export function pagedTableMatches(tbl, query) {
  if (!tbl.__paged) return false;
  const searchText = pagedSearchText(tbl.__paged);
  for (let i = 0; i < searchText.length; i++) if (searchText[i].includes(query)) return true;
  return false;
}

function applyPagedTableState(tbl) {
  const paged = tbl.__paged;
  const pageSizeRaw = tbl.dataset.pageSize || '20';
  const pageSize = pageSizeRaw === 'all' ? 0 : Number(pageSizeRaw);
  const input = document.querySelector('.table-filter-input[data-target-table="' + tbl.id + '"]');
  const query = input ? input.value.trim().toLowerCase() : '';

  let matched = pagedOrder(paged);
  if (query) {
    const searchText = pagedSearchText(paged);
    const filtered = [];
    for (let i = 0; i < matched.length; i++) if (searchText[matched[i]].includes(query)) filtered.push(matched[i]);
    matched = filtered;
  }

  const window_ = pageWindow(tbl, matched.length, pageSize);
  const fragment = document.createDocumentFragment();
  const rows = paged.model.rows;
  for (let i = window_.start; i < Math.min(window_.end, matched.length); i++) fragment.appendChild(paged.model.buildRow(rows[matched[i]], matched[i]));
  paged.tbody.replaceChildren(fragment);
  updatePagerUi(tbl, matched.length, window_, query, pageSizeRaw);
  applyTableCellClamp(tbl);
}

// Sorts a large table over its data. A column with any number sorts as numbers (largest first on the first click), otherwise as text;
// cells with no value go last either way. Returns the direction used.
function sortPagedTable(tbl, column, direction) {
  const paged = tbl.__paged;
  const model = paged.model;
  const rows = model.rows;
  const keys = new Array(rows.length);
  let numericColumn = false;
  for (let i = 0; i < rows.length; i++) {
    const row = rows[i];
    const cells = Array.isArray(row) ? row : (row && Array.isArray(row.cells) ? row.cells : []);
    const key = model.sortValue(cells[column], model.headerMeta[column] || null);
    keys[i] = key;
    if (typeof key === 'number') numericColumn = true;
  }
  if (numericColumn) for (let i = 0; i < keys.length; i++) if (typeof keys[i] !== 'number') keys[i] = null;
  const dir = direction === 0 ? (numericColumn ? -1 : 1) : direction;

  const order = pagedOrder(paged);
  order.sort(function (a, b) {
    const ka = keys[a], kb = keys[b];
    if (ka === null || kb === null) return ka === kb ? a - b : (ka === null ? 1 : -1);
    if (ka === kb) return a - b;
    return dir * (ka < kb ? -1 : 1);
  });
  tbl.dataset.page = '1';
  applyPagedTableState(tbl);
  return dir;
}

function setupSortableTables(scope) {
  (scope || document).querySelectorAll('table').forEach(function (tbl) {
    if (tbl.dataset.sortBound === '1') return;
    tbl.dataset.sortBound = '1';
    const parseSortableNumber = function (cell) {
      if (!cell) return NaN;

      const raw = cell.dataset && cell.dataset.value;
      if (raw !== undefined && raw !== null && raw !== '') {
        const n = Number(String(raw).replace(/,/g, '').trim());
        if (!Number.isNaN(n)) return n;
      }

      const text = (cell.textContent || '').trim();

      // Parse byte values like "1.2 GB", "850 KB", or "42 B".
      const bytesMatch = text.match(/^([+-]?\d[\d,]*(?:\.\d+)?)\s*(B|KB|MB|GB|TB|PB|EB)$/i);
      if (bytesMatch) {
        const value = Number(bytesMatch[1].replace(/,/g, ''));
        if (!Number.isNaN(value)) {
          const unit = bytesMatch[2].toUpperCase();
          const power = unit === 'B' ? 0 :
            unit === 'KB' ? 1 :
            unit === 'MB' ? 2 :
            unit === 'GB' ? 3 :
            unit === 'TB' ? 4 :
            unit === 'PB' ? 5 : 6;
          return value * Math.pow(1024, power);
        }
      }

      // Parse plain numeric text like "12,345", "-10", "42.5", or "87%".
      if (/^[+-]?\d[\d,]*(?:\.\d+)?%?$/.test(text)) {
        const n = Number(text.replace(/,/g, '').replace(/%$/, ''));
        if (!Number.isNaN(n)) return n;
      }

      return NaN;
    };

    const ths = tbl.querySelectorAll('thead th');
    ths.forEach(function (th, col) {
      th.classList.add('sortable');
      th.setAttribute('tabindex', '0');
      let dir = 0;
      function doSort() {
        if (tbl.__paged) {
          dir = sortPagedTable(tbl, col, dir);
          ths.forEach(function (h) { h.removeAttribute('aria-sort'); });
          th.setAttribute('aria-sort', dir > 0 ? 'ascending' : 'descending');
          dir = -dir;
          return;
        }
        const tb = tbl.querySelector('tbody'); if (!tb) return;
        const rows = Array.from(tb.querySelectorAll('tr'));
        if (dir === 0) {
          let numericColumn = false;
          for (let i = 0; i < rows.length; i++) {
            const n = parseSortableNumber(rows[i].cells[col]);
            if (!isNaN(n)) { numericColumn = true; break; }
          }
          dir = numericColumn ? -1 : 1;
        }
        rows.sort(function (a, b) {
          const ac = a.cells[col], bc = b.cells[col];
          const av = parseSortableNumber(ac);
          const bv = parseSortableNumber(bc);
          if (!isNaN(av) && !isNaN(bv)) return dir * (av - bv);
          const at = (ac ? ac.textContent : '').toLowerCase();
          const bt = (bc ? bc.textContent : '').toLowerCase();
          return dir * (at < bt ? -1 : at > bt ? 1 : 0);
        });
        rows.forEach(function (r) { tb.appendChild(r); });
        if (typeof tbl.__applyManagedState === 'function') tbl.__applyManagedState();
        ths.forEach(function (h) { h.removeAttribute('aria-sort'); });
        th.setAttribute('aria-sort', dir > 0 ? 'ascending' : 'descending');
        dir = -dir;
      }
      th.addEventListener('click', doSort);
      th.addEventListener('keydown', function (e) { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); doSort(); } });
    });
  });
}

function initDetailTable(tbl) {
  if (!tbl.__applyManagedState) tbl.__applyManagedState = function () { applyManagedTableState(tbl); };
  applyManagedTableState(tbl);
}

function tableFor(element, selector) {
  const control = element && element.closest ? element.closest(selector) : null;
  const tableId = control ? control.getAttribute('data-target-table') : null;
  const tbl = tableId ? document.getElementById(tableId) : null;
  return control && tbl ? { control: control, tbl: tbl } : null;
}

export function setupDetailTables() {
  document.querySelectorAll('table.detail-filterable-table').forEach(initDetailTable);
  applyTableCellClamp(document);

  // One listener per kind of control on the document, so a table built later (a lazily opened section) works without being bound.
  document.addEventListener('input', function (e) {
    const hit = tableFor(e.target, '.table-filter-input[data-target-table]');
    if (!hit) return;
    hit.tbl.dataset.page = '1';
    applyManagedTableState(hit.tbl);
  });

  document.addEventListener('change', function (e) {
    const hit = tableFor(e.target, '.table-page-size-select[data-target-table]');
    if (!hit) return;
    hit.tbl.dataset.pageSize = hit.control.value;
    hit.tbl.dataset.page = '1';
    applyManagedTableState(hit.tbl);
  });

  document.addEventListener('click', function (e) {
    const exportHit = tableFor(e.target, '.table-export-csv[data-target-table]');
    if (exportHit) {
      // A large table that was never opened has no data attached yet.
      if (typeof exportHit.tbl.__hydrateRows === 'function' && exportHit.tbl.dataset.hydrated !== '1') exportHit.tbl.__hydrateRows();
      exportTableCsv(exportHit.tbl, exportHit.control.getAttribute('data-title'));
      return;
    }
    const prev = tableFor(e.target, '.table-page-prev-btn[data-target-table]');
    if (prev) {
      prev.tbl.dataset.page = String(Math.max(1, Number(prev.tbl.dataset.page || '1') - 1));
      applyManagedTableState(prev.tbl);
      return;
    }
    const next = tableFor(e.target, '.table-page-next-btn[data-target-table]');
    if (next) {
      next.tbl.dataset.page = String(Number(next.tbl.dataset.page || '1') + 1);
      applyManagedTableState(next.tbl);
    }
  });

  document.addEventListener('dumpdetective:section-body-rendered', function (ev) {
    const wrapper = ev.detail && ev.detail.wrapper;
    if (!wrapper) return;
    wrapper.querySelectorAll('table.detail-filterable-table').forEach(function (tbl) { if (!tbl.__paged && !(tbl.dataset.lazyHydrate === '1' && tbl.dataset.hydrated !== '1')) initDetailTable(tbl); });
    applyTableCellClamp(wrapper);
    setupSortableTables(wrapper);
  });

  setupSortableTables();
}
