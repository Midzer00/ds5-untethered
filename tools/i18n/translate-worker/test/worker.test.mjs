// Tests for the Worker with a mocked fetch. Run: node --test test/   (never calls GitHub)
import test from 'node:test';
import assert from 'node:assert/strict';
import nodeCrypto from 'node:crypto';
import fs from 'node:fs';
import zlib from 'node:zlib';
import worker from '../worker.js';
import { appJwt, pkcs1ToPkcs8, privateKeyDer } from '../app-auth.js';
import { createRequire } from 'node:module';
import { checkSubmission, issueBody, countFenceOpens, cleanName, MARKER, CALLBACKS, DEV_CALLBACK } from '../submission.js';

const intake = createRequire(import.meta.url)('../../translation-check/intake.js');

const SITE = 'https://ds5untethered.com';
const DEV = 'http://localhost:8765';

const KEYS = nodeCrypto.generateKeyPairSync('rsa', {
  modulusLength: 2048,
  publicKeyEncoding: { type: 'spki', format: 'pem' },
  privateKeyEncoding: { type: 'pkcs1', format: 'pem' }
});
const PKCS1_PEM = KEYS.privateKey;
const PKCS8_PEM = nodeCrypto.createPrivateKey(PKCS1_PEM).export({ type: 'pkcs8', format: 'pem' });

const ENV = {
  OAUTH_CLIENT_ID: 'Ov23liTESTCLIENTID00',
  OAUTH_CLIENT_SECRET: 'test-oauth-secret-0000',
  APP_ID: '123456',
  APP_PRIVATE_KEY: PKCS1_PEM,
  REPO: 'owner-x/repo-y',
  DEV: 'false',
  COMMENT_DELAY_MS: 0
};
const env = (extra) => Object.assign({}, ENV, extra || {});

const USER_TOKEN = 'gho_' + 'u'.repeat(36);
const INST_TOKEN = 'ghs_' + 'i'.repeat(36);
const VOLUNTEER = { login: 'anna-dev', id: 4242, type: 'User' };

/* ---------- a page-built submission ---------- */

function block(k, n, sha, data) {
  return '```ds5-translate part=' + k + '/' + n + ' encoding=gzip+base64 sha256=' + sha + '\n' + data.match(/.{1,76}/g).join('\n') + '\n```';
}

function submission(opts) {
  opts = opts || {};
  const text = opts.file || JSON.stringify({ '@name': 'Deutsch', 'About.Version': 'Version {version}' }, null, 2) + '\n';
  const sha = nodeCrypto.createHash('sha256').update(text).digest('hex');
  const b64 = zlib.gzipSync(Buffer.from(text)).toString('base64');
  const size = opts.partChars || b64.length;
  const chunks = [];
  for (let i = 0; i < b64.length; i += size) chunks.push(b64.slice(i, i + size));
  const n = chunks.length;
  const body = MARKER + '\n' + (opts.extraHead || '') + '**Language:** de (Deutsch)\n**Strings:** 1 of 2 · sent from ds5untethered.com/translate\n\n' +
    '<details><summary>Translation file (compressed)</summary>\n\n' + block(1, n, sha, chunks[0]) + '\n</details>\n';
  const comments = chunks.slice(1).map((c, i) => block(i + 2, n, sha, c));
  return Object.assign({
    code: 'a1b2c3d4e5f6a7b8c9d0',
    code_verifier: 'v'.repeat(64),
    redirect_uri: CALLBACKS[0],
    title: 'Translation: de',
    body,
    comments,
    listing: 'shown'
  }, opts.fields || {});
}

const post = (body, origin = SITE, path = '/submit') => new Request('https://translate-auth.example' + path, {
  method: 'POST',
  headers: Object.assign({ 'Content-Type': 'text/plain;charset=UTF-8', 'CF-Connecting-IP': '203.0.113.7' }, origin ? { Origin: origin } : {}),
  body: typeof body === 'string' ? body : JSON.stringify(body)
});

/* ---------- a fake GitHub ---------- */

const REVOKE_USER = 'DELETE /applications/' + ENV.OAUTH_CLIENT_ID + '/grant';

const json = (status, obj) => new Response(obj === undefined ? null : JSON.stringify(obj), { status, headers: { 'Content-Type': 'application/json' } });

// routes: overrides keyed by "METHOD path" (path without the host). Every call is recorded.
function github(routes) {
  routes = routes || {};
  const calls = [];
  let issueNumber = 77;
  const defaults = {
    'POST /login/oauth/access_token': () => json(200, { access_token: USER_TOKEN, token_type: 'bearer', scope: '' }),
    'GET /user': () => json(200, Object.assign({ name: 'Anna', email: null }, VOLUNTEER)),
    [REVOKE_USER]: () => new Response(null, { status: 204 }),
    'GET /repos/owner-x/repo-y/installation': () => json(200, { id: 99 }),
    'POST /app/installations/99/access_tokens': () => json(201, { token: INST_TOKEN, expires_at: '2026-11-26T12:00:00Z' }),
    'POST /repos/owner-x/repo-y/issues': () => json(201, { number: issueNumber, html_url: 'https://github.com/owner-x/repo-y/issues/' + issueNumber }),
    'POST /repos/owner-x/repo-y/issues/77/comments': () => json(201, { id: 1 }),
    'PATCH /repos/owner-x/repo-y/issues/77': () => json(200, {}),
    'DELETE /installation/token': () => new Response(null, { status: 204 })
  };
  const fn = async (url, init) => {
    const u = new URL(String(url));
    const method = (init && init.method) || 'GET';
    const key = method + ' ' + u.pathname;
    calls.push({ key, url: String(url), init: init || {} });
    const h = routes[key] || defaults[key];
    if (!h) throw new Error('unexpected call ' + key);
    return h(init || {}, calls.length);
  };
  fn.calls = calls;
  fn.keys = () => calls.map((c) => c.key);
  fn.find = (key) => calls.filter((c) => c.key === key);
  return fn;
}

async function withFetch(fake, fn) {
  const real = globalThis.fetch;
  globalThis.fetch = fake;
  try { return await fn(fake); } finally { globalThis.fetch = real; }
}


/* ---------- JWT and keys ---------- */

function decodePart(p) { return JSON.parse(Buffer.from(p, 'base64url').toString('utf8')); }

