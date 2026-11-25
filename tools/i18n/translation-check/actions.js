/* Glue between the GitHub workflows and the pure modules. Called from actions/github-script:
 *
 *   translation-validate.yml: await require(<workspace>/base/tools/i18n/translation-check/actions.js).validate({ github, context, core })
 *   translation-intake.yml:   await require(<workspace>/tools/i18n/translation-check/actions.js).intake({ github, context, core })
 *   translators-data.yml:     await require(<workspace>/tools/i18n/translation-check/actions.js).translatorsData({ github, context, core })
 *
 * The decisions live in pr-check.js, intake.js and credits.js (unit-tested); this file only reads files and calls the
 * REST API.
 * Issue and PR content never reaches a shell. Zero dependencies, CommonJS, Node 20 or newer. */
'use strict';

const fs = require('fs');
const path = require('path');
const rules = require('./rules');
const prCheck = require('./pr-check');
const intakeLib = require('./intake');
const credits = require('./credits');

/* ---------- way 1: validate a pull request ---------- */

// Reads a checked-out file of the PR only if it is a regular file inside the checkout (no symlinks out of it).
function readPrFile(root, relPath) {
  const full = path.join(root, relPath);
  let st;
  try { st = fs.lstatSync(full); } catch (e) { return { missing: true }; }
  if (st.isSymbolicLink() || !st.isFile()) return { symlink: true };
  let realRoot, realFull;
  try { realRoot = fs.realpathSync(root); realFull = fs.realpathSync(full); } catch (e) { return { missing: true }; }
  if (!realFull.startsWith(realRoot + path.sep)) return { symlink: true };
  return { bytes: fs.readFileSync(realFull) };
}

async function validate({ github, context, core, workspace, owner: ownerLogin }) {
  workspace = workspace || process.env.GITHUB_WORKSPACE;
  ownerLogin = ownerLogin || process.env.REPO_OWNER || context.repo.owner;
  const pr = context.payload.pull_request;
  if (!pr) { core.setFailed('This workflow runs on pull_request events only.'); return null; }
  const author = pr.user && pr.user.login || '';
  const isOwner = author.toLowerCase() === String(ownerLogin).toLowerCase();

  const listed = await github.paginate(github.rest.pulls.listFiles, {
    owner: context.repo.owner, repo: context.repo.repo, pull_number: pr.number, per_page: 100
  });
  const baseRoot = path.join(workspace, 'base');
  const prRoot = path.join(workspace, 'pr');
  const enText = fs.readFileSync(path.join(baseRoot, rules.STRINGS_DIR, 'en.json'), 'utf8');

  const files = [];
  for (const f of listed) {
    if (prCheck.fileNameOf(f.filename) == null && prCheck.fileNameOf(f.previous_filename) == null) continue;
    const entry = { path: f.filename, status: f.status, previousPath: f.previous_filename };
    if (f.status !== 'removed' && prCheck.fileNameOf(f.filename) != null) {
      const r = readPrFile(prRoot, f.filename);
      if (r.symlink) entry.symlink = true;
      else if (r.missing) entry.text = '';
      else entry.bytes = r.bytes;
    }
    files.push(entry);
  }

  const result = prCheck.checkPullRequest(files, { enText, isOwner });
  for (const f of result.files) {
    for (const p of f.errors.concat(f.warnings)) {
      const props = { title: 'Translation check: ' + p.rule, file: f.path };
      if (p.line) props.startLine = p.line;
      if (p.level === 'error') core.error(p.message, props);
      else core.warning(p.message, props);
    }
  }
  await core.summary.addRaw(prCheck.summaryMarkdown(result)).write();
  if (!result.ok) core.setFailed(result.errorCount + ' translation problem(s); see the annotations and the job summary.');
  else core.info('Translation check passed (' + result.files.length + ' file(s), ' + result.warningCount + ' warning(s)).');
  return result;
}

/* ---------- ways 2 and 3: issue intake ---------- */

function isGone(e) { return !!e && (e.status === 404 || e.status === 410); }

function isBot(user) { return !user || user.type === 'Bot' || /\[bot\]$/.test(String(user.login || '')); }

