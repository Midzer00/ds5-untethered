/* Pure logic of the validate Action (way 1: a pull request that changes language files) and of the CLI.
 * No I/O: the caller reads the files and passes them in. Zero dependencies, CommonJS, Node 20 or newer. */
'use strict';

const rules = require('./rules');

const PATH_RE = /^src\/Kopru\.Core\/Localization\/strings\/([^/]+)$/;
const OWNER_ONLY = ['en.json', 'tr.json'];

function fileNameOf(path) {
  const m = PATH_RE.exec(String(path || ''));
  return m ? m[1] : null;
}

function isOwnerOnly(name) {
  return name != null && OWNER_ONLY.indexOf(name.toLowerCase()) >= 0;
}

const ZWSP = '​';

// Escapes text for GitHub markdown (job summaries, issue and PR comments): no links, images, mentions or HTML.
// Backslash escapes stop markdown syntax; GitHub's autolinks (bare scheme://... and www. URLs, e-mail addresses,
// @mentions and #references) are found in plain text, so a zero-width space breaks each of their triggers.
function mdEscape(s) {
  return String(s)
    .replace(/[\r\n\t]+/g, ' ')
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/([\\`*_{}[\]()#+!|~@])/g, '\\$1')
    .replace(/:\/\//g, ':' + ZWSP + '//')
    .replace(/(www)\./gi, '$1' + ZWSP + '.')
    .replace(/\\([@#])/g, '\\$1' + ZWSP);
}

// Untrusted text as one inline code span: nothing inside is markdown, a link or a mention. The fence is one backtick
// longer than the longest run inside, with a space of padding when the text starts or ends with a backtick.
// inTable: also escape "|", which GitHub tables split cells on even inside code spans.
function mdCode(s, inTable) {
  let t = String(s == null ? '' : s).replace(/[\r\n\t]+/g, ' ');
  if (inTable) t = t.replace(/\|/g, '\\|');
  if (t.trim() === '') return '';
  const longest = (t.match(/`+/g) || []).reduce((n, run) => Math.max(n, run.length), 0);
  const fence = '`'.repeat(longest + 1);
  const pad = /^[` ]|[` ]$/.test(t) ? ' ' : '';
  return fence + pad + t + pad + fence;
}

/**
 * files: [{ path, status ('added'|'modified'|'removed'|'renamed'|...), previousPath?, bytes? | text?, symlink? }]
 * opts: { enText: en.json at the base branch, isOwner: PR author is the repo owner }
 * Returns { ok, files: [{ path, name, code, status, counts?, errors, warnings }], errorCount, warningCount }.
 */
function checkPullRequest(files, opts) {
  const out = [];
  const isOwner = !!opts.isOwner;
  const base = rules.checkSource(opts.enText);
  let en = base.en || {};

  const relevant = files.filter((f) => fileNameOf(f.path) != null || fileNameOf(f.previousPath) != null);

  // An owner PR that changes en.json is checked against its own en.json.
  const enChange = relevant.find((f) => fileNameOf(f.path) === 'en.json' && f.status !== 'removed');
  if (isOwner && enChange) {
    const own = rules.checkSource(textOf(enChange));
    if (own.en) en = own.en;
  }

  for (const f of relevant) {
    const name = fileNameOf(f.path) || fileNameOf(f.previousPath);
    const entry = { path: f.path, name, code: null, status: f.status, counts: null, errors: [], warnings: [] };
    out.push(entry);
    const prevName = fileNameOf(f.previousPath);

    if (!isOwner && (isOwnerOnly(fileNameOf(f.path)) || isOwnerOnly(prevName))) {
      entry.errors.push({ level: 'error', rule: 'owner.only', message: 'en.json and tr.json are maintained by the repo owner; a pull request from anyone else must not change them.' });
      continue;
    }
    if (f.status === 'removed') {
      entry.warnings.push({ level: 'warning', rule: 'file.removed', message: 'This pull request deletes ' + rules.show(name, 40) + '.' });
      continue;
    }
    if (fileNameOf(f.path) == null) {
      entry.errors.push({ level: 'error', rule: 'file.moved', message: 'A language file is moved out of ' + rules.STRINGS_DIR + '.' });
      continue;
    }
    if (f.symlink) {
      entry.errors.push({ level: 'error', rule: 'file.symlink', message: 'Language files must be regular files, not symbolic links.' });
      continue;
    }
    if (!/\.json$/.test(name)) {
      entry.errors.push({ level: 'error', rule: 'file.name', message: 'Only <code>.json files belong in ' + rules.STRINGS_DIR + '.' });
      continue;
    }
    if (name === 'en.json') {
      const r = rules.checkSource(textOf(f));
      entry.code = 'en';
      entry.errors.push(...r.errors);
      entry.warnings.push(...r.warnings);
      continue;
    }
    const code = name.slice(0, -'.json'.length);
    const input = { code, fileName: name, en, allowMaintained: isOwner && name.toLowerCase() === 'tr.json' };
    if (f.bytes != null) input.bytes = f.bytes;
    else input.text = f.text;
    const r = rules.checkFile(input);
    entry.code = r.code;
    entry.counts = r.counts;
    entry.errors.push(...r.errors);
    entry.warnings.push(...r.warnings);
  }

  if (!base.ok) {
    out.unshift({ path: rules.STRINGS_DIR + '/en.json', name: 'en.json', code: 'en', status: 'base', counts: null,
      errors: base.errors.map((p) => Object.assign({}, p, { message: 'en.json on the base branch: ' + p.message })), warnings: [] });
  }

  const errorCount = out.reduce((n, e) => n + e.errors.length, 0);
  const warningCount = out.reduce((n, e) => n + e.warnings.length, 0);
  return { ok: errorCount === 0, files: out, errorCount, warningCount };
}

function textOf(f) {
  if (f.text != null) return String(f.text);
  if (f.bytes != null) return Buffer.from(f.bytes).toString('utf8');
  return '';
}

// Job summary in markdown: one table row per file, then the problems (first `limit`).
function summaryMarkdown(result, limit) {
  limit = limit || 200;
  const lines = [];
  lines.push('## Translation check: ' + (result.ok ? 'passed' : 'failed'));
  lines.push('');
  if (!result.files.length) {
    lines.push('No language files changed.');
    return lines.join('\n') + '\n';
  }
  lines.push('| File | Code | Strings | Errors | Warnings |');
  lines.push('| --- | --- | --- | --- | --- |');
  for (const f of result.files) {
    const strings = f.counts ? f.counts.translated + ' of ' + f.counts.total : '';
    lines.push('| ' + mdCode(f.path, true) + ' | ' + mdCode(f.code || '', true) + ' | ' + strings + ' | ' + f.errors.length + ' | ' + f.warnings.length + ' |');
  }
  const all = [];
  for (const f of result.files) for (const p of f.errors.concat(f.warnings)) all.push({ f, p });
  if (all.length) {
    lines.push('');
    lines.push('### Problems');
    lines.push('');
    for (const { f, p } of all.slice(0, limit)) {
      lines.push('- **' + p.level + '** ' + mdCode(f.name + (p.line ? ':' + p.line : '')) + ' [' + p.rule + '] ' + mdEscape(p.message));
    }
    if (all.length > limit) lines.push('- ... and ' + (all.length - limit) + ' more');
  }
  return lines.join('\n') + '\n';
}

module.exports = { PATH_RE, OWNER_ONLY, fileNameOf, isOwnerOnly, mdEscape, mdCode, checkPullRequest, summaryMarkdown };