test('JWT: RS256, verifiable with the App\'s public key, iat 60 s back, exp within 10 minutes, iss = App ID', async () => {
  const now = 1_800_000_000;
  for (const pem of [PKCS1_PEM, PKCS8_PEM]) {
    const jwt = await appJwt('123456', pem, now);
    const [h, p, s] = jwt.split('.');
    assert.deepEqual(decodePart(h), { alg: 'RS256', typ: 'JWT' });
    assert.deepEqual(decodePart(p), { iat: now - 60, exp: now + 540, iss: 123456 });
    assert.ok(decodePart(p).exp - now <= 600);
    const ok = nodeCrypto.verify('RSA-SHA256', Buffer.from(h + '.' + p), KEYS.publicKey, Buffer.from(s, 'base64url'));
    assert.equal(ok, true);
    // A different key does not verify it.
    const other = nodeCrypto.generateKeyPairSync('rsa', { modulusLength: 2048 }).publicKey;
    assert.equal(nodeCrypto.verify('RSA-SHA256', Buffer.from(h + '.' + p), other, Buffer.from(s, 'base64url')), false);
  }
  // A client ID as iss stays a string.
  const jwt = await appJwt('Iv23ctYlmBbJsIrz6c26', PKCS8_PEM, now);
  assert.equal(decodePart(jwt.split('.')[1]).iss, 'Iv23ctYlmBbJsIrz6c26');
});

test('PKCS#1 is wrapped to exactly the PKCS#8 Node produces; PKCS#8 is used as given', () => {
  const pkcs1Der = nodeCrypto.createPrivateKey(PKCS1_PEM).export({ type: 'pkcs1', format: 'der' });
  const pkcs8Der = nodeCrypto.createPrivateKey(PKCS1_PEM).export({ type: 'pkcs8', format: 'der' });
  assert.deepEqual(Buffer.from(pkcs1ToPkcs8(new Uint8Array(pkcs1Der))), pkcs8Der);
  assert.deepEqual(Buffer.from(privateKeyDer(PKCS1_PEM)), pkcs8Der);
  assert.deepEqual(Buffer.from(privateKeyDer(PKCS8_PEM)), pkcs8Der);
  // Pasted with CRLF, literal \n escapes or as one line of base64 without the PEM lines.
  assert.deepEqual(Buffer.from(privateKeyDer(PKCS1_PEM.replace(/\n/g, '\r\n'))), pkcs8Der);
  assert.deepEqual(Buffer.from(privateKeyDer(PKCS1_PEM.replace(/\n/g, '\\n'))), pkcs8Der);
  assert.deepEqual(Buffer.from(privateKeyDer(pkcs1Der.toString('base64'))), pkcs8Der);
  // Short-form and long-form DER lengths.
  const small = pkcs1ToPkcs8(new Uint8Array([0x30, 0x03, 0x02, 0x01, 0x00]));
  assert.deepEqual(Array.from(small.slice(0, 2)), [0x30, 3 + 15 + 2 + 5]);
  assert.throws(() => privateKeyDer('-----BEGIN RSA PRIVATE KEY-----\nnot base64!\n-----END RSA PRIVATE KEY-----'));
  assert.throws(() => privateKeyDer(''));
  assert.throws(() => privateKeyDer('-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----'));
});

/* ---------- the happy path ---------- */

test('submit: identifies, revokes, mints a scoped installation token, opens the issue as the bot, then the comments in order', async () => {
  const sub = submission({ partChars: 200, file: JSON.stringify({ '@name': 'Deutsch', pad: nodeCrypto.randomBytes(400).toString('hex') }) });
  assert.ok(sub.comments.length >= 1);
  await withFetch(github(), async (gh) => {
    const r = await worker.fetch(post(sub), env());
    assert.equal(r.status, 200);
    assert.equal(r.headers.get('Access-Control-Allow-Origin'), SITE);
    assert.equal(r.headers.get('Cache-Control'), 'no-store');
    const text = await r.text();
    assert.deepEqual(JSON.parse(text), { issue_url: 'https://github.com/owner-x/repo-y/issues/77', issue_number: 77 });
    assert.ok(!text.includes('gho_') && !text.includes('ghs_') && !text.includes(ENV.OAUTH_CLIENT_SECRET));

    const comments = sub.comments.map(() => 'POST /repos/owner-x/repo-y/issues/77/comments');
    assert.deepEqual(gh.keys(), [
      'POST /login/oauth/access_token', 'GET /user', REVOKE_USER,
      'GET /repos/owner-x/repo-y/installation', 'POST /app/installations/99/access_tokens',
      'POST /repos/owner-x/repo-y/issues', ...comments, 'DELETE /installation/token'
    ]);

    // The code exchange: client secret, PKCE verifier and the allowlisted callback the page used.
    const form = new URLSearchParams(gh.calls[0].init.body);
    assert.equal(form.get('client_id'), ENV.OAUTH_CLIENT_ID);
    assert.equal(form.get('client_secret'), ENV.OAUTH_CLIENT_SECRET);
    assert.equal(form.get('code'), sub.code);
    assert.equal(form.get('code_verifier'), sub.code_verifier);
    assert.equal(form.get('redirect_uri'), CALLBACKS[0]);
    assert.equal(gh.calls[1].init.headers.Authorization, 'Bearer ' + USER_TOKEN);

    // The whole grant is deleted (not only the token) with Basic auth.
    const rev = gh.calls[2];
    assert.equal(rev.init.headers.Authorization, 'Basic ' + btoa(ENV.OAUTH_CLIENT_ID + ':' + ENV.OAUTH_CLIENT_SECRET));
    assert.deepEqual(JSON.parse(rev.init.body), { access_token: USER_TOKEN });

    // The App JWT verifies with the public key; the installation token is scoped to the repository and issues.
    const jwt = gh.calls[3].init.headers.Authorization.replace(/^Bearer /, '');
    const [h, p, s] = jwt.split('.');
    assert.equal(nodeCrypto.verify('RSA-SHA256', Buffer.from(h + '.' + p), KEYS.publicKey, Buffer.from(s, 'base64url')), true);
    assert.equal(decodePart(p).iss, 123456);
    assert.equal(gh.calls[4].init.headers.Authorization, 'Bearer ' + jwt);
    assert.deepEqual(JSON.parse(gh.calls[4].init.body), { repositories: ['repo-y'], permissions: { issues: 'write' } });

    // The issue: rebuilt title, the sender line (no @) directly under the marker; a page-built body comes out the same.
    const issue = JSON.parse(gh.calls[5].init.body);
    assert.equal(gh.calls[5].init.headers.Authorization, 'Bearer ' + INST_TOKEN);
    assert.equal(issue.title, 'Translation: de');
    assert.equal(issue.body, sub.body.replace(MARKER + '\n', MARKER + '\n**Sender:** anna-dev (id 4242, verified)\n'));
    const posted = gh.find('POST /repos/owner-x/repo-y/issues/77/comments').map((c) => JSON.parse(c.init.body).body);
    assert.deepEqual(posted, sub.comments);
    assert.equal(gh.calls[gh.calls.length - 1].init.headers.Authorization, 'Bearer ' + INST_TOKEN);
  });
});

