'use strict';

const fs = require('fs');
const path = require('path');
const zlib = require('zlib');
const crypto = require('crypto');

const REPO_ROOT = path.resolve(__dirname, '..', '..', '..', '..');
const STRINGS = path.join(REPO_ROOT, 'src', 'Kopru.Core', 'Localization', 'strings');

function readStrings(name) {
  return fs.readFileSync(path.join(STRINGS, name), 'utf8');
}

const EN_TEXT = readStrings('en.json');
const EN = JSON.parse(EN_TEXT);

// A small English source for crafted cases.
const MINI_EN = {
  '@name': 'English',
  'A.Plain': 'Save',
  'A.Hello': 'Hello {name}',
  'A.Count': '{count, plural, one {# game} other {# games}}',
  'A.Link': 'See https://example.org/help for details',
  'A.Two': '{done} of {total}'
};

function json(obj) {
  return JSON.stringify(obj, null, 2) + '\n';
}

// The issue body the translation page builds (contract, way 3). maxPartChars splits the base64 into parts;
// returns { body, comments: [comment bodies for parts 2..n], sha256, parts }.
function pageIssue(code, fileText, opts) {
  opts = opts || {};
  const bytes = Buffer.from(fileText, 'utf8');
  const sha = opts.sha256 || crypto.createHash('sha256').update(bytes).digest('hex');
  const b64 = zlib.gzipSync(bytes).toString('base64');
  const size = opts.maxPartChars || b64.length;
  const chunks = [];
  for (let i = 0; i < b64.length; i += size) chunks.push(b64.slice(i, i + size));
  const n = chunks.length;
  const block = (k) => '```ds5-translate part=' + k + '/' + n + ' encoding=gzip+base64 sha256=' + sha + '\n' +
    chunks[k - 1].match(/.{1,76}/g).join('\n') + '\n```';
  const body = '<!-- ds5-translate v1 -->\n' +
    '**Language:** ' + (opts.bodyCode || code) + ' (Name)\n' +
    (opts.listing ? '**Listing:** ' + opts.listing + '\n' : '') +
    '**Strings:** 3 of 6 · sent from ds5untethered.com/translate\n\n' +
    '<details><summary>Translation file (compressed)</summary>\n\n' + block(1) + '\n</details>\n';
  const comments = [];
  for (let k = 2; k <= n; k++) comments.push(block(k));
  return { title: 'Translation: ' + (opts.titleCode || code), body: opts.crlf ? body.replace(/\n/g, '\r\n') : body, comments, sha256: sha, parts: n };
}

// The body GitHub renders for the issue form (way 2). hide: undefined leaves the Credits section out (older form),
// true/false renders the checkbox ticked or not.
function formIssue(code, fileSection, notes, hide) {
  const credits = hide === undefined ? '' :
    '### Credits\n\n- [' + (hide ? 'X' : ' ') + '] Do not list my name among the translators\n\n';
  return {
    title: 'Translation: ' + code,
    body: '### Language code\n\n' + code + '\n\n### Translation file\n\n' + fileSection + '\n\n' + credits +
      '### Notes\n\n' + (notes || '_No response_')
  };
}

// A fetch stand-in: routes maps URL -> { status, headers, body (Buffer|string) }.
function fakeFetch(routes) {
  const calls = [];
  const fn = async (url, init) => {
    calls.push({ url, init });
    const r = routes[url];
    if (!r) return response(404, {}, '');
    if (r.throws) throw new Error('network');
    return response(r.status || 200, r.headers || {}, r.body || '');
  };
  fn.calls = calls;
  return fn;
}

function response(status, headers, body) {
  const buf = Buffer.isBuffer(body) ? body : Buffer.from(String(body));
  const lower = {};
  for (const k of Object.keys(headers)) lower[k.toLowerCase()] = String(headers[k]);
  let sent = false;
  return {
    status,
    headers: { get: (k) => (k.toLowerCase() in lower ? lower[k.toLowerCase()] : null) },
    body: {
      getReader() {
        // Deliver in 64 KB chunks so the streaming size cap is exercised.
        let offset = 0;
        return {
          read: async () => {
            if (offset >= buf.length) { sent = true; return { done: true, value: undefined }; }
            const value = new Uint8Array(buf.subarray(offset, offset + 65536));
            offset += value.length;
            return { done: false, value };
          },
          cancel: async () => { sent = true; }
        };
      }
    },
    get consumed() { return sent; }
  };
}

module.exports = { REPO_ROOT, STRINGS, readStrings, EN_TEXT, EN, MINI_EN, json, pageIssue, formIssue, fakeFetch };
