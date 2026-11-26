'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const actions = require('../actions');

const DIR = 'src/Kopru.Core/Localization/strings';
const BOT = { login: 'github-actions[bot]', type: 'Bot' };

function httpError(status) { return Object.assign(new Error('HTTP ' + status), { status }); }

function fakeCore() {
  const log = { info: [], summary: '' };
  return {
    log,
    info: (m) => log.info.push(m),
    notice: () => {}, warning: () => {}, error: () => {}, setFailed: (m) => { throw new Error(m); },
    summary: { addRaw(s) { log.summary += s; return this; }, async write() { return this; } }
  };
}

// A commit of the intake today: the volunteer is the author (noreply address), the bot the committer.
function volunteerCommit(sha, date, login, id, linked) {
  return {
    sha,
    author: linked === false ? null : { login, type: 'User' },
    committer: BOT,
    commit: {
      message: 'Translation: de (5 strings)\n',
      author: { name: login, email: id + '+' + login + '@users.noreply.github.com', date },
      committer: { name: 'github-actions[bot]', email: '41898282+github-actions[bot]@users.noreply.github.com', date }
    }
  };
}

// A legacy intake commit (bot author, the volunteer as co-author) by default. Its co-author counts only on the legacy
// SHA allowlist (credits.LEGACY_INTAKE_SHAS), so LEGACY is that SHA.
const LEGACY = '0be59dfb6cf5d812b771b4fddc12458a73547dcd';
function apiCommit(sha, date, login, message, author, committer) {
  return { sha, author: author === undefined ? BOT : author, committer: committer || null,
    commit: { message: message || 'Translation: de (5 strings)\n\nCo-authored-by: ' + login + ' <1+' + login + '@users.noreply.github.com>\n', author: { date } } };
}

// state: { history: { path: [api commits] }, hidden?, data? (existing branch: { parents, entries, text }) }
function fakeGithub(state) {
  const calls = [];
  const rec = (name, p) => calls.push({ name, p });
  const dirEntries = Object.keys(state.history).map((p) => ({ type: 'file', path: p, name: p.split('/').pop() }));
  const gh = {
    calls,
    paginate: async (fn, params) => {
      rec('paginate', params);
      const all = (await fn(params)).data;
      return all;
    },
    rest: {
      repos: {
        get: async () => ({ data: { default_branch: 'main' } }),
        getContent: async (p) => {
          rec('repos.getContent', p);
          if (p.path === DIR) return { data: dirEntries.concat([{ type: 'dir', path: DIR + '/sub', name: 'sub' }]) };
          if (p.path === 'tools/i18n/translators-hidden.txt') {
            if (state.hidden == null) throw httpError(404);
            return { data: state.hidden };
          }
          if (p.path === 'translators.json' && state.data) return { data: state.data.text };
          throw httpError(404);
        },
        listCommits: async (p) => { rec('repos.listCommits', p); return { data: state.history[p.path] || [] }; }
      },
      git: {
        getRef: async (p) => {
          rec('git.getRef', p);
          if (p.ref === 'heads/main') return { data: { object: { sha: 'main-sha' } } };
          if (p.ref === 'heads/translation-data' && state.data) return { data: { object: { sha: 'data-sha' } } };
          throw httpError(404);
        },
        getCommit: async () => ({ data: { tree: { sha: 'data-tree' }, parents: state.data.parents || [] } }),
        getTree: async () => ({ data: { tree: state.data.entries } }),
        createTree: async (p) => { rec('git.createTree', p); return { data: { sha: 'new-tree' } }; },
        createCommit: async (p) => { rec('git.createCommit', p); return { data: { sha: 'new-commit' } }; },
        updateRef: async (p) => { rec('git.updateRef', p); return { data: {} }; },
        createRef: async (p) => { rec('git.createRef', p); return { data: {} }; }
      }
    }
  };
  gh.called = (name) => calls.filter((c) => c.name === name);
  return gh;
}

const ctx = { repo: { owner: 'owner', repo: 'repo' }, payload: { repository: { default_branch: 'main' } } };
const NOW = '2026-11-25T10:00:00Z';

function history() {
  return {
    [DIR + '/en.json']: [apiCommit('e1', '2026-12-01T10:00:00Z', 'x', 'Release', { login: 'owner', type: 'User' })],
    [DIR + '/tr.json']: [apiCommit('t1', '2026-12-01T10:00:00Z', 'x', 'Release', { login: 'owner', type: 'User' })],
    [DIR + '/de.json']: [
      apiCommit(LEGACY, '2026-12-20T10:00:00Z', 'anna'),
      apiCommit('r1', '2026-12-05T10:00:00Z', 'x', 'Release strings', { login: 'owner', type: 'User' }),
      volunteerCommit('d1', '2026-12-01T10:00:00Z', 'lukas', 3131)
    ],
    // A hidden send of the intake today: bot author and committer, the trailer, nobody credited.
    [DIR + '/fr.json']: [apiCommit('f1', '2026-12-03T10:00:00Z', 'claire', 'Translation: fr (4 strings)\n\nTranslator-Listing: hidden\n', BOT, BOT)]
  };
}

