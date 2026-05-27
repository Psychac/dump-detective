// Block and table rendering primitives — renderBlocks, buildDetailTable.
// buildChartBlock is defined in report.renderers.charts.js; in the inline bundle
// it is available via hoisting. In ES module mode it would need a separate import.
import { el, t, wrapAddresses, indentClass } from './report.dom.js';

export function renderBlocks(blocks, container, announce) {
  if (!blocks || !blocks.length) return;
  const stack = [container];
  if (container && !container._headingCounter) container._headingCounter = 0;
  if (container && !container._collapseCounter) container._collapseCounter = 0;
  // Pre-build sparkline registry so __SPARKREF__ table cells can look up inline data
  const sparkRegistry = new Map();
  for (const b of blocks) { if (b && b.type === 'sparkline' && b.metricKey) sparkRegistry.set(b.metricKey, b); }
  for (const block of blocks) {
    const top = stack[stack.length - 1];
    switch (block.type) {
      case 'heading': {
        const d = el('div', 'detail-subheading' + indentClass(block.indentLevel || 0));
        d.textContent = block.text || '';
        try {
          const sidx = container && container.dataset && container.dataset.sectionIndex;
          if (sidx != null) {
            const counter = (container._headingCounter = (container._headingCounter || 0) + 1) - 1;
            d.id = `detail-${String(sidx)}-heading-${counter}`;
          }
        } catch (e) { }
        top.appendChild(d);
        break;
      }
      case 'metric': {
        const d = el('div', 'detail-line' + indentClass(block.indentLevel || 0));
        const k = el('span', 'detail-key'); k.textContent = (block.label || '') + ':';
        const v = el('span', 'detail-value wrap'); v.textContent = block.value || '';
        d.appendChild(k); d.appendChild(t(' ')); d.appendChild(v);
        top.appendChild(d);
        wrapAddresses(d);
        break;
      }
      case 'path': {
        const d = el('div', 'detail-line' + indentClass(block.indentLevel || 0));
        const k = el('span', 'detail-key'); k.textContent = (block.label || '') + ':';
        const v = el('span', 'detail-path wrap'); v.textContent = block.path || '';
        d.appendChild(k); d.appendChild(t(' ')); d.appendChild(v);
        top.appendChild(d);
        wrapAddresses(d);
        break;
      }
      case 'stackframe': {
        const d = el('div', 'detail-line' + indentClass(block.indentLevel || 0));
        const span = el('span', block.isFrameworkFrame ? 'detail-frame framework' : 'detail-frame');
        span.textContent = block.frame || '';
        d.appendChild(span);
        top.appendChild(d);
        wrapAddresses(d);
        break;
      }
      case 'text': {
        const d = el('div', 'detail-line' + indentClass(block.indentLevel || 0));
        d.textContent = block.text || '';
        top.appendChild(d);
        wrapAddresses(d);
        break;
      }
      case 'listItem': {
        const d = el('div', 'detail-line' + indentClass(block.indentLevel || 0));
        d.textContent = '\u2022 ' + (block.text || '');
        top.appendChild(d);
        break;
      }
      case 'divider': {
        top.appendChild(el('div', 'detail-divider'));
        break;
      }
      case 'blank': {
        top.appendChild(el('div', 'detail-gap'));
        break;
      }
      case 'table': {
        top.appendChild(buildDetailTable(block, announce, sparkRegistry));
        break;
      }
      case 'sparkline': {
        top.appendChild(buildSparklineBlock(block));
        break;
      }
      case 'chart': {
        // buildChartBlock is defined in report.renderers.charts.js — available
        // via hoisting in the inline bundle.
        top.appendChild(buildChartBlock(block));
        break;
      }
      case 'collapsibleBegin': {
        const details = el('details', 'detail-nested');
        try {
          const sidx = container && container.dataset && container.dataset.sectionIndex;
          if (sidx != null) {
            const counter = (container._collapseCounter = (container._collapseCounter || 0) + 1) - 1;
            details.id = `detail-${String(sidx)}-collapse-${counter}`;
          }
        } catch (e) { }
        const summary = el('summary'); summary.textContent = block.title || '';
        details.appendChild(summary);
        const content = el('div', 'detail-nested-content');
        details.appendChild(content);
        top.appendChild(details);
        stack.push(content);
        break;
      }
      case 'collapsibleEnd': {
        if (stack.length > 1) stack.pop();
        break;
      }
      case 'confidenceBand': {
        const d = el('div', 'confidence-band confidence-band--' + (block.band || 'medium').toLowerCase().replace('-', ''));
        const sym = el('span', 'confidence-band__symbol'); sym.textContent = block.symbol || '';
        const lbl = el('span', 'confidence-band__band'); lbl.textContent = block.band || '';
        d.appendChild(sym); d.appendChild(document.createTextNode('\u2002')); d.appendChild(lbl);
        if (block.score != null) {
          const sc = el('span', 'confidence-band__score'); sc.textContent = '\u2002' + (Number(block.score) * 100).toFixed(0) + '%'; d.appendChild(sc);
        }
        if (block.caveats && block.caveats.length) {
          for (const cav of block.caveats) { const c = el('div', 'confidence-band__caveat'); c.textContent = '\u26a0 ' + cav; d.appendChild(c); }
        }
        top.appendChild(d);
        break;
      }
      default:
        break;
    }
  }
}

