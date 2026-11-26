/* The submission the translation page sends to POST /submit: shape checks, and the issue the bot opens.
 *
 *   checkSubmission(json, { dev }) -> { ok: true, code, codeVerifier, redirectUri, title, language, strings, parts,
 *                                       listing } | { ok: false, error }
 *                            error: bad_request, bad_code, bad_redirect, bad_listing, bad_body, too_large,
 *                            too_many_parts, bad_title, bad_marker or bad_part (the names the page knows)
 *   issueBody(checked, user) -> the issue body, REBUILT by the Worker from the checked values only
 *   comments: checked.parts.slice(1), each rebuilt as exactly one fenced block
 *
 * The bot posts under its own name, so nothing the volunteer typed is passed through: no mentions, links, repository
 * references, markdown or HTML. The issue holds only the marker, the verified sender line, the optional listing line,
 * the language code with a letters-only name, two numbers, a fixed wrapper and the base64 parts.
 *
 * Only the shape is checked here. The file itself (gzip, sha256, the translation rules) is checked by the intake
 * workflow in the repository, as for every other way of sending. */

export const MARKER = '<!-- ds5-translate v1 -->';
export const MAX_TEXT = 60000;   // characters, for the body and for each comment
export const MAX_PARTS = 64;
export const MAX_NAME = 40;      // characters (code points) of the language name

// The OAuth callback pages the page may have used. GitHub checks the redirect_uri at the exchange against the one in
// the authorize request, so a code issued for any other page of the site cannot be exchanged here.
export const CALLBACKS = [
  'https://ds5untethered.com/translate/oauth-callback.html',
  'https://ds5untethered.com/preview-e31a5521/translate/oauth-callback.html'
];
// Accepted only when the Worker runs with DEV=true (local testing with a separate OAuth App).
export const DEV_CALLBACK = 'http://localhost:8765/translate/oauth-callback.html';

// "Translation: <code>": a BCP-47 shaped code. Whether it names a known language is the intake's job.
const CODE = '[A-Za-z]{2,3}(?:-[A-Za-z0-9]{1,8}){0,4}';
const TITLE_RE = new RegExp('^Translation: (' + CODE + ')$');
const CODE_RE = /^[A-Za-z0-9_-]{8,128}$/;
const VERIFIER_RE = /^[A-Za-z0-9._~-]{43,128}$/;   // RFC 7636

// The page's header lines. Only the code, the name (filtered below) and the two numbers are taken from them.
const LANGUAGE_RE = new RegExp('^\\*\\*Language:\\*\\*[ \\t]*(' + CODE + ')[ \\t]*(?:\\(([^\\n]*)\\))?[ \\t]*$', 'm');
const STRINGS_RE = /^\*\*Strings:\*\*[ \t]*(\d{1,7})[ \t]+of[ \t]+(\d{1,7})(?![0-9])/m;
// Everything but letters, combining marks, space and hyphen: no @, #, /, :, ., brackets or markup can survive.
const NAME_DROP_RE = /[^\p{L}\p{M} -]+/gu;

// One fenced file part. The data is base64 wrapped in lines; the attributes are exactly part, encoding and sha256.
const FENCE_OPEN = '```ds5-translate';
const FENCE_RE = /^```ds5-translate[ \t]+([^\n]*)\n([\s\S]*?)^```[ \t]*$/gm;
const ATTRS_RE = /^part=([1-9]\d{0,1})\/([1-9]\d{0,1}) encoding=gzip\+base64 sha256=([0-9a-f]{64})$/;
const DATA_RE = /^[A-Za-z0-9+/=\n]+$/;

function lf(s) {
  return s.replace(/\r\n?/g, '\n');
}

// How many times the fence opener appears, counting stops past `max`. Plain indexOf: no regex work on junk.
export function countFenceOpens(text, max) {
  let n = 0;
  for (let at = text.indexOf(FENCE_OPEN); at >= 0; at = text.indexOf(FENCE_OPEN, at + FENCE_OPEN.length)) {
    if (++n > max) break;
  }
  return n;
}

// The single fenced part of a text that holds exactly one opener (counted before). null when malformed.
function parseFence(text) {
  FENCE_RE.lastIndex = 0;
  const m = FENCE_RE.exec(text);
  if (!m) return null;
  const a = ATTRS_RE.exec(m[1].trim());
  const data = m[2].replace(/\n$/, '');
  if (!a || !DATA_RE.test(data)) return null;
  return { part: Number(a[1]), of: Number(a[2]), sha256: a[3], data, start: m.index, end: FENCE_RE.lastIndex };
}

export function fenceText(k, n, sha256, data) {
  return FENCE_OPEN + ' part=' + k + '/' + n + ' encoding=gzip+base64 sha256=' + sha256 + '\n' + data + '\n```';
}

