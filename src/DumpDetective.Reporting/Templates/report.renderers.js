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
          const a = document.createElement('a'); a.className = 'trend-jump'; a.href = '#' + target; a.setAttribute('aria-label', 'Jump to snapshot'); a.textContent = ' â†³'; td.appendChild(a);
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
  const prev = el('button', 'action-btn table-prev'); prev.type = 'button'; prev.textContent = 'â† Prev'; prev.setAttribute('aria-label', 'Previous rows');
  const next = el('button', 'action-btn table-next'); next.type = 'button'; next.textContent = 'Next â†’'; next.setAttribute('aria-label', 'Next rows');
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
  return text.length > maxLength ? text.slice(0, Math.max(0, maxLength - 1)) + 'â€¦' : text;
}

export function buildHeader(doc) {
  const isTrend = !!doc.isTrendReport;
  const title = isTrend ? 'Trend Analysis Report' : 'Analysis Report';
  const dumpLabel = isTrend ? 'Latest dump' : 'Dump';
  const rawName = (doc.dumpPath || 'report').replace(/\\/g, '/').split('/').pop() || 'report';
  const exportName = rawName.replace(/\.[^.]+$/, '') || 'report';

  const sec = el('section', 'header-card');
  sec.id = 'sec-header';

  // â”€â”€ Hero band â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  const hero = el('div', 'header-hero');

  const heroLeft = el('div', 'header-hero__left');
  const badge = el('div', 'header-hero__badge');
  const dot = el('span', 'header-hero__badge-dot'); badge.appendChild(dot); badge.appendChild(t('DumpDetective'));
  heroLeft.appendChild(badge);
  const h1 = document.createElement('h1'); h1.className = 'header-hero__title'; h1.textContent = title; heroLeft.appendChild(h1);
  if (rawName) { const fn = el('div', 'header-hero__filename'); fn.textContent = rawName; heroLeft.appendChild(fn); }
  hero.appendChild(heroLeft);

  const heroActions = el('div', 'header-hero__actions');
  heroActions.setAttribute('role', 'toolbar'); heroActions.setAttribute('aria-label', 'Report actions');
  function heroBtn(id, ariaLabel, txt) { const btn = el('button', 'header-hero__btn'); btn.type = 'button'; btn.id = id; btn.dataset.filename = exportName; btn.setAttribute('aria-label', ariaLabel); btn.textContent = txt; return btn; }
  heroActions.appendChild(heroBtn('btn-download-json', 'Download report as JSON', '\u2B07\u202FJSON'));
  heroActions.appendChild(heroBtn('btn-export-csv', 'Export findings as CSV', '\u2B07\u202FCSV'));
  heroActions.appendChild(heroBtn('btn-print', 'Print this report', '\u2399\u202FPrint'));
  heroActions.appendChild(heroBtn('btn-toggle-contrast', 'Toggle high contrast mode', '\u25D1\u202FContrast'));
  hero.appendChild(heroActions);
  sec.appendChild(hero);

  // â”€â”€ Body â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  const body = el('div', 'header-body');
  const ctx = doc.incidentContext || {};
  const execSum = doc.executiveSummary || {};

  // Path + file size row
  if (doc.dumpPath) {
    const pathRow = el('div', 'header-path');
    const pathLabel = el('span', 'header-path__label'); pathLabel.textContent = dumpLabel + ':'; pathRow.appendChild(pathLabel);
    const pathVal = el('span', 'header-path__value'); pathVal.textContent = doc.dumpPath; pathRow.appendChild(pathVal);
    const fsBytes = ctx.dumpFileSizeBytes != null ? Number(ctx.dumpFileSizeBytes) : null;
    if (fsBytes !== null && fsBytes > 0) {
      const sizeBadge = el('span', 'header-path__size');
      sizeBadge.textContent = formatBytes(fsBytes) + (ctx.dumpSizeTierLabel ? '\u2002\u00B7\u2002' + ctx.dumpSizeTierLabel : '');
      pathRow.appendChild(sizeBadge);
    }
    body.appendChild(pathRow);
  }

  function statItem(label, value) { const d = el('div', 'header-stat'); const l = el('span', 'header-stat__label'); l.textContent = label; const v = el('span', 'header-stat__value'); v.textContent = value; d.appendChild(l); d.appendChild(v); return d; }
  function statRow(groupLabel, items) {
    if (!items.length) return null;
    const row = el('div', 'header-meta-row');
    const badge = el('span', 'header-meta-row__group'); badge.textContent = groupLabel; row.appendChild(badge);
    for (const [lbl, val] of items) row.appendChild(statItem(lbl, val));
    return row;
  }

  // Row 1 â€” Analysis run
  const genRaw = doc.generatedAtUtc; const genStr = genRaw ? (new Date(genRaw)).toISOString().replace('T', ' ').slice(0, 19) + ' UTC' : '';
  const runItems = [];
  if (genStr) runItems.push(['Analyzed at', genStr]);
  runItems.push(['Elapsed', ((doc.elapsedSeconds) || 0).toFixed(1) + 's']);
  if (doc.schemaVersion) runItems.push(['Schema', doc.schemaVersion]);
  if (doc.analyzerVersion) runItems.push(['Version', doc.analyzerVersion]);
  const runRow = statRow('Analysis run', runItems); if (runRow) body.appendChild(runRow);

  // Row 2 â€” Runtime environment
  const rtItems = [];
  if (ctx.runtimeVersion || ctx.runtimeFlavor) { const rv = [ctx.runtimeVersion, ctx.runtimeFlavor].filter(Boolean).join(' / '); rtItems.push(['Runtime', rv]); }
  if (ctx.gcMode) rtItems.push(['GC mode', ctx.gcMode]);
  if (ctx.heapCount != null) rtItems.push(['Logical heaps', String(ctx.heapCount)]);
  if (ctx.heapCanWalk != null) rtItems.push(['Heap walkable', ctx.heapCanWalk ? 'Yes' : 'No']);
  const rtRow = statRow('Runtime', rtItems); if (rtRow) body.appendChild(rtRow);

  // Row 3 â€” Managed heap snapshot
  const heapItems = [];
  if (execSum.totalManagedBytes != null) heapItems.push(['Total managed', formatBytes(Number(execSum.totalManagedBytes || 0))]);
  if (execSum.totalObjects != null) heapItems.push(['Objects', Number(execSum.totalObjects).toLocaleString('en-US')]);
  if (execSum.uniqueTypes != null) heapItems.push(['Unique types', Number(execSum.uniqueTypes).toLocaleString('en-US')]);
  const heapRow = statRow('Managed heap', heapItems); if (heapRow) body.appendChild(heapRow);

  // Trend dump list
  if (isTrend) {
    const td = el('div', 'dedup-note'); td.textContent = 'Dumps analyzed: ' + (doc.trendDumpCount || 0); body.appendChild(td);
    if (doc.trendDumpPaths && doc.trendDumpPaths.length) { const dp = el('div', 'dedup-note'); const strong = document.createElement('strong'); strong.textContent = 'Analyzed dumps:'; dp.appendChild(strong); for (const p of doc.trendDumpPaths) { dp.appendChild(document.createElement('br')); dp.appendChild(t('\u2022 ' + p)); } body.appendChild(dp); }
  }

  sec.appendChild(body);
  return sec;
}

