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
  const limit = Number(tbl.dataset.limit || '0');
  const showAll = tbl.dataset.showAll === '1';
  const input = document.querySelector('.table-filter-input[data-target-table="' + tbl.id + '"]');
  const query = input ? input.value.trim().toLowerCase() : '';
  const rows = Array.from(tbl.querySelectorAll('tbody tr'));
  let matched = 0;
  let visible = 0;
  for (let i = 0; i < rows.length; i++) {
    const row = rows[i];
    const text = (row.textContent || '').toLowerCase();
    const isMatch = !query || text.includes(query);
    if (!isMatch) {
      row.hidden = true;
      continue;
    }
    matched++;
    if (!showAll && limit > 0 && matched > limit) {
      row.hidden = true;
    } else {
      row.hidden = false;
      visible++;
    }
  }

  const count = document.querySelector('[data-target-table-count="' + tbl.id + '"]');
  if (count) {
    count.textContent = query ? (visible + ' of ' + matched + ' matching rows') : (visible + ' rows shown');
  }

  const btn = document.querySelector('.table-show-all-btn[data-target-table="' + tbl.id + '"]');
  if (btn) {
    if (showAll) {
      btn.textContent = 'Show top ' + limit + ' rows';
    } else {
      const labelCount = query ? matched : rows.length;
      btn.textContent = 'Show all ' + labelCount + ' rows';
    }
    btn.disabled = limit <= 0 || matched <= limit;
  }

  applyTableCellClamp(tbl);
}

function setupSortableTables() {
  document.querySelectorAll('table').forEach(function (tbl) {
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

export function setupDetailTables() {
  document.querySelectorAll('table.detail-filterable-table').forEach(function (tbl) {
    tbl.__applyManagedState = function () { applyManagedTableState(tbl); };
    applyManagedTableState(tbl);
  });

  applyTableCellClamp(document);

  document.querySelectorAll('.table-filter-input[data-target-table]').forEach(function (input) {
    input.addEventListener('input', function () {
      const tableId = input.getAttribute('data-target-table');
      const tbl = tableId ? document.getElementById(tableId) : null;
      applyManagedTableState(tbl);
    });
  });

  document.querySelectorAll('.table-show-all-btn[data-target-table]').forEach(function (btn) {
    btn.addEventListener('click', function () {
      const tableId = btn.getAttribute('data-target-table');
      const tbl = tableId ? document.getElementById(tableId) : null;
      if (!tbl) return;
      tbl.dataset.showAll = tbl.dataset.showAll === '1' ? '0' : '1';
      applyManagedTableState(tbl);
    });
  });

  setupSortableTables();
}
