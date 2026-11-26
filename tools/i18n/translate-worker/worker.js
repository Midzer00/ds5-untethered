/* "Send with GitHub" Worker for ds5untethered.com/translate (verified-identity sending). Cloudflare Worker.
 * It logs nothing (no console calls; observability is off in wrangler.toml) and returns errors as names only.
 *
 *   POST /submit   body: JSON sent as text/plain, at most 1.5 MB
 *     { code, code_verifier, redirect_uri, title, body, comments?: [..], listing?: "shown"|"hidden" }
 *     -> 200 { issue_url, issue_number }   |   4xx/5xx { error: <name> }
 *
 *   Error names (the page maps exactly these): origin, rate_limited, too_large, too_many_parts, bad_request,
 *   bad_json, bad_code, bad_redirect, bad_title, bad_body, bad_marker, bad_part, bad_listing, bad_verification_code,
 *   exchange_failed, user_failed, github, installation, issue_failed, comment_failed.
 *   A code that was already used (a retry after a lost reply) gets bad_verification_code from GitHub and opens
 *   nothing.
 *
 *   1. Checks the request: Origin, sizes, the number of fence openers, the callback address (an allowlist), title,
 *      marker, language line, the fenced file parts (submission.js).
 *   2. Finds out who the volunteer is: exchanges the code with the OAuth App (client secret + PKCE + the callback
 *      address the page used), reads GET /user, then deletes the whole grant at once, whatever happens next, so the
 *      next sign-in shows GitHub's consent screen again. The token never leaves the Worker.
 *   3. Rate-limits the volunteer by user id: a burst limit (Workers rate limiting binding) and, when the SUBMIT_LOG
 *      KV namespace is bound, at most 5 sends per id per rolling hour (refused when the store fails). Plus the per-IP
 *      limit before anything else and an optional site-wide limit (GLOBAL_LIMITER). All limits are approximate.
 *   4. Mints a GitHub App installation token (RS256 JWT, WebCrypto), scoped to the repository and issues:write.
 *   5. Opens the issue as the App's bot. Title and body are REBUILT from the checked values (marker, verified
 *      "**Sender:**" line, listing, language, numbers, the parts); nothing the volunteer typed is passed through.
 *      Then the comments (each exactly one rebuilt part) in order. The installation token is revoked at the end.
 *
 * Settings:
 *   OAUTH_CLIENT_ID      [vars]   the OAuth App's Client ID (public)
 *   APP_ID               [vars]   the GitHub App's App ID (public)
 *   REPO                 [vars]   owner/name of the repository the App is installed on
 *   DEV                  [vars]   "true" also allows http://localhost:8765 (local testing only)
 *   OAUTH_CLIENT_SECRET  secret   the OAuth App's client secret
 *   APP_PRIVATE_KEY      secret   the GitHub App's private key, PEM (PKCS#1 as GitHub gives it, or PKCS#8)
 *   LIMITER              binding  rate limiting, keyed by client IP
 *   USER_LIMITER         binding  rate limiting, keyed by GitHub user id
 *   GLOBAL_LIMITER       binding  optional; rate limiting with one key for the whole Worker (verified sends only)
 *   SUBMIT_LOG           KV       optional; send times per user id, kept for one hour, for the hourly cap */

import { checkSubmission, issueBody } from './submission.js';
import { appJwt } from './app-auth.js';

const SITE = 'https://ds5untethered.com';
const DEV_ORIGIN = 'http://localhost:8765';
const MAX_REQUEST_BYTES = 1536 * 1024;
const OAUTH_TOKEN_RE = /^[A-Za-z0-9_]{20,255}$/;
const LOGIN_RE = /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$/;
const REPO_RE = /^([A-Za-z0-9-]{1,39})\/([A-Za-z0-9._-]{1,100})$/;
const HOURLY_LIMIT = 5;
const HOUR = 3600;
const COMMENT_DELAY_MS = 1000;   // GitHub asks for a pause between content-creating requests
const UA = 'ds5-translate-auth';
const API = 'https://api.github.com';

