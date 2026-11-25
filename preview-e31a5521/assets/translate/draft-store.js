/* Draft storage for the translation page (v2). Drop-in replacement for loadDraft/saveDraft in app.js.

   What it changes, compared with v1 (one JSON blob per language, written 400 ms after typing):
   - One localStorage entry per translated key, written synchronously on every input. A write is ~100 bytes,
     so it is cheap enough per keystroke, and nothing is pending when the tab dies.
   - Two tabs no longer overwrite each other: a tab only ever writes the keys the user edits in it, and each
     entry carries a timestamp, so the newest edit of a key wins everywhere. Other tabs hear about it through
     the 'storage' event (and BroadcastChannel when storage is blocked) and update their rows live.
   - Strings that en.json no longer has are kept as "orphans" instead of being dropped on the next save.
   - Discard keeps a copy in IndexedDB for undo; IndexedDB also holds the draft when localStorage fails.
   - "Save Draft File" / "Load Draft File" as the manual backup; a plain <code>.json loads too.
   - beforeunload asks only when the draft really exists nowhere else (storage failed and no file saved since).

   Pure helpers (DraftCore) need no DOM and are tested in Node; DraftStore.open() is the browser adapter. Both
   accept injected storage / channel / idb so the adapter is tested too. */
(function (root, factory) {
  var api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else { root.DraftCore = api.core; root.DraftStore = api.store; }
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  var PREFIX = 'ds5-translate:d2:';          // ds5-translate:d2:<code>|<key>  -> item JSON
  var SEP = '|';                              // never in a BCP-47 code, so the split is unambiguous
  var META = 'ds5-translate:d2meta:';         // ds5-translate:d2meta:<code>    -> { saved, exported }
  var V1 = 'ds5-translate:draft:';            // old blob, migrated once
  var CORRUPT = 'ds5-translate:corrupt:';     // unreadable entries are parked here, never parsed again
  var FILE_FORMAT = 'ds5-translate-draft';
  var has = function (o, k) { return Object.prototype.hasOwnProperty.call(o, k); };

  /* ---------- pure core ---------- */

  // A stored item: { t: text, en: English at edit time, b: base value at edit time (or ''), at: ms }.
  function cleanItem(it) {
    if (!it || typeof it !== 'object' || typeof it.t !== 'string') return null;
    return {
      t: it.t,
      en: typeof it.en === 'string' ? it.en : '',
      b: typeof it.b === 'string' ? it.b : '',
      at: typeof it.at === 'number' && isFinite(it.at) ? it.at : 0
    };
  }

  function itemKey(code, key) { return PREFIX + code + SEP + key; }
  function splitKey(storageKey) {
    if (storageKey.indexOf(PREFIX) !== 0) return null;
    var rest = storageKey.slice(PREFIX.length), i = rest.indexOf(SEP);
    return i < 1 ? null : { code: rest.slice(0, i), key: rest.slice(i + 1) };
  }

  // Newest edit wins. Equal times keep a (local), so a tab never flips its own text back.
  function newer(a, b) {
    if (!a) return b; if (!b) return a;
    return b.at > a.at ? b : a;
  }

  // Splits stored items into live ones (key still in en.json) and orphans (key removed upstream).
  // Items whose text equals the current base are dropped: the file already has them.
  function partition(items, enHas, base) {
    var live = {}, orphans = {};
    Object.keys(items).forEach(function (k) {
      var it = items[k];
      if (!enHas(k)) { orphans[k] = it; return; }
      var b = base && has(base, k) && typeof base[k] === 'string' ? base[k] : '';
      if (it.t === b) return;
      live[k] = it;
    });
    return { live: live, orphans: orphans };
  }

  // Keys where the file on GitHub changed since the volunteer edited (someone else's translation landed).
  function baseConflicts(items, base) {
    return Object.keys(items).filter(function (k) {
      var now = base && has(base, k) && typeof base[k] === 'string' ? base[k] : '';
      return items[k].b !== now && items[k].t !== now;
    });
  }

  // v1 blob { v:1, saved, items: { key: { t, en } } } -> v2 items (time = saved).
  function migrateV1(raw) {
    var obj;
    try { obj = JSON.parse(raw); } catch (e) { return null; }
    if (!obj || typeof obj.items !== 'object' || !obj.items) return null;
    var at = typeof obj.saved === 'number' ? obj.saved : 0, out = {};
    Object.keys(obj.items).forEach(function (k) {
      var it = obj.items[k];
      if (it && typeof it.t === 'string') out[k] = { t: it.t, en: typeof it.en === 'string' ? it.en : '', b: '', at: at };
    });
    return out;
  }

  function stamp(d) {
    function p(n) { return (n < 10 ? '0' : '') + n; }
    return d.getFullYear() + p(d.getMonth() + 1) + p(d.getDate()) + '-' + p(d.getHours()) + p(d.getMinutes());
  }

  // The manual backup file. Holds orphans too, so nothing is lost by a round trip.
  function buildDraftFile(code, items, orphans, now) {
    var d = new Date(now);
    var all = {};
    Object.keys(items).forEach(function (k) { all[k] = items[k]; });
    Object.keys(orphans || {}).forEach(function (k) { if (!has(all, k)) all[k] = orphans[k]; });
    return {
      name: 'ds5-translate-' + code + '-draft-' + stamp(d) + '.json',
      text: JSON.stringify({ format: FILE_FORMAT, v: 2, code: code, saved: now, items: all }, null, 1) + '\n'
    };
  }

  // Reads a draft file or a plain <code>.json. Returns { ok:false, reason } or
  // { ok:true, kind:'draft'|'translation', code|null, items }. A plain file's strings get time `now`.
  function parseDraftFile(text, now) {
    var obj;
    try { obj = JSON.parse(String(text).replace(/^﻿/, '')); } catch (e) { return { ok: false, reason: 'json' }; }
    if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return { ok: false, reason: 'shape' };
    var items = {};
    if (obj.format === FILE_FORMAT) {
      if (!obj.items || typeof obj.items !== 'object') return { ok: false, reason: 'shape' };
      Object.keys(obj.items).forEach(function (k) { var it = cleanItem(obj.items[k]); if (it) items[k] = it; });
      return { ok: true, kind: 'draft', code: typeof obj.code === 'string' ? obj.code : null, items: items };
    }
    var n = 0;
    Object.keys(obj).forEach(function (k) {
      if (typeof obj[k] === 'string') { items[k] = { t: obj[k], en: '', b: '', at: now }; n++; }
    });
    if (!n) return { ok: false, reason: 'empty' };
    return { ok: true, kind: 'translation', code: null, items: items };
  }

  // What importing would do against the current items. A draft file's item replaces the current one only when
  // newer; a plain translation file replaces everything it has (the user chose that file on purpose).
  function planImport(current, incoming, kind) {
    var take = {}, replaced = [], added = [], kept = [];
    Object.keys(incoming).forEach(function (k) {
      var inc = incoming[k], cur = current[k];
      if (cur && cur.t === inc.t) return;
      if (cur && kind === 'draft' && !(inc.at > cur.at)) { kept.push(k); return; }
      take[k] = inc;
      (cur ? replaced : added).push(k);
    });
    return { take: take, added: added, replaced: replaced, kept: kept };
  }

  // Ask the volunteer to save a file? When storage is not keeping the draft at all, or (Safari deletes site
  // storage after 7 days without a visit) when a sizeable draft has not been saved to a file for a while.
  // mode: 'local' (localStorage), 'idb' (IndexedDB only), 'memory' (nothing survives a reload).
  function backupNudge(s) {
    if (s.changed === 0) return null;
    if (s.mode === 'memory') return s.changedSinceExport ? 'unsafe' : null;
    if (s.changed >= 50 && s.changedSinceExport >= 50 && (!s.exported || s.now - s.exported > 3 * 864e5)) return 'big';
    return null;
  }

  // Only when leaving would really lose text: nothing stores it, or the IndexedDB copy is still being written.
  function needsUnloadWarning(s) {
    return (s.mode === 'memory' && s.changedSinceExport > 0) || (s.mode === 'idb' && !!s.pending);
  }

  // The drafts kept in storage, one per language, for the landing screen: [{ code, n, at }], newest edit first.
  // entries: [[storageKey, rawValue]]. n counts the edited strings (a v1 blob not yet migrated counts too, a key
  // in both once); at is the newest edit. Unreadable entries are skipped; a language with none left is not listed.
  function summarize(entries) {
    var by = {};
    function add(code, key, at) {
      var d = by[code] || (by[code] = { code: code, keys: {}, n: 0, at: 0 });
      if (!has(d.keys, key)) { d.keys[key] = 1; d.n++; }
      if (at > d.at) d.at = at;
    }
    (entries || []).forEach(function (e) {
      var sk = e && e[0], raw = e && e[1];
      if (typeof sk !== 'string' || typeof raw !== 'string') return;
      var p = splitKey(sk);
      if (p) {
        var it;
        try { it = cleanItem(JSON.parse(raw)); } catch (x) { it = null; }
        if (it) add(p.code, p.key, it.at);
      } else if (sk.indexOf(V1) === 0 && sk.length > V1.length) {
        var old = migrateV1(raw), code = sk.slice(V1.length);
        if (old) Object.keys(old).forEach(function (k) { add(code, k, old[k].at); });
      }
    });
    return Object.keys(by).map(function (c) { return { code: c, n: by[c].n, at: by[c].at }; })
      .sort(function (a, b) { return b.at - a.at || (a.code < b.code ? -1 : a.code > b.code ? 1 : 0); });
  }

  var core = {
    PREFIX: PREFIX, META: META, V1: V1, FILE_FORMAT: FILE_FORMAT,
    cleanItem: cleanItem, itemKey: itemKey, splitKey: splitKey, newer: newer,
    partition: partition, baseConflicts: baseConflicts, migrateV1: migrateV1,
    buildDraftFile: buildDraftFile, parseDraftFile: parseDraftFile, planImport: planImport,
    backupNudge: backupNudge, needsUnloadWarning: needsUnloadWarning, summarize: summarize
  };

  /* ---------- browser adapter ---------- */

  function safeStorage(s) {
    // Touching window.localStorage itself throws when the browser blocks storage.
    if (s !== undefined) return s;
    try { var ls = window.localStorage, t = PREFIX + '__probe'; ls.setItem(t, '1'); ls.removeItem(t); return ls; } catch (e) { return null; }
  }

  // Minimal IndexedDB key/value: one record per language under 'snap:<code>' and 'discarded:<code>'.
  function idbKv(name) {
    var dbp = null;
    function db() {
      if (!dbp) dbp = new Promise(function (res, rej) {
        var r = indexedDB.open(name, 1);
        r.onupgradeneeded = function () { r.result.createObjectStore('kv'); };
        r.onsuccess = function () { res(r.result); };
        r.onerror = function () { rej(r.error); };
      });
      return dbp;
    }
    function tx(mode, fn) {
      return db().then(function (d) {
        return new Promise(function (res, rej) {
          var t = d.transaction('kv', mode), st = t.objectStore('kv'), out = fn(st);
          t.oncomplete = function () { res(out && 'result' in out ? out.result : undefined); };
          t.onerror = t.onabort = function () { rej(t.error); };
        });
      });
    }
    return {
      get: function (k) { return tx('readonly', function (s) { return s.get(k); }); },
      put: function (k, v) { return tx('readwrite', function (s) { s.put(v, k); }); },
      del: function (k) { return tx('readwrite', function (s) { s.delete(k); }); }
    };
  }

  /* opts:
       code            language code (canonical)
       enHas(key)      true when en.json has the key
       base            the language file's current entries ({} for a new language)
       onRemote(keys)  another tab changed these keys: re-read items() and refresh those rows
       onStatus(st)    storage state changed: { mode:'local'|'idb'|'memory', changed, orphans, changedSinceExport, exported, nudge }
       storage, channel, idb, now   injection points for tests (defaults: localStorage, BroadcastChannel, IndexedDB, Date.now)
     Returns a store; ready() resolves once the IndexedDB copy has been checked. */
  function open(opts) {
    var code = opts.code, base = opts.base || {};
    var now = opts.now || function () { return Date.now(); };
    var ls = safeStorage(opts.storage);
    var idb = opts.idb !== undefined ? opts.idb : (typeof indexedDB !== 'undefined' ? idbKv('ds5-translate') : null);
    var ch = opts.channel !== undefined ? opts.channel
      : (typeof BroadcastChannel !== 'undefined' ? new BroadcastChannel('ds5-translate') : null);
    var tabId = Math.random().toString(36).slice(2);
    var items = {}, orphans = {};
    var mode = ls ? 'local' : 'memory';
    var meta = { exported: 0, changedSinceExport: 0 };
    var snapTimer = 0, snapPending = false, idbOk = false, closed = false;

    function readMeta() {
      if (!ls) return;
      try { var m = JSON.parse(ls.getItem(META + code) || 'null'); if (m) { meta.exported = m.exported || 0; meta.changedSinceExport = m.changedSinceExport || 0; } } catch (e) { /* keep */ }
    }
    function writeMeta() {
      if (!ls || mode !== 'local') return;
      try { ls.setItem(META + code, JSON.stringify({ saved: now(), exported: meta.exported, changedSinceExport: meta.changedSinceExport })); } catch (e) { /* item writes report failures */ }
    }

    function readAll() {
      var out = {};
      if (!ls) return out;
      var n = ls.length, names = [];
      for (var i = 0; i < n; i++) names.push(ls.key(i));
      names.forEach(function (sk) {
        var p = sk && splitKey(sk);
        if (!p || p.code !== code) return;
        var it;
        try { it = cleanItem(JSON.parse(ls.getItem(sk))); } catch (e) { it = null; }
        if (it) out[p.key] = it;
        else { try { ls.setItem(CORRUPT + sk.slice(PREFIX.length), ls.getItem(sk)); ls.removeItem(sk); } catch (e) { /* best effort */ } }
      });
      return out;
    }

    function baseOf(k) { return has(base, k) && typeof base[k] === 'string' ? base[k] : ''; }

    function migrate() {
      if (!ls) return;
      var raw;
      try { raw = ls.getItem(V1 + code); } catch (e) { return; }
      if (!raw) return;
      var old = migrateV1(raw);
      if (!old) { try { ls.setItem(CORRUPT + 'v1:' + code, raw); ls.removeItem(V1 + code); } catch (e) { /* keep */ } return; }
      var ok = true;
      Object.keys(old).forEach(function (k) {
        // v1 kept no base value; today's file is the best guess, so a migrated string is not flagged as changed on GitHub
        old[k].b = baseOf(k);
        var cur = null;
        try { cur = cleanItem(JSON.parse(ls.getItem(itemKey(code, k)))); } catch (e) { cur = null; }
        if (cur && cur.at >= old[k].at) return;
        try { ls.setItem(itemKey(code, k), JSON.stringify(old[k])); } catch (e) { ok = false; }
      });
      if (ok) { try { ls.removeItem(V1 + code); } catch (e) { /* retried next load */ } }
    }

    function adopt(all) {
      var p = partition(all, opts.enHas, base);
      items = p.live; orphans = p.orphans;
    }

    function status() {
      var st = {
        mode: mode, changed: Object.keys(items).length, orphans: Object.keys(orphans).length,
        changedSinceExport: meta.changedSinceExport, exported: meta.exported, now: now(), pending: snapPending
      };
      st.nudge = backupNudge(st);
      return st;
    }
    function emitStatus() { if (opts.onStatus) opts.onStatus(status()); }

    function writeSnapshot() {
      clearTimeout(snapTimer);
      if (!idb || !snapPending) return Promise.resolve();
      var all = {};
      Object.keys(orphans).forEach(function (k) { all[k] = orphans[k]; });
      Object.keys(items).forEach(function (k) { all[k] = items[k]; });
      return idb.put('snap:' + code, { code: code, saved: now(), items: all }).then(function () {
        snapPending = false;
        if (mode === 'idb') emitStatus();
      }, function () { /* best effort */ });
    }
    // The IndexedDB copy trails localStorage by 2 s; when it is the only copy it is written after 300 ms.
    function scheduleSnapshot() {
      if (!idb) return;
      snapPending = true;
      clearTimeout(snapTimer);
      snapTimer = setTimeout(writeSnapshot, mode === 'local' ? 2000 : 300);
    }
    function flush() { return writeSnapshot(); }

    function writeItem(k, it) {
      if (!ls || mode === 'memory') return false;
      try {
        if (it) ls.setItem(itemKey(code, k), JSON.stringify(it)); else ls.removeItem(itemKey(code, k));
        return true;
      } catch (e) { return false; }
    }

    // Load
    migrate();
    readMeta();
    (function () {
      var all = readAll();
      adopt(all);
      // Entries whose text the file on GitHub now has are done: drop them, or they would come back as edits
      // the next time someone changes that string upstream.
      var dropped = 0;
      Object.keys(all).forEach(function (k) { if (!has(items, k) && !has(orphans, k)) { writeItem(k, null); dropped++; } });
      if (dropped) scheduleSnapshot();   // and from the IndexedDB copy
    })();

    var ready = (idb ? idb.get('snap:' + code).then(function (snap) {
      idbOk = true;
      // The IndexedDB copy fills in what localStorage lost (cleared, quota, blocked) - newest per key wins.
      if (!snap || !snap.items) return;
      var all = {}, changed = [];
      Object.keys(orphans).forEach(function (k) { all[k] = orphans[k]; });
      Object.keys(items).forEach(function (k) { all[k] = items[k]; });
      Object.keys(snap.items).forEach(function (k) {
        var it = cleanItem(snap.items[k]);
        if (!it || (opts.enHas(k) && it.t === baseOf(k))) return;
        var w = newer(all[k], it);
        if (w !== all[k]) { all[k] = w; changed.push(k); if (mode === 'local') writeItem(k, w); }
      });
      if (changed.length) { adopt(all); if (opts.onRemote) opts.onRemote(changed); }
    }).catch(function () { /* no IndexedDB: localStorage only */ }) : Promise.resolve()).then(function () {
      if (mode === 'memory' && idbOk) mode = 'idb';
      emitStatus();
    });

    // Other tabs
    function remoteKeys(keys) {
      if (closed || !keys.length) return;
      if (opts.onRemote) opts.onRemote(keys);
      emitStatus();
    }
    function onStorage(e) {
      if (!ls || e.storageArea !== ls) return;
      if (e.key === null) {
        // storage cleared elsewhere: report the keys that went away as well as what is left
        var gone = Object.keys(items).concat(Object.keys(orphans));
        adopt(readAll());
        remoteKeys(gone.concat(Object.keys(items).filter(function (k) { return gone.indexOf(k) < 0; })));
        return;
      }
      var p = splitKey(e.key);
      if (!p || p.code !== code) return;
      var it = null;
      try { it = e.newValue ? cleanItem(JSON.parse(e.newValue)) : null; } catch (x) { it = null; }
      applyRemote(p.key, it, !e.newValue);
    }
    function applyRemote(k, it, removed) {
      var cur = items[k] || orphans[k];
      if (removed) {
        if (!cur) return;
        delete items[k]; delete orphans[k];
      } else {
        if (!it || newer(cur, it) === cur) return;
        if (opts.enHas(k)) { items[k] = it; } else orphans[k] = it;
        var b = has(base, k) && typeof base[k] === 'string' ? base[k] : '';
        if (items[k] && items[k].t === b) delete items[k];
      }
      remoteKeys([k]);
    }
    function onMessage(e) {
      var m = e && e.data;
      if (!m || m.tab === tabId || m.code !== code) return;
      // Only needed when this tab cannot see localStorage events (storage blocked or a different store).
      if (m.type === 'set') applyRemote(m.key, cleanItem(m.item), false);
      else if (m.type === 'del') applyRemote(m.key, null, true);
      else if (m.type === 'clear') { var ks = Object.keys(items).concat(Object.keys(orphans)); items = {}; orphans = {}; remoteKeys(ks); }
    }
    if (typeof window !== 'undefined' && window.addEventListener && opts.storage === undefined) window.addEventListener('storage', onStorage);
    if (ch) ch.onmessage = onMessage;

    function post(m) { if (ch && mode !== 'local') { m.tab = tabId; m.code = code; try { ch.postMessage(m); } catch (e) { /* closed */ } } }

    function beforeUnload(e) {
      if (!needsUnloadWarning(status())) return;
      e.preventDefault();
      e.returnValue = '';
    }
    function onHidden() { if (typeof document === 'undefined' || document.visibilityState === 'hidden') flush(); }
    if (typeof window !== 'undefined' && window.addEventListener) {
      window.addEventListener('beforeunload', beforeUnload);
      window.addEventListener('pagehide', flush);
      document.addEventListener('visibilitychange', onHidden);
    }

    var store = {
      ready: function () { return ready; },
      // { key: text } of the live draft (what buildOutput needs)
      values: function () { var o = {}; for (var k in items) o[k] = items[k].t; return o; },
      items: function () { return items; },
      item: function (k) { return items[k] || null; },
      orphans: function () { return orphans; },
      conflicts: function () { return baseConflicts(items, base); },
      status: status,

      // Call on every input. text equal to the base value removes the entry.
      set: function (k, text, enText) {
        var b = has(base, k) && typeof base[k] === 'string' ? base[k] : '';
        var prev = items[k];
        if (text === b) {
          if (!prev) return;
          delete items[k];
          if (!writeItem(k, null) && mode === 'local') mode = idbOk ? 'idb' : 'memory';
          post({ type: 'del', key: k });
        } else {
          if (prev && prev.t === text) return;
          var it = { t: text, en: enText, b: prev ? prev.b : b, at: Math.max(now(), prev ? prev.at + 1 : 0) };
          items[k] = it;
          delete orphans[k];
          if (!writeItem(k, it) && mode === 'local') mode = idbOk ? 'idb' : 'memory';
          post({ type: 'set', key: k, item: it });
        }
        var first = meta.changedSinceExport === 0;
        meta.changedSinceExport++;
        if (first) writeMeta();
        scheduleSnapshot();
        emitStatus();
      },

      // Removes the whole draft (and orphans) but keeps an undo copy in IndexedDB.
      discard: function () {
        var all = {};
        Object.keys(orphans).forEach(function (k) { all[k] = orphans[k]; });
        Object.keys(items).forEach(function (k) { all[k] = items[k]; });
        var keep = idb ? idb.put('discarded:' + code, { code: code, saved: now(), items: all }).catch(function () {}) : Promise.resolve();
        Object.keys(all).forEach(function (k) { writeItem(k, null); });
        items = {}; orphans = {};
        meta.changedSinceExport = 0; writeMeta();
        if (idb) idb.del('snap:' + code).catch(function () {});
        post({ type: 'clear' });
        emitStatus();
        return keep;
      },
      // Resolves to the number of strings brought back (0 when there is nothing to undo).
      undoDiscard: function () {
        if (!idb) return Promise.resolve(0);
        return idb.get('discarded:' + code).then(function (rec) {
          if (!rec || !rec.items) return 0;
          // 'draft': a string typed again after the discard is newer and stays
          var plan = planImport(items, rec.items, 'draft');
          applyTake(plan.take);
          return idb.del('discarded:' + code).then(function () { return plan.added.length + plan.replaced.length; });
        }).catch(function () { return 0; });
      },

      exportFile: function () {
        var f = buildDraftFile(code, items, orphans, now());
        meta.exported = now(); meta.changedSinceExport = 0; writeMeta();
        emitStatus();
        return f;
      },
      // Returns { ok, reason?, kind, code, added, replaced, kept } and applies the import.
      importText: function (text) {
        var parsed = parseDraftFile(text, now());
        if (!parsed.ok) return parsed;
        if (parsed.kind === 'draft' && parsed.code && parsed.code !== code) return { ok: false, reason: 'otherLanguage', code: parsed.code };
        var all = {};
        Object.keys(orphans).forEach(function (k) { all[k] = orphans[k]; });
        Object.keys(items).forEach(function (k) { all[k] = items[k]; });
        // A plain translation file repeats the base for untouched strings; those are not edits.
        var inc = {};
        Object.keys(parsed.items).forEach(function (k) {
          var it = parsed.items[k], b = baseOf(k);
          if (it.t === b) return;
          // a plain file says nothing about the base it was made from: take today's file
          if (parsed.kind === 'translation') it.b = b;
          inc[k] = it;
        });
        var plan = planImport(all, inc, parsed.kind);
        applyTake(plan.take);
        return { ok: true, kind: parsed.kind, code: parsed.code, added: plan.added.length, replaced: plan.replaced.length, kept: plan.kept.length };
      },

      flush: flush,
      // Language switched or page torn down.
      close: function () {
        closed = true;
        clearTimeout(snapTimer);
        if (typeof window !== 'undefined' && window.removeEventListener) {
          window.removeEventListener('storage', onStorage);
          window.removeEventListener('beforeunload', beforeUnload);
          window.removeEventListener('pagehide', flush);
          document.removeEventListener('visibilitychange', onHidden);
        }
        flush();
        if (ch && opts.channel === undefined) { try { ch.close(); } catch (e) { /* closed */ } }
      },
      // Test hook: deliver a storage event (Node has no window).
      _storageEvent: onStorage,
      _message: onMessage
    };

    function applyTake(take) {
      var keys = Object.keys(take);
      keys.forEach(function (k) {
        var it = { t: take[k].t, en: take[k].en, b: take[k].b, at: Math.max(now(), take[k].at) };
        if (opts.enHas(k)) items[k] = it; else orphans[k] = it;
        if (!writeItem(k, it) && mode === 'local') mode = idbOk ? 'idb' : 'memory';
        post({ type: 'set', key: k, item: it });
      });
      if (keys.length) { meta.changedSinceExport += keys.length; writeMeta(); scheduleSnapshot(); if (opts.onRemote) opts.onRemote(keys); emitStatus(); }
    }

    return store;
  }

  // Asks the browser not to evict site storage under pressure. Chromium decides silently; Firefox would show a
  // permission prompt, so this only runs where no prompt is possible.
  function requestPersistence() {
    try {
      if (!navigator.storage || !navigator.storage.persist || !navigator.userAgentData) return Promise.resolve(false);
      return navigator.storage.persisted().then(function (p) { return p || navigator.storage.persist(); }).catch(function () { return false; });
    } catch (e) { return Promise.resolve(false); }
  }

  // The drafts in localStorage (see summarize). storage: injection point for tests. [] when storage is blocked.
  function list(storage) {
    var ls = safeStorage(storage), entries = [];
    if (!ls) return [];
    try {
      var n = ls.length;
      for (var i = 0; i < n; i++) {
        var k = ls.key(i);
        if (k && (k.indexOf(PREFIX) === 0 || k.indexOf(V1) === 0)) entries.push([k, ls.getItem(k)]);
      }
    } catch (e) { /* what was read so far */ }
    return summarize(entries);
  }

  return { core: core, store: { open: open, list: list, requestPersistence: requestPersistence, idbKv: idbKv } };
});
