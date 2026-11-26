'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const path = require('path');
const rules = require('../rules');
const { readStrings, EN, MINI_EN, json, REPO_ROOT } = require('./helpers');

function check(values, opts) {
  opts = opts || {};
  return rules.checkFile(Object.assign({ code: 'de', en: MINI_EN }, opts, { text: typeof values === 'string' ? values : json(values) }));
}

function rulesOf(list) { return list.map((p) => p.rule); }

// Warnings without the @name reminder, which most crafted files trigger.
function warnRules(r) { return rulesOf(r.warnings).filter((x) => x !== 'name.missing'); }

test('real tr.json passes for the repo owner', () => {
  const r = rules.checkFile({ code: 'tr', fileName: 'tr.json', bytes: Buffer.from(readStrings('tr.json')), en: EN, allowMaintained: true });
  assert.equal(r.errors.length, 0, JSON.stringify(r.errors.slice(0, 5)));
  assert.equal(r.code, 'tr');
  assert.equal(r.counts.total, Object.keys(EN).length);
  assert.ok(r.counts.translated > 1000);
});

test('real en.json passes the source checks', () => {
  const r = rules.checkSource(readStrings('en.json'));
  assert.equal(r.ok, true, JSON.stringify(r.errors.slice(0, 5)));
  assert.equal(r.warnings.length, 0);
});

test('tr.json from a volunteer is rejected as maintained', () => {
  const r = rules.checkFile({ code: 'tr', bytes: Buffer.from(readStrings('tr.json')), en: EN });
  assert.deepEqual(rulesOf(r.errors), ['code.maintained']);
});

test('real tr.json values as a German file pass (German needs plurals, Turkish dropped them)', () => {
  // Turkish is "other only" and has no plural blocks; under de every dropped plural must be reported.
  const r = rules.checkFile({ code: 'de', text: readStrings('tr.json'), en: EN });
  assert.ok(r.errors.length > 0);
  assert.ok(r.errors.every((p) => p.rule === 'icu.pluralDropped'), JSON.stringify(r.errors.find((p) => p.rule !== 'icu.pluralDropped')));
});

test('English values copied into a translation pass for a one/other language', () => {
  const r = rules.checkFile({ code: 'de', text: readStrings('en.json'), en: EN });
  assert.equal(r.errors.length, 0, JSON.stringify(r.errors.slice(0, 5)));
});

test('a good small file passes and counts translated strings', () => {
  const r = check({ '@name': 'Deutsch', 'A.Plain': 'Speichern', 'A.Hello': 'Hallo {name}', 'A.Count': '{count, plural, one {# Spiel} other {# Spiele}}' });
  assert.equal(r.ok, true, JSON.stringify(r.errors));
  assert.equal(r.code, 'de');
  assert.deepEqual(r.counts, { translated: 4, total: 6, keys: 4 });
});

/* ---------- language codes ---------- */

test('codes: canonical forms are accepted', () => {
  assert.deepEqual(rules.canonicalCode('pt_br'), { ok: true, code: 'pt-BR' });
  assert.deepEqual(rules.canonicalCode('zh-hans'), { ok: true, code: 'zh-Hans' });
  assert.deepEqual(rules.canonicalCode('es-419'), { ok: true, code: 'es-419' });
  assert.deepEqual(rules.canonicalCode('DE'), { ok: true, code: 'de' });
});

test('codes: en, tr (any region), qps, qtg and junk are rejected', () => {
  assert.equal(rules.canonicalCode('en').reason, 'source');
  assert.equal(rules.canonicalCode('EN').reason, 'source');
  assert.equal(rules.canonicalCode('tr').reason, 'maintained');
  assert.equal(rules.canonicalCode('tr-CY').reason, 'maintained');
  assert.equal(rules.canonicalCode('qps').reason, 'reserved');
  assert.equal(rules.canonicalCode('qtg').reason, 'reserved');
  assert.equal(rules.canonicalCode('qps-ploc').reason, 'reserved');
  for (const bad of ['', 'x', 'german', 'de-', 'de/../en', 'de DE', '<b>', 'de-DE-1996-x']) {
    assert.equal(rules.canonicalCode(bad).reason, 'format', bad);
  }
});

