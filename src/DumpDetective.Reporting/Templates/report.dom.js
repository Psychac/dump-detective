// DOM helper utilities for report rendering
export function el(tag, className) {
  const e = document.createElement(tag);
  if (className) e.className = className;
  return e;
}

export function t(text) {
  return document.createTextNode(String(text));
}

export function sevCss(s) {
  const l = (s || '').toLowerCase();
  return l === 'critical' ? 'severity-critical' : l === 'warning' ? 'severity-warning' : 'severity-info';
}

export function formatBytes(bytes) {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let v = Number(bytes) || 0, u = 0;
  while (v >= 1024 && u < units.length - 1) { v /= 1024; u++; }
  return (Math.round(v * 100) / 100) + ' ' + units[u];
}

export function nvl(a, b) { return (a !== undefined && a !== null) ? a : b; }

export function wrapAddresses(container) {
  const addrRe = /0x[0-9A-Fa-f]{6,}/g;
  const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT, null);
  const nodes = [];
  let n;
  while ((n = walker.nextNode())) {
    if (addrRe.test(n.textContent)) nodes.push(n);
    addrRe.lastIndex = 0;
  }
  for (const node of nodes) {
    const txt = node.textContent;
    addrRe.lastIndex = 0;
    const frag = document.createDocumentFragment();
    let last = 0, m;
    while ((m = addrRe.exec(txt)) !== null) {
      if (m.index > last) frag.appendChild(t(txt.slice(last, m.index)));
      const span = el('span', 'addr');
      span.appendChild(t(m[0]));
      const btn = el('button', 'copy-btn');
      btn.type = 'button';
      btn.setAttribute('aria-label', 'Copy ' + m[0]);
      btn.setAttribute('data-copy', m[0]);
      btn.title = 'Copy to clipboard';
      btn.textContent = '\u2398';
      span.appendChild(btn);
      frag.appendChild(span);
      last = m.index + m[0].length;
    }
    if (last < txt.length) frag.appendChild(t(txt.slice(last)));
    if (node.parentNode) node.parentNode.replaceChild(frag, node);
  }
}

export function linkifyAnchors(container) {
  const re = /#(?:detail|finding)-\d+/g;
  const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT, null);
  const nodes = [];
  let n;
  while ((n = walker.nextNode())) {
    if (re.test(n.textContent)) nodes.push(n);
    re.lastIndex = 0;
  }
  for (const node of nodes) {
    const txt = node.textContent;
    re.lastIndex = 0;
    const frag = document.createDocumentFragment();
    let last = 0, m;
    while ((m = re.exec(txt)) !== null) {
      if (m.index > last) frag.appendChild(t(txt.slice(last, m.index)));
      const a = document.createElement('a');
      a.href = m[0];
      a.textContent = m[0];
      a.className = 'intext-anchor';
      a.setAttribute('aria-label', 'Jump to ' + m[0].substring(1));
      frag.appendChild(a);
      last = m.index + m[0].length;
    }
    if (last < txt.length) frag.appendChild(t(txt.slice(last)));
    if (node.parentNode) node.parentNode.replaceChild(frag, node);
  }
}

export function indentClass(level) {
  if (level >= 2) return ' detail-indent-2';
  if (level === 1) return ' detail-indent-1';
  return '';
}

export function isInside(elm, selector) {
  let el = elm;
  while (el) {
    if (el.matches && el.matches(selector)) return true;
    el = el.parentElement;
  }
  return false;
}

export function createAriaLive() {
  let __ariaLive = document.getElementById('report-aria-live');
  if (!__ariaLive) {
    __ariaLive = document.createElement('div');
    __ariaLive.id = 'report-aria-live';
    __ariaLive.setAttribute('aria-live', 'polite');
    __ariaLive.setAttribute('aria-atomic', 'true');
    __ariaLive.className = 'sr-only';
    document.body.appendChild(__ariaLive);
  }
  function announce(msg) { try { __ariaLive.textContent = msg; } catch (e) { /* ignore */ } }
  return { announce, __ariaLive };
}