export function buildHealthScorecard(doc) {
  const scorecard = doc.healthScorecard;
  if (!scorecard || !Array.isArray(scorecard.domains) || !scorecard.domains.length) return null;

  function sevInfo(sev) {
    const s = String(sev).toLowerCase(); const n = Number(sev);
    if (n === 3 || s === 'critical') return { label: 'Critical', css: 'critical', dot: '\u25CF' };
    if (n === 2 || s === 'warning')  return { label: 'Warning',  css: 'warning',  dot: '\u25CF' };
    if (n === 1 || s === 'ok')       return { label: 'OK',       css: 'ok',       dot: '\u2713' };
    return                                   { label: 'Unknown',  css: 'unknown',  dot: '\u25CB' };
  }

  const sec = el('section', 'section-card health-scorecard');
  sec.id = 'sec-health';

  // â”€â”€ Overall banner â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  const overall = sevInfo(scorecard.overallSeverity);
  const banner = el('div', 'health-scorecard__banner health-scorecard__banner--' + overall.css);

  const bannerLeft = el('div', 'health-scorecard__banner-left');
  const bannerTitle = el('span', 'health-scorecard__banner-title'); bannerTitle.textContent = 'Health Summary'; bannerLeft.appendChild(bannerTitle);
  const verdict = el('span', 'health-scorecard__banner-verdict'); verdict.textContent = overall.dot + '\u2002' + overall.label; bannerLeft.appendChild(verdict);
  banner.appendChild(bannerLeft);

  // Aggregate counts across all domains
  const totalCrit = scorecard.domains.reduce((s, d) => s + (d.criticalCount || 0), 0);
  const totalWarn = scorecard.domains.reduce((s, d) => s + (d.warningCount || 0), 0);
  if (totalCrit > 0 || totalWarn > 0) {
    const bannerRight = el('div', 'health-scorecard__banner-right');
    if (totalCrit > 0) {
      const cs = el('div', 'health-scorecard__banner-stat health-scorecard__banner-stat--critical');
      const cl = el('span', 'health-scorecard__banner-stat-label'); cl.textContent = 'Critical'; cs.appendChild(cl);
      const cv = el('span', 'health-scorecard__banner-stat-value'); cv.textContent = String(totalCrit); cs.appendChild(cv);
      bannerRight.appendChild(cs);
    }
    if (totalWarn > 0) {
      const ws = el('div', 'health-scorecard__banner-stat health-scorecard__banner-stat--warning');
      const wl = el('span', 'health-scorecard__banner-stat-label'); wl.textContent = 'Warning'; ws.appendChild(wl);
      const wv = el('span', 'health-scorecard__banner-stat-value'); wv.textContent = String(totalWarn); ws.appendChild(wv);
      bannerRight.appendChild(ws);
    }
    banner.appendChild(bannerRight);
  }
  sec.appendChild(banner);

  // â”€â”€ Domain grid â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  const grid = el('div', 'health-scorecard__grid');
  grid.setAttribute('role', 'list');
  const domainOrder = ['Leaks', 'Memory', 'GC', 'TypeSystem', 'Threads', 'Async', 'Exceptions', 'Runtime'];
  const domainMap = new Map();
  for (const entry of scorecard.domains) domainMap.set((entry.domain || '').toLowerCase(), entry);
  const ordered = [];
  for (const d of domainOrder) { const e = domainMap.get(d.toLowerCase()); if (e) ordered.push(e); }
  for (const [, e] of domainMap) { if (!domainOrder.map(d => d.toLowerCase()).includes((e.domain || '').toLowerCase())) ordered.push(e); }

  for (const entry of ordered) {
    const si = sevInfo(entry.severity);
    const row = el('div', 'health-domain-row health-domain-row--' + si.css);
    row.setAttribute('role', 'listitem');

    const name = el('span', 'health-domain-row__name'); name.textContent = entry.domain || ''; row.appendChild(name);

    const pill = el('span', 'health-domain-row__pill health-domain-row__pill--' + si.css);
    pill.textContent = si.dot + '\u2002' + si.label; row.appendChild(pill);

    const crit = entry.criticalCount || 0; const warn = entry.warningCount || 0;
    if (crit > 0 || warn > 0) {
      const counts = el('span', 'health-domain-row__counts');
      const parts = [];
      if (crit > 0) parts.push(crit + '\u00A0crit');
      if (warn > 0) parts.push(warn + '\u00A0warn');
      counts.textContent = parts.join('\u2002\u00B7\u2002'); row.appendChild(counts);
    }
    grid.appendChild(row);
  }
  sec.appendChild(grid);
  return sec;
}

