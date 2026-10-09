// Entity dossier panel (docs/ReportStructure/m2-entities-plan.md, step 5b): a slide-over opened from an entity cell or from a
// #entity/<kind>/<name> address. It reads only what the report already carries: the entity table, the per-entity summary and
// the claim list. Names come from the analysed dump, so everything is written with textContent, never as markup.
import { el, t } from './report.dom.js';
import { findingAnchorId } from './report.renderers.shared.js';

const ENTITY_TYPE_PANELS = ['Size and population', 'Generations and heaps', 'Retention', 'Lifecycle', 'Shape and cost', 'Exceptions and async'];
const ENTITY_THREAD_PANELS = ['State', 'Retention', 'Exceptions and async'];
const ENTITY_MODULE_PANELS = ['Footprint', 'Activity', 'Identity'];
const ENTITY_PANEL_ORDER = ENTITY_TYPE_PANELS.concat(['State'], ENTITY_MODULE_PANELS, ['Other']);

let _entityTable = null;
let _entitySummary = null;
let _entityByKey = new Map();
let _entityRowById = new Map();
let _entityClaimById = new Map();
let _entityRelations = null;
let _entityNeighbours = new Map();
let _entityPanelEl = null;
let _entityReturnHash = '';
let _entityOpenerEl = null;

export function setEntityData(doc) {
  _entityTable = Array.isArray(doc && doc.entities) ? doc.entities : null;
  _entitySummary = (doc && doc.entitySummary) || null;
  _entityByKey = new Map();
  _entityRowById = new Map();
  _entityClaimById = new Map();
  _entityRelations = (doc && doc.relations) || null;
  _entityNeighbours = new Map();
  if (_entityRelations && Array.isArray(_entityRelations.pairs)) {
    for (const pair of _entityRelations.pairs) {
      addEntityNeighbour(pair[0], pair[1], pair[2]);
      addEntityNeighbour(pair[1], pair[0], pair[2]);
    }
  }
  if (_entityTable) {
    for (const entity of _entityTable) _entityByKey.set(entityKey(entity.kind, entity.joinKey), entity);
  }
  if (_entitySummary && Array.isArray(_entitySummary.entities)) {
    for (const row of _entitySummary.entities) _entityRowById.set(row[0], row);
  }
  const claims = [].concat((doc && doc.crossDomainInsights) || []);
  for (const domain of (doc && doc.domains) || []) claims.push(...(domain.domainInsights || []));
  for (const claim of claims) _entityClaimById.set(claim.id, claim);
}

// Each pair is stored once, lower id first; the page of either entity lists the other.
function addEntityNeighbour(id, otherId, sourceIndexes) {
  if (!_entityNeighbours.has(id)) _entityNeighbours.set(id, []);
  _entityNeighbours.get(id).push({ id: otherId, sources: sourceIndexes });
}

function entityKey(kind, joinKey) {
  return String(kind || '').toLowerCase() + '/' + joinKey;
}

function entityHref(entity) {
  return '#entity/' + String(entity.kind).toLowerCase() + '/' + encodeURIComponent(entity.joinKey);
}

// A table cell that names an entity becomes a link, so it can be opened, copied and shared like any address.
export function decorateEntityCell(td, entityId, shownName) {
  const entity = _entityTable && _entityTable[entityId];
  td.textContent = '';
  if (!entity) {
    td.textContent = shownName;
    return;
  }
  const link = el('a', 'entity-link');
  link.href = entityHref(entity);
  // Thread ids keep the thousands separators the plain number cell had, and keep sorting as numbers.
  const isNumericId = String(entity.kind) === 'Thread' && /^[0-9]+$/.test(shownName);
  link.textContent = isNumericId ? Number(shownName).toLocaleString('en-US') : shownName;
  if (isNumericId) td.dataset.value = shownName;
  link.title = 'Open the page for this ' + String(entity.kind).toLowerCase();
  td.appendChild(link);
}

function parseEntityHash(hash) {
  const match = /^#entity\/([a-z]+)\/(.+)$/.exec(hash || '');
  if (!match) return null;
  try {
    return { kind: match[1], joinKey: decodeURIComponent(match[2]) };
  } catch (err) {
    return null;
  }
}