test('codes: only codes with an English display name for every subtag are accepted', () => {
  // The canonical codes in use today keep working.
  for (const c of ['zh-Hans', 'pt-BR', 'es-419', 'sr-Latn', 'de', 'ja', 'uk', 'fil', 'zh-Hant', 'es-ES']) {
    assert.deepEqual(rules.canonicalCode(c), { ok: true, code: c }, c);
  }
  // Well-formed but unknown: language, script or region, and the codes that name no language.
  for (const c of ['xx', 'abc', 'qaa', 'xx-Latn', 'de-XX', 'de-Xxxx', 'de-ZZ', 'und', 'mul', 'zxx', 'mis', 'de-999']) {
    assert.equal(rules.canonicalCode(c).reason, 'unknown', c);
  }
  const r = rules.checkFile({ code: 'xx', text: '{}', en: MINI_EN });
  assert.deepEqual(rulesOf(r.errors), ['code.unknown']);
});

test('codes: checkFile reports each code rule', () => {
  assert.deepEqual(rulesOf(check({}, { code: 'en' }).errors), ['code.source']);
  assert.deepEqual(rulesOf(check({}, { code: 'tr' }).errors), ['code.maintained']);
  assert.deepEqual(rulesOf(check({}, { code: 'qps' }).errors), ['code.reserved']);
  assert.deepEqual(rulesOf(check({}, { code: 'qtg' }).errors), ['code.reserved']);
  assert.deepEqual(rulesOf(check({}, { code: 'xx_12345' }).errors), ['code.format']);
});

test('codes: the file name must be the canonical code', () => {
  assert.deepEqual(rulesOf(check({}, { code: 'pt-br', fileName: 'pt-br.json' }).errors), ['code.fileName']);
  assert.equal(check({}, { code: 'pt-BR', fileName: 'pt-BR.json' }).ok, true);
});

/* ---------- file shape ---------- */

test('file: over 1 MB is rejected', () => {
  const big = Buffer.alloc(rules.MAX_FILE_BYTES + 1, 0x20);
  assert.deepEqual(rulesOf(rules.checkFile({ code: 'de', bytes: big, en: MINI_EN }).errors), ['file.size']);
  const text = '{"A.Plain": "' + 'x'.repeat(rules.MAX_FILE_BYTES) + '"}';
  assert.deepEqual(rulesOf(check(text).errors), ['file.size']);
});

test('file: exactly 1 MB is still read', () => {
  const pad = rules.MAX_FILE_BYTES - Buffer.byteLength('{"A.Plain": ""}');
  const text = '{"A.Plain": ""' + ' '.repeat(pad) + '}';
  assert.equal(Buffer.byteLength(text), rules.MAX_FILE_BYTES);
  assert.equal(check(text).errors.length, 0);
});

test('file: invalid UTF-8 is rejected', () => {
  const bytes = Buffer.from([0x7b, 0x22, 0x41, 0xff, 0x22, 0x3a, 0x22, 0x22, 0x7d]);
  assert.deepEqual(rulesOf(rules.checkFile({ code: 'de', bytes, en: MINI_EN }).errors), ['file.encoding']);
});

test('file: a BOM is a warning only', () => {
  const r = rules.checkFile({ code: 'de', bytes: Buffer.from('﻿' + json({ 'A.Plain': 'Speichern' })), en: MINI_EN });
  assert.equal(r.ok, true);
  assert.deepEqual(warnRules(r), ['file.bom']);
});

test('file: invalid JSON is rejected with a line', () => {
  const r = check('{\n  "A.Plain": "Speichern",\n}\n');
  assert.deepEqual(rulesOf(r.errors), ['file.json']);
  assert.equal(r.errors[0].line, 3);
});

test('file: arrays, null and strings are not a flat object', () => {
  for (const t of ['[]', 'null', '"x"', '42']) assert.deepEqual(rulesOf(check(t).errors), ['file.shape'], t);
});

test('file: nested objects and non-string values are rejected per key', () => {
  const r = check({ 'A.Plain': { x: 'y' }, 'A.Hello': 3, 'A.Two': null, 'A.Count': ['a'] });
  assert.deepEqual(rulesOf(r.errors), ['value.type', 'value.type', 'value.type', 'value.type']);
  assert.deepEqual(r.errors.map((p) => p.key), ['A.Plain', 'A.Hello', 'A.Two', 'A.Count']);
});