export function buildExecutiveSummary(doc) {
  const summary = doc.executiveSummary;
  if (!summary) return null;

  const sec = el('section', 'section-card executive-summary');
  sec.id = 'sec-exec';

  // â”€â”€ KPI strip â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  const strip = el('div', 'exec-kpi-strip');

  function kpi(label, value, sev) {
    if (value == null) return null;
    const d = el('div', 'exec-kpi');
    const lbl = el('span', 'exec-kpi__label'); lbl.textContent = label; d.appendChild(lbl);
    const val = el('span', 'exec-kpi__value' + (sev ? ' exec-kpi__value--' + sev : '')); val.textContent = String(value); d.appendChild(val);
    return d;
  }
  function kpiGroup(...items) {
    const g = el('div', 'exec-kpi-group');
    for (const item of items) { if (item) g.appendChild(item); }
    return g.children.length ? g : null;
  }

  // Memory
  const g1 = kpiGroup(
    kpi('Total heap', formatBytes(Number(summary.totalManagedBytes || 0))),
    summary.lohBytes != null ? kpi('LOH', formatBytes(Number(summary.lohBytes)) + (summary.lohPercent != null ? ' (' + Number(summary.lohPercent).toFixed(1) + '%)' : '')) : null,
    summary.gen2Percent != null ? kpi('Gen2', Number(summary.gen2Percent).toFixed(1) + '%', Number(summary.gen2Percent) > 60 ? 'warning' : null) : null
  );
  // GC & Leaks
  const gcPressureVal = summary.gcPressureLevel || (summary.gcPressureScore != null ? summary.gcPressureScore + '/100' : null);
  const g2 = kpiGroup(
    gcPressureVal ? kpi('GC pressure', gcPressureVal, summary.gcPressureScore > 66 ? 'warning' : null) : null,
    summary.leakCandidateCount != null ? kpi('Leak suspects', String(summary.leakCandidateCount), summary.leakCandidateCount > 0 ? 'warning' : 'ok') : null,
    summary.finalizerQueueCount != null ? kpi('Finalizer queue', Number(summary.finalizerQueueCount).toLocaleString('en-US'), summary.finalizerQueueCount > 1000 ? 'warning' : null) : null
  );
  // Threads
  const g3 = kpiGroup(
    summary.blockedThreads != null ? kpi('Blocked threads', String(summary.blockedThreads), summary.blockedThreads > 0 ? 'warning' : 'ok') : null,
    summary.deadlockCycles != null ? kpi('Deadlocks', String(summary.deadlockCycles), summary.deadlockCycles > 0 ? 'critical' : 'ok') : null,
    summary.hangScore != null ? kpi('Hang score', summary.hangScore + '/100', summary.hangScore < 50 ? 'warning' : 'ok') : null
  );
  // Exceptions
  const g4 = kpiGroup(
    summary.activeExceptions != null ? kpi('Active exceptions', String(summary.activeExceptions), summary.activeExceptions > 0 ? 'critical' : 'ok') : null
  );

  for (const g of [g1, g2, g3, g4]) { if (g && g.children.length) strip.appendChild(g); }
  if (strip.children.length) sec.appendChild(strip);

  // â”€â”€ Score triplet â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  function scoreLevel(s) { return s >= 67 ? 'ok' : s >= 34 ? 'warning' : 'critical'; }
  if (summary.leakLikelihoodScore != null || summary.gcPressureScore != null || summary.threadContentionScore != null) {
    const scores = el('div', 'exec-scores');
    function scoreCard(label, sub, score) {
      if (score == null) return;
      const lv = scoreLevel(score);
      const card = el('div', 'exec-score');
      const dial = el('div', 'exec-score__dial exec-score__dial--' + lv); dial.textContent = String(score); card.appendChild(dial);
      const info = el('div', 'exec-score__info');
      const lbl = el('div', 'exec-score__label'); lbl.textContent = label; info.appendChild(lbl);
      const sl = el('div', 'exec-score__sublabel'); sl.textContent = sub; info.appendChild(sl);
      card.appendChild(info);
      scores.appendChild(card);
    }
    scoreCard('Leak Likelihood', 'memory retention risk', summary.leakLikelihoodScore);
    scoreCard('GC Pressure', 'collection burden', summary.gcPressureScore);
    scoreCard('Thread Contention', 'concurrency health', summary.threadContentionScore);
    sec.appendChild(scores);
  }

  // â”€â”€ Findings â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  const critFindings = summary.criticalFindings || [];
  const warnFindings = summary.warningFindings || [];
  if (critFindings.length || warnFindings.length) {
    const findingsWrap = el('div', 'exec-findings');
    appendExecFindingGroup(findingsWrap, 'critical', critFindings);
    appendExecFindingGroup(findingsWrap, 'warning', warnFindings);
    sec.appendChild(findingsWrap);
  }

  // â”€â”€ Recommendations â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  const recs = summary.topRecommendations || [];
  if (recs.length) {
    const recWrap = el('div', 'exec-recommendations');
    const heading = el('div', 'exec-recommendations__heading'); heading.textContent = 'Top recommendations'; recWrap.appendChild(heading);
    const ol = el('ol', 'exec-rec-list');
    for (const finding of recs.slice(0, 3)) {
      const li = el('li', 'exec-rec-item');
      const num = el('span', 'exec-rec-num'); num.textContent = String(ol.children.length + 1); li.appendChild(num);
      const body = el('div', 'exec-rec-body');
      if (finding.title) { const title = el('div', 'exec-rec-title'); title.textContent = finding.title; body.appendChild(title); }
      if (finding.recommendation) { const text = el('div', 'exec-rec-text'); text.textContent = finding.recommendation; body.appendChild(text); }
      li.appendChild(body);
      ol.appendChild(li);
    }
    recWrap.appendChild(ol);
    sec.appendChild(recWrap);
  }

  return sec;
}

function appendExecFindingGroup(parent, sev, findings) {
  if (!findings || !findings.length) return;
  const block = el('div', 'exec-findings-block');
  const header = el('div', 'exec-findings-block__header');
  const badge = el('span', 'exec-findings-block__badge exec-findings-block__badge--' + sev);
  badge.textContent = sev === 'critical' ? 'Critical' : 'Warning'; header.appendChild(badge);
  const count = el('span', 'exec-findings-block__count');
  count.textContent = findings.length + ' finding' + (findings.length !== 1 ? 's' : ''); header.appendChild(count);
  block.appendChild(header);
  for (const f of findings.slice(0, 5)) {
    const row = el('div', 'exec-finding-row exec-finding-row--' + sev);
    const title = el('div', 'exec-finding-row__title'); title.textContent = f.title || ''; row.appendChild(title);
    if (f.evidence) { const ev = el('div', 'exec-finding-row__evidence'); ev.textContent = f.evidence; row.appendChild(ev); }
    if (f.recommendation) { const rec = el('div', 'exec-finding-row__rec'); rec.textContent = '\u2192\u2002' + f.recommendation; row.appendChild(rec); }
    if (f.analyzer) { const meta = el('div', 'exec-finding-row__meta'); meta.textContent = f.analyzer + (f.confidenceScore != null ? '\u2002\u00B7\u2002confidence\u00A0' + Number(f.confidenceScore).toFixed(2) : ''); row.appendChild(meta); }
    block.appendChild(row);
  }
  parent.appendChild(block);
}

