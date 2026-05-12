import { el, t, sevCss, formatBytes, wrapAddresses, linkifyAnchors, indentClass } from './report.dom.js';

export function renderBlocks(blocks, container, announce) {
  if (!blocks || !blocks.length) return;
  const stack = [container];
  if (container && !container._headingCounter) container._headingCounter = 0;
  if (container && !container._collapseCounter) container._collapseCounter = 0;
  for (const block of blocks) {
    const top = stack[stack.length - 1];
    switch (block.type) {
      case 'heading': {
        const d = el('div', 'detail-subheading' + indentClass(block.indentLevel || 0));
        d.textContent = block.text || '';
        try {
          const sidx = container && container.dataset && container.dataset.sectionIndex;
          if (sidx != null) {
            const idx = Number(sidx);
            const counter = (container._headingCounter = (container._headingCounter || 0) + 1) - 1;
            d.id = `detail-${idx}-heading-${counter}`;
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
        top.appendChild(buildDetailTable(block, announce));
        break;
      }
      case 'chart': {
        top.appendChild(buildChartBlock(block));
        break;
      }
      case 'collapsibleBegin': {
        const details = el('details', 'detail-nested');
        try {
          const sidx = container && container.dataset && container.dataset.sectionIndex;
          if (sidx != null) {
            const idx = Number(sidx);
            const counter = (container._collapseCounter = (container._collapseCounter || 0) + 1) - 1;
            details.id = `detail-${idx}-collapse-${counter}`;
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
      default:
        break;
    }
  }
}

export function buildDetailTable(block, announce) {
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
    for (const cell of (row.cells || [])) {
      const td = document.createElement('td');
      const disp = cell.display || '';
      if (disp.startsWith('__SPARK__')) {
        const payload = disp.substring('__SPARK__'.length);
        td.setAttribute('data-sparkline', payload);
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
  const prev = el('button', 'action-btn table-prev'); prev.type = 'button'; prev.textContent = '← Prev'; prev.setAttribute('aria-label', 'Previous rows');
  const next = el('button', 'action-btn table-next'); next.type = 'button'; next.textContent = 'Next →'; next.setAttribute('aria-label', 'Next rows');
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

export function renderSparklines() {
  const tds = document.querySelectorAll('td[data-sparkline]');
  for (const td of tds) {
    try {
      const payload = JSON.parse(td.getAttribute('data-sparkline'));
      const values = (payload && payload.values) || [];
      const w = 84, h = 20, pad = 2;
      const nums = values.map(v => isFinite(v) ? v : NaN);
      const valid = nums.filter(n => !Number.isNaN(n));
      const min = valid.length ? Math.min(...valid) : 0;
      const max = valid.length ? Math.max(...valid) : 1;
      const range = max - min || 1;
      const points = [];
      for (let i = 0; i < nums.length; i++) {
        const v = nums[i];
        const x = pad + (i * (w - pad*2) / Math.max(1, nums.length - 1));
        const y = Number.isNaN(v) ? h - pad : pad + (1 - (v - min) / range) * (h - pad*2);
        points.push([x.toFixed(1), y.toFixed(1)].join(','));
      }
      const ns = 'http://www.w3.org/2000/svg';
      const svg = document.createElementNS(ns, 'svg');
      svg.setAttribute('viewBox', `0 0 ${w} ${h}`);
      svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
      svg.classList.add('sparkline'); svg.style.width = '6.5em'; svg.style.height = '1.6em'; svg.setAttribute('role', 'img');
      const poly = document.createElementNS(ns, 'polyline'); poly.setAttribute('fill', 'none'); poly.setAttribute('stroke-width', '1.5'); poly.setAttribute('points', points.join(' '));
      let trend = 'flat';
      if (valid.length >= 2) {
        const first = valid[0]; const last = valid[valid.length - 1]; const diff = last - first;
        if (Math.abs(diff) < Math.max(1e-6, Math.abs(first) * 0.005)) trend = 'flat'; else trend = diff > 0 ? 'up' : 'down';
      }
      const strokeColor = (payload && payload.color) || (trend === 'up' ? '#059669' : (trend === 'down' ? '#b91c1c' : '#6b7280'));
      poly.setAttribute('stroke', strokeColor);
      svg.appendChild(poly);
      const minVal = (payload && payload.min != null) ? payload.min : (valid.length ? min : null);
      const maxVal = (payload && payload.max != null) ? payload.max : (valid.length ? max : null);
      const latest = (payload && payload.latest != null) ? payload.latest : (valid.length ? valid[valid.length - 1] : null);
      const tooltipParts = [];
      if (minVal != null) tooltipParts.push('min: ' + String(minVal));
      if (maxVal != null) tooltipParts.push('max: ' + String(maxVal));
      if (latest != null) tooltipParts.push('latest: ' + String(latest));
      const title = document.createElementNS(ns, 'title'); title.textContent = tooltipParts.join('; '); svg.appendChild(title);
      td.textContent = ''; td.appendChild(svg);
    } catch (e) { }
  }
}

export function renderCharts() {
  const blocks = document.querySelectorAll('.detail-chart[data-chart-kind][data-chart-payload]');
  for (const block of blocks) {
    try {
      const kind = (block.getAttribute('data-chart-kind') || '').toLowerCase();
      const payload = JSON.parse(block.getAttribute('data-chart-payload') || '{}');
      block.replaceChildren(buildChartSvg(kind, payload));
    } catch (e) { }
  }
}

function buildChartBlock(block) {
  const wrap = el('div', 'detail-chart');
  wrap.dataset.chartKind = block.kind || '';
  wrap.dataset.chartPayload = block.payloadJson || '{}';
  wrap.dataset.chartTitle = block.title || '';
  const title = el('div', 'detail-chart__title');
  title.textContent = block.title || '';
  wrap.appendChild(title);
  return wrap;
}

function buildChartSvg(kind, payload) {
  if (kind === 'treemap') return buildTreemapChart(payload);
  if (kind === 'heatmap') return buildHeatmapChart(payload);
  if (kind === 'waterfall') return buildWaterfallChart(payload);
  return buildPieChart(payload);
}

function svgEl(name, cls) {
  const ns = 'http://www.w3.org/2000/svg';
  const node = document.createElementNS(ns, name);
  if (cls) node.setAttribute('class', cls);
  return node;
}

function renderSvgText(svg, x, y, text, cls) {
  const node = svgEl('text', cls || '');
  node.setAttribute('x', String(x));
  node.setAttribute('y', String(y));
  node.textContent = text;
  svg.appendChild(node);
  return node;
}

function palette(i) {
  const colors = ['#2563eb', '#0f766e', '#7c3aed', '#d97706', '#dc2626', '#0891b2', '#16a34a', '#db2777', '#475569', '#8b5cf6'];
  return colors[i % colors.length];
}

function normalizeItems(payload) {
  const items = Array.isArray(payload.items) ? payload.items : Array.isArray(payload.segments) ? payload.segments : Array.isArray(payload.steps) ? payload.steps : [];
  return items.map(function (item, index) {
    const value = Number(item.value ?? item.bytes ?? item.current ?? item.amount ?? 0);
    return {
      label: String(item.label ?? item.name ?? item.title ?? `Item ${index + 1}`),
      value: isFinite(value) ? value : 0,
      color: item.color || palette(index)
    };
  });
}

function buildBaseChart(title) {
  const wrap = el('div', 'detail-chart__frame');
  if (title) {
    const caption = el('div', 'detail-chart__caption');
    caption.textContent = title;
    wrap.appendChild(caption);
  }
  return wrap;
}

function buildPieChart(payload) {
  const wrap = buildBaseChart(payload.title || 'Chart');
  const items = normalizeItems(payload)
    .filter(item => item.value > 0)
    .sort((a, b) => b.value - a.value);
  const chartItems = items.length > 5
    ? items.slice(0, 5).concat([{ label: 'Other', value: items.slice(5).reduce((sum, item) => sum + item.value, 0), color: '#cbd5e1' }])
    : items;
  const total = chartItems.reduce((sum, item) => sum + item.value, 0) || 1;
  const svg = svgEl('svg', 'detail-chart__svg');
  svg.setAttribute('viewBox', '0 0 280 170');
  svg.setAttribute('role', 'img');
  const cx = 72, cy = 82, r = 52;
  const legendX = 146;
  let start = -Math.PI / 2;
  chartItems.forEach(function (item) {
    const angle = (item.value / total) * Math.PI * 2;
    const end = start + angle;
    const x1 = cx + Math.cos(start) * r;
    const y1 = cy + Math.sin(start) * r;
    const x2 = cx + Math.cos(end) * r;
    const y2 = cy + Math.sin(end) * r;
    const large = angle > Math.PI ? 1 : 0;
    const path = svgEl('path');
    path.setAttribute('d', `M ${cx} ${cy} L ${x1} ${y1} A ${r} ${r} 0 ${large} 1 ${x2} ${y2} Z`);
    path.setAttribute('fill', item.color);
    path.setAttribute('opacity', '0.92');
    svg.appendChild(path);
    start = end;
  });
  const hole = svgEl('circle');
  hole.setAttribute('cx', String(cx));
  hole.setAttribute('cy', String(cy));
  hole.setAttribute('r', '33');
  hole.setAttribute('fill', '#ffffff');
  svg.appendChild(hole);
  renderSvgText(svg, cx, cy - 4, formatBytesChart(total), 'detail-chart__center-label');
  renderSvgText(svg, cx, cy + 12, `${chartItems.length} groups`, 'detail-chart__center-subtitle');
  chartItems.slice(0, 5).forEach(function (item, index) {
    const y = 28 + index * 24;
    const swatch = svgEl('rect');
    swatch.setAttribute('x', String(legendX));
    swatch.setAttribute('y', String(y - 10));
    swatch.setAttribute('width', '9');
    swatch.setAttribute('height', '9');
    swatch.setAttribute('rx', '2');
    swatch.setAttribute('fill', item.color);
    svg.appendChild(swatch);
    renderSvgText(svg, legendX + 16, y, item.label, 'detail-chart__legend-label');
    renderSvgText(svg, 270, y, formatChartValue(item.value, total), 'detail-chart__legend-value');
  });
  wrap.appendChild(svg);
  return wrap;
}

function buildTreemapChart(payload) {
  const wrap = buildBaseChart(payload.title || 'Chart');
  const items = normalizeItems(payload)
    .filter(item => item.value > 0)
    .sort((a, b) => b.value - a.value)
    .slice(0, 8);
  const svg = svgEl('svg', 'detail-chart__svg');
  svg.setAttribute('viewBox', '0 0 280 160');
  svg.setAttribute('role', 'img');
  const total = items.reduce((sum, item) => sum + item.value, 0) || 1;
  let x = 8;
  let y = 8;
  let rowH = 32;
  for (let i = 0; i < items.length; i++) {
    const item = items[i];
    const width = Math.max(54, Math.round((item.value / total) * 264 * 1.7));
    if (x + width > 272) {
      x = 8;
      y += rowH + 6;
      rowH = 32;
    }
    if (y > 126) break;
    const rect = svgEl('rect');
    rect.setAttribute('x', String(x));
    rect.setAttribute('y', String(y));
    rect.setAttribute('width', String(Math.min(width, 264 - x)));
    rect.setAttribute('height', String(rowH));
    rect.setAttribute('rx', '5');
    rect.setAttribute('fill', item.color);
    rect.setAttribute('opacity', '0.92');
    svg.appendChild(rect);
    renderSvgText(svg, x + 6, y + 12, truncateLabel(item.label, 18), 'detail-chart__tile-label');
    renderSvgText(svg, x + 6, y + 24, formatBytesChart(item.value), 'detail-chart__tile-value');
    x += Math.min(width, 264 - x) + 6;
  }
  wrap.appendChild(svg);
  return wrap;
}

function buildHeatmapChart(payload) {
  const wrap = buildBaseChart(payload.title || 'Chart');
  const items = normalizeItems(payload).filter(item => item.value >= 0).slice(0, 8);
  const svg = svgEl('svg', 'detail-chart__svg');
  svg.setAttribute('viewBox', '0 0 280 160');
  svg.setAttribute('role', 'img');
  const cols = Math.max(2, Math.min(4, Math.ceil(Math.sqrt(items.length))));
  const rows = Math.max(1, Math.ceil(items.length / cols));
  const cellW = 260 / cols;
  const cellH = 96 / rows;
  const max = Math.max(...items.map(item => item.value), 1);
  items.forEach(function (item, index) {
    const col = index % cols;
    const row = Math.floor(index / cols);
    const x = 8 + col * cellW;
    const y = 10 + row * cellH;
    const strength = item.value / max;
    const rect = svgEl('rect');
    rect.setAttribute('x', String(x + 3));
    rect.setAttribute('y', String(y + 3));
    rect.setAttribute('width', String(Math.max(18, cellW - 8)));
    rect.setAttribute('height', String(Math.max(20, cellH - 8)));
    rect.setAttribute('rx', '6');
    rect.setAttribute('fill', heatColor(strength));
    rect.setAttribute('opacity', '0.96');
    svg.appendChild(rect);
    renderSvgText(svg, x + 10, y + 18, truncateLabel(item.label, 16), 'detail-chart__tile-label detail-chart__tile-label--light');
    renderSvgText(svg, x + 10, y + 31, formatPercent(item.value), 'detail-chart__tile-value detail-chart__tile-value--light');
  });
  renderSvgText(svg, 44, 146, 'low', 'detail-chart__axis-value');
  renderSvgText(svg, 140, 146, 'fragmentation', 'detail-chart__center-subtitle');
  renderSvgText(svg, 236, 146, 'high', 'detail-chart__axis-value');
  wrap.appendChild(svg);
  return wrap;
}

function buildWaterfallChart(payload) {
  const wrap = buildBaseChart(payload.title || 'Chart');
  const items = normalizeItems(payload);
  const svg = svgEl('svg', 'detail-chart__svg');
  svg.setAttribute('viewBox', '0 0 280 160');
  svg.setAttribute('role', 'img');
  const maxAbs = Math.max(1, ...items.map(item => Math.abs(item.value)));
  const baseline = 94;
  const barW = 42;
  const gap = 10;
  let x = 12;
  const total = items.reduce((sum, item) => sum + item.value, 0);
  renderSvgText(svg, 140, 16, `Net ${formatCountChart(total)}`, 'detail-chart__center-label');
  renderSvgText(svg, 140, 30, 'new / resolved / regressions / improvements', 'detail-chart__center-subtitle');
  items.forEach(function (item, index) {
    const value = item.value;
    const height = Math.max(8, Math.round(Math.abs(value) / maxAbs * 54));
    const y = value >= 0 ? baseline - height : baseline;
    const rect = svgEl('rect');
    rect.setAttribute('x', String(x));
    rect.setAttribute('y', String(y));
    rect.setAttribute('width', String(barW));
    rect.setAttribute('height', String(height));
    rect.setAttribute('rx', '6');
    rect.setAttribute('fill', value >= 0 ? '#2563eb' : '#16a34a');
    svg.appendChild(rect);
    renderSvgText(svg, x + 1, 144, truncateLabel(item.label, 9), 'detail-chart__axis-label');
    renderSvgText(svg, x + 1, value >= 0 ? y - 4 : y + height + 11, formatCountChart(value), 'detail-chart__axis-value');
    x += barW + gap;
  });
  const axis = svgEl('line');
  axis.setAttribute('x1', '10');
  axis.setAttribute('x2', '270');
  axis.setAttribute('y1', String(baseline));
  axis.setAttribute('y2', String(baseline));
  axis.setAttribute('stroke', '#cbd5e1');
  axis.setAttribute('stroke-width', '1');
  svg.insertBefore(axis, svg.firstChild);
  wrap.appendChild(svg);
  return wrap;
}

function heatColor(strength) {
  const clamped = Math.max(0, Math.min(1, strength));
  const r = Math.round(244 - clamped * 120);
  const g = Math.round(114 - clamped * 60);
  const b = Math.round(182 - clamped * 80);
  return `rgb(${r},${g},${b})`;
}

function formatBytesChart(value) {
  const units = ['B', 'KB', 'MB', 'GB'];
  let n = Math.abs(Number(value) || 0);
  let i = 0;
  while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
  return i === 0 ? `${Math.round(n)} B` : `${n.toFixed(1)} ${units[i]}`;
}

function formatPercent(value) {
  const num = Number(value) || 0;
  return `${num.toFixed(1)}%`;
}

function formatChartValue(value, total) {
  const pct = total > 0 ? (value * 100 / total).toFixed(1) : '0.0';
  return `${formatBytesChart(value)} (${pct}%)`;
}

function formatCountChart(value) {
  const num = Number(value) || 0;
  return `${num >= 0 ? '+' : '-'}${Math.abs(Math.round(num)).toLocaleString('en-US')}`;
}

function truncateLabel(value, maxLength) {
  const text = String(value || '');
  return text.length > maxLength ? text.slice(0, Math.max(0, maxLength - 1)) + '…' : text;
}

export function buildHeader(doc) {
  const isTrend = !!doc.isTrendReport;
  const title = isTrend ? 'DumpDetective Trend Analysis Report' : 'DumpDetective Analysis Report';
  const dumpLabel = isTrend ? 'Latest dump' : 'Dump';
  const rawName = (doc.dumpPath || 'report').replace(/\\/g, '/').split('/').pop() || 'report';
  const exportName = rawName.replace(/\.[^.]+$/, '') || 'report';
  const sec = el('section', 'header-card'); const h1 = document.createElement('h1'); h1.textContent = title; sec.appendChild(h1);
  function metaItem(label, value, extraClass) { const d = el('div', 'meta-item' + (extraClass ? ' ' + extraClass : '')); const s = el('span', 'meta-label'); s.textContent = label + ':'; d.appendChild(s); d.appendChild(t(' ' + value)); return d; }
  const mainRow = el('div', 'meta-grid meta-grid-main');
  mainRow.appendChild(metaItem(dumpLabel, doc.dumpPath || '', 'meta-item--full'));
  sec.appendChild(mainRow);

  const grid = el('div', 'meta-grid meta-grid-secondary');
  const genRaw = doc.generatedAtUtc; const genStr = genRaw ? (new Date(genRaw)).toISOString().replace('T', ' ').slice(0, 19) + ' UTC' : '';
  grid.appendChild(metaItem('Generated (UTC)', genStr)); grid.appendChild(metaItem('Elapsed', ((doc.elapsedSeconds) || 0).toFixed(1) + 's')); grid.appendChild(metaItem('Schema', doc.schemaVersion || ''));
  sec.appendChild(grid);
  // dedup diagnostics removed
  if (isTrend) { const td = el('div', 'dedup-note'); td.textContent = 'Dumps analyzed: ' + (doc.trendDumpCount || 0); sec.appendChild(td); if (doc.trendDumpPaths && doc.trendDumpPaths.length) { const dp = el('div', 'dedup-note'); const strong = document.createElement('strong'); strong.textContent = 'Analyzed dumps:'; dp.appendChild(strong); for (const p of doc.trendDumpPaths) { dp.appendChild(document.createElement('br')); dp.appendChild(t('\u2022 ' + p)); } sec.appendChild(dp); } }
  const bar = el('div', 'action-bar'); bar.setAttribute('role', 'toolbar'); bar.setAttribute('aria-label', 'Report actions');
  function actionBtn(id, ariaLabel, txt) { const btn = el('button', 'action-btn'); btn.type = 'button'; btn.id = id; btn.dataset.filename = exportName; btn.setAttribute('aria-label', ariaLabel); btn.textContent = txt; return btn; }
  bar.appendChild(actionBtn('btn-download-json', 'Download report as JSON', '\u2B07 JSON'));
  bar.appendChild(actionBtn('btn-export-csv', 'Export findings as CSV', '\u2B07 CSV'));
  bar.appendChild(actionBtn('btn-print', 'Print this report', '\u2399 Print'));
  bar.appendChild(actionBtn('btn-toggle-contrast', 'Toggle high contrast mode', '\u263C Contrast'));
  sec.appendChild(bar);
  return sec;
}

export function buildIncidentContext(doc) {
  const ctx = doc.incidentContext; if (!ctx) return null;
  const sec = el('section', 'section-card incident-context-card');
  const h2 = document.createElement('h2'); h2.textContent = 'Incident Context'; sec.appendChild(h2);
  const subtitle = document.createElement('p'); subtitle.className = 'incident-context__subtitle'; subtitle.textContent = 'Runtime settings and snapshot context used to generate this report.'; sec.appendChild(subtitle);

  const summary = el('div', 'incident-context__summary');
  function stat(label, value) {
    const item = el('div', 'incident-context__stat');
    const statLabel = el('div', 'incident-context__stat-label'); statLabel.textContent = label;
    const statValue = el('div', 'incident-context__stat-value'); statValue.textContent = value || '—';
    item.appendChild(statLabel); item.appendChild(statValue); summary.appendChild(item);
  }
  stat('Mode', ctx.mode || '');
  stat('Report', ((ctx.reportFormat || '') + ' / ' + (ctx.reportAudience || '')).trim().replace(/^\s*\/\s*|\s*\/\s*$/g, '').replace(/^\s*$/, '—'));
  stat('Runtime', ((ctx.runtimeFlavor || 'n/a') + (ctx.runtimeVersion ? ' ' + ctx.runtimeVersion : '')).trim());
  stat('GC Mode', ctx.gcMode || 'n/a');
  stat('Heap Count', ctx.heapCount != null ? String(ctx.heapCount) : 'n/a');
  stat('Active Analyzers', String(ctx.activeAnalyzerCount || 0));
  sec.appendChild(summary);

  const details = el('div', 'incident-context__details');
  function detail(label, value) {
    const item = el('div', 'incident-context__detail');
    const detailLabel = el('div', 'incident-context__detail-label'); detailLabel.textContent = label;
    const detailValue = el('div', 'incident-context__detail-value'); detailValue.textContent = value || '—';
    item.appendChild(detailLabel); item.appendChild(detailValue); details.appendChild(item);
  }
  detail('Dump Path', ctx.dumpPath || '');
  if (ctx.baselineDumpPath) detail('Baseline Dump', ctx.baselineDumpPath);
  detail('Config', (ctx.usedConfigFile ? 'config file' : 'command line') + (ctx.configPath ? ' (' + ctx.configPath + ')' : ''));
  detail('Diagnostic Mode', ctx.diagnosticMode ? 'on' : 'off');
  detail('Index Prebuild', ctx.indexPrebuildMode || '');
  detail('Heap Walkable', ctx.heapCanWalk ? 'yes' : 'no');
  detail('Analysis Elapsed', (Number(ctx.analysisElapsedSeconds || 0)).toFixed(1) + 's');
  sec.appendChild(details);

  if (ctx.trendSnapshots && ctx.trendSnapshots.length) {
    const h3 = document.createElement('h3'); h3.textContent = 'Snapshot Contexts'; sec.appendChild(h3);
    const snaps = el('div', 'incident-context__snapshots');
    for (const snap of ctx.trendSnapshots) {
      const card = el('div', 'incident-context__snapshot');
      const label = snap.isBaseline ? 'Baseline' : (snap.isCurrent ? 'Current' : ('Snapshot ' + (snap.index + 1)));
      const title = el('div', 'incident-context__snapshot-title'); title.textContent = label;
      const path = el('div', 'incident-context__snapshot-path'); path.textContent = snap.dumpPath || '—';
      const meta = el('div', 'incident-context__snapshot-meta'); meta.textContent = (Number(snap.elapsedSeconds || 0)).toFixed(1) + 's • ' + String(snap.analyzerCount || 0) + ' analyzers • ' + String(snap.findingCount || 0) + ' findings';
      card.appendChild(title); card.appendChild(path); card.appendChild(meta); snaps.appendChild(card);
    }
    sec.appendChild(snaps);
  }
  return sec;
}

export function buildDevActionPlan(doc) {
  const plan = doc.developerActionPlan; if (!plan || !plan.length) return null; const sec = el('section', 'section-card'); const h2 = document.createElement('h2'); h2.textContent = 'Developer Action Plan'; sec.appendChild(h2); const tbl = el('table'); const thead = el('thead'); const htr = el('tr'); for (const col of ['Priority', 'Title', 'Action', 'Impact']) { const th = document.createElement('th'); th.scope = 'col'; th.textContent = col; htr.appendChild(th); } thead.appendChild(htr); tbl.appendChild(thead); const tbody = el('tbody'); for (const action of plan) { const tr = el('tr'); [action.priority, action.title, action.action, action.impact].forEach(function (v, i) { const td = document.createElement('td'); if (i >= 2) td.className = 'wrap'; td.textContent = v || ''; tr.appendChild(td); }); tbody.appendChild(tr); } tbl.appendChild(tbody); sec.appendChild(tbl); return sec; }

export function buildFilterBar(doc) {
  const findings = doc.findings || []; if (!findings.length) return null; let crit = 0, warn = 0, info = 0; for (const f of findings) { const s = (f.severity || '').toLowerCase(); if (s === 'critical') crit++; else if (s === 'warning') warn++; else info++; }
  const bar = el('div', 'filter-bar'); bar.id = 'filter-bar'; bar.setAttribute('role', 'search'); bar.setAttribute('aria-label', 'Filter findings'); const group = el('div', 'filter-group'); group.setAttribute('aria-label', 'Severity filter'); function fbtn(sev, label, extra) { const btn = el('button', 'filter-btn' + (extra ? ' ' + extra : '') + (sev === 'all' ? ' active' : '')); btn.type = 'button'; btn.dataset.sev = sev; btn.setAttribute('aria-pressed', sev === 'all' ? 'true' : 'false'); btn.textContent = label; return btn; }
  group.appendChild(fbtn('all', 'All (' + findings.length + ')')); if (crit) group.appendChild(fbtn('critical', 'Critical (' + crit + ')', 'filter-critical')); if (warn) group.appendChild(fbtn('warning', 'Warning (' + warn + ')', 'filter-warning')); if (info) group.appendChild(fbtn('info', 'Info (' + info + ')', 'filter-info'));
  bar.appendChild(group);
  const search = document.createElement('input'); search.type = 'search'; search.id = 'filter-search'; search.className = 'filter-search'; search.placeholder = 'Search findings\u2026'; search.setAttribute('aria-label', 'Search findings by title or evidence'); bar.appendChild(search);
  const count = el('span', 'filter-count'); count.id = 'filter-count'; count.setAttribute('aria-live', 'polite'); count.setAttribute('aria-atomic', 'true'); bar.appendChild(count);
  return bar;
}

export function buildTOC(doc) {
  const sections = doc.analyzerSections || [];
  if (!sections || !sections.length) return null;

  const nav = document.getElementById('toc') || el('nav', 'toc');
  nav.className = 'toc report-navbar__toc';
  nav.setAttribute('aria-label', 'Report sections');
  const existingTitle = nav.querySelector('.toc-title');
  if (existingTitle) existingTitle.remove();

  const container = el('div', 'toc-section');
  for (let i = 0; i < sections.length; i++) {
    const sec = sections[i];
    const det = document.createElement('details');
    det.open = false;
    det.dataset.target = '#detail-' + i;

    const summ = document.createElement('summary');
    summ.textContent = sec.displayTitle || sec.analyzerName || ('Section ' + i);
    det.appendChild(summ);

    det.addEventListener('toggle', function () {
      if (!det.open)
        return;

      const target = document.getElementById(det.dataset.target ? det.dataset.target.substring(1) : '');
      if (!target)
        return;

      try {
        target.scrollIntoView({ behavior: 'smooth', block: 'start' });
        history.replaceState(null, '', det.dataset.target || '#');
      } catch (e) { }
    });

    const tocNodes = buildTocNodes(sec.blocks || [], i);
    if (tocNodes.length)
      det.appendChild(renderTocNodes(tocNodes));

    container.appendChild(det);
  }

  nav.replaceChildren(container);
  return nav;
}

function buildTocNodes(blocks, sectionIndex) {
  const root = [];
  const contextStack = [{ nodes: root, headingStack: [] }];
  let headingIndex = 0;
  let collapseIndex = 0;

  for (const block of blocks) {
    if (!block)
      continue;

    if (block.type === 'collapsibleBegin') {
      const current = contextStack[contextStack.length - 1];
      const node = createTocNode(block.title || 'Narrative', `#detail-${sectionIndex}-collapse-${collapseIndex++}`);
      current.nodes.push(node);
      contextStack.push({ nodes: node.children, headingStack: [] });
      continue;
    }

    if (block.type === 'collapsibleEnd') {
      if (contextStack.length > 1)
        contextStack.pop();
      continue;
    }

    if (block.type !== 'heading')
      continue;

    const current = contextStack[contextStack.length - 1];
    const level = Math.max(0, block.indentLevel || 0);
    while (current.headingStack.length > level)
      current.headingStack.pop();

    let parentNode = null;
    if (level > 0)
      parentNode = current.headingStack[level - 1] || current.headingStack[current.headingStack.length - 1] || null;

    const node = createTocNode(block.text || (`Heading ${headingIndex + 1}`), `#detail-${sectionIndex}-heading-${headingIndex++}`);
    if (parentNode)
      parentNode.children.push(node);
    else
      current.nodes.push(node);

    current.headingStack[level] = node;
    current.headingStack.length = level + 1;
  }

  return root;
}

function createTocNode(text, href) {
  return { text, href, children: [] };
}

function renderTocNodes(nodes) {
  const list = document.createElement('ol');
  for (const node of nodes) {
    const li = document.createElement('li');
    const a = document.createElement('a');
    a.href = node.href;
    a.textContent = node.text || '';
    li.appendChild(a);
    if (node.children.length)
      li.appendChild(renderTocNodes(node.children));
    list.appendChild(li);
  }
  return list;
}

export function buildFindingCard(f, i) {
  const sec = el('section', 'section-card finding-card'); sec.id = 'finding-' + i; sec.dataset.severity = (f.severity || 'info').toLowerCase(); sec.dataset.title = f.title || ''; sec.dataset.summary = (f.evidence || '').substring(0, 200);
  const header = el('div', 'finding-card__header');
  const eyebrow = el('div', 'finding-card__eyebrow');
  const badge = el('span', 'severity-badge ' + sevCss(f.severity)); badge.textContent = f.severity || 'Info'; eyebrow.appendChild(badge);
  const cat = el('span', 'category'); cat.textContent = f.category || 'Finding'; eyebrow.appendChild(cat);
  header.appendChild(eyebrow);
  const actions = el('div', 'finding-card__actions');
  const pa = document.createElement('a'); pa.className = 'permalink'; pa.href = '#finding-' + i; pa.setAttribute('aria-label', 'Permalink'); pa.textContent = '🔗';
  const copyBtn = el('button', 'copy-btn'); copyBtn.type = 'button'; copyBtn.setAttribute('aria-label', 'Copy permalink'); copyBtn.title = 'Copy permalink'; copyBtn.dataset.copy = (location.href || '').split('#')[0] + '#finding-' + i; copyBtn.textContent = '\u2398';
  actions.appendChild(pa); actions.appendChild(copyBtn); header.appendChild(actions); sec.appendChild(header);

  const h2 = document.createElement('h2'); h2.className = 'finding-card__title'; h2.textContent = f.title || '';
  sec.appendChild(h2);

  const summary = document.createElement('p'); summary.className = 'finding-card__summary'; summary.textContent = f.evidence || ''; sec.appendChild(summary); linkifyAnchors(summary);

  const details = el('div', 'finding-card__details');
  function detailField(label, value) {
    const field = el('div', 'finding-card__field');
    const fieldLabel = el('div', 'finding-card__field-label'); fieldLabel.textContent = label;
    const fieldValue = el('div', 'finding-card__field-value'); fieldValue.textContent = value || '—'; wrapAddresses(fieldValue); linkifyAnchors(fieldValue);
    field.appendChild(fieldLabel); field.appendChild(fieldValue); details.appendChild(field);
  }
  if (f.cause) detailField('Cause', f.cause);
  if (f.effect) detailField('Effect', f.effect);
  if (f.confidenceScore != null) detailField('Confidence', Number(f.confidenceScore).toFixed(2));
  if (f.suggestedOwner) detailField('Owner', f.suggestedOwner);
  if (f.effort) detailField('Effort', f.effort);
  if (f.validationStep) detailField('Validation', f.validationStep);
  if (f.trackingStatus) detailField('Status', f.trackingStatus);
  detailField('Evidence', f.evidence || '');
  if (f.fix) detailField('Fix', f.fix);
  if (f.recommendation) detailField('Recommendation', f.recommendation);
  sec.appendChild(details);
  return sec;
}

export function buildConfidenceNotes(doc) {
  const notes = doc.confidence; if (!notes || !notes.length) return null; const sec = el('section', 'section-card'); const h2 = document.createElement('h2'); h2.textContent = 'Confidence Notes'; sec.appendChild(h2); const ul = document.createElement('ul'); for (const note of notes) { const li = document.createElement('li'); const strong = document.createElement('strong'); strong.textContent = '[' + note.analyzer + ']'; li.appendChild(strong); li.appendChild(t(' ' + note.reason)); ul.appendChild(li); } sec.appendChild(ul); return sec; }

export function buildAnalyzerSection(section, i) {
  const wrapper = el('section', 'analyzer-section detail-color-' + (i % 6)); wrapper.id = 'detail-' + i; const details = el('details'); const summaryEl = el('summary'); summaryEl.id = 'detail-' + i + '-summary';
  const title = el('span', 'detail-summary__title'); title.textContent = section.displayTitle || section.analyzerName || '';
  const blocks = section.blocks || [];
  let headingCount = 0;
  for (const block of blocks) { if (block && block.type === 'heading') headingCount++; }
  const meta = el('span', 'detail-summary__meta'); meta.textContent = headingCount > 0 ? (headingCount + ' insights') : (blocks.length + ' blocks');
  summaryEl.appendChild(title); summaryEl.appendChild(meta); details.appendChild(summaryEl);
  const content = el('div', 'detail-block'); content.setAttribute('role', 'region'); content.setAttribute('aria-labelledby', summaryEl.id); content.dataset.sectionIndex = String(i); renderBlocks(section.blocks || [], content); details.appendChild(content); wrapper.appendChild(details);
  const pa = document.createElement('a'); pa.className = 'permalink'; pa.href = '#detail-' + i; pa.setAttribute('aria-label', 'Permalink'); pa.textContent = '🔗'; summaryEl.appendChild(t(' ')); summaryEl.appendChild(pa);
  const copyBtn = el('button', 'copy-btn'); copyBtn.type = 'button'; copyBtn.setAttribute('aria-label', 'Copy permalink'); copyBtn.title = 'Copy permalink'; copyBtn.dataset.copy = (location.href || '').split('#')[0] + '#detail-' + i; copyBtn.textContent = '\u2398'; summaryEl.appendChild(copyBtn);
  return wrapper;
}

export function renderFindingsPaged(doc, announce) {
  const findings = doc.findings || []; if (!findings.length) return null; const container = el('div', 'findings-paged');
  const header = el('div', 'findings-paged__header');
  const title = document.createElement('h2'); title.textContent = 'Findings';
  header.appendChild(title);

  const controls = el('div', 'pagination-controls'); controls.setAttribute('role', 'region'); controls.setAttribute('aria-label', 'Findings pagination'); const prevBtn = el('button', 'action-btn findings-prev'); prevBtn.type = 'button'; prevBtn.setAttribute('aria-label', 'Previous page'); prevBtn.textContent = '← Prev'; const nextBtn = el('button', 'action-btn findings-next'); nextBtn.type = 'button'; nextBtn.setAttribute('aria-label', 'Next page'); nextBtn.textContent = 'Next →'; const pageInfo = el('span', 'page-info'); const sizeSel = document.createElement('select'); sizeSel.setAttribute('aria-label', 'Findings per page'); [[10,'10'],[20,'20'],[50,'50'],[100,'100'],[0,'All']].forEach(function (opt) { const o = document.createElement('option'); o.value = String(opt[0]); o.text = String(opt[1]); sizeSel.appendChild(o); }); controls.appendChild(prevBtn); controls.appendChild(pageInfo); controls.appendChild(nextBtn); controls.appendChild(t(' ')); controls.appendChild(sizeSel);
  const list = el('div', 'findings-list'); list.setAttribute('role', 'list'); let pageSize = 10; let pageIndex = 0; function renderPage() { list.innerHTML = ''; const total = findings.length; const start = pageSize === 0 ? 0 : pageIndex * pageSize; const end = pageSize === 0 ? total : Math.min(total, start + pageSize); for (let i = start; i < end; i++) list.appendChild(buildFindingCard(findings[i], i)); pageInfo.textContent = pageSize === 0 ? `${total} findings` : `${start + 1}-${end} of ${total}`; prevBtn.disabled = (pageIndex === 0) || (pageSize === 0); nextBtn.disabled = (end >= total) || (pageSize === 0); controls.style.display = (total <= pageSize || pageSize === 0) ? 'none' : ''; if (pageSize !== 0 && announce) announce(`Showing ${start + 1} to ${end} of ${total} findings`); }
  prevBtn.addEventListener('click', function () { if (pageSize === 0) return; if (pageIndex > 0) { pageIndex--; renderPage(); } }); nextBtn.addEventListener('click', function () { if (pageSize === 0) return; pageIndex++; renderPage(); }); sizeSel.addEventListener('change', function () { pageSize = parseInt(sizeSel.value, 10) || 0; pageIndex = 0; renderPage(); }); sizeSel.value = String(pageSize); container.appendChild(header); container.appendChild(controls); container.appendChild(list); renderPage(); return container; }
