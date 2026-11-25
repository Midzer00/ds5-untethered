'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const zlib = require('zlib');
const crypto = require('crypto');
const intake = require('../intake');
const rules = require('../rules');
const { EN, json, pageIssue, formIssue, fakeFetch } = require('./helpers');

const USER = { login: 'volunteer-1', id: 1234567 };
const GOOD = json({ '@name': 'Deutsch', 'About.Version': 'Version {version}', 'Action.Run': 'Ausführen' });
const ATTACH = 'https://github.com/user-attachments/files/123456/de.json';
const SIGNED = 'https://objects.githubusercontent.com/github-production-repository-file-5c1aeb/1/123456?X-Amz-Signature=abc';

function issueOf(p) { return { title: p.title, body: p.body, user: USER }; }
function commentsOf(p, user) { return p.comments.map((body, i) => ({ id: i + 1, body, user: user || USER })); }

/* ---------- recognising issues ---------- */

test('recognise: marker -> page, form headings -> form, anything else -> null', () => {
  assert.equal(intake.recognise(issueOf(pageIssue('de', GOOD))), 'page');
  assert.equal(intake.recognise(formIssue('de', ATTACH)), 'form');
  assert.equal(intake.recognise({ title: 'Translation: de', body: 'please add German' }), null);
  assert.equal(intake.recognise({ title: 'x', body: null }), null);
  assert.equal(intake.recognise(null), null);
});

test('form body: fields are read by heading, "_No response_" is empty', () => {
  const f = intake.parseFormBody(formIssue('pt-BR', '[pt-BR.json](' + ATTACH + ')').body.replace(/\n/g, '\r\n'));
  assert.equal(f.language, 'pt-BR');
  assert.equal(f.file, '[pt-BR.json](' + ATTACH + ')');
  assert.equal(f.notes, '');
});

/* ---------- way 3: parts ---------- */

test('single part: decoded, verified and ready to check', () => {
  const p = pageIssue('de', GOOD);
  assert.equal(p.parts, 1);
  const d = intake.resolveSubmission(issueOf(p), []);
  assert.equal(d.action, 'check');
  assert.equal(d.code, 'de');
  assert.equal(Buffer.from(d.bytes).toString('utf8'), GOOD);
  assert.equal(intake.checkSubmission(d.code, d.bytes, EN).ok, true);
});

test('single part with CRLF line endings (as GitHub stores web bodies)', () => {
  const p = pageIssue('de', GOOD, { crlf: true });
  assert.equal(intake.resolveSubmission(issueOf(p), []).action, 'check');
});

test('multiple parts: body plus comments are joined in order', () => {
  const big = json(Object.fromEntries(Object.keys(EN).map((k) => [k, EN[k]])));
  const p = pageIssue('de', big, { maxPartChars: 4000 });
  assert.ok(p.parts >= 3, 'parts: ' + p.parts);
  // Comments arrive in any order; an unrelated chat comment sits in between.
  const comments = commentsOf(p).reverse();
  comments.splice(1, 0, { id: 99, body: 'Thanks!', user: USER });
  const d = intake.resolveSubmission(issueOf(p), comments);
  assert.equal(d.action, 'check');
  assert.equal(Buffer.from(d.bytes).toString('utf8'), big);
});

test('multiple parts: a part sent again later replaces the earlier copy', () => {
  const p = pageIssue('de', GOOD + ' '.repeat(3000), { maxPartChars: 100 });
  const comments = commentsOf(p);
  const broken = comments[0].body.replace(/\n[A-Za-z0-9+/]{4}/, '\nAAAA');
  const d = intake.resolveSubmission(issueOf(p), [{ id: 50, body: broken, user: USER }].concat(comments));
  assert.equal(d.action, 'check');
});

test('missing part: wait for the next comment, do nothing yet', () => {
  const p = pageIssue('de', GOOD + ' '.repeat(3000), { maxPartChars: 100 });
  const comments = commentsOf(p);
  const d = intake.resolveSubmission(issueOf(p), comments.slice(0, -1));
  assert.equal(d.action, 'wait');
  assert.equal(d.of, p.parts);
  assert.equal(d.have.length, p.parts - 1);
  assert.equal(intake.resolveSubmission(issueOf(p), []).action, 'wait');
});

