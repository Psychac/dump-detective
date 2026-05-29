// Header, health scorecard, and executive summary renderers.
// Covers both single-dump and trend modes; the isTrend flag selects the appropriate layout.
import { el, t, formatBytes } from './report.dom.js';
import { domainAnchorId, findingAnchorId } from './report.renderers.shared.js';

// ── Report header (hero + meta-stat rows) ────────────────────────────────────

export function buildHeader(doc) {
  const isTrend = !!(doc.isTrendReport || doc['$kind'] === 'trend');
  const title = isTrend ? 'Trend Analysis' : 'Analysis Report';
  const rawName = (doc.dumpPath || 'report').replace(/\\/g, '/').split('/').pop() || 'report';
  const exportName = rawName.replace(/\.[^.]+$/, '') || 'report';
  const ctx = doc.incidentContext || {};
  const execSum = doc.executiveSummary || {};
  const snapshots = Array.isArray(ctx.trendSnapshots) ? ctx.trendSnapshots : [];
  const paths = Array.isArray(doc.trendDumpPaths) && doc.trendDumpPaths.length
    ? doc.trendDumpPaths
    : (snapshots.length
      ? snapshots.map(function (s) { return s && s.dumpPath ? s.dumpPath : ''; }).filter(function (p) { return !!p; })
      : (Array.isArray(doc.perDumpDocs)
        ? doc.perDumpDocs.map(function (d) { return d && d.dumpPath ? d.dumpPath : ''; }).filter(function (p) { return !!p; })
        : []));
  const trendDumpCount = doc.trendDumpCount || paths.length || snapshots.length;

  const sec = el('section', 'section-card header-card report-header');
  sec.id = 'report-header';
  sec.setAttribute('data-component-id', 'report-header');
  const headerLegacyAnchor = el('span', 'section-anchor-legacy');
  headerLegacyAnchor.id = 'sec-header';
  headerLegacyAnchor.setAttribute('aria-hidden', 'true');
  sec.appendChild(headerLegacyAnchor);

  // ── Hero band ────────────────────────────────────────────────────────────
  const hero = el('div', 'header-hero');

  const heroLeft = el('div', 'header-hero__left');
  const badge = el('div', 'header-hero__badge');
  const dot = el('span', 'header-hero__badge-dot'); badge.appendChild(dot); badge.appendChild(t('DumpDetective'));
  heroLeft.appendChild(badge);
  const titleWrap = el('div', 'header-hero__title-wrap');
  const h1 = document.createElement('h1'); h1.className = 'header-hero__title'; h1.textContent = title; titleWrap.appendChild(h1);
  if (isTrend && trendDumpCount > 0) {
    const dumpBadge = el('span', 'header-hero__trend-count');
    dumpBadge.textContent = trendDumpCount + '\u202Fdumps'; titleWrap.appendChild(dumpBadge);
  }
  heroLeft.appendChild(titleWrap);
  hero.appendChild(heroLeft);

  const heroActions = el('div', 'header-hero__actions');
  heroActions.setAttribute('role', 'toolbar'); heroActions.setAttribute('aria-label', 'Report actions');
  function heroBtn(id, ariaLabel, txt) { const btn = el('button', 'header-hero__btn'); btn.type = 'button'; btn.id = id; btn.dataset.filename = exportName; btn.setAttribute('aria-label', ariaLabel); btn.textContent = txt; return btn; }
  heroActions.appendChild(heroBtn('btn-download-json', 'Download report as JSON', '\u2B07\u202FJSON'));
  heroActions.appendChild(heroBtn('btn-export-csv', 'Export findings as CSV', '\u2B07\u202FCSV'));
  heroActions.appendChild(heroBtn('btn-print', 'Print this report', '\u2399\u202FPrint'));
  heroActions.appendChild(heroBtn('btn-toggle-contrast', 'Toggle high contrast mode', '\u25D1\u202FContrast'));
  hero.appendChild(heroActions);
  sec.appendChild(hero);

  // ── Body (meta-stat rows) ────────────────────────────────────────────────
  const body = el('div', 'header-body');

  function splitPathParts(path) {
    const normalized = String(path || '').replace(/\\/g, '/');
    const lastSlash = normalized.lastIndexOf('/');
    if (lastSlash < 0) return { fileName: normalized, directory: '' };
    return {
      fileName: normalized.slice(lastSlash + 1),
      directory: normalized.slice(0, lastSlash)
    };
  }

  function statItem(label, value) {
    const d = el('div', 'header-stat');
    const l = el('span', 'header-stat__label'); l.textContent = label;
    const v = el('span', 'header-stat__value'); v.textContent = value;
    d.appendChild(l); d.appendChild(v); return d;
  }
  function statRow(groupLabel, items) {
    if (!items.length) return null;
    const row = el('div', 'header-meta-row');
    const grpBadge = el('span', 'header-meta-row__group'); grpBadge.textContent = groupLabel; row.appendChild(grpBadge);
    for (const [lbl, val] of items) row.appendChild(statItem(lbl, val));
    return row;
  }
  function fmtDate(utcStr) {
    if (!utcStr) return '';
    const dt = new Date(utcStr);
    if (Number.isNaN(dt.getTime())) return '';
    return dt.toLocaleString(undefined, {
      month: 'short',
      day: 'numeric',
      year: 'numeric',
      hour: '2-digit',
      minute: '2-digit'
    });
  }

  function fmtDateWithZone(utcStr) {
    if (!utcStr) return '';
    const dt = new Date(utcStr);
    if (Number.isNaN(dt.getTime())) return '';
    return dt.toLocaleString(undefined, {
      month: 'short',
      day: 'numeric',
      year: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
      timeZoneName: 'short'
    });
  }

  if (isTrend) {
    // ── Trend: dump-range row + lifecycle + collapsible dump list ───────────
    const baseSnap = snapshots.find(function (s) { return s && s.index === 0; });
    const curSnap  = snapshots.find(function (s) { return s && s.index === paths.length - 1; }) || snapshots[snapshots.length - 1];
    const baseMs   = baseSnap ? (baseSnap.dumpCapturedAtUtc || baseSnap.generatedAtUtc) : null;
    const curMs    = curSnap  ? (curSnap.dumpCapturedAtUtc  || curSnap.generatedAtUtc)  : null;
    const baseMsN  = baseMs ? new Date(baseMs).getTime() : null;
    const curMsN   = curMs  ? new Date(curMs).getTime()  : null;
    function fmtShort(ms) {
      if (ms == null) return '?';
      return new Date(ms).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
    }
    function fmtSpan(msA, msB) {
      if (msA == null || msB == null) return null;
      const days = Math.round(Math.abs(msB - msA) / 86400000);
      if (days < 1)   return 'same day';
      if (days === 1) return '1 day';
      if (days < 30)  return days + ' days';
      const months = Math.round(days / 30.44);
      if (months < 12) return months + (months === 1 ? ' month' : ' months');
      const years = (days / 365.25).toFixed(1).replace(/\.0$/, '');
      return years + (years === '1' ? ' year' : ' years');
    }
    const rangeRow = el('div', 'header-trend-range');
    const rl = el('span', 'header-trend-range__label'); rl.textContent = 'Analysis span'; rangeRow.appendChild(rl);
    const rd = el('span', 'header-trend-range__dates');
    rd.textContent = fmtShort(baseMsN) + '\u2002\u2192\u2002' + fmtShort(curMsN);
    rangeRow.appendChild(rd);
    const span = fmtSpan(baseMsN, curMsN);
    const dumpCount = trendDumpCount;
    const meta = el('span', 'header-trend-range__count');
    meta.textContent = (span ? span + '\u2002\u00B7\u2002' : '') + dumpCount + ' dump' + (dumpCount !== 1 ? 's' : '');
    rangeRow.appendChild(meta);
    body.appendChild(rangeRow);
  } else if (doc.dumpPath) {
    // ── Single dump: source row (name + path + size/time badges) ────────
    const dumpParts = splitPathParts(doc.dumpPath);
    const pathRow = el('div', 'header-path');
    const pathLabel = el('span', 'header-path__label'); pathLabel.textContent = 'Source'; pathRow.appendChild(pathLabel);

    const source = el('div', 'header-path__source');
    const pathName = el('span', 'header-path__name');
    pathName.textContent = dumpParts.fileName || rawName;
    source.appendChild(pathName);

    const pathVal = el('span', 'header-path__value');
    pathVal.textContent = dumpParts.directory || doc.dumpPath;
    source.appendChild(pathVal);
    pathRow.appendChild(source);

    const meta = el('div', 'header-path__meta');
    const fsBytes = ctx.dumpFileSizeBytes != null ? Number(ctx.dumpFileSizeBytes) : null;
    if (fsBytes !== null && fsBytes > 0) {
      const sizeBadge = el('span', 'header-path__size');
      sizeBadge.textContent = formatBytes(fsBytes) + (ctx.dumpSizeTierLabel ? '\u2002\u00B7\u2002' + ctx.dumpSizeTierLabel : '');
      meta.appendChild(sizeBadge);
    }
    const capturedAt = fmtDate(ctx.dumpCapturedAtUtc || ctx.generatedAtUtc || doc.generatedAtUtc);
    if (capturedAt) {
      const tsBadge = el('span', 'header-path__generated');
      tsBadge.textContent = 'Generated ' + capturedAt;
      meta.appendChild(tsBadge);
    }
    if (meta.childNodes.length > 0) pathRow.appendChild(meta);
    body.appendChild(pathRow);
  }

  // Row 1 — Analysis run (both modes)
  const genRaw = doc.generatedAtUtc;
  const genStr = fmtDateWithZone(genRaw);
  const runItems = [];
  if (genStr) runItems.push(['Analyzed at (local)', genStr]);
  runItems.push(['Elapsed', ((doc.elapsedSeconds) || 0).toFixed(1) + 's']);
  if (doc.schemaVersion) runItems.push(['Schema', doc.schemaVersion]);
  const scoringModelVersion = (doc.scoringModelVersion || execSum.actionScoringModelVersion || '').toString().trim();
  if (scoringModelVersion) runItems.push(['Scoring model', scoringModelVersion]);
  if (doc.analyzerVersion) runItems.push(['Version', doc.analyzerVersion]);
  const runRow = statRow('Analysis run', runItems); if (runRow) body.appendChild(runRow);

  // Row 2 — Runtime environment (both modes)
  const rtItems = [];
  if (ctx.runtimeVersion || ctx.runtimeFlavor) { const rv = [ctx.runtimeVersion, ctx.runtimeFlavor].filter(Boolean).join(' / '); rtItems.push(['Runtime', rv]); }
  if (ctx.gcMode) rtItems.push(['GC mode', ctx.gcMode]);
  if (ctx.heapCount != null) rtItems.push(['Logical heaps', String(ctx.heapCount)]);
  if (ctx.heapCanWalk != null) rtItems.push(['Heap walkable', ctx.heapCanWalk ? 'Yes' : 'No']);
  const rtRow = statRow('Runtime', rtItems); if (rtRow) body.appendChild(rtRow);

  if (!isTrend) {
    // Row 3 — Managed heap snapshot (single dump only)
    const heapItems = [];
    if (execSum.totalManagedBytes != null) heapItems.push(['Total managed', formatBytes(Number(execSum.totalManagedBytes || 0))]);
    if (execSum.totalObjects != null) heapItems.push(['Objects', Number(execSum.totalObjects).toLocaleString('en-US')]);
    if (execSum.uniqueTypes != null) heapItems.push(['Unique types', Number(execSum.uniqueTypes).toLocaleString('en-US')]);
    const heapRow = statRow('Managed heap', heapItems); if (heapRow) body.appendChild(heapRow);
  }

  if (isTrend) {
    // Finding lifecycle row + collapsible dump list (trend only)
    const hasLifecycle = doc.trendNewFindingCount != null || doc.trendPersistentFindingCount != null || doc.trendResolvedFindingCount != null;
    if (hasLifecycle) {
      const lcItems = [];
      if (doc.trendNewFindingCount        != null) lcItems.push(['New',        String(doc.trendNewFindingCount)]);
      if (doc.trendPersistentFindingCount != null) lcItems.push(['Persistent', String(doc.trendPersistentFindingCount)]);
      if (doc.trendResolvedFindingCount   != null) lcItems.push(['Resolved',   String(doc.trendResolvedFindingCount)]);
      const lcRow = statRow('Findings', lcItems); if (lcRow) body.appendChild(lcRow);
    }

    if (paths.length > 0) {
      const dumpListWrap = el('div', 'header-trend-dumps');
      const useCollapse = paths.length >= 5;
      let listEl;
      if (useCollapse) {
        const details = document.createElement('details'); details.className = 'header-trend-dumps__details';
        const summary = document.createElement('summary'); summary.className = 'header-trend-dumps__summary';
        summary.textContent = trendDumpCount + ' dumps \u2014 expand to see all'; details.appendChild(summary);
        listEl = el('div', 'header-trend-dumps__list'); details.appendChild(listEl);
        dumpListWrap.appendChild(details);
      } else {
        listEl = el('div', 'header-trend-dumps__list');
        dumpListWrap.appendChild(listEl);
      }
      for (let i = 0; i < paths.length; i++) {
        const snap = snapshots.find(function (s) { return s && s.index === i; });
        const pathParts = splitPathParts(paths[i]);
        const roleLabel = i === 0 ? 'Baseline' : 'D' + (i + 1);
        const roleCss   = i === 0 ? 'baseline' : i === paths.length - 1 ? 'last' : 'mid';
        const drow = el('div', 'header-trend-dump header-trend-dump--' + roleCss);
        const roleBadge = el('span', 'header-trend-dump__role'); roleBadge.textContent = roleLabel; drow.appendChild(roleBadge);
        const src = el('div', 'header-trend-dump__source');
        const name = el('span', 'header-trend-dump__name'); name.textContent = pathParts.fileName || ('Dump ' + (i + 1)); src.appendChild(name);
        const pv = el('span', 'header-trend-dump__path'); pv.textContent = pathParts.directory || paths[i]; src.appendChild(pv);
        drow.appendChild(src);

        const meta = el('div', 'header-trend-dump__meta');
        if (snap && snap.dumpFileSizeBytes != null && snap.dumpFileSizeBytes > 0) {
          const sz = el('span', 'header-trend-dump__size'); sz.textContent = formatBytes(Number(snap.dumpFileSizeBytes)); meta.appendChild(sz);
        }
        if (snap) {
          const ts = el('span', 'header-trend-dump__ts');
          const tsText = fmtDate(snap.dumpCapturedAtUtc || snap.generatedAtUtc);
          if (tsText) {
            ts.textContent = 'Generated ' + tsText;
            meta.appendChild(ts);
          }
        }
        if (meta.childNodes.length > 0) drow.appendChild(meta);
        listEl.appendChild(drow);
      }
      body.appendChild(dumpListWrap);
    }
  }

  sec.appendChild(body);
  return sec;
}

