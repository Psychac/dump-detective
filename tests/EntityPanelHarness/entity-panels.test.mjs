// Panel harness for report.renderers.entities.js. Run: node --test tests/EntityPanelHarness
// The renderer only needs a tiny slice of the DOM, so a fake one stands in for a browser.
import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';

class FakeNode {
  constructor(tag) {
    this.tag = tag;
    this.children = [];
    this.parentNode = null;
    this.attributes = {};
    this.dataset = {};
    this.hidden = false;
    this.open = false;
    this.handlers = {};
    this.className = '';
    this._text = null;
    const owner = this;
    this.classList = {
      add(name) { if (!owner.className.split(' ').includes(name)) owner.className = (owner.className + ' ' + name).trim(); },
    };
  }
  appendChild(child) {
    child.parentNode = this;
    this.children.push(child);
    return child;
  }
  setAttribute(name, value) { this.attributes[name] = value; }
  addEventListener(type, handler) { (this.handlers[type] = this.handlers[type] || []).push(handler); }
  fire(type) { for (const handler of this.handlers[type] || []) handler(); }
  focus() { }
  get textContent() {
    if (this.tag === '#text') return this._text;
    return this.children.map(function (child) { return child.textContent; }).join('');
  }
  set textContent(value) {
    this.children = [];
    if (this.tag === '#text') this._text = String(value);
    else if (value !== '') this.appendChild(makeText(value));
  }
  descendants() {
    const found = [];
    for (const child of this.children) found.push(child, ...child.descendants());
    return found;
  }
}

function makeText(text) {
  const node = new FakeNode('#text');
  node._text = String(text);
  return node;
}

globalThis.document = {
  body: new FakeNode('body'),
  createElement: function (tag) { return new FakeNode(tag); },
  createTextNode: makeText,
  addEventListener() { },
  contains() { return true; },
  querySelector() { return null; },
  getElementById() { return null; },
};
globalThis.window = { addEventListener() { } };
globalThis.location = { hash: '', pathname: '/', search: '' };
globalThis.history = { replaceState() { } };

const templates = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../src/DumpDetective.Reporting/Templates');
const entities = await import(pathToFileURL(path.join(templates, 'report.renderers.entities.js')).href);

// Fact kinds are indexes into entitySummary.factKinds; a group is [factKindIndex, count, stats].
function documentWith(entity, factKinds, groups) {
  return {
    entities: [entity],
    entitySummary: {
      factKinds: factKinds,
      measures: [],
      claimIds: [],
      entities: [[0, groups, []]],
    },
    crossDomainInsights: [],
    domains: [],
  };
}

function openPage(entity, factKinds, groups) {
  entities.setEntityData(documentWith(entity, factKinds, groups));
  location.hash = '#entity/' + String(entity.kind).toLowerCase() + '/' + encodeURIComponent(entity.joinKey);
  entities.setupEntityPanel();
  const panel = document.body.children.find(function (child) { return child.tag === 'aside'; });
  assert.ok(panel, 'panel was created');
  const body = panel._parts.body;
  const headings = body.descendants().filter(function (n) { return n.tag === 'h3'; }).map(function (n) { return n.textContent; });
  return { body, headings, text: body.textContent };
}

const moduleEntity = { id: 0, kind: 'Module', joinKey: 'System.Core.dll', fidelity: 'High', details: [] };

function fact(name, panel) {
  return { name: name, analyzer: 'A', panel: panel, measures: [] };
}

test('module page expects a Footprint panel and says so when it is missing', function () {
  const page = openPage(moduleEntity, [fact('runtime.jit-module-frames', 'Activity')], [[0, 1, []]]);
  assert.match(page.text, /No facts were reported for: Footprint\./);
});

test('module page with a footprint fact has no missing-panel note', function () {
  const page = openPage(moduleEntity, [fact('runtime.module-disk-footprint', 'Footprint')], [[0, 1, []]]);
  assert.doesNotMatch(page.text, /No facts were reported for/);
});

test('module panels render in Footprint, Activity, Identity order', function () {
  const kinds = [
    fact('infra.http-handler-module', 'Identity'),
    fact('runtime.jit-module-frames', 'Activity'),
    fact('runtime.module-disk-footprint', 'Footprint'),
  ];
  const page = openPage(moduleEntity, kinds, [[0, 1, []], [1, 1, []], [2, 1, []]]);
  const shown = page.headings.filter(function (h) { return ['Footprint', 'Activity', 'Identity'].includes(h); });
  assert.deepEqual(shown, ['Footprint', 'Activity', 'Identity']);
});

