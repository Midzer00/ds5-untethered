'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const actions = require('../actions');
const { EN_TEXT, readStrings, json, pageIssue, formIssue, fakeFetch } = require('./helpers');

const DIR = 'src/Kopru.Core/Localization/strings';
const USER = { login: 'volunteer-1', id: 1234567, type: 'User' };
const GOOD = json({ '@name': 'Deutsch', 'About.Version': 'Version {version}' });

function httpError(status) { return Object.assign(new Error('HTTP ' + status), { status }); }

function fakeCore() {
  const log = { info: [], notice: [], warning: [], error: [], failed: [], summary: '' };
  return {
    log,
    info: (m) => log.info.push(m),
    notice: (m) => log.notice.push(m),
    warning: (m, p) => log.warning.push({ m, p }),
    error: (m, p) => log.error.push({ m, p }),
    setFailed: (m) => log.failed.push(m),
    summary: { addRaw(s) { log.summary += s; return this; }, async write() { return this; } }
  };
}

function fakeGithub(state) {
  const calls = [];
  const rec = (name, p) => calls.push({ name, p });
  const gh = {
    calls,
    paginate: async (fn, params) => (await fn(params)).data,
    rest: {
      issues: {
        get: async (p) => { rec('issues.get', p); if (state.issueStatus) throw httpError(state.issueStatus); return { data: state.issue }; },
        listComments: async (p) => { rec('issues.listComments', p); return { data: state.comments || [] }; },
        createComment: async (p) => { rec('issues.createComment', p); if (state.commentStatus) throw httpError(state.commentStatus); return { data: {} }; },
        update: async (p) => { rec('issues.update', p); if (state.closeStatus && state.closeStatus.length) throw httpError(state.closeStatus.shift()); return { data: {} }; }
      },
      repos: {
        get: async () => ({ data: { default_branch: 'main' } }),
        getContent: async (p) => {
          rec('repos.getContent', p);
          if (!Object.prototype.hasOwnProperty.call(state.files, p.path)) throw httpError(404);
          return { data: state.files[p.path] };
        }
      },
      git: {
        getRef: async (p) => { rec('git.getRef', p); return { data: { object: { sha: 'main-sha' } } }; },
        getCommit: async () => ({ data: { tree: { sha: 'main-tree' } } }),
        createTree: async (p) => { rec('git.createTree', p); return { data: { sha: 'new-tree' } }; },
        createCommit: async (p) => { rec('git.createCommit', p); return { data: { sha: 'new-commit' } }; },
        updateRef: async (p) => { rec('git.updateRef', p); if (!state.branchExists) throw httpError(422); return { data: {} }; },
        createRef: async (p) => { rec('git.createRef', p); return { data: {} }; }
      },
      pulls: {
        list: async (p) => { rec('pulls.list', p); return { data: state.openPulls || [] }; },
        create: async (p) => { rec('pulls.create', p); return { data: { number: 7, html_url: 'https://github.com/owner/repo/pull/7' } }; },
        update: async (p) => { rec('pulls.update', p); return { data: { number: p.pull_number, html_url: 'https://github.com/owner/repo/pull/' + p.pull_number } }; },
        listFiles: async (p) => { rec('pulls.listFiles', p); return { data: state.prFiles || [] }; }
      }
    }
  };
  gh.called = (name) => calls.filter((c) => c.name === name);
  return gh;
}

function issueState(p, extra) {
  return Object.assign({
    issue: Object.assign({ number: 12, title: p.title, body: p.body, user: USER, state: 'open', locked: false, repository_url: 'https://api.github.com/repos/owner/repo' }, extra || {}),
    files: { [DIR + '/en.json']: EN_TEXT }
  });
}

function ctx(eventName, extra) {
  return {
    eventName,
    repo: { owner: 'owner', repo: 'repo' },
    payload: Object.assign({ action: 'opened', issue: { number: 12 }, sender: USER, repository: { default_branch: 'main' } }, extra || {})
  };
}

/* ---------- intake ---------- */