function sevRank(sev) {
  if (sev == null) return -1;
  const s = String(sev).toLowerCase();
  if (s === 'critical' || s === '2') return 3;
  if (s === 'warning'  || s === '1') return 2;
  if (s === 'info'     || s === '0') return 1;
  if (s === 'ok') return 1;
  return -1;
}

function domainSevLabel(sev) {
  if (sev == null) return 'Info';
  const s = String(sev).toLowerCase();
  if (s === '2' || s === 'critical') return 'Critical';
  if (s === '1' || s === 'warning')  return 'Warning';
  if (s === '0' || s === 'info')     return 'Info';
  return String(sev).charAt(0).toUpperCase() + String(sev).slice(1).toLowerCase();
}

export function buildDomains(doc) {
  const domains = doc.domains;
  if (!Array.isArray(domains) || !domains.length) return null;

  // Sort domains: Critical first, then Warning, then Info/OK, then Unknown
  const sortedDomains = domains.slice().sort(function (a, b) {
    return sevRank(b.leadSeverity) - sevRank(a.leadSeverity);
  });

  const wrap = el('div', 'report-domains');
  for (let i = 0; i < sortedDomains.length; i++) {
    const domain = sortedDomains[i] || {};
    const domainSev = String(domain.leadSeverity || 'Info').toLowerCase();
    const sec = el('section', 'section-card report-domain report-domain--' + domainSev);
    sec.id = 'domain-' + i;
    sec.dataset.domain = domain.domain || '';
    sec.dataset.leadSeverity = domainSev;

    const hdr = el('div', 'domain-header domain-header--' + domainSev);
    const dot = el('span', 'toc-dot toc-dot--' + domainSev); hdr.appendChild(dot);
    const title = el('span', 'domain-header__name'); title.textContent = domain.domain || 'Domain';
    hdr.appendChild(title);
    const domSevLabel = domainSevLabel(domain.leadSeverity);
    if (domSevLabel !== 'Info') {
      const pill = el('span', 'domain-header__sev domain-header__sev--' + domainSev); pill.textContent = domSevLabel; hdr.appendChild(pill);
    }
    sec.appendChild(hdr);


    const sections = Array.isArray(domain.sections) ? domain.sections.slice().sort(function (a, b) {
      return sevRank(b.leadFinding && b.leadFinding.severity) - sevRank(a.leadFinding && a.leadFinding.severity);
    }) : [];
    if (sections.length) {
      const body = el('div', 'domain-body');
      for (let j = 0; j < sections.length; j++) {
        body.appendChild(buildAnalyzerSection(sections[j], i * 1000 + j));
      }
      sec.appendChild(body);
    }

    const insights = Array.isArray(domain.domainInsights) ? domain.domainInsights : [];
    if (insights.length) {
      const insightsSec = el('section', 'section-card report-domain__insights');
      insightsSec.id = 'domain-' + i + '-insights';
      const h3 = document.createElement('h3');
      h3.textContent = 'Domain Insights';
      insightsSec.appendChild(h3);
      for (let k = 0; k < insights.length; k++) {
        insightsSec.appendChild(buildFindingCard(insights[k], `${i}-insight-${k}`));
      }
      sec.appendChild(insightsSec);
    }

    wrap.appendChild(sec);
  }

  return wrap;
}

export function buildCrossDomainInsights(doc) {
  const findings = Array.isArray(doc.crossDomainInsights) ? doc.crossDomainInsights : [];
  if (!findings.length) return null;

  const sec = el('section', 'section-card cross-domain-insights');
  const hdr = el('div', 'cross-domain-insights__header');
  const title = el('span', 'cross-domain-insights__title'); title.textContent = 'Cross-Domain Insights';
  const cnt = el('span', 'cross-domain-insights__count'); cnt.textContent = findings.length + ' finding' + (findings.length !== 1 ? 's' : '');
  hdr.appendChild(title); hdr.appendChild(cnt);
  sec.appendChild(hdr);

  const list = el('div', 'cross-domain-insights__list');
  for (let i = 0; i < findings.length; i++) {
    list.appendChild(buildFindingCard(findings[i], 'cross-' + i));
  }
  sec.appendChild(list);

  return sec;
}