test('submit: a hidden listing adds "**Listing:** hidden" under the sender line', async () => {
  await withFetch(github(), async (gh) => {
    const sub = submission({ fields: { listing: 'hidden' } });
    assert.equal((await worker.fetch(post(sub), env())).status, 200);
    const body = JSON.parse(gh.find('POST /repos/owner-x/repo-y/issues')[0].init.body).body;
    assert.ok(body.startsWith(MARKER + '\n**Sender:** anna-dev (id 4242, verified)\n**Listing:** hidden\n**Language:** de'));
    assert.ok(!body.includes('@'), 'a hidden volunteer is not mentioned');
  });
});

test('submit: redirect_uri must be one of the two callback pages and is sent to GitHub at the exchange', async () => {
  assert.deepEqual(CALLBACKS, [
    'https://ds5untethered.com/translate/oauth-callback.html',
    'https://ds5untethered.com/preview-e31a5521/translate/oauth-callback.html'
  ]);
  for (const uri of CALLBACKS) {
    await withFetch(github(), async (gh) => {
      assert.equal((await worker.fetch(post(submission({ fields: { redirect_uri: uri } })), env())).status, 200, uri);
      assert.equal(new URLSearchParams(gh.calls[0].init.body).get('redirect_uri'), uri);
    });
  }
  const bad = [undefined, 42, '', 'https://evil.example/x', 'https://ds5untethered.com/', 'https://ds5untethered.com/translate/',
    'https://ds5untethered.com/translate/oauth-callback.html?x=1', 'https://ds5untethered.com/translate/oauth-callback.html#x',
    'https://ds5untethered.com/other/oauth-callback.html', 'http://ds5untethered.com/translate/oauth-callback.html',
    'https://DS5UNTETHERED.com/translate/oauth-callback.html', DEV_CALLBACK];
  await withFetch(github(), async (gh) => {
    for (const uri of bad) {
      const r = await worker.fetch(post(submission({ fields: { redirect_uri: uri } })), env());
      assert.equal(r.status, 400, String(uri));
      assert.deepEqual(await r.json(), { error: 'bad_redirect' }, String(uri));
    }
    assert.equal(gh.calls.length, 0);
  });
  // The local callback only on a DEV Worker.
  await withFetch(github(), async (gh) => {
    const r = await worker.fetch(post(submission({ fields: { redirect_uri: DEV_CALLBACK } }), DEV), env({ DEV: 'true' }));
    assert.equal(r.status, 200);
    assert.equal(new URLSearchParams(gh.calls[0].init.body).get('redirect_uri'), DEV_CALLBACK);
  });
});

test('submit: a retry with an already used code gets bad_verification_code and opens nothing', async () => {
  const used = new Set();
  const gh = github({
    'POST /login/oauth/access_token': (init) => {
      const code = new URLSearchParams(init.body).get('code');
      if (used.has(code)) return json(200, { error: 'bad_verification_code', error_description: 'The code passed is incorrect or expired.' });
      used.add(code);
      return json(200, { access_token: USER_TOKEN, token_type: 'bearer', scope: '' });
    }
  });
  await withFetch(gh, async () => {
    const sub = submission();
    assert.equal((await worker.fetch(post(sub), env())).status, 200);
    const again = await worker.fetch(post(sub), env());
    assert.equal(again.status, 400);
    assert.deepEqual(await again.json(), { error: 'bad_verification_code' });
    assert.equal(gh.find('POST /repos/owner-x/repo-y/issues').length, 1);
    assert.equal(gh.find('POST /app/installations/99/access_tokens').length, 1);
  });
});

/* ---------- sender and listing lines ---------- */

test('sender lines the volunteer wrote are dropped; only the verified one is under the marker', () => {
  const forged = '**Sender:** @someone-else (id 1, verified)\n';
  const raw = submission({ extraHead: forged + '  **sender:** @x (id 2, verified)\n\u2028**Sender:** v2 (id 3, verified)\n**Listing:** hidden\n' });
  const sub = checkSubmission(raw);
  assert.equal(sub.ok, true);
  const body = issueBody(sub, { login: 'anna-dev', id: 4242 });
  const lines = body.split('\n');
  assert.equal(lines[0], MARKER);
  assert.equal(lines[1], '**Sender:** anna-dev (id 4242, verified)');
  assert.equal(body.match(/\*\*Sender:\*\*/gi).length, 1);
  assert.ok(!/someone-else|@x |v2|\u2028/.test(body));
  assert.ok(!/\*\*Listing:\*\*/.test(body), 'the listing line comes from the listing field only');
  assert.equal(lines[2], '**Language:** de (Deutsch)');
  // The fenced part is kept byte for byte.
  assert.ok(body.includes(raw.body.slice(raw.body.indexOf('```ds5-translate'), raw.body.indexOf('\n</details>'))));
});

/* ---------- the rebuilt issue (nothing the volunteer typed is reposted by the bot) ---------- */

const NASTY = [
  '@torvalds', '@org/team', 'https://evil.example/login', 'http://evil.example', 'www.evil.example', 'evil.example/x',
  '[Click](https://evil.example)', '<a href="https://evil.example">x</a>', '<img src=x onerror=alert(1)>', 'other/repo#1',
  'owner-x/repo-y#12', '#123', 'GH-5', 'deadbeefcafe1234', '![i](https://evil.example/p.png)', '<details open>', '**bold**',
  '`code`', '> quote', '- [ ] task', '| table |', ':shipit:', '\u202eevil', 'mailto:a@b.example', 'a@b.example'
];
// Structure that would let the bot mention, link, reference or format anything. Letters alone may survive in the name.
const NEEDLES = ['@', '://', 'www.', '.example', 'repo#', '#1', 'GH-5', '<a', '<img', 'onerror', '**bold', '`code', '> quote',
  '[ ]', '|', ':shipit:', '\u202e', 'mailto:', '](', '<details open', '<b>'];