// Comment and close must not break the run: a locked or deleted issue only leaves a warning.
async function safely(core, what, fn) {
  try { return await fn(); } catch (e) {
    if (e && (e.status === 403 || e.status === 404 || e.status === 410 || e.status === 422)) {
      core.warning('Could not ' + what + ' (HTTP ' + e.status + '); the issue may be locked or deleted.');
      return null;
    }
    throw e;
  }
}

async function getText(github, repoRef, filePath, ref) {
  try {
    const res = await github.rest.repos.getContent(Object.assign({}, repoRef, { path: filePath, ref, mediaType: { format: 'raw' } }));
    return typeof res.data === 'string' ? res.data : Buffer.from(res.data).toString('utf8');
  } catch (e) {
    if (e && e.status === 404) return null;
    throw e;
  }
}

/**
 * Handles one issues/issue_comment event. Returns a short outcome string (for tests and the log):
 * 'ignored' | 'gone' | 'waiting' | 'failed' | 'unchanged' | 'created' | 'updated'.
 */
async function intake({ github, context, core, fetchImpl }) {
  fetchImpl = fetchImpl || globalThis.fetch;
  const repoRef = { owner: context.repo.owner, repo: context.repo.repo };
  const payload = context.payload || {};
  const number = payload.issue && payload.issue.number;
  const sender = payload.sender;
  const ignore = (why) => { core.info('Ignored: ' + why); return 'ignored'; };

  if (!number || payload.issue.pull_request) return ignore('not an issue');
  if (isBot(sender)) return ignore('sent by a bot');

  // Fresh state: the payload may be stale when runs queue up behind each other.
  let issue;
  try {
    issue = (await github.rest.issues.get(Object.assign({}, repoRef, { issue_number: number }))).data;
  } catch (e) {
    if (isGone(e)) { core.notice('Issue #' + number + ' no longer exists; nothing to do.'); return 'gone'; }
    throw e;
  }
  const expectedRepo = '/repos/' + repoRef.owner + '/' + repoRef.repo;
  if (issue.number !== number || (issue.repository_url && !String(issue.repository_url).toLowerCase().endsWith(expectedRepo.toLowerCase()))) {
    core.notice('Issue #' + number + ' was moved to another repository; nothing to do.');
    return 'gone';
  }
  if (issue.pull_request) return ignore('a pull request');
  if (issue.state !== 'open') return ignore('the issue is closed');
  if (issue.locked) { core.notice('Issue #' + number + ' is locked; nothing to do.'); return 'ignored'; }
  if (isBot(issue.user)) return ignore('opened by a bot');
  if (!sender || sender.login !== issue.user.login) return ignore('the event was caused by someone other than the issue author');
  if (context.eventName === 'issue_comment') {
    const c = payload.comment;
    if (!c || !c.user || c.user.login !== issue.user.login) return ignore('the comment is not by the issue author');
    if (String(c.body || '').indexOf('```ds5-translate') < 0) return ignore('the comment carries no file part');
  }
  const kind = intakeLib.recognise(issue);
  if (!kind) return ignore('not a translation issue');

  let comments = [];
  if (kind === 'page') {
    try {
      comments = await github.paginate(github.rest.issues.listComments, Object.assign({}, repoRef, { issue_number: number, per_page: 100 }));
    } catch (e) {
      if (isGone(e)) { core.notice('Issue #' + number + ' no longer exists; nothing to do.'); return 'gone'; }
      throw e;
    }
  }

  const fail = async (problems) => {
    core.warning('Translation in #' + number + ' did not pass: ' + problems.length + ' problem(s).');
    await safely(core, 'comment on the issue', () =>
      github.rest.issues.createComment(Object.assign({}, repoRef, { issue_number: number, body: intakeLib.failureComment(problems) })));
    return 'failed';
  };

  const decision = intakeLib.resolveSubmission(issue, comments);
  if (decision.action === 'ignore') return ignore(decision.reason);
  if (decision.action === 'wait') {
    core.info('Waiting for more parts: have ' + decision.have.join(',') + ' of ' + decision.of + '.');
    return 'waiting';
  }
  if (decision.action === 'fail') return fail(decision.problems);

  let bytes = decision.bytes;
  if (decision.action === 'download') {
    const d = await intakeLib.download(decision.url, fetchImpl);
    if (!d.ok) return fail([d.problem]);
    bytes = d.bytes;
  }
  const code = decision.code;

  const base = (payload.repository && payload.repository.default_branch) ||
    (await github.rest.repos.get(repoRef)).data.default_branch;
  const mainSha = (await github.rest.git.getRef(Object.assign({}, repoRef, { ref: 'heads/' + base }))).data.object.sha;
  const enText = await getText(github, repoRef, rules.STRINGS_DIR + '/en.json', mainSha);
  if (enText == null) throw new Error('en.json is missing on ' + base);
  const en = JSON.parse(enText);

  const result = intakeLib.checkSubmission(code, bytes, en);
  if (!result.ok) return fail(result.errors);

  const plan = intakeLib.planPullRequest({
    code, login: issue.user.login, userId: issue.user.id, issueNumber: number, result, en, hidden: !!decision.hidden
  });
  const close = () => safely(core, 'close the issue', () =>
    github.rest.issues.update(Object.assign({}, repoRef, { issue_number: number, state: 'closed', state_reason: 'completed' })));

  const existing = await getText(github, repoRef, plan.path, mainSha);
  if (existing === plan.content) {
    await safely(core, 'comment on the issue', () =>
      github.rest.issues.createComment(Object.assign({}, repoRef, { issue_number: number, body: intakeLib.unchangedComment(code) })));
    await close();
    return 'unchanged';
  }

  // One commit on top of main with only the language file; the branch is reset to it every time.
  const baseCommit = (await github.rest.git.getCommit(Object.assign({}, repoRef, { commit_sha: mainSha }))).data;
  const tree = (await github.rest.git.createTree(Object.assign({}, repoRef, {
    base_tree: baseCommit.tree.sha,
    tree: [{ path: plan.path, mode: '100644', type: 'blob', content: plan.content }]
  }))).data;
  const commit = (await github.rest.git.createCommit(Object.assign({}, repoRef, {
    message: plan.commitMessage, tree: tree.sha, parents: [mainSha]
  }))).data;
  try {
    await github.rest.git.updateRef(Object.assign({}, repoRef, { ref: 'heads/' + plan.branch, sha: commit.sha, force: true }));
  } catch (e) {
    if (!(e && (e.status === 422 || e.status === 404))) throw e;
    await github.rest.git.createRef(Object.assign({}, repoRef, { ref: 'refs/heads/' + plan.branch, sha: commit.sha }));
  }

  const open = (await github.rest.pulls.list(Object.assign({}, repoRef, { state: 'open', head: repoRef.owner + ':' + plan.branch, base, per_page: 10 }))).data;
  let pr, updated = false;
  if (open.length) {
    pr = (await github.rest.pulls.update(Object.assign({}, repoRef, { pull_number: open[0].number, title: plan.prTitle, body: plan.prBody }))).data;
    updated = true;
  } else {
    pr = (await github.rest.pulls.create(Object.assign({}, repoRef, { title: plan.prTitle, head: plan.branch, base, body: plan.prBody }))).data;
  }
  core.info((updated ? 'Updated ' : 'Opened ') + pr.html_url);

  await safely(core, 'comment on the issue', () =>
    github.rest.issues.createComment(Object.assign({}, repoRef, { issue_number: number, body: intakeLib.successComment(plan, pr.html_url, updated) })));
  await close();
  return updated ? 'updated' : 'created';
}

