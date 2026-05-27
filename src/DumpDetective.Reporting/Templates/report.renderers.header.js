// Header, health scorecard, and executive summary renderers.
// Covers both single-dump and trend modes; the isTrend flag selects the appropriate layout.
import { el, t, formatBytes } from './report.dom.js';

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

  const sec = el('section', 'header-card');
  sec.id = 'sec-header';

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

  function sevInfo(sev) {
    const s = String(sev).toLowerCase(); const n = Number(sev);
    if (n === 3 || s === 'critical') return { label: 'Critical', css: 'critical', dot: '\u25CF' };
    if (n === 2 || s === 'warning')  return { label: 'Warning',  css: 'warning',  dot: '\u25CF' };
    if (n === 1 || s === 'ok')       return { label: 'OK',       css: 'ok',       dot: '\u2713' };
    return                                   { label: 'Unknown',  css: 'unknown',  dot: '\u25CB' };
  }

  const sec = el('section', 'section-card health-scorecard');
  sec.id = 'sec-health';

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
  sec.id = 'sec-exec';

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
    lcChip('New', doc.trendNewFindingCount, 'new');
    lcChip('Persistent', doc.trendPersistentFindingCount, 'persistent');
    lcChip('Resolved', doc.trendResolvedFindingCount, 'resolved');
    if (doc.trendDumpCount != null) {
      const net = (doc.trendNewFindingCount || 0) - (doc.trendResolvedFindingCount || 0);
      lcChip('Net', net, net > 0 ? 'worse' : net < 0 ? 'better' : 'flat');
    }
    lifecycle.appendChild(chips);
    sec.appendChild(lifecycle);
  }

  // ── KPI strip ────────────────────────────────────────────────────────────
  const strip = el('div', 'exec-kpi-strip');

  function kpi(label, value, sev) {
    if (value == null) return null;
    const d = el('div', 'exec-kpi');
    const lbl = el('span', 'exec-kpi__label'); lbl.textContent = label; d.appendChild(lbl);
    const val = el('span', 'exec-kpi__value' + (sev ? ' exec-kpi__value--' + sev : '')); val.textContent = String(value); d.appendChild(val);
    return d;
  }
  function kpiGroup(...items) {
    const g = el('div', 'exec-kpi-group');
    for (const item of items) { if (item) g.appendChild(item); }
    return g.children.length ? g : null;
  }

  const g1 = kpiGroup(
    kpi('Total heap', formatBytes(Number(summary.totalManagedBytes || 0))),
    summary.lohBytes != null ? kpi('LOH', formatBytes(Number(summary.lohBytes)) + (summary.lohPercent != null ? ' (' + Number(summary.lohPercent).toFixed(1) + '%)' : '')) : null,
    summary.gen2Percent != null ? kpi('Gen2', Number(summary.gen2Percent).toFixed(1) + '%', Number(summary.gen2Percent) > 60 ? 'warning' : null) : null
  );
  const gcPressureVal = summary.gcPressureLevel || (summary.gcPressureScore != null ? summary.gcPressureScore + '/100' : null);
  const g2 = kpiGroup(
    gcPressureVal ? kpi('GC pressure', gcPressureVal, summary.gcPressureScore > 66 ? 'warning' : null) : null,
    summary.leakCandidateCount != null ? kpi('Leak suspects', String(summary.leakCandidateCount), summary.leakCandidateCount > 0 ? 'warning' : 'ok') : null,
    summary.finalizerQueueCount != null ? kpi('Finalizer queue', Number(summary.finalizerQueueCount).toLocaleString('en-US'), summary.finalizerQueueCount > 1000 ? 'warning' : null) : null
  );
  const g3 = kpiGroup(
    summary.blockedThreads != null ? kpi('Blocked threads', String(summary.blockedThreads), summary.blockedThreads > 0 ? 'warning' : 'ok') : null,
    summary.deadlockCycles != null ? kpi('Deadlocks', String(summary.deadlockCycles), summary.deadlockCycles > 0 ? 'critical' : 'ok') : null,
    summary.hangScore != null ? kpi('Hang score', summary.hangScore + '/100', summary.hangScore < 50 ? 'warning' : 'ok') : null
  );
  const g4 = kpiGroup(
    summary.activeExceptions != null ? kpi('Active exceptions', String(summary.activeExceptions), summary.activeExceptions > 0 ? 'critical' : 'ok') : null
  );

  for (const g of [g1, g2, g3, g4]) { if (g && g.children.length) strip.appendChild(g); }
  if (strip.children.length) sec.appendChild(strip);

  // ── Score triplet ─────────────────────────────────────────────────────────
  function scoreLevel(s) { return s >= 67 ? 'ok' : s >= 34 ? 'warning' : 'critical'; }
  const isTrendExec = !!(doc['$kind'] === 'trend' || doc.isTrendReport);
  if (summary.leakLikelihoodScore != null || summary.gcPressureScore != null || summary.threadContentionScore != null) {
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

  // ── Findings ──────────────────────────────────────────────────────────────
  const critFindings = summary.criticalFindings || [];
  const warnFindings = summary.warningFindings || [];
  if (critFindings.length || warnFindings.length) {
    const findingsWrap = el('div', 'exec-findings');
    appendExecFindingGroup(findingsWrap, 'critical', critFindings);
    appendExecFindingGroup(findingsWrap, 'warning', warnFindings);
    sec.appendChild(findingsWrap);
  }

  // ── Recommendations ───────────────────────────────────────────────────────
  const recs = summary.topRecommendations || [];
  if (recs.length) {
    const recWrap = el('div', 'exec-recommendations');
    const heading = el('div', 'exec-recommendations__heading'); heading.textContent = 'Top recommendations'; recWrap.appendChild(heading);
    const ol = el('ol', 'exec-rec-list');
    for (const finding of recs.slice(0, 3)) {
      const li = el('li', 'exec-rec-item');
      const num = el('span', 'exec-rec-num'); num.textContent = String(ol.children.length + 1); li.appendChild(num);
      const body = el('div', 'exec-rec-body');
      if (finding.title) { const title = el('div', 'exec-rec-title'); title.textContent = finding.title; body.appendChild(title); }
      if (finding.recommendation) { const text = el('div', 'exec-rec-text'); text.textContent = finding.recommendation; body.appendChild(text); }
      li.appendChild(body);
      ol.appendChild(li);
    }
    recWrap.appendChild(ol);
    sec.appendChild(recWrap);
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