test('file: duplicate keys are rejected', () => {
  const r = check('{\n  "A.Plain": "a",\n  "A.Plain": "b"\n}\n');
  assert.deepEqual(rulesOf(r.errors), ['key.duplicate']);
  assert.equal(r.errors[0].line, 3);
});

test('keys: unknown keys are rejected, @name is allowed', () => {
  const r = check({ '@name': 'Deutsch', 'A.Plain': 'Speichern', 'A.Nope': 'x', '@other': 'y' });
  assert.deepEqual(rulesOf(r.errors), ['key.unknown', 'key.unknown']);
  assert.deepEqual(r.errors.map((p) => p.key), ['A.Nope', '@other']);
  assert.equal(r.errors[0].line, 4);
});

test('keys: a missing or empty @name is a warning', () => {
  assert.deepEqual(rulesOf(check({ 'A.Plain': 'Speichern' }).warnings), ['name.missing']);
  assert.deepEqual(rulesOf(check({ '@name': ' ', 'A.Plain': 'Speichern' }).warnings), ['name.missing', 'value.empty']);
  assert.deepEqual(rulesOf(check({ '@name': 'Deutsch' }).warnings), []);
});

test('keys: empty values warn and do not count', () => {
  const r = check({ 'A.Plain': '', 'A.Hello': '  ' });
  assert.equal(r.ok, true);
  assert.deepEqual(warnRules(r), ['value.empty', 'value.empty']);
  assert.equal(r.counts.translated, 0);
});

/* ---------- ICU ---------- */

test('icu: unbalanced braces are syntax errors', () => {
  for (const v of ['Hallo {name', 'Hallo name}', 'Hallo {}', '{count, plural, one {# Spiel} other {# Spiele}']) {
    const r = check({ 'A.Hello': v });
    assert.equal(r.errors[0].rule, 'icu.syntax', v);
    assert.equal(r.errors[0].key, 'A.Hello');
  }
});

test('icu: an unclosed apostrophe quote is a syntax error, a doubled one is fine', () => {
  assert.equal(check({ 'A.Plain': "l'{x" }).errors[0].rule, 'icu.syntax');
  assert.equal(check({ 'A.Plain': "l''audio" }).ok, true);
  assert.equal(check({ 'A.Plain': "don't" }).ok, true);
});

test('icu: placeholders must match English', () => {
  assert.deepEqual(rulesOf(check({ 'A.Hello': 'Hallo' }).errors), ['icu.missing']);
  assert.deepEqual(rulesOf(check({ 'A.Hello': 'Hallo {nom}' }).errors), ['icu.missing', 'icu.extra']);
  assert.deepEqual(rulesOf(check({ 'A.Plain': 'Speichern {x}' }).errors), ['icu.extra']);
  assert.equal(check({ 'A.Two': '{total}: {done}' }).ok, true, 'reordering is fine');
});

test('icu: an apostrophe that hides a placeholder gets the quote hint', () => {
  const r = check({ 'A.Hello': "Hallo '{name}'" });
  assert.equal(r.errors[0].rule, 'icu.missing');
  assert.match(r.errors[0].message, /apostrophe/);
});

test('icu: plural must keep an other branch', () => {
  const r = check({ 'A.Count': '{count, plural, one {# Spiel}}' });
  assert.equal(r.errors[0].rule, 'icu.syntax');
  assert.match(r.errors[0].message, /other/);
});

test('icu: only plural is supported as an argument type', () => {
  assert.equal(check({ 'A.Count': '{count, select, other {x}}' }).errors[0].rule, 'icu.syntax');
  assert.equal(check({ 'A.Count': '{count, plural, lots {x} other {y}}' }).errors[0].rule, 'icu.syntax');
});

test('icu: dropping the plural is an error where the language has plural forms', () => {
  assert.deepEqual(rulesOf(check({ 'A.Count': '{count} Spiele' }).errors), ['icu.pluralDropped']);
  assert.deepEqual(rulesOf(check({ 'A.Count': '{count} Spiele' }, { code: 'ru' }).errors), ['icu.pluralDropped']);
});