const LINE_OK = [
  (l) => l === MARKER,
  (l) => l === '**Sender:** anna-dev (id 4242, verified)',
  (l) => l === '**Listing:** hidden',
  (l) => /^\*\*Language:\*\* de \([\p{L}\p{M} -]{1,40}\)$/u.test(l),
  (l) => /^\*\*Strings:\*\* \d{1,7} of \d{1,7} \u00b7 sent from ds5untethered\.com\/translate$/.test(l),
  (l) => l === '' || l === '<details><summary>Translation file (compressed)</summary>' || l === '</details>',
  (l) => /^```ds5-translate part=\d{1,2}\/\d{1,2} encoding=gzip\+base64 sha256=[0-9a-f]{64}$/.test(l),
  (l) => /^[A-Za-z0-9+/=]*$/.test(l) || l === '```'
];

function rebuilt(raw, listing) {
  const sub = checkSubmission(Object.assign(raw, { listing: listing || 'shown' }));
  assert.equal(sub.ok, true, JSON.stringify(sub));
  return { sub, title: sub.title, body: issueBody(sub, { login: 'anna-dev', id: 4242 }), comments: sub.comments };
}

function assertClean(out, what) {
  assert.match(out.title, /^Translation: de$/);
  for (const text of [out.body].concat(out.comments)) {
    for (const l of text.split('\n')) assert.ok(LINE_OK.some((ok) => ok(l)), what + ': unexpected line ' + JSON.stringify(l));
    for (const n of NEEDLES) assert.ok(!text.includes(n), what + ': "' + n + '" reached the issue');
  }
}

test('H1: mentions, links, repo references, markdown and HTML anywhere in the volunteer body never reach the issue', () => {
  const junk = NASTY.join(' ');
  for (const [where, make] of [
    ['before the language line', (b) => b.replace(MARKER + '\n', MARKER + '\n' + junk + '\n')],
    ['after the marker on its own lines', (b) => b.replace(MARKER + '\n', MARKER + '\n' + NASTY.join('\n') + '\n')],
    ['in the language name', (b) => b.replace('(Deutsch)', '(Deutsch ' + junk + ')')],
    ['after the strings numbers', (b) => b.replace('1 of 2 \u00b7 sent from ds5untethered.com/translate', '1 of 2 ' + junk)],
    ['in the summary', (b) => b.replace('Translation file (compressed)', junk)],
    ['instead of the details wrapper', (b) => b.replace('<details><summary>Translation file (compressed)</summary>', junk)],
    ['after the fence', (b) => b.replace('\n</details>\n', '\n' + junk + '\n</details>\n' + NASTY.join('\n') + '\n')],
    ['between the header lines', (b) => { const at = b.indexOf('```'); return b.slice(0, at).split('\n').join('\n<!-- ' + junk + ' -->\n') + b.slice(at); }],
    ['as a forged sender and listing', (b) => b.replace(MARKER + '\n', MARKER + '\n**Sender:** @torvalds (id 1, verified) https://evil.example\n**Listing:** @torvalds\n')]
  ]) {
    const raw = submission({ partChars: 200, file: JSON.stringify({ '@name': 'Deutsch', pad: nodeCrypto.randomBytes(400).toString('hex') }) });
    raw.body = make(raw.body);
    assert.equal(checkSubmission(raw).ok, true, where);
    for (const listing of ['shown', 'hidden']) assertClean(rebuilt(Object.assign({}, raw), listing), where + ' / ' + listing);
  }
});

test('H1: the issue is exactly marker, sender, listing, language, numbers, the fixed wrapper and the parts', () => {
  const raw = submission({ partChars: 300, file: JSON.stringify({ '@name': 'x', pad: nodeCrypto.randomBytes(600).toString('hex') }) });
  raw.body = raw.body.replace('(Deutsch)', '(Deutsch @x [l](u) <i>r/r#1 \u0928\u093f 123)');
  const out = rebuilt(raw, 'hidden');
  const n = out.comments.length + 1;
  const sha = /sha256=([0-9a-f]{64})/.exec(raw.body)[1];
  const data1 = /sha256=[0-9a-f]{64}\n([A-Za-z0-9+/=\n]+)\n```/.exec(raw.body)[1];
  assert.equal(out.title, 'Translation: de');
  assert.equal(out.body, [
    MARKER,
    '**Sender:** anna-dev (id 4242, verified)',
    '**Listing:** hidden',
    '**Language:** de (Deutsch x l u i r r \u0928\u093f)',
    '**Strings:** 1 of 2 \u00b7 sent from ds5untethered.com/translate',
    '',
    '<details><summary>Translation file (compressed)</summary>',
    '',
    '```ds5-translate part=1/' + n + ' encoding=gzip+base64 sha256=' + sha,
    data1,
    '```',
    '</details>',
    ''
  ].join('\n'));
  out.comments.forEach((c, i) => assert.match(c, new RegExp('^```ds5-translate part=' + (i + 2) + '/' + n + ' encoding=gzip\\+base64 sha256=' + sha + '\\n[A-Za-z0-9+/=\\n]+\\n```$')));
  assert.deepEqual(out.comments, raw.comments, 'page-built comments come out unchanged');
});

test('H1: title rebuilt from the code; language line required and must match; name letters only, 40 at most', () => {
  const s = () => submission();
  const lang = (name) => { const x = s(); x.body = x.body.replace('(Deutsch)', name); return checkSubmission(x); };
  assert.equal(lang('(Portugu\u00eas (Brasil))').language.name, 'Portugu\u00eas Brasil');
  assert.equal(lang('').language.name, 'de', 'no name: the code');
  assert.equal(lang('(@@@ 123 ///)').language.name, 'de', 'nothing left: the code');
  assert.equal(Array.from(lang('(' + 'ab '.repeat(40) + ')').language.name).length <= 40, true);
  assert.equal(lang('(' + '\u4e2d'.repeat(50) + ')').language.name, '\u4e2d'.repeat(40));
  assert.equal(cleanName('a\u200bb\u202ec-d e_f.g'), 'a b c-d e f g');
  const wrongCode = s(); wrongCode.body = wrongCode.body.replace('**Language:** de', '**Language:** fr');
  assert.deepEqual(checkSubmission(wrongCode), { ok: false, error: 'bad_body' });
  const noLang = s(); noLang.body = noLang.body.replace(/\*\*Language:\*\*[^\n]*\n/, '');
  assert.deepEqual(checkSubmission(noLang), { ok: false, error: 'bad_body' });
  const after = s(); after.body = after.body.replace(/\*\*Language:\*\*[^\n]*\n/, '').replace('</details>', '</details>\n**Language:** de (x)');
  assert.deepEqual(checkSubmission(after), { ok: false, error: 'bad_body' }, 'the language line must be above the file');
  const more = s(); more.body = more.body.replace('**Strings:** 1 of 2', '**Strings:** 3 of 2');
  assert.deepEqual(checkSubmission(more), { ok: false, error: 'bad_body' });
  const noStrings = s(); noStrings.body = noStrings.body.replace(/\*\*Strings:\*\*[^\n]*\n/, '');
  const ns = checkSubmission(noStrings);
  assert.equal(ns.strings, null);
  assert.ok(!issueBody(ns, { login: 'a', id: 1 }).includes('Strings'));
  const caseTitle = s(); caseTitle.title = 'Translation: DE'; caseTitle.body = caseTitle.body.replace('**Language:** de', '**Language:** DE');
  assert.equal(checkSubmission(caseTitle).title, 'Translation: DE');
});