// ── Inline sparkline helpers (used by renderBlocks and buildDetailTable) ─────

function buildSparklineSvg(values, direction) {
  const w = 84, h = 20, pad = 2;
  const nums = (values || []).map(function (v) { return (v != null && isFinite(Number(v))) ? Number(v) : NaN; });
  const valid = nums.filter(function (n) { return !Number.isNaN(n); });
  if (!valid.length) return null;
  const min = Math.min.apply(null, valid), max = Math.max.apply(null, valid);
  const range = max - min || 1;
  const points = [];
  for (let i = 0; i < nums.length; i++) {
    const v = nums[i];
    const x = pad + (i * (w - pad*2) / Math.max(1, nums.length - 1));
    const y = Number.isNaN(v) ? h - pad : pad + (1 - (v - min) / range) * (h - pad*2);
    points.push(x.toFixed(1) + ',' + y.toFixed(1));
  }
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', '0 0 ' + w + ' ' + h);
  svg.classList.add('sparkline'); svg.style.width = '6.5em'; svg.style.height = '1.6em'; svg.setAttribute('role', 'img');
  const poly = document.createElementNS(ns, 'polyline');
  poly.setAttribute('fill', 'none'); poly.setAttribute('stroke-width', '1.5');
  let trend = 'flat';
  if (valid.length >= 2) { const diff = valid[valid.length - 1] - valid[0]; trend = Math.abs(diff) < Math.max(1e-6, Math.abs(valid[0]) * 0.005) ? 'flat' : diff > 0 ? 'up' : 'down'; }
  const dir = (direction || 'Neutral').toLowerCase();
  const higherWorse = dir === 'higherisworse' || dir === 'higherworse';
  const lowerWorse  = dir === 'lowerisworse'  || dir === 'lowerworse';
  const strokeColor = trend === 'flat' ? '#6b7280' : trend === 'up' ? (higherWorse ? '#b91c1c' : '#059669') : (lowerWorse ? '#b91c1c' : '#059669');
  poly.setAttribute('stroke', strokeColor); poly.setAttribute('points', points.join(' '));
  svg.appendChild(poly);
  const titleEl = document.createElementNS(ns, 'title');
  titleEl.textContent = 'min: ' + valid[0] + '  max: ' + valid[valid.length - 1] + '  latest: ' + valid[valid.length - 1];
  svg.appendChild(titleEl);
  return svg;
}

function buildSparklineBlock(block) {
  const wrap = el('div', 'detail-sparkline-wrap');
  if (block.metricKey) {
    const lbl = el('span', 'detail-key'); lbl.textContent = block.metricKey + (block.unit ? ' (' + block.unit + ')' : '') + ':'; wrap.appendChild(lbl);
  }
  const svg = buildSparklineSvg(block.values || [], block.direction || 'Neutral');
  if (svg) wrap.appendChild(svg);
  return wrap;
}

// ── Paginated table ───────────────────────────────────────────────────────────
// renderSparklines is defined in report.renderers.charts.js; available in the
// inline bundle via hoisting.