export function buildAppendix(doc) {
  const appendix = doc.appendix;
  if (!appendix) return null;

  const sec = el('section', 'section-card report-appendix');
  sec.id = 'sec-appendix';

  // â”€â”€ Z1. Analyzer Run Summary â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  const runs = appendix.analyzerRunSummary || [];
  if (runs.length) {
    const panel = document.createElement('details');
    panel.className = 'appendix-panel';
    panel.open = true;
    const summary = document.createElement('summary');

    const titleSpan = el('span', 'appendix-panel__title'); titleSpan.textContent = 'Analyzer Run Summary';
    summary.appendChild(titleSpan);

    // Tally counts
    const tally = { completed: 0, failed: 0, skipped: 0, timedout: 0 };
    for (const r of runs) {
      const s = (r.status || '').toLowerCase();
      if (s === 'completed' || s === 'success') tally.completed++;
      else if (s === 'failed') tally.failed++;
      else if (s.startsWith('skipped')) tally.skipped++;
      else if (s === 'timedout') tally.timedout++;
    }
    const metaSpan = el('span', 'appendix-panel__meta');
    metaSpan.textContent = runs.length + ' analyzers';
    summary.appendChild(metaSpan);
    panel.appendChild(summary);

    const body = el('div', 'appendix-panel__body');

    // Tally chips
    const tallyRow = el('div', 'appendix-tally');
    function chip(count, label, mod) {
      if (!count) return;
      const c = el('span', 'appendix-tally__chip appendix-tally__chip--' + mod);
      c.textContent = count + '\u00A0' + label; tallyRow.appendChild(c);
    }
    chip(tally.completed, 'Completed', 'completed');
    chip(tally.failed,    'Failed',    'failed');
    chip(tally.timedout,  'Timed out', 'timedout');
    chip(tally.skipped,   'Skipped',   'skipped');
    body.appendChild(tallyRow);

    // Per-analyzer rows
    const list = el('div', 'appendix-run-list');
    for (const run of runs) {
      const s = (run.status || 'unknown').toLowerCase();
      // Normalize 'success' â†’ 'completed' for display/styling
      const sNorm = (s === 'success') ? 'completed' : s;
      const row = el('div', 'appendix-run-row appendix-run-row--' + sNorm);

      const name = el('div', 'appendix-run-row__name'); name.textContent = run.analyzerName || ''; row.appendChild(name);

      const stats = el('div', 'appendix-run-row__stats');
      const parts = [];
      if (run.findingCount) parts.push(run.findingCount + '\u00A0findings');
      if (run.objectScanCount) parts.push(Number(run.objectScanCount).toLocaleString('en-US') + '\u00A0objs scanned');
      if (run.cacheHits || run.cacheMisses) parts.push('cache\u00A0' + (run.cacheHits || 0) + '/' + ((run.cacheHits || 0) + (run.cacheMisses || 0)));
      stats.textContent = parts.join('\u2002\u00B7\u2002'); row.appendChild(stats);

      const dur = el('div', 'appendix-run-row__dur');
      dur.textContent = Number(run.durationMs || 0) >= 1000
        ? (Number(run.durationMs) / 1000).toFixed(2) + '\u00A0s'
        : Number(run.durationMs || 0).toFixed(0) + '\u00A0ms';
      row.appendChild(dur);

      const pill = el('span', 'appendix-run-row__pill appendix-run-row__pill--' + sNorm);
      pill.textContent = sNorm.charAt(0).toUpperCase() + sNorm.slice(1); row.appendChild(pill);

      const note = run.errorMessage || run.findingGeneratorError || run.skipReason || '';
      if (note) { const noteEl = el('div', 'appendix-run-row__note'); noteEl.textContent = note; row.appendChild(noteEl); }

      list.appendChild(row);
    }
    body.appendChild(list);
    panel.appendChild(body);
    sec.appendChild(panel);
  }

  // â”€â”€ Z2. Memory Diagnostics â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  const memory = appendix.memoryDiagnostics || [];
  if (memory.length) {
    const panel = document.createElement('details');
    panel.className = 'appendix-panel';
    const summary = document.createElement('summary');
    const titleSpan = el('span', 'appendix-panel__title'); titleSpan.textContent = 'Memory Diagnostics';
    const metaSpan = el('span', 'appendix-panel__meta'); metaSpan.textContent = memory.length + ' analyzers';
    summary.appendChild(titleSpan); summary.appendChild(metaSpan);
    panel.appendChild(summary);

    const body = el('div', 'appendix-panel__body');
    const wrap = el('div', 'detail-block');
    wrap.appendChild(buildSimpleTable(
      ['Analyzer', 'WS Before', 'WS After', 'WS \u0394', 'Heap Before', 'Heap After', 'Heap \u0394'],
      memory.map(r => [
        r.analyzerName || '',
        formatBytes(Number(r.workingSetBefore || 0)),
        formatBytes(Number(r.workingSetAfter || 0)),
        (Number(r.workingSetDelta || 0) > 0 ? '+' : '') + formatBytes(Number(r.workingSetDelta || 0)),
        formatBytes(Number(r.managedHeapBefore || 0)),
        formatBytes(Number(r.managedHeapAfter || 0)),
        (Number(r.managedHeapDelta || 0) > 0 ? '+' : '') + formatBytes(Number(r.managedHeapDelta || 0))
      ])
    ));
    body.appendChild(wrap);
    panel.appendChild(body);
    sec.appendChild(panel);
  }

  // â”€â”€ Z3. Known Limitations â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  const limitations = appendix.knownLimitations || [];
  if (limitations.length) {
    const panel = document.createElement('details');
    panel.className = 'appendix-panel';
    const summary = document.createElement('summary');
    const titleSpan = el('span', 'appendix-panel__title'); titleSpan.textContent = 'Known Limitations';
    const metaSpan = el('span', 'appendix-panel__meta'); metaSpan.textContent = limitations.length + ' notes';
    summary.appendChild(titleSpan); summary.appendChild(metaSpan);
    panel.appendChild(summary);

    const body = el('div', 'appendix-panel__body');
    const ul = el('ul', 'appendix-limitations');
    for (const item of limitations) { const li = document.createElement('li'); li.textContent = item; ul.appendChild(li); }
    body.appendChild(ul);
    panel.appendChild(body);
    sec.appendChild(panel);
  }

  return sec;
}

function buildSimpleTable(headers, rows) {
  const table = el('table');
  const thead = el('thead');
  const htr = el('tr');
  for (const h of headers) {
    const th = document.createElement('th');
    th.scope = 'col';
    th.textContent = h;
    htr.appendChild(th);
  }
  thead.appendChild(htr);
  table.appendChild(thead);

  const tbody = el('tbody');
  for (const row of rows) {
    const tr = el('tr');
    for (const cell of row) tr.appendChild(tdText(String(cell ?? '')));
    tbody.appendChild(tr);
  }
  table.appendChild(tbody);
  return table;
}