test('parts from other people are ignored (only the issue author counts)', () => {
  const p = pageIssue('de', GOOD + ' '.repeat(3000), { maxPartChars: 100 });
  const d = intake.resolveSubmission(issueOf(p), commentsOf(p, { login: 'someone-else', id: 9 }));
  assert.equal(d.action, 'wait');
});

test('parts from an older send (other sha256) are ignored', () => {
  const old = pageIssue('de', json({ 'Action.Run': 'Alt' }) + ' '.repeat(3000), { maxPartChars: 100 });
  const p = pageIssue('de', GOOD + ' '.repeat(3000), { maxPartChars: 100 });
  const comments = commentsOf(old).concat(commentsOf(p).slice(0, -1));
  assert.equal(intake.resolveSubmission(issueOf(p), comments).action, 'wait');
});

test('sha256 mismatch: fails with a checksum message', () => {
  const p = pageIssue('de', GOOD, { sha256: 'a'.repeat(64) });
  const d = intake.resolveSubmission(issueOf(p), []);
  assert.equal(d.action, 'fail');
  assert.equal(d.problems[0].rule, 'parts.sha256');
});

test('damaged base64 or gzip fails', () => {
  const p = pageIssue('de', GOOD);
  const bad = Object.assign({}, issueOf(p), { body: p.body.replace(/\n([A-Za-z0-9+/]{8})/, '\n!!!!!!!!') });
  assert.equal(intake.resolveSubmission(bad, []).problems[0].rule, 'parts.base64');
  assert.equal(intake.decodeParts(Buffer.from('not gzip').toString('base64'), 'a'.repeat(64)).problem.rule, 'parts.gzip');
});

test('a body without a part-1 block, or with a bad header, fails', () => {
  const p = pageIssue('de', GOOD);
  const noBlock = Object.assign({}, issueOf(p), { body: p.body.replace(/```ds5-translate[\s\S]*```/, '') });
  assert.equal(intake.resolveSubmission(noBlock, []).problems[0].rule, 'parts.none');
  const part2 = Object.assign({}, issueOf(p), { body: p.body.replace('part=1/1', 'part=2/2') });
  assert.equal(intake.resolveSubmission(part2, []).problems[0].rule, 'parts.header');
  const enc = Object.assign({}, issueOf(p), { body: p.body.replace('gzip+base64', 'base64') });
  assert.equal(intake.resolveSubmission(enc, []).problems[0].rule, 'parts.encoding');
  const tooMany = Object.assign({}, issueOf(p), { body: p.body.replace('part=1/1', 'part=1/999') });
  assert.equal(intake.resolveSubmission(tooMany, []).problems[0].rule, 'parts.header');
});

test('oversized file: a gzip bomb over 1 MB is refused without unpacking it all', () => {
  const huge = Buffer.alloc(rules.MAX_FILE_BYTES + 10, 0x20);
  const sha = crypto.createHash('sha256').update(huge).digest('hex');
  const r = intake.decodeParts(zlib.gzipSync(huge).toString('base64'), sha);
  assert.equal(r.ok, false);
  assert.equal(r.problem.rule, 'file.size');
  const exact = Buffer.alloc(rules.MAX_FILE_BYTES, 0x20);
  const ok = intake.decodeParts(zlib.gzipSync(exact).toString('base64'), crypto.createHash('sha256').update(exact).digest('hex'));
  assert.equal(ok.ok, true);
  assert.equal(intake.decodeParts('A'.repeat(3 * 1024 * 1024), sha).problem.rule, 'file.size');
});

/* ---------- codes ---------- */

test('wrong code: an invalid code fails', () => {
  const d = intake.resolveSubmission(issueOf(pageIssue('xx_12345', GOOD)), []);
  assert.equal(d.action, 'fail');
  assert.equal(d.problems[0].rule, 'code.format');
});

test('wrong code: the title and the body must name the same language', () => {
  const d = intake.resolveSubmission(issueOf(pageIssue('de', GOOD, { titleCode: 'fr' })), []);
  assert.equal(d.problems[0].rule, 'code.title');
  const f = formIssue('de', ATTACH);
  f.title = 'Translation: nl';
  assert.equal(intake.resolveSubmission(Object.assign({ user: USER }, f), []).problems[0].rule, 'code.title');
});