export function buildDetailTable(block, announce, sparkRegistry) {
  const container = el('div', 'table-with-pagination');
  const tbl = el('table');
  if (block.caption) { const cap = document.createElement('caption'); cap.textContent = block.caption; tbl.appendChild(cap); }
  const thead = el('thead'); const htr = el('tr');
  for (const h of (block.headers || [])) { const th = document.createElement('th'); th.scope = 'col'; th.textContent = h; htr.appendChild(th); }
  thead.appendChild(htr); tbl.appendChild(thead);
  const tbody = el('tbody');

  const rowElements = [];
  for (const row of (block.rows || [])) {
    const tr = el('tr');
    const cells = Array.isArray(row)
      ? row
      : (row && Array.isArray(row.cells) ? row.cells : []);
    for (const cell of cells) {
      if (!cell) {
        tr.appendChild(document.createElement('td'));
        continue;
      }
      const td = document.createElement('td');
      const disp = cell.display || '';
      if (disp.startsWith('__SPARK__')) {
        const payload = disp.substring('__SPARK__'.length);
        td.setAttribute('data-sparkline', payload);
      } else if (disp.startsWith('__SPARKREF__')) {
        const key = disp.substring('__SPARKREF__'.length);
        const sparkBlock = sparkRegistry && sparkRegistry.get(key);
        if (sparkBlock) {
          const svg = buildSparklineSvg(sparkBlock.values || [], sparkBlock.direction || 'Neutral');
          if (svg) td.appendChild(svg);
        } else {
          td.textContent = '∼';
        }
      } else if (cell.linkTarget) {
        td.textContent = disp;
        const a = document.createElement('a'); a.className = 'trend-jump'; a.href = '#' + cell.linkTarget;
        a.setAttribute('aria-label', 'Jump to section'); a.textContent = ' ↳'; td.appendChild(a);
      } else {
        const linkMarker = '||__LINK__';
        const li = disp.indexOf(linkMarker);
        if (li >= 0) {
          const left = disp.substring(0, li);
          const target = disp.substring(li + linkMarker.length);
          td.textContent = left;
          const a = document.createElement('a'); a.className = 'trend-jump'; a.href = '#' + target; a.setAttribute('aria-label', 'Jump to snapshot'); a.textContent = ' ↳'; td.appendChild(a);
        } else {
          td.textContent = disp;
        }
      }
      if (cell.rawValue != null) td.dataset.value = cell.rawValue;
      wrapAddresses(td);
      tr.appendChild(td);
    }
    rowElements.push(tr);
  }

  tbl.appendChild(tbody);

  const controls = el('div', 'table-pagination-controls'); controls.setAttribute('role', 'group'); controls.setAttribute('aria-label', 'Table pagination');
  const prev = el('button', 'action-btn table-prev'); prev.type = 'button'; prev.textContent = '\u2190 Prev'; prev.setAttribute('aria-label', 'Previous rows');
  const next = el('button', 'action-btn table-next'); next.type = 'button'; next.textContent = 'Next \u2192'; next.setAttribute('aria-label', 'Next rows');
  const info = el('span', 'page-info');
  const sizeSel = document.createElement('select'); sizeSel.setAttribute('aria-label', 'Rows per page');
  [[10,'10'],[20,'20'],[50,'50'],[0,'All']].forEach(function (opt) { const o = document.createElement('option'); o.value = String(opt[0]); o.text = String(opt[1]); sizeSel.appendChild(o); });
  controls.appendChild(prev); controls.appendChild(info); controls.appendChild(next); controls.appendChild(t(' ')); controls.appendChild(sizeSel);

  let pageSize = 10; let pageIndex = 0;
  function renderTablePage() {
    tbody.innerHTML = '';
    const total = rowElements.length;
    const start = pageSize === 0 ? 0 : pageIndex * pageSize;
    const end = pageSize === 0 ? total : Math.min(total, start + pageSize);
    for (let i = start; i < end; i++) tbody.appendChild(rowElements[i]);
    info.textContent = pageSize === 0 ? `${total} rows` : `${start + 1}-${end} of ${total}`;
    prev.disabled = (pageIndex === 0) || (pageSize === 0);
    next.disabled = (end >= total) || (pageSize === 0);
    renderSparklines();
    controls.style.display = (total <= pageSize || pageSize === 0) ? 'none' : '';
    if (pageSize !== 0 && announce) announce(`Showing ${start + 1} to ${end} of ${total} rows`);
  }
  prev.addEventListener('click', function () { if (pageSize === 0) return; if (pageIndex > 0) { pageIndex--; renderTablePage(); } });
  next.addEventListener('click', function () { if (pageSize === 0) return; pageIndex++; renderTablePage(); });
  sizeSel.addEventListener('change', function () { pageSize = parseInt(sizeSel.value, 10) || 0; pageIndex = 0; renderTablePage(); });
  sizeSel.value = String(pageSize);

  container.appendChild(controls); container.appendChild(tbl);
  renderTablePage();
  if (rowElements.length <= pageSize || pageSize === 0) controls.style.display = rowElements.length <= pageSize ? 'none' : '';
  return container;
}
