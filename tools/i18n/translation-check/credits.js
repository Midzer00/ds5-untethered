/* Pure logic of the translator credits data (contract addendum "translator credits"): turns the commits that touched
 * the language files on main into translators.json.
 *
 *   languageOfPath(path)        -> the canonical code of src/Kopru.Core/Localization/strings/<code>.json, or null for
 *                                  en, tr, qps, qtg and anything that is not a language file
 *   fromApiCommit(c, languages) -> the input shape below from one item of GET /repos/{o}/{r}/commits; when GitHub
 *                                  links no account to the author (or committer), a noreply address still gives the
 *                                  login
 *   intakeKind(commit, legacy)  -> 'intake' (made by the intake workflow today), 'legacy' (an allowlisted older intake
 *                                  commit) or null (any other commit, whatever its message says)
 *   loginFromNoreply(email)     -> the login of "<id>+<login>@users.noreply.github.com" (or "<login>@..."), or null
 *   parseCoAuthors(message)     -> [{ login, id }] from "Co-authored-by: ... <<id>+<login>@users.noreply.github.com>"
 *   hasHiddenTrailer(message)   -> true when the message carries "Translator-Listing: hidden"
 *   parseHiddenList(text)       -> the logins of tools/i18n/translators-hidden.txt (lower case)
 *   buildTranslators(input)     -> { generated, languages: { <code>: [{ login, commits, first, last }] } }
 *
 * A commit is { sha, author: { login, type } | null, committer: { login, type } | null, date (ISO 8601), message,
 * languages: [codes] }.
 * Rules:
 *   - a commit credits its author (the GitHub account from the API). The intake makes the volunteer the author of its
 *     bot-committed commits, so that is the credit for every new commit, as for any human commit;
 *   - Co-authored-by lines and the "Translator-Listing: hidden" trailer are message text anyone can write, so they
 *     count only on commits of the intake (intakeKind):
 *       'intake': committed by github-actions[bot], message exactly "Translation: <code> (<n> strings)", optionally
 *                 followed by a blank line and the trailer. The trailer makes that commit credit nobody; it never
 *                 hides anyone elsewhere. (The intake authors a hidden send as the bot, so it names nobody anyway.)
 *       'legacy': one of LEGACY_INTAKE_SHAS (older intake commits: bot author, the volunteer as the one
 *                 Co-authored-by line). They credit that co-author; with the trailer, the co-author is hidden in
 *                 every language, as before;
 *     on every other commit (a normal pull request, a squash merge) both are ignored: no co-author credit, no hiding;
 *   - the repo owner and bots ([bot] logins, type Bot, github-actions, web-flow) are never listed;
 *   - logins in the hidden list are never listed;
 *   - the same commit seen for several files is merged, a person credited twice by one commit counts once, and logins
 *     are compared without case;
 *   - each language lists people by last contribution, newest first (then more commits, then login).
 * Zero dependencies, CommonJS, Node 20 or newer. */
'use strict';

const rules = require('./rules');

const HIDDEN_TRAILER_RE = /^[ \t]*Translator-Listing:[ \t]*hidden[ \t]*$/im;
const CO_AUTHOR_RE = /^[ \t]*Co-authored-by:[^<\n]*<([^>\n]+)>[ \t]*$/gim;
const NOREPLY_RE = /^(?:(\d+)\+)?([A-Za-z0-9](?:[A-Za-z0-9-]{0,38})(?:\[bot\])?)@users\.noreply\.github\.com$/i;
const BOT_LOGINS = ['github-actions', 'web-flow'];
// The identity the intake workflow commits as (tools/i18n/translation-check/intake.js BOT_COMMITTER).
const INTAKE_COMMITTER = 'github-actions[bot]';
// Intake commits made before the volunteer became the commit author (Co-authored-by credits). Listed by SHA, so a
// commit written later cannot claim to be one of them by its date or message. Only 0be59df exists (never on main).
const LEGACY_INTAKE_SHAS = ['0be59dfb6cf5d812b771b4fddc12458a73547dcd'];
const CODE = '[A-Za-z]{2,3}(?:-[A-Za-z0-9]{1,8}){0,4}';
const LOGIN = '[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})';
const INTAKE_MESSAGE_RE = new RegExp('^Translation: ' + CODE + ' \\(\\d{1,7} strings\\)(\\n\\nTranslator-Listing: hidden)?$');
const LEGACY_MESSAGE_RE = new RegExp('^Translation: ' + CODE + ' \\(\\d{1,7} strings\\)\\n\\nCo-authored-by: [^\\n<>]+ <\\d{1,16}\\+(' +
  LOGIN + ')@users\\.noreply\\.github\\.com>(\\nTranslator-Listing: hidden)?$');

