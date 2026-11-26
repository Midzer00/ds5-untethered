/* The shared rule set for volunteer translation files (<code>.json under src/Kopru.Core/Localization/strings/).
 *
 * Used by the CLI (cli.js), the validate Action (pull requests) and the intake Action (issues). Zero dependencies,
 * CommonJS, Node 20 or newer.
 *
 * KEEP IN SYNC with the translation page's validator, site/web/assets/translate/core.js (parse, placeholders,
 * pluralInfo, validate, canonicalCode, buildOutput, countTranslated). The ICU parser, the placeholder, plural, HTML,
 * link and length checks and the language-code rules below are a port of that file; a change on one side must be made on the other,
 * or the page accepts files the Action rejects (or the other way round). The file-level rules (JSON shape, unknown
 * keys, duplicate keys, size cap, the reserved codes qps/qtg) come from the submission contract.
 *
 * The ICU subset follows the app's IcuMessage.cs: named arguments, {x, plural, ...} with =N and CLDR categories,
 * '#' inside plural branches, and DOUBLE_OPTIONAL apostrophe quoting.
 */
'use strict';

const MAX_FILE_BYTES = 1024 * 1024;
const NAME_KEY = '@name';
const STRINGS_DIR = 'src/Kopru.Core/Localization/strings';

/* ---------- ICU parser (port of core.js) ---------- */

const CATEGORIES = ['zero', 'one', 'two', 'few', 'many', 'other'];

// Plural blocks nest by recursion; deeper input is reported as a syntax error instead of exhausting the stack.
const MAX_DEPTH = 16;

// TRANSLATING.md lists these as "other only" although CLDR gives them a "one" form too.
const OTHER_ONLY = { tr: true };

function isSpace(c) { return /\s/.test(c); }
function isIdStart(c) { return /[A-Za-z_]/.test(c); }
function isIdChar(c) { return /[A-Za-z0-9_]/.test(c); }

class IcuError {
  constructor(code, detail) { this.code = code; this.detail = detail || ''; }
}

