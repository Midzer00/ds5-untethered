/* Pure logic of the intake Action (ways 2 and 3 of the translation submission contract).
 *
 *   recognise(issue)            -> 'page' (marker, way 3) | 'form' (issue form headings, way 2) | null
 *   resolveSubmission(...)      -> what to do with an issue: ignore, wait for more parts, download, check or fail
 *   listingHidden(kind, body)   -> true when the volunteer asked not to be listed among the translators
 *   decodeParts(...)            -> joined gzip+base64 parts -> bytes, with size cap and sha256 check
 *   download(url, fetch)        -> the form attachment, only from https://github.com/user-attachments/, size capped
 *   checkSubmission(...)        -> the shared rule set (rules.js) on the file
 *   planPullRequest(...)        -> branch, path, commit message, PR title/body and the issue comments
 *   failureComment(...)         -> the comment for a submission that does not pass
 *
 * Everything that comes from an issue is untrusted text: it is parsed here, never passed to a shell, and only
 * shown back in comments escaped (pr-check.mdEscape) or inside inline code.
 * Zero dependencies, CommonJS, Node 20 or newer. */
'use strict';

const crypto = require('crypto');
const zlib = require('zlib');
const rules = require('./rules');
const { mdEscape } = require('./pr-check');

const MARKER = '<!-- ds5-translate v1 -->';
const FORM_HEADINGS = { language: 'Language code', file: 'Translation file', hide: 'Credits', notes: 'Notes' };
// The issue form's checkbox (field id `hide`) and the commit trailer that carries the choice to the credits data.
const HIDE_LABEL = 'Do not list my name among the translators';
const LISTING_TRAILER = 'Translator-Listing: hidden';
const TITLE_PREFIX = 'Translation:';
const MAX_PARTS = 64;
// Base64 of a gzip stream that unpacks to at most 1 MB; real files compress to a fraction of this.
const MAX_BASE64_CHARS = 2 * 1024 * 1024;
const MAX_PROBLEMS_IN_COMMENT = 30;
const SITE = 'ds5untethered.com/translate';

function normalise(text) {
  return String(text == null ? '' : text).replace(/\r\n?/g, '\n');
}

function hasFormHeadings(body) {
  const b = normalise(body);
  return headingRe(FORM_HEADINGS.language).test(b) && headingRe(FORM_HEADINGS.file).test(b);
}

function headingRe(label) {
  return new RegExp('^###[ \\t]+' + label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '[ \\t]*$', 'm');
}

function recognise(issue) {
  if (!issue) return null;
  const body = normalise(issue.body);
  if (body.indexOf(MARKER) >= 0) return 'page';
  if (hasFormHeadings(body)) return 'form';
  return null;
}

// Issue form body: "### <label>\n\n<value>\n\n### <label>..." with "_No response_" for empty optional fields.
function parseFormBody(body) {
  const b = normalise(body);
  const out = {};
  const re = /^###[ \t]+(.+?)[ \t]*$/gm;
  const heads = [];
  let m;
  while ((m = re.exec(b))) heads.push({ label: m[1], start: m.index, end: re.lastIndex });
  for (let i = 0; i < heads.length; i++) {
    const value = b.slice(heads[i].end, i + 1 < heads.length ? heads[i + 1].start : b.length).trim();
    for (const id of Object.keys(FORM_HEADINGS)) {
      if (FORM_HEADINGS[id] === heads[i].label && !(id in out)) out[id] = value === '_No response_' ? '' : value;
    }
  }
  return out;
}

// "Translation: de" -> "de"; anything else -> null.
function codeFromTitle(title) {
  const m = /^\s*Translation:\s*(\S+)\s*$/.exec(String(title || ''));
  return m ? m[1] : null;
}