function languageOfPath(filePath) {
  const prefix = rules.STRINGS_DIR + '/';
  const p = String(filePath || '');
  if (!p.startsWith(prefix)) return null;
  const name = p.slice(prefix.length);
  if (name.indexOf('/') >= 0 || !name.endsWith('.json')) return null;
  const cc = rules.canonicalCode(name.slice(0, -'.json'.length));
  return cc.ok ? cc.code : null;
}

function loginFromNoreply(email) {
  const m = NOREPLY_RE.exec(String(email || '').trim());
  return m ? m[2] : null;
}

// The account GitHub links to a commit identity, else the login of its noreply address.
function accountOf(linked, raw) {
  if (linked && linked.login) return { login: linked.login, type: linked.type || 'User' };
  // GitHub resolves a noreply address to its account; if it ever does not, the address itself names the login.
  const login = loginFromNoreply(raw && raw.email);
  return login ? { login, type: /\[bot\]$/i.test(login) ? 'Bot' : 'User' } : null;
}

function fromApiCommit(c, languages) {
  const commit = (c && c.commit) || {};
  const date = (commit.author && commit.author.date) || (commit.committer && commit.committer.date) || null;
  return {
    sha: c && c.sha,
    author: accountOf(c && c.author, commit.author),
    committer: accountOf(c && c.committer, commit.committer),
    date,
    message: String(commit.message || ''),
    languages: (languages || []).slice()
  };
}

function parseCoAuthors(message) {
  const out = [];
  const text = String(message || '').replace(/\r\n?/g, '\n');
  CO_AUTHOR_RE.lastIndex = 0;
  let m;
  while ((m = CO_AUTHOR_RE.exec(text))) {
    const e = NOREPLY_RE.exec(m[1].trim());
    if (e) out.push({ login: e[2], id: e[1] ? Number(e[1]) : null });
  }
  return out;
}

function hasHiddenTrailer(message) {
  return HIDDEN_TRAILER_RE.test(String(message || '').replace(/\r\n?/g, '\n'));
}

