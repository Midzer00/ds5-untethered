'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');
const cli = require('../cli');
const { STRINGS, json } = require('./helpers');

function capture(argv) {
  let out = '', err = '';
  const code = cli.main(argv, { out: (s) => { out += s; }, err: (s) => { err += s; } });
  return { code, out, err };
}

function tmpFile(name, text) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'translation-check-'));
  const p = path.join(dir, name);
  fs.writeFileSync(p, text);
  return p;
}

test('real tr.json passes with --owner and prints JSON', () => {
  const r = capture(['--owner', path.join(STRINGS, 'tr.json')]);
  assert.equal(r.code, 0, r.err);
  const res = JSON.parse(r.out);
  assert.equal(res.ok, true);
  assert.equal(res.files[0].code, 'tr');
  assert.match(r.err, /tr\.json: ok/);
});

test('real tr.json fails without --owner (not accepted from volunteers)', () => {
  const r = capture([path.join(STRINGS, 'tr.json')]);
  assert.equal(r.code, 1);
  assert.equal(JSON.parse(r.out).files[0].errors[0].rule, 'owner.only');
});

test('a bad file fails with readable lines and JSON problems', () => {
  const p = tmpFile('de.json', json({ 'About.Version': 'Version', 'Nope.Key': 'x' }));
  const r = capture([p]);
  assert.equal(r.code, 1);
  const res = JSON.parse(r.out);
  assert.deepEqual(res.files[0].errors.map((e) => e.rule), ['icu.missing', 'key.unknown']);
  assert.match(r.err, /de\.json:2: error \[icu\.missing\] "About\.Version" is missing the placeholder \{version\}/);
  assert.match(r.err, /de\.json: FAIL \(2 errors/);
});

test('several files are checked in one run', () => {
  const a = tmpFile('de.json', json({ 'About.Version': 'Version {version}' }));
  const b = tmpFile('fr.json', json({ 'About.Version': 'Version {version}' }));
  const r = capture([a, b]);
  assert.equal(r.code, 0, r.err);
  assert.equal(JSON.parse(r.out).files.length, 2);
});

test('bad usage exits 2', () => {
  assert.equal(capture([]).code, 2);
  assert.equal(capture(['--bogus', 'x.json']).code, 2);
  assert.equal(capture(['/no/such/de.json']).code, 2);
});

test('the script runs as a process', () => {
  const p = tmpFile('de.json', json({ 'About.Version': 'Version {version}' }));
  const r = spawnSync(process.execPath, [path.join(__dirname, '..', 'cli.js'), p], { encoding: 'utf8' });
  assert.equal(r.status, 0, r.stderr);
  assert.equal(JSON.parse(r.stdout).ok, true);
});
