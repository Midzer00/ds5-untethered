'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const prCheck = require('../pr-check');
const { EN_TEXT, readStrings, json } = require('./helpers');

const DIR = 'src/Kopru.Core/Localization/strings/';
const de = json({ '@name': 'Deutsch', 'About.Version': 'Version {version}' });

function run(files, isOwner) {
  return prCheck.checkPullRequest(files, { enText: EN_TEXT, isOwner: !!isOwner });
}

test('a new language file from a volunteer passes', () => {
  const r = run([{ path: DIR + 'de.json', status: 'added', text: de }]);
  assert.equal(r.ok, true, JSON.stringify(r.files));
  assert.equal(r.files[0].code, 'de');
  assert.equal(r.files[0].counts.translated, 2);
});

test('files outside the strings folder are ignored', () => {
  const r = run([{ path: 'README.md', status: 'modified', text: 'x' }, { path: DIR + 'sub/de.json', status: 'added', text: '{' }]);
  assert.equal(r.ok, true);
  assert.equal(r.files.length, 0);
});

test('a non-owner touching en.json or tr.json fails', () => {
  for (const name of ['en.json', 'tr.json', 'TR.json']) {
    for (const status of ['modified', 'removed', 'added']) {
      const r = run([{ path: DIR + name, status, text: EN_TEXT }]);
      assert.equal(r.ok, false, name + ' ' + status);
      assert.equal(r.files[0].errors[0].rule, 'owner.only');
    }
  }
  const renamed = run([{ path: DIR + 'de.json', previousPath: DIR + 'tr.json', status: 'renamed', text: de }]);
  assert.equal(renamed.files[0].errors[0].rule, 'owner.only');
});

test('the repo owner may change en.json and tr.json', () => {
  const r = run([
    { path: DIR + 'en.json', status: 'modified', text: EN_TEXT },
    { path: DIR + 'tr.json', status: 'modified', text: readStrings('tr.json') }
  ], true);
  assert.equal(r.ok, true, JSON.stringify(r.files.map((f) => f.errors)));
});

test('an owner PR adding a key to en.json checks other files against the new en.json', () => {
  const en = JSON.parse(EN_TEXT);
  en['Zz.New'] = 'New {thing}';
  const enText = JSON.stringify(en, null, 2) + '\n';
  const deNew = json({ 'Zz.New': 'Neu {thing}' });
  assert.equal(run([{ path: DIR + 'de.json', status: 'added', text: deNew }], true).ok, false);
  assert.equal(run([{ path: DIR + 'en.json', status: 'modified', text: enText }, { path: DIR + 'de.json', status: 'added', text: deNew }], true).ok, true);
});

test('a broken en.json from the owner fails', () => {
  const r = run([{ path: DIR + 'en.json', status: 'modified', text: '{"a": "{x"}' }], true);
  assert.equal(r.ok, false);
  assert.equal(r.files[0].errors[0].rule, 'icu.syntax');
});

test('the file name must be the canonical code, and other files do not belong there', () => {
  assert.equal(run([{ path: DIR + 'pt-br.json', status: 'added', text: '{}' }]).files[0].errors[0].rule, 'code.fileName');
  assert.equal(run([{ path: DIR + 'notes.txt', status: 'added', text: '' }]).files[0].errors[0].rule, 'file.name');
  assert.equal(run([{ path: DIR + 'qps.json', status: 'added', text: '{}' }]).files[0].errors[0].rule, 'code.reserved');
});

test('symlinks are rejected, removals of other languages are a warning', () => {
  assert.equal(run([{ path: DIR + 'de.json', status: 'added', symlink: true }]).files[0].errors[0].rule, 'file.symlink');
  const r = run([{ path: DIR + 'de.json', status: 'removed' }]);
  assert.equal(r.ok, true);
  assert.equal(r.files[0].warnings[0].rule, 'file.removed');
});

test('moving a language file out of the folder fails', () => {
  const r = run([{ path: 'other/de.json', previousPath: DIR + 'de.json', status: 'renamed', text: de }]);
  assert.equal(r.files[0].errors[0].rule, 'file.moved');
});