test('codes are canonicalised (pt_br in the body, pt-BR in the title)', () => {
  const d = intake.resolveSubmission(issueOf(pageIssue('pt-BR', GOOD, { bodyCode: 'pt_br' })), []);
  assert.equal(d.action, 'check');
  assert.equal(d.code, 'pt-BR');
});

test('a title without a code is fine; the body decides', () => {
  const f = Object.assign({ user: USER }, formIssue('de', ATTACH));
  f.title = 'Translation: ';
  assert.equal(intake.resolveSubmission(f, []).action, 'download');
});

test('tr and en are rejected, in both ways', () => {
  for (const code of ['tr', 'en', 'tr-CY']) {
    const p = intake.resolveSubmission(issueOf(pageIssue(code, GOOD)), []);
    assert.equal(p.action, 'fail', code);
    assert.match(p.problems[0].rule, /^code\.(maintained|source)$/);
    const f = intake.resolveSubmission(Object.assign({ user: USER }, formIssue(code, ATTACH)), []);
    assert.equal(f.action, 'fail', code);
    assert.match(f.problems[0].rule, /^code\.(maintained|source)$/);
  }
  for (const code of ['qps', 'qtg']) {
    assert.equal(intake.resolveSubmission(issueOf(pageIssue(code, GOOD)), []).problems[0].rule, 'code.reserved');
  }
});

/* ---------- way 2: attachments ---------- */

test('form: the attachment link is found in markdown', () => {
  const d = intake.resolveSubmission(Object.assign({ user: USER }, formIssue('de', '[de.json](' + ATTACH + ')')), []);
  assert.deepEqual({ action: d.action, code: d.code, url: d.url }, { action: 'download', code: 'de', url: ATTACH });
});

test('form: non-user-attachments URLs are refused', () => {
  for (const u of [
    'https://example.org/de.json',
    'https://gist.githubusercontent.com/x/raw/de.json',
    'http://github.com/user-attachments/files/1/de.json',
    'https://github.com/someone/repo/files/1/de.json',
    'https://github.com.evil.example/user-attachments/files/1/de.json',
    'https://user@github.com/user-attachments/files/1/de.json',
    'https://github.com:444/user-attachments/files/1/de.json'
  ]) {
    const d = intake.resolveSubmission(Object.assign({ user: USER }, formIssue('de', '[de.json](' + u + ')')), []);
    assert.equal(d.action, 'fail', u);
    assert.equal(d.problems[0].rule, 'attachment.host', u);
    assert.equal(intake.isAllowedAttachmentUrl(u), false, u);
  }
});

test('form: no file or two files fail', () => {
  assert.equal(intake.resolveSubmission(Object.assign({ user: USER }, formIssue('de', '_No response_')), []).problems[0].rule, 'attachment.none');
  const two = '[a](' + ATTACH + ') [b](https://github.com/user-attachments/files/7/b.json)';
  assert.equal(intake.resolveSubmission(Object.assign({ user: USER }, formIssue('de', two)), []).problems[0].rule, 'attachment.many');
});

test('download: follows the signed redirect and returns the bytes, without credentials', async () => {
  const f = fakeFetch({ [ATTACH]: { status: 302, headers: { location: SIGNED } }, [SIGNED]: { body: GOOD, headers: { 'content-length': Buffer.byteLength(GOOD) } } });
  const r = await intake.download(ATTACH, f);
  assert.equal(r.ok, true);
  assert.equal(r.bytes.toString('utf8'), GOOD);
  assert.equal(f.calls.length, 2);
  for (const c of f.calls) {
    assert.equal(c.init.redirect, 'manual');
    assert.equal(c.init.headers.Authorization, undefined);
  }
});

test('download: redirects to other hosts are refused', async () => {
  const f = fakeFetch({ [ATTACH]: { status: 302, headers: { location: 'https://evil.example/x' } } });
  const r = await intake.download(ATTACH, f);
  assert.equal(r.ok, false);
  assert.equal(r.problem.rule, 'download');
  assert.equal(f.calls.length, 1);
  assert.equal(intake.isAllowedRedirect('http://objects.githubusercontent.com/x'), false);
  assert.equal(intake.isAllowedRedirect('https://objects.githubusercontent.com.evil.example/x'), false);
  assert.equal(intake.isAllowedRedirect('https://github-production-user-asset-6210df.s3.amazonaws.com/x'), true);
});

