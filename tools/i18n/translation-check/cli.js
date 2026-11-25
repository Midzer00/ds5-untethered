#!/usr/bin/env node
/* Checks language files against en.json with the shared rule set (rules.js).
 *
 *   node tools/i18n/translation-check/cli.js [--en <en.json>] [--owner] <file.json> [...]
 *
 * The language code comes from each file name (de.json -> de). --owner checks as the repo owner: tr.json and
 * en.json are then allowed (en.json gets the source checks only). Readable lines go to stderr, one JSON result to
 * stdout:
 *   { "ok": bool, "errorCount": n, "warningCount": n,
 *     "files": [{ "path", "name", "code", "counts": { "translated", "total", "keys" }, "errors": [...], "warnings": [...] }] }
 * Each problem is { "level", "rule", "message", "key"?, "line"? }. Exit code 0 when there are no errors, 1 when there
 * are, 2 on bad usage. Zero dependencies, Node 20 or newer. */
'use strict';

const fs = require('fs');
const path = require('path');
const rules = require('./rules');
const prCheck = require('./pr-check');

const DEFAULT_EN = path.resolve(__dirname, '..', '..', '..', rules.STRINGS_DIR, 'en.json');

function parseArgs(argv) {
  const opts = { en: DEFAULT_EN, owner: false, files: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--en') { opts.en = argv[++i]; if (!opts.en) throw new Error('--en needs a path'); }
    else if (a === '--owner') opts.owner = true;
    else if (a === '-h' || a === '--help') opts.help = true;
    else if (a.startsWith('--')) throw new Error('unknown option ' + a);
    else opts.files.push(a);
  }
  return opts;
}

function main(argv, io) {
  io = io || { out: (s) => process.stdout.write(s), err: (s) => process.stderr.write(s) };
  let opts;
  try { opts = parseArgs(argv); } catch (e) {
    io.err('translation-check: ' + e.message + '\n');
    return 2;
  }
  if (opts.help || !opts.files.length) {
    io.err('usage: node tools/i18n/translation-check/cli.js [--en <en.json>] [--owner] <file.json> [...]\n');
    return opts.help ? 0 : 2;
  }
  let enText;
  try { enText = fs.readFileSync(opts.en, 'utf8'); } catch (e) {
    io.err('translation-check: cannot read ' + opts.en + '\n');
    return 2;
  }

  // Present every file as if it sat in the strings folder, so the PR rules (owner-only files, file name = code)
  // apply the same way.
  const files = [];
  const shown = {};
  for (const f of opts.files) {
    const name = path.basename(f);
    const p = rules.STRINGS_DIR + '/' + name;
    shown[p] = f;
    let bytes;
    try { bytes = fs.readFileSync(f); } catch (e) {
      io.err('translation-check: cannot read ' + f + '\n');
      return 2;
    }
    files.push({ path: p, status: 'modified', bytes });
  }
  const result = prCheck.checkPullRequest(files, { enText, isOwner: opts.owner });

  for (const f of result.files) {
    const label = shown[f.path] || f.path;
    for (const p of f.errors.concat(f.warnings)) io.err(rules.formatProblem(p, label) + '\n');
    const counts = f.counts ? ', ' + f.counts.translated + ' of ' + f.counts.total + ' strings' : '';
    io.err(label + ': ' + (f.errors.length ? 'FAIL' : 'ok') + ' (' + f.errors.length + ' errors, ' + f.warnings.length + ' warnings' + counts + ')\n');
  }
  const json = {
    ok: result.ok,
    errorCount: result.errorCount,
    warningCount: result.warningCount,
    files: result.files.map((f) => ({ path: shown[f.path] || f.path, name: f.name, code: f.code, counts: f.counts, errors: f.errors, warnings: f.warnings }))
  };
  io.out(JSON.stringify(json, null, 2) + '\n');
  return result.ok ? 0 : 1;
}

if (require.main === module) process.exitCode = main(process.argv.slice(2));

module.exports = { main, parseArgs };