// Returns { ok, code?, detail?, names: [...in order of first use], plurals: { name: { cats: [], exacts: [] } } }
function parse(pattern) {
  const st = { s: String(pattern == null ? '' : pattern), i: 0, depth: 0, names: [], seen: {}, plurals: {} };
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
  const s = st.s;
  let inQuote = false;
  while (st.i < s.length) {
    const c = s[st.i];
    if (inQuote) {
      if (c === "'") {
        if (s[st.i + 1] === "'") st.i += 2;
        else { inQuote = false; st.i++; }
      } else st.i++;
      continue;
    }
    if (c === "'") {
      const n = s[st.i + 1];
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
  const s = st.s, start = st.i;
  if (st.i < s.length && isIdStart(s[st.i])) {
    st.i++;
    while (st.i < s.length && isIdChar(s[st.i])) st.i++;
  }
  return s.slice(start, st.i);
}

function parseHole(st) {
  const s = st.s;
  skipSpaces(st);
  const name = readId(st);
  if (!name) throw new IcuError(st.i >= s.length ? 'unclosedArg' : 'badName');
  skipSpaces(st);
  if (st.i >= s.length) throw new IcuError('unclosedArg', name);
  if (s[st.i] === '}') { st.i++; addName(st, name); return; }
  if (s[st.i] !== ',') throw new IcuError('badArg', name);
  st.i++;
  skipSpaces(st);
  const kind = readId(st);
  if (kind !== 'plural') throw new IcuError('kind', kind || '?');
  skipSpaces(st);
  if (s[st.i] !== ',') throw new IcuError('badArg', name);
  st.i++;
  addName(st, name);
  if (++st.depth > MAX_DEPTH) throw new IcuError('tooDeep', name);
  const info = st.plurals[name] || (st.plurals[name] = { cats: [], exacts: [] });
  const cats = [], exacts = [];
  let hasOther = false;
  for (;;) {
    skipSpaces(st);
    if (st.i >= s.length) throw new IcuError('unclosedPlural', name);
    if (s[st.i] === '}') { st.i++; break; }
    if (s[st.i] === '=') {
      st.i++;
      const numStart = st.i;
      while (st.i < s.length && /[0-9.]/.test(s[st.i])) st.i++;
      const txt = s.slice(numStart, st.i), val = Number(txt);
      if (!txt || !isFinite(val)) throw new IcuError('badExact', '=' + txt);
      if (exacts.indexOf(val) >= 0) throw new IcuError('dupSelector', '=' + txt);
      exacts.push(val);
    } else {
      const sel = readId(st);
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
  info.cats = info.cats.concat(cats.filter((c) => info.cats.indexOf(c) < 0));
  info.exacts = info.exacts.concat(exacts);
}

// Placeholder names in order of first use; a pattern that does not parse falls back to a plain scan.
function placeholders(pattern) {
  const p = parse(pattern);
  if (p.ok) return p.names.slice();
  const out = [], re = /\{\s*([A-Za-z_][A-Za-z0-9_]*)/g;
  let m;
  while ((m = re.exec(String(pattern)))) if (out.indexOf(m[1]) < 0) out.push(m[1]);
  return out;
}

const pluralCache = {};
// all: CLDR categories for the language; required: the ones whole numbers reach (plus other);
// optional: the rest (for example French "many", used only for exact millions).
function pluralInfo(code) {
  if (pluralCache[code]) return pluralCache[code];
  let info;
  let supported = false;
  try { supported = Intl.PluralRules.supportedLocalesOf([code]).length > 0; } catch (e) { supported = false; }
  if (!supported) {
    info = { supported: false, all: CATEGORIES.slice(), required: ['other'], optional: [] };
  } else {
    const pr = new Intl.PluralRules(code);
    const all = pr.resolvedOptions().pluralCategories.slice();
    all.sort((a, b) => CATEGORIES.indexOf(a) - CATEGORIES.indexOf(b));
    const hit = { other: true };
    for (let n = 0; n <= 1000; n++) hit[pr.select(n)] = true;
    const primary = String(code).split('-')[0].toLowerCase();
    const required = OTHER_ONLY[primary] ? ['other'] : all.filter((c) => hit[c]);
    info = { supported: true, all, required, optional: all.filter((c) => required.indexOf(c) < 0) };
  }
  pluralCache[code] = info;
  return info;
}

function charLength(s) { return Array.from(String(s)).length; }
const HTML_TAG = /<\/?[A-Za-z][A-Za-z0-9-]*(?:\s[^<>]*)?\/?>/;
const URL_RE = /\b(?:https?:\/\/|www\.)\S/i;

function hasHtmlTag(s) { return HTML_TAG.test(String(s)); }
function hasUrl(s) { return URL_RE.test(String(s)); }

// Hard cap: at most 4x the English length plus 20 (in characters).
function maxLength(en) { return charLength(en) * 4 + 20; }

// Compares a translation against its English source (same result shape as core.js validate).
// errors: syntax (code, detail), html, url, tooLong (max), missing (name, quoteHint), extra (name), pluralDropped (name)
// warnings: pluralMissing (name, cats), pluralUnused (name, cats), length (len, enLen)
function validate(en, text, code) {
  const res = { errors: [], warnings: [] };
  if (text == null || String(text).trim() === '') return res;
  text = String(text);
  const e = parse(String(en));
  const t = parse(text);
  if (!t.ok) {
    res.errors.push({ kind: 'syntax', code: t.code, detail: t.detail });
    return res;
  }
  // The submission rules (contract): no HTML tags, no links unless English has one, at most 4x English + 20.
  if (HTML_TAG.test(text)) res.errors.push({ kind: 'html' });
  if (URL_RE.test(text) && !URL_RE.test(String(en))) res.errors.push({ kind: 'url' });
  const maxLen = maxLength(en);
  if (charLength(text) > maxLen) res.errors.push({ kind: 'tooLong', max: maxLen });
  const enNames = e.ok ? e.names : placeholders(en);
  enNames.forEach((n) => {
    if (t.names.indexOf(n) < 0) res.errors.push({ kind: 'missing', name: n, quoteHint: /'\{/.test(text) });
  });
  t.names.forEach((n) => {
    if (enNames.indexOf(n) < 0) res.errors.push({ kind: 'extra', name: n });
  });
  const info = pluralInfo(code);
  const needsPlural = info.required.some((c) => c !== 'other');
  if (e.ok) {
    Object.keys(e.plurals).forEach((n) => {
      if (!t.plurals[n] && t.names.indexOf(n) >= 0 && needsPlural) res.errors.push({ kind: 'pluralDropped', name: n });
    });
  }
  Object.keys(t.plurals).forEach((n) => {
    const cats = t.plurals[n].cats;
    const missing = info.required.filter((c) => cats.indexOf(c) < 0);
    if (missing.length) res.warnings.push({ kind: 'pluralMissing', name: n, cats: missing });
    const unused = cats.filter((c) => info.all.indexOf(c) < 0);
    if (unused.length) res.warnings.push({ kind: 'pluralUnused', name: n, cats: unused });
  });
  if (!res.errors.length && e.ok && !Object.keys(e.plurals).length) {
    const el = charLength(en), tl = charLength(text);
    if (el <= 40 && tl > el * 1.5 && tl - el >= 8) res.warnings.push({ kind: 'length', len: tl, enLen: el });
  }
  return res;
}

function hasText(v) { return typeof v === 'string' && v.trim() !== ''; }

// Full <code>.json text as the page builds it: entries in en.json key order, untranslated keys left out,
// two-space indent, LF, trailing newline, non-ASCII kept as is.
function buildOutput(enKeys, values) {
  const lines = [];
  for (const k of enKeys) {
    const v = Object.prototype.hasOwnProperty.call(values, k) ? values[k] : undefined;
    if (hasText(v)) lines.push('  ' + JSON.stringify(k) + ': ' + JSON.stringify(v));
  }
  return lines.length ? '{\n' + lines.join(',\n') + '\n}\n' : '{}\n';
}

function countTranslated(enKeys, values) {
  let n = 0;
  for (const k of enKeys) if (Object.prototype.hasOwnProperty.call(values, k) && hasText(values[k])) n++;
  return n;
}

/* ---------- language codes (port of core.js canonicalCode, plus the contract's reserved codes) ---------- */

// Test-only pseudo-locales of the app; never accepted as a translation.
const RESERVED = { qps: true, qtg: true };

// Language subtags that name no language: undetermined, multiple, no linguistic content, uncoded.
const NOT_A_LANGUAGE = { und: true, mul: true, zxx: true, mis: true };

// True when English display names know every subtag of a canonical code: Intl.DisplayNames gives a name that
// differs from the subtag itself (an unknown one comes back unchanged, or as "Unknown Region"/"Unknown Script").
function knownCode(c) {
  if (typeof Intl.DisplayNames !== 'function') return true;
  try {
    const parts = c.split('-');
    const lang = parts[0].toLowerCase();
    if (NOT_A_LANGUAGE[lang]) return false;
    const names = new Intl.DisplayNames(['en'], { type: 'language' });
    if (names.of(c) === c || names.of(lang) === lang) return false;
    for (let i = 1; i < parts.length; i++) {
      const sub = parts[i];
      const n = new Intl.DisplayNames(['en'], { type: sub.length === 4 ? 'script' : 'region' }).of(sub);
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
  const s = String(input == null ? '' : input).trim().replace(/_/g, '-');
  if (!/^[A-Za-z]{2,3}(-[A-Za-z]{4})?(-(?:[A-Za-z]{2}|[0-9]{3}))?$/.test(s)) return { ok: false, reason: 'format' };
  let c;
  try { c = Intl.getCanonicalLocales(s)[0]; } catch (e) { return { ok: false, reason: 'format' }; }
  if (!c) return { ok: false, reason: 'format' };
  const primary = c.split('-')[0].toLowerCase();
  if (RESERVED[primary]) return { ok: false, reason: 'reserved' };
  if (c.toLowerCase() === 'en') return { ok: false, reason: 'source' };
  // Turkish is maintained by the project itself, in any region.
  if (primary === 'tr') return { ok: false, reason: 'maintained' };
  if (!knownCode(c)) return { ok: false, reason: 'unknown' };
  return { ok: true, code: c };
}

/* ---------- file-level helpers ---------- */

// Top-level keys of a JSON object text with their 1-based line numbers, in file order (duplicates included).
// Tolerant: stops quietly on text that is not a JSON object.
function topLevelKeys(text) {
  const out = [];
  let depth = 0, line = 1, i = 0;
  const s = String(text);
  let lastString = null, lastStringLine = 0, lastStringDepth = -1;
  while (i < s.length) {
    const c = s[i];
    if (c === '\n') { line++; i++; continue; }
    if (c === '"') {
      const start = i, startLine = line;
      i++;
      while (i < s.length && s[i] !== '"') {
        if (s[i] === '\\') i++;
        else if (s[i] === '\n') line++;
        i++;
      }
      i++;
      let str;
      try { str = JSON.parse(s.slice(start, i)); } catch (e) { str = null; }
      lastString = str; lastStringLine = startLine; lastStringDepth = depth;
      continue;
    }
    if (c === ':' && lastString !== null && lastStringDepth === 1) {
      out.push({ key: lastString, line: lastStringLine });
      lastString = null;
      i++;
      continue;
    }
    if (c === '{' || c === '[') depth++;
    else if (c === '}' || c === ']') depth--;
    if (!isSpace(c)) lastString = null;
    i++;
  }
  return out;
}

// A short, single-line, quoted rendering of untrusted text for messages: control characters escaped, long text cut.
function show(value, max) {
  max = max || 60;
  let s = String(value);
  if (charLength(s) > max) s = Array.from(s).slice(0, max).join('') + '…';
  return JSON.stringify(s);
}


const SYNTAX_TEXT = {
  unexpectedClose: 'a "}" without a matching "{"',
  unclosedQuote: "an apostrophe quote that is never closed (double a literal apostrophe next to { or })",
  unclosedBranch: 'a plural branch that is never closed',
  unclosedArg: 'a placeholder that is never closed',
  badName: 'a placeholder without a valid name',
  badArg: 'a malformed placeholder or plural block',
  kind: 'an unsupported argument type (only plural is allowed)',
  unclosedPlural: 'a plural block that is never closed',
  badExact: 'a bad exact selector',
  dupSelector: 'a repeated plural selector',
  unknownCategory: 'an unknown plural category',
  noOther: 'a plural block without an "other" branch',
  tooDeep: 'plural blocks nested more than ' + MAX_DEPTH + ' levels deep'
};

function problem(level, rule, message, extra) {
  return Object.assign({ level, rule, message }, extra || {});
}

function codeProblem(reason, input) {
  switch (reason) {
    case 'source': return problem('error', 'code.source', 'en is the English source and is not accepted as a translation.');
    case 'maintained': return problem('error', 'code.maintained', 'Turkish (tr) is maintained by the project and is not accepted from volunteers.');
    case 'reserved': return problem('error', 'code.reserved', show(input, 20) + ' is a reserved test code (qps, qtg).');
    case 'unknown': return problem('error', 'code.unknown', show(input, 20) + ' is not a known language. Use a BCP-47 code such as de, pt-BR or zh-Hans.');
    default: return problem('error', 'code.format', show(input, 20) + ' is not a language code. Use a BCP-47 code such as de, pt-BR or zh-Hans.');
  }
}

/**
 * Checks one translation file.
 *
 * input: {
 *   code: language code (from the file name or the issue),
 *   bytes | text: the file (bytes are decoded as strict UTF-8; text is used as is),
 *   en: the parsed en.json object (at main),
 *   allowMaintained: true lets tr (and en-only checks) through, for the repo owner's own files,
 *   fileName: optional; when given it must be exactly "<canonical code>.json"
 * }
 * Returns { ok, code, counts: { translated, total, keys }, errors: [...], warnings: [...], values }.
 * Each problem: { level, rule, message, key?, line? }.
 */
function checkFile(input) {
  const errors = [], warnings = [];
  const add = (p) => (p.level === 'error' ? errors : warnings).push(p);
  const en = input.en || {};
  const enKeys = Object.keys(en);
  const result = { ok: false, code: null, counts: { translated: 0, total: enKeys.length, keys: 0 }, errors, warnings, values: null };

  // Code.
  const cc = canonicalCode(input.code);
  let code = cc.ok ? cc.code : null;
  if (!cc.ok) {
    if (input.allowMaintained && cc.reason === 'maintained') code = Intl.getCanonicalLocales(String(input.code).trim().replace(/_/g, '-'))[0];
    else add(codeProblem(cc.reason, input.code));
  }
  result.code = code;
  if (input.fileName != null && code) {
    const expected = code + '.json';
    if (input.fileName !== expected) {
      add(problem('error', 'code.fileName', 'The file name ' + show(input.fileName, 40) + ' should be ' + show(expected) + '.'));
    }
  }

  // Bytes, size, encoding.
  let text;
  if (input.bytes != null) {
    const bytes = input.bytes;
    if (bytes.length > MAX_FILE_BYTES) {
      add(problem('error', 'file.size', 'The file is ' + bytes.length + ' bytes; the limit is ' + MAX_FILE_BYTES + ' bytes (1 MB).'));
      return result;
    }
    try {
      text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes);
    } catch (e) {
      add(problem('error', 'file.encoding', 'The file is not valid UTF-8 text.'));
      return result;
    }
  } else {
    text = String(input.text == null ? '' : input.text);
    const size = Buffer.byteLength(text, 'utf8');
    if (size > MAX_FILE_BYTES) {
      add(problem('error', 'file.size', 'The file is ' + size + ' bytes; the limit is ' + MAX_FILE_BYTES + ' bytes (1 MB).'));
      return result;
    }
  }
  if (text.charCodeAt(0) === 0xfeff) {
    text = text.slice(1);
    add(problem('warning', 'file.bom', 'The file starts with a byte order mark; save it as UTF-8 without BOM.'));
  }

  // JSON shape.
  let data;
  try {
    data = JSON.parse(text);
  } catch (e) {
    // Node 20 says "at position N"; newer versions add "(line L column C)".
    const ml = /line (\d+)/.exec(e.message), mp = /position (\d+)/.exec(e.message);
    let line;
    if (ml) line = Number(ml[1]);
    else if (mp) line = text.slice(0, Number(mp[1])).split('\n').length;
    add(problem('error', 'file.json', 'The file is not valid JSON' + (line ? ' (near line ' + line + ')' : '') + '.', line ? { line } : undefined));
    return result;
  }
  if (data === null || typeof data !== 'object' || Array.isArray(data)) {
    add(problem('error', 'file.shape', 'The file must be one JSON object of "key": "text" pairs.'));
    return result;
  }

  const lines = {};
  const seen = {};
  for (const k of topLevelKeys(text)) {
    if (Object.prototype.hasOwnProperty.call(seen, k.key)) {
      add(problem('error', 'key.duplicate', 'The key ' + show(k.key) + ' appears more than once.', { key: k.key, line: k.line }));
    } else {
      seen[k.key] = true;
      lines[k.key] = k.line;
    }
  }

  const values = {};
  const keys = Object.keys(data);
  result.counts.keys = keys.length;
  const pluralCode = code || 'und';
  for (const key of keys) {
    const v = data[key];
    const at = { key, line: lines[key] };
    if (!Object.prototype.hasOwnProperty.call(en, key)) {
      add(problem('error', 'key.unknown', 'The key ' + show(key) + ' does not exist in en.json.', at));
      continue;
    }
    if (typeof v !== 'string') {
      add(problem('error', 'value.type', 'The value of ' + show(key) + ' must be a string, not ' + (v === null ? 'null' : Array.isArray(v) ? 'an array' : typeof v) + '.', at));
      continue;
    }
    values[key] = v;
    if (!hasText(v)) {
      add(problem('warning', 'value.empty', show(key) + ' is empty and will show in English.', at));
      continue;
    }
    const source = String(en[key]);
    const r = validate(source, v, pluralCode);
    for (const e of r.errors) {
      if (e.kind === 'syntax') {
        add(problem('error', 'icu.syntax', show(key) + ' has ' + (SYNTAX_TEXT[e.code] || 'an ICU syntax error') + (e.detail ? ' (' + show(e.detail, 30) + ')' : '') + '.', at));
      } else if (e.kind === 'missing') {
        add(problem('error', 'icu.missing', show(key) + ' is missing the placeholder {' + e.name + '}' + (e.quoteHint ? " (an apostrophe before { hides it; write '' for a literal apostrophe)" : '') + '.', at));
      } else if (e.kind === 'extra') {
        add(problem('error', 'icu.extra', show(key) + ' has the placeholder ' + show('{' + e.name + '}', 40) + ', which the English text does not have.', at));
      } else if (e.kind === 'pluralDropped') {
        add(problem('error', 'icu.pluralDropped', show(key) + ' must keep the plural block for {' + e.name + '} (with an "other" branch).', at));
      } else if (e.kind === 'html') {
        add(problem('error', 'html', show(key) + ' contains an HTML tag.', at));
      } else if (e.kind === 'url') {
        add(problem('error', 'url', show(key) + ' contains a link, but the English text has none.', at));
      } else if (e.kind === 'tooLong') {
        add(problem('error', 'length', show(key) + ' is ' + charLength(v) + ' characters; the limit is ' + e.max + ' (4x English + 20).', at));
      }
    }
    for (const w of r.warnings) {
      if (w.kind === 'pluralMissing') add(problem('warning', 'plural.missing', show(key) + ': the plural for {' + w.name + '} has no ' + w.cats.join(', ') + ' branch.', at));
      else if (w.kind === 'pluralUnused') add(problem('warning', 'plural.unused', show(key) + ': the plural for {' + w.name + '} has ' + w.cats.join(', ') + ', which this language does not use.', at));
      else if (w.kind === 'length') add(problem('warning', 'length.soft', show(key) + ' is much longer than English (' + w.len + ' vs ' + w.enLen + ' characters); it may not fit.', at));
    }
  }

  // The app's test suite requires a native name for every shipped language; not a contract rule, so a warning.
  if (Object.prototype.hasOwnProperty.call(en, NAME_KEY) && !hasText(values[NAME_KEY])) {
    add(problem('warning', 'name.missing', 'Set "@name" to the native name of the language (shown in Settings > General > Language).'));
  }

  const byLine = (a, b) => (a.line || 0) - (b.line || 0);
  errors.sort(byLine);
  warnings.sort(byLine);
  result.values = values;
  result.counts.translated = countTranslated(enKeys, values);
  result.ok = errors.length === 0;
  return result;
}

/**
 * Checks en.json itself (only the repo owner may change it): a flat object of strings whose patterns parse.
 */
function checkSource(text) {
  const errors = [], warnings = [];
  let data;
  try { data = JSON.parse(String(text).replace(/^﻿/, '')); } catch (e) {
    errors.push(problem('error', 'file.json', 'en.json is not valid JSON.'));
    return { ok: false, errors, warnings, en: null };
  }
  if (data === null || typeof data !== 'object' || Array.isArray(data)) {
    errors.push(problem('error', 'file.shape', 'en.json must be one JSON object.'));
    return { ok: false, errors, warnings, en: null };
  }
  const lines = {};
  for (const k of topLevelKeys(text)) if (!(k.key in lines)) lines[k.key] = k.line;
  for (const key of Object.keys(data)) {
    const v = data[key];
    const at = { key, line: lines[key] };
    if (typeof v !== 'string') { errors.push(problem('error', 'value.type', 'The value of ' + show(key) + ' must be a string.', at)); continue; }
    const p = parse(v);
    if (!p.ok) errors.push(problem('error', 'icu.syntax', show(key) + ' has ' + (SYNTAX_TEXT[p.code] || 'an ICU syntax error') + '.', at));
  }
  if (!Object.prototype.hasOwnProperty.call(data, NAME_KEY)) warnings.push(problem('warning', 'key.name', 'en.json has no "@name" entry.'));
  return { ok: errors.length === 0, errors, warnings, en: errors.length ? null : data };
}

// One readable line per problem: "<file>:<line>: error [rule] message".
function formatProblem(p, file) {
  return (file || '') + (p.line ? ':' + p.line : '') + (file || p.line ? ': ' : '') + p.level + ' [' + p.rule + '] ' + p.message;
}

module.exports = {
  MAX_FILE_BYTES,
  NAME_KEY,
  STRINGS_DIR,
  CATEGORIES,
  MAX_DEPTH,
  RESERVED,
  parse,
  placeholders,
  pluralInfo,
  validate,
  hasText,
  buildOutput,
  countTranslated,
  canonicalCode,
  knownCode,
  codeProblem,
  charLength,
  topLevelKeys,
  show,
  hasHtmlTag,
  hasUrl,
  maxLength,
  checkFile,
  checkSource,
  formatProblem
};