test('H1: the rebuilt issue is read by the intake as before (verified sender, code, listing, parts)', () => {
  for (const listing of ['shown', 'hidden']) {
    const raw = submission({ partChars: 200, file: JSON.stringify({ '@name': 'Deutsch', pad: nodeCrypto.randomBytes(300).toString('hex') }) });
    const out = rebuilt(raw, listing);
    const bot = { login: 'ds5-untethered-translate[bot]', type: 'Bot' };
    const issue = { title: out.title, body: out.body, user: bot, performed_via_github_app: { slug: 'ds5-untethered-translate' } };
    assert.equal(intake.recognise(issue), 'page');
    assert.deepEqual(intake.verifiedSender(out.body), { login: 'anna-dev', id: 4242 });
    assert.deepEqual(intake.volunteerOf(issue), { login: 'anna-dev', id: 4242, verified: true });
    assert.equal(intake.listingFromPageBody(out.body), listing);
    const comments = out.comments.map((body, i) => ({ id: i + 1, body, user: bot }));
    const d = intake.resolveSubmission(issue, comments);
    assert.equal(d.action, 'check', JSON.stringify(d).slice(0, 300));
    assert.equal(d.code, 'de');
    assert.equal(d.hidden, listing === 'hidden');
  }
});

/* ---------- cheap rejection before regex work (L2) ---------- */

test('L2: more than one fence opener in the body or a comment is refused before any fence regex runs', () => {
  assert.equal(countFenceOpens('', 1), 0);
  assert.equal(countFenceOpens('```ds5-translate', 1), 1);
  assert.equal(countFenceOpens('```ds5-translate```ds5-translate```ds5-translate', 1), 2, 'counting stops past the cap');
  const realExec = RegExp.prototype.exec;
  let fenceRuns = 0;
  RegExp.prototype.exec = function (s) { if (this.source.includes('ds5-translate')) fenceRuns++; return realExec.call(this, s); };
  try {
    const bodies = [
      (MARKER + '\n' + '```ds5-translate\n'.repeat(3528)).slice(0, 60000),
      (MARKER + '\n' + '```ds5-translate x\n'.repeat(3000)).slice(0, 60000)
    ];
    for (const body of bodies) assert.deepEqual(checkSubmission(Object.assign(submission(), { body })), { ok: false, error: 'bad_part' });
    const two = submission({ partChars: 100, file: JSON.stringify({ a: nodeCrypto.randomBytes(200).toString('hex') }) });
    two.comments[0] = two.comments[0] + '\n' + '```ds5-translate x\n'.repeat(3000);
    assert.deepEqual(checkSubmission(two), { ok: false, error: 'bad_part' });
    // Over the size caps: refused before even counting.
    assert.deepEqual(checkSubmission(Object.assign(submission(), { body: MARKER + '\n' + '```ds5-translate\n'.repeat(4000) })), { ok: false, error: 'too_large' });
    assert.equal(fenceRuns, 0, 'no fence regex ran on junk');
    // A normal send runs the fence regex once per part.
    const ok = submission({ partChars: 100, file: JSON.stringify({ a: nodeCrypto.randomBytes(200).toString('hex') }) });
    assert.equal(checkSubmission(ok).ok, true);
    assert.equal(fenceRuns, ok.comments.length + 1);
  } finally {
    RegExp.prototype.exec = realExec;
  }
});

test('a forged sender line on the marker\'s own line is refused: the marker must be the whole first line', () => {
  const sub = submission();
  sub.body = sub.body.replace(MARKER + '\n', MARKER + ' **Sender:** @evil (id 1, verified)\n');
  assert.deepEqual(checkSubmission(sub), { ok: false, error: 'bad_marker' });
});

/* ---------- request checks ---------- */

test('CORS: preflight and responses only for ds5untethered.com; localhost:8765 only with DEV=true', async () => {
  const ok = await worker.fetch(new Request('https://x/submit', { method: 'OPTIONS', headers: { Origin: SITE } }), env());
  assert.equal(ok.status, 204);
  assert.equal(ok.headers.get('Access-Control-Allow-Origin'), SITE);
  assert.equal(ok.headers.get('Access-Control-Allow-Methods'), 'POST');
  for (const o of ['https://evil.example', 'https://ds5untethered.com.evil.example', 'http://ds5untethered.com', DEV, 'null']) {
    const r = await worker.fetch(new Request('https://x/submit', { method: 'OPTIONS', headers: { Origin: o } }), env());
    assert.equal(r.status, 403, o);
    assert.equal(r.headers.get('Access-Control-Allow-Origin'), null);
  }
  await withFetch(github(), async (gh) => {
    for (const o of ['https://evil.example', DEV, null]) {
      const r = await worker.fetch(post(submission(), o), env());
      assert.equal(r.status, 403, String(o));
      assert.deepEqual(await r.json(), { error: 'origin' });
    }
    assert.equal(gh.calls.length, 0);
    const r = await worker.fetch(post(submission(), DEV), env({ DEV: 'true' }));
    assert.equal(r.status, 200);
    assert.equal(r.headers.get('Access-Control-Allow-Origin'), DEV);
  });
});

test('routes: only POST /submit; /token and /revoke are gone', async () => {
  await withFetch(github(), async (gh) => {
    for (const p of ['/token', '/revoke', '/', '/submit/x']) assert.equal((await worker.fetch(post(submission(), SITE, p), env())).status, 404, p);
    assert.equal((await worker.fetch(new Request('https://x/submit', { method: 'GET', headers: { Origin: SITE } }), env())).status, 405);
    assert.equal(gh.calls.length, 0);
  });
});