function tdText(text) {
  const td = document.createElement('td');
  td.textContent = text;
  return td;
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
    const statValue = el('div', 'incident-context__stat-value'); statValue.textContent = value || 'â€”';
    item.appendChild(statLabel); item.appendChild(statValue); summary.appendChild(item);
  }
  stat('Mode', ctx.mode || '');
  stat('Report', ((ctx.reportFormat || '') + ' / ' + (ctx.reportAudience || '')).trim().replace(/^\s*\/\s*|\s*\/\s*$/g, '').replace(/^\s*$/, 'â€”'));
  stat('Runtime', ((ctx.runtimeFlavor || 'n/a') + (ctx.runtimeVersion ? ' ' + ctx.runtimeVersion : '')).trim());
  stat('GC Mode', ctx.gcMode || 'n/a');
  stat('Heap Count', ctx.heapCount != null ? String(ctx.heapCount) : 'n/a');
  stat('Active Analyzers', String(ctx.activeAnalyzerCount || 0));
  sec.appendChild(summary);

  const details = el('div', 'incident-context__details');
  function detail(label, value) {
    const item = el('div', 'incident-context__detail');
    const detailLabel = el('div', 'incident-context__detail-label'); detailLabel.textContent = label;
    const detailValue = el('div', 'incident-context__detail-value'); detailValue.textContent = value || 'â€”';
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
      const path = el('div', 'incident-context__snapshot-path'); path.textContent = snap.dumpPath || 'â€”';
      const meta = el('div', 'incident-context__snapshot-meta'); meta.textContent = (Number(snap.elapsedSeconds || 0)).toFixed(1) + 's â€¢ ' + String(snap.analyzerCount || 0) + ' analyzers â€¢ ' + String(snap.findingCount || 0) + ' findings';
      card.appendChild(title); card.appendChild(path); card.appendChild(meta); snaps.appendChild(card);
    }
    sec.appendChild(snaps);
  }
  return sec;
}

export function buildDevActionPlan(doc) {
  if (Array.isArray(doc.domains) && doc.domains.length) return null;
  const plan = doc.developerActionPlan;
  if (!plan || !plan.length) return null;
  const sec = el('section', 'section-card');
  const h2 = document.createElement('h2'); h2.textContent = 'Developer Action Plan'; sec.appendChild(h2);
  const tbl = el('table');
  const thead = el('thead'); const htr = el('tr');
  for (const col of ['Priority', 'Title', 'Action', 'Impact']) {
    const th = document.createElement('th'); th.scope = 'col'; th.textContent = col; htr.appendChild(th);
  }
  thead.appendChild(htr); tbl.appendChild(thead);
  const tbody = el('tbody');
  for (const action of plan) {
    const tr = el('tr');
    [action.priority, action.title, action.action, action.impact].forEach(function (v, i) {
      const td = document.createElement('td');
      if (i >= 2) td.className = 'wrap';
      td.textContent = v || '';
      tr.appendChild(td);
    });
    tbody.appendChild(tr);
  }
  tbl.appendChild(tbody); sec.appendChild(tbl);
  return sec;
}

export function buildFilterBar(doc) {
  const findings = doc.findings || [];
  if (!findings.length) return null;
  let crit = 0, warn = 0, info = 0;
  for (const f of findings) {
    const s = (f.severity || '').toLowerCase();
    if (s === 'critical') crit++; else if (s === 'warning') warn++; else info++;
  }
  const bar = el('div', 'filter-bar'); bar.id = 'filter-bar';
  bar.setAttribute('role', 'search'); bar.setAttribute('aria-label', 'Filter findings');
  const group = el('div', 'filter-group'); group.setAttribute('aria-label', 'Severity filter');
  function fbtn(sev, label, extra) {
    const btn = el('button', 'filter-btn' + (extra ? ' ' + extra : '') + (sev === 'all' ? ' active' : ''));
    btn.type = 'button'; btn.dataset.sev = sev;
    btn.setAttribute('aria-pressed', sev === 'all' ? 'true' : 'false');
    btn.textContent = label; return btn;
  }
  group.appendChild(fbtn('all', 'All (' + findings.length + ')'));
  if (crit) group.appendChild(fbtn('critical', 'Critical (' + crit + ')', 'filter-critical'));
  if (warn) group.appendChild(fbtn('warning', 'Warning (' + warn + ')', 'filter-warning'));
  if (info) group.appendChild(fbtn('info', 'Info (' + info + ')', 'filter-info'));
  bar.appendChild(group);
  const search = document.createElement('input'); search.type = 'search'; search.id = 'filter-search';
  search.className = 'filter-search'; search.placeholder = 'Search findings\u2026';
  search.setAttribute('aria-label', 'Search findings by title or evidence');
  bar.appendChild(search);
  const count = el('span', 'filter-count'); count.id = 'filter-count';
  count.setAttribute('aria-live', 'polite'); count.setAttribute('aria-atomic', 'true');
  bar.appendChild(count);
  return bar;
}

