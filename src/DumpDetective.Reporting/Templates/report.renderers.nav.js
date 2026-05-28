// Navigation: domain sections list, cross-domain insights, and table of contents.
// buildDomains and buildCrossDomainInsights call buildFindingCard (findings.js)
// and buildAnalyzerSection (sections.js) — resolved via hoisting in the IIFE bundle.
import { el } from './report.dom.js';
import { sortSectionsForRender, buildInsightStats, domainAnchorId, domainSevLabel } from './report.renderers.shared.js';

// ── Domain sections list ──────────────────────────────────────────────────────

export function buildDomains(doc) {
  const domains = doc.domains;
  if (!Array.isArray(domains) || !domains.length) return null;

  function buildDomainHistogram(domain) {
    const critical = Number(domain.criticalCount || 0);
    const warning = Number(domain.warningCount || 0);
    const totalFindings = Number(domain.findingCount || 0);
    const info = Math.max(0, totalFindings - critical - warning);
    const buckets = [
      { cls: 'critical', count: critical },
      { cls: 'warning', count: warning },
      { cls: 'info', count: info },
      { cls: 'ok', count: 0 },
      { cls: 'unknown', count: 0 }
    ];

    const wrap = el('span', 'domain-header__histogram');
    const max = Math.max(1, ...buckets.map(function (b) { return b.count; }));
    for (let i = 0; i < buckets.length; i++) {
      const b = buckets[i];
      const seg = el('span', 'domain-header__histogram-bar domain-header__histogram-bar--' + b.cls);
      const scaled = b.count <= 0 ? 1 : Math.min(5, Math.max(1, Math.round((b.count / max) * 5)));
      seg.style.height = String(4 + scaled * 2) + 'px';
      seg.title = b.cls + ': ' + b.count;
      wrap.appendChild(seg);
    }

    wrap.setAttribute('aria-label', 'Domain finding distribution');
    return wrap;
  }

  const wrap = el('div', 'report-domains');
  for (let i = 0; i < domains.length; i++) {
    const domain = domains[i] || {};
    const domainSev = String(domain.leadSeverity || 'Info').toLowerCase();
    const domainId = domainAnchorId(domain, i);
    const sec = el('section', 'section-card report-domain report-domain--' + domainSev);
    sec.id = domainId;
    sec.dataset.domain = domain.domain || '';
    sec.dataset.leadSeverity = domainSev;

    const details = el('details', 'report-domain__details');
    details.open = domainSev === 'critical' || domainSev === 'warning';

    const hdr = document.createElement('summary');
    hdr.className = 'domain-header domain-header--' + domainSev;
    const dot = el('span', 'toc-dot toc-dot--' + domainSev); hdr.appendChild(dot);
    const title = el('span', 'domain-header__name'); title.textContent = domain.domain || 'Domain';
    hdr.appendChild(title);
    const histogram = buildDomainHistogram(domain);
    hdr.appendChild(histogram);
    const domSevLabel = domainSevLabel(domain.leadSeverity);
    if (domSevLabel !== 'Info') {
      const pill = el('span', 'domain-header__sev domain-header__sev--' + domainSev); pill.textContent = domSevLabel; hdr.appendChild(pill);
    }
    details.appendChild(hdr);

    const insights = Array.isArray(domain.domainInsights) ? domain.domainInsights : [];
    if (insights.length) {
      const insightsSec = el('section', 'report-domain__insights');
      insightsSec.id = domainId + '-insights';
      const insightsHdr = el('div', 'domain-insights__header');
      const h3 = el('h3', 'domain-insights__title');
      h3.textContent = 'Domain Insights';
      const stats = buildInsightStats(insights, 'domain-insights__stats');
      insightsHdr.appendChild(h3);
      insightsHdr.appendChild(stats);
      insightsSec.appendChild(insightsHdr);

      const insightsList = el('div', 'domain-insights__list');
      for (let k = 0; k < insights.length; k++) {
        insightsList.appendChild(buildFindingCard(insights[k], `${domainId}-insight-${k}`));
      }
      insightsSec.appendChild(insightsList);
      details.appendChild(insightsSec);
    }

    const sections = Array.isArray(domain.sections) ? sortSectionsForRender(domain.sections) : [];
    if (sections.length) {
      const body = el('div', 'domain-body');
      const heading = el('div', 'domain-body__heading');
      heading.textContent = 'Analyzer Details';
      body.appendChild(heading);

      const batchSize = 8;
      let rendered = 0;

      function renderNextBatch() {
        const end = Math.min(sections.length, rendered + batchSize);
        for (let j = rendered; j < end; j++) {
          body.appendChild(buildAnalyzerSection(sections[j], j, domainId));
        }
        rendered = end;
      }

      renderNextBatch();
      if (rendered < sections.length) {
        const loadMore = el('button', 'action-btn domain-load-more');
        loadMore.type = 'button';
        loadMore.textContent = 'Load more sections (' + (sections.length - rendered) + ' remaining)';
        loadMore.addEventListener('click', function () {
          renderNextBatch();
          const remaining = sections.length - rendered;
          if (remaining > 0) {
            loadMore.textContent = 'Load more sections (' + remaining + ' remaining)';
          } else {
            loadMore.remove();
          }
        });
        body.appendChild(loadMore);
      }
      details.appendChild(body);
    }

    sec.appendChild(details);

    wrap.appendChild(sec);
  }

  return wrap;
}