test('icu: "other only" languages may drop the plural (Turkish rule; also ja, zh, ko by CLDR)', () => {
  assert.equal(check({ 'A.Count': '{count} 個のゲーム' }, { code: 'ja' }).ok, true);
  assert.equal(check({ 'A.Count': '{count} 款游戏' }, { code: 'zh-Hans' }).ok, true);
  assert.equal(rules.pluralInfo('tr').required.join(), 'other');
});

test('plural categories: missing required forms and unused forms are warnings', () => {
  const ru = check({ 'A.Count': '{count, plural, one {# игра} other {# игр}}' }, { code: 'ru' });
  assert.equal(ru.ok, true);
  assert.deepEqual(warnRules(ru), ['plural.missing']);
  assert.match(ru.warnings.find((w) => w.rule === 'plural.missing').message, /few, many/);
  const ja = check({ 'A.Count': '{count, plural, one {# 個} other {# 個}}' }, { code: 'ja' });
  assert.deepEqual(warnRules(ja), ['plural.unused']);
  const fr = check({ 'A.Count': '{count, plural, one {# jeu} other {# jeux}}' }, { code: 'fr' });
  assert.deepEqual(warnRules(fr), [], 'French many is optional');
});

/* ---------- HTML, URLs, length ---------- */

test('html: tags are rejected, comparisons are not', () => {
  assert.deepEqual(rulesOf(check({ 'A.Plain': '<b>Speichern</b>' }).errors), ['html']);
  assert.deepEqual(rulesOf(check({ 'A.Plain': 'Speichern<br/>' }).errors), ['html']);
  assert.equal(check({ 'A.Plain': 'a < b > c' }).ok, true);
  assert.equal(check({ 'A.Plain': '< 5 ms' }).ok, true);
});

test('url: links are rejected unless English has one', () => {
  assert.deepEqual(rulesOf(check({ 'A.Plain': 'Siehe https://evil.example' }).errors), ['url']);
  assert.deepEqual(rulesOf(check({ 'A.Plain': 'www.evil.example' }).errors), ['url']);
  assert.equal(check({ 'A.Link': 'Siehe https://example.org/help' }).ok, true);
});

test('length: at most 4x English plus 20 characters', () => {
  const max = rules.maxLength('Save');
  assert.equal(max, 36);
  assert.equal(check({ 'A.Plain': 'x'.repeat(36) }).errors.length, 0);
  const r = check({ 'A.Plain': 'x'.repeat(37) });
  assert.deepEqual(rulesOf(r.errors), ['length']);
  // Characters, not UTF-16 units: 36 emoji are fine.
  assert.equal(check({ 'A.Plain': '\u{1F3AE}'.repeat(36) }).errors.length, 0);
});

test('length: soft warning for much longer short labels', () => {
  const r = check({ 'A.Plain': 'Speichern und schliessen' });
  assert.deepEqual(warnRules(r), ['length.soft']);
});

/* ---------- output ---------- */

test('problems are ordered by line and messages are single-line and quoted', () => {
  const r = check('{\n  "A.Two": "{x}",\n  "A.Bad\\nKey": "x",\n  "A.Plain": "<i>x</i>"\n}\n');
  assert.deepEqual(r.errors.map((p) => p.line), [2, 2, 2, 3, 4]);
  for (const p of r.errors) assert.ok(!/[\r\n]/.test(p.message), p.message);
  assert.match(r.errors[3].message, /"A\.Bad\\nKey"/);
});

test('formatProblem gives file:line: level [rule] message', () => {
  assert.equal(rules.formatProblem({ level: 'error', rule: 'html', message: 'm', line: 4 }, 'de.json'), 'de.json:4: error [html] m');
  assert.equal(rules.formatProblem({ level: 'warning', rule: 'x', message: 'm' }, 'de.json'), 'de.json: warning [x] m');
});

test('buildOutput matches the page format and the real tr.json layout', () => {
  const tr = readStrings('tr.json');
  assert.equal(rules.buildOutput(Object.keys(EN), JSON.parse(tr)), tr.replace(/\r\n/g, '\n'));
  assert.equal(rules.buildOutput(['a', 'b'], { b: 'ü', a: '' }), '{\n  "b": "ü"\n}\n');
  assert.equal(rules.buildOutput(['a'], {}), '{}\n');
});

