'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const actions = require('../actions');
const intakeLib = require('../intake');
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
        listComments: async (p) => {
          rec('issues.listComments', p);
          const per = p.per_page || 30, page = p.page || 1;
          return { data: (state.comments || []).slice((page - 1) * per, page * per) };
        },
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

// An open pull request of the intake bot on a branch of this repository (or of a fork).
function botPull(number, ref, repo) {
  return { number, head: { ref, repo: { full_name: repo || 'owner/repo' } }, base: { ref: 'main' } };
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

test('intake: a page issue opens a PR with the volunteer as commit author, comments and closes', async () => {
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
  assert.equal(commit.message, 'Translation: de (2 strings)\n');
  assert.deepEqual(commit.author, { name: 'volunteer-1', email: '1234567+volunteer-1@users.noreply.github.com' });
  assert.deepEqual(commit.committer, { name: 'github-actions[bot]', email: '41898282+github-actions[bot]@users.noreply.github.com' });
  assert.equal(github.called('git.createRef')[0].p.ref, 'refs/heads/translate/1234567/de');
  const pr = github.called('pulls.create')[0].p;
  assert.equal(pr.title, 'Translation: de by @volunteer-1');
  assert.equal(pr.head, 'translate/1234567/de');
  assert.equal(pr.base, 'main');
  assert.match(pr.body, /merge commit or Rebase and merge \(not Squash\)/);
  assert.match(github.called('issues.createComment')[0].p.body, /pull\/7/);
  assert.deepEqual(github.called('issues.update')[0].p.state, 'closed');
});

test('intake: a later send resets the branch and updates the open PR', async () => {
  const state = Object.assign(issueState(pageIssue('de', GOOD)), { branchExists: true, openPulls: [botPull(5, 'translate/1234567/de')] });
  const github = fakeGithub(state);
  assert.equal(await actions.intake({ github, context: ctx('issues'), core: fakeCore() }), 'updated');
  const upd = github.called('git.updateRef')[0].p;
  assert.deepEqual([upd.ref, upd.sha, upd.force], ['heads/translate/1234567/de', 'new-commit', true]);
  assert.equal(github.called('pulls.create').length, 0);
  assert.equal(github.called('pulls.update')[0].p.pull_number, 5);
  assert.equal(github.called('pulls.list')[0].p.state, 'open');
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

test('intake: opting out (page line or form checkbox) leaves the bot as author with the listing trailer', async () => {
  const page = fakeGithub(issueState(pageIssue('de', GOOD, { listing: 'hidden' })));
  assert.equal(await actions.intake({ github: page, context: ctx('issues'), core: fakeCore() }), 'created');
  const hiddenCommit = page.called('git.createCommit')[0].p;
  assert.equal(hiddenCommit.message, 'Translation: de (2 strings)\n\nTranslator-Listing: hidden\n');
  assert.equal(hiddenCommit.author, undefined, 'the API makes the bot the author');
  assert.equal(hiddenCommit.committer, undefined);
  assert.equal(page.called('pulls.create')[0].p.title, 'Translation: de');

  const url = 'https://github.com/user-attachments/files/56/de.json';
  const form = fakeGithub(issueState(formIssue('de', '[de.json](' + url + ')', null, true)));
  const fetchImpl = fakeFetch({ [url]: { body: GOOD } });
  assert.equal(await actions.intake({ github: form, context: ctx('issues'), core: fakeCore(), fetchImpl }), 'created');
  assert.match(form.called('git.createCommit')[0].p.message, /\nTranslator-Listing: hidden\n$/);
  assert.equal(form.called('git.createCommit')[0].p.author, undefined);

  const shown = fakeGithub(issueState(formIssue('de', '[de.json](' + url + ')', null, false)));
  await actions.intake({ github: shown, context: ctx('issues'), core: fakeCore(), fetchImpl: fakeFetch({ [url]: { body: GOOD } }) });
  assert.doesNotMatch(shown.called('git.createCommit')[0].p.message, /Translator-Listing/);
  assert.equal(shown.called('git.createCommit')[0].p.author.email, '1234567+volunteer-1@users.noreply.github.com');
});

/* ---------- issues opened by our App's bot ("Send with GitHub", verified sender) ---------- */

const APP_BOT = { login: 'ds5-untethered-translate[bot]', id: 900001, type: 'Bot' };
const SENDER = '**Sender:** @anna-dev (id 4242, verified)';
function appIssue(p, line) {
  return Object.assign({}, p, { body: p.body.replace(intakeLib.MARKER + '\n', intakeLib.MARKER + '\n' + (line === undefined ? SENDER : line) + '\n') });
}
const appState = (p, extra) => issueState(p, Object.assign({ user: APP_BOT, performed_via_github_app: null }, extra || {}));
const appCtx = (eventName, extra) => ctx(eventName, Object.assign({ sender: APP_BOT }, extra || {}));

test('intake: our bot\'s issue is accepted; the verified sender drives the branch, the author and the PR', async () => {
  const github = fakeGithub(appState(appIssue(pageIssue('de', GOOD))));
  assert.equal(await actions.intake({ github, context: appCtx('issues'), core: fakeCore() }), 'created');
  const commit = github.called('git.createCommit')[0].p;
  assert.equal(commit.message, 'Translation: de (2 strings)\n');
  assert.deepEqual(commit.author, { name: 'anna-dev', email: '4242+anna-dev@users.noreply.github.com' });
  assert.equal(commit.committer.name, 'github-actions[bot]');
  assert.equal(github.called('git.createRef')[0].p.ref, 'refs/heads/translate/4242/de');
  const pr = github.called('pulls.create')[0].p;
  assert.equal(pr.title, 'Translation: de by @anna-dev');
  assert.match(pr.body, /sent by @anna-dev in #12/);
  assert.doesNotMatch(JSON.stringify(github.calls), /ds5-untethered-translate\[bot\]@|900001/);
  assert.equal(github.called('issues.update')[0].p.state, 'closed');
});

test('intake: our bot\'s further parts arrive as its own comments and complete the send', async () => {
  const p = appIssue(pageIssue('de', GOOD + ' '.repeat(3000), { maxPartChars: 100 }));
  const state = appState(p);
  state.comments = p.comments.map((body, i) => ({ id: i, body, user: APP_BOT }));
  const last = state.comments[state.comments.length - 1];
  const github = fakeGithub(state);
  const out = await actions.intake({ github, context: appCtx('issue_comment', { action: 'created', comment: last, issue: { number: 12, user: APP_BOT } }), core: fakeCore() });
  assert.equal(out, 'created');
  assert.equal(github.called('git.createCommit')[0].p.author.name, 'anna-dev');
  // A part posted by someone else on our bot's issue is ignored from the payload.
  const other = fakeGithub(state);
  const outOther = await actions.intake({ github: other, context: ctx('issue_comment', { action: 'created', comment: Object.assign({}, last, { user: USER }), issue: { number: 12, user: APP_BOT } }), core: fakeCore() });
  assert.equal(outOther, 'ignored');
  assert.equal(other.calls.length, 0);
});

test('intake: our bot\'s hidden issue gets a bot-authored commit that names nobody', async () => {
  const github = fakeGithub(appState(appIssue(pageIssue('de', GOOD, { listing: 'hidden' }))));
  assert.equal(await actions.intake({ github, context: appCtx('issues'), core: fakeCore() }), 'created');
  const commit = github.called('git.createCommit')[0].p;
  assert.equal(commit.message, 'Translation: de (2 strings)\n\nTranslator-Listing: hidden\n');
  assert.equal(commit.author, undefined);
  assert.equal(commit.committer, undefined);
  assert.doesNotMatch(commit.message, /anna-dev|4242/);
  assert.equal(github.called('pulls.create')[0].p.title, 'Translation: de');
  assert.equal(github.called('git.createRef')[0].p.ref, 'refs/heads/translate/4242/de');
});

test('intake: the open pull request limit counts the verified sender\'s branches', async () => {
  const mine = [botPull(4, 'translate/4242/fr'), botPull(5, 'translate/4242/ja'), botPull(6, 'translate/4242/pl')];
  const state = Object.assign(appState(appIssue(pageIssue('de', GOOD))), { openPulls: mine });
  const github = fakeGithub(state);
  assert.equal(await actions.intake({ github, context: appCtx('issues'), core: fakeCore() }), 'limited');
  assert.match(github.called('issues.createComment')[0].p.body, /\(#4, #5, #6\)/);
  // Three open pull requests of another volunteer (or of the bot's own id) do not count.
  const others = Object.assign(appState(appIssue(pageIssue('de', GOOD))), {
    openPulls: [botPull(4, 'translate/1234567/fr'), botPull(5, 'translate/1234567/ja'), botPull(6, 'translate/900001/pl')]
  });
  assert.equal(await actions.intake({ github: fakeGithub(others), context: appCtx('issues'), core: fakeCore() }), 'created');
});

test('intake: a Sender line in a user\'s issue is ignored; the issue author is the volunteer', async () => {
  for (const line of [SENDER, '**Sender:** @Midzer00 (id 1, verified)']) {
    const github = fakeGithub(issueState(appIssue(pageIssue('de', GOOD), line)));
    assert.equal(await actions.intake({ github, context: ctx('issues'), core: fakeCore() }), 'created', line);
    const commit = github.called('git.createCommit')[0].p;
    assert.deepEqual(commit.author, { name: 'volunteer-1', email: '1234567+volunteer-1@users.noreply.github.com' });
    assert.equal(github.called('git.createRef')[0].p.ref, 'refs/heads/translate/1234567/de');
    assert.equal(github.called('pulls.create')[0].p.title, 'Translation: de by @volunteer-1');
    assert.doesNotMatch(JSON.stringify(github.calls), /anna-dev|Midzer00 </);
  }
});

test('intake: another bot\'s issue is ignored, even with a well-formed Sender line', async () => {
  const other = { login: 'helper[bot]', id: 77, type: 'Bot' };
  const p = appIssue(pageIssue('de', GOOD));
  // From the payload (the sender is another bot) ...
  let github = fakeGithub(appState(p, { user: other }));
  assert.equal(await actions.intake({ github, context: ctx('issues', { sender: other }), core: fakeCore() }), 'ignored');
  assert.equal(github.calls.length, 0);
  // ... and from the fetched issue (another App's bot, even if the event looks like ours).
  github = fakeGithub(appState(p, { user: other, performed_via_github_app: { slug: 'helper' } }));
  assert.equal(await actions.intake({ github, context: appCtx('issues'), core: fakeCore() }), 'ignored');
  assert.equal(github.called('git.createCommit').length, 0);
  assert.equal(github.called('issues.createComment').length, 0);
  // A user account that merely names itself like our bot is not our bot.
  github = fakeGithub(appState(p, { user: { login: APP_BOT.login, id: 3, type: 'User' } }));
  assert.equal(await actions.intake({ github, context: appCtx('issues'), core: fakeCore() }), 'ignored');
});

test('intake: our bot\'s issue without a valid Sender line under the marker is ignored', async () => {
  const bad = [
    pageIssue('de', GOOD),
    appIssue(pageIssue('de', GOOD), '**Sender:** @anna-dev (id 4242)'),
    (() => { const p = pageIssue('de', GOOD); return Object.assign({}, p, { body: p.body.replace('**Strings:**', SENDER + '\n**Strings:**') }); })()
  ];
  for (const p of bad) {
    const github = fakeGithub(appState(p));
    assert.equal(await actions.intake({ github, context: appCtx('issues'), core: fakeCore() }), 'ignored');
    assert.equal(github.called('git.createCommit').length, 0);
    assert.equal(github.called('issues.createComment').length, 0);
  }
  // A form-style issue from our bot is not something the Worker makes.
  const url = 'https://github.com/user-attachments/files/57/de.json';
  const form = fakeGithub(appState(formIssue('de', '[de.json](' + url + ')')));
  assert.equal(await actions.intake({ github: form, context: appCtx('issues'), core: fakeCore(), fetchImpl: fakeFetch({ [url]: { body: GOOD } }) }), 'ignored');
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

/* ---------- branches and the open pull request limit ---------- */

test('intake: an old-format branch or a fork with the same branch name is never updated', async () => {
  const state = Object.assign(issueState(pageIssue('de', GOOD)), {
    openPulls: [botPull(3, 'translate/de-volunteer-1'), botPull(4, 'translate/1234567/de', 'volunteer-1/repo')]
  });
  const github = fakeGithub(state);
  assert.equal(await actions.intake({ github, context: ctx('issues'), core: fakeCore() }), 'created');
  assert.equal(github.called('pulls.update').length, 0);
  assert.equal(github.called('pulls.create')[0].p.head, 'translate/1234567/de');
});

test('intake: a fourth language is refused while three of the volunteer\'s pull requests are open', async () => {
  const mine = [botPull(4, 'translate/1234567/fr'), botPull(5, 'translate/1234567/ja'), botPull(6, 'translate/1234567/pl')];
  const others = [botPull(7, 'translate/7654321/de'), botPull(8, 'translate/de-volunteer-1')];
  const state = Object.assign(issueState(pageIssue('de', GOOD)), { openPulls: mine.concat(others) });
  const github = fakeGithub(state), core = fakeCore();
  assert.equal(await actions.intake({ github, context: ctx('issues'), core }), 'limited');
  const body = github.called('issues.createComment')[0].p.body;
  assert.match(body, /already have 3 translation pull requests waiting for review \(#4, #5, #6\)/);
  for (const name of ['git.createTree', 'git.createCommit', 'git.updateRef', 'git.createRef', 'pulls.create', 'pulls.update', 'issues.update']) {
    assert.equal(github.called(name).length, 0, name);
  }
  assert.equal(core.log.failed.length, 0);

  // Two open: the third language is fine. An update of one of the three open languages is always fine.
  const two = fakeGithub(Object.assign(issueState(pageIssue('de', GOOD)), { openPulls: mine.slice(0, 2) }));
  assert.equal(await actions.intake({ github: two, context: ctx('issues'), core: fakeCore() }), 'created');
  const update = fakeGithub(Object.assign(issueState(pageIssue('fr', GOOD.replace('Deutsch', 'Français'))), { branchExists: true, openPulls: mine }));
  assert.equal(await actions.intake({ github: update, context: ctx('issues'), core: fakeCore() }), 'updated');
  assert.equal(update.called('pulls.update')[0].p.pull_number, 4);
});

/* ---------- rate limit ---------- */

test('intake: comment events without a well-formed further part cost no API call', async () => {
  const p = pageIssue('de', GOOD + ' '.repeat(3000), { maxPartChars: 40 });
  const sha = p.sha256;
  const block = (attrs) => '```ds5-translate ' + attrs + '\nQUJD\n```';
  const bodies = ['thanks', '```ds5-translate', block('part=2/0 encoding=gzip+base64 sha256=' + sha),
    block('part=2/99 encoding=gzip+base64 sha256=' + sha), block('part=1/3 encoding=gzip+base64 sha256=' + sha),
    block('part=2/3 encoding=gzip+base64 sha256=nope'), block('part=4/3 encoding=gzip+base64 sha256=' + sha)];
  for (const body of bodies) {
    const github = fakeGithub(issueState(p));
    const out = await actions.intake({ github, context: ctx('issue_comment', { action: 'created', comment: { id: 1, body, user: USER } }), core: fakeCore() });
    assert.equal(out, 'ignored', body);
    assert.equal(github.calls.length, 0, body);
  }
  // A comment by someone else is dropped from the payload too, even with a good part.
  const other = { login: 'someone-else', type: 'User' };
  const github = fakeGithub(issueState(p));
  const out = await actions.intake({ github, context: ctx('issue_comment', { sender: other, issue: { number: 12, user: USER }, comment: { id: 1, body: p.comments[0], user: other } }), core: fakeCore() });
  assert.equal(out, 'ignored');
  assert.equal(github.calls.length, 0);
  // An issue event whose payload is not a translation issue is dropped before the API too.
  const plain = fakeGithub(issueState(p));
  assert.equal(await actions.intake({ github: plain, context: ctx('issues', { issue: { number: 12, title: 'Bug', body: 'it crashes' } }), core: fakeCore() }), 'ignored');
  assert.equal(plain.calls.length, 0);
});

test('intake: at most 3 pages of comments are read', async () => {
  const p = pageIssue('de', GOOD);
  const state = issueState(p);
  state.comments = Array.from({ length: 1000 }, (x, i) => ({ id: i, body: 'chatter ' + i, user: { login: 'someone-else', type: 'User' } }));
  const github = fakeGithub(state);
  assert.equal(await actions.intake({ github, context: ctx('issues'), core: fakeCore() }), 'created');
  assert.deepEqual(github.called('issues.listComments').map((c) => c.p.page), [1, 2, 3]);
});

test('intake: after 3 refusal comments on an issue the run only logs', async () => {
  const bad = pageIssue('de', json({ 'About.Version': 'Version' }));
  const bot = { login: 'github-actions[bot]', type: 'Bot' };
  const refusal = (i) => ({ id: 100 + i, body: failureBody(), user: bot });
  for (const [prior, comments] of [[2, true], [3, false]]) {
    const state = issueState(bad);
    state.comments = Array.from({ length: prior }, (x, i) => refusal(i)).concat([{ id: 50, body: 'Thank you! ...', user: bot }]);
    const github = fakeGithub(state), core = fakeCore();
    assert.equal(await actions.intake({ github, context: ctx('issues', { action: 'edited' }), core }), 'failed');
    assert.equal(github.called('issues.createComment').length, comments ? 1 : 0, 'prior ' + prior);
    if (!comments) assert.match(core.log.warning.map((w) => w.m).join('\n'), /already has 3 refusal comments; not commenting again/);
  }
  // The open pull request limit counts as a refusal too.
  const mine = [botPull(4, 'translate/1234567/fr'), botPull(5, 'translate/1234567/ja'), botPull(6, 'translate/1234567/pl')];
  const state = Object.assign(issueState(pageIssue('de', GOOD)), { openPulls: mine });
  state.comments = [refusal(1), refusal(2), refusal(3)];
  const github = fakeGithub(state);
  assert.equal(await actions.intake({ github, context: ctx('issues'), core: fakeCore() }), 'limited');
  assert.equal(github.called('issues.createComment').length, 0);
});

function failureBody() {
  return intakeLib.failureComment([{ level: 'error', rule: 'icu.missing', message: 'm' }]);
}

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
