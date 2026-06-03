// Navigation: domain sections list, cross-domain insights, and table of contents.
// buildDomains and buildCrossDomainInsights call buildFindingCard (findings.js)
// and buildAnalyzerSection (sections.js) — resolved via hoisting in the IIFE bundle.
import { el, nvl } from './report.dom.js';
import { sortSectionsForRender, buildInsightStats, domainAnchorId, domainSevLabel, slugifyAnchor } from './report.renderers.shared.js';

function normalizeAnalyzerKey(value) {
  return String(value || '').toLowerCase().replace(/[^a-z0-9]+/g, '');
}

function registerAnalyzerTarget(map, label, target) {
  const key = normalizeAnalyzerKey(label);
  if (!key || map.has(key)) return;
  map.set(key, target);

  if (key.endsWith('analyzer')) {
    const analysisAlias = key.slice(0, -8) + 'analysis';
    if (analysisAlias && !map.has(analysisAlias)) map.set(analysisAlias, target);
    const baseAlias = key.slice(0, -8);
    if (baseAlias && !map.has(baseAlias)) map.set(baseAlias, target);
  } else if (key.endsWith('analysis')) {
    const analyzerAlias = key.slice(0, -8) + 'analyzer';
    if (analyzerAlias && !map.has(analyzerAlias)) map.set(analyzerAlias, target);
    const baseAlias = key.slice(0, -8);
    if (baseAlias && !map.has(baseAlias)) map.set(baseAlias, target);
  }
}

function buildAnalyzerSectionTargetMap(doc) {
  const map = new Map();
  const domains = Array.isArray(doc && doc.domains) ? doc.domains : [];

  for (let i = 0; i < domains.length; i++) {
    const domain = domains[i] || {};
    const domainId = domainAnchorId(domain, i);
    const sections = Array.isArray(domain.sections) ? sortSectionsForRender(domain.sections) : [];
    for (let j = 0; j < sections.length; j++) {
      const section = sections[j] || {};
      const stableId = (section.sectionId && section.sectionId.trim())
        ? section.sectionId.trim()
        : ('detail-' + slugifyAnchor(domainId, 'scope') + '-' + j);
      const target = '#' + stableId;
      registerAnalyzerTarget(map, section.analyzerName, target);
      registerAnalyzerTarget(map, section.displayTitle, target);
    }
  }

  return map;
}

function resolveFindingTarget(sectionTargetMap, finding) {
  const analyzer = normalizeAnalyzerKey(finding && finding.analyzer);
  if (!analyzer) return '';

  if (sectionTargetMap.has(analyzer)) return sectionTargetMap.get(analyzer);

  for (const [key, target] of sectionTargetMap.entries()) {
    if (key.startsWith(analyzer) || analyzer.startsWith(key)) return target;
  }

  return '';
}

// ── Domain sections list ──────────────────────────────────────────────────────

export function buildDomains(doc) {
  const domains = doc.domains;
  if (!Array.isArray(domains) || !domains.length) return null;
  const sectionTargetMap = buildAnalyzerSectionTargetMap(doc);

  function buildDomainHistogram(domain) {
    const critical = Number(nvl(nvl(domain.crit, domain.criticalCount), 0));
    const warning = Number(nvl(nvl(domain.warn, domain.warningCount), 0));
    const totalFindings = Number(nvl(nvl(domain.find, domain.findingCount), 0));
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
  wrap.id = 'report-domains';
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
        const promoteTarget = resolveFindingTarget(sectionTargetMap, insights[k]);
        insightsList.appendChild(buildFindingCard(insights[k], `${domainId}-insight-${k}`, { promoteTarget: promoteTarget }));
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
          try {
            document.dispatchEvent(new CustomEvent('dumpdetective:domain-sections-appended', {
              detail: {
                domainId: domainId,
                renderedCount: rendered,
                totalCount: sections.length
              }
            }));
          } catch (e) { }
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

    if (!sections.length && !insights.length && (domainSev === 'ok' || domainSev === 'info')) {
      const empty = el('div', 'domain-empty-state');
      const icon = el('span', 'domain-empty-state__icon');
      icon.textContent = '\u2713';
      empty.appendChild(icon);
      const text = el('div', 'domain-empty-state__text');
      text.textContent = 'No findings in this domain — system appears healthy';
      empty.appendChild(text);
      details.appendChild(empty);
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
  const sectionTargetMap = buildAnalyzerSectionTargetMap(doc);

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
    const promoteTarget = resolveFindingTarget(sectionTargetMap, findings[i]);
    list.appendChild(buildFindingCard(findings[i], 'cross-' + i, { promoteTarget: promoteTarget }));
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

  const sectionAnchorOccurrences = new Map();
  function resolveSectionHref(sec, domainId, sectionIndex) {
    const stableId = (sec && sec.sectionId && sec.sectionId.trim())
      ? sec.sectionId.trim()
      : ('detail-' + slugifyAnchor(domainId, 'scope') + '-' + sectionIndex);

    const seen = sectionAnchorOccurrences.get(stableId) || 0;
    sectionAnchorOccurrences.set(stableId, seen + 1);

    if (seen === 0) return '#' + stableId;
    return '#' + stableId + '-' + (seen + 1);
  }

  function attachScrollToggle(det) {
    det.addEventListener('toggle', function () {
      if (!det.open) return;
      const target = document.getElementById(det.dataset.target ? det.dataset.target.substring(1) : '');
      if (!target) return;
      try { target.scrollIntoView({ behavior: 'smooth', block: 'start' }); history.replaceState(null, '', det.dataset.target || '#'); } catch (e) { }
    });
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
  if (doc.appendix) {
    const appendixLi = quickLink('#sec-appendix', 'Appendix', '\u00B6');
    appendixLi.classList.add('toc-forensics-only');
    quickList.appendChild(appendixLi);
  }
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
      const domainId = domainAnchorId(domain, i);
      const domainSev = String(domain.leadSeverity || 'Info').toLowerCase();

      const det = document.createElement('details');
      det.open = false;
      det.dataset.target = '#' + domainId;

      const summ = document.createElement('summary');
      summ.className = 'toc-domain-summary';

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
        const cnt = el('span', 'toc-domain-summary__count toc-domain-summary__count--' + domainSev);
        cnt.textContent = String(sectionCount);
        summ.appendChild(cnt);
      }
      det.appendChild(summ);
      attachScrollToggle(det);

      const list = document.createElement('ol');
      const domainSections = Array.isArray(domain.sections) ? sortSectionsForRender(domain.sections) : [];
      for (let j = 0; j < domainSections.length; j++) {
        const li = document.createElement('li');
        const sec = domainSections[j];
        const secHref = resolveSectionHref(sec, domainId, j);
        const a = document.createElement('a');
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
      if (list.children.length) {
        list.className = 'toc-forensics-only';
        det.appendChild(list);
      }
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

        // Derive the display name from explicit per-doc dumpPath when present,
        // otherwise fall back to a generic label.
        const rawPath = (Array.isArray(doc.trendDumpPaths) && doc.trendDumpPaths.length && doc.trendDumpPaths[dumpIndex])
          || subDoc.dumpPath || '';
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
