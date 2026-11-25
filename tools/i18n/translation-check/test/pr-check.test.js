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
  assert.match(md, /\| src\/Kopru\.Core\/Localization\/strings\/de\.json \| de \| 0 of \d+ \| 2 \| 1 \|/);
});

test('mdEscape neutralises links, mentions, html and newlines', () => {
  assert.equal(prCheck.mdEscape('a\n@b [c](d) <e> `f`'), 'a \\@b \\[c\\]\\(d\\) &lt;e&gt; \\`f\\`');
});