function parseHiddenList(text) {
  const out = new Set();
  for (const raw of String(text || '').split(/\r\n?|\n/)) {
    const line = raw.replace(/#.*/, '').trim().replace(/^@/, '');
    if (line) out.add(line.toLowerCase());
  }
  return out;
}

function isBot(login, type) {
  const l = String(login || '').toLowerCase();
  return type === 'Bot' || /\[bot\]$/.test(l) || BOT_LOGINS.indexOf(l) >= 0;
}

function messageOf(commit) {
  return String(commit.message || '').replace(/\r\n?/g, '\n').replace(/\n+$/, '');
}

function isLogin(account, login) {
  return !!account && String(account.login || '').toLowerCase() === login;
}

/**
 * 'intake' | 'legacy' | null. legacy: the SHAs of older intake commits (default LEGACY_INTAKE_SHAS).
 * Only these commits may credit a co-author or carry a listing choice; see the rules above.
 */
function intakeKind(commit, legacy) {
  if (!commit) return null;
  const msg = messageOf(commit);
  const shas = legacy || new Set(LEGACY_INTAKE_SHAS);
  if (commit.sha && shas.has(commit.sha) && isLogin(commit.author, INTAKE_COMMITTER) && LEGACY_MESSAGE_RE.test(msg)) return 'legacy';
  if (isLogin(commit.committer, INTAKE_COMMITTER) && INTAKE_MESSAGE_RE.test(msg)) return 'intake';
  return null;
}

// The people a commit credits: [{ key (lower case), login }], owner and bots left out, each person once.
// kind: intakeKind(commit). Only a legacy intake commit credits its co-author.
function creditedPeople(commit, ownerKey, kind) {
  const seen = new Map();
  const add = (login, type) => {
    if (!login || isBot(login, type)) return;
    const key = String(login).toLowerCase();
    if (key === ownerKey || seen.has(key)) return;
    seen.set(key, { key, login: String(login) });
  };
  if (kind === 'intake' && hasHiddenTrailer(commit.message)) return [];
  if (commit.author) add(commit.author.login, commit.author.type);
  if (kind === 'legacy') for (const c of parseCoAuthors(commit.message)) add(c.login, 'User');
  return Array.from(seen.values());
}

// Same sha seen for several language files -> one commit with the union of the languages.
function mergeCommits(commits) {
  const bySha = new Map();
  const out = [];
  for (const c of commits || []) {
    if (!c) continue;
    const key = c.sha || ('#' + out.length);
    const prev = bySha.get(key);
    if (prev) {
      for (const l of c.languages || []) if (prev.languages.indexOf(l) < 0) prev.languages.push(l);
      continue;
    }
    const copy = Object.assign({}, c, { languages: (c.languages || []).slice() });
    bySha.set(key, copy);
    out.push(copy);
  }
  return out;
}

function isoSeconds(d) {
  return new Date(d).toISOString().replace(/\.\d{3}Z$/, 'Z');
}

/**
 * input: { commits, owner, hidden (text of translators-hidden.txt, or an iterable of logins), generated? (Date|string),
 *          legacyShas? (iterable; default LEGACY_INTAKE_SHAS, for tests) }
 */
function buildTranslators(input) {
  const ownerKey = String(input.owner || '').toLowerCase();
  const hidden = typeof input.hidden === 'string' || input.hidden == null
    ? parseHiddenList(input.hidden)
    : new Set(Array.from(input.hidden, (l) => String(l).replace(/^@/, '').toLowerCase()));
  const legacy = new Set(input.legacyShas ? Array.from(input.legacyShas) : LEGACY_INTAKE_SHAS);
  const commits = mergeCommits(input.commits).map((c) => {
    const t = Date.parse(c.date);
    const kind = intakeKind(c, legacy);
    return Object.assign({}, c, { time: t, kind, people: creditedPeople(c, ownerKey, kind) });
  }).filter((c) => isFinite(c.time));

  // A hidden listing on a legacy intake commit hides its co-author everywhere (the commit is on the allowlist, so the
  // name in its message is the volunteer's own choice). Hidden intake commits only credit nobody (creditedPeople).
  for (const c of commits) {
    if (c.kind === 'legacy' && hasHiddenTrailer(c.message)) for (const p of c.people) hidden.add(p.key);
  }

  const langs = new Map();
  for (const c of commits) {
    for (const lang of c.languages) {
      const code = rules.canonicalCode(lang);
      if (!code.ok) continue;
      if (!langs.has(code.code)) langs.set(code.code, new Map());
      const people = langs.get(code.code);
      for (const p of c.people) {
        if (hidden.has(p.key)) continue;
        let e = people.get(p.key);
        if (!e) { e = { login: p.login, shas: new Set(), first: c.time, last: c.time, loginTime: c.time }; people.set(p.key, e); }
        e.shas.add(c.sha);
        if (c.time < e.first) e.first = c.time;
        if (c.time > e.last) e.last = c.time;
        if (c.time >= e.loginTime) { e.login = p.login; e.loginTime = c.time; }
      }
    }
  }

  const languages = {};
  for (const code of Array.from(langs.keys()).sort()) {
    const list = Array.from(langs.get(code).values()).sort((a, b) =>
      (b.last - a.last) || (b.shas.size - a.shas.size) || (a.login.toLowerCase() < b.login.toLowerCase() ? -1 : a.login.toLowerCase() > b.login.toLowerCase() ? 1 : 0));
    if (!list.length) continue;
    languages[code] = list.map((e) => ({
      login: e.login,
      commits: e.shas.size,
      first: new Date(e.first).toISOString().slice(0, 10),
      last: new Date(e.last).toISOString().slice(0, 10)
    }));
  }
  return { generated: isoSeconds(input.generated || new Date()), languages };
}

function serialise(data) {
  return JSON.stringify(data, null, 2) + '\n';
}

// True when two translators.json objects list the same people (the generated time is ignored).
function sameLanguages(a, b) {
  return !!a && !!b && JSON.stringify(a.languages) === JSON.stringify(b.languages);
}

module.exports = {
  BOT_LOGINS,
  INTAKE_COMMITTER,
  LEGACY_INTAKE_SHAS,
  intakeKind,
  languageOfPath,
  fromApiCommit,
  loginFromNoreply,
  parseCoAuthors,
  hasHiddenTrailer,
  parseHiddenList,
  isBot,
  creditedPeople,
  mergeCommits,
  buildTranslators,
  serialise,
  sameLanguages
};