// ── Health Scorecard (single-dump rows + trend cards with timeline bars) ──────

export function buildHealthScorecard(doc) {
  const scorecard = doc.healthScorecard;
  if (!scorecard || !Array.isArray(scorecard.domains) || !scorecard.domains.length) return null;

  const domainAnchorMap = new Map();
  if (Array.isArray(doc.domains)) {
    for (let i = 0; i < doc.domains.length; i++) {
      const d = doc.domains[i];
      const key = String((d && d.domain) || '').toLowerCase();
      if (!key || domainAnchorMap.has(key)) continue;
      domainAnchorMap.set(key, domainAnchorId(d, i));
    }
  }

  function sevInfo(sev) {
    const s = String(sev).toLowerCase(); const n = Number(sev);
    if (n === 3 || s === 'critical') return { label: 'Critical', css: 'critical', dot: '\u25CF' };
    if (n === 2 || s === 'warning')  return { label: 'Warning',  css: 'warning',  dot: '\u25CF' };
    if (n === 1 || s === 'ok')       return { label: 'OK',       css: 'ok',       dot: '\u2713' };
    return                                   { label: 'Unknown',  css: 'unknown',  dot: '\u25CB' };
  }

  const sec = el('section', 'section-card health-scorecard');
  sec.id = 'health-scorecard';
  sec.setAttribute('data-component-id', 'health-scorecard');
  const healthLegacyAnchor = el('span', 'section-anchor-legacy');
  healthLegacyAnchor.id = 'sec-health';
  healthLegacyAnchor.setAttribute('aria-hidden', 'true');
  sec.appendChild(healthLegacyAnchor);

  // ── Overall banner ───────────────────────────────────────────────────────
  const overall = sevInfo(scorecard.overallSeverity);
  const banner = el('div', 'health-scorecard__banner health-scorecard__banner--' + overall.css);

  const bannerLeft = el('div', 'health-scorecard__banner-left');
  const bannerTitle = el('span', 'health-scorecard__banner-title'); bannerTitle.textContent = 'Health Summary'; bannerLeft.appendChild(bannerTitle);
  const verdict = el('span', 'health-scorecard__banner-verdict'); verdict.textContent = overall.dot + '\u2002' + overall.label; bannerLeft.appendChild(verdict);
  banner.appendChild(bannerLeft);

  const totalCrit = scorecard.domains.reduce((s, d) => s + (d.criticalCount || 0), 0);
  const totalWarn = scorecard.domains.reduce((s, d) => s + (d.warningCount || 0), 0);
  if (totalCrit > 0 || totalWarn > 0) {
    const bannerRight = el('div', 'health-scorecard__banner-right');
    if (totalCrit > 0) {
      const cs = el('div', 'health-scorecard__banner-stat health-scorecard__banner-stat--critical');
      const cl = el('span', 'health-scorecard__banner-stat-label'); cl.textContent = 'Critical'; cs.appendChild(cl);
      const cv = el('span', 'health-scorecard__banner-stat-value'); cv.textContent = String(totalCrit); cs.appendChild(cv);
      bannerRight.appendChild(cs);
    }
    if (totalWarn > 0) {
      const ws = el('div', 'health-scorecard__banner-stat health-scorecard__banner-stat--warning');
      const wl = el('span', 'health-scorecard__banner-stat-label'); wl.textContent = 'Warning'; ws.appendChild(wl);
      const wv = el('span', 'health-scorecard__banner-stat-value'); wv.textContent = String(totalWarn); ws.appendChild(wv);
      bannerRight.appendChild(ws);
    }
    banner.appendChild(bannerRight);
  }
  sec.appendChild(banner);

  if (totalCrit > 0) {
    const sticky = el('div', 'critical-sticky-bar');
    sticky.id = 'critical-sticky-bar';
    const txt = el('span', 'critical-sticky-bar__text');
    txt.textContent = totalCrit + ' Critical finding' + (totalCrit !== 1 ? 's' : '') + ' require immediate attention';
    sticky.appendChild(txt);

    const jump = document.createElement('a');
    jump.className = 'critical-sticky-bar__jump';
    jump.href = '#sec-action-queue';
    jump.textContent = 'Review now';
    sticky.appendChild(jump);

    const dismiss = document.createElement('button');
    dismiss.type = 'button';
    dismiss.className = 'critical-sticky-bar__dismiss';
    dismiss.id = 'critical-sticky-dismiss';
    dismiss.setAttribute('aria-label', 'Dismiss critical banner');
    dismiss.textContent = 'Dismiss';
    sticky.appendChild(dismiss);
    sec.appendChild(sticky);
  }

  // ── Domain grid ──────────────────────────────────────────────────────────
  const grid = el('div', 'health-scorecard__grid');
  grid.setAttribute('role', 'list');
  const domainOrder = ['Leaks', 'Memory', 'GC', 'TypeSystem', 'Threads', 'Async', 'Exceptions', 'Runtime'];
  const domainMap = new Map();
  for (const entry of scorecard.domains) domainMap.set((entry.domain || '').toLowerCase(), entry);
  const ordered = [];
  for (const d of domainOrder) { const e = domainMap.get(d.toLowerCase()); if (e) ordered.push(e); }
  for (const [, e] of domainMap) { if (!domainOrder.map(d => d.toLowerCase()).includes((e.domain || '').toLowerCase())) ordered.push(e); }

  // Detect trend mode: any entry has a non-null 'change' field
  const isTrendScorecard = ordered.some(function (e) { return e.change != null; });

  // For trend mode: single legend above grid explaining the timeline bar
  if (isTrendScorecard) {
    const hasAnyHistory = ordered.some(function (e) { return Array.isArray(e.severityHistory) && e.severityHistory.length > 2; });
    if (hasAnyHistory) {
      const snapshotCount = ordered.find(function (e) { return Array.isArray(e.severityHistory); })?.severityHistory?.length || 0;
      const legend = el('div', 'health-scorecard__legend');
      const legendBar = el('span', 'health-scorecard__legend-bar');
      for (const css of ['ok', 'warning', 'critical']) {
        const seg = el('span', 'health-timeline-seg health-timeline-seg--' + css); legendBar.appendChild(seg);
      }
      legend.appendChild(legendBar);
      const legendText = el('span', 'health-scorecard__legend-text');
      legendText.textContent = 'Severity trend bar — Base \u2192 D' + snapshotCount + ' (' + snapshotCount + ' dumps)';
      legend.appendChild(legendText);
      sec.appendChild(legend);
    }
  }

  // change enum: Stable=0, Improved=1, Regressed=2, NewDomain=3, Removed=4
  function changeInfo(change) {
    const c = Number(change); const s = String(change).toLowerCase();
    if (c === 1 || s === 'improved')   return { label: '\u2191 Improved',  css: 'improved'  };
    if (c === 2 || s === 'regressed')  return { label: '\u2193 Regressed', css: 'regressed' };
    if (c === 3 || s === 'newdomain')  return { label: '\u2605 New',       css: 'new'       };
    if (c === 4 || s === 'removed')    return { label: '\u2715 Removed',   css: 'resolved'  };
    return                                    { label: '\u2192 Stable',    css: 'stable'    };
  }

  for (const entry of ordered) {
    const si = sevInfo(entry.severity);

    if (isTrendScorecard) {
      // ── Trend: vertical card with timeline bar ───────────────────────────
      const card = el('div', 'health-domain-card health-domain-card--' + si.css);
      card.setAttribute('role', 'listitem');
      const domainTarget = domainAnchorMap.get(String(entry.domain || '').toLowerCase());
      if (domainTarget) card.dataset.domainTarget = domainTarget;

      const head = el('div', 'health-domain-card__head');
      const nameEl = el('span', 'health-domain-card__name'); nameEl.textContent = entry.domain || ''; head.appendChild(nameEl);
      if (entry.change != null) {
        const ci = changeInfo(entry.change);
        const chg = el('span', 'health-domain-card__change health-domain-card__change--' + ci.css);
        chg.textContent = ci.label; head.appendChild(chg);
      }
      card.appendChild(head);

      const hasHistory = Array.isArray(entry.severityHistory) && entry.severityHistory.length > 2;
      if (hasHistory) {
        const wrap = el('div', 'health-domain-card__timeline-wrap');
        const bar = el('div', 'health-domain-card__timeline');
        for (let i = 0; i < entry.severityHistory.length; i++) {
          const hsi = sevInfo(entry.severityHistory[i]);
          const seg = el('span', 'health-timeline-seg health-timeline-seg--' + hsi.css);
          const role = i === 0 ? 'Baseline' : i === entry.severityHistory.length - 1 ? 'Current' : 'Dump';
          seg.title = role + ' #' + (i + 1) + ' \u2014 ' + hsi.label;
          bar.appendChild(seg);
        }
        wrap.appendChild(bar);
        const indices = el('div', 'health-domain-card__timeline-indices');
        for (let i = 0; i < entry.severityHistory.length; i++) {
          const idx = el('span', 'health-timeline-idx' + (i === 0 ? ' health-timeline-idx--first' : ''));
          idx.textContent = i === 0 ? 'Base' : 'D' + (i + 1);
          indices.appendChild(idx);
        }
        wrap.appendChild(indices);
        card.appendChild(wrap);
      } else if (entry.baselineSeverity != null) {
        const baseSi = sevInfo(entry.baselineSeverity);
        const transition = el('div', 'health-domain-card__transition');
        const baseSpan = el('span', 'health-domain-card__trans-sev health-domain-card__trans-sev--' + baseSi.css);
        baseSpan.textContent = baseSi.label; baseSpan.title = 'Baseline'; transition.appendChild(baseSpan);
        const arrow = el('span', 'health-domain-card__trans-arrow'); arrow.textContent = '\u2192'; transition.appendChild(arrow);
        const curSpan = el('span', 'health-domain-card__trans-sev health-domain-card__trans-sev--' + si.css + ' health-domain-card__trans-sev--current');
        curSpan.textContent = si.label; curSpan.title = 'Current'; transition.appendChild(curSpan);
        card.appendChild(transition);
      }

      const foot = el('div', 'health-domain-card__foot');
      const curPill = el('span', 'health-domain-card__sev health-domain-card__sev--' + si.css);
      curPill.textContent = si.dot + '\u2002' + si.label; foot.appendChild(curPill);
      const crit = entry.criticalCount || 0; const warn = entry.warningCount || 0;
      if (crit > 0 || warn > 0) {
        const counts = el('span', 'health-domain-card__counts');
        if (crit > 0) { const c = el('span', 'health-domain-card__count-chip health-domain-card__count-chip--crit'); c.textContent = crit + '\u00A0crit'; counts.appendChild(c); }
        if (warn > 0) { const w = el('span', 'health-domain-card__count-chip health-domain-card__count-chip--warn'); w.textContent = warn + '\u00A0warn'; counts.appendChild(w); }
        foot.appendChild(counts);
      }
      card.appendChild(foot);
      grid.appendChild(card);

    } else {
      // ── Single-dump: compact horizontal row ─────────────────────────────
      const row = el('div', 'health-domain-row health-domain-row--' + si.css);
      row.setAttribute('role', 'listitem');
      const domainTarget = domainAnchorMap.get(String(entry.domain || '').toLowerCase());
      if (domainTarget) row.dataset.domainTarget = domainTarget;
      const name = el('span', 'health-domain-row__name'); name.textContent = entry.domain || ''; row.appendChild(name);
      const pill = el('span', 'health-domain-row__pill health-domain-row__pill--' + si.css);
      pill.textContent = si.dot + '\u2002' + si.label; row.appendChild(pill);
      const crit = entry.criticalCount || 0; const warn = entry.warningCount || 0;
      if (crit > 0 || warn > 0) {
        const counts = el('span', 'health-domain-row__counts');
        const parts = [];
        if (crit > 0) parts.push(crit + '\u00A0crit');
        if (warn > 0) parts.push(warn + '\u00A0warn');
        counts.textContent = parts.join('\u2002\u00B7\u2002'); row.appendChild(counts);
      }
      grid.appendChild(row);
    }
  }
  sec.appendChild(grid);
  return sec;
}

