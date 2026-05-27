// Chart and sparkline rendering — renderSparklines, renderCharts, SVG chart builders.
import { el } from './report.dom.js';

// ── DOM-based sparkline rendering (post-render pass for pre-rendered tables) ──

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

// ── DOM-based chart rendering (post-render pass for pre-rendered chart blocks) ─

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

// ── Chart block builder (called from renderBlocks in report.renderers.blocks.js) ─

export function buildChartBlock(block) {
  const wrap = el('div', 'detail-chart');
  wrap.dataset.chartKind = block.kind || '';
  wrap.dataset.chartPayload = block.payloadJson || '{}';
  wrap.dataset.chartTitle = block.title || '';
  const title = el('div', 'detail-chart__title');
  title.textContent = block.title || '';
  wrap.appendChild(title);
  return wrap;
}

// ── SVG chart builders ────────────────────────────────────────────────────────

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