export function buildTOC(doc) {
  const rawDomains = Array.isArray(doc.domains) ? doc.domains : [];
  // Sort to match the same order buildDomains renders
  const domains = rawDomains.slice().sort(function (a, b) {
    return sevRank(b.leadSeverity) - sevRank(a.leadSeverity);
  });
  const sections = doc.analyzerSections || [];
  if ((!domains || !domains.length) && (!sections || !sections.length)) return null;

  const nav = document.getElementById('toc') || el('nav', 'toc');
  nav.className = 'toc report-navbar__toc';
  nav.setAttribute('aria-label', 'Report sections');
  const existingTitle = nav.querySelector('.toc-title');
  if (existingTitle) existingTitle.remove();

  // â”€â”€ Shared helper: smooth scroll on details open â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  function attachScrollToggle(det) {
    det.addEventListener('toggle', function () {
      if (!det.open) return;
      const target = document.getElementById(det.dataset.target ? det.dataset.target.substring(1) : '');
      if (!target) return;
      try { target.scrollIntoView({ behavior: 'smooth', block: 'start' }); history.replaceState(null, '', det.dataset.target || '#'); } catch (e) { }
    });
  }

  // â”€â”€ Shared helper: severity dot â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  function sevDot(sev) {
    const s = String(sev == null ? '' : sev).toLowerCase();
    const n = Number(sev);
    if (n === 3 || s === 'critical') return { cls: 'toc-dot--critical', label: 'Critical' };
    if (n === 2 || s === 'warning')  return { cls: 'toc-dot--warning',  label: 'Warning' };
    if (n === 1 || s === 'ok')       return { cls: 'toc-dot--ok',       label: 'OK' };
    return                                   { cls: 'toc-dot--info',     label: 'Info' };
  }

  // â”€â”€ Helper: quick nav link â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  function quickLink(href, text, iconChar) {
    const li = document.createElement('li');
    const a = document.createElement('a');
    a.href = href; a.className = 'toc-quick-link';
    if (iconChar) { const icon = el('span', 'toc-quick-link__icon'); icon.textContent = iconChar; a.appendChild(icon); }
    const span = document.createElement('span'); span.textContent = text; a.appendChild(span);
    li.appendChild(a);
    return li;
  }

  const fragment = document.createDocumentFragment();

  // â”€â”€ 1. Pinned quick-nav section â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  const quickSection = el('div', 'toc-quick-nav');
  const quickLabel = el('div', 'toc-quick-nav__label'); quickLabel.textContent = 'Report'; quickSection.appendChild(quickLabel);
  const quickList = document.createElement('ul'); quickList.className = 'toc-quick-nav__list';
  quickList.appendChild(quickLink('#sec-header',  'Overview',          '\u25CE'));
  if (doc.healthScorecard) quickList.appendChild(quickLink('#sec-health', 'Health Summary', '\u271A'));
  if (doc.executiveSummary) quickList.appendChild(quickLink('#sec-exec',  'Executive Summary', '\u00A7'));
  if (doc.appendix) quickList.appendChild(quickLink('#sec-appendix', 'Appendix', '\u00B6'));
  quickSection.appendChild(quickList);
  fragment.appendChild(quickSection);

  // â”€â”€ 2. Domain / section tree â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  const treeLabel = el('div', 'toc-quick-nav__label');
  treeLabel.textContent = domains.length ? 'Domains' : 'Sections';
  fragment.appendChild(treeLabel);

  const container = el('div', 'toc-section');
  if (domains.length) {
    for (let i = 0; i < domains.length; i++) {
      const domain = domains[i] || {};
      const dot = sevDot(domain.leadSeverity);

      const det = document.createElement('details');
      det.open = false;
      det.dataset.target = '#domain-' + i;

      // Summary: dot + anchor link + chevron (chevron is native on <summary>)
      const summ = document.createElement('summary');
      summ.className = 'toc-domain-summary';

      const dotEl = el('span', 'toc-dot ' + dot.cls);
      dotEl.setAttribute('aria-label', dot.label);
      summ.appendChild(dotEl);

      const domainLink = document.createElement('a');
      domainLink.className = 'toc-domain-summary__link';
      domainLink.href = '#domain-' + i;
      domainLink.textContent = domain.domain || ('Domain ' + i);
      domainLink.addEventListener('click', function (e) {
        e.stopPropagation(); // don't toggle the <details>
      });
      summ.appendChild(domainLink);

      const sectionCount = (Array.isArray(domain.sections) ? domain.sections : []).length;
      if (sectionCount) {
        const cnt = el('span', 'toc-domain-summary__count'); cnt.textContent = String(sectionCount); summ.appendChild(cnt);
      }
      det.appendChild(summ);
      attachScrollToggle(det);

      const list = document.createElement('ol');
      const domainSections = Array.isArray(domain.sections) ? domain.sections.slice().sort(function (a, b) {
        return sevRank(b.leadFinding && b.leadFinding.severity) - sevRank(a.leadFinding && a.leadFinding.severity);
      }) : [];
      for (let j = 0; j < domainSections.length; j++) {
        const li = document.createElement('li');
        const a = document.createElement('a');
        // Use stable sectionId when available (e.g. "#A1"), fall back to positional
        const sec = domainSections[j];
        const secHref = (sec.sectionId && sec.sectionId.trim()) ? ('#' + sec.sectionId.trim()) : ('#detail-' + (i * 1000 + j));
        a.href = secHref;
        a.textContent = sec.displayTitle || sec.analyzerName || ('Section ' + j);
        li.appendChild(a);
        list.appendChild(li);
      }
      if (domain.domainInsights && domain.domainInsights.length) {
        const li = document.createElement('li');
        const a = document.createElement('a');
        a.href = '#domain-' + i + '-insights';
        a.textContent = 'Domain insights';
        li.appendChild(a);
        list.appendChild(li);
      }
      if (list.children.length) det.appendChild(list);
      container.appendChild(det);
    }
  } else {
    for (let i = 0; i < sections.length; i++) {
      const sec = sections[i];
      const det = document.createElement('details');
      det.open = false;
      det.dataset.target = '#detail-' + i;
      const summ = document.createElement('summary');
      summ.textContent = sec.displayTitle || sec.analyzerName || ('Section ' + i);
      det.appendChild(summ);
      attachScrollToggle(det);
      const tocNodes = buildTocNodes(sec.blocks || [], i);
      if (tocNodes.length) det.appendChild(renderTocNodes(tocNodes));
      container.appendChild(det);
    }
  }
  fragment.appendChild(container);

  nav.replaceChildren(fragment);
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
  const pa = document.createElement('a'); pa.className = 'permalink'; pa.href = '#finding-' + i; pa.setAttribute('aria-label', 'Permalink'); pa.textContent = 'ðŸ”—';
  const copyBtn = el('button', 'copy-btn'); copyBtn.type = 'button'; copyBtn.setAttribute('aria-label', 'Copy permalink'); copyBtn.title = 'Copy permalink'; copyBtn.dataset.copy = (location.href || '').split('#')[0] + '#finding-' + i; copyBtn.textContent = '\u2398';
  actions.appendChild(pa); actions.appendChild(copyBtn); header.appendChild(actions); sec.appendChild(header);

  const h2 = document.createElement('h2'); h2.className = 'finding-card__title'; h2.textContent = f.title || '';
  sec.appendChild(h2);

  const summary = document.createElement('p'); summary.className = 'finding-card__summary'; summary.textContent = f.evidence || ''; sec.appendChild(summary); linkifyAnchors(summary);

  const details = el('div', 'finding-card__details');
  function detailField(label, value) {
    const field = el('div', 'finding-card__field');
    const fieldLabel = el('div', 'finding-card__field-label'); fieldLabel.textContent = label;
    const fieldValue = el('div', 'finding-card__field-value'); fieldValue.textContent = value || 'â€”'; wrapAddresses(fieldValue); linkifyAnchors(fieldValue);
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
  if (Array.isArray(doc.domains) && doc.domains.length) return null;
  const notes = doc.confidence;
  if (!notes || !notes.length) return null;
  const sec = el('section', 'section-card');
  const h2 = document.createElement('h2'); h2.textContent = 'Confidence Notes'; sec.appendChild(h2);
  const ul = document.createElement('ul');
  for (const note of notes) {
    const li = document.createElement('li');
    const strong = document.createElement('strong'); strong.textContent = '[' + note.analyzer + ']'; li.appendChild(strong);
    li.appendChild(t(' ' + note.reason));
    ul.appendChild(li);
  }
  sec.appendChild(ul);
  return sec;
}