test('translators data: first run creates the branch with one parentless commit holding only translators.json', async () => {
  const github = fakeGithub({ history: history() });
  const out = await actions.translatorsData({ github, context: ctx, core: fakeCore(), now: NOW });
  assert.equal(out, 'created');
  // Only the volunteer language files are listed, on main, with pagination.
  const listed = github.called('repos.listCommits').map((c) => c.p.path).sort();
  assert.deepEqual(listed, [DIR + '/de.json', DIR + '/fr.json']);
  for (const c of github.called('repos.listCommits')) { assert.equal(c.p.sha, 'main-sha'); assert.equal(c.p.per_page, 100); }
  assert.equal(github.called('paginate').length, 2);
  const tree = github.called('git.createTree')[0].p;
  assert.equal(tree.base_tree, undefined);
  assert.deepEqual(tree.tree.map((t) => [t.path, t.mode, t.type]), [['translators.json', '100644', 'blob']]);
  assert.deepEqual(JSON.parse(tree.tree[0].content), {
    generated: NOW,
    languages: { de: [
      { login: 'anna', commits: 1, first: '2026-12-20', last: '2026-12-20' },
      { login: 'lukas', commits: 1, first: '2026-12-01', last: '2026-12-01' }
    ] }
  });
  assert.deepEqual(github.called('git.createCommit')[0].p.parents, []);
  assert.deepEqual(github.called('git.createRef')[0].p, { owner: 'owner', repo: 'repo', ref: 'refs/heads/translation-data', sha: 'new-commit' });
  assert.equal(github.called('git.updateRef').length, 0);
});

test('translators data: the hidden list is read from main', async () => {
  const github = fakeGithub({ history: history(), hidden: '# hidden\nAnna\n' });
  await actions.translatorsData({ github, context: ctx, core: fakeCore(), now: NOW });
  const hiddenRead = github.called('repos.getContent').find((c) => c.p.path === 'tools/i18n/translators-hidden.txt');
  assert.equal(hiddenRead.p.ref, 'main-sha');
  const data = JSON.parse(github.called('git.createTree')[0].p.tree[0].content);
  assert.deepEqual(data.languages.de.map((p) => p.login), ['lukas']);
});

test('translators data: a changed list force-updates the existing branch', async () => {
  const old = { generated: '2026-11-01T00:00:00Z', languages: { de: [{ login: 'lukas', commits: 1, first: '2026-12-01', last: '2026-12-01' }] } };
  const github = fakeGithub({ history: history(), data: { entries: [{ path: 'translators.json', type: 'blob' }], text: JSON.stringify(old) } });
  assert.equal(await actions.translatorsData({ github, context: ctx, core: fakeCore(), now: NOW }), 'updated');
  const upd = github.called('git.updateRef')[0].p;
  assert.deepEqual([upd.ref, upd.sha, upd.force], ['heads/translation-data', 'new-commit', true]);
  assert.equal(github.called('git.createRef').length, 0);
});

test('translators data: the same people leave the branch alone; a branch with anything else is rewritten', async () => {
  const first = fakeGithub({ history: history() });
  await actions.translatorsData({ github: first, context: ctx, core: fakeCore(), now: NOW });
  const text = first.called('git.createTree')[0].p.tree[0].content;

  const same = fakeGithub({ history: history(), data: { entries: [{ path: 'translators.json', type: 'blob' }], text } });
  assert.equal(await actions.translatorsData({ github: same, context: ctx, core: fakeCore(), now: '2027-01-01T00:00:00Z' }), 'unchanged');
  assert.equal(same.called('git.createCommit').length, 0);

  const extraFile = fakeGithub({ history: history(), data: { entries: [{ path: 'translators.json', type: 'blob' }, { path: 'x.txt', type: 'blob' }], text } });
  assert.equal(await actions.translatorsData({ github: extraFile, context: ctx, core: fakeCore(), now: NOW }), 'updated');

  const withParent = fakeGithub({ history: history(), data: { parents: [{ sha: 'p' }], entries: [{ path: 'translators.json', type: 'blob' }], text } });
  assert.equal(await actions.translatorsData({ github: withParent, context: ctx, core: fakeCore(), now: NOW }), 'updated');

  const broken = fakeGithub({ history: history(), data: { entries: [{ path: 'translators.json', type: 'blob' }], text: '{not json' } });
  assert.equal(await actions.translatorsData({ github: broken, context: ctx, core: fakeCore(), now: NOW }), 'updated');
});

test('translators data: volunteer-authored intake commits are credited next to legacy co-author commits', async () => {
  const h = history();
  h[DIR + '/de.json'] = [
    volunteerCommit('n2', '2026-12-22T10:00:00Z', 'mia', 5151, false),
    volunteerCommit('n1', '2026-12-21T10:00:00Z', 'anna', 4242)
  ].concat(h[DIR + '/de.json']);
  h[DIR + '/fr.json'] = h[DIR + '/fr.json'].concat([apiCommit('f0', '2026-12-02T10:00:00Z', 'x', 'Translation: fr (3 strings)\n\nTranslator-Listing: hidden\n')]);
  const github = fakeGithub({ history: h });
  assert.equal(await actions.translatorsData({ github, context: ctx, core: fakeCore(), now: NOW }), 'created');
  const data = JSON.parse(github.called('git.createTree')[0].p.tree[0].content);
  assert.deepEqual(data.languages.de, [
    { login: 'mia', commits: 1, first: '2026-12-22', last: '2026-12-22' },
    { login: 'anna', commits: 2, first: '2026-12-20', last: '2026-12-21' },
    { login: 'lukas', commits: 1, first: '2026-12-01', last: '2026-12-01' }
  ]);
  assert.equal(data.languages.fr, undefined, 'a hidden bot-authored commit credits nobody');
});