// "**Language:** de (Deutsch)" -> "de"
function codeFromPageBody(body) {
  const m = /^\*\*Language:\*\*[ \t]*([^\s(]+)/m.exec(normalise(body));
  return m ? m[1] : null;
}

// Way 3: "**Listing:** hidden" or "**Listing:** shown" under the header; anything else (or no line) means shown.
function listingFromPageBody(body) {
  const m = /^\*\*Listing:\*\*[ \t]*([A-Za-z]+)[ \t]*$/m.exec(normalise(body));
  return m && m[1].toLowerCase() === 'hidden' ? 'hidden' : 'shown';
}

// Way 2: the checkboxes field renders as "- [X] <label>" (ticked) or "- [ ] <label>" under its heading.
function hideTickedInForm(section) {
  const re = /^[ \t]*[-*][ \t]+\[([ xX])\][ \t]+(.+?)[ \t]*$/gm;
  const text = normalise(section);
  let m;
  while ((m = re.exec(text))) {
    if (m[1] !== ' ' && m[2] === HIDE_LABEL) return true;
  }
  return false;
}

function listingHidden(kind, body) {
  if (kind === 'page') return listingFromPageBody(body) === 'hidden';
  if (kind === 'form') return hideTickedInForm(parseFormBody(body).hide || '');
  return false;
}

/* ---------- way 2: the form attachment ---------- */

// Every http(s) URL in a piece of markdown: link targets and bare URLs.
function findUrls(text) {
  const out = [];
  const re = /\bhttps?:\/\/[^\s<>()\[\]"'`]+/gi;
  let m;
  while ((m = re.exec(String(text || '')))) {
    const u = m[0].replace(/[.,;:!?]+$/, '');
    if (out.indexOf(u) < 0) out.push(u);
  }
  return out;
}

function parseUrl(u) {
  try { return new URL(u); } catch (e) { return null; }
}

// Only https://github.com/user-attachments/... (no user info, no port).
function isAllowedAttachmentUrl(u) {
  const url = parseUrl(u);
  return !!url && url.protocol === 'https:' && url.hostname === 'github.com' && url.port === '' &&
    url.username === '' && url.password === '' && url.pathname.startsWith('/user-attachments/') &&
    String(u).startsWith('https://github.com/user-attachments/');
}

// GitHub answers with a 302 to a short-lived signed URL on objects.githubusercontent.com (observed for
// /user-attachments/files/... in public repositories). Redirects may only go to GitHub's own storage hosts.
function isAllowedRedirect(u) {
  const url = parseUrl(u);
  if (!url || url.protocol !== 'https:' || url.port !== '' || url.username !== '' || url.password !== '') return false;
  const h = url.hostname.toLowerCase();
  return h === 'github.com' || h.endsWith('.githubusercontent.com') || /^github-production-[a-z0-9-]+\.s3\.amazonaws\.com$/.test(h);
}

/**
 * Downloads the attachment with the injected fetch (Node's global fetch in the Action). No credentials are sent:
 * attachments of public repositories are readable without authentication. Redirects are followed by hand so every
 * hop is checked; the body is read in chunks and abandoned past maxBytes.
 * Returns { ok: true, bytes } or { ok: false, problem }.
 */
async function download(url, fetchImpl, options) {
  const maxBytes = (options && options.maxBytes) || rules.MAX_FILE_BYTES;
  const maxRedirects = 4;
  const timeoutMs = (options && options.timeoutMs) || 30000;
  if (!isAllowedAttachmentUrl(url)) return { ok: false, problem: attachmentHostProblem() };
  let current = url;
  for (let hop = 0; hop <= maxRedirects; hop++) {
    let res;
    try {
      const init = { redirect: 'manual', headers: { 'User-Agent': 'translation-intake', Accept: '*/*' } };
      if (typeof AbortSignal !== 'undefined' && AbortSignal.timeout) init.signal = AbortSignal.timeout(timeoutMs);
      res = await fetchImpl(current, init);
    } catch (e) {
      return { ok: false, problem: problem('download', 'The attached file could not be downloaded. Please attach it again.') };
    }
    if (res.status >= 300 && res.status < 400) {
      const loc = res.headers.get('location');
      const next = loc ? parseUrl(new URL(loc, current).href) : null;
      if (!next || !isAllowedRedirect(next.href)) {
        return { ok: false, problem: problem('download', 'The attachment link led somewhere unexpected. Please attach the file again.') };
      }
      current = next.href;
      continue;
    }
    if (res.status !== 200) {
      return { ok: false, problem: problem('download', 'The attached file could not be downloaded (HTTP ' + res.status + '). Please attach it again.') };
    }
    const declared = Number(res.headers.get('content-length'));
    if (isFinite(declared) && declared > maxBytes) return { ok: false, problem: sizeProblem(declared) };
    const chunks = [];
    let total = 0;
    if (res.body && typeof res.body.getReader === 'function') {
      const reader = res.body.getReader();
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        total += value.length;
        if (total > maxBytes) {
          try { await reader.cancel(); } catch (e) { /* ignore */ }
          return { ok: false, problem: sizeProblem(total) };
        }
        chunks.push(Buffer.from(value));
      }
    } else {
      const buf = Buffer.from(await res.arrayBuffer());
      total = buf.length;
      if (total > maxBytes) return { ok: false, problem: sizeProblem(total) };
      chunks.push(buf);
    }
    return { ok: true, bytes: Buffer.concat(chunks, total) };
  }
  return { ok: false, problem: problem('download', 'The attachment link redirected too many times.') };
}

function attachmentHostProblem() {
  return problem('attachment.host', 'The file must be attached in the form (a https://github.com/user-attachments/ link); other links are not downloaded.');
}

function sizeProblem(n) {
  return problem('file.size', 'The file is ' + (n > 0 ? n + ' bytes' : 'too large') + '; the limit is ' + rules.MAX_FILE_BYTES + ' bytes (1 MB).');
}

function problem(rule, message, extra) {
  return Object.assign({ level: 'error', rule, message }, extra || {});
}

/* ---------- way 3: the parts from the page ---------- */

// ```ds5-translate part=k/n encoding=gzip+base64 sha256=<hex>
// <base64>
// ```
const BLOCK_RE = /^```ds5-translate[ \t]+([^\n]*)\n([\s\S]*?)^```[ \t]*$/gm;

// texts: [{ source: 'body' | 'comment', id, text }] in creation order. Returns the parsed blocks.
function extractBlocks(texts) {
  const out = [];
  for (const t of texts) {
    const s = normalise(t.text);
    BLOCK_RE.lastIndex = 0;
    let m;
    while ((m = BLOCK_RE.exec(s))) {
      const attrs = {};
      for (const kv of m[1].trim().split(/[ \t]+/)) {
        const eq = kv.indexOf('=');
        if (eq > 0) attrs[kv.slice(0, eq)] = kv.slice(eq + 1);
      }
      const pm = /^(\d{1,4})\/(\d{1,4})$/.exec(attrs.part || '');
      out.push({
        source: t.source,
        id: t.id,
        part: pm ? Number(pm[1]) : null,
        of: pm ? Number(pm[2]) : null,
        encoding: attrs.encoding || null,
        sha256: (attrs.sha256 || '').toLowerCase(),
        data: m[2]
      });
    }
  }
  return out;
}

/**
 * Joins the parts. The block in the issue body must be part 1; its n, encoding and sha256 rule. Comment blocks with
 * a different n or sha256 belong to another send and are ignored; for a part sent twice the later one counts.
 * Returns { status: 'complete', base64, sha256, of } | { status: 'incomplete', have, of } | { status: 'error', problems }.
 */
function joinParts(blocks) {
  const first = blocks.find((b) => b.source === 'body');
  if (!first) return { status: 'error', problems: [problem('parts.none', 'The issue has no translation file block. Send it again from ' + SITE + '.')] };
  if (first.part !== 1 || !first.of || first.of < 1 || first.of > MAX_PARTS) {
    return { status: 'error', problems: [problem('parts.header', 'The file block in the issue is not a valid "part=1/<n>" block.')] };
  }
  if (first.encoding !== 'gzip+base64') {
    return { status: 'error', problems: [problem('parts.encoding', 'The file block uses an unknown encoding; expected gzip+base64.')] };
  }
  if (!/^[0-9a-f]{64}$/.test(first.sha256)) {
    return { status: 'error', problems: [problem('parts.header', 'The file block has no valid sha256.')] };
  }
  const byPart = {};
  byPart[1] = first;
  for (const b of blocks) {
    if (b === first) continue;
    if (b.of !== first.of || b.sha256 !== first.sha256 || b.encoding !== first.encoding) continue;
    if (!b.part || b.part < 2 || b.part > first.of) continue;
    byPart[b.part] = b;
  }
  const have = Object.keys(byPart).map(Number).sort((a, b) => a - b);
  if (have.length < first.of) return { status: 'incomplete', have, of: first.of };
  let base64 = '';
  for (let k = 1; k <= first.of; k++) base64 += byPart[k].data.replace(/\s+/g, '');
  return { status: 'complete', base64, sha256: first.sha256, of: first.of };
}

// Base64 -> gunzip -> sha256 check. Returns { ok: true, bytes } or { ok: false, problem }.
function decodeParts(base64, sha256, maxBytes) {
  maxBytes = maxBytes || rules.MAX_FILE_BYTES;
  const b64 = String(base64);
  if (b64.length > MAX_BASE64_CHARS) return { ok: false, problem: sizeProblem(0) };
  if (!/^[A-Za-z0-9+/]*={0,2}$/.test(b64) || b64.length % 4 !== 0) {
    return { ok: false, problem: problem('parts.base64', 'The file block is damaged (not valid base64). Send it again from ' + SITE + '.') };
  }
  const packed = Buffer.from(b64, 'base64');
  let bytes;
  try {
    bytes = zlib.gunzipSync(packed, { maxOutputLength: maxBytes + 1 });
  } catch (e) {
    if (e && (e.code === 'ERR_BUFFER_TOO_LARGE' || e instanceof RangeError)) return { ok: false, problem: sizeProblem(0) };
    return { ok: false, problem: problem('parts.gzip', 'The file block is damaged (it does not unpack). Send it again from ' + SITE + '.') };
  }
  if (bytes.length > maxBytes) return { ok: false, problem: sizeProblem(bytes.length) };
  const actual = crypto.createHash('sha256').update(bytes).digest('hex');
  if (actual !== String(sha256).toLowerCase()) {
    return { ok: false, problem: problem('parts.sha256', 'The file does not match its checksum (sha256); a part is missing or changed. Send it again from ' + SITE + '.') };
  }
  return { ok: true, bytes };
}

/* ---------- the whole decision ---------- */

/**
 * issue: { title, body, user: { login } }; comments: [{ id, body, user: { login } }] in creation order.
 * Only comments by the issue author count. Returns one of:
 *   { action: 'ignore', reason }
 *   { action: 'wait', have, of }                         way 3, parts still missing
 *   { action: 'fail', kind, code?, problems }
 *   { action: 'download', kind: 'form', code, url, hidden }   way 2: fetch url, then checkSubmission
 *   { action: 'check', kind: 'page', code, bytes, hidden }    way 3: decoded and verified, ready for checkSubmission
 * hidden: the volunteer asked not to be listed among the translators.
 */
function resolveSubmission(issue, comments) {
  const kind = recognise(issue);
  if (!kind) return { action: 'ignore', reason: 'not a translation issue' };
  const author = issue.user && issue.user.login;
  const titleCode = codeFromTitle(issue.title);

  if (kind === 'form') {
    const f = parseFormBody(issue.body);
    const raw = (f.language || '').trim();
    const code = checkCodeInput(raw, titleCode);
    if (!code.ok) return { action: 'fail', kind, problems: code.problems };
    const urls = findUrls(f.file);
    if (!urls.length) return { action: 'fail', kind, code: code.code, problems: [problem('attachment.none', 'No file is attached. Attach your <code>.json file in the "Translation file" field.')] };
    if (urls.some((u) => !isAllowedAttachmentUrl(u))) return { action: 'fail', kind, code: code.code, problems: [attachmentHostProblem()] };
    if (urls.length > 1) return { action: 'fail', kind, code: code.code, problems: [problem('attachment.many', 'Attach exactly one file.')] };
    return { action: 'download', kind, code: code.code, url: urls[0], hidden: listingHidden(kind, issue.body) };
  }

  const code = checkCodeInput(codeFromPageBody(issue.body) || '', titleCode);
  if (!code.ok) return { action: 'fail', kind, problems: code.problems };
  const texts = [{ source: 'body', id: 0, text: issue.body }];
  for (const c of comments || []) {
    if (c && c.user && author && c.user.login === author) texts.push({ source: 'comment', id: c.id, text: c.body });
  }
  const joined = joinParts(extractBlocks(texts));
  if (joined.status === 'error') return { action: 'fail', kind, code: code.code, problems: joined.problems };
  if (joined.status === 'incomplete') return { action: 'wait', have: joined.have, of: joined.of };
  const decoded = decodeParts(joined.base64, joined.sha256);
  if (!decoded.ok) return { action: 'fail', kind, code: code.code, problems: [decoded.problem] };
  return { action: 'check', kind, code: code.code, bytes: decoded.bytes, hidden: listingHidden(kind, issue.body) };
}

// The code from the body/form, cross-checked with the title when the title names one.
function checkCodeInput(raw, titleCode) {
  if (!raw) return { ok: false, problems: [problem('code.format', 'No language code was given.')] };
  const cc = rules.canonicalCode(raw);
  if (!cc.ok) {
    return { ok: false, problems: [rules.codeProblem(cc.reason, raw)] };
  }
  if (titleCode) {
    const tc = rules.canonicalCode(titleCode);
    if (!tc.ok || tc.code !== cc.code) {
      return { ok: false, problems: [problem('code.title', 'The title says ' + rules.show(titleCode, 20) + ' but the language code is ' + rules.show(cc.code) + '. Make them the same.')] };
    }
  }
  return { ok: true, code: cc.code };
}

// The shared rule set on the received file. en: parsed en.json at main.
function checkSubmission(code, bytes, en) {
  return rules.checkFile({ code, bytes, en });
}

/* ---------- pull request plan ---------- */

/**
 * input: { code, login, userId, issueNumber, result (checkSubmission), en (parsed en.json), hidden?, baseBranch? }
 * hidden adds the trailer "Translator-Listing: hidden" after the Co-authored-by line.
 * Returns { branch, path, content, commitMessage, prTitle, prBody, count }.
 * The committed file is normalised to the page's output format (en.json key order, 2-space indent, LF).
 */
function planPullRequest(input) {
  const { code, login, userId, issueNumber, result, en, hidden } = input;
  if (!/^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$/.test(String(login))) throw new Error('unexpected login');
  if (!/^\d+$/.test(String(userId))) throw new Error('unexpected user id');
  const canon = rules.canonicalCode(code);
  if (!canon.ok || canon.code !== code) throw new Error('unexpected code');
  const enKeys = Object.keys(en);
  const content = rules.buildOutput(enKeys, result.values || {});
  const count = result.counts.translated;
  const branch = 'translate/' + code + '-' + login;
  const commitMessage = 'Translation: ' + code + ' (' + count + ' strings)\n\n' +
    'Co-authored-by: ' + login + ' <' + userId + '+' + login + '@users.noreply.github.com>\n' +
    (hidden ? LISTING_TRAILER + '\n' : '');
  const prTitle = 'Translation: ' + code + ' by @' + login;
  const warnings = result.warnings || [];
  const lines = [
    'Translation for `' + code + '` sent by @' + login + ' in #' + issueNumber + '.',
    '',
    '| Check | Result |',
    '| --- | --- |',
    '| Strings | ' + count + ' of ' + result.counts.total + ' |',
    '| Errors | 0 |',
    '| Warnings | ' + warnings.length + ' |',
    ''
  ];
  if (warnings.length) {
    lines.push('<details><summary>Warnings</summary>', '');
    for (const w of warnings.slice(0, 100)) lines.push('- ' + (w.line ? 'line ' + w.line + ': ' : '') + '[' + w.rule + '] ' + mdEscape(w.message));
    if (warnings.length > 100) lines.push('- ... and ' + (warnings.length - 100) + ' more');
    lines.push('', '</details>', '');
  }
  if (hidden) {
    lines.push('The volunteer asked not to be listed among the translators. The commit carries the trailer `' +
      LISTING_TRAILER + '`; keep it when merging.', '');
  }
  lines.push('Opened by the translation intake workflow. A later send from the same account for the same language updates this pull request. Review before merging; it is never merged automatically.');
  return {
    branch,
    path: rules.STRINGS_DIR + '/' + code + '.json',
    content,
    commitMessage,
    prTitle,
    prBody: lines.join('\n') + '\n',
    count
  };
}

function successComment(plan, prUrl, updated) {
  return 'Thank you! Your translation passed the checks (' + plan.count + ' strings) and is in ' + prUrl +
    (updated ? ' (updated)' : '') + '. The repo owner reviews it there. This issue is now closed; to send a newer version, ' +
    'use ' + SITE + ' again and the same pull request is updated.\n';
}

function unchangedComment(code) {
  return 'Thank you! This `' + code + '` file is identical to the one already in the repository, so there is nothing to add. Closing this issue.\n';
}

// Comment for a submission that does not pass: the first 30 problems, with key names and lines.
function failureComment(problems, limit) {
  limit = limit || MAX_PROBLEMS_IN_COMMENT;
  const errors = problems.filter((p) => p.level !== 'warning');
  const lines = ['The translation could not be accepted yet. Please fix ' + (errors.length === 1 ? 'this problem' : 'these problems') + ', then edit this issue or send the file again:', ''];
  for (const p of errors.slice(0, limit)) {
    lines.push('- ' + (p.line ? 'line ' + p.line + ': ' : '') + '[' + p.rule + '] ' + mdEscape(p.message));
  }
  if (errors.length > limit) lines.push('- ... and ' + (errors.length - limit) + ' more');
  lines.push('', 'The translation page at https://' + SITE + '/ shows the same checks while you translate.');
  return lines.join('\n') + '\n';
}

module.exports = {
  MARKER,
  FORM_HEADINGS,
  HIDE_LABEL,
  LISTING_TRAILER,
  TITLE_PREFIX,
  MAX_PARTS,
  MAX_PROBLEMS_IN_COMMENT,
  recognise,
  hasFormHeadings,
  parseFormBody,
  codeFromTitle,
  codeFromPageBody,
  listingFromPageBody,
  hideTickedInForm,
  listingHidden,
  findUrls,
  isAllowedAttachmentUrl,
  isAllowedRedirect,
  download,
  extractBlocks,
  joinParts,
  decodeParts,
  resolveSubmission,
  checkCodeInput,
  checkSubmission,
  planPullRequest,
  successComment,
  unchangedComment,
  failureComment
};
