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
  addEventListener() { }
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