export function setupEntityPanel() {
  if (!_entityTable) return;
  window.addEventListener('hashchange', syncEntityPanelToHash);
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && _entityPanelEl && !_entityPanelEl.hidden) closeEntityPanel(true);
  });
  document.addEventListener('click', function (e) {
    const link = e.target && e.target.closest ? e.target.closest('.entity-link') : null;
    if (!link) return;
    _entityOpenerEl = link;
    if (!parseEntityHash(location.hash)) _entityReturnHash = location.hash || '';
  });
  const initial = parseEntityHash(location.hash);
  if (initial) openEntityPanel(initial);
  else _entityReturnHash = location.hash || '';
}

function syncEntityPanelToHash() {
  const target = parseEntityHash(location.hash);
  if (target) {
    openEntityPanel(target);
    return;
  }
  _entityReturnHash = location.hash || '';
  closeEntityPanel(false);
}

function ensureEntityPanel() {
  if (_entityPanelEl) return _entityPanelEl;
  const panel = el('aside', 'entity-panel');
  panel.id = 'entity-panel';
  panel.setAttribute('role', 'dialog');
  panel.setAttribute('aria-labelledby', 'entity-panel-title');
  panel.hidden = true;

  const header = el('header', 'entity-panel__header');
  const heading = el('div', 'entity-panel__heading');
  const kind = el('div', 'entity-panel__kind');
  const title = el('h2', 'entity-panel__title');
  title.id = 'entity-panel-title';
  heading.appendChild(kind);
  heading.appendChild(title);
  const close = el('button', 'entity-panel__close');
  close.type = 'button';
  close.setAttribute('aria-label', 'Close');
  close.textContent = '\u00d7';
  close.addEventListener('click', function () { closeEntityPanel(true); });
  header.appendChild(heading);
  header.appendChild(close);

  const body = el('div', 'entity-panel__body');
  panel.appendChild(header);
  panel.appendChild(body);
  document.body.appendChild(panel);
  panel._parts = { kind: kind, title: title, body: body, close: close };
  _entityPanelEl = panel;
  return panel;
}

function openEntityPanel(target) {
  const panel = ensureEntityPanel();
  const parts = panel._parts;
  const entity = _entityByKey.get(entityKey(target.kind, target.joinKey));
  parts.kind.textContent = target.kind;
  parts.title.textContent = entityPageTitle(target);
  parts.body.textContent = '';
  if (entity) {
    renderEntityBody(parts.body, entity);
  } else {
    parts.body.appendChild(entityNote('This report has no entry for that ' + target.kind + '. The link may come from a different report or run.'));
  }
  panel.hidden = false;
  parts.body.scrollTop = 0;
  parts.close.focus();
}

// An object's key carries the dump it came from ("<dump path>:0x..."); the page is about the address, so that is the title.
function entityPageTitle(target) {
  if (target.kind === 'thread') return 'OS thread ' + target.joinKey;
  if (target.kind === 'object') return target.joinKey.slice(target.joinKey.lastIndexOf(':') + 1);
  return target.joinKey;
}

function closeEntityPanel(restoreAddress) {
  if (!_entityPanelEl || _entityPanelEl.hidden) return;
  _entityPanelEl.hidden = true;
  if (restoreAddress) {
    try { history.replaceState(null, '', location.pathname + location.search + _entityReturnHash); } catch (err) { }
  }
  if (_entityOpenerEl && document.contains(_entityOpenerEl)) {
    try { _entityOpenerEl.focus({ preventScroll: true }); } catch (err) { }
  }
}

function renderEntityBody(body, entity) {
  const row = _entityRowById.get(entity.id);
  const groups = row ? row[1] : [];
  const claimIndexes = row && row[2] ? row[2] : [];
  const byPanel = entityGroupsByPanel(groups);

  body.appendChild(entityIdentity(entity, groups, claimIndexes));
  body.appendChild(entityClaims(claimIndexes));
  for (const panelName of ENTITY_PANEL_ORDER) {
    if (byPanel.has(panelName)) body.appendChild(entityFactPanel(panelName, byPanel.get(panelName)));
  }
  body.appendChild(entityRelations(entity));

  const expected = expectedEntityPanels(entity.kind);
  const absent = expected.filter(function (name) { return !byPanel.has(name); });
  if (absent.length) body.appendChild(entityNote('No facts were reported for: ' + absent.join(', ') + '.'));
  body.appendChild(entityNote('Counts and totals are per kind of fact. The individual facts are in the observations file written beside this report.'));
}