test('topLevelKeys finds keys with lines and skips nested ones', () => {
  assert.deepEqual(rules.topLevelKeys('{\n "a": "x:y",\n "b": {"c": 1},\n "d\\"e": "z"\n}'), [
    { key: 'a', line: 2 }, { key: 'b', line: 3 }, { key: 'd"e', line: 4 }
  ]);
});

/* ---------- nesting depth ---------- */

// {n, plural, other {{n, plural, other {... x ...}}}} with the given number of plural blocks.
function deepPlural(levels) {
  return '{n, plural, other {'.repeat(levels) + 'x' + '}}'.repeat(levels);
}

test('ICU: nesting is capped at MAX_DEPTH plural blocks and deeper input is a syntax error, not a crash', () => {
  assert.equal(rules.MAX_DEPTH, 16);
  assert.equal(rules.parse(deepPlural(16)).ok, true);
  const p = rules.parse(deepPlural(17));
  assert.equal(p.ok, false);
  assert.equal(p.code, 'tooDeep');
  // About 20k levels used to throw an uncaught RangeError (stack overflow).
  assert.equal(rules.parse(deepPlural(20000)).code, 'tooDeep');
  // Siblings do not add up: depth is nesting, not count.
  assert.equal(rules.parse('{n, plural, other {x}} '.repeat(100)).ok, true);
});

test('ICU: a deeply nested value is reported as a normal validation error', () => {
  const en = { 'A.Count': '{count, plural, one {# game} other {# games}}' };
  const deep = '{count, plural, one {# Spiel} other {' + deepPlural(20000) + '}}';
  const r = rules.checkFile({ code: 'de', text: json({ 'A.Count': deep }), en });
  assert.equal(r.ok, false);
  assert.deepEqual(rulesOf(r.errors), ['icu.syntax']);
  assert.match(r.errors[0].message, /nested more than 16 levels/);
  const src = rules.checkSource(json({ 'A.Deep': deepPlural(20000) }));
  assert.equal(src.ok, false);
  assert.equal(src.errors[0].rule, 'icu.syntax');
});

/* ---------- parity with the translation page ---------- */

const CORE_JS = process.env.TRANSLATE_CORE_JS || path.resolve(REPO_ROOT, '..', 'site', 'web', 'assets', 'translate', 'core.js');

test('parity with the site page core.js (skipped when the site folder is not next to the repo)', { skip: !fs.existsSync(CORE_JS) && 'core.js not found' }, () => {
  const core = require(CORE_JS);
  const tr = JSON.parse(readStrings('tr.json'));
  const samples = [];
  for (const k of Object.keys(EN)) samples.push([EN[k], EN[k]], [EN[k], tr[k] || '']);
  samples.push(
    ['{count, plural, one {# game} other {# games}}', '{count} Spiele'],
    ['{count, plural, one {# game} other {# games}}', '{count, plural, one {# Spiel}}'],
    ['Hello {name}', "Hallo '{name}'"],
    ['Hello {name}', 'Hallo {name'],
    ['Save', 'Speichern und wirklich alles schliessen']
  );
  samples.push(['{n, plural, other {x}}', deepPlural(16)], ['{n, plural, other {x}}', deepPlural(17)], ['{n, plural, other {x}}', deepPlural(20000)]);
  for (const code of ['de', 'ru', 'ja', 'tr', 'fr', 'pl', 'zh-Hans', 'xx']) {
    for (const [en, t] of samples) {
      assert.deepEqual(rules.validate(en, t, code), core.validate(en, t, code), code + ': ' + t);
    }
  }
  for (const c of ['de', 'pt_br', 'zh-hans', 'en', 'tr', 'tr-CY', 'x', 'de-DE', 'es-419', 'sr-Latn', 'xx', 'de-XX', 'und', 'de-Xxxx']) {
    assert.deepEqual(rules.canonicalCode(c), core.canonicalCode(c), c);
  }
  // Known, intended difference: the contract also rejects qps and qtg as "reserved".
  assert.equal(rules.canonicalCode('qtg').reason, 'reserved');
  const keys = Object.keys(EN);
  assert.equal(rules.buildOutput(keys, tr), core.buildOutput(keys, tr, {}));
  assert.equal(rules.countTranslated(keys, tr), core.countTranslated(keys, tr, {}));
});
