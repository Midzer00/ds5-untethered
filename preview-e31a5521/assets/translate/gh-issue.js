/* Send with GitHub (verified-identity sending): the volunteer confirms who they are with the project's OAuth App
   in a popup, and the Worker opens the issue as the project's GitHub App bot, naming the verified sender. The
   repo's intake Action turns that issue into a pull request with the volunteer as the commit author.

   - The OAuth App asks for no scope ("Public data only"). The page never holds a GitHub token: it hands the
     one-time code and its PKCE verifier to the Worker's POST /submit with the issue, and gets back the issue link.
   - The client secret and the GitHub App's private key live only in the Worker. This file holds the public
     OAuth client id, read from config.js.
   - The issue follows the shared contract: marker, gzip + base64 parts under 60000 characters per body,
     sha256 of the uncompressed file. Pure helpers take no DOM, so Node tests drive them. */
(function (root, factory) {
  var api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.GhIssue = api;
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  var AUTHORIZE = 'https://github.com/login/oauth/authorize';
  var MARKER = '<!-- ds5-translate v1 -->';
  var LIMIT = 60000;            // every issue body and comment stays under this many characters
  var LINE = 76;                // base64 line width
  var MAX_FILE = 1024 * 1024;   // the intake Action refuses bigger files
  var CHANNEL = 'ds5-gh-oauth';
  var PENDING_KEY = 'ds5-gh-oauth';          // sessionStorage, same-tab fallback only
  var RESULT_KEY = 'ds5-gh-oauth-result';    // sessionStorage (same tab) or a localStorage ping (popup)
  var RETRY_GAP = 1500;         // the pause before the one retry of a /submit that never reached the Worker

  function err(code, extra) { var e = new Error(code); e.code = code; if (extra) for (var k in extra) e[k] = extra[k]; return e; }

  /* ---------- bytes ---------- */

  function utf8(s) { return new TextEncoder().encode(s); }
  function bytesToB64(bytes) {
    var s = '';
    for (var i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    return btoa(s);
  }
  function b64ToBytes(b64) {
    var bin = atob(String(b64).replace(/\s/g, '')), bytes = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return bytes;
  }
  function b64url(bytes) { return bytesToB64(bytes).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); }
  function hex(buf) {
    var a = new Uint8Array(buf), s = '';
    for (var i = 0; i < a.length; i++) s += (a[i] < 16 ? '0' : '') + a[i].toString(16);
    return s;
  }
  function sha256Hex(bytes) { return crypto.subtle.digest('SHA-256', bytes).then(hex); }
  function pipe(bytes, stream) {
    return new Response(new Blob([bytes]).stream().pipeThrough(stream)).arrayBuffer().then(function (b) { return new Uint8Array(b); });
  }
  function gzip(bytes) { return pipe(bytes, new CompressionStream('gzip')); }
  function gunzip(bytes) { return pipe(bytes, new DecompressionStream('gzip')); }
  function supported(W) {
    W = W || (typeof window !== 'undefined' ? window : null);
    return !!(W && W.fetch && W.crypto && W.crypto.subtle && W.CompressionStream && W.TextEncoder && W.Promise);
  }

  /* ---------- the issue ---------- */

  function issueTitle(code) { return 'Translation: ' + code; }
  // The language name is the volunteer's own text: one line, no markup characters.
  function cleanName(s) {
    return String(s == null ? '' : s).replace(/[\u0000-\u001f\u007f<>`*_[\]\\|#]/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 80);
  }
  function wrap(s, n) {
    var out = [];
    for (var i = 0; i < s.length; i += n) out.push(s.slice(i, i + n));
    return out.join('\n');
  }
  function fenceOpen(k, n, sha) { return '```ds5-translate part=' + k + '/' + n + ' encoding=gzip+base64 sha256=' + sha; }
  function fence(k, n, sha, chunk) { return fenceOpen(k, n, sha) + '\n' + wrap(chunk, LINE) + '\n```'; }
  function head(info) {
    return MARKER + '\n' +
      '**Language:** ' + info.code + ' (' + (cleanName(info.name) || info.code) + ')\n' +
      '**Strings:** ' + info.translated + ' of ' + info.total + ' · sent from ds5untethered.com/translate\n' +
      (info.listing === 'hidden' ? '**Listing:** hidden\n' : '') + '\n' +
      '<details><summary>Translation file (compressed)</summary>\n\n';
  }
  var TAIL = '\n</details>\n';

  // Splits the base64 over the issue body (part 1) and as many comments as needed, each under LIMIT.
  // A part holds whole 76-character lines, so only the last line of the last part is short.
  function layout(info, b64, sha) {
    var h = head(info);
    for (var n = 1; n < 10000; n++) {
      var chunks = [], pos = 0;
      for (var k = 1; k <= n; k++) {
        var over = fenceOpen(k, n, sha).length + 5 + (k === 1 ? h.length + TAIL.length : 0);
        var cap = Math.floor((LIMIT - over) / (LINE + 1)) * LINE;
        if (cap <= 0) throw err('layout');
        chunks.push(b64.slice(pos, pos + cap));
        pos += cap;
      }
      if (pos < b64.length || (n > 1 && !chunks[n - 1].length)) continue;
      return {
        body: h + fence(1, n, sha, chunks[0]) + TAIL,
        comments: chunks.slice(1).map(function (c, i) { return fence(i + 2, n, sha, c); })
      };
    }
    throw err('layout');
  }

  /* info: { code, name, translated, total, text, listing }. listing 'hidden' adds the line **Listing:** hidden under
     the header (the volunteer asked not to be listed among the translators); anything else adds nothing (shown).
     Resolves to { title, body, comments, sha256, bytes, parts }. */
  function buildIssue(info) {
    var bytes = utf8(info.text);
    if (bytes.length > MAX_FILE) return Promise.reject(err('tooBig', { bytes: bytes.length }));
    return Promise.all([sha256Hex(bytes), gzip(bytes)]).then(function (r) {
      var sha = r[0], L = layout(info, bytesToB64(r[1]), sha);
      return { title: issueTitle(info.code), body: L.body, comments: L.comments, sha256: sha, bytes: bytes.length, parts: 1 + L.comments.length };
    });
  }

  // The reverse, as the intake Action does it: join the parts in order, decode, gunzip, check the sha256.
  // texts: the issue body followed by its comments. Resolves to { text, sha256 } or rejects.
  var FENCE_RE = /```ds5-translate part=(\d+)\/(\d+) encoding=gzip\+base64 sha256=([0-9a-f]{64})\n([A-Za-z0-9+/=\n]*?)\n?```/g;
  function decodeIssue(texts) {
    return Promise.resolve().then(function () { return decodeParts(texts); });
  }
  function decodeParts(texts) {
    var parts = {}, n = 0, sha = null;
    texts.forEach(function (t) {
      var m;
      FENCE_RE.lastIndex = 0;
      while ((m = FENCE_RE.exec(String(t)))) {
        var k = +m[1], total = +m[2];
        if (n && total !== n) throw err('partCount');
        if (sha && m[3] !== sha) throw err('partSha');
        n = total; sha = m[3]; parts[k] = m[4];
      }
    });
    if (!n) throw err('noParts');
    var b64 = '';
    for (var k = 1; k <= n; k++) {
      if (parts[k] == null) throw err('missingPart', { part: k });
      b64 += parts[k];
    }
    return gunzip(b64ToBytes(b64)).then(function (bytes) {
      return sha256Hex(bytes).then(function (got) {
        if (got !== sha) throw err('shaMismatch');
        return { text: new TextDecoder().decode(bytes), sha256: got };
      });
    });
  }

  /* ---------- the Worker's POST /submit ---------- */

  /* The request body, exactly as the Worker reads it. issue: buildIssue() result. listing: 'hidden' or 'shown'.
     The body leaves out the **Listing:** line (build it with listing 'shown'): the Worker adds that line under
     its verified **Sender:** line when listing is hidden. redirectUri: the callback page this sign-in used; the
     Worker accepts only the known callback pages and sends it to GitHub with the code. */
  function submitBody(o) {
    var iss = o.issue || {};
    return {
      code: String(o.code || ''),
      code_verifier: String(o.verifier || ''),
      redirect_uri: String(o.redirectUri || ''),
      title: iss.title,
      body: iss.body,
      comments: (iss.comments || []).slice(),
      listing: o.listing === 'hidden' ? 'hidden' : 'shown'
    };
  }

  /* The Worker answers errors as { error: <name> } with an HTTP status. The page shows one short message per kind:
       busy      rate limited (per account or per address)
       tooBig    over the Worker's size caps
       origin    this page's address is not allowed to send
       signIn    GitHub did not confirm the volunteer (bad, used or expired code, wrong verifier, /user failed)
       rejected  the request itself was refused (title, marker, part fences, listing, JSON)
       github    GitHub refused the bot (installation, token, issue or comment)
       other     anything else
     submit() adds workerNetwork (no answer, even after the one retry) and maybeSent (the retry's code was refused,
     so the first try may have reached GitHub). Known names map directly; an unknown name is matched by its words, then by the status. */
  var WORKER_ERRORS = {
    rate_limited: 'busy', rate_limit: 'busy', too_many_requests: 'busy', busy: 'busy',
    too_large: 'tooBig', payload_too_large: 'tooBig', too_many_parts: 'tooBig', body_too_long: 'tooBig', comment_too_long: 'tooBig',
    origin: 'origin', bad_origin: 'origin', forbidden_origin: 'origin', forbidden: 'origin',
    bad_code: 'signIn', bad_verification_code: 'signIn', code: 'signIn', exchange: 'signIn', exchange_failed: 'signIn',
    oauth: 'signIn', user: 'signIn', user_failed: 'signIn', identity: 'signIn', unverified: 'signIn',
    bad_request: 'rejected', bad_json: 'rejected', bad_redirect: 'rejected', bad_title: 'rejected', bad_body: 'rejected', bad_marker: 'rejected',
    bad_part: 'rejected', bad_parts: 'rejected', bad_fence: 'rejected', bad_listing: 'rejected', bad_comments: 'rejected',
    method: 'rejected', not_found: 'rejected',
    github: 'github', installation: 'github', install: 'github', app_token: 'github', installation_token: 'github',
    issue: 'github', issue_failed: 'github', comment_failed: 'github', upstream: 'github'
  };
  function workerError(status, name) {
    var n = String(name == null ? '' : name).toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '');
    if (n && Object.prototype.hasOwnProperty.call(WORKER_ERRORS, n)) return WORKER_ERRORS[n];
    if (status === 429 || /rate|limit|busy/.test(n)) return 'busy';
    if (status === 413 || /too_?(large|big|long|many)|size/.test(n)) return 'tooBig';
    if (/origin/.test(n)) return 'origin';
    if (/install|app_|jwt|github|issue|upstream/.test(n)) return 'github';
    if (/code|verifier|exchange|oauth|user|identity|login|expired|denied/.test(n)) return 'signIn';
    if (/title|body|part|fence|marker|json|request|invalid|listing|comment/.test(n)) return 'rejected';
    if (status === 401) return 'signIn';
    if (status === 403) return 'origin';
    if (status === 400 || status === 422) return 'rejected';
    if (status >= 500) return 'github';
    return 'other';
  }

  var ISSUE_URL = /^https:\/\/github\.com\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\/issues\/[1-9][0-9]*$/;

  /* POSTs the issue to <workerUrl>/submit. o: { fetch, workerUrl, code, verifier, redirectUri, issue, listing, sleep,
     retries }.
     A request that never reached the Worker (fetch rejects) is tried once more after a short pause; the code is
     single-use, so the Worker refuses a second use of one it already took and no issue is ever made twice.
     Resolves to { url, number }; rejects with code workerNetwork or one of workerError()'s kinds. */
  function submit(o) {
    var url = String(o.workerUrl || '').replace(/\/+$/, '') + '/submit';
    var payload = JSON.stringify(submitBody(o));
    var sleep = o.sleep || function (ms) { return new Promise(function (r) { setTimeout(r, ms); }); };
    var left = o.retries == null ? 1 : o.retries, retried = false;
    function attempt() {
      return o.fetch(url, {
        method: 'POST',
        // text/plain keeps this a simple CORS request: no preflight round trip
        headers: { 'Content-Type': 'text/plain;charset=UTF-8' },
        body: payload,
        credentials: 'omit', cache: 'no-store', referrerPolicy: 'no-referrer'
      }).then(null, function () {
        if (left-- > 0) { retried = true; return sleep(RETRY_GAP).then(attempt); }
        throw err('workerNetwork');
      });
    }
    return attempt().then(function (r) {
      return r.text().catch(function () { return ''; }).then(function (t) {
        var j = null;
        try { j = t ? JSON.parse(t) : null; } catch (e) { j = null; }
        if (!r.ok || !j || j.error) {
          var name = j && typeof j.error === 'string' ? j.error : '';
          var kind = workerError(r.status, name);
          // after a dropped first try, a refused code may mean the first try went through
          if (retried && kind === 'signIn') kind = 'maybeSent';
          throw err(kind, { status: r.status, detail: name });
        }
        var n = j.issue_number, u = j.issue_url;
        if (typeof u !== 'string' || !ISSUE_URL.test(u) || !(n === +n && n > 0 && Math.floor(n) === n)) throw err('other', { status: r.status, detail: 'reply' });
        return { url: u, number: n };
      });
    });
  }

  /* ---------- authorization (browser) ---------- */

  // The OAuth App's authorize page. No scope: GitHub shows "Public data only" and grants nothing beyond it.
  function authorizeUrl(o) {
    var q = new URLSearchParams({
      client_id: o.clientId, redirect_uri: o.redirectUri, state: o.state,
      code_challenge: o.challenge, code_challenge_method: 'S256', allow_signup: 'true'
    });
    return AUTHORIZE + '?' + q.toString();
  }

  /* cfg: { clientId, workerUrl, redirectUri }. window/fetch are injection points for tests. */
  function createAuth(cfg) {
    var W = cfg.window || (typeof window !== 'undefined' ? window : null);
    var fetchFn = cfg.fetch || (W && W.fetch && W.fetch.bind(W));
    var waiting = null;

    function randomB64(n) { var a = new Uint8Array(n); W.crypto.getRandomValues(a); return b64url(a); }
    function challengeOf(verifier) {
      return W.crypto.subtle.digest('SHA-256', utf8(verifier)).then(function (d) { return b64url(new Uint8Array(d)); });
    }
    function session() { try { return W.sessionStorage; } catch (e) { return null; } }

    /* Must run inside the click handler: the popup opens before anything async happens.
       extra: kept with the same-tab fallback, handed back by resume(). Resolves to { code, verifier }. */
    function authorize(extra) {
      cancel();
      var popup = null;
      try { popup = W.open('', 'ds5-gh-auth', 'popup,width=560,height=760'); } catch (e) { popup = null; }
      var state = randomB64(24), verifier = randomB64(48);
      return challengeOf(verifier).then(function (challenge) {
        var url = authorizeUrl({ clientId: cfg.clientId, redirectUri: cfg.redirectUri, state: state, challenge: challenge });
        if (!popup || popup.closed) {
          // Popup blocked: same-tab redirect. The draft is already stored on every keystroke, so leaving is safe.
          var ss = session();
          if (!ss) throw err('popupBlocked');
          ss.setItem(PENDING_KEY, JSON.stringify({ mode: 'redirect', state: state, verifier: verifier, returnTo: W.location.href, extra: extra || null, at: Date.now() }));
          W.location.assign(url);
          return new Promise(function () { /* the page unloads */ });
        }
        try { popup.location.href = url; } catch (e) { throw err('popupBlocked'); }
        return waitForCode(state).then(function (code) { return { code: code, verifier: verifier }; });
      });
    }

    function waitForCode(state) {
      return new Promise(function (resolve, reject) {
        var bc = null, timer = 0;
        function done(fn, v) { cleanup(); fn(v); }
        function onMsg(m) {
          if (!m || m.type !== CHANNEL || m.state !== state) return;   // not ours: keep waiting
          if (m.error || !m.code) done(reject, err(m.error === 'access_denied' ? 'denied' : 'authorize', { detail: m.error || '' }));
          else done(resolve, m.code);
        }
        function onStorage(e) {
          if (e.key !== RESULT_KEY || !e.newValue) return;
          try { onMsg(JSON.parse(e.newValue)); } catch (x) { /* ignore */ }
        }
        function cleanup() {
          clearTimeout(timer);
          if (bc) { try { bc.close(); } catch (e) { /* closed */ } }
          W.removeEventListener('storage', onStorage);
          waiting = null;
        }
        try { bc = new W.BroadcastChannel(CHANNEL); bc.onmessage = function (e) { onMsg(e.data); }; } catch (e) { bc = null; }
        W.addEventListener('storage', onStorage);
        timer = setTimeout(function () { done(reject, err('timeout')); }, 10 * 60 * 1000);
        waiting = { cancel: function () { done(reject, err('cancelled')); } };
      });
    }
    function cancel() { if (waiting) waiting.cancel(); }

    /* Same-tab fallback: call once on page load. Resolves to { code, verifier, extra } when an authorization just
       came back, else null. */
    function resume() {
      var ss = session();
      if (!ss) return Promise.resolve(null);
      var p, r;
      try { p = JSON.parse(ss.getItem(PENDING_KEY) || 'null'); r = JSON.parse(ss.getItem(RESULT_KEY) || 'null'); } catch (e) { p = r = null; }
      if (!p || !r) return Promise.resolve(null);
      ss.removeItem(PENDING_KEY); ss.removeItem(RESULT_KEY);
      var extra = p.extra || null;
      if (r.state !== p.state || Date.now() - p.at > 15 * 60 * 1000) return Promise.reject(err('state', { extra: extra }));
      if (r.error || !r.code) return Promise.reject(err(r.error === 'access_denied' ? 'denied' : 'authorize', { extra: extra }));
      return Promise.resolve({ code: r.code, verifier: p.verifier, extra: extra });
    }

    return {
      authorize: authorize,
      cancel: cancel,
      resume: resume,
      // o: { code, verifier, issue, listing }
      submit: function (o) {
        return submit({ fetch: fetchFn, workerUrl: cfg.workerUrl, code: o.code, verifier: o.verifier, redirectUri: cfg.redirectUri, issue: o.issue, listing: o.listing });
      }
    };
  }

  return {
    buildIssue: buildIssue, decodeIssue: decodeIssue, layout: layout, issueTitle: issueTitle, cleanName: cleanName,
    submitBody: submitBody, submit: submit, workerError: workerError,
    createAuth: createAuth, authorizeUrl: authorizeUrl, supported: supported,
    utf8: utf8, bytesToB64: bytesToB64, b64ToBytes: b64ToBytes, b64url: b64url, sha256Hex: sha256Hex, gzip: gzip, gunzip: gunzip,
    MARKER: MARKER, LIMIT: LIMIT, LINE: LINE, MAX_FILE: MAX_FILE, CHANNEL: CHANNEL, PENDING_KEY: PENDING_KEY, RESULT_KEY: RESULT_KEY
  };
});