test('size caps: body and comments 60000 characters, 64 parts, 1.5 MB request', async () => {
  const big = submission();
  big.body = big.body + 'x'.repeat(60001 - big.body.length);
  assert.deepEqual(checkSubmission(big), { ok: false, error: 'too_large' });
  const edge = submission();
  edge.body = edge.body + 'x'.repeat(60000 - edge.body.length);
  assert.equal(checkSubmission(edge).ok, true);

  const many = submission({ partChars: 4, file: nodeCrypto.randomBytes(200).toString('hex') });
  assert.ok(many.comments.length + 1 > 64);
  assert.deepEqual(checkSubmission(many), { ok: false, error: 'too_many_parts' });
  const longComment = submission({ partChars: 100, file: JSON.stringify({ a: nodeCrypto.randomBytes(300).toString('hex') }) });
  longComment.comments[0] = longComment.comments[0] + ' '.repeat(60001);
  assert.deepEqual(checkSubmission(longComment), { ok: false, error: 'too_large' });

  await withFetch(github(), async (gh) => {
    const huge = JSON.stringify(Object.assign(submission(), { pad: 'y'.repeat(1536 * 1024) }));
    let r = await worker.fetch(post(huge), env());
    assert.equal(r.status, 413);
    assert.deepEqual(await r.json(), { error: 'too_large' });
    // A lying (absent) Content-Length does not get the whole body read.
    r = await worker.fetch(new Request('https://x/submit', { method: 'POST', headers: { Origin: SITE }, body: new Blob([huge]).stream(), duplex: 'half' }), env());
    assert.equal(r.status, 413);
    assert.equal(gh.calls.length, 0);
  });
});

