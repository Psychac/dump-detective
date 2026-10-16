// Lazy report segments (docs/ReportStructure/m4-payload-plan.md steps 4-6). The core payload in #report-json holds what the page
// shows first; sections' typed slots, large tables and the entity data ship as separate <script id="dd-seg-..."> elements listed in
// #dd-manifest. Each element is plain JSON or gzip-base64, as its data-encoding says.

const SEGMENT_ELEMENT_PREFIX = 'dd-seg-';
const NULL_REASON_CODES = { nm: 'nm', na: 'na', amb: 'amb' };

// Plain "json" (or the attribute being absent, for older reports) is returned as-is.
export async function decodeElementText(el) {
  const text = el.textContent;
  if (!text || !text.trim()) return null;
  const encoding = el.dataset ? el.dataset.encoding : null;
  if (encoding !== 'gzip-base64') return text;
  if (typeof DecompressionStream === 'undefined') {
    throw new Error('This report\'s data is gzip-compressed and requires a browser with ' +
      'DecompressionStream support (current Chrome, Edge, or Firefox) to open.');
  }
  const binary = atob(text.trim());
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  const decompressed = new Blob([bytes]).stream().pipeThrough(new DecompressionStream('gzip'));
  return await new Response(decompressed).text();
}

export function readSegmentManifest() {
  const el = document.getElementById('dd-manifest');
  if (!el || !el.textContent || !el.textContent.trim()) return [];
  try { return JSON.parse(el.textContent); } catch (e) { return []; }
}

const _segmentLoads = new Map();

// One load per segment id, remembered; a failure names the segment so the page can say which piece did not open.
export function loadSegment(id) {
  if (_segmentLoads.has(id)) return _segmentLoads.get(id);
  const load = (async function () {
    const el = document.getElementById(SEGMENT_ELEMENT_PREFIX + id);
    if (!el) throw new Error('Report segment "' + id + '" is missing from this file.');
    try {
      const text = await decodeElementText(el);
      return text ? JSON.parse(text) : null;
    } catch (e) {
      throw new Error('Report segment "' + id + '" failed to load: ' + (e && e.message ? e.message : String(e)));
    }
  })();
  _segmentLoads.set(id, load);
  return load;
}

// A column-store table (m4-payload-plan.md step 3) back to the row-major arrays the table renderer reads.
export function columnTableToRows(table) {
  const cols = Array.isArray(table && table.cols) ? table.cols : [];
  const rowCount = table && typeof table.rows === 'number' ? table.rows : 0;
  const rows = new Array(rowCount);
  for (let r = 0; r < rowCount; r++) {
    const row = new Array(cols.length);
    for (let c = 0; c < cols.length; c++) row[c] = columnCell(cols[c], r);
    rows[r] = row;
  }
  return rows;
}

function columnCell(col, row) {
  const value = col.values[row];
  if (value !== null && value !== undefined) return value;
  const reason = (col.nulls && col.nulls[row]) || col.nullReason;
  return reason ? { u: NULL_REASON_CODES[reason] || 'nm' } : null;
}

// Puts a section's lazy content back into the section object: its typed slots from the section segment, and the rows of each large table from
// its table segment. Idempotent, so an open and an export can race.
export async function loadSectionSegment(section) {
  if (!section || !section.segmentId) return;
  const payload = await loadSegment(section.segmentId);
  if (payload) Object.keys(payload).forEach(function (key) { section[key] = payload[key]; });

  // Table blocks arrive from the core without rows; their rows come with the section's segment, by block index.
  if (Array.isArray(section.blockTableRows) && Array.isArray(section.blocks)) {
    section.blockTableRows.forEach(function (entry) {
      if (section.blocks[entry.blockIndex]) section.blocks[entry.blockIndex].rows = entry.rows;
    });
    delete section.blockTableRows;
  }

  const stubs = (Array.isArray(section.compactTables) ? section.compactTables : []).filter(function (table) { return table.segmentId; });
  const columnTables = await Promise.all(stubs.map(function (table) { return loadSegment(table.segmentId); }));
  stubs.forEach(function (table, i) {
    table.rows = columnTableToRows(columnTables[i]);
    delete table.segmentId;
    delete table.rowCount;
  });
  delete section.segmentId;
}

// Every section of the document and, for a trend report, of every per-dump document, for the downloads, which need the whole report.
export async function loadAllSections(doc, perDumpDocs) {
  const sections = [];
  const collect = function (document_) {
    (Array.isArray(document_ && document_.domains) ? document_.domains : []).forEach(function (domain) {
      (Array.isArray(domain.sections) ? domain.sections : []).forEach(function (section) { sections.push(section); });
    });
  };
  collect(doc);
  (Array.isArray(doc && doc.trendAnalyzerSections) ? doc.trendAnalyzerSections : []).forEach(function (section) { sections.push(section); });
  (Array.isArray(perDumpDocs) ? perDumpDocs : []).forEach(collect);
  await Promise.all(sections.map(loadSectionSegment));
}

// Entity data is lazy (m4-payload-plan.md, D10): the entity table loads before the first table with entity cells is built, and the dossier data
// (summary and relations) when a dossier first opens. `apply` hands the document to the viewer's entity code each time something arrives.
let _entityDoc = null;
let _applyEntityData = null;
let _entitiesLoad = null;
let _dossierLoad = null;

export function configureEntityLoading(doc, apply) {
  _entityDoc = doc;
  _applyEntityData = apply;
}

function manifestEntry(kind) {
  return readSegmentManifest().find(function (entry) { return entry.kind === kind; }) || null;
}

export function loadEntities() {
  if (!_entityDoc) return Promise.resolve();
  if (!_entitiesLoad) {
    _entitiesLoad = (async function () {
      const entry = manifestEntry('entities');
      if (!entry || _entityDoc.entities) return;
      _entityDoc.entities = await loadSegment(entry.id);
      _applyEntityData(_entityDoc);
    })();
  }
  return _entitiesLoad;
}

// The dossier needs the entity table too, so this loads both.
export function loadDossier() {
  if (!_entityDoc) return Promise.resolve();
  if (!_dossierLoad) {
    _dossierLoad = (async function () {
      await loadEntities();
      const entry = manifestEntry('dossier');
      if (!entry) return;
      const payload = await loadSegment(entry.id);
      if (payload && payload.entitySummary) _entityDoc.entitySummary = payload.entitySummary;
      if (payload && payload.relations) _entityDoc.relations = payload.relations;
      _applyEntityData(_entityDoc);
    })();
  }
  return _dossierLoad;
}

export function hasLazyEntities() {
  return !!manifestEntry('entities');
}