function isDev(env) {
  return String(env.DEV || '').toLowerCase() === 'true';
}

function allowedOrigin(req, env) {
  const origin = req.headers.get('Origin');
  if (origin === SITE) return origin;
  if (origin === DEV_ORIGIN && isDev(env)) return origin;
  return null;
}

function reply(status, body, origin) {
  const h = { 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', 'Referrer-Policy': 'no-referrer' };
  if (origin) { h['Access-Control-Allow-Origin'] = origin; h['Vary'] = 'Origin'; }
  h['Content-Type'] = 'application/json';
  return new Response(JSON.stringify(body), { status, headers: h });
}

// A failure with a name: the only thing that ever goes back to the page.
class Stop extends Error {
  constructor(status, name) { super(name); this.status = status; this.name = name; }
}

// Reads at most MAX_REQUEST_BYTES, whatever Content-Length says. Returns the parsed object, or throws Stop.
async function readJson(req) {
  const len = Number(req.headers.get('Content-Length') || 0);
  if (len > MAX_REQUEST_BYTES) throw new Stop(413, 'too_large');
  if (!req.body) throw new Stop(400, 'bad_json');
  const reader = req.body.getReader();
  const chunks = [];
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.byteLength;
    if (size > MAX_REQUEST_BYTES) {
      try { await reader.cancel(); } catch { /* gone */ }
      throw new Stop(413, 'too_large');
    }
    chunks.push(value);
  }
  const bytes = new Uint8Array(size);
  let at = 0;
  for (const c of chunks) { bytes.set(c, at); at += c.byteLength; }
  try {
    return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  } catch {
    throw new Stop(400, 'bad_json');
  }
}

async function call(url, init) {
  try {
    return await fetch(url, init);
  } catch {
    throw new Stop(502, 'github');
  }
}

async function jsonOf(res) {
  try { return await res.json(); } catch { return null; }
}

