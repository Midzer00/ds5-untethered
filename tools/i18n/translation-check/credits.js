/* Pure logic of the translator credits data (contract addendum "translator credits"): turns the commits that touched
 * the language files on main into translators.json.
 *
 *   languageOfPath(path)        -> the canonical code of src/Kopru.Core/Localization/strings/<code>.json, or null for
 *                                  en, tr, qps, qtg and anything that is not a language file
 *   fromApiCommit(c, languages) -> the input shape below from one item of GET /repos/{o}/{r}/commits
 *   parseCoAuthors(message)     -> [{ login, id }] from "Co-authored-by: ... <<id>+<login>@users.noreply.github.com>"
 *   hasHiddenTrailer(message)   -> true when the message carries "Translator-Listing: hidden"
 *   parseHiddenList(text)       -> the logins of tools/i18n/translators-hidden.txt (lower case)
 *   buildTranslators(input)     -> { generated, languages: { <code>: [{ login, commits, first, last }] } }
 *
 * A commit is { sha, author: { login, type } | null, date (ISO 8601), message, languages: [codes] }.
 * Rules:
 *   - a commit credits its author (the GitHub account from the API) and every noreply co-author;
 *   - the repo owner and bots ([bot] logins, type Bot, github-actions, web-flow) are never listed;
 *   - a commit with "Translator-Listing: hidden" hides everyone it credits, in every language (the volunteer of a
 *     bot commit is its co-author; for a squash merge the author may be the owner, so the co-authors are hidden too);
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

function languageOfPath(filePath) {
  const prefix = rules.STRINGS_DIR + '/';
  const p = String(filePath || '');
  if (!p.startsWith(prefix)) return null;
  const name = p.slice(prefix.length);
  if (name.indexOf('/') >= 0 || !name.endsWith('.json')) return null;
  const cc = rules.canonicalCode(name.slice(0, -'.json'.length));
  return cc.ok ? cc.code : null;
}

function fromApiCommit(c, languages) {
  const commit = (c && c.commit) || {};
  const date = (commit.author && commit.author.date) || (commit.committer && commit.committer.date) || null;
  return {
    sha: c && c.sha,
    author: c && c.author && c.author.login ? { login: c.author.login, type: c.author.type || 'User' } : null,
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

// The people a commit credits: [{ key (lower case), login }], owner and bots left out, each person once.
function creditedPeople(commit, ownerKey) {
  const seen = new Map();
  const add = (login, type) => {
    if (!login || isBot(login, type)) return;
    const key = String(login).toLowerCase();
    if (key === ownerKey || seen.has(key)) return;
    seen.set(key, { key, login: String(login) });
  };
  if (commit.author) add(commit.author.login, commit.author.type);
  for (const c of parseCoAuthors(commit.message)) add(c.login, 'User');
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
 * input: { commits, owner, hidden (text of translators-hidden.txt, or an iterable of logins), generated? (Date|string) }
 */
function buildTranslators(input) {
  const ownerKey = String(input.owner || '').toLowerCase();
  const hidden = typeof input.hidden === 'string' || input.hidden == null
    ? parseHiddenList(input.hidden)
    : new Set(Array.from(input.hidden, (l) => String(l).replace(/^@/, '').toLowerCase()));
  const commits = mergeCommits(input.commits).map((c) => {
    const t = Date.parse(c.date);
    return Object.assign({}, c, { time: t, people: creditedPeople(c, ownerKey) });
  }).filter((c) => isFinite(c.time));

  for (const c of commits) {
    if (hasHiddenTrailer(c.message)) for (const p of c.people) hidden.add(p.key);
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
  languageOfPath,
  fromApiCommit,
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