test('malformed requests and fences never reach GitHub', async () => {
  const s = () => submission();
  const two = () => submission({ partChars: 100, file: JSON.stringify({ a: nodeCrypto.randomBytes(200).toString('hex') }) });
  const cases = [
    ['not json', 'bad_json'],
    ['', 'bad_json'],
    ['[1,2]', 'bad_request'],
    [Object.assign(s(), { code: 'bad code!' }), 'bad_code'],
    [Object.assign(s(), { code: undefined }), 'bad_code'],
    [Object.assign(s(), { code_verifier: 'short' }), 'bad_code'],
    [Object.assign(s(), { code_verifier: undefined }), 'bad_code'],
    [Object.assign(s(), { listing: 'maybe' }), 'bad_listing'],
    [Object.assign(s(), { title: 7 }), 'bad_title'],
    [Object.assign(s(), { body: null }), 'bad_body'],
    [Object.assign(s(), { comments: 'x' }), 'bad_body'],
    [Object.assign(s(), { comments: [1] }), 'bad_body'],
    [Object.assign(s(), { title: 'Translation: de extra' }), 'bad_title'],
    [Object.assign(s(), { title: 'Translation:de' }), 'bad_title'],
    [Object.assign(s(), { title: 'Bug report' }), 'bad_title'],
    [Object.assign(s(), { body: 'hello\n' + s().body }), 'bad_marker'],
    [(() => { const x = s(); x.body = x.body.replace('encoding=gzip+base64', 'encoding=plain'); return x; })(), 'bad_part'],
    [(() => { const x = s(); x.body = x.body.replace(/sha256=[0-9a-f]{64}/, 'sha256=abc'); return x; })(), 'bad_part'],
    [(() => { const x = s(); x.body = x.body.replace('part=1/1', 'part=2/1'); return x; })(), 'bad_part'],
    [(() => { const x = s(); x.body = x.body.replace('part=1/1', 'part=1/2'); return x; })(), 'bad_part'],
    [(() => { const x = s(); x.body = x.body.replace(/\n```\n<\/details>/, '\n</details>'); return x; })(), 'bad_part'],
    [(() => { const x = s(); x.body = x.body.replace('<details>', '```ds5-translate part=1/1\n```\n<details>'); return x; })(), 'bad_part'],
    [(() => { const x = s(); x.body = x.body.replace(/(sha256=[0-9a-f]{64}\n)/, '$1<b>!</b>\n'); return x; })(), 'bad_part'],
    [(() => { const x = s(); x.body = x.body.replace(/```ds5-translate[\s\S]*```\n/, ''); return x; })(), 'bad_part'],
    [(() => { const x = s(); x.body = x.body + '\n' + x.body.slice(x.body.indexOf('```ds5-translate')); return x; })(), 'bad_part'],
    [(() => { const x = two(); x.comments[0] = 'hello\n' + x.comments[0]; return x; })(), 'bad_part'],
    [(() => { const x = two(); x.comments[0] = x.comments[0] + '\nmore text'; return x; })(), 'bad_part'],
    [(() => { const x = two(); x.comments.push(x.comments[0]); return x; })(), 'bad_part'],
    [(() => { const x = two(); x.comments.reverse(); return x.comments.length > 1 ? x : Object.assign(x, { comments: [] }); })(), 'bad_part'],
    [(() => { const x = two(); x.comments[0] = x.comments[0].replace(/sha256=[0-9a-f]{64}/, 'sha256=' + 'f'.repeat(64)); return x; })(), 'bad_part']
  ];
  await withFetch(github(), async (gh) => {
    for (const [body, error] of cases) {
      const r = await worker.fetch(post(body), env());
      assert.equal(r.status, 400, JSON.stringify(body).slice(0, 120));
      assert.deepEqual(await r.json(), { error }, JSON.stringify(body).slice(0, 200));
    }
    assert.equal(gh.calls.length, 0);
  });
  // CRLF bodies and comments are accepted and sent with LF.
  const crlf = two();
  crlf.body = crlf.body.replace(/\n/g, '\r\n');
  crlf.comments = crlf.comments.map((c) => c.replace(/\n/g, '\r\n'));
  const checked = checkSubmission(crlf);
  assert.equal(checked.ok, true);
  assert.ok(!issueBody(checked, { login: 'a', id: 1 }).includes('\r') && !checked.comments.join('').includes('\r'));
});

test('the smoke-test request in OWNER-SETUP passes the checks and gets bad_verification_code from GitHub', async () => {
  const smoke = fs.readFileSync(new URL('../smoke-submit.json', import.meta.url), 'utf8');
  assert.equal(checkSubmission(JSON.parse(smoke)).ok, true);
  const gh = github({ 'POST /login/oauth/access_token': () => json(200, { error: 'bad_verification_code', error_description: 'The code passed is incorrect or expired.' }) });
  await withFetch(gh, async () => {
    const r = await worker.fetch(post(smoke), env());
    assert.equal(r.status, 400);
    assert.deepEqual(await r.json(), { error: 'bad_verification_code' });
    assert.deepEqual(gh.keys(), ['POST /login/oauth/access_token']);
  });
});

/* ---------- rate limits ---------- */

test('rate limit: per IP before anything, keyed submit:<ip>', async () => {
  const keys = [];
  const LIMITER = { limit: async ({ key }) => { keys.push(key); return { success: false }; } };
  await withFetch(github(), async (gh) => {
    const r = await worker.fetch(post(submission()), env({ LIMITER }));
    assert.equal(r.status, 429);
    assert.deepEqual(await r.json(), { error: 'rate_limited' });
    assert.deepEqual(keys, ['submit:203.0.113.7']);
    assert.equal(gh.calls.length, 0);
  });
});

test('rate limit: per volunteer by user id; the token is revoked and nothing is opened', async () => {
  const keys = [];
  const USER_LIMITER = { limit: async ({ key }) => { keys.push(key); return { success: false }; } };
  await withFetch(github(), async (gh) => {
    const r = await worker.fetch(post(submission()), env({ USER_LIMITER }));
    assert.equal(r.status, 429);
    assert.deepEqual(await r.json(), { error: 'rate_limited' });
    assert.deepEqual(keys, ['user:4242']);
    assert.deepEqual(gh.keys(), ['POST /login/oauth/access_token', 'GET /user', REVOKE_USER]);
  });
});

function fakeKv() {
  const store = new Map();
  return {
    store,
    puts: [],
    async get(k) { return store.has(k) ? store.get(k) : null; },
    async put(k, v, o) { this.puts.push({ k, o }); store.set(k, v); }
  };
}

test('rate limit: a SUBMIT_LOG that fails to read or write refuses the send (fail closed); the grant was deleted', async () => {
  const broken = [
    { async get() { throw new Error('kv down'); }, async put() {} },
    { async get() { return null; }, async put() { throw new Error('kv write limit'); } }
  ];
  for (const kv of broken) {
    await withFetch(github(), async (gh) => {
      const r = await worker.fetch(post(submission()), env({ SUBMIT_LOG: kv }));
      assert.equal(r.status, 429);
      assert.deepEqual(await r.json(), { error: 'rate_limited' });
      assert.deepEqual(gh.keys(), ['POST /login/oauth/access_token', 'GET /user', REVOKE_USER]);
    });
  }
  // An unreadable entry (not a store failure) is replaced and the send goes through.
  const kv = fakeKv();
  kv.store.set('u:4242', '{not json');
  await withFetch(github(), async () => {
    assert.equal((await worker.fetch(post(submission()), env({ SUBMIT_LOG: kv }))).status, 200);
  });
  assert.equal(JSON.parse(kv.store.get('u:4242')).length, 1);
});

test('rate limit: the optional site-wide limiter counts verified sends with one key', async () => {
  const keys = [];
  let left = 1;
  const GLOBAL_LIMITER = { limit: async ({ key }) => { keys.push(key); return { success: left-- > 0 }; } };
  await withFetch(github(), async (gh) => {
    assert.equal((await worker.fetch(post(submission()), env({ GLOBAL_LIMITER }))).status, 200);
    const r = await worker.fetch(post(submission()), env({ GLOBAL_LIMITER }));
    assert.equal(r.status, 429);
    assert.deepEqual(await r.json(), { error: 'rate_limited' });
    assert.deepEqual(keys, ['all', 'all']);
    assert.equal(gh.find('POST /repos/owner-x/repo-y/issues').length, 1);
  });
  // Unrelated junk never reaches it.
  await withFetch(github(), async () => {
    await worker.fetch(post('not json'), env({ GLOBAL_LIMITER }));
    assert.equal(keys.length, 2);
  });
});

test('rate limit: at most 5 sends per user id per rolling hour (SUBMIT_LOG)', async () => {
  const kv = fakeKv();
  await withFetch(github(), async (gh) => {
    for (let i = 0; i < 5; i++) assert.equal((await worker.fetch(post(submission()), env({ SUBMIT_LOG: kv }))).status, 200, 'send ' + (i + 1));
    const r = await worker.fetch(post(submission()), env({ SUBMIT_LOG: kv }));
    assert.equal(r.status, 429);
    assert.deepEqual(await r.json(), { error: 'rate_limited' });
    assert.equal(gh.find('POST /repos/owner-x/repo-y/issues').length, 5);
    assert.equal(gh.find(REVOKE_USER).length, 6);
  });
  assert.deepEqual(kv.puts[0], { k: 'u:4242', o: { expirationTtl: 3600 } });
  // Entries older than an hour no longer count.
  const old = Math.floor(Date.now() / 1000) - 3700;
  kv.store.set('u:4242', JSON.stringify([old, old, old, old, old]));
  await withFetch(github(), async () => {
    assert.equal((await worker.fetch(post(submission()), env({ SUBMIT_LOG: kv }))).status, 200);
  });
  // Another volunteer has their own count.
  const gh2 = github({ 'GET /user': () => json(200, { login: 'lukas', id: 777, type: 'User' }) });
  kv.store.set('u:4242', JSON.stringify(Array(5).fill(Math.floor(Date.now() / 1000))));
  await withFetch(gh2, async () => {
    assert.equal((await worker.fetch(post(submission()), env({ SUBMIT_LOG: kv }))).status, 200);
  });
});

/* ---------- error paths ---------- */

test('errors: bad_verification_code passes through bare; every other exchange answer is exchange_failed', async () => {
  for (const [res, error] of [
    [() => json(200, { error: 'bad_verification_code', error_description: 'secret detail' }), 'bad_verification_code'],
    [() => json(200, { error: 'incorrect_client_credentials' }), 'exchange_failed'],
    [() => json(200, { error: 'redirect_uri_mismatch' }), 'exchange_failed'],
    [() => json(200, { error: 'Weird Error <b>' }), 'exchange_failed'],
    [() => json(200, { access_token: 'gho_short' }), 'exchange_failed'],
    [() => json(500, {}), 'exchange_failed'],
    [() => new Response('not json', { status: 200 }), 'exchange_failed']
  ]) {
    const gh = github({ 'POST /login/oauth/access_token': res });
    await withFetch(gh, async () => {
      const r = await worker.fetch(post(submission()), env());
      assert.equal(r.status, 400);
      const text = await r.text();
      assert.deepEqual(JSON.parse(text), { error });
      assert.ok(!text.includes('detail'));
      assert.deepEqual(gh.keys(), ['POST /login/oauth/access_token'], 'no token, nothing to revoke');
    });
  }
  const down = async () => { throw new TypeError('network'); };
  await withFetch(down, async () => {
    const r = await worker.fetch(post(submission()), env());
    assert.equal(r.status, 502);
    assert.deepEqual(await r.json(), { error: 'github' });
  });
});

test('errors: the user token is revoked even when GET /user fails, throws or returns a non-user', async () => {
  const variants = [
    () => json(401, { message: 'Bad credentials' }),
    () => { throw new TypeError('network'); },
    () => json(200, { login: 'some-org', id: 5, type: 'Organization' }),
    () => json(200, { login: 'bad login', id: 5, type: 'User' }),
    () => json(200, { login: 'anna', id: '5', type: 'User' })
  ];
  for (const v of variants) {
    const gh = github({ 'GET /user': v });
    await withFetch(gh, async () => {
      const r = await worker.fetch(post(submission()), env());
      assert.equal(r.status, 502);
      assert.ok(['user_failed', 'github'].includes((await r.json()).error));
      assert.deepEqual(gh.keys(), ['POST /login/oauth/access_token', 'GET /user', REVOKE_USER]);
    });
  }
});

test('errors: a failing revoke does not stop the send, and the token is still never returned', async () => {
  for (const v of [() => json(500, {}), () => { throw new TypeError('network'); }]) {
    await withFetch(github({ [REVOKE_USER]: v }), async () => {
      const r = await worker.fetch(post(submission()), env());
      assert.equal(r.status, 200);
      assert.ok(!(await r.text()).includes('gho_'));
    });
  }
});

test('errors: App not installed, token refused, bad private key are "installation"; the user token was revoked first', async () => {
  const cases = [
    [{ 'GET /repos/owner-x/repo-y/installation': () => json(404, { message: 'Not Found' }) }, env(), 'installation'],
    [{ 'GET /repos/owner-x/repo-y/installation': () => json(401, { message: 'JWT' }) }, env(), 'installation'],
    [{ 'POST /app/installations/99/access_tokens': () => json(422, { message: 'no' }) }, env(), 'installation'],
    [{}, env({ APP_PRIVATE_KEY: '-----BEGIN RSA PRIVATE KEY-----\nAAAA\n-----END RSA PRIVATE KEY-----' }), 'installation']
  ];
  for (const [routes, e, error] of cases) {
    const gh = github(routes);
    await withFetch(gh, async () => {
      const r = await worker.fetch(post(submission()), e);
      assert.deepEqual(await r.json(), { error }, error);
      assert.ok(r.status >= 500);
      assert.deepEqual(gh.keys().slice(0, 3), ['POST /login/oauth/access_token', 'GET /user', REVOKE_USER]);
      assert.equal(gh.find('POST /repos/owner-x/repo-y/issues').length, 0);
    });
  }
});

test('errors: a failed issue revokes the installation token; a failed comment also closes the half-sent issue', async () => {
  let gh = github({ 'POST /repos/owner-x/repo-y/issues': () => json(410, { message: 'Issues are disabled' }) });
  await withFetch(gh, async () => {
    const r = await worker.fetch(post(submission()), env());
    assert.deepEqual(await r.json(), { error: 'issue_failed' });
    assert.equal(gh.keys().at(-1), 'DELETE /installation/token');
  });
  const sub = submission({ partChars: 100, file: JSON.stringify({ a: nodeCrypto.randomBytes(300).toString('hex') }) });
  assert.ok(sub.comments.length >= 2);
  gh = github({ 'POST /repos/owner-x/repo-y/issues/77/comments': (init, n) => (n > 7 ? json(403, { message: 'secondary rate limit' }) : json(201, {})) });
  await withFetch(gh, async () => {
    const r = await worker.fetch(post(sub), env());
    assert.equal(r.status, 502);
    assert.deepEqual(await r.json(), { error: 'comment_failed' });
    const keys = gh.keys();
    assert.deepEqual(keys.slice(-2), ['PATCH /repos/owner-x/repo-y/issues/77', 'DELETE /installation/token']);
    assert.deepEqual(JSON.parse(gh.find('PATCH /repos/owner-x/repo-y/issues/77')[0].init.body), { state: 'closed', state_reason: 'not_planned' });
    assert.equal(gh.find(REVOKE_USER).length, 1);
  });
});

test('an unconfigured Worker refuses to run, without calling GitHub', async () => {
  await withFetch(github(), async (gh) => {
    for (const missing of ['OAUTH_CLIENT_ID', 'OAUTH_CLIENT_SECRET', 'APP_ID', 'APP_PRIVATE_KEY', 'REPO']) {
      const e = env();
      delete e[missing];
      const r = await worker.fetch(post(submission()), e);
      assert.equal(r.status, 500, missing);
      assert.deepEqual(await r.json(), { error: 'github' });
    }
    assert.equal((await worker.fetch(post(submission()), env({ REPO: 'not a repo' }))).status, 500);
    assert.equal(gh.calls.length, 0);
  });
});

test('the Worker source logs nothing and the config holds no secret', () => {
  for (const f of ['worker.js', 'submission.js', 'app-auth.js']) {
    const src = fs.readFileSync(new URL('../' + f, import.meta.url), 'utf8');
    assert.ok(!/console\./.test(src), f);
  }
  const toml = fs.readFileSync(new URL('../wrangler.toml', import.meta.url), 'utf8');
  assert.ok(!/OAUTH_CLIENT_SECRET\s*=/.test(toml));
  assert.ok(!/APP_PRIVATE_KEY\s*=/.test(toml));
  assert.ok(!/PRIVATE KEY-----/.test(toml));
  assert.match(toml, /\[observability\]\s*\nenabled = false/);
  assert.match(toml, /REPO = "Midzer00\/ds5-untethered"/);
});

test('the Worker answers only with error names the page knows', () => {
  const known = ['rate_limited', 'too_large', 'too_many_parts', 'origin', 'bad_code', 'bad_redirect', 'bad_verification_code', 'exchange_failed',
    'user_failed', 'bad_request', 'bad_json', 'bad_title', 'bad_body', 'bad_marker', 'bad_part', 'bad_listing', 'github',
    'installation', 'issue_failed', 'comment_failed'];
  const used = new Set();
  for (const f of ['worker.js', 'submission.js']) {
    const src = fs.readFileSync(new URL('../' + f, import.meta.url), 'utf8');
    for (const m of src.matchAll(/(?:Stop\(\d+, |error: |fail\()'([a-z_]+)'/g)) used.add(m[1]);
  }
  assert.ok(used.size >= 15, [...used].join(','));
  for (const name of used) assert.ok(known.includes(name), name);
});
