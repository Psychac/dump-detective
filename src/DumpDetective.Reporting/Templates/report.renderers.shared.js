// Shared private rendering helpers — used across multiple renderer modules.
// These helpers have no exports in the ES-module sense that callers need to name
// individually; they are re-exported only so that the barrel (report.renderers.js)
// and individual split files can import them when running as real ES modules.
// In the production inline-bundle path the C# bundler strips all import/export
// keywords and concatenates the files into one IIFE, so everything is in scope
// via normal JS function hoisting.
import { el } from './report.dom.js';

// ── Severity ranking ─────────────────────────────────────────────────────────
export function sevRank(sev) {
  if (sev == null) return -1;
  const s = String(sev).toLowerCase();
  if (s === 'critical' || s === '2') return 3;
  if (s === 'warning'  || s === '1') return 2;
  if (s === 'info'     || s === '0') return 1;
  if (s === 'ok') return 1;
  return -1;
}

// ── Domain ordering ──────────────────────────────────────────────────────────
const DOMAIN_RENDER_ORDER = [
  'Leaks',
  'Memory',
  'GC',
  'TypeSystem',
  'Threads',
  'Async',
  'Exceptions',
  'Runtime',
  'Infrastructure'
];

export function domainOrderRank(domain) {
  if (!domain) return 999;
  const idx = DOMAIN_RENDER_ORDER.findIndex(function (d) {
    return d.toLowerCase() === String(domain).toLowerCase();
  });
  return idx >= 0 ? idx : 999;
}

export function sortDomainsForRender(doc, domains) {
  const prioritizeSeverity = !!doc.prioritizeDomainsBySeverity;

  return domains.slice().sort(function (a, b) {
    if (prioritizeSeverity) {
      const sevCmp = sevRank(b.leadSeverity) - sevRank(a.leadSeverity);
      if (sevCmp !== 0) return sevCmp;
    }

    const orderCmp = domainOrderRank(a.domain) - domainOrderRank(b.domain);
    if (orderCmp !== 0) return orderCmp;

    const sevCmp = sevRank(b.leadSeverity) - sevRank(a.leadSeverity);
    if (sevCmp !== 0) return sevCmp;

    return String(a.domain || '').localeCompare(String(b.domain || ''));
  });
}

export function sortSectionsForRender(sections) {
  return sections.slice().sort(function (a, b) {
    const idCmp = String(a.sectionId || '').localeCompare(String(b.sectionId || ''));
    if (idCmp !== 0) return idCmp;

    const sevCmp = sevRank(b.leadFinding && b.leadFinding.severity) - sevRank(a.leadFinding && a.leadFinding.severity);
    if (sevCmp !== 0) return sevCmp;

    const aName = String(a.displayTitle || a.analyzerName || '');
    const bName = String(b.displayTitle || b.analyzerName || '');
    return aName.localeCompare(bName);
  });
}

// ── Anchor / slug helpers ────────────────────────────────────────────────────
export function slugifyAnchor(value, fallback) {
  const raw = String(value || '').trim().toLowerCase();
  const slug = raw.replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  if (slug) return slug;
  return fallback || 'item';
}

export function stableHash(value) {
  const str = String(value || '');
  let hash = 2166136261;
  for (let i = 0; i < str.length; i++) {
    hash ^= str.charCodeAt(i);
    hash = Math.imul(hash, 16777619);
  }
  return (hash >>> 0).toString(36);
}

export function domainAnchorId(domain, fallbackIndex) {
  const name = domain && domain.domain ? String(domain.domain) : '';
  return 'domain-' + slugifyAnchor(name, 'domain-' + fallbackIndex);
}

export function findingAnchorId(finding, fallbackId) {
  if (finding && finding.id)
    return 'finding-' + slugifyAnchor(finding.id, stableHash(finding.id));

  const key = [
    finding && finding.title,
    finding && finding.analyzer,
    finding && finding.category,
    finding && finding.summary
  ].join('|');
  return 'finding-' + stableHash(key || fallbackId || 'finding');
}

// ── DOM id uniqueness ───────────────────────────────────────────────────────
export function ensureUniqueDomId(baseId) {
  const root = document;
  const seed = String(baseId || '').trim() || 'node';
  const key = '__dumpdetective_used_dom_ids__';
  if (!root[key]) root[key] = new Set();
  const used = root[key];

  let candidate = seed;
  let suffix = 2;
  while (used.has(candidate) || !!document.getElementById(candidate)) {
    candidate = seed + '-' + suffix;
    suffix++;
  }
  used.add(candidate);
  return candidate;
}

export function domainSevLabel(sev) {
  if (sev == null) return 'Info';
  const s = String(sev).toLowerCase();
  if (s === '2' || s === 'critical') return 'Critical';
  if (s === '1' || s === 'warning')  return 'Warning';
  if (s === '0' || s === 'info')     return 'Info';
  return String(sev).charAt(0).toUpperCase() + String(sev).slice(1).toLowerCase();
}

// ── Finding sort helpers ─────────────────────────────────────────────────────
export function findingSeverityRank(severity) {
  const s = String(severity || 'info').toLowerCase();
  if (s === 'critical') return 3;
  if (s === 'warning') return 2;
  return 1;
}

export function sortFindingsBySeverity(findings) {
  return findings.slice().sort(function (a, b) {
    const sev = findingSeverityRank(b && b.severity) - findingSeverityRank(a && a.severity);
    if (sev !== 0) return sev;

    const analyzer = String((a && a.analyzer) || '').localeCompare(String((b && b.analyzer) || ''));
    if (analyzer !== 0) return analyzer;

    return String((a && a.title) || '').localeCompare(String((b && b.title) || ''));
  });
}

// ── Insight stats chip strip (used by buildDomains + buildCrossDomainInsights) ─
export function buildInsightStats(findings, className) {
  const wrap = el('div', className + ' insight-stats');
  const counts = { critical: 0, warning: 0, info: 0 };
  for (let i = 0; i < findings.length; i++) {
    const sev = String((findings[i] && findings[i].severity) || 'info').toLowerCase();
    if (sev === 'critical' || sev === 'warning' || sev === 'info') counts[sev]++;
  }

  function addChip(kind, label) {
    if (!counts[kind]) return;
    const chip = el('span', 'insight-stats__chip insight-stats__chip--' + kind);
    chip.textContent = counts[kind] + ' ' + label;
    wrap.appendChild(chip);
  }

  addChip('critical', 'critical');
  addChip('warning', 'warning');
  addChip('info', 'info');

  if (!wrap.childNodes.length) {
    const chip = el('span', 'insight-stats__chip insight-stats__chip--info');
    chip.textContent = '0 insights';
    wrap.appendChild(chip);
  }
  return wrap;
}