test('the summary escapes untrusted text', () => {
  const r = run([{ path: DIR + 'de.json', status: 'added', text: json({ '![x](https://evil.example/p.png)': 'x', '@someone <b>': 'y' }) }]);
  const md = prCheck.summaryMarkdown(r);
  assert.match(md, /Translation check: failed/);
  assert.doesNotMatch(md, /!\[x\]\(/);
  assert.doesNotMatch(md, /<b>/);
  assert.doesNotMatch(md, /(^|[^\\])@someone/);
  assert.match(md, /\| `src\/Kopru\.Core\/Localization\/strings\/de\.json` \| `de` \| 0 of \d+ \| 2 \| 1 \|/);
});

const Z = '​';

test('mdEscape neutralises links, mentions, html and newlines', () => {
  assert.equal(prCheck.mdEscape('a\n@b [c](d) <e> `f`'), 'a \\@' + Z + 'b \\[c\\]\\(d\\) &lt;e&gt; \\`f\\`');
});

// What GitHub would turn into a link or a notification in plain comment text (after markdown escapes are undone).
function autolinkable(md) {
  const t = md.replace(/\\/g, '');
  return /[a-z][a-z0-9+.-]*:\/\//i.test(t) || /www\./i.test(t) || /@[A-Za-z0-9]/.test(t) || /#[0-9]/.test(t) ||
    /[A-Za-z0-9._%+-]@[A-Za-z0-9-]+\.[A-Za-z]/.test(t);
}

test('mdEscape breaks bare URLs, www. links, e-mail addresses, mentions and issue references', () => {
  for (const s of ['https://evil.example/x', 'HTTP://EVIL.example', 'ftp://evil.example', 'see www.evil.example now',
    'WWW.evil.example', '_www.evil.example', 'mail me@evil.example', '@someone', 'fixes #12', 'javascript://x']) {
    assert.ok(autolinkable(s), 'the check itself sees ' + s);
    const out = prCheck.mdEscape(s);
    assert.equal(autolinkable(out), false, s + ' -> ' + JSON.stringify(out));
  }
});

test('mdCode puts untrusted text in one code span, whatever backticks it holds', () => {
  assert.equal(prCheck.mdCode('de'), '`de`');
  assert.equal(prCheck.mdCode('a`b'), '``a`b``');
  assert.equal(prCheck.mdCode('```x'), '```` ```x ````');
  assert.equal(prCheck.mdCode('x`'), '`` x` ``');
  assert.equal(prCheck.mdCode('a\nb'), '`a b`');
  assert.equal(prCheck.mdCode(''), '');
  assert.equal(prCheck.mdCode('a|b', true), '`a\\|b`');
  assert.equal(prCheck.mdCode('a|b'), '`a|b`');
});

// Removes inline code spans (literal on GitHub) so only the prose around them is left.
function prose(md) { return md.replace(/(`+)(?!`)[\s\S]*?(?<!`)\1(?!`)/g, ''); }

test('a URL used as a key gives no clickable link in the summary', () => {
  const r = run([{ path: DIR + 'de.json', status: 'added', text: json({ 'https://evil.example/login': 'x', 'www.evil.example': 'y', 'a@evil.example': 'z' }) }]);
  assert.equal(r.files[0].errors.filter((e) => e.rule === 'key.unknown').length, 3);
  const md = prCheck.summaryMarkdown(r);
  assert.equal(autolinkable(prose(md)), false, md);
  assert.doesNotMatch(md, /https:\/\/evil/);
  assert.doesNotMatch(md, /www\.evil/);
});

test('a file name with a pipe or backticks stays in its table cell', () => {
  const path = DIR + 'a|`b.json';
  const r = { ok: false, errorCount: 1, warningCount: 0, files: [{ path, name: 'a|`b.json', code: null, counts: null, errors: [{ level: 'error', rule: 'file.name', message: 'm' }], warnings: [] }] };
  const row = prCheck.summaryMarkdown(r).split('\n').find((l) => l.indexOf('a\\|') >= 0);
  assert.ok(row, 'row with the escaped pipe');
  assert.equal(row.split(/(?<!\\)\|/).length, 7, row);
  assert.ok(row.indexOf('``' + DIR + 'a\\|`b.json``') > 0, row);
});