test('download: oversized by header or by stream is refused', async () => {
  const big = Buffer.alloc(rules.MAX_FILE_BYTES + 1, 0x20);
  const byHeader = await intake.download(ATTACH, fakeFetch({ [ATTACH]: { body: 'x', headers: { 'content-length': big.length } } }));
  assert.equal(byHeader.problem.rule, 'file.size');
  const byStream = await intake.download(ATTACH, fakeFetch({ [ATTACH]: { body: big } }));
  assert.equal(byStream.problem.rule, 'file.size');
});

test('download: a non-user-attachments URL is never fetched', async () => {
  const f = fakeFetch({});
  const r = await intake.download('https://example.org/de.json', f);
  assert.equal(r.problem.rule, 'attachment.host');
  assert.equal(f.calls.length, 0);
});

test('download: HTTP errors, network errors and redirect loops fail politely', async () => {
  assert.equal((await intake.download(ATTACH, fakeFetch({}))).problem.rule, 'download');
  assert.equal((await intake.download(ATTACH, fakeFetch({ [ATTACH]: { throws: true } }))).problem.rule, 'download');
  const loop = await intake.download(ATTACH, fakeFetch({ [ATTACH]: { status: 302, headers: { location: ATTACH } } }));
  assert.equal(loop.problem.rule, 'download');
});

/* ---------- checks, plan and comments ---------- */

test('the downloaded file goes through the shared rules', () => {
  const r = intake.checkSubmission('de', Buffer.from(json({ 'About.Version': 'Version', 'X.Y': 'z' })), EN);
  assert.deepEqual(r.errors.map((p) => p.rule), ['icu.missing', 'key.unknown']);
});