test('module facts in Other still render after the module panels', function () {
  const kinds = [fact('runtime.module-future-kind', 'Other'), fact('runtime.module-disk-footprint', 'Footprint')];
  const page = openPage(moduleEntity, kinds, [[0, 1, []], [1, 1, []]]);
  assert.ok(page.headings.indexOf('Footprint') < page.headings.indexOf('Other'));
});

test('type and thread pages keep their own expected panels', function () {
  const typePage = openPage({ id: 0, kind: 'Type', joinKey: 'System.String', fidelity: 'High' }, [], []);
  assert.match(typePage.text, /No facts were reported for: Size and population, Generations and heaps/);
  const threadPage = openPage({ id: 0, kind: 'Thread', joinKey: '12', fidelity: 'High' }, [], []);
  assert.match(threadPage.text, /No facts were reported for: State, Retention, Exceptions and async\./);
});

const awkwardModuleNames = ['App_Theme_DevEx.1a2b.dll', 'My Module (x86).dll', 'weird#name%20.dll', 'Über.Modul.dll', 'a/b\c.dll'];

for (const name of awkwardModuleNames) {
  test('module link for "' + name + '" opens that module page', function () {
    const entity = { id: 0, kind: 'Module', joinKey: name, fidelity: 'High', details: [] };
    entities.setEntityData(documentWith(entity, [], []));
    const cell = new FakeNode('td');
    entities.decorateEntityCell(cell, 0, name);
    const link = cell.children[0];
    assert.equal(link.tag, 'a');
    assert.equal(link.textContent, name);
    assert.ok(/^#entity\/module\/[^#]+$/.test(link.href), 'address has exactly one hash and no raw separators in the name: ' + link.href);

    const page = openPage(entity, [], []);
    location.hash = link.href;
    entities.setupEntityPanel();
    const panel = document.body.children.find(function (child) { return child.tag === 'aside'; });
    assert.equal(panel._parts.title.textContent, name);
    assert.equal(panel._parts.kind.textContent, 'module');
    assert.ok(page.text.length > 0);
  });
}

test('an address for a module this report does not have says so instead of opening another page', function () {
  const entity = { id: 0, kind: 'Module', joinKey: 'System.Core.dll', fidelity: 'High', details: [] };
  entities.setEntityData(documentWith(entity, [], []));
  location.hash = '#entity/module/' + encodeURIComponent('Other.dll');
  entities.setupEntityPanel();
  const panel = document.body.children.find(function (child) { return child.tag === 'aside'; });
  assert.match(panel._parts.body.textContent, /This report has no entry for that module\./);
});

test('a module cell with no entity stays plain text', function () {
  entities.setEntityData(documentWith({ id: 0, kind: 'Module', joinKey: 'A.dll', fidelity: 'High' }, [], []));
  const cell = new FakeNode('td');
  entities.decorateEntityCell(cell, 5, '');
  assert.equal(cell.children.length === 0 || cell.children[0].tag === '#text', true);
});

const methodEntity = { id: 0, kind: 'Method', joinKey: 'Serilog.PortableTimer::OnTick', fidelity: 'Low', details: [] };

test('method cell shows the bare name and links to the Class::Method address', function () {
  entities.setEntityData(documentWith(methodEntity, [], []));
  const cell = new FakeNode('td');
  entities.decorateEntityCell(cell, 0, 'OnTick');
  const link = cell.children[0];
  assert.equal(link.textContent, 'OnTick');
  assert.equal(link.href, '#entity/method/' + encodeURIComponent('Serilog.PortableTimer::OnTick'));
});

test('method page is titled Class::Method and shows its async fact', function () {
  const page = openPage(methodEntity, [fact('type.async-state-machine', 'Exceptions and async')], [[0, 1, []]]);
  const panel = document.body.children.find(function (child) { return child.tag === 'aside'; });
  assert.equal(panel._parts.title.textContent, 'Serilog.PortableTimer::OnTick');
  assert.equal(panel._parts.kind.textContent, 'method');
  assert.ok(page.headings.includes('Exceptions and async'));
  assert.doesNotMatch(page.text, /No facts were reported for/);
});

test('method page with no async fact says so', function () {
  const page = openPage(methodEntity, [], []);
  assert.match(page.text, /No facts were reported for: Exceptions and async\./);
});

const objectEntity = { id: 0, kind: 'Object', joinKey: 'D:\DUmps\Crash.dmp:0x1E101A03098', fidelity: 'High', details: [] };

test('object cell shows the address and links to the keyed page', function () {
  entities.setEntityData(documentWith(objectEntity, [], []));
  const cell = new FakeNode('td');
  entities.decorateEntityCell(cell, 0, '0x1E101A03098');
  const link = cell.children[0];
  assert.equal(link.textContent, '0x1E101A03098');
  assert.equal(link.href, '#entity/object/' + encodeURIComponent(objectEntity.joinKey));
});

test('object page is titled by its address, not the dump path, and round-trips the address', function () {
  const page = openPage(objectEntity, [fact('infra.sql-transaction', 'Size and population')], [[0, 1, []]]);
  const panel = document.body.children.find(function (child) { return child.tag === 'aside'; });
  assert.equal(panel._parts.title.textContent, '0x1E101A03098');
  assert.equal(panel._parts.kind.textContent, 'object');
  assert.ok(page.headings.includes('Size and population'));
  assert.doesNotMatch(page.text, /No facts were reported for/);
});

const appDomainEntity = { id: 0, kind: 'AppDomain', joinKey: '/LM/W3SVC/1/ROOT-2-134187409034415175', fidelity: 'High', details: [] };

test('appdomain cell shows the domain name and links to its page', function () {
  entities.setEntityData(documentWith(appDomainEntity, [], []));
  const cell = new FakeNode('td');
  entities.decorateEntityCell(cell, 0, appDomainEntity.joinKey);
  const link = cell.children[0];
  assert.equal(link.textContent, appDomainEntity.joinKey);
  assert.equal(link.href, '#entity/appdomain/' + encodeURIComponent(appDomainEntity.joinKey));
});

test('appdomain page is titled by the name, shows Footprint and Activity, and expects Footprint', function () {
  const kinds = [fact('runtime.appdomain-footprint', 'Footprint'), fact('thread.appdomain-population', 'Activity')];
  const page = openPage(appDomainEntity, kinds, [[0, 1, []], [1, 1, []]]);
  const panel = document.body.children.find(function (child) { return child.tag === 'aside'; });
  assert.equal(panel._parts.title.textContent, appDomainEntity.joinKey);
  assert.equal(panel._parts.kind.textContent, 'appdomain');
  assert.deepEqual(page.headings.filter(function (h) { return h === 'Footprint' || h === 'Activity'; }), ['Footprint', 'Activity']);
  assert.doesNotMatch(page.text, /No facts were reported for/);
});

test('appdomain page with no footprint fact says so', function () {
  const page = openPage(appDomainEntity, [], []);
  assert.match(page.text, /No facts were reported for: Footprint\./);
});

function relationEntities(count, kind, prefix) {
  const found = [];
  for (let i = 0; i < count; i++) found.push({ kind: kind, joinKey: prefix + String(i).padStart(3, '0') });
  return found;
}

// Entity 0 is the one whose page is opened; the others are numbered after it. Pairs are stored lower id first, as the tool writes them.
function openRelationsPage(subject, others, pairs, sources) {
  const table = [subject].concat(others).map(function (entity, id) { return Object.assign({ id: id, fidelity: 'High', details: [] }, entity); });
  entities.setEntityData({
    entities: table,
    entitySummary: { factKinds: [], measures: [], claimIds: [], entities: [] },
    relations: { sources: sources, pairs: pairs },
    crossDomainInsights: [],
    domains: [],
  });
  location.hash = '#entity/' + String(subject.kind).toLowerCase() + '/' + encodeURIComponent(subject.joinKey);
  entities.setupEntityPanel();
  const panel = document.body.children.find(function (child) { return child.tag === 'aside'; });
  const body = panel._parts.body;
  const groups = body.descendants().filter(function (n) { return n.tag === 'details'; });
  return { body, groups, text: body.textContent };
}

function groupItems(group) {
  return group.descendants().filter(function (n) { return n.tag === 'li'; });
}

test('a module page lists its types, sorted, with the shared source on the group line', function () {
  const types = [{ kind: 'Type', joinKey: 'System.Zeta' }, { kind: 'Type', joinKey: 'System.Alpha' }, { kind: 'Type', joinKey: 'System.Mid' }];
  const page = openRelationsPage({ kind: 'Module', joinKey: 'mscorlib.dll' }, types, [[0, 1, [0]], [0, 2, [0]], [0, 3, [0]]], ['Memory Analysis / Top types']);
  assert.equal(page.groups.length, 1);
  const summary = page.groups[0].children.find(function (n) { return n.tag === 'summary'; });
  assert.equal(summary.textContent, 'Types (3) · in Memory Analysis / Top types');
  const items = groupItems(page.groups[0]);
  assert.deepEqual(items.map(function (li) { return li.children[0].textContent; }), ['System.Alpha', 'System.Mid', 'System.Zeta']);
  assert.equal(items[0].children[0].href, '#entity/type/' + encodeURIComponent('System.Alpha'));
  assert.equal(items[0].children.length, 1, 'no per-item source when the whole group shares one');
  assert.equal(page.groups[0].open, true);
});

test('a type page lists its module from the same stored pair', function () {
  const page = openRelationsPage({ kind: 'Type', joinKey: 'System.Alpha' }, [{ kind: 'Module', joinKey: 'mscorlib.dll' }], [[0, 1, [0]]], ['Memory Analysis / Top types']);
  const summary = page.groups[0].children.find(function (n) { return n.tag === 'summary'; });
  assert.equal(summary.textContent, 'Modules (1) · in Memory Analysis / Top types');
  assert.equal(groupItems(page.groups[0])[0].children[0].textContent, 'mscorlib.dll');
});

test('a large group starts collapsed and empty, and fills in when opened', function () {
  const types = relationEntities(30, 'Type', 'System.T');
  const pairs = types.map(function (_, i) { return [0, i + 1, [0]]; });
  const page = openRelationsPage({ kind: 'Module', joinKey: 'mscorlib.dll' }, types, pairs, ['Memory Analysis / Top types']);
  const group = page.groups[0];
  assert.equal(group.open, false);
  assert.equal(groupItems(group).length, 0);
  assert.match(group.children[0].textContent, /^Types \(30\)/);
  group.open = true;
  group.fire('toggle');
  assert.equal(groupItems(group).length, 30, 'every neighbour is listed, none dropped');
  group.fire('toggle');
  assert.equal(groupItems(group).length, 30, 'opening again does not duplicate the list');
});

test('groups follow kind order, and an entity seen in different tables shows each item source', function () {
  const others = [{ kind: 'Thread', joinKey: '24088' }, { kind: 'Type', joinKey: 'System.A' }, { kind: 'Type', joinKey: 'System.B' }];
  const page = openRelationsPage({ kind: 'Method', joinKey: 'App.Foo::Run' }, others, [[0, 1, [0]], [0, 2, [0]], [0, 3, [0, 1]]], ['Async / Top', 'Crash / Exceptions']);
  const labels = page.groups.map(function (g) { return g.children[0].textContent; });
  assert.match(labels[0], /^Types \(2\)$/);
  assert.match(labels[1], /^Threads \(1\) · in Async \/ Top$/);
  const typeItems = groupItems(page.groups[0]);
  assert.equal(typeItems[1].children[1].textContent, ' · in Async / Top, Crash / Exceptions');
  assert.equal(groupItems(page.groups[1])[0].children[0].textContent, 'OS thread 24088');
});

test('relations are labelled as appears-with, never as retains or holds', function () {
  const page = openRelationsPage({ kind: 'Module', joinKey: 'a.dll' }, [{ kind: 'Type', joinKey: 'T' }], [[0, 1, [0]]], ['S / t']);
  assert.match(page.text, /Appears with: seen in the same table row, or as two subjects of one fact\./);
  assert.match(page.text, /does not say that one holds or retains the other/);
});

test('an entity with no relations says so', function () {
  const page = openRelationsPage({ kind: 'Module', joinKey: 'a.dll' }, [{ kind: 'Type', joinKey: 'T' }], [], []);
  assert.match(page.text, /Nothing else in this report appears alongside this one\./);
  assert.equal(page.groups.length, 0);
});

test('a fact source reads "in fact <kind>", beside table sources that stay as they are', function () {
  const others = [{ kind: 'Type', joinKey: 'System.A' }, { kind: 'Type', joinKey: 'System.B' }];
  const page = openRelationsPage({ kind: 'Method', joinKey: 'App.Foo::Run' }, others, [[0, 1, [0]], [0, 2, [1]]], ['Async / Top', 'fact: type.async-state-machine']);
  const items = groupItems(page.groups[0]);
  assert.equal(items[0].children[1].textContent, ' \u00b7 in Async / Top');
  assert.equal(items[1].children[1].textContent, ' \u00b7 in fact type.async-state-machine');
});

test('a group whose every pair comes from one fact names the fact once on the group line', function () {
  const page = openRelationsPage({ kind: 'Method', joinKey: 'App.Foo::Run' }, [{ kind: 'Type', joinKey: 'System.A' }], [[0, 1, [0]]], ['fact: type.async-state-machine']);
  assert.equal(page.groups[0].children[0].textContent, 'Types (1) \u00b7 in fact type.async-state-machine');
});

test('an AppDomain page lists its modules and threads from membership facts, and a module page lists its domain', function () {
  const others = [{ kind: 'Module', joinKey: 'b.dll' }, { kind: 'Module', joinKey: 'a.dll' }, { kind: 'Thread', joinKey: '24088' }];
  const sources = ['fact: runtime.module-appdomain-membership', 'fact: thread.appdomain-membership'];
  const page = openRelationsPage({ kind: 'AppDomain', joinKey: 'DefaultDomain' }, others, [[0, 1, [0]], [0, 2, [0]], [0, 3, [1]]], sources);
  const labels = page.groups.map(function (g) { return g.children[0].textContent; });
  assert.deepEqual(labels, ['Modules (2) \u00b7 in fact runtime.module-appdomain-membership', 'Threads (1) \u00b7 in fact thread.appdomain-membership']);
  assert.deepEqual(groupItems(page.groups[0]).map(function (li) { return li.children[0].textContent; }), ['a.dll', 'b.dll']);

  const modulePage = openRelationsPage({ kind: 'Module', joinKey: 'a.dll' }, [{ kind: 'AppDomain', joinKey: 'DefaultDomain' }], [[0, 1, [0]]], sources);
  assert.equal(modulePage.groups[0].children[0].textContent, 'AppDomains (1) \u00b7 in fact runtime.module-appdomain-membership');
  assert.equal(groupItems(modulePage.groups[0])[0].children[0].href, '#entity/appdomain/' + encodeURIComponent('DefaultDomain'));
});

test('subscription relations say which side the page is on, from the pair order and the reversed marker', function () {
  const sources = ['fact: event.subscription', 'fact: event.subscription (reversed)'];
  // Entity 0 is the page. Forward: first subject has the lower id. Reversed: first subject has the higher id.
  const others = [{ kind: 'Type', joinKey: 'App.ViewA' }, { kind: 'Type', joinKey: 'App.ViewB' }];
  // Page is entity 0 = the publisher in both, so its pairs are (0,1) forward and (0,2) forward: the others subscribe to it.
  const asPublisher = openRelationsPage({ kind: 'Type', joinKey: 'App.Publisher' }, others, [[0, 1, [0]], [0, 2, [0]]], sources);
  assert.equal(asPublisher.groups[0].children[0].textContent, 'Types (2) \u00b7 subscribes to this');

  // Page is entity 0 = a subscriber; the publisher has the higher id, so the stored source is "(reversed)" (first subject, the publisher, is higher).
  const asSubscriber = openRelationsPage({ kind: 'Type', joinKey: 'App.ViewA' }, [{ kind: 'Type', joinKey: 'App.Publisher' }], [[0, 1, [1]]], sources);
  assert.equal(asSubscriber.groups[0].children[0].textContent, 'Types (1) \u00b7 this subscribes to it');
});

test('a type that is both publisher and subscriber of another shows both roles on one line', function () {
  const sources = ['fact: event.subscription', 'fact: event.subscription (reversed)'];
  const page = openRelationsPage({ kind: 'Type', joinKey: 'App.A' }, [{ kind: 'Type', joinKey: 'App.B' }], [[0, 1, [0, 1]]], sources);
  assert.equal(page.groups[0].children[0].textContent, 'Types (1) · subscribes to this, this subscribes to it');
});