function expectedEntityPanels(kind) {
  switch (String(kind).toLowerCase()) {
    case 'type': return ENTITY_TYPE_PANELS;
    case 'thread': return ENTITY_THREAD_PANELS;
    case 'module': return ['Footprint'];
    case 'method': return ['Exceptions and async'];
    case 'appdomain': return ['Footprint'];
    default: return [];
  }
}

function entityGroupsByPanel(groups) {
  const byPanel = new Map();
  const kinds = (_entitySummary && _entitySummary.factKinds) || [];
  for (const group of groups) {
    const fact = kinds[group[0]];
    if (!fact) continue;
    if (!byPanel.has(fact.panel)) byPanel.set(fact.panel, []);
    byPanel.get(fact.panel).push({ fact: fact, count: group[1], stats: group[2] });
  }
  for (const list of byPanel.values()) list.sort(function (a, b) { return a.fact.name < b.fact.name ? -1 : a.fact.name > b.fact.name ? 1 : 0; });
  return byPanel;
}

function entityIdentity(entity, groups, claimIndexes) {
  const wrap = el('div', 'entity-panel__summary');
  const fidelity = String(entity.fidelity || '');
  const meta = el('p', 'entity-panel__meta');
  meta.textContent = 'Identity: ' + fidelity.toLowerCase()
    + (fidelity === 'Low' ? ' (compiler-generated names are merged, so numbered variants share this entry)' : '');
  let facts = 0;
  for (const group of groups) facts += group[1];
  const counts = el('p', 'entity-panel__meta');
  counts.textContent = claimIndexes.length + (claimIndexes.length === 1 ? ' claim' : ' claims') + ' \u00b7 ' + facts.toLocaleString('en-US') + (facts === 1 ? ' fact' : ' facts');
  wrap.appendChild(meta);
  wrap.appendChild(counts);
  const details = Array.isArray(entity.details) ? entity.details : [];
  if (details.length) wrap.appendChild(entityDetails(entity, details));
  return wrap;
}

// Details are facts about the entity that are not part of its identity. A module is identified by its file name, so two loaded modules
// that share one are a single entry here; listing every path keeps that visible instead of merging them silently.
function entityDetails(entity, details) {
  const wrap = el('div', 'entity-panel__details');
  const isModule = String(entity.kind) === 'Module';
  const heading = el('p', 'entity-panel__meta');
  if (isModule && details.length > 1) {
    heading.textContent = details.length + ' modules share this file name and are shown as one. Loaded from:';
    heading.classList.add('entity-panel__meta--warning');
  } else {
    heading.textContent = isModule ? 'Loaded from:' : 'Details:';
  }
  wrap.appendChild(heading);
  const list = el('ul', 'entity-panel__paths');
  for (const detail of details) {
    const item = el('li', 'entity-panel__path');
    item.textContent = detail;
    list.appendChild(item);
  }
  wrap.appendChild(list);
  return wrap;
}

function entityClaims(claimIndexes) {
  const section = el('section', 'entity-panel__section');
  section.appendChild(entityHeading('Claims'));
  if (!claimIndexes.length) {
    section.appendChild(entityNote('No claim cites this one.'));
    return section;
  }
  const ids = (_entitySummary && _entitySummary.claimIds) || [];
  const list = el('ul', 'entity-claims');
  const ordered = claimIndexes.map(function (index) { return { id: ids[index], claim: _entityClaimById.get(ids[index]) }; });
  ordered.sort(entityClaimOrder);
  for (const entry of ordered) {
    const id = entry.id;
    const claim = entry.claim;
    const item = el('li', 'entity-claims__item');
    const severity = String((claim && claim.severity) || 'info').toLowerCase();
    const chip = el('span', 'entity-claims__severity entity-claims__severity--' + severity);
    chip.textContent = severity;
    item.appendChild(chip);
    const card = findClaimCard(id);
    const title = (claim && claim.title) || id;
    if (card) {
      const link = el('a', 'entity-claims__link');
      link.href = '#' + card.id;
      link.textContent = title;
      item.appendChild(link);
    } else {
      item.appendChild(t(title));
      const missing = el('span', 'entity-claims__missing');
      missing.textContent = ' (no card for this claim in this view)';
      item.appendChild(missing);
    }
    list.appendChild(item);
  }
  section.appendChild(list);
  return section;
}

