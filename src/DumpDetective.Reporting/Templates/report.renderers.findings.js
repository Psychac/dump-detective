// Findings UI: finding cards, confidence notes, and paged findings list.
import { el, t, sevCss, linkifyAnchors } from './report.dom.js';
import { findingAnchorId } from './report.renderers.shared.js';
import { ensureUniqueDomId } from './report.renderers.shared.js';

// ── Finding card ──────────────────────────────────────────────────────────────

export function buildFindingCard(f, i, options) {
  const severity = String((f && f.severity) || 'Info').toLowerCase();

  const evidenceItems = Array.isArray(f.evidenceItems) ? f.evidenceItems.filter(function (x) { return !!x; }) : [];
  const recommendationItems = Array.isArray(f.recommendationItems) ? f.recommendationItems.filter(function (x) { return !!x; }) : [];
  const evidenceSummary = evidenceItems.length > 0 ? evidenceItems[0] : (f.evidence || '');
  const recommendationLine = recommendationItems.length > 0 ? recommendationItems[0] : (f.recommendation || f.fix || '');

  const normalize = function (text) {
    return String(text || '').trim().replace(/\s+/g, ' ').toLowerCase();
  };
  const toOneLine = function (text) {
    return String(text || '').replace(/\s+/g, ' ').trim();
  };
  const sameText = function (a, b) {
    return normalize(a) && normalize(a) === normalize(b);
  };

  const canonicalFindingId = findingAnchorId(f, i);
  const findingId = ensureUniqueDomId(canonicalFindingId);
  const sec = el('section', 'finding-card finding-card--' + severity);
  sec.id = findingId;
  sec.setAttribute('data-anchor-alias', canonicalFindingId);
  sec.setAttribute('data-anchoralias', canonicalFindingId);
  sec.dataset.severity = severity;
  sec.dataset.title = f.title || '';
  sec.dataset.summary = evidenceSummary.substring(0, 200);
  const header = el('div', 'finding-card__header');
  const eyebrow = el('div', 'finding-card__eyebrow');
  const badge = el('span', 'severity-badge ' + sevCss(f.severity)); badge.textContent = f.severity || 'Info'; eyebrow.appendChild(badge);
  const cat = el('span', 'category'); cat.textContent = f.category || 'Finding'; eyebrow.appendChild(cat);
  header.appendChild(eyebrow);
  const headerMeta = el('div', 'finding-card__header-meta');
  if (f.confidenceScore != null) {
    const conf = Number(f.confidenceScore);
    const band = conf >= 0.85 ? 'high' : conf >= 0.65 ? 'medhigh' : conf >= 0.45 ? 'medium' : 'low';
    const confChip = el('span', 'finding-card__confidence-chip finding-card__confidence-chip--' + band);
    const meter = el('span', 'finding-card__confidence-meter');
    const slots = Math.max(1, Math.min(4, Math.round(conf * 4)));
    for (let si = 0; si < 4; si++) {
      const slot = el('span', 'finding-card__confidence-slot' + (si < slots ? ' finding-card__confidence-slot--on' : ''));
      meter.appendChild(slot);
    }
    const score = el('span', 'finding-card__confidence-score');
    score.textContent = conf.toFixed(2);
    confChip.appendChild(meter);
    confChip.appendChild(score);
    headerMeta.appendChild(confChip);
  }
  if (headerMeta.childNodes.length) header.appendChild(headerMeta);
  sec.appendChild(header);

  const h2 = document.createElement('h2'); h2.className = 'finding-card__title'; h2.textContent = f.title || '';
  sec.appendChild(h2);

  const issueLine = toOneLine(evidenceSummary);
  const brief = el('div', 'finding-card__brief');
  if (issueLine) {
    const issueRow = el('div', 'finding-card__brief-row finding-card__brief-row--issue');
    const issueLabel = el('span', 'finding-card__brief-label finding-card__brief-label--issue');
    issueLabel.textContent = '!';
    issueLabel.setAttribute('aria-label', 'Issue');
    issueLabel.title = 'Issue';
    const issueValue = el('span', 'finding-card__brief-value'); issueValue.textContent = issueLine;
    issueRow.appendChild(issueLabel);
    issueRow.appendChild(issueValue);
    brief.appendChild(issueRow);
    linkifyAnchors(issueValue);
  }

  const promoteTarget = options && options.promoteTarget ? String(options.promoteTarget) : '';
  const meta = el('div', 'finding-card__meta');
  if (f.analyzer) {
    const chip = el('span', 'finding-chip');
    chip.textContent = 'Analyzer: ' + f.analyzer;
    meta.appendChild(chip);
  }
  if (promoteTarget) {
    const investigate = document.createElement('a');
    investigate.className = 'finding-card__investigate finding-card__meta-action incident-promote-link';
    investigate.href = promoteTarget;
    investigate.setAttribute('data-promote-target', promoteTarget);
    investigate.setAttribute('aria-label', 'Investigate this finding in forensics view');
    const icon = el('span', 'finding-card__investigate-icon');
    icon.textContent = '\u2197';
    icon.setAttribute('aria-hidden', 'true');
    const label = el('span', 'finding-card__investigate-label');
    label.textContent = 'Investigate';
    investigate.appendChild(label);
    investigate.appendChild(icon);
    meta.appendChild(investigate);
  }
  if (meta.childNodes.length) sec.appendChild(meta);

  if (recommendationLine && !sameText(recommendationLine, issueLine)) {
    const recRow = el('div', 'finding-card__brief-row finding-card__brief-row--recommendation');
    const recLabel = el('span', 'finding-card__brief-label finding-card__brief-label--recommendation');
    recLabel.textContent = '\u2192';
    recLabel.setAttribute('aria-label', 'Recommendation');
    recLabel.title = 'Recommendation';
    const recValue = el('span', 'finding-card__brief-value'); recValue.textContent = toOneLine(recommendationLine);
    recRow.appendChild(recLabel);
    recRow.appendChild(recValue);
    brief.appendChild(recRow);
    linkifyAnchors(recValue);
  }

  if (brief.childNodes.length) sec.appendChild(brief);

  const caveats = Array.isArray(f.caveatItems) ? f.caveatItems.filter(function (x) { return !!x; }) : [];
  if (caveats.length > 0) {
    const caveatWrap = el('div', 'finding-card__caveats');
    for (let ci = 0; ci < caveats.length; ci++) {
      const caveat = el('div', 'finding-card__caveat');
      caveat.textContent = '\u26A0 ' + caveats[ci];
      caveatWrap.appendChild(caveat);
    }
    sec.appendChild(caveatWrap);
  }

  return sec;
}

// ── Confidence notes (legacy flat-list mode) ─────────────────────────────────

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