test('intake: a page issue opens a PR with the co-authored commit, comments and closes', async () => {
  const state = issueState(pageIssue('de', GOOD));
  const github = fakeGithub(state), core = fakeCore();
  const out = await actions.intake({ github, context: ctx('issues'), core });
  assert.equal(out, 'created');
  const tree = github.called('git.createTree')[0].p;
  assert.equal(tree.base_tree, 'main-tree');
  assert.deepEqual(tree.tree.map((t) => t.path), [DIR + '/de.json']);
  assert.equal(tree.tree[0].content, GOOD);
  const commit = github.called('git.createCommit')[0].p;
  assert.deepEqual(commit.parents, ['main-sha']);
  assert.match(commit.message, /^Translation: de \(2 strings\)\n\nCo-authored-by: volunteer-1 <1234567\+volunteer-1@users\.noreply\.github\.com>\n$/);
  assert.equal(github.called('git.createRef')[0].p.ref, 'refs/heads/translate/de-volunteer-1');
  const pr = github.called('pulls.create')[0].p;
  assert.equal(pr.title, 'Translation: de by @volunteer-1');
  assert.equal(pr.head, 'translate/de-volunteer-1');
  assert.equal(pr.base, 'main');
  assert.match(github.called('issues.createComment')[0].p.body, /pull\/7/);
  assert.deepEqual(github.called('issues.update')[0].p.state, 'closed');
});

test('intake: a later send resets the branch and updates the open PR', async () => {
  const state = Object.assign(issueState(pageIssue('de', GOOD)), { branchExists: true, openPulls: [{ number: 5 }] });
  const github = fakeGithub(state);
  assert.equal(await actions.intake({ github, context: ctx('issues'), core: fakeCore() }), 'updated');
  const upd = github.called('git.updateRef')[0].p;
  assert.deepEqual([upd.ref, upd.sha, upd.force], ['heads/translate/de-volunteer-1', 'new-commit', true]);
  assert.equal(github.called('pulls.create').length, 0);
  assert.equal(github.called('pulls.update')[0].p.pull_number, 5);
  assert.equal(github.called('pulls.list')[0].p.head, 'owner:translate/de-volunteer-1');
});

test('intake: a failing file gets one comment and stays open', async () => {
  const state = issueState(pageIssue('de', json({ 'About.Version': 'Version' })));
  const github = fakeGithub(state), core = fakeCore();
  assert.equal(await actions.intake({ github, context: ctx('issues'), core }), 'failed');
  assert.match(github.called('issues.createComment')[0].p.body, /icu\.missing/);
  assert.equal(github.called('issues.update').length, 0);
  assert.equal(github.called('git.createCommit').length, 0);
  assert.equal(core.log.failed.length, 0, 'a volunteer error is not a workflow failure');
});

test('intake: tr is rejected with a comment', async () => {
  const github = fakeGithub(issueState(pageIssue('tr', GOOD)));
  assert.equal(await actions.intake({ github, context: ctx('issues'), core: fakeCore() }), 'failed');
  assert.match(github.called('issues.createComment')[0].p.body, /code\.maintained/);
});

test('intake: missing parts wait silently', async () => {
  const p = pageIssue('de', GOOD + ' '.repeat(3000), { maxPartChars: 100 });
  const state = issueState(p);
  state.comments = p.comments.slice(0, -1).map((body, i) => ({ id: i, body, user: USER }));
  const github = fakeGithub(state);
  assert.equal(await actions.intake({ github, context: ctx('issues'), core: fakeCore() }), 'waiting');
  assert.equal(github.called('issues.createComment').length, 0);
});

test('intake: the last part arriving as a comment completes the send', async () => {
  const p = pageIssue('de', GOOD + ' '.repeat(3000), { maxPartChars: 100 });
  const state = issueState(p);
  state.comments = p.comments.map((body, i) => ({ id: i, body, user: USER }));
  const last = state.comments[state.comments.length - 1];
  const github = fakeGithub(state);
  const out = await actions.intake({ github, context: ctx('issue_comment', { action: 'created', comment: last }), core: fakeCore() });
  assert.equal(out, 'created');
});

test('intake: a form issue downloads the attachment', async () => {
  const url = 'https://github.com/user-attachments/files/55/de.json';
  const github = fakeGithub(issueState(formIssue('de', '[de.json](' + url + ')')));
  const fetchImpl = fakeFetch({ [url]: { body: GOOD } });
  assert.equal(await actions.intake({ github, context: ctx('issues'), core: fakeCore(), fetchImpl }), 'created');
  assert.equal(fetchImpl.calls.length, 1);
});