const ENTITY_SEVERITY_RANK = { critical: 0, warning: 1, info: 2 };

// Most severe first, then by title, so the order does not depend on how claim ids happen to sort.
function entityClaimOrder(a, b) {
  const rankA = ENTITY_SEVERITY_RANK[String((a.claim && a.claim.severity) || 'info').toLowerCase()];
  const rankB = ENTITY_SEVERITY_RANK[String((b.claim && b.claim.severity) || 'info').toLowerCase()];
  if (rankA !== rankB) return (rankA === undefined ? 3 : rankA) - (rankB === undefined ? 3 : rankB);
  const titleA = String((a.claim && a.claim.title) || a.id);
  const titleB = String((b.claim && b.claim.title) || b.id);
  return titleA < titleB ? -1 : titleA > titleB ? 1 : 0;
}

function findClaimCard(claimId) {
  const anchor = findingAnchorId({ id: claimId }, 0);
  return document.querySelector('[data-anchor-alias="' + anchor + '"]') || document.getElementById(anchor);
}

function entityFactPanel(panelName, facts) {
  const section = el('section', 'entity-panel__section');
  section.appendChild(entityHeading(panelName));
  const measures = (_entitySummary && _entitySummary.measures) || [];
  for (const item of facts) {
    const block = el('div', 'entity-fact');
    const head = el('div', 'entity-fact__head');
    const name = el('strong', 'entity-fact__name');
    name.textContent = item.fact.name;
    const source = el('span', 'entity-fact__source');
    source.textContent = ' \u00b7 ' + item.fact.analyzer + ' \u00b7 ' + item.count.toLocaleString('en-US') + (item.count === 1 ? ' fact' : ' facts');
    head.appendChild(name);
    head.appendChild(source);
    block.appendChild(head);
    const list = el('dl', 'entity-fact__stats');
    for (const stat of item.stats) {
      const measure = measures[stat[0]] || { name: String(stat[0]), unit: 'Count' };
      const label = el('dt', 'entity-fact__label');
      label.textContent = measure.name.replace(/[.\-]/g, ' ');
      const value = el('dd', 'entity-fact__value');
      renderEntityStat(value, stat, measure.unit, item.count);
      list.appendChild(label);
      list.appendChild(value);
    }
    block.appendChild(list);
    section.appendChild(block);
  }
  return section;
}

// One fact gives its value. Several facts give a total when adding them is meaningful (the tool decided that), and a
// range when it is not (retained bytes overlap, ratios do not add).
function renderEntityStat(cell, stat, unit, factCount) {
  const sum = stat[1], min = stat[2], max = stat[3];
  if (factCount === 1) {
    cell.textContent = entityFormatValue(min, unit);
    if (unit === 'Bytes') cell.title = entityFormatNumber(min) + ' bytes';
    return;
  }
  if (sum !== null) {
    cell.textContent = 'total ' + entityFormatValue(sum, unit);
    const range = el('span', 'entity-fact__range');
    range.textContent = ' (min ' + entityFormatValue(min, unit) + ', max ' + entityFormatValue(max, unit) + ')';
    cell.appendChild(range);
    if (unit === 'Bytes') cell.title = entityFormatNumber(sum) + ' bytes';
    return;
  }
  if (min === max) {
    cell.textContent = entityFormatValue(min, unit);
    return;
  }
  cell.textContent = entityFormatValue(min, unit) + ' \u2013 ' + entityFormatValue(max, unit);
  const note = el('span', 'entity-fact__range');
  note.textContent = ' (range across facts; these do not add up)';
  cell.appendChild(note);
}

function entityFormatNumber(value) {
  return (Math.round(value * 1000) / 1000).toLocaleString('en-US');
}