// ── Executive Summary (KPI strip + scores + findings + recommendations) ───────
// Includes lifecycle strip for trend mode.

export function buildExecutiveSummary(doc) {
  const summary = doc.executiveSummary;
  if (!summary) return null;

  const sec = el('section', 'section-card executive-summary');
  sec.id = 'executive-summary';
  sec.setAttribute('data-component-id', 'executive-summary');
  const execLegacyAnchor = el('span', 'section-anchor-legacy');
  execLegacyAnchor.id = 'sec-exec';
  execLegacyAnchor.setAttribute('aria-hidden', 'true');
  sec.appendChild(execLegacyAnchor);

  // T2a — Lifecycle Summary strip (trend mode only)
  const isTrendExecSection = !!(doc['$kind'] === 'trend' || doc.isTrendReport);
  if (isTrendExecSection) {
    const lifecycle = el('div', 'exec-lifecycle-strip');
    const lcTitle = el('div', 'exec-lifecycle-strip__title'); lcTitle.textContent = 'Finding Lifecycle'; lifecycle.appendChild(lcTitle);
    const chips = el('div', 'exec-lifecycle-strip__chips');
    function lcChip(label, count, mod) {
      if (count == null) return;
      const chip = el('div', 'exec-lifecycle-chip exec-lifecycle-chip--' + mod);
      const cv = el('span', 'exec-lifecycle-chip__count'); cv.textContent = String(count); chip.appendChild(cv);
      const cl = el('span', 'exec-lifecycle-chip__label'); cl.textContent = label; chip.appendChild(cl);
      chips.appendChild(chip);
    }
    const snapshots = (doc.incidentContext && Array.isArray(doc.incidentContext.trendSnapshots))
      ? doc.incidentContext.trendSnapshots
      : [];
    const dumpCount = (doc.trendDumpCount != null)
      ? Number(doc.trendDumpCount)
      : (Array.isArray(doc.trendDumpPaths) ? doc.trendDumpPaths.length : snapshots.length);
    const newCount = (doc.trendNewFindingCount != null) ? Number(doc.trendNewFindingCount) : 0;
    const persistentCount = (doc.trendPersistentFindingCount != null) ? Number(doc.trendPersistentFindingCount) : 0;
    const resolvedCount = (doc.trendResolvedFindingCount != null) ? Number(doc.trendResolvedFindingCount) : 0;
    const net = newCount - resolvedCount;

    lcChip('Dumps', dumpCount, 'meta');
    lcChip('New', newCount, 'new');
    lcChip('Persistent', persistentCount, 'persistent');
    lcChip('Resolved', resolvedCount, 'resolved');
    lcChip('Net', net, net > 0 ? 'worse' : net < 0 ? 'better' : 'flat');
    lifecycle.appendChild(chips);
    sec.appendChild(lifecycle);

    if (dumpCount > 0 || snapshots.length >= 2) {
      const windowRow = el('div', 'exec-lifecycle-strip exec-lifecycle-strip--window');
      const wTitle = el('div', 'exec-lifecycle-strip__title');
      wTitle.textContent = 'Snapshot Window';
      windowRow.appendChild(wTitle);

      const first = snapshots.length ? snapshots[0] : null;
      const last = snapshots.length ? snapshots[snapshots.length - 1] : null;
      const firstTs = first ? (first.dumpCapturedAtUtc || first.generatedAtUtc) : null;
      const lastTs = last ? (last.dumpCapturedAtUtc || last.generatedAtUtc) : null;
      const firstLabel = firstTs ? new Date(firstTs).toLocaleString() : 'baseline';
      const lastLabel = lastTs ? new Date(lastTs).toLocaleString() : 'current';
      const count = dumpCount > 0 ? dumpCount : snapshots.length;

      const text = el('div', 'exec-kpi__label');
      text.textContent = String(count) + ' dump' + (Number(count) === 1 ? '' : 's') + ': ' + firstLabel + ' -> ' + lastLabel;
      windowRow.appendChild(text);
      sec.appendChild(windowRow);
    }
  }

  function toNumber(value) {
    if (value == null) return null;
    const n = Number(value);
    return Number.isFinite(n) ? n : null;
  }

  function formatInt(value) {
    const n = toNumber(value);
    return n == null ? null : Math.round(n).toLocaleString('en-US');
  }

  function statusClass(level) {
    return level === 'critical' || level === 'warning' || level === 'ok' ? level : 'ok';
  }

  // ── KPI dashboard (single dump only) ────────────────────────────────────
  if (!isTrendExecSection) {
    const dashboard = el('div', 'exec-kpi-dashboard');

    const totalManagedBytes = toNumber(summary.totalManagedBytes);
    const lohBytes = toNumber(summary.lohBytes);
    const lohPercent = toNumber(summary.lohPercent);
    const gen2Percent = toNumber(summary.gen2Percent);
    const leakCandidateCount = toNumber(summary.leakCandidateCount);
    const finalizerQueueCount = toNumber(summary.finalizerQueueCount);
    const blockedThreads = toNumber(summary.blockedThreads);
    const deadlockCycles = toNumber(summary.deadlockCycles);
    const hangScore = toNumber(summary.hangScore);
    const gcPressureScore = toNumber(summary.gcPressureScore);
    const activeExceptions = toNumber(summary.activeExceptions);

    function buildKpiTile(label, value, context, status, thresholdText) {
      if (value == null || value === '') return null;
      const tile = el('section', 'exec-kpi-tile exec-kpi-tile--' + statusClass(status));
      const head = el('div', 'exec-kpi-tile__head');
      const lbl = el('span', 'exec-kpi-tile__label'); lbl.textContent = label; head.appendChild(lbl);
      const state = el('span', 'exec-kpi-tile__state exec-kpi-tile__state--' + statusClass(status));
      state.textContent = statusClass(status).toUpperCase();
      head.appendChild(state);
      tile.appendChild(head);

      const val = el('div', 'exec-kpi-tile__value'); val.textContent = String(value); tile.appendChild(val);
      if (context) {
        const ctx = el('div', 'exec-kpi-tile__context');
        ctx.textContent = context;
        tile.appendChild(ctx);
      }
      if (thresholdText) {
        const hint = el('div', 'exec-kpi-tile__threshold');
        hint.textContent = thresholdText;
        tile.appendChild(hint);
      }
      return tile;
    }

    let heapStatus = 'ok';
    if ((lohPercent != null && lohPercent >= 25) || (gen2Percent != null && gen2Percent >= 75)) heapStatus = 'critical';
    else if ((lohPercent != null && lohPercent >= 15) || (gen2Percent != null && gen2Percent >= 60)) heapStatus = 'warning';
    const heapContext = [
      lohBytes != null ? ('LOH ' + formatBytes(lohBytes) + (lohPercent != null ? ' (' + lohPercent.toFixed(1) + '%)' : '')) : null,
      gen2Percent != null ? ('Gen2 ' + gen2Percent.toFixed(1) + '%') : null
    ].filter(Boolean).join(' | ');

    let leakStatus = 'ok';
    if ((leakCandidateCount != null && leakCandidateCount >= 10) || (finalizerQueueCount != null && finalizerQueueCount > 5000)) leakStatus = 'critical';
    else if ((leakCandidateCount != null && leakCandidateCount > 0) || (finalizerQueueCount != null && finalizerQueueCount > 1000)) leakStatus = 'warning';
    const leakContext = [
      leakCandidateCount != null ? (Math.round(leakCandidateCount) + ' suspects') : null,
      finalizerQueueCount != null ? ('Finalizer queue ' + finalizerQueueCount.toLocaleString('en-US')) : null
    ].filter(Boolean).join(' | ');

    let threadStatus = 'ok';
    if ((deadlockCycles != null && deadlockCycles > 0) || (blockedThreads != null && blockedThreads >= 20) || (hangScore != null && hangScore < 25)) threadStatus = 'critical';
    else if ((blockedThreads != null && blockedThreads > 0) || (hangScore != null && hangScore < 50)) threadStatus = 'warning';
    const threadContext = [
      blockedThreads != null ? ('Blocked ' + Math.round(blockedThreads)) : null,
      deadlockCycles != null ? ('Deadlocks ' + Math.round(deadlockCycles)) : null,
      hangScore != null ? ('Hang score ' + Math.round(hangScore) + '/100') : null
    ].filter(Boolean).join(' | ');

    let runtimeStatus = 'ok';
    if ((activeExceptions != null && activeExceptions > 0) || (gcPressureScore != null && gcPressureScore >= 85)) runtimeStatus = 'critical';
    else if ((gcPressureScore != null && gcPressureScore >= 66)) runtimeStatus = 'warning';
    const gcPressureLabel = summary.gcPressureLevel || (gcPressureScore != null ? (Math.round(gcPressureScore) + '/100') : null);
    const runtimeContext = [
      gcPressureLabel ? ('GC pressure ' + gcPressureLabel) : null,
      activeExceptions != null ? ('Active exceptions ' + formatInt(activeExceptions)) : null
    ].filter(Boolean).join(' | ');

    const tiles = [
      buildKpiTile(
        'Managed Heap',
        totalManagedBytes != null ? formatBytes(totalManagedBytes) : null,
        heapContext,
        heapStatus,
        'Warn: LOH >= 15% or Gen2 >= 60% | Crit: LOH >= 25% or Gen2 >= 75%'
      ),
      buildKpiTile(
        'Leak Signals',
        leakCandidateCount != null ? Math.round(leakCandidateCount).toLocaleString('en-US') : null,
        leakContext,
        leakStatus,
        'Warn: suspects > 0 or finalizer queue > 1,000 | Crit: suspects >= 10 or finalizer queue > 5,000'
      ),
      buildKpiTile(
        'Threading Risk',
        blockedThreads != null ? Math.round(blockedThreads).toLocaleString('en-US') : null,
        threadContext,
        threadStatus,
        'Warn: blocked > 0 or hang < 50 | Crit: deadlocks > 0, blocked >= 20, or hang < 25'
      ),
      buildKpiTile(
        'Runtime Pressure',
        gcPressureLabel,
        runtimeContext,
        runtimeStatus,
        'Warn: GC pressure >= 66 | Crit: GC pressure >= 85 or active exceptions > 0'
      )
    ];

    for (const tile of tiles) {
      if (tile) dashboard.appendChild(tile);
    }

    if (dashboard.children.length) sec.appendChild(dashboard);
  }

  // ── Score summary ─────────────────────────────────────────────────────────
  function scoreLevel(s) { return s >= 67 ? 'ok' : s >= 34 ? 'warning' : 'critical'; }
  const isTrendExec = !!(doc['$kind'] === 'trend' || doc.isTrendReport);
  if (isTrendExec && (summary.leakLikelihoodScore != null || summary.gcPressureScore != null || summary.threadContentionScore != null)) {
    const scoreWrap = el('div', 'exec-trend-scores');
    const heading = el('div', 'exec-recommendations__heading');
    heading.textContent = 'Score Deltas (Baseline -> Current)';
    scoreWrap.appendChild(heading);

    const tableWrap = el('div', 'table-wrap');
    const table = document.createElement('table');
    const thead = document.createElement('thead');
    const hr = document.createElement('tr');
    ['Score', 'Baseline', 'Current', 'Delta'].forEach(function (h) {
      const th = document.createElement('th');
      th.textContent = h;
      hr.appendChild(th);
    });
    thead.appendChild(hr);
    table.appendChild(thead);

    const tbody = document.createElement('tbody');
    function appendDeltaRow(label, current, delta) {
      if (current == null) return;
      const tr = document.createElement('tr');
      const baseline = delta == null ? null : (Number(current) - Number(delta));
      const deltaNum = delta == null ? null : Number(delta);
      const deltaClass = deltaNum == null ? 'exec-delta-cell--flat' : (deltaNum > 0 ? 'exec-delta-cell--up' : deltaNum < 0 ? 'exec-delta-cell--down' : 'exec-delta-cell--flat');
      const deltaText = deltaNum == null
        ? '\u2014'
        : ((deltaNum > 0 ? '+' : '') + String(deltaNum) + (deltaNum > 0 ? ' (worse)' : deltaNum < 0 ? ' (better)' : ' (stable)'));
      const cells = [label, baseline == null ? '\u2014' : String(baseline), String(current), deltaText];
      cells.forEach(function (v, idx) {
        const td = document.createElement('td');
        td.textContent = v;
        if (idx === 3) td.className = 'exec-delta-cell ' + deltaClass;
        tr.appendChild(td);
      });
      tbody.appendChild(tr);
    }
    appendDeltaRow('Leak likelihood', summary.leakLikelihoodScore, summary.leakScoreDelta);
    appendDeltaRow('GC pressure', summary.gcPressureScore, summary.gcPressureScoreDelta);
    appendDeltaRow('Thread contention', summary.threadContentionScore, summary.threadContentionScoreDelta);
    table.appendChild(tbody);
    tableWrap.appendChild(table);
    scoreWrap.appendChild(tableWrap);
    sec.appendChild(scoreWrap);
  } else if (summary.leakLikelihoodScore != null || summary.gcPressureScore != null || summary.threadContentionScore != null) {
    const scores = el('div', 'exec-scores');
    function scoreCard(label, sub, score, delta) {
      if (score == null) return;
      const lv = scoreLevel(score);
      const card = el('div', 'exec-score');
      const dial = el('div', 'exec-score__dial exec-score__dial--' + lv); dial.textContent = String(score); card.appendChild(dial);
      const info = el('div', 'exec-score__info');
      const lbl = el('div', 'exec-score__label'); lbl.textContent = label; info.appendChild(lbl);
      const sl = el('div', 'exec-score__sublabel'); sl.textContent = sub; info.appendChild(sl);
      if (isTrendExec && delta != null) {
        const dv = Number(delta);
        const dBadge = el('span', 'score-delta ' + (dv > 0 ? 'score-delta-up' : dv < 0 ? 'score-delta-down' : 'score-delta-flat'));
        dBadge.textContent = (dv > 0 ? '\u2191+' : dv < 0 ? '\u2193' : '\u2192') + Math.abs(dv); info.appendChild(dBadge);
      }
      card.appendChild(info);
      scores.appendChild(card);
    }
    scoreCard('Leak Likelihood', 'memory retention risk', summary.leakLikelihoodScore, summary.leakScoreDelta);
    scoreCard('GC Pressure', 'collection burden', summary.gcPressureScore, summary.gcPressureScoreDelta);
    scoreCard('Thread Contention', 'concurrency health', summary.threadContentionScore, summary.threadContentionScoreDelta);
    sec.appendChild(scores);
  }

  if (isTrendExecSection) {
    const trendFindings = Array.isArray(doc.findings) ? doc.findings : [];
    const summaryRegressions = Array.isArray(summary.topRegressions) ? summary.topRegressions : [];
    const summaryImprovements = Array.isArray(summary.topImprovements) ? summary.topImprovements : [];
    const sevRank = { critical: 3, warning: 2, info: 1 };

    function toPercentDelta(base, current) {
      if (base == null || current == null) return null;
      const b = Number(base);
      const c = Number(current);
      if (!Number.isFinite(b) || !Number.isFinite(c) || Math.abs(b) < 1e-9) return null;
      return ((c - b) * 100) / b;
    }

    function buildTrendTable(title, rows) {
      if (!rows.length) return;
      const wrap = el('div', 'exec-trend-findings');
      const heading = el('div', 'exec-recommendations__heading');
      heading.textContent = title;
      wrap.appendChild(heading);

      const tableWrap = el('div', 'table-wrap');
      const table = document.createElement('table');
      const thead = document.createElement('thead');
      const hr = document.createElement('tr');
      ['Severity', 'Analyzer', 'Metric', 'Baseline', 'Current', 'Delta%'].forEach(function (h) {
        const th = document.createElement('th');
        th.textContent = h;
        hr.appendChild(th);
      });
      thead.appendChild(hr);
      table.appendChild(thead);

      const tbody = document.createElement('tbody');
      for (const row of rows) {
        const tr = document.createElement('tr');
        [row.severity, row.analyzer, row.metric, row.baseline, row.current, row.deltaPct].forEach(function (v, idx) {
          const td = document.createElement('td');
          td.textContent = v;
          if (idx === 5) {
            const n = Number(String(v).replace('%', ''));
            const cls = !Number.isFinite(n) ? 'exec-delta-pct-cell--flat' : (n > 0 ? 'exec-delta-pct-cell--up' : n < 0 ? 'exec-delta-pct-cell--down' : 'exec-delta-pct-cell--flat');
            td.className = 'exec-delta-pct-cell ' + cls;
          }
          tr.appendChild(td);
        });
        tbody.appendChild(tr);
      }
      table.appendChild(tbody);
      tableWrap.appendChild(table);
      wrap.appendChild(tableWrap);
      sec.appendChild(wrap);
    }

    function pickRows(source, maxCount) {
      return source
        .sort(function (a, b) {
          const sa = sevRank[String(a.severity || '').toLowerCase()] || 0;
          const sb = sevRank[String(b.severity || '').toLowerCase()] || 0;
          if (sb !== sa) return sb - sa;
          const av = Math.abs(Number(a.metricValue || 0));
          const bv = Math.abs(Number(b.metricValue || 0));
          return bv - av;
        })
        .slice(0, maxCount)
        .map(function (f) {
          const ev0 = Array.isArray(f.evidenceRefs) && f.evidenceRefs.length ? f.evidenceRefs[0] : null;
          const metric = (ev0 && ev0.metricKey) || f.title || '\u2014';
          const baseline = f.metricBaseline;
          const current = f.metricCurrent;
          const deltaPct = toPercentDelta(baseline, current);
          const metricUnit = f.metricUnit || '';
          return {
            severity: f.severity || '\u2014',
            analyzer: f.analyzer || '\u2014',
            metric: metric,
            baseline: baseline == null ? '\u2014' : String(Number(baseline).toFixed(1)) + (metricUnit ? ' ' + metricUnit : ''),
            current: current == null ? '\u2014' : String(Number(current).toFixed(1)) + (metricUnit ? ' ' + metricUnit : ''),
            deltaPct: deltaPct == null ? '\u2014' : ((deltaPct >= 0 ? '+' : '') + deltaPct.toFixed(1) + '%')
          };
        });
    }

    const regressionSource = summaryRegressions.length
      ? summaryRegressions.slice()
      : trendFindings.filter(function (f) { return Array.isArray(f.tags) && f.tags.indexOf('regression') >= 0; });
    const improvementSource = summaryImprovements.length
      ? summaryImprovements.slice()
      : trendFindings.filter(function (f) { return Array.isArray(f.tags) && f.tags.indexOf('improvement') >= 0; });

    buildTrendTable('Top Regressions', pickRows(regressionSource, 5));
    buildTrendTable('Top Improvements', pickRows(improvementSource, 3));
  }

  // ── Correlation highlights (single + trend) ─────────────────────────────
  const correlationEvents = Array.isArray(doc.correlationEvents) ? doc.correlationEvents : [];
  if (correlationEvents.length) {
    const findings = Array.isArray(doc.findings) ? doc.findings : [];
    const findingByFingerprint = new Map();
    for (let fi = 0; fi < findings.length; fi++) {
      const f = findings[fi] || {};
      const fp = String(f.fingerprint || '').toLowerCase();
      if (!fp || findingByFingerprint.has(fp)) continue;
      findingByFingerprint.set(fp, f);
    }

    function normalizeFingerprint(value) {
      return String(value || '').trim().toLowerCase();
    }

    function resolveFindingByFingerprint(value) {
      const normalized = normalizeFingerprint(value);
      if (!normalized) return null;

      if (findingByFingerprint.has(normalized))
        return findingByFingerprint.get(normalized);

      const tail = normalized.includes('::') ? normalized.split('::').pop() : normalized;
      if (tail && findingByFingerprint.has(tail))
        return findingByFingerprint.get(tail);

      for (const [key, finding] of findingByFingerprint.entries()) {
        if (key.includes(normalized) || normalized.includes(key))
          return finding;
      }

      return null;
    }

    function formatSignalLabel(signalKey) {
      const raw = String(signalKey || '');
      const idx = raw.indexOf(':');
      const token = (idx >= 0 ? raw.slice(idx + 1) : raw).trim().toLowerCase();
      if (!token) return '';

      const pretty = token
        .replace(/[_-]+/g, ' ')
        .replace(/\s+/g, ' ')
        .trim();

      if (pretty === 'thread pool') return 'thread pool pressure';
      if (pretty === 'gc handle') return 'GC handle retention';
      if (pretty === 'connection pool') return 'connection pool pressure';
      return pretty;
    }

    const sectionByAnalyzer = new Map();
    if (Array.isArray(doc.domains)) {
      for (let di = 0; di < doc.domains.length; di++) {
        const domain = doc.domains[di] || {};
        const sections = Array.isArray(domain.sections) ? domain.sections : [];
        for (let si = 0; si < sections.length; si++) {
          const section = sections[si] || {};
          const analyzer = String(section.analyzerName || '').toLowerCase();
          const sectionId = String(section.sectionId || '').trim();
          if (!analyzer || !sectionId || sectionByAnalyzer.has(analyzer)) continue;
          sectionByAnalyzer.set(analyzer, sectionId);
        }
      }
    }

    const domainAnchorMap = new Map();
    if (Array.isArray(doc.domains)) {
      for (let di = 0; di < doc.domains.length; di++) {
        const domain = doc.domains[di] || {};
        const key = String(domain.domain || '').toLowerCase();
        if (!key || domainAnchorMap.has(key)) continue;
        domainAnchorMap.set(key, '#' + domainAnchorId(domain, di));
      }
    }

    function resolveFallbackAnchor(evt) {
      if (Array.isArray(evt.domains)) {
        for (let d = 0; d < evt.domains.length; d++) {
          const domKey = String(evt.domains[d] || '').toLowerCase();
          const anchor = domainAnchorMap.get(domKey);
          if (anchor) return anchor;
        }
      }
      return '#sec-action-queue';
    }

    function resolvePrimaryAnchor(evt) {
      const sourceFingerprints = Array.isArray(evt.sourceFingerprints) ? evt.sourceFingerprints : [];
      for (let p = 0; p < sourceFingerprints.length; p++) {
        const finding = resolveFindingByFingerprint(sourceFingerprints[p]);
        if (!finding) continue;
        return '#' + findingAnchorId(finding, 'corr-title-' + p);
      }
      return resolveFallbackAnchor(evt);
    }

    const corrWrap = el('div', 'exec-correlation');
    const heading = el('div', 'exec-recommendations__heading');
    heading.textContent = 'Correlation Signals';
    corrWrap.appendChild(heading);

    const list = el('div', 'exec-correlation__list');
    for (let i = 0; i < correlationEvents.length && i < 3; i++) {
      const evt = correlationEvents[i] || {};
      const kind = String(evt.eventType || 'co-move').toLowerCase();
      const item = el('article', 'exec-correlation__item exec-correlation__item--' + kind);

      const title = el('div', 'exec-correlation__title');
      const titleLink = document.createElement('a');
      titleLink.className = 'exec-correlation__item-link';
      titleLink.href = resolvePrimaryAnchor(evt);
      titleLink.textContent = evt.title || 'Correlation signal';
      title.appendChild(titleLink);
      item.appendChild(title);

      if (evt.rationale) {
        const rationale = el('div', 'exec-correlation__signals');
        rationale.textContent = evt.rationale;
        item.appendChild(rationale);
      }

      const meta = el('div', 'exec-correlation__meta');
      const confidence = evt.confidence || 'Unknown';
      const domains = Array.isArray(evt.domains) ? evt.domains.join(', ') : 'Unknown';
      meta.textContent = confidence + ' confidence | ' + domains;
      item.appendChild(meta);

      if (Array.isArray(evt.signalKeys) && evt.signalKeys.length) {
        const labels = [];
        const seen = new Set();
        for (let s = 0; s < evt.signalKeys.length && labels.length < 2; s++) {
          const label = formatSignalLabel(evt.signalKeys[s]);
          if (label && !seen.has(label)) {
            seen.add(label);
            labels.push(label);
          }
        }
        const signals = el('div', 'exec-correlation__signals');
        signals.textContent = labels.length ? ('Shared signals: ' + labels.join(' | ')) : '';
        item.appendChild(signals);
      }

      if (Array.isArray(evt.sourceFingerprints) && evt.sourceFingerprints.length) {
        const provenance = el('div', 'exec-correlation__provenance');
        const label = el('span', 'exec-correlation__provenance-label');
        label.textContent = 'Sources:';
        provenance.appendChild(label);

        for (let p = 0; p < evt.sourceFingerprints.length && p < 3; p++) {
          const fp = String(evt.sourceFingerprints[p] || '');
          const finding = resolveFindingByFingerprint(fp);
          const link = document.createElement('a');
          link.className = 'exec-correlation__provenance-link';
          if (finding) {
            link.href = '#' + findingAnchorId(finding, 'corr-' + i + '-' + p);
          } else {
            const analyzerHint = fp.split('::')[0];
            const sectionId = analyzerHint ? sectionByAnalyzer.get(String(analyzerHint).toLowerCase()) : null;
            link.href = sectionId ? ('#' + sectionId) : resolveFallbackAnchor(evt);
          }
          link.textContent = 'F' + (p + 1);
          if (finding && finding.title) link.title = finding.title;
          provenance.appendChild(link);
        }

        item.appendChild(provenance);
      }

      list.appendChild(item);
    }

    corrWrap.appendChild(list);
    sec.appendChild(corrWrap);
  }

  // ── Findings (single dump only) ──────────────────────────────────────────
  if (!isTrendExecSection) {
    const critFindings = summary.criticalFindings || [];
    const warnFindings = summary.warningFindings || [];
    if (critFindings.length || warnFindings.length) {
      const findingsWrap = el('div', 'exec-triage');
      const heading = el('div', 'exec-recommendations__heading');
      heading.textContent = 'Triage';
      findingsWrap.appendChild(heading);

      const grid = el('div', 'exec-triage__grid');
      const combined = critFindings.concat(warnFindings).slice(0, 10);
      for (let fi = 0; fi < combined.length; fi++) {
        const finding = combined[fi];
        const sev = String(finding.severity || 'Info').toLowerCase();
        const row = el('article', 'exec-triage-card exec-triage-card--' + sev);

        const title = el('div', 'exec-triage-card__title');
        title.textContent = finding.title || '';
        row.appendChild(title);

        if (finding.evidence) {
          const ev = el('div', 'exec-triage-card__evidence');
          ev.textContent = finding.evidence;
          row.appendChild(ev);
        }

        if (finding.recommendation) {
          const rec = el('div', 'exec-triage-card__rec');
          rec.textContent = '-> ' + finding.recommendation;
          row.appendChild(rec);
        }

        const meta = el('div', 'exec-triage-card__meta');
        if (finding.analyzer) {
          const analyzer = el('span', 'exec-triage-card__analyzer');
          analyzer.textContent = finding.analyzer;
          meta.appendChild(analyzer);
        }

        if (finding.confidenceScore != null) {
          const confidenceScore = Number(finding.confidenceScore);
          const meter = el('span', 'exec-confidence-meter');
          const slots = Math.max(1, Math.min(4, Math.round(confidenceScore * 4)));
          meter.setAttribute('aria-label', 'Confidence ' + confidenceScore.toFixed(2));
          for (let si = 0; si < 4; si++) {
            const slot = el('span', 'exec-confidence-meter__slot' + (si < slots ? ' exec-confidence-meter__slot--on' : ''));
            meter.appendChild(slot);
          }
          meta.appendChild(meter);
        }

        if (meta.childNodes.length) row.appendChild(meta);
        grid.appendChild(row);
      }

      findingsWrap.appendChild(grid);
      sec.appendChild(findingsWrap);
    }
  }

  // ── Recommendations ───────────────────────────────────────────────────────
  const recs = summary.topRecommendations || [];
  if (recs.length && !isTrendExecSection) {
    const recWrap = el('div', 'exec-recommendations');
    const heading = el('div', 'exec-recommendations__heading'); heading.textContent = 'Top 3 Actions'; recWrap.appendChild(heading);
    const ol = el('ol', 'exec-rec-list');
    const analyzerSectionMap = new Map();
    if (Array.isArray(doc.domains)) {
      for (let di = 0; di < doc.domains.length; di++) {
        const domain = doc.domains[di];
        const domainId = domainAnchorId(domain, di);
        const sections = Array.isArray(domain.sections) ? domain.sections : [];
        for (let si = 0; si < sections.length; si++) {
          const section = sections[si];
          const analyzerName = String(section.analyzerName || '').toLowerCase();
          if (!analyzerName || analyzerSectionMap.has(analyzerName)) continue;
          analyzerSectionMap.set(analyzerName, (section.sectionId && section.sectionId.trim()) ? section.sectionId.trim() : domainId);
        }
      }
    }

    for (const finding of recs.slice(0, 3)) {
      const li = el('li', 'exec-rec-item');
      const num = el('span', 'exec-rec-num'); num.textContent = String(ol.children.length + 1); li.appendChild(num);
      const body = el('div', 'exec-rec-body');
      if (finding.title) {
        const targetAnchor = analyzerSectionMap.get(String(finding.analyzer || '').toLowerCase()) || 'sec-action-queue';
        const titleLink = document.createElement('a');
        titleLink.className = 'exec-rec-link';
        titleLink.href = '#' + targetAnchor;
        titleLink.textContent = finding.title;
        const title = el('div', 'exec-rec-title');
        title.appendChild(titleLink);
        body.appendChild(title);
      }
      if (finding.recommendation) { const text = el('div', 'exec-rec-text'); text.textContent = finding.recommendation; body.appendChild(text); }
      li.appendChild(body);
      ol.appendChild(li);
    }
    recWrap.appendChild(ol);
    sec.appendChild(recWrap);
  }

  // ── Incident handoff (single dump) ───────────────────────────────────────
  if (!isTrendExecSection) {
    const topActions = Array.isArray(summary.topActions) ? summary.topActions : [];
    const criticalFindings = Array.isArray(summary.criticalFindings) ? summary.criticalFindings : [];
    const warningFindings = Array.isArray(summary.warningFindings) ? summary.warningFindings : [];
    const knownLimitations = doc && doc.appendix && Array.isArray(doc.appendix.knownLimitations)
      ? doc.appendix.knownLimitations
      : [];

    const handoff = el('div', 'exec-handoff');
    const heading = el('div', 'exec-recommendations__heading');
    heading.textContent = 'Incident Handoff';
    const copyBtn = document.createElement('button');
    copyBtn.type = 'button';
    copyBtn.className = 'action-btn copy-btn exec-handoff__copy-btn';
    copyBtn.textContent = 'Copy';
    copyBtn.setAttribute('aria-label', 'Copy incident handoff summary');
    handoff.appendChild(heading);
    handoff.appendChild(copyBtn);

    const summaryList = el('ul', 'exec-handoff__list');
    const incidentLine = document.createElement('li');
    incidentLine.textContent = criticalFindings.length + ' critical and ' + warningFindings.length + ' warning findings require follow-up.';
    summaryList.appendChild(incidentLine);

    const focusLine = document.createElement('li');
    if (topActions.length && topActions[0].title) {
      focusLine.textContent = 'Primary focus: ' + topActions[0].title;
    } else if (recs.length && recs[0].title) {
      focusLine.textContent = 'Primary focus: ' + recs[0].title;
    } else {
      focusLine.textContent = 'Primary focus: validate top warning and runtime stability signals.';
    }
    summaryList.appendChild(focusLine);
    handoff.appendChild(summaryList);

    const topActionsTitle = el('div', 'exec-handoff__subheading');
    topActionsTitle.textContent = 'Top Actions';
    handoff.appendChild(topActionsTitle);

    const actionsList = el('ol', 'exec-handoff__list');
    const actionsSource = topActions.length ? topActions.slice(0, 3) : recs.slice(0, 3);
    for (let i = 0; i < actionsSource.length; i++) {
      const action = actionsSource[i] || {};
      const li = document.createElement('li');
      const label = action.title || action.recommendation || action.action || 'Action ' + (i + 1);
      const a = document.createElement('a');
      if (action.findingFingerprint || action.analyzer || action.title) {
        a.href = '#' + findingAnchorId({
          fingerprint: action.findingFingerprint,
          analyzer: action.analyzer,
          title: action.title,
          severity: 'Warning'
        }, 'handoff-action-' + i);
      } else {
        a.href = '#sec-action-queue';
      }
      a.textContent = label;
      li.appendChild(a);
      actionsList.appendChild(li);
    }
    if (!actionsList.children.length) {
      const li = document.createElement('li');
      li.textContent = 'No ranked actions were produced.';
      actionsList.appendChild(li);
    }
    handoff.appendChild(actionsList);

    const riskTitle = el('div', 'exec-handoff__subheading');
    riskTitle.textContent = 'Risks If Unaddressed';
    handoff.appendChild(riskTitle);
    const riskList = el('ul', 'exec-handoff__list');
    const risks = criticalFindings.concat(warningFindings).slice(0, 3);
    for (let i = 0; i < risks.length; i++) {
      const finding = risks[i] || {};
      const li = document.createElement('li');
      li.textContent = finding.title || 'Unspecified risk signal';
      riskList.appendChild(li);
    }
    if (!riskList.children.length) {
      const li = document.createElement('li');
      li.textContent = 'Risk level is currently low-confidence; verify with section evidence.';
      riskList.appendChild(li);
    }
    handoff.appendChild(riskList);

    const evidenceTitle = el('div', 'exec-handoff__subheading');
    evidenceTitle.textContent = 'Evidence References';
    handoff.appendChild(evidenceTitle);
    const evidenceList = el('ul', 'exec-handoff__list');
    for (let i = 0; i < topActions.length && i < 3; i++) {
      const action = topActions[i] || {};
      const li = document.createElement('li');
      const a = document.createElement('a');
      a.href = '#' + findingAnchorId({
        fingerprint: action.findingFingerprint,
        analyzer: action.analyzer,
        title: action.title,
        severity: 'Warning'
      }, 'handoff-evidence-' + i);
      a.textContent = 'F' + (i + 1) + ': ' + (action.title || 'finding');
      li.appendChild(a);
      evidenceList.appendChild(li);
    }
    if (!evidenceList.children.length) {
      const li = document.createElement('li');
      li.textContent = 'No direct evidence links available.';
      evidenceList.appendChild(li);
    }
    handoff.appendChild(evidenceList);

    const limitTitle = el('div', 'exec-handoff__subheading');
    limitTitle.textContent = 'Known Limitations';
    handoff.appendChild(limitTitle);
    const limitList = el('ul', 'exec-handoff__list');
    for (let i = 0; i < knownLimitations.length && i < 3; i++) {
      const li = document.createElement('li');
      li.textContent = String(knownLimitations[i] || '');
      limitList.appendChild(li);
    }
    if (!limitList.children.length) {
      const li = document.createElement('li');
      li.textContent = 'No explicit limitations were reported.';
      limitList.appendChild(li);
    }
    handoff.appendChild(limitList);

    const copyLines = [];
    copyLines.push('Incident summary: ' + incidentLine.textContent);
    if (focusLine.textContent) copyLines.push(focusLine.textContent);
    copyLines.push('Top actions:');
    Array.from(actionsList.querySelectorAll('li')).forEach(function (li, idx) {
      copyLines.push(String(idx + 1) + '. ' + (li.textContent || '').trim());
    });
    copyLines.push('Risks if unaddressed:');
    Array.from(riskList.querySelectorAll('li')).forEach(function (li) {
      copyLines.push('- ' + (li.textContent || '').trim());
    });
    copyLines.push('Evidence references:');
    Array.from(evidenceList.querySelectorAll('li')).forEach(function (li) {
      copyLines.push('- ' + (li.textContent || '').trim());
    });
    copyLines.push('Known limitations:');
    Array.from(limitList.querySelectorAll('li')).forEach(function (li) {
      copyLines.push('- ' + (li.textContent || '').trim());
    });
    copyBtn.setAttribute('data-copy', copyLines.join('\n'));

    sec.appendChild(handoff);
  }

  return sec;
}