/* ---------- translator credits: translators.json on the branch translation-data ---------- */

const DATA_BRANCH = 'translation-data';
const DATA_FILE = 'translators.json';
const HIDDEN_LIST = 'tools/i18n/translators-hidden.txt';

/**
 * Lists the commits of every language file on the default branch, builds translators.json (credits.js) and
 * force-updates DATA_BRANCH to a single parentless commit holding only that file, through the Git data API.
 * Nothing is checked out or run from the commits; their messages are only parsed. When the branch already holds
 * exactly that file with the same people, it is left alone.
 * Returns 'unchanged' | 'updated' | 'created'.
 */
async function translatorsData({ github, context, core, owner: ownerLogin, now }) {
  const repoRef = { owner: context.repo.owner, repo: context.repo.repo };
  ownerLogin = ownerLogin || process.env.REPO_OWNER || context.repo.owner;
  const payload = context.payload || {};
  const base = (payload.repository && payload.repository.default_branch) ||
    (await github.rest.repos.get(repoRef)).data.default_branch;
  const mainSha = (await github.rest.git.getRef(Object.assign({}, repoRef, { ref: 'heads/' + base }))).data.object.sha;

  const listing = (await github.rest.repos.getContent(Object.assign({}, repoRef, { path: rules.STRINGS_DIR, ref: mainSha }))).data;
  const files = (Array.isArray(listing) ? listing : [])
    .filter((f) => f.type === 'file' && credits.languageOfPath(f.path) != null)
    .map((f) => ({ path: f.path, code: credits.languageOfPath(f.path) }))
    .sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));

  const commits = [];
  for (const f of files) {
    const list = await github.paginate(github.rest.repos.listCommits, Object.assign({}, repoRef, { sha: mainSha, path: f.path, per_page: 100 }));
    for (const c of list) commits.push(credits.fromApiCommit(c, [f.code]));
    core.info(f.code + ': ' + list.length + ' commit(s)');
  }
  const hiddenText = (await getText(github, repoRef, HIDDEN_LIST, mainSha)) || '';
  const data = credits.buildTranslators({ commits, owner: ownerLogin, hidden: hiddenText, generated: now || new Date() });
  const content = credits.serialise(data);

  const existing = await readDataBranch(github, repoRef);
  if (existing.clean && credits.sameLanguages(existing.data, data)) {
    core.info(DATA_BRANCH + ' already lists the same translators; nothing to do.');
    return 'unchanged';
  }

  const tree = (await github.rest.git.createTree(Object.assign({}, repoRef, {
    tree: [{ path: DATA_FILE, mode: '100644', type: 'blob', content }]
  }))).data;
  const commit = (await github.rest.git.createCommit(Object.assign({}, repoRef, {
    message: 'Translator credits data\n\nGenerated from ' + base + ' at ' + mainSha + ' by the translators-data workflow.\n',
    tree: tree.sha,
    parents: []
  }))).data;
  let outcome = 'updated';
  if (existing.exists) {
    await github.rest.git.updateRef(Object.assign({}, repoRef, { ref: 'heads/' + DATA_BRANCH, sha: commit.sha, force: true }));
  } else {
    await github.rest.git.createRef(Object.assign({}, repoRef, { ref: 'refs/heads/' + DATA_BRANCH, sha: commit.sha }));
    outcome = 'created';
  }
  const counts = Object.keys(data.languages).map((code) => code + ' ' + data.languages[code].length);
  core.info((outcome === 'created' ? 'Created ' : 'Updated ') + DATA_BRANCH + ': ' + (counts.join(', ') || 'nobody listed yet'));
  await core.summary.addRaw('Translator credits: ' + (counts.length ? counts.join(', ') : 'nobody listed yet') +
    ' (' + outcome + ' `' + DATA_BRANCH + '`).\n').write();
  return outcome;
}

// The data branch as it is: exists, clean (one parentless commit with only DATA_FILE) and its parsed data.
async function readDataBranch(github, repoRef) {
  let sha;
  try {
    sha = (await github.rest.git.getRef(Object.assign({}, repoRef, { ref: 'heads/' + DATA_BRANCH }))).data.object.sha;
  } catch (e) {
    if (e && e.status === 404) return { exists: false, clean: false, data: null };
    throw e;
  }
  const commit = (await github.rest.git.getCommit(Object.assign({}, repoRef, { commit_sha: sha }))).data;
  const tree = (await github.rest.git.getTree(Object.assign({}, repoRef, { tree_sha: commit.tree.sha }))).data;
  const entries = tree.tree || [];
  const clean = (commit.parents || []).length === 0 && entries.length === 1 && entries[0].path === DATA_FILE && entries[0].type === 'blob';
  let data = null;
  if (clean) {
    try { data = JSON.parse(await getText(github, repoRef, DATA_FILE, sha)); } catch (e) { data = null; }
  }
  return { exists: true, clean: clean && !!data, data };
}

module.exports = { validate, intake, readPrFile, translatorsData, DATA_BRANCH, DATA_FILE, HIDDEN_LIST };