test('intake: opting out (page line or form checkbox) puts the listing trailer on the bot commit', async () => {
  const page = fakeGithub(issueState(pageIssue('de', GOOD, { listing: 'hidden' })));
  assert.equal(await actions.intake({ github: page, context: ctx('issues'), core: fakeCore() }), 'created');
  assert.match(page.called('git.createCommit')[0].p.message,
    /\nCo-authored-by: volunteer-1 <1234567\+volunteer-1@users\.noreply\.github\.com>\nTranslator-Listing: hidden\n$/);

  const url = 'https://github.com/user-attachments/files/56/de.json';
  const form = fakeGithub(issueState(formIssue('de', '[de.json](' + url + ')', null, true)));
  const fetchImpl = fakeFetch({ [url]: { body: GOOD } });
  assert.equal(await actions.intake({ github: form, context: ctx('issues'), core: fakeCore(), fetchImpl }), 'created');
  assert.match(form.called('git.createCommit')[0].p.message, /\nTranslator-Listing: hidden\n$/);

  const shown = fakeGithub(issueState(formIssue('de', '[de.json](' + url + ')', null, false)));
  await actions.intake({ github: shown, context: ctx('issues'), core: fakeCore(), fetchImpl: fakeFetch({ [url]: { body: GOOD } }) });
  assert.doesNotMatch(shown.called('git.createCommit')[0].p.message, /Translator-Listing/);
});

test('intake: an identical file closes without a PR', async () => {
  const state = issueState(pageIssue('de', GOOD));
  state.files[DIR + '/de.json'] = GOOD;
  const github = fakeGithub(state);
  assert.equal(await actions.intake({ github, context: ctx('issues'), core: fakeCore() }), 'unchanged');
  assert.equal(github.called('git.createCommit').length, 0);
  assert.equal(github.called('issues.update').length, 1);
});

test('intake: a deleted issue is handled quietly', async () => {
  for (const status of [404, 410]) {
    const state = issueState(pageIssue('de', GOOD));
    state.issueStatus = status;
    const github = fakeGithub(state), core = fakeCore();
    assert.equal(await actions.intake({ github, context: ctx('issues'), core }), 'gone');
    assert.equal(github.called('issues.createComment').length, 0);
  }
});

test('intake: a transferred issue is left alone', async () => {
  const github = fakeGithub(issueState(pageIssue('de', GOOD), { repository_url: 'https://api.github.com/repos/other/place' }));
  assert.equal(await actions.intake({ github, context: ctx('issues'), core: fakeCore() }), 'gone');
});

test('intake: a locked or closed issue is ignored', async () => {
  for (const extra of [{ locked: true }, { state: 'closed' }]) {
    const github = fakeGithub(issueState(pageIssue('de', GOOD), extra));
    assert.equal(await actions.intake({ github, context: ctx('issues'), core: fakeCore() }), 'ignored');
    assert.equal(github.called('issues.createComment').length, 0);
  }
});

test('intake: an issue locked while the run works only leaves a warning', async () => {
  const state = issueState(pageIssue('de', json({ 'About.Version': 'Version' })));
  state.commentStatus = 403;
  const core = fakeCore();
  assert.equal(await actions.intake({ github: fakeGithub(state), context: ctx('issues'), core }), 'failed');
  assert.match(core.log.warning.map((w) => w.m).join('\n'), /locked or deleted/);
});

test('intake: bots and edits or comments by others are ignored', async () => {
  const p = pageIssue('de', GOOD);
  const cases = [
    ctx('issues', { sender: { login: 'helper[bot]', type: 'Bot' } }),
    ctx('issues', { action: 'edited', sender: { login: 'someone-else', type: 'User' } }),
    ctx('issue_comment', { action: 'created', sender: { login: 'someone-else', type: 'User' }, comment: { body: '```ds5-translate', user: { login: 'someone-else' } } }),
    ctx('issue_comment', { action: 'created', comment: { body: 'thanks', user: USER } })
  ];
  for (const c of cases) {
    const github = fakeGithub(issueState(p));
    assert.equal(await actions.intake({ github, context: c, core: fakeCore() }), 'ignored');
    assert.equal(github.called('issues.createComment').length, 0);
  }
  const botIssue = fakeGithub(issueState(p, { user: { login: 'helper[bot]', id: 1, type: 'Bot' } }));
  assert.equal(await actions.intake({ github: botIssue, context: ctx('issues', { sender: { login: 'helper[bot]', type: 'User' } }), core: fakeCore() }), 'ignored');
});

test('intake: unmarked issues and pull requests are ignored', async () => {
  const github = fakeGithub(issueState({ title: 'Bug', body: 'it crashes' }));
  assert.equal(await actions.intake({ github, context: ctx('issues'), core: fakeCore() }), 'ignored');
  const prCtx = ctx('issue_comment', { issue: { number: 12, pull_request: {} } });
  assert.equal(await actions.intake({ github, context: prCtx, core: fakeCore() }), 'ignored');
});

