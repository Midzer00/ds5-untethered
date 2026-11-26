/* Pure helpers for the translation page. No DOM, so Node can test them.
   The ICU parser follows the app's IcuMessage.cs: named arguments, {x, plural, ...} with =N and CLDR
   categories, '#' inside plural branches, and DOUBLE_OPTIONAL apostrophe quoting. */
(function (root, factory) {
  var api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.TrCore = api;
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  var CATEGORIES = ['zero', 'one', 'two', 'few', 'many', 'other'];

  // Plural blocks nest by recursion; deeper input is reported as a syntax error instead of exhausting the stack.
  var MAX_DEPTH = 16;

  // TRANSLATING.md lists these as "other only" although CLDR gives them a "one" form too.
  var OTHER_ONLY = { tr: true };

  function isSpace(c) { return /\s/.test(c); }
  function isIdStart(c) { return /[A-Za-z_]/.test(c); }
  function isIdChar(c) { return /[A-Za-z0-9_]/.test(c); }

  function IcuError(code, detail) { this.code = code; this.detail = detail || ''; }

  // Returns { ok, code?, detail?, names: [...in order of first use], plurals: { name: { cats: [], exacts: [] } } }
  function parse(pattern) {
    var st = { s: String(pattern == null ? '' : pattern), i: 0, depth: 0, names: [], seen: {}, plurals: {} };
    try {
      parseNodes(st, false, false);
      return { ok: true, names: st.names, plurals: st.plurals };
    } catch (e) {
      if (e instanceof IcuError) return { ok: false, code: e.code, detail: e.detail, names: st.names, plurals: st.plurals };
      throw e;
    }
  }

  function addName(st, name) {
    if (!st.seen[name]) { st.seen[name] = true; st.names.push(name); }
  }

  function parseNodes(st, inPlural, stopAtClose) {
    var s = st.s, inQuote = false;
    while (st.i < s.length) {
      var c = s[st.i];
      if (inQuote) {
        if (c === "'") {
          if (s[st.i + 1] === "'") st.i += 2;
          else { inQuote = false; st.i++; }
        } else st.i++;
        continue;
      }
      if (c === "'") {
        var n = s[st.i + 1];
        if (n === "'") { st.i += 2; continue; }
        if (n === '{' || n === '}' || (inPlural && n === '#')) { inQuote = true; st.i++; continue; }
        st.i++;
        continue;
      }
      if (c === '}' && stopAtClose) { st.i++; return; }
      if (c === '}') throw new IcuError('unexpectedClose');
      if (c === '{') { st.i++; parseHole(st); continue; }
      st.i++;
    }
    if (inQuote) throw new IcuError('unclosedQuote');
    if (stopAtClose) throw new IcuError('unclosedBranch');
  }

  function skipSpaces(st) { while (st.i < st.s.length && isSpace(st.s[st.i])) st.i++; }

  function readId(st) {
    var s = st.s, start = st.i;
    if (st.i < s.length && isIdStart(s[st.i])) {
      st.i++;
      while (st.i < s.length && isIdChar(s[st.i])) st.i++;
    }
    return s.slice(start, st.i);
  }

  function parseHole(st) {
    var s = st.s;
    skipSpaces(st);
    var name = readId(st);
    if (!name) throw new IcuError(st.i >= s.length ? 'unclosedArg' : 'badName');
    skipSpaces(st);
    if (st.i >= s.length) throw new IcuError('unclosedArg', name);
    if (s[st.i] === '}') { st.i++; addName(st, name); return; }
    if (s[st.i] !== ',') throw new IcuError('badArg', name);
    st.i++;
    skipSpaces(st);
    var kind = readId(st);
    if (kind !== 'plural') throw new IcuError('kind', kind || '?');
    skipSpaces(st);
    if (s[st.i] !== ',') throw new IcuError('badArg', name);
    st.i++;
    addName(st, name);
    if (++st.depth > MAX_DEPTH) throw new IcuError('tooDeep', name);
    var info = st.plurals[name] || (st.plurals[name] = { cats: [], exacts: [] });
    var cats = [], exacts = [], hasOther = false;
    for (;;) {
      skipSpaces(st);
      if (st.i >= s.length) throw new IcuError('unclosedPlural', name);
      if (s[st.i] === '}') { st.i++; break; }
      if (s[st.i] === '=') {
        st.i++;
        var numStart = st.i;
        while (st.i < s.length && /[0-9.]/.test(s[st.i])) st.i++;
        var txt = s.slice(numStart, st.i), val = Number(txt);
        if (!txt || !isFinite(val)) throw new IcuError('badExact', '=' + txt);
        if (exacts.indexOf(val) >= 0) throw new IcuError('dupSelector', '=' + txt);
        exacts.push(val);
      } else {
        var sel = readId(st);
        if (CATEGORIES.indexOf(sel) < 0) throw new IcuError('unknownCategory', sel || s[st.i] || '');
        if (cats.indexOf(sel) >= 0) throw new IcuError('dupSelector', sel);
        cats.push(sel);
        if (sel === 'other') hasOther = true;
      }
      skipSpaces(st);
      if (s[st.i] !== '{') throw new IcuError('badArg', name);
      st.i++;
      parseNodes(st, true, true);
    }
    if (!hasOther) throw new IcuError('noOther', name);
    st.depth--;
    info.cats = info.cats.concat(cats.filter(function (c) { return info.cats.indexOf(c) < 0; }));
    info.exacts = info.exacts.concat(exacts);
  }

  // Placeholder names in order of first use; a pattern that does not parse falls back to a plain scan.
  function placeholders(pattern) {
    var p = parse(pattern);
    if (p.ok) return p.names.slice();
    var out = [], re = /\{\s*([A-Za-z_][A-Za-z0-9_]*)/g, m;
    while ((m = re.exec(String(pattern)))) if (out.indexOf(m[1]) < 0) out.push(m[1]);
    return out;
  }

  var pluralCache = {};
  // all: CLDR categories for the language; required: the ones whole numbers reach (plus other);
  // optional: the rest (for example French "many", used only for exact millions).
  function pluralInfo(code) {
    if (pluralCache[code]) return pluralCache[code];
    var info;
    var supported = false;
    try { supported = Intl.PluralRules.supportedLocalesOf([code]).length > 0; } catch (e) { supported = false; }
    if (!supported) {
      info = { supported: false, all: CATEGORIES.slice(), required: ['other'], optional: [] };
    } else {
      var pr = new Intl.PluralRules(code);
      var all = pr.resolvedOptions().pluralCategories.slice();
      all.sort(function (a, b) { return CATEGORIES.indexOf(a) - CATEGORIES.indexOf(b); });
      var hit = { other: true };
      for (var n = 0; n <= 1000; n++) hit[pr.select(n)] = true;
      var primary = String(code).split('-')[0].toLowerCase();
      var required = OTHER_ONLY[primary] ? ['other'] : all.filter(function (c) { return hit[c]; });
      info = { supported: true, all: all, required: required, optional: all.filter(function (c) { return required.indexOf(c) < 0; }) };
    }
    pluralCache[code] = info;
    return info;
  }

  function charLength(s) { return Array.from(String(s)).length; }
  var HTML_TAG = /<\/?[A-Za-z][A-Za-z0-9-]*(?:\s[^<>]*)?\/?>/;
  var URL_RE = /\b(?:https?:\/\/|www\.)\S/i;

  var enCache = {};
  function parseEnglish(en) {
    var hit = enCache[en];
    if (!hit) {
      hit = enCache[en] = parse(en);
    }
    return hit;
  }

  // Compares a translation against its English source.
  // errors: syntax (code, detail), missing (name, quoteHint), extra (name), pluralDropped (name)
  // warnings: pluralMissing (name, cats), pluralUnused (name, cats), length (len, enLen)
  function validate(en, text, code) {
    var res = { errors: [], warnings: [] };
    if (text == null || String(text).trim() === '') return res;
    text = String(text);
    var e = parseEnglish(String(en));
    var t = parse(text);
    if (!t.ok) {
      res.errors.push({ kind: 'syntax', code: t.code, detail: t.detail });
      return res;
    }
    // The submission rules the repo's intake and validate Actions apply too
    if (HTML_TAG.test(text)) res.errors.push({ kind: 'html' });
    if (URL_RE.test(text) && !URL_RE.test(String(en))) res.errors.push({ kind: 'url' });
    var maxLen = charLength(en) * 4 + 20;
    if (charLength(text) > maxLen) res.errors.push({ kind: 'tooLong', max: maxLen });
    var enNames = e.ok ? e.names : placeholders(en);
    enNames.forEach(function (n) {
      if (t.names.indexOf(n) < 0) res.errors.push({ kind: 'missing', name: n, quoteHint: /'\{/.test(text) });
    });
    t.names.forEach(function (n) {
      if (enNames.indexOf(n) < 0) res.errors.push({ kind: 'extra', name: n });
    });
    var info = pluralInfo(code);
    var needsPlural = info.required.some(function (c) { return c !== 'other'; });
    if (e.ok) {
      Object.keys(e.plurals).forEach(function (n) {
        if (!t.plurals[n] && t.names.indexOf(n) >= 0 && needsPlural) res.errors.push({ kind: 'pluralDropped', name: n });
      });
    }
    Object.keys(t.plurals).forEach(function (n) {
      var cats = t.plurals[n].cats;
      var missing = info.required.filter(function (c) { return cats.indexOf(c) < 0; });
      if (missing.length) res.warnings.push({ kind: 'pluralMissing', name: n, cats: missing });
      var unused = cats.filter(function (c) { return info.all.indexOf(c) < 0; });
      if (unused.length) res.warnings.push({ kind: 'pluralUnused', name: n, cats: unused });
    });
    if (!res.errors.length && e.ok && !Object.keys(e.plurals).length) {
      var el = charLength(en), tl = charLength(text);
      if (el <= 40 && tl > el * 1.5 && tl - el >= 8) res.warnings.push({ kind: 'length', len: tl, enLen: el });
    }
    return res;
  }

  function hasText(v) { return typeof v === 'string' && v.trim() !== ''; }

  // The value the file will hold for a key: the draft wins over the existing file.
  function effective(key, base, draft) {
    if (draft && Object.prototype.hasOwnProperty.call(draft, key)) return draft[key];
    return base && Object.prototype.hasOwnProperty.call(base, key) ? base[key] : undefined;
  }

  // Full <code>.json text: existing entries plus the draft (key -> string), in en.json key order,
  // untranslated keys left out. Two-space indent, LF, trailing newline, non-ASCII kept as is.
  function buildOutput(enKeys, base, draft) {
    var lines = [];
    for (var i = 0; i < enKeys.length; i++) {
      var k = enKeys[i], v = effective(k, base, draft);
      if (hasText(v)) lines.push('  ' + JSON.stringify(k) + ': ' + JSON.stringify(v));
    }
    return lines.length ? '{\n' + lines.join(',\n') + '\n}\n' : '{}\n';
  }

  function countTranslated(enKeys, base, draft) {
    var n = 0;
    for (var i = 0; i < enKeys.length; i++) if (hasText(effective(enKeys[i], base, draft))) n++;
    return n;
  }

  // Language subtags that name no language: undetermined, multiple, no linguistic content, uncoded.
  var NOT_A_LANGUAGE = { und: true, mul: true, zxx: true, mis: true };

  // True when English display names know every subtag of a canonical code: Intl.DisplayNames gives a name that
  // differs from the subtag itself (an unknown one comes back unchanged, or as "Unknown Region"/"Unknown Script").
  // A browser without Intl.DisplayNames skips this; the repo's checks still apply it.
  function knownCode(c) {
    if (typeof Intl.DisplayNames !== 'function') return true;
    try {
      var parts = c.split('-'), lang = parts[0].toLowerCase();
      if (NOT_A_LANGUAGE[lang]) return false;
      var names = new Intl.DisplayNames(['en'], { type: 'language' });
      if (names.of(c) === c || names.of(lang) === lang) return false;
      for (var i = 1; i < parts.length; i++) {
        var sub = parts[i];
        var n = new Intl.DisplayNames(['en'], { type: sub.length === 4 ? 'script' : 'region' }).of(sub);
        if (!n || n === sub || /^unknown\b/i.test(n)) return false;
      }
      return true;
    } catch (e) {
      return false;
    }
  }

  // BCP-47 subset used for file names: language, optional script, optional region.
  // Returns { ok: true, code } or { ok: false, reason: 'format' | 'source' | 'maintained' | 'reserved' | 'unknown' }.
  function canonicalCode(input) {
    var s = String(input == null ? '' : input).trim().replace(/_/g, '-');
    if (!/^[A-Za-z]{2,3}(-[A-Za-z]{4})?(-(?:[A-Za-z]{2}|[0-9]{3}))?$/.test(s)) return { ok: false, reason: 'format' };
    var c;
    try { c = Intl.getCanonicalLocales(s)[0]; } catch (e) { return { ok: false, reason: 'format' }; }
    if (!c) return { ok: false, reason: 'format' };
    if (c.toLowerCase() === 'en') return { ok: false, reason: 'source' };
    // Turkish is maintained by the project itself, in any region.
    if (c.split('-')[0].toLowerCase() === 'tr') return { ok: false, reason: 'maintained' };
    // The app's own test locales: qps (pseudo-locale) and qtg (key tags for the context screenshots)
    var lc = c.split('-')[0].toLowerCase();
    if (lc === 'qps' || lc === 'qtg') return { ok: false, reason: 'reserved' };
    if (!knownCode(c)) return { ok: false, reason: 'unknown' };
    return { ok: true, code: c };
  }

  function groupOf(key) {
    if (key.charAt(0) === '@') return '@';
    var dot = key.indexOf('.');
    return dot < 0 ? key : key.slice(0, dot);
  }

  /* ---------- visual mode ---------- */

  // A pattern as the preview paints it: plural blocks show their "other" branch, # becomes {name},
  // quoting is undone and placeholders stay as {name}. A pattern that does not parse is shown as typed.
  function previewText(pattern) {
    var s = String(pattern == null ? '' : pattern);
    if (!parse(s).ok) return s;
    return renderNodes({ s: s, i: 0 }, null, false);
  }
  function renderNodes(st, plural, stopAtClose) {
    var s = st.s, out = '', inQuote = false;
    while (st.i < s.length) {
      var c = s[st.i];
      if (inQuote) {
        if (c === "'" && s[st.i + 1] === "'") { out += "'"; st.i += 2; }
        else if (c === "'") { inQuote = false; st.i++; }
        else { out += c; st.i++; }
        continue;
      }
      if (c === "'") {
        var n = s[st.i + 1];
        if (n === "'") { out += "'"; st.i += 2; continue; }
        if (n === '{' || n === '}' || (plural && n === '#')) { inQuote = true; st.i++; continue; }
        out += c; st.i++; continue;
      }
      if (c === '}' && stopAtClose) { st.i++; return out; }
      if (c === '#' && plural) { out += '{' + plural + '}'; st.i++; continue; }
      if (c === '{') { st.i++; out += renderHole(st); continue; }
      out += c; st.i++;
    }
    return out;
  }
  function renderHole(st) {
    var s = st.s;
    skipSpaces(st);
    var name = readId(st);
    skipSpaces(st);
    if (s[st.i] === '}') { st.i++; return '{' + name + '}'; }
    st.i++; skipSpaces(st); readId(st); skipSpaces(st); st.i++;
    var chosen = '';
    for (;;) {
      skipSpaces(st);
      if (st.i >= s.length || s[st.i] === '}') { st.i++; break; }
      var sel;
      if (s[st.i] === '=') { st.i++; var a = st.i; while (st.i < s.length && /[0-9.]/.test(s[st.i])) st.i++; sel = '=' + s.slice(a, st.i); }
      else sel = readId(st);
      skipSpaces(st);
      st.i++;
      var body = renderNodes(st, name, true);
      if (sel === 'other') chosen = body;
    }
    return chosen;
  }

  function num(v, d) {
    var n = typeof v === 'number' ? v : (typeof v === 'string' && v.trim() !== '' ? Number(v) : NaN);
    return isFinite(n) ? n : d;
  }
  var ALIGNS = { left: 'left', start: 'left', center: 'center', centre: 'center', right: 'right', end: 'right' };

  // One hotspot. Required: key and a positive box; everything else falls back to a sensible default.
  function normalizeItem(it) {
    if (!it || typeof it !== 'object' || typeof it.key !== 'string' || !it.key) return null;
    var x = num(it.x, NaN), y = num(it.y, NaN), w = num(it.w, NaN), h = num(it.h, NaN);
    if (!isFinite(x) || !isFinite(y) || !(w > 0) || !(h > 0)) return null;
    var size = num(it.size, 13); if (!(size > 0)) size = 13;
    var lh = num(it.lineHeight, NaN); if (!(lh > 0)) lh = Math.round(size * 1.33 * 10) / 10;
    var maxW = num(it.maxW, w); if (!(maxW > 0)) maxW = w;
    var maxLines = Math.floor(num(it.maxLines, 1)); if (!(maxLines >= 1)) maxLines = 1;
    var weight = num(it.weight, 400); weight = Math.min(900, Math.max(100, weight));
    var out = {
      key: it.key, x: x, y: y, w: w, h: h, size: size, weight: weight,
      wrap: it.wrap === true, align: ALIGNS[String(it.align).toLowerCase()] || 'left',
      maxW: maxW, maxLines: maxLines, lineHeight: lh,
      trim: it.trim === true, formatted: it.formatted === true, composite: it.composite === true
    };
    if (typeof it.color === 'string' && /^#[0-9a-f]{6}$/i.test(it.color)) {
      out.color = [parseInt(it.color.slice(1, 3), 16), parseInt(it.color.slice(3, 5), 16), parseInt(it.color.slice(5, 7), 16)];
    }
    if (typeof it.text === 'string') out.text = it.text;
    return out;
  }

  // Top to bottom, then left to right. An item whose vertical centre falls inside the first item of the
  // current line joins that line, so labels a pixel or two apart still read left to right.
  function readingOrder(items) {
    var a = items.slice().sort(function (p, q) { return p.y - q.y || p.x - q.x; });
    var out = [], line = [], anchor = null;
    function flush() {
      line.sort(function (p, q) { return p.x - q.x || p.y - q.y; });
      out.push.apply(out, line);
      line = [];
    }
    a.forEach(function (it) {
      var mid = it.y + it.h / 2;
      if (anchor && !(mid >= anchor.y && mid <= anchor.y + anchor.h)) { flush(); anchor = null; }
      if (!anchor) anchor = it;
      line.push(it);
    });
    if (line.length) flush();
    return out;
  }

  function isRelativeUrl(u) {
    return typeof u === 'string' && u.trim() !== '' && !/^[a-z][a-z0-9+.-]*:/i.test(u) && !/^\/\//.test(u) && !/^\//.test(u);
  }

  // Validates a context manifest. Returns { ok:false, reason } or { ok:true, width, height, scale, stringsHash,
  // shots:[{ id, group, title, image, image1x?, items:[...in reading order] }], keysSeen }.
  function normalizeManifest(raw) {
    if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return { ok: false, reason: 'format' };
    var width = num(raw.width, NaN), height = num(raw.height, NaN);
    if (!(width > 0) || !(height > 0)) return { ok: false, reason: 'size' };
    if (!Array.isArray(raw.shots)) return { ok: false, reason: 'shots' };
    var scale = num(raw.scale, 1); if (!(scale > 0)) scale = 1;
    var ids = {}, shots = [];
    raw.shots.forEach(function (s, i) {
      if (!s || typeof s !== 'object' || !isRelativeUrl(s.image) || !Array.isArray(s.items)) return;
      var id = typeof s.id === 'string' && s.id ? s.id : 'shot-' + (i + 1);
      var base = id, n = 2;
      while (ids[id]) id = base + '-' + (n++);
      var items = s.items.map(normalizeItem).filter(Boolean);
      if (!items.length) return;
      ids[id] = true;
      var title = typeof s.title === 'string' && s.title.trim() ? s.title.trim() : id;
      var shot = {
        id: id, title: title,
        group: typeof s.group === 'string' && s.group.trim() ? s.group.trim() : title,
        image: s.image, items: readingOrder(items)
      };
      if (isRelativeUrl(s.image1x)) shot.image1x = s.image1x;
      shots.push(shot);
    });
    if (!shots.length) return { ok: false, reason: 'empty' };
    var seen = Array.isArray(raw.keysSeen) ? raw.keysSeen.filter(function (k) { return typeof k === 'string'; }) : null;
    if (!seen) {
      seen = [];
      shots.forEach(function (s) { s.items.forEach(function (it) { if (seen.indexOf(it.key) < 0) seen.push(it.key); }); });
    }
    return {
      ok: true, width: width, height: height, scale: scale,
      stringsHash: typeof raw.stringsHash === 'string' ? raw.stringsHash : '',
      generated: typeof raw.generated === 'string' ? raw.generated : '',
      appVersion: typeof raw.appVersion === 'string' ? raw.appVersion : '',
      shots: shots, keysSeen: seen
    };
  }

  function sameHash(a, b) {
    function n(h) { return String(h || '').trim().toLowerCase().replace(/^sha-?256:/, ''); }
    return n(a) !== '' && n(a) === n(b);
  }

  // Ties a normalised manifest to the live English strings. Items for keys en.json no longer has are dropped.
  // When the manifest was made from other strings (hash differs) an item that records its English text is
  // dropped too if that text changed, since its box no longer matches. Every key without a box is "offscreen"
  // and lives in List only. A key on several shots counts once, on its first ("home") shot.
  function reconcile(man, en, liveHash) {
    var stale = !!(liveHash && man.stringsHash && !sameHash(liveHash, man.stringsHash));
    var enKeys = Object.keys(en), shots = [], home = {}, shotsOf = {}, dropped = [];
    man.shots.forEach(function (s) {
      var items = s.items.filter(function (it) {
        var keep = Object.prototype.hasOwnProperty.call(en, it.key) && !(stale && typeof it.text === 'string' && it.text !== en[it.key]);
        if (!keep && dropped.indexOf(it.key) < 0) dropped.push(it.key);
        return keep;
      });
      if (!items.length) return;
      var idx = shots.length, keys = [];
      items.forEach(function (it) { if (keys.indexOf(it.key) < 0) keys.push(it.key); });
      keys.forEach(function (k) {
        (shotsOf[k] || (shotsOf[k] = [])).push(idx);
        if (home[k] == null) home[k] = idx;
      });
      var o = {};
      for (var p in s) o[p] = s[p];
      o.items = items; o.keys = keys;
      shots.push(o);
    });
    shots.forEach(function (s, i) { s.home = s.keys.filter(function (k) { return home[k] === i; }); });
    dropped = dropped.filter(function (k) { return home[k] == null; });
    return {
      stale: stale, width: man.width, height: man.height, scale: man.scale,
      shots: shots, home: home, shotsOf: shotsOf, dropped: dropped,
      offscreen: enKeys.filter(function (k) { return home[k] == null; })
    };
  }

  // Next untranslated box: the rest of this shot (wrapping), then each following shot's home keys in turn.
  function nextUntranslated(shots, isDone, shotIdx, key) {
    if (!shots.length) return null;
    if (!(shotIdx >= 0 && shotIdx < shots.length)) shotIdx = 0;
    var keys = shots[shotIdx].keys, start = key == null ? -1 : keys.indexOf(key);
    for (var i = 1; i <= keys.length; i++) {
      var k = keys[(start + i + keys.length) % keys.length];
      if (k !== key && !isDone(k)) return { shot: shotIdx, key: k };
    }
    for (var j = 1; j < shots.length; j++) {
      var si = (shotIdx + j) % shots.length, hk = shots[si].home;
      for (var m = 0; m < hk.length; m++) if (!isDone(hk[m])) return { shot: si, key: hk[m] };
    }
    return null;
  }

  function shotProgress(shot, isDone) {
    var done = 0;
    shot.home.forEach(function (k) { if (isDone(k)) done++; });
    return { done: done, total: shot.home.length };
  }

  var FIT_TOLERANCE = 1;
  // Lines in a measured block; 1 px of slack so a block exactly n lines tall is not read as n+1.
  function linesFromHeight(height, lineHeight) {
    if (!(lineHeight > 0)) return 1;
    return Math.max(1, Math.ceil((height - FIT_TOLERANCE) / lineHeight));
  }

  // Advisory fit check. m: { width } for one-line text, { lines } for wrapping text (both measured at 1x).
  // Returns kind 'skip' (formatted or composite: no check), 'fit', 'wide' (pct wider, side that spills)
  // or 'lines' (lines needed, max the space holds, minor when it is just one more).
  function fitCheck(item, m) {
    if (!item || !m || item.formatted || item.composite) return { kind: 'skip' };
    if (item.wrap) {
      var lines = m.lines != null ? m.lines : 1;
      // minor: one line more than today. Cards that grow with their content record today's count, so that
      // case gets the note but not the edge.
      if (lines > item.maxLines) return { kind: 'lines', lines: lines, max: item.maxLines, side: 'bottom', minor: lines === item.maxLines + 1 };
      return { kind: 'fit', lines: lines };
    }
    var over = m.width - item.maxW;
    if (over > FIT_TOLERANCE) {
      return { kind: 'wide', pct: Math.max(1, Math.round(over / item.maxW * 100)), side: item.align === 'right' ? 'left' : 'right' };
    }
    return { kind: 'fit' };
  }

  function median(a) {
    var s = a.slice().sort(function (p, q) { return p - q; });
    return s.length ? s[(s.length - 1) >> 1] : 0;
  }

  // Background behind a text box, from the RGBA pixels of the ring just outside it: the per-channel median,
  // usable only when most of the ring is close to it (a flat panel, not art or a border).
  function ringColor(px, tolerance, share) {
    tolerance = tolerance == null ? 18 : tolerance;
    share = share == null ? 0.85 : share;
    var r = [], g = [], b = [];
    for (var i = 0; i + 3 < px.length; i += 4) {
      if (px[i + 3] < 128) continue;
      r.push(px[i]); g.push(px[i + 1]); b.push(px[i + 2]);
    }
    if (r.length < 4) return { ok: false };
    var c = [median(r), median(g), median(b)], close = 0;
    for (var j = 0; j < r.length; j++) {
      if (Math.max(Math.abs(r[j] - c[0]), Math.abs(g[j] - c[1]), Math.abs(b[j] - c[2])) <= tolerance) close++;
    }
    return close / r.length >= share ? { ok: true, rgb: c } : { ok: false, rgb: c };
  }

  // Text colour inside a box: the median of the pixels furthest from the background. Null when the box has
  // no clear ink (the caller then uses the app's text colour).
  function inkColor(px, bg) {
    var d = [], max = 0;
    for (var i = 0; i + 3 < px.length; i += 4) {
      var v = Math.max(Math.abs(px[i] - bg[0]), Math.abs(px[i + 1] - bg[1]), Math.abs(px[i + 2] - bg[2]));
      d.push(v);
      if (v > max) max = v;
    }
    if (max < 40) return null;
    var cut = max * 0.7, r = [], g = [], b = [];
    for (var j = 0; j < d.length; j++) {
      if (d[j] >= cut) { r.push(px[j * 4]); g.push(px[j * 4 + 1]); b.push(px[j * 4 + 2]); }
    }
    return [median(r), median(g), median(b)];
  }

  return {
    previewText: previewText,
    normalizeItem: normalizeItem,
    normalizeManifest: normalizeManifest,
    readingOrder: readingOrder,
    sameHash: sameHash,
    reconcile: reconcile,
    nextUntranslated: nextUntranslated,
    shotProgress: shotProgress,
    linesFromHeight: linesFromHeight,
    fitCheck: fitCheck,
    ringColor: ringColor,
    inkColor: inkColor,
    CATEGORIES: CATEGORIES,
    parse: parse,
    placeholders: placeholders,
    pluralInfo: pluralInfo,
    validate: validate,
    effective: effective,
    hasText: hasText,
    buildOutput: buildOutput,
    countTranslated: countTranslated,
    canonicalCode: canonicalCode,
    knownCode: knownCode,
    MAX_DEPTH: MAX_DEPTH,
    groupOf: groupOf,
    charLength: charLength
  };
});