function entityFormatBytes(bytes) {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = Number(bytes) || 0, unit = 0;
  while (Math.abs(value) >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return (Math.round(value * 100) / 100).toLocaleString('en-US') + ' ' + units[unit];
}

function entityFormatValue(value, unit) {
  if (value === null || value === undefined) return '\u2014';
  if (unit === 'Bytes') return entityFormatBytes(value);
  if (unit === 'Percent') return entityFormatNumber(Math.round(value * 10) / 10) + '%';
  return entityFormatNumber(value);
}

const ENTITY_RELATION_KIND_ORDER = ['Type', 'Module', 'AppDomain', 'Thread', 'Method', 'Object'];
const ENTITY_RELATION_KIND_LABEL = { Type: 'Types', Module: 'Modules', AppDomain: 'AppDomains', Thread: 'Threads', Method: 'Methods', Object: 'Objects' };
// A group at or above this size starts collapsed and fills in when opened, so a module with thousands of types costs nothing until asked for.
const ENTITY_RELATION_COLLAPSE_AT = 25;

// "Appears with" is all a table row says: two entities in one row, not that one holds or retains the other (m2b-relations-plan.md, D2).
function entityRelations(entity) {
  const section = el('section', 'entity-panel__section');
  section.appendChild(entityHeading('Relations'));
  const neighbours = _entityNeighbours.get(entity.id) || [];
  if (!neighbours.length) {
    section.appendChild(entityNote('Nothing else in this report appears alongside this one.'));
    return section;
  }
  section.appendChild(entityNote('Appears with: seen in the same table row, or as two subjects of one fact. This does not say that one holds or retains the other.'));
  const byKind = new Map();
  for (const neighbour of neighbours) {
    const other = _entityTable && _entityTable[neighbour.id];
    if (!other) continue;
    if (!byKind.has(other.kind)) byKind.set(other.kind, []);
    byKind.get(other.kind).push({ entity: other, sources: neighbour.sources });
  }
  const kinds = ENTITY_RELATION_KIND_ORDER.filter(function (kind) { return byKind.has(kind); })
    .concat(Array.from(byKind.keys()).filter(function (kind) { return !ENTITY_RELATION_KIND_ORDER.includes(kind); }));
  for (const kind of kinds) section.appendChild(entityRelationGroup(kind, byKind.get(kind)));
  return section;
}

function entityRelationGroup(kind, items) {
  items.sort(function (a, b) {
    const nameA = entityRelationName(a.entity), nameB = entityRelationName(b.entity);
    return nameA < nameB ? -1 : nameA > nameB ? 1 : 0;
  });
  const sources = (_entityRelations && _entityRelations.sources) || [];
  const sourceNames = function (item) { return item.sources.map(function (index) { return entityRelationSourceLabel(sources[index] || String(index)); }).join(', '); };
  const sharedSource = sourceNames(items[0]);
  const allShare = items.every(function (item) { return sourceNames(item) === sharedSource; });

  const group = el('details', 'entity-relations__group');
  const summary = el('summary', 'entity-relations__summary');
  summary.textContent = (ENTITY_RELATION_KIND_LABEL[kind] || kind) + ' (' + items.length.toLocaleString('en-US') + ')'
    + (allShare ? ' \u00b7 in ' + sharedSource : '');
  group.appendChild(summary);
  const list = el('ul', 'entity-relations__list');
  group.appendChild(list);

  const fill = function () {
    for (const item of items) list.appendChild(entityRelationItem(item, allShare ? null : sourceNames(item)));
  };
  if (items.length < ENTITY_RELATION_COLLAPSE_AT) {
    group.open = true;
    fill();
  } else {
    let filled = false;
    group.addEventListener('toggle', function () {
      if (group.open && !filled) { filled = true; fill(); }
    });
  }
  return group;
}

// A source is a table ("Analyzer / Table title") or a fact kind, written with a "fact: " prefix by the tool.
function entityRelationSourceLabel(source) {
  return source.startsWith('fact: ') ? 'fact ' + source.slice(6) : source;
}

function entityRelationItem(item, sourceText) {
  const row = el('li', 'entity-relations__item');
  const link = el('a', 'entity-link');
  link.href = entityHref(item.entity);
  link.textContent = entityRelationName(item.entity);
  row.appendChild(link);
  if (sourceText) {
    const source = el('span', 'entity-relations__source');
    source.textContent = ' \u00b7 in ' + sourceText;
    row.appendChild(source);
  }
  return row;
}

function entityRelationName(entity) {
  return entityPageTitle({ kind: String(entity.kind).toLowerCase(), joinKey: entity.joinKey });
}

function entityHeading(text) {
  const heading = el('h3', 'entity-panel__section-title');
  heading.textContent = text;
  return heading;
}

function entityNote(text) {
  const note = el('p', 'entity-panel__note');
  note.textContent = text;
  return note;
}