export function buildAnalyzerSection(section, i) {
  // Use stable sectionId (e.g. "A1", "B4") when available; fall back to positional detail-N
  const stableId = section.sectionId && section.sectionId.trim() ? section.sectionId.trim() : ('detail-' + i);
  const wrapper = el('section', 'analyzer-section detail-color-' + (i % 6));
  wrapper.id = stableId;
  // Keep detail-N as a data attribute for internal use by collapsible/heading ID generation
  wrapper.dataset.detailIndex = String(i);

  // â”€â”€ Collapsible: section handle (summary) always at top â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  const details = el('details'); const summaryEl = el('summary'); summaryEl.id = 'detail-' + i + '-summary';
  // Section-ID badge (e.g. "A1") + title
  if (section.sectionId && section.sectionId.trim()) {
    const idBadge = el('span', 'detail-summary__section-id'); idBadge.textContent = section.sectionId.trim(); summaryEl.appendChild(idBadge);
  }
  const title = el('span', 'detail-summary__title'); title.textContent = section.displayTitle || section.analyzerName || '';
  const blocks = section.blocks || [];
  // Lead-finding severity badge in the summary row
  const leadSev = section.leadFinding ? (section.leadFinding.severity || '').toLowerCase() : '';
  if (leadSev && leadSev !== 'info') {
    const sevBadge = el('span', 'detail-summary__sev detail-summary__sev--' + leadSev);
    sevBadge.textContent = leadSev.charAt(0).toUpperCase() + leadSev.slice(1);
    summaryEl.appendChild(title); summaryEl.appendChild(sevBadge);
  } else {
    summaryEl.appendChild(title);
  } details.appendChild(summaryEl);

  const content = el('div', 'detail-block'); content.setAttribute('role', 'region'); content.setAttribute('aria-labelledby', summaryEl.id); content.dataset.sectionIndex = String(i);

  // â”€â”€ Inside the expanded area: Lead Finding first, then Key Metrics strip â”€â”€
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
      for (let ci = 0; ci < lead.caveats.length; ci++) { const c = el('div', 'lead-finding__caveat'); c.textContent = '\u26a0 ' + lead.caveats[ci]; lfCav.appendChild(c); }
      lf.appendChild(lfCav);
    }
    content.appendChild(lf);
  }

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

  // â”€â”€ Narrative blocks (headings, text, confidence bands, collapsibles) â”€â”€â”€â”€â”€â”€
  renderBlocks(blocks, content);

  // â”€â”€ Typed section tables (each collapsed by default) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  const sectionTables = section.tables;
  if (sectionTables && sectionTables.length) {
    for (let ti = 0; ti < sectionTables.length; ti++) {
      const tbl = sectionTables[ti];
      const rowCount = tbl.rows ? tbl.rows.length : 0;
      if (rowCount === 0) continue; // spec: omit empty tables entirely
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
          const cells = dataRow.cells || dataRow;
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

  // â”€â”€ Provenance footer â€” collapsed, scoped outside main details â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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
        const note = el('div', 'provenance__note'); note.textContent = '\u26a0 ' + prov.cappingNotes[ni]; provContent.appendChild(note);
      }
    }
    provDetails.appendChild(provContent); wrapper.appendChild(provDetails);
  }

  return wrapper;
}

export function renderFindingsPaged(doc, announce) {
  const findings = doc.findings || []; if (!findings.length) return null; const container = el('div', 'findings-paged');
  const header = el('div', 'findings-paged__header');
  const title = document.createElement('h2'); title.textContent = 'Findings';
  header.appendChild(title);

  const controls = el('div', 'pagination-controls'); controls.setAttribute('role', 'region'); controls.setAttribute('aria-label', 'Findings pagination'); const prevBtn = el('button', 'action-btn findings-prev'); prevBtn.type = 'button'; prevBtn.setAttribute('aria-label', 'Previous page'); prevBtn.textContent = 'â† Prev'; const nextBtn = el('button', 'action-btn findings-next'); nextBtn.type = 'button'; nextBtn.setAttribute('aria-label', 'Next page'); nextBtn.textContent = 'Next â†’'; const pageInfo = el('span', 'page-info'); const sizeSel = document.createElement('select'); sizeSel.setAttribute('aria-label', 'Findings per page'); [[10,'10'],[20,'20'],[50,'50'],[100,'100'],[0,'All']].forEach(function (opt) { const o = document.createElement('option'); o.value = String(opt[0]); o.text = String(opt[1]); sizeSel.appendChild(o); }); controls.appendChild(prevBtn); controls.appendChild(pageInfo); controls.appendChild(nextBtn); controls.appendChild(t(' ')); controls.appendChild(sizeSel);
  const list = el('div', 'findings-list'); list.setAttribute('role', 'list'); let pageSize = 10; let pageIndex = 0; function renderPage() { list.innerHTML = ''; const total = findings.length; const start = pageSize === 0 ? 0 : pageIndex * pageSize; const end = pageSize === 0 ? total : Math.min(total, start + pageSize); for (let i = start; i < end; i++) list.appendChild(buildFindingCard(findings[i], i)); pageInfo.textContent = pageSize === 0 ? `${total} findings` : `${start + 1}-${end} of ${total}`; prevBtn.disabled = (pageIndex === 0) || (pageSize === 0); nextBtn.disabled = (end >= total) || (pageSize === 0); controls.style.display = (total <= pageSize || pageSize === 0) ? 'none' : ''; if (pageSize !== 0 && announce) announce(`Showing ${start + 1} to ${end} of ${total} findings`); }
  prevBtn.addEventListener('click', function () { if (pageSize === 0) return; if (pageIndex > 0) { pageIndex--; renderPage(); } }); nextBtn.addEventListener('click', function () { if (pageSize === 0) return; pageIndex++; renderPage(); }); sizeSel.addEventListener('change', function () { pageSize = parseInt(sizeSel.value, 10) || 0; pageIndex = 0; renderPage(); }); sizeSel.value = String(pageSize); container.appendChild(header); container.appendChild(controls); container.appendChild(list); renderPage(); return container; }