// ── Cross-domain insights ────────────────────────────────────────────────────

export function buildCrossDomainInsights(doc) {
  const findings = Array.isArray(doc.crossDomainInsights) ? doc.crossDomainInsights : [];
  if (!findings.length) return null;

  const sec = el('section', 'section-card cross-domain-insights');
  const hdr = el('div', 'cross-domain-insights__header');
  const title = el('span', 'cross-domain-insights__title'); title.textContent = 'Cross-Domain Insights';
  const stats = buildInsightStats(findings, 'cross-domain-insights__stats');
  hdr.appendChild(title); hdr.appendChild(stats);
  sec.appendChild(hdr);

  const cnt = el('span', 'cross-domain-insights__count');
  cnt.textContent = findings.length + ' finding' + (findings.length !== 1 ? 's' : '');
  sec.appendChild(cnt);

  const list = el('div', 'cross-domain-insights__list');
  for (let i = 0; i < findings.length; i++) {
    list.appendChild(buildFindingCard(findings[i], 'cross-' + i));
  }
  sec.appendChild(list);

  return sec;
}

// ── Table of Contents ─────────────────────────────────────────────────────────

export function buildTOC(doc, perDumpDocs) {
  const isTrendToc = !!(doc['$kind'] === 'trend' || doc.isTrendReport);
  if (!Array.isArray(perDumpDocs)) perDumpDocs = [];
  const domains = Array.isArray(doc.domains) ? doc.domains : [];
  const sections = isTrendToc ? (doc.trendAnalyzerSections || []) : (doc.analyzerSections || []);
  if ((!domains || !domains.length) && (!sections || !sections.length)) return null;

  const nav = document.getElementById('toc') || el('nav', 'toc');
  nav.className = 'toc report-navbar__toc';
  nav.setAttribute('aria-label', 'Report sections');
  const existingTitle = nav.querySelector('.toc-title');
  if (existingTitle) existingTitle.remove();

  function attachScrollToggle(det) {
    det.addEventListener('toggle', function () {
      if (!det.open) return;
      const target = document.getElementById(det.dataset.target ? det.dataset.target.substring(1) : '');
      if (!target) return;
      try { target.scrollIntoView({ behavior: 'smooth', block: 'start' }); history.replaceState(null, '', det.dataset.target || '#'); } catch (e) { }
    });
  }

  function sevDot(sev) {
    const s = String(sev == null ? '' : sev).toLowerCase();
    const n = Number(sev);
    if (n === 3 || s === 'critical') return { cls: 'toc-dot--critical', label: 'Critical' };
    if (n === 2 || s === 'warning')  return { cls: 'toc-dot--warning',  label: 'Warning' };
    if (n === 1 || s === 'ok')       return { cls: 'toc-dot--ok',       label: 'OK' };
    return                                   { cls: 'toc-dot--info',     label: 'Info' };
  }

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

  // ── 1. Pinned quick-nav ──────────────────────────────────────────────────
  const quickSection = el('div', 'toc-quick-nav');
  const quickLabel = el('div', 'toc-quick-nav__label'); quickLabel.textContent = 'Report'; quickSection.appendChild(quickLabel);
  const quickList = document.createElement('ul'); quickList.className = 'toc-quick-nav__list';
  quickList.appendChild(quickLink('#sec-header',  'Overview',          '\u25CE'));
  if (doc.healthScorecard) quickList.appendChild(quickLink('#sec-health', 'Health Summary', '\u271A'));
  if (doc.executiveSummary) quickList.appendChild(quickLink('#sec-exec',  'Executive Summary', '\u00A7'));
  if (Array.isArray(doc.findings) && doc.findings.length) quickList.appendChild(quickLink('#sec-action-queue', 'Action Queue', '!'));
  if (doc.appendix) quickList.appendChild(quickLink('#sec-appendix', 'Appendix', '\u00B6'));
  quickSection.appendChild(quickList);
  fragment.appendChild(quickSection);

  // ── 2. Domain / section tree ─────────────────────────────────────────────
  const treeLabel = el('div', 'toc-quick-nav__label');
  treeLabel.textContent = domains.length ? 'Domains' : 'Sections';
  fragment.appendChild(treeLabel);

  const container = el('div', 'toc-section');
  if (domains.length) {
    for (let i = 0; i < domains.length; i++) {
      const domain = domains[i] || {};
      const dot = sevDot(domain.leadSeverity);
      const domainId = domainAnchorId(domain, i);

      const det = document.createElement('details');
      det.open = false;
      det.dataset.target = '#' + domainId;

      const summ = document.createElement('summary');
      summ.className = 'toc-domain-summary';

      const dotEl = el('span', 'toc-dot ' + dot.cls);
      dotEl.setAttribute('aria-label', dot.label);
      summ.appendChild(dotEl);

      const domainLink = document.createElement('a');
      domainLink.className = 'toc-domain-summary__link';
      domainLink.href = '#' + domainId;
      domainLink.textContent = domain.domain || ('Domain ' + i);
      domainLink.addEventListener('click', function (e) {
        e.stopPropagation();
      });
      summ.appendChild(domainLink);

      const sectionCount = (Array.isArray(domain.sections) ? domain.sections : []).length;
      if (sectionCount) {
        const cnt = el('span', 'toc-domain-summary__count'); cnt.textContent = String(sectionCount); summ.appendChild(cnt);
      }
      det.appendChild(summ);
      attachScrollToggle(det);

      const list = document.createElement('ol');
      const domainSections = Array.isArray(domain.sections) ? sortSectionsForRender(domain.sections) : [];
      for (let j = 0; j < domainSections.length; j++) {
        const li = document.createElement('li');
        const a = document.createElement('a');
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
        a.href = '#' + domainId + '-insights';
        a.textContent = 'Domain insights';
        li.appendChild(a);
        list.appendChild(li);
      }
      if (list.children.length) det.appendChild(list);
      container.appendChild(det);
    }
  } else {
    if (isTrendToc) {
      for (let i = 0; i < sections.length; i++) {
        const sec = sections[i];
        const secHref = (sec.sectionId && sec.sectionId.trim()) ? ('#' + sec.sectionId.trim()) : ('#detail-' + i);
        const det = document.createElement('details');
        det.open = false;
        det.dataset.target = secHref;
        const summ = document.createElement('summary');
        summ.textContent = sec.displayTitle || sec.analyzerName || ('Section ' + i);
        det.appendChild(summ);
        attachScrollToggle(det);
        const tocNodes = buildTocNodes(sec.blocks || [], i);
        if (tocNodes.length) det.appendChild(renderTocNodes(tocNodes));
        container.appendChild(det);
      }

      for (let dumpIndex = 0; dumpIndex < perDumpDocs.length; dumpIndex++) {
        const subDoc = perDumpDocs[dumpIndex];
        if (!subDoc) continue;

        const rawPath = subDoc.dumpPath || '';
        const dumpName = rawPath ? rawPath.replace(/^.*[\\/]/, '') : ('Dump ' + (dumpIndex + 1));
        const targetHref = '#dump-detail-' + dumpIndex;
        const det = document.createElement('details');
        det.open = false;
        det.dataset.target = targetHref;
        const summ = document.createElement('summary');
        summ.textContent = dumpName;
        det.appendChild(summ);
        attachScrollToggle(det);

        const list = document.createElement('ol');
        if (Array.isArray(subDoc.domains) && subDoc.domains.length) {
          const subDomains = subDoc.domains;
          for (let sdi = 0; sdi < subDomains.length; sdi++) {
            const domain = subDomains[sdi];
            const domainId = domainAnchorId(domain, sdi);
            const li = document.createElement('li');
            const a = document.createElement('a');
            a.href = '#' + domainId;
            a.textContent = domain.domain || ('Domain ' + sdi);
            li.appendChild(a);
            list.appendChild(li);
          }
        }
        if (list.children.length) det.appendChild(list);
        container.appendChild(det);
      }
    } else {
      for (let i = 0; i < sections.length; i++) {
        const sec = sections[i];
        const secHref = (sec.sectionId && sec.sectionId.trim()) ? ('#' + sec.sectionId.trim()) : ('#detail-' + i);
        const det = document.createElement('details');
        det.open = false;
        det.dataset.target = secHref;
        const summ = document.createElement('summary');
        summ.textContent = sec.displayTitle || sec.analyzerName || ('Section ' + i);
        det.appendChild(summ);
        attachScrollToggle(det);
        const tocNodes = buildTocNodes(sec.blocks || [], i);
        if (tocNodes.length) det.appendChild(renderTocNodes(tocNodes));
        container.appendChild(det);
      }
    }
  }
  fragment.appendChild(container);

  nav.replaceChildren(fragment);
  return nav;
}