// The volunteer's language name, reduced to letters, marks, spaces and hyphens, at most MAX_NAME code points.
export function cleanName(raw) {
  const s = String(raw == null ? '' : raw).replace(NAME_DROP_RE, ' ').replace(/ {2,}/g, ' ').trim();
  return Array.from(s).slice(0, MAX_NAME).join('').trim();
}

function allowedCallback(uri, dev) {
  return CALLBACKS.includes(uri) || (dev && uri === DEV_CALLBACK);
}

// opts.dev: the Worker runs with DEV=true. Fields the Worker does not use are ignored.
export function checkSubmission(b, opts) {
  const dev = !!(opts && opts.dev);
  const fail = (error) => ({ ok: false, error });
  if (!b || typeof b !== 'object' || Array.isArray(b)) return fail('bad_request');
  if (typeof b.code !== 'string' || !CODE_RE.test(b.code)) return fail('bad_code');
  if (typeof b.code_verifier !== 'string' || !VERIFIER_RE.test(b.code_verifier)) return fail('bad_code');
  if (typeof b.redirect_uri !== 'string' || !allowedCallback(b.redirect_uri, dev)) return fail('bad_redirect');
  const listing = b.listing === undefined ? 'shown' : b.listing;
  if (listing !== 'shown' && listing !== 'hidden') return fail('bad_listing');
  if (typeof b.title !== 'string') return fail('bad_title');
  if (typeof b.body !== 'string') return fail('bad_body');
  const comments = b.comments === undefined ? [] : b.comments;
  if (!Array.isArray(comments) || comments.some((c) => typeof c !== 'string')) return fail('bad_body');

  // Cheap checks first: sizes, then the number of fence openers (one in the body, one per comment), before any regex.
  if (b.body.length > MAX_TEXT || comments.some((c) => c.length > MAX_TEXT)) return fail('too_large');
  if (comments.length + 1 > MAX_PARTS) return fail('too_many_parts');
  if (countFenceOpens(b.body, 1) !== 1 || comments.some((c) => countFenceOpens(c, 1) !== 1)) return fail('bad_part');

  const t = TITLE_RE.exec(b.title);
  if (!t) return fail('bad_title');
  const code = t[1];
  const body = lf(b.body);
  // The marker is the whole first line.
  if (!body.startsWith(MARKER + '\n')) return fail('bad_marker');

  // Part 1 in the body, parts 2..n in the comments, in order, all of the same file.
  const first = parseFence(body);
  if (!first || first.part !== 1) return fail('bad_part');
  const n = first.of;
  if (n !== comments.length + 1) return fail('bad_part');
  const parts = [fenceText(1, n, first.sha256, first.data)];
  for (let i = 0; i < comments.length; i++) {
    const text = lf(comments[i]).trim();
    const f = parseFence(text);
    // A comment holds exactly one fenced block and nothing else.
    if (!f || f.start !== 0 || f.end !== text.length) return fail('bad_part');
    if (f.part !== i + 2 || f.of !== n || f.sha256 !== first.sha256) return fail('bad_part');
    parts.push(fenceText(f.part, n, f.sha256, f.data));
  }

  // The header lines above the fence: the language line is required and must name the title's code.
  const headText = body.slice(MARKER.length + 1, first.start);
  const lang = LANGUAGE_RE.exec(headText);
  if (!lang || lang[1] !== code) return fail('bad_body');
  const s = STRINGS_RE.exec(headText);
  let strings = null;
  if (s) {
    const done = Number(s[1]);
    const total = Number(s[2]);
    if (done > total) return fail('bad_body');
    strings = { done, total };
  }

  return {
    ok: true,
    code: b.code,
    codeVerifier: b.code_verifier,
    redirectUri: b.redirect_uri,
    title: 'Translation: ' + code,
    language: { code, name: cleanName(lang[2]) || code },
    strings,
    parts,
    comments: parts.slice(1),
    listing
  };
}

// No "@": the bot names the volunteer without notifying them (and a hidden volunteer is never pinged).
export function senderLine(user) {
  return '**Sender:** ' + user.login + ' (id ' + user.id + ', verified)';
}

// checked: a checkSubmission result. user: the verified { login, id }. Nothing else goes into the body.
export function issueBody(checked, user) {
  const lines = [MARKER, senderLine(user)];
  if (checked.listing === 'hidden') lines.push('**Listing:** hidden');
  lines.push('**Language:** ' + checked.language.code + ' (' + checked.language.name + ')');
  if (checked.strings) {
    lines.push('**Strings:** ' + checked.strings.done + ' of ' + checked.strings.total + ' · sent from ds5untethered.com/translate');
  }
  lines.push('', '<details><summary>Translation file (compressed)</summary>', '', checked.parts[0], '</details>', '');
  return lines.join('\n');
}