function apiHeaders(auth, withBody) {
  const h = { Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': UA, Authorization: auth };
  if (withBody) h['Content-Type'] = 'application/json';
  return h;
}

/* ---------- 2. who is sending ---------- */

async function exchangeCode(env, sub) {
  const form = {
    client_id: env.OAUTH_CLIENT_ID,
    client_secret: env.OAUTH_CLIENT_SECRET,
    code: sub.code,
    code_verifier: sub.codeVerifier,
    // One of the allowlisted callback pages (submission.js). GitHub refuses the exchange when it differs from the
    // authorize request's, so a code delivered to any other page cannot be used here.
    redirect_uri: sub.redirectUri
  };
  const res = await call('https://github.com/login/oauth/access_token', {
    method: 'POST',
    headers: { Accept: 'application/json', 'Content-Type': 'application/x-www-form-urlencoded', 'User-Agent': UA },
    body: new URLSearchParams(form).toString()
  });
  const j = await jsonOf(res);
  if (res.ok && j && typeof j.access_token === 'string' && OAUTH_TOKEN_RE.test(j.access_token)) return j.access_token;
  // GitHub answers 200 with { error } for a bad, expired or already used code (or a wrong verifier). Only that name is
  // passed on; any other answer (wrong client credentials, for one) is exchange_failed.
  throw new Stop(400, j && j.error === 'bad_verification_code' ? 'bad_verification_code' : 'exchange_failed');
}

// Deletes the whole authorization (the grant and every token of it), not only this token: no grant persists
// silently, so every sign-in shows the consent screen and a sign-in link from elsewhere cannot pass unnoticed.
async function revokeGrant(env, token) {
  try {
    await fetch(API + '/applications/' + encodeURIComponent(env.OAUTH_CLIENT_ID) + '/grant', {
      method: 'DELETE',
      headers: apiHeaders('Basic ' + btoa(env.OAUTH_CLIENT_ID + ':' + env.OAUTH_CLIENT_SECRET), true),
      body: JSON.stringify({ access_token: token })
    });
  } catch { /* the token was never stored or returned; nothing else to do */ }
}

async function identify(env, sub) {
  const token = await exchangeCode(env, sub);
  try {
    const res = await call(API + '/user', { headers: apiHeaders('Bearer ' + token, false) });
    const u = res.ok ? await jsonOf(res) : null;
    if (!u || u.type !== 'User' || typeof u.login !== 'string' || !LOGIN_RE.test(u.login) ||
        !Number.isSafeInteger(u.id) || u.id < 1) {
      throw new Stop(502, 'user_failed');
    }
    return { login: u.login, id: u.id };
  } finally {
    await revokeGrant(env, token);
  }
}

/* ---------- 3. per-volunteer limits ---------- */

async function limitUser(env, userId) {
  if (env.USER_LIMITER) {
    let ok = true;
    try { ok = (await env.USER_LIMITER.limit({ key: 'user:' + userId })).success; } catch { /* unavailable */ }
    if (!ok) throw new Stop(429, 'rate_limited');
  }
  if (env.SUBMIT_LOG) {
    // The store is configured: when it cannot be read or written the cap cannot be kept, so the send is refused
    // (fail closed). KV is eventually consistent and per location, so the cap is approximate, never exact.
    const key = 'u:' + userId;
    const now = Math.floor(Date.now() / 1000);
    let raw;
    try { raw = await env.SUBMIT_LOG.get(key); } catch { throw new Stop(429, 'rate_limited'); }
    let times = [];
    try {
      const parsed = raw ? JSON.parse(raw) : [];
      if (Array.isArray(parsed)) times = parsed.filter((t) => Number.isInteger(t) && t > now - HOUR);
    } catch { times = []; }   // an unreadable entry is replaced below
    if (times.length >= HOURLY_LIMIT) throw new Stop(429, 'rate_limited');
    times.push(now);
    try { await env.SUBMIT_LOG.put(key, JSON.stringify(times), { expirationTtl: HOUR }); } catch { throw new Stop(429, 'rate_limited'); }
  }
  // Site-wide cap on verified sends (optional binding): bounds what many accounts together can open per minute.
  if (env.GLOBAL_LIMITER) {
    let ok = true;
    try { ok = (await env.GLOBAL_LIMITER.limit({ key: 'all' })).success; } catch { /* unavailable */ }
    if (!ok) throw new Stop(429, 'rate_limited');
  }
}

/* ---------- 4. the App's installation token ---------- */

async function installationToken(env, owner, name) {
  let jwt;
  // A bad private key, a wrong App ID, the App not installed or the token refused: all "installation".
  try { jwt = await appJwt(env.APP_ID, env.APP_PRIVATE_KEY); } catch { throw new Stop(500, 'installation'); }
  const inst = await call(API + '/repos/' + owner + '/' + name + '/installation', { headers: apiHeaders('Bearer ' + jwt, false) });
  const ij = inst.ok ? await jsonOf(inst) : null;
  if (!ij || !Number.isSafeInteger(ij.id)) throw new Stop(502, 'installation');
  const res = await call(API + '/app/installations/' + ij.id + '/access_tokens', {
    method: 'POST',
    headers: apiHeaders('Bearer ' + jwt, true),
    body: JSON.stringify({ repositories: [name], permissions: { issues: 'write' } })
  });
  const tj = res.status === 201 || res.ok ? await jsonOf(res) : null;
  if (!tj || typeof tj.token !== 'string' || !tj.token) throw new Stop(502, 'installation');
  return tj.token;
}

async function revokeInstallationToken(token) {
  try {
    await fetch(API + '/installation/token', { method: 'DELETE', headers: apiHeaders('Bearer ' + token, false) });
  } catch { /* it expires within the hour anyway */ }
}

/* ---------- 5. the issue ---------- */

const pause = (ms) => (ms > 0 ? new Promise((r) => setTimeout(r, ms)) : Promise.resolve());

async function openIssue(env, owner, name, token, sub, user) {
  const base = API + '/repos/' + owner + '/' + name + '/issues';
  const res = await call(base, {
    method: 'POST',
    headers: apiHeaders('Bearer ' + token, true),
    body: JSON.stringify({ title: sub.title, body: issueBody(sub, user) })
  });
  const issue = res.status === 201 || res.ok ? await jsonOf(res) : null;
  if (!issue || !Number.isSafeInteger(issue.number) || typeof issue.html_url !== 'string') throw new Stop(502, 'issue_failed');

  const delay = env.COMMENT_DELAY_MS != null ? Number(env.COMMENT_DELAY_MS) : COMMENT_DELAY_MS;
  for (const body of sub.comments) {
    await pause(delay);
    let ok = false;
    try {
      const c = await fetch(base + '/' + issue.number + '/comments', {
        method: 'POST', headers: apiHeaders('Bearer ' + token, true), body: JSON.stringify({ body })
      });
      ok = c.status === 201 || c.ok;
    } catch { ok = false; }
    if (!ok) {
      // An issue with parts missing would wait forever; close it so the intake leaves it alone.
      try {
        await fetch(base + '/' + issue.number, {
          method: 'PATCH', headers: apiHeaders('Bearer ' + token, true),
          body: JSON.stringify({ state: 'closed', state_reason: 'not_planned' })
        });
      } catch { /* left open; the intake only waits for parts and posts nothing */ }
      throw new Stop(502, 'comment_failed');
    }
  }
  return { issue_url: issue.html_url, issue_number: issue.number };
}

/* ---------- the request ---------- */

function configured(env) {
  return !!(env.OAUTH_CLIENT_ID && env.OAUTH_CLIENT_SECRET && env.APP_ID && env.APP_PRIVATE_KEY &&
    REPO_RE.test(String(env.REPO || '')));
}

async function submit(req, env, origin) {
  const checked = checkSubmission(await readJson(req), { dev: isDev(env) });
  if (!checked.ok) throw new Stop(checked.error === 'too_large' ? 413 : 400, checked.error);
  const user = await identify(env, checked);
  await limitUser(env, user.id);
  const [, owner, name] = REPO_RE.exec(env.REPO);
  const token = await installationToken(env, owner, name);
  try {
    return reply(200, await openIssue(env, owner, name, token, checked, user), origin);
  } finally {
    await revokeInstallationToken(token);
  }
}

export default {
  async fetch(req, env) {
    const url = new URL(req.url);
    const origin = allowedOrigin(req, env);

    if (req.method === 'OPTIONS') {
      if (!origin) return reply(403, { error: 'origin' }, null);
      return new Response(null, {
        status: 204,
        headers: {
          'Access-Control-Allow-Origin': origin, Vary: 'Origin',
          'Access-Control-Allow-Methods': 'POST',
          'Access-Control-Allow-Headers': 'Content-Type',
          'Access-Control-Max-Age': '86400'
        }
      });
    }
    if (req.method !== 'POST') return reply(405, { error: 'bad_request' }, origin);
    // Browsers always send Origin on a cross-origin POST; other sites' pages are refused here.
    if (!origin) return reply(403, { error: 'origin' }, null);
    if (url.pathname !== '/submit') return reply(404, { error: 'bad_request' }, origin);
    // A missing value or secret on the Worker: the page can only say that GitHub sending is not working.
    if (!configured(env)) return reply(500, { error: 'github' }, origin);

    if (env.LIMITER) {
      const ip = req.headers.get('CF-Connecting-IP') || 'unknown';
      let ok = true;
      try { ok = (await env.LIMITER.limit({ key: 'submit:' + ip })).success; } catch { /* unavailable: carry on */ }
      if (!ok) return reply(429, { error: 'rate_limited' }, origin);
    }
    try {
      return await submit(req, env, origin);
    } catch (e) {
      if (e instanceof Stop) return reply(e.status, { error: e.name }, origin);
      return reply(500, { error: 'github' }, origin);
    }
  }
};