// Private helper — only used within this module
function appendExecFindingGroup(parent, sev, findings) {
  if (!findings || !findings.length) return;
  const block = el('div', 'exec-findings-block');
  const header = el('div', 'exec-findings-block__header');
  const badge = el('span', 'exec-findings-block__badge exec-findings-block__badge--' + sev);
  badge.textContent = sev === 'critical' ? 'Critical' : 'Warning'; header.appendChild(badge);
  const count = el('span', 'exec-findings-block__count');
  count.textContent = findings.length + ' finding' + (findings.length !== 1 ? 's' : ''); header.appendChild(count);
  block.appendChild(header);
  for (const f of findings.slice(0, 5)) {
    const row = el('div', 'exec-finding-row exec-finding-row--' + sev);
    const title = el('div', 'exec-finding-row__title'); title.textContent = f.title || ''; row.appendChild(title);
    if (f.evidence) { const ev = el('div', 'exec-finding-row__evidence'); ev.textContent = f.evidence; row.appendChild(ev); }
    if (f.recommendation) { const rec = el('div', 'exec-finding-row__rec'); rec.textContent = '\u2192\u2002' + f.recommendation; row.appendChild(rec); }
    if (f.analyzer) { const meta = el('div', 'exec-finding-row__meta'); meta.textContent = f.analyzer + (f.confidenceScore != null ? '\u2002\u00B7\u2002confidence\u00A0' + Number(f.confidenceScore).toFixed(2) : ''); row.appendChild(meta); }
    block.appendChild(row);
  }
  parent.appendChild(block);
}