/* ---------- validate ---------- */

function workspace(files) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'translation-validate-'));
  const baseDir = path.join(root, 'base', DIR);
  fs.mkdirSync(baseDir, { recursive: true });
  fs.writeFileSync(path.join(baseDir, 'en.json'), EN_TEXT);
  const prDir = path.join(root, 'pr', DIR);
  fs.mkdirSync(prDir, { recursive: true });
  for (const [name, text] of Object.entries(files)) fs.writeFileSync(path.join(prDir, name), text);
  return root;
}

function prCtx(login) {
  return { repo: { owner: 'owner', repo: 'repo' }, payload: { pull_request: { number: 3, user: { login } } } };
}

test('validate: a good volunteer file passes with a summary', async () => {
  const ws = workspace({ 'de.json': GOOD });
  const github = fakeGithub({ prFiles: [{ filename: DIR + '/de.json', status: 'added' }, { filename: 'README.md', status: 'modified' }] });
  const core = fakeCore();
  const r = await actions.validate({ github, context: prCtx('volunteer-1'), core, workspace: ws, owner: 'the-owner' });
  assert.equal(r.ok, true);
  assert.equal(core.log.failed.length, 0);
  assert.match(core.log.summary, /Translation check: passed/);
});

test('validate: errors become annotations with file and line, and the job fails', async () => {
  const ws = workspace({ 'de.json': json({ '@name': 'Deutsch', 'About.Version': 'Version' }) });
  const github = fakeGithub({ prFiles: [{ filename: DIR + '/de.json', status: 'modified' }] });
  const core = fakeCore();
  await actions.validate({ github, context: prCtx('volunteer-1'), core, workspace: ws, owner: 'the-owner' });
  assert.equal(core.log.failed.length, 1);
  assert.deepEqual(core.log.error[0].p, { title: 'Translation check: icu.missing', file: DIR + '/de.json', startLine: 3 });
});

test('validate: a non-owner touching tr.json fails, the owner passes', async () => {
  const files = [{ filename: DIR + '/tr.json', status: 'modified' }];
  const ws = workspace({ 'tr.json': readStrings('tr.json') });
  const volunteer = fakeCore();
  await actions.validate({ github: fakeGithub({ prFiles: files }), context: prCtx('volunteer-1'), core: volunteer, workspace: ws, owner: 'The-Owner' });
  assert.equal(volunteer.log.failed.length, 1);
  assert.equal(volunteer.log.error[0].p.title, 'Translation check: owner.only');
  const owner = fakeCore();
  await actions.validate({ github: fakeGithub({ prFiles: files }), context: prCtx('the-owner'), core: owner, workspace: ws, owner: 'The-Owner' });
  assert.equal(owner.log.failed.length, 0, JSON.stringify(owner.log.error));
});

test('validate: symlinked language files are not read', (t) => {
  const ws = workspace({});
  const target = path.join(ws, 'secret.txt');
  fs.writeFileSync(target, 'secret');
  try { fs.symlinkSync(target, path.join(ws, 'pr', DIR, 'de.json')); } catch (e) { t.skip('cannot create symlinks here'); return; }
  assert.deepEqual(actions.readPrFile(path.join(ws, 'pr'), DIR + '/de.json'), { symlink: true });
});

test('validate: files outside the checkout are not read', () => {
  const ws = workspace({ 'de.json': GOOD });
  assert.ok(actions.readPrFile(path.join(ws, 'pr'), DIR + '/de.json').bytes);
  assert.deepEqual(actions.readPrFile(path.join(ws, 'pr'), DIR + '/missing.json'), { missing: true });
});

test('intake: a GitHub 500 on closing the issue is retried, then only warned about; the PR stays', async () => {
  actions.safely.retryDelayMs = 0;
  for (const statuses of [[500], [500, 502]]) {
    const state = issueState(pageIssue('de', GOOD));
    state.closeStatus = statuses.slice();
    const core = fakeCore();
    const github = fakeGithub(state);
    const result = await actions.intake({ github, context: ctx('issues'), core });
    assert.notEqual(result, 'failed');
    assert.equal(github.called('issues.update').length, 2);
    assert.equal(core.log.failed.length, 0);
    if (statuses.length === 2) assert.match(core.log.warning.map((w) => w.m).join(String.fromCharCode(10)), /twice/);
  }
});
