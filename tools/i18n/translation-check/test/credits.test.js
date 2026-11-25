'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const credits = require('../credits');

const DIR = 'src/Kopru.Core/Localization/strings';
const OWNER = 'Owner-Login';
const BOT = { login: 'github-actions[bot]', type: 'Bot' };

function user(login) { return { login, type: 'User' }; }
function coAuthor(login, id) { return 'Co-authored-by: ' + login + ' <' + (id || 1000) + '+' + login + '@users.noreply.github.com>'; }
function botCommit(sha, date, lang, login, extra) {
  return { sha, author: BOT, date, languages: [lang], message: 'Translation: ' + lang + ' (5 strings)\n\n' + coAuthor(login) + '\n' + (extra || '') };
}
function build(commits, extra) {
  return credits.buildTranslators(Object.assign({ commits, owner: OWNER, hidden: '', generated: '2026-11-25T10:00:00.123Z' }, extra || {}));
}
function logins(data, lang) { return (data.languages[lang] || []).map((p) => p.login); }

/* ---------- small parsers ---------- */

test('languageOfPath: language files only, canonical, never en/tr/qps/qtg', () => {
  assert.equal(credits.languageOfPath(DIR + '/de.json'), 'de');
  assert.equal(credits.languageOfPath(DIR + '/pt-BR.json'), 'pt-BR');
  assert.equal(credits.languageOfPath(DIR + '/zh-Hans.json'), 'zh-Hans');
  for (const code of ['en', 'tr', 'qps', 'qps-ploc', 'qtg']) assert.equal(credits.languageOfPath(DIR + '/' + code + '.json'), null, code);
  assert.equal(credits.languageOfPath(DIR + '/de.txt'), null);
  assert.equal(credits.languageOfPath(DIR + '/sub/de.json'), null);
  assert.equal(credits.languageOfPath('other/de.json'), null);
  assert.equal(credits.languageOfPath(DIR + '/not a code.json'), null);
});

test('parseCoAuthors: login from the noreply address, with or without the id; other addresses ignored', () => {
  const msg = 'Translation: de (3 strings)\n\n' +
    'Co-authored-by: Anna Example <123+anna@users.noreply.github.com>\n' +
    'co-authored-by: lukas <lukas@users.noreply.github.com>\r\n' +
    'Co-authored-by: Someone <someone@example.com>\n' +
    'Co-authored-by: bot <49699333+dependabot[bot]@users.noreply.github.com>\n';
  assert.deepEqual(credits.parseCoAuthors(msg), [
    { login: 'anna', id: 123 }, { login: 'lukas', id: null }, { login: 'dependabot[bot]', id: 49699333 }
  ]);
  assert.deepEqual(credits.parseCoAuthors('no trailers'), []);
  assert.deepEqual(credits.parseCoAuthors('Co-authored-by: x <1+bad login@users.noreply.github.com>'), []);
});

test('hasHiddenTrailer: a line of its own, case of the key ignored', () => {
  assert.equal(credits.hasHiddenTrailer('T\n\n' + coAuthor('anna') + '\nTranslator-Listing: hidden\n'), true);
  assert.equal(credits.hasHiddenTrailer('T\r\n\r\ntranslator-listing:  Hidden\r\n'), true);
  assert.equal(credits.hasHiddenTrailer('T\n\nTranslator-Listing: shown\n'), false);
  assert.equal(credits.hasHiddenTrailer('Mentions Translator-Listing: hidden inline'), false);
});

test('parseHiddenList: one login per line, # comments, @ and case ignored', () => {
  const set = credits.parseHiddenList('# header\n\n@Anna\nlukas   # asked in #12\r\n  # indented comment\n');
  assert.deepEqual(Array.from(set).sort(), ['anna', 'lukas']);
  assert.equal(credits.parseHiddenList('').size, 0);
  assert.equal(credits.parseHiddenList(null).size, 0);
});

test('fromApiCommit: author login and type, author date, full message', () => {
  const c = credits.fromApiCommit({
    sha: 'abc', author: { login: 'anna', type: 'User', id: 5 },
    commit: { message: 'M\n\nbody', author: { date: '2026-12-01T10:00:00Z' }, committer: { date: '2026-12-02T10:00:00Z' } }
  }, ['de']);
  assert.deepEqual(c, { sha: 'abc', author: { login: 'anna', type: 'User' }, date: '2026-12-01T10:00:00Z', message: 'M\n\nbody', languages: ['de'] });
  // An e-mail that is not linked to an account has no author login.
  assert.equal(credits.fromApiCommit({ sha: 'x', author: null, commit: { message: '', author: { date: '2026-12-01T00:00:00Z' } } }, ['de']).author, null);
});

