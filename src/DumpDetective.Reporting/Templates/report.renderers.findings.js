// Findings UI: finding cards, confidence notes, and paged findings list.
import { el, t, sevCss, linkifyAnchors } from './report.dom.js';
import { findingAnchorId } from './report.renderers.shared.js';
import { ensureUniqueDomId } from './report.renderers.shared.js';
import { confidenceSlots } from './report.renderers.shared.js';

// ── Finding card ──────────────────────────────────────────────────────────────

export function buildFindingCard(f, i, options) {
  const severity = String((f && f.severity) || 'Info').toLowerCase();

  const details = Array.isArray(f.details) ? f.details.filter(function (x) { return !!x; }) : [];
  const caveats = Array.isArray(f.caveats) ? f.caveats.filter(function (x) { return !!x; }) : [];
  const evidenceSummary = details.length > 0 ? details[0] : (f.summary || '');
  const recommendationLine = String(f.recommendation || '');

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
  if (f && f.regressionClass) {
    sec.dataset.regressionClass = String(f.regressionClass);
  }
  const header = el('div', 'finding-card__header');
  const eyebrow = el('div', 'finding-card__eyebrow');
  const badge = el('span', 'severity-badge ' + sevCss(f.severity)); badge.textContent = f.severity || 'Info'; eyebrow.appendChild(badge);
  const cat = el('span', 'category'); cat.textContent = f.category || 'Finding'; eyebrow.appendChild(cat);
  header.appendChild(eyebrow);
  const headerMeta = el('div', 'finding-card__header-meta');
  // Claim cards take confidence from the breakdown; other findings keep their severity-defaulted score.
  const claim = f.claim || null;
  const headlineConfidence = claim && claim.confidence != null ? claim.confidence : f.confidence;
  const headlineBand = claim && claim.confidenceBand ? claim.confidenceBand : f.confidenceBand;
  if (headlineConfidence != null) {
    const conf = Number(headlineConfidence);
    const band = headlineBand || 'low';
    const confChip = el('span', 'finding-card__confidence-chip finding-card__confidence-chip--' + band);
    if (claim && claim.breakdown) {
      const tip = confidenceTooltip(claim.breakdown);
      confChip.title = tip;
      confChip.tabIndex = 0;
      confChip.setAttribute('aria-label', 'Confidence ' + conf.toFixed(2) + '. ' + tip.replace(/\n/g, '. '));
      const label = el('span', 'finding-card__confidence-label');
      label.textContent = 'Confidence';
      confChip.appendChild(label);
    }
    const meter = el('span', 'finding-card__confidence-meter');
    const slots = confidenceSlots(band);
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

  // Claim fields exist only for rules that state an assertion; every other finding renders as before.
  if (claim) {
    sec.appendChild(buildClaimAssertion(claim));
    if (claim.confidenceReason) sec.appendChild(buildClaimConfidenceReason(claim));
  }

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
  if (f.regressionClass) {
    const rc = el('span', 'finding-chip finding-chip--regression');
    rc.textContent = String(f.regressionClass);
    meta.appendChild(rc);
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

  if (caveats.length > 0) {
    const caveatWrap = el('div', 'finding-card__caveats');
    for (let ci = 0; ci < caveats.length; ci++) {
      const caveat = el('div', 'finding-card__caveat');
      caveat.textContent = '\u26A0 ' + caveats[ci];
      caveatWrap.appendChild(caveat);
    }
    sec.appendChild(caveatWrap);
  }

  if (claim) sec.appendChild(buildClaimWhy(claim, f.analyzer, options));

  return sec;
}

// ── Claim card parts ─────────────────────────────────────────────

function buildClaimAssertion(claim) {
  const line = el('p', 'finding-card__assertion');
  const predicate = el('span', 'finding-card__predicate');
  predicate.textContent = claim.predicate;
  line.appendChild(predicate);
  line.appendChild(t(claim.assertion));
  return line;
}

function confidenceTooltip(breakdown) {
  const rows = [
    'Overall ' + Number(breakdown.composite).toFixed(2),
    'Evidence strength ' + Number(breakdown.evidenceStrength).toFixed(2),
    'Capability fidelity ' + Number(breakdown.capabilityFidelity).toFixed(2),
    'Identity fidelity cap ' + Number(breakdown.identityFidelityCap).toFixed(2),
    'Temporal alignment cap ' + Number(breakdown.temporalAlignmentCap).toFixed(2),
    'Conflict penalty ' + Number(breakdown.conflictPenalty).toFixed(2),
  ];
  const factors = Array.isArray(breakdown.limitingFactors) ? breakdown.limitingFactors : [];
  return rows.concat(factors).join('\n');
}

function buildClaimConfidenceReason(claim) {
  const line = el('p', 'finding-card__confidence-reason');
  line.textContent = claim.confidenceReason;
  return line;
}

function claimSupportCount(claim) { return Array.isArray(claim.support) ? claim.support.length : 0; }
function claimCounterCount(claim) { return claim.counter && Array.isArray(claim.counter.statements) ? claim.counter.statements.length : 0; }
function claimTestCount(claim) { return Array.isArray(claim.tests) ? claim.tests.length : 0; }

function pluralize(count, singular) { return count + ' ' + singular + (count === 1 ? '' : 's'); }

function buildClaimWhy(claim, cardAnalyzer, options) {
  const details = el('details', 'finding-card__why');
  const summary = document.createElement('summary');
  summary.className = 'finding-card__why-summary';
  summary.textContent = 'Why · ' + pluralize(claimSupportCount(claim), 'support') + ' · '
    + pluralize(claimCounterCount(claim), 'counter') + ' · ' + pluralize(claimTestCount(claim), 'test');
  details.appendChild(summary);

  const sourceTarget = options && typeof options.sourceTarget === 'function' ? options.sourceTarget : null;

  if (claimSupportCount(claim) > 0) {
    details.appendChild(buildEvidenceGroup('Supports', claim.support, sourceTarget, cardAnalyzer));
  }

  if (claim.counter) {
    const counterGroup = buildEvidenceGroup('Weakened by', claim.counter.statements || [], sourceTarget, cardAnalyzer);
    if (claimCounterCount(claim) === 0 && claim.counter.noneBecause) {
      const none = el('p', 'finding-card__why-none');
      none.textContent = 'None found. ' + claim.counter.noneBecause;
      counterGroup.appendChild(none);
    }
    details.appendChild(counterGroup);
  }

  if (claimTestCount(claim) > 0) {
    details.appendChild(buildTestGroup(claim.tests));
  }

  return details;
}

function buildGroupShell(label) {
  const group = el('div', 'finding-card__why-group');
  const heading = el('div', 'finding-card__why-heading');
  heading.textContent = label;
  group.appendChild(heading);
  return group;
}

function buildEvidenceGroup(label, statements, sourceTarget, cardAnalyzer) {
  const group = buildGroupShell(label);
  for (let i = 0; i < statements.length; i++) {
    const statement = statements[i];
    const row = el('div', 'finding-card__why-row');
    const text = el('span', 'finding-card__why-text');
    text.textContent = statement.why;
    row.appendChild(text);

    const sources = Array.isArray(statement.sources) ? statement.sources : [];
    // A source that is the card's own analyzer is already named in the card header; only others add information.
    for (let s = 0; s < sources.length; s++) {
      if (sources[s] === cardAnalyzer) continue;
      row.appendChild(buildSourceLink(sources[s], sourceTarget));
    }
    group.appendChild(row);
  }
  return group;
}

function buildSourceLink(analyzerName, sourceTarget) {
  const target = sourceTarget ? sourceTarget(analyzerName) : '';
  if (!target) {
    const plain = el('span', 'finding-chip finding-card__why-source');
    plain.textContent = analyzerName;
    return plain;
  }

  const link = document.createElement('a');
  link.className = 'finding-chip finding-card__why-source incident-promote-link';
  link.href = target;
  link.setAttribute('data-promote-target', target);
  link.textContent = analyzerName;
  link.title = 'Open the ' + analyzerName + ' section';
  return link;
}

function buildTestGroup(tests) {
  const group = buildGroupShell('Wrong if…');
  for (let i = 0; i < tests.length; i++) {
    const test = tests[i];
    const row = el('div', 'finding-card__why-row finding-card__why-row--test');
    const kind = el('span', 'finding-chip finding-card__why-kind');
    kind.textContent = test.kind;
    row.appendChild(kind);

    const text = el('span', 'finding-card__why-text');
    text.textContent = test.test;
    row.appendChild(text);

    if (test.command) {
      const command = el('span', 'finding-card__why-command');
      const code = document.createElement('code');
      code.textContent = test.command;
      command.appendChild(code);
      const copy = el('button', 'copy-btn');
      copy.type = 'button';
      copy.setAttribute('aria-label', 'Copy ' + test.command);
      copy.setAttribute('data-copy', test.command);
      copy.title = 'Copy to clipboard';
      copy.textContent = '⎘';
      command.appendChild(copy);
      row.appendChild(command);
    }
    group.appendChild(row);
  }
  return group;
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