test('plan: branch, path, commit message with the co-author trailer, PR title and body', () => {
  const result = intake.checkSubmission('pt-BR', Buffer.from(json({ 'Action.Run': 'Executar', '@name': 'Português (Brasil)' })), EN);
  const plan = intake.planPullRequest({ code: 'pt-BR', login: USER.login, userId: USER.id, issueNumber: 42, result, en: EN });
  assert.equal(plan.branch, 'translate/pt-BR-volunteer-1');
  assert.equal(plan.path, 'src/Kopru.Core/Localization/strings/pt-BR.json');
  assert.equal(plan.commitMessage, 'Translation: pt-BR (2 strings)\n\nCo-authored-by: volunteer-1 <1234567+volunteer-1@users.noreply.github.com>\n');
  assert.equal(plan.prTitle, 'Translation: pt-BR by @volunteer-1');
  assert.match(plan.prBody, /#42/);
  assert.match(plan.prBody, /\| Strings \| 2 of \d+ \|/);
  // Normalised to en.json key order, LF, trailing newline.
  assert.equal(plan.content, '{\n  "@name": "Português (Brasil)",\n  "Action.Run": "Executar"\n}\n');
});

test('plan: refuses odd logins, ids and codes', () => {
  const result = intake.checkSubmission('de', Buffer.from(GOOD), EN);
  const base = { code: 'de', login: USER.login, userId: USER.id, issueNumber: 1, result, en: EN };
  assert.throws(() => intake.planPullRequest(Object.assign({}, base, { login: 'a b' })));
  assert.throws(() => intake.planPullRequest(Object.assign({}, base, { login: '../x' })));
  assert.throws(() => intake.planPullRequest(Object.assign({}, base, { userId: '1; x' })));
  assert.throws(() => intake.planPullRequest(Object.assign({}, base, { code: 'tr' })));
});

/* ---------- opting out of the translator listing ---------- */

test('listing (way 3): "**Listing:** hidden" hides, "shown", odd values or no line mean shown', () => {
  const hidden = pageIssue('de', GOOD, { listing: 'hidden' });
  assert.equal(intake.listingFromPageBody(hidden.body), 'hidden');
  assert.equal(intake.listingFromPageBody(hidden.body.replace(/\n/g, '\r\n')), 'hidden');
  assert.equal(intake.listingFromPageBody(pageIssue('de', GOOD, { listing: 'Hidden' }).body), 'hidden');
  assert.equal(intake.listingFromPageBody(pageIssue('de', GOOD, { listing: 'shown' }).body), 'shown');
  assert.equal(intake.listingFromPageBody(pageIssue('de', GOOD, { listing: 'maybe' }).body), 'shown');
  assert.equal(intake.listingFromPageBody(pageIssue('de', GOOD).body), 'shown');
  // Only a line of its own counts, not the words inside other text.
  assert.equal(intake.listingFromPageBody('Note: **Listing:** hidden'), 'shown');
});

test('listing (way 3): resolveSubmission carries hidden', () => {
  const h = pageIssue('de', GOOD, { listing: 'hidden' });
  assert.equal(intake.resolveSubmission(issueOf(h), commentsOf(h)).hidden, true);
  const s = pageIssue('de', GOOD, { listing: 'shown' });
  assert.equal(intake.resolveSubmission(issueOf(s), commentsOf(s)).hidden, false);
  const none = pageIssue('de', GOOD);
  assert.equal(intake.resolveSubmission(issueOf(none), commentsOf(none)).hidden, false);
});

test('listing (way 2): the hide checkbox is read from the Credits section', () => {
  const link = '[de.json](' + ATTACH + ')';
  const ticked = intake.resolveSubmission(Object.assign(formIssue('de', link, null, true), { user: USER }), []);
  assert.equal(ticked.action, 'download');
  assert.equal(ticked.hidden, true);
  assert.equal(intake.resolveSubmission(Object.assign(formIssue('de', link, null, false), { user: USER }), []).hidden, false);
  assert.equal(intake.resolveSubmission(Object.assign(formIssue('de', link), { user: USER }), []).hidden, false);
  // Lower-case x, CRLF bodies.
  const lower = formIssue('de', link, null, true);
  lower.body = lower.body.replace('[X]', '[x]').replace(/\n/g, '\r\n');
  assert.equal(intake.listingHidden('form', lower.body), true);
  // A ticked box with the label written in the Notes does not count.
  const inNotes = formIssue('de', link, '- [X] Do not list my name among the translators', false);
  assert.equal(intake.listingHidden('form', inNotes.body), false);
  assert.equal(intake.hideTickedInForm('- [X] Something else'), false);
});

test('plan: a hidden listing adds the trailer after Co-authored-by and a note in the PR body', () => {
  const result = intake.checkSubmission('de', Buffer.from(GOOD), EN);
  const base = { code: 'de', login: USER.login, userId: USER.id, issueNumber: 3, result, en: EN };
  const hidden = intake.planPullRequest(Object.assign({}, base, { hidden: true }));
  assert.equal(hidden.commitMessage, 'Translation: de (3 strings)\n\n' +
    'Co-authored-by: volunteer-1 <1234567+volunteer-1@users.noreply.github.com>\nTranslator-Listing: hidden\n');
  assert.match(hidden.prBody, /not to be listed/);
  const shown = intake.planPullRequest(base);
  assert.doesNotMatch(shown.commitMessage, /Translator-Listing/);
  assert.doesNotMatch(shown.prBody, /not to be listed/);
});

test('failure comment: first 30 problems with key names and lines, escaped', () => {
  const values = {};
  Object.keys(EN).slice(1, 41).forEach((k) => { values[k] = 'x {bogus}'; });
  values['[evil](https://evil.example) @someone'] = 'x';
  const r = intake.checkSubmission('de', Buffer.from(json(values)), EN);
  assert.ok(r.errors.length > 30);
  const c = intake.failureComment(r.errors);
  const bullets = c.split('\n').filter((l) => l.startsWith('- '));
  assert.equal(bullets.length, 31);
  assert.match(bullets[30], /and \d+ more/);
  assert.match(bullets[0], /^- line 2: \[icu\.extra\] "About\.Tagline"/);
  assert.ok(c.indexOf(Object.keys(EN)[1]) >= 0, 'key name shown');
  const only = intake.failureComment(r.errors.filter((p) => p.rule === 'key.unknown'));
  assert.doesNotMatch(only, /\]\(https/);
  assert.doesNotMatch(only, /(^|[^\\])@someone/);
});

test('success and unchanged comments', () => {
  const result = intake.checkSubmission('de', Buffer.from(GOOD), EN);
  const plan = intake.planPullRequest({ code: 'de', login: USER.login, userId: USER.id, issueNumber: 1, result, en: EN });
  assert.match(intake.successComment(plan, 'https://github.com/o/r/pull/5', false), /pull\/5\. The repo owner/);
  assert.match(intake.successComment(plan, 'https://github.com/o/r/pull/5', true), /\(updated\)/);
  assert.match(intake.unchangedComment('de'), /identical/);
});