/* ---------- the generator ---------- */

test('format: generated time in whole seconds, people with commits, first and last day', () => {
  const data = build([botCommit('a1', '2026-12-01T10:00:00Z', 'de', 'anna'), botCommit('a2', '2026-12-20T23:30:00+02:00', 'de', 'anna')]);
  assert.deepEqual(data, {
    generated: '2026-11-25T10:00:00Z',
    languages: { de: [{ login: 'anna', commits: 2, first: '2026-12-01', last: '2026-12-20' }] }
  });
});

test('bot commits credit their co-author, never the bot', () => {
  const data = build([botCommit('a1', '2026-12-01T10:00:00Z', 'fr', 'claire')]);
  assert.deepEqual(logins(data, 'fr'), ['claire']);
});

test('human commits credit the author and noreply co-authors', () => {
  const data = build([{ sha: 'h1', author: user('anna'), date: '2026-12-01T10:00:00Z', languages: ['de'],
    message: 'German tweaks\n\n' + coAuthor('lukas') + '\nCo-authored-by: X <x@example.com>\n' }]);
  assert.deepEqual(logins(data, 'de').sort(), ['anna', 'lukas']);
});

test('the owner is never listed, as author or co-author, whatever the case', () => {
  const data = build([
    { sha: 'o1', author: user('owner-login'), date: '2026-12-05T10:00:00Z', languages: ['de', 'fr', 'ja'], message: 'Release 0.2: strings' },
    botCommit('b1', '2026-12-01T10:00:00Z', 'de', 'Owner-Login'),
    { sha: 'h1', author: user('anna'), date: '2026-12-02T10:00:00Z', languages: ['de'], message: 'x\n\n' + coAuthor('OWNER-LOGIN') }
  ]);
  assert.deepEqual(data.languages, { de: [{ login: 'anna', commits: 1, first: '2026-12-02', last: '2026-12-02' }] });
});

test('bots are never listed: [bot] logins, type Bot, github-actions, bot co-authors', () => {
  const data = build([
    { sha: 'd1', author: { login: 'dependabot[bot]', type: 'Bot' }, date: '2026-12-01T10:00:00Z', languages: ['de'], message: 'x' },
    { sha: 'd2', author: { login: 'renovate', type: 'Bot' }, date: '2026-12-01T10:00:00Z', languages: ['de'], message: 'x' },
    { sha: 'd3', author: user('github-actions'), date: '2026-12-01T10:00:00Z', languages: ['de'], message: 'x' },
    { sha: 'd4', author: BOT, date: '2026-12-01T10:00:00Z', languages: ['de'],
      message: 'x\n\nCo-authored-by: bot <49699333+dependabot[bot]@users.noreply.github.com>\n' + coAuthor('anna') }
  ]);
  assert.deepEqual(logins(data, 'de'), ['anna']);
});

test('en, tr, qps and qtg are never listed', () => {
  const data = build([{ sha: 'h1', author: user('anna'), date: '2026-12-01T10:00:00Z', languages: ['en', 'tr', 'qps-ploc', 'qtg', 'de'], message: 'x' }]);
  assert.deepEqual(Object.keys(data.languages), ['de']);
});

test('hidden trailer on a bot commit hides its co-author everywhere', () => {
  const data = build([
    botCommit('b1', '2026-12-01T10:00:00Z', 'de', 'anna', 'Translator-Listing: hidden\n'),
    botCommit('b2', '2026-12-10T10:00:00Z', 'de', 'anna'),
    botCommit('b3', '2026-12-11T10:00:00Z', 'fr', 'anna'),
    botCommit('b4', '2026-12-12T10:00:00Z', 'de', 'lukas')
  ]);
  assert.deepEqual(data.languages, { de: [{ login: 'lukas', commits: 1, first: '2026-12-12', last: '2026-12-12' }] });
});