// Private TOC helpers

function buildTocNodes(blocks, sectionIndex) {
  const root = [];
  const contextStack = [{ nodes: root, headingStack: [] }];
  let headingIndex = 0;
  let collapseIndex = 0;

  for (const block of blocks) {
    if (!block) continue;

    if (block.type === 'collapsibleBegin') {
      const current = contextStack[contextStack.length - 1];
      const node = createTocNode(block.title || 'Narrative', `#detail-${sectionIndex}-collapse-${collapseIndex++}`);
      current.nodes.push(node);
      contextStack.push({ nodes: node.children, headingStack: [] });
      continue;
    }

    if (block.type === 'collapsibleEnd') {
      if (contextStack.length > 1) contextStack.pop();
      continue;
    }

    if (block.type !== 'heading') continue;

    const current = contextStack[contextStack.length - 1];
    const level = Math.max(0, block.indentLevel || 0);
    while (current.headingStack.length > level) current.headingStack.pop();

    let parentNode = null;
    if (level > 0) parentNode = current.headingStack[level - 1] || current.headingStack[current.headingStack.length - 1] || null;

    const node = createTocNode(block.text || (`Heading ${headingIndex + 1}`), `#detail-${sectionIndex}-heading-${headingIndex++}`);
    if (parentNode) parentNode.children.push(node);
    else current.nodes.push(node);

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
    if (node.children.length) li.appendChild(renderTocNodes(node.children));
    list.appendChild(li);
  }
  return list;
}