test('hidden trailer on a human commit hides its author (and its co-authors)', () => {
  const data = build([
    { sha: 'h1', author: user('anna'), date: '2026-12-01T10:00:00Z', languages: ['de'], message: 'x\n\n' + coAuthor('lukas') + '\nTranslator-Listing: hidden' },
    { sha: 'h2', author: user('mia'), date: '2026-12-02T10:00:00Z', languages: ['de'], message: 'x' }
  ]);
  assert.deepEqual(logins(data, 'de'), ['mia']);
});

test('hidden trailer survives a squash merge authored by the owner: the co-author stays hidden', () => {
  const data = build([{ sha: 's1', author: user(OWNER), date: '2026-12-01T10:00:00Z', languages: ['de'],
    message: 'Translation: de by @anna (#9)\n\n' + coAuthor('anna') + '\nTranslator-Listing: hidden\n' }]);
  assert.deepEqual(data.languages, {});
});

test('the hidden list removes people (text or a list of logins, any case, leading @)', () => {
  const commits = [botCommit('b1', '2026-12-01T10:00:00Z', 'de', 'Anna'), botCommit('b2', '2026-12-02T10:00:00Z', 'de', 'lukas')];
  assert.deepEqual(logins(build(commits, { hidden: '# hidden translators\nanna\n' }), 'de'), ['lukas']);
  assert.deepEqual(logins(build(commits, { hidden: ['@ANNA'] }), 'de'), ['lukas']);
  assert.deepEqual(build(commits, { hidden: 'anna\nlukas' }).languages, {});
});

test('duplicates merge: one commit seen for several files, the same person twice in one commit, login case', () => {
  const twice = { sha: 'h1', author: user('Anna'), date: '2026-12-01T10:00:00Z', languages: ['de'], message: 'x\n\n' + coAuthor('anna') };
  const data = build([
    twice,
    Object.assign({}, twice, { languages: ['fr'] }),
    Object.assign({}, twice, { languages: ['de'] }),
    botCommit('b2', '2026-12-03T10:00:00Z', 'de', 'ANNA')
  ]);
  assert.deepEqual(data.languages.de, [{ login: 'ANNA', commits: 2, first: '2026-12-01', last: '2026-12-03' }]);
  assert.deepEqual(data.languages.fr, [{ login: 'Anna', commits: 1, first: '2026-12-01', last: '2026-12-01' }]);
});

test('sorting: newest last contribution first, then more commits, then login; languages by code', () => {
  const data = build([
    botCommit('1', '2026-12-01T10:00:00Z', 'de', 'old'),
    botCommit('2', '2026-12-20T10:00:00Z', 'de', 'newest'),
    botCommit('3', '2026-12-10T10:00:00Z', 'de', 'zed'),
    botCommit('4', '2026-12-10T10:00:00Z', 'de', 'amy'),
    botCommit('5', '2026-12-10T10:00:00Z', 'de', 'busy'),
    botCommit('6', '2026-12-09T10:00:00Z', 'de', 'busy'),
    botCommit('7', '2026-12-01T10:00:00Z', 'pt-BR', 'joao'),
    botCommit('8', '2026-12-01T10:00:00Z', 'fr', 'claire')
  ]);
  assert.deepEqual(logins(data, 'de'), ['newest', 'busy', 'amy', 'zed', 'old']);
  assert.deepEqual(Object.keys(data.languages), ['de', 'fr', 'pt-BR']);
});

test('languages where nobody is listed are left out; commits without a valid date are skipped', () => {
  const data = build([
    { sha: 'o1', author: user(OWNER), date: '2026-12-01T10:00:00Z', languages: ['ja'], message: 'x' },
    { sha: 'n1', author: user('anna'), date: 'not a date', languages: ['ko'], message: 'x' }
  ]);
  assert.deepEqual(data.languages, {});
});

test('serialise and sameLanguages', () => {
  const a = build([botCommit('1', '2026-12-01T10:00:00Z', 'de', 'anna')]);
  const b = Object.assign({}, a, { generated: '2027-01-01T00:00:00Z' });
  assert.equal(credits.sameLanguages(a, b), true);
  assert.equal(credits.sameLanguages(a, build([])), false);
  assert.equal(credits.sameLanguages(a, null), false);
  assert.ok(credits.serialise(a).endsWith('}\n'));
  assert.deepEqual(JSON.parse(credits.serialise(a)), a);
});
