/* Send with GitHub: the volunteer authorizes the project's GitHub App in a popup, and this page opens one issue
   in the volunteer's name. The repo's intake Action turns that issue into a pull request.

   - The GitHub App has a single repository permission, Issues: Read and write, and is installed only on the
     project's repository. Its user token can therefore only open and read issues there.
   - The client secret lives only in the Worker (code exchange and revocation). This file holds the public
     client id, read from config.js.
   - The token lives only in this module's closure: never in storage, a URL or a cookie. It is revoked as soon
     as the issue is complete, and on the way out of the page.
   - The issue follows the shared contract: marker, gzip + base64 parts under 60000 characters per body,
     sha256 of the uncompressed file. Pure helpers take no DOM, so Node tests drive them. */
(function (root, factory) {
  var api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.GhIssue = api;
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  var API = 'https://api.github.com';
  var AUTHORIZE = 'https://github.com/login/oauth/authorize';
  var MARKER = '<!-- ds5-translate v1 -->';
  var LIMIT = 60000;            // every issue body and comment stays under this many characters
  var LINE = 76;                // base64 line width
  var MAX_FILE = 1024 * 1024;   // the intake Action refuses bigger files
  var CHANNEL = 'ds5-gh-oauth';
  var PENDING_KEY = 'ds5-gh-oauth';          // sessionStorage, same-tab fallback only
  var RESULT_KEY = 'ds5-gh-oauth-result';    // sessionStorage (same tab) or a localStorage ping (popup)
  var COMMENT_GAP = 1000;       // GitHub asks for a pause between content-creating requests

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

  /* ---------- GitHub REST client ---------- */

  // token null: an anonymous request (public data only)
  function client(fetchFn, token) {
    function call(method, path, body) {
      var headers = { Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28' };
      if (token) headers.Authorization = 'Bearer ' + token;
      if (body !== undefined) headers['Content-Type'] = 'application/json';
      return fetchFn(API + path, { method: method, headers: headers, body: body === undefined ? undefined : JSON.stringify(body), cache: 'no-store', credentials: 'omit', referrerPolicy: 'no-referrer' })
        .then(function (r) {
          return r.text().then(function (t) {
            var j = null;
            try { j = t ? JSON.parse(t) : null; } catch (e) { j = null; }
            if (!r.ok) throw err('http', { status: r.status, message: (j && j.message) || '', path: path });
            return j;
          });
        }, function () { throw err('network', { path: path }); });
    }
    return {
      get: function (p) { return call('GET', p); },
      post: function (p, b) { return call('POST', p, b || {}); }
    };
  }

  /* The intake Action's branch for a volunteer and a language: translate/<numeric user id>/<code>. Built from the id,
     which never changes and holds no "-" or "/", so two accounts or two languages never share a branch. */
  function branchFor(userId, code) {
    if (!/^[1-9][0-9]*$/.test(String(userId))) return null;
    return 'translate/' + String(userId) + '/' + code;
  }

  /* An earlier send from this account for this code that is still open: the issue (titled as ours, opened by this
     login) and the bot's pull request on branch translate/<user id>/<code>, in this repository. A new send updates
     that pull request. Old-format branches (translate/<code>-<login>) are not matches.
     o: { gh, anon, repo, login, userId, code }. Never rejects; a failed lookup just finds nothing. */
  function findExisting(o) {
    var R = '/repos/' + o.repo, title = issueTitle(o.code).toLowerCase(), branch = branchFor(o.userId, o.code);
    var repoName = String(o.repo).toLowerCase();
    var issues = o.gh.get(R + '/issues?state=open&creator=' + encodeURIComponent(o.login) + '&per_page=100').then(function (list) {
      return (list || []).filter(function (i) { return !i.pull_request && String(i.title || '').trim().toLowerCase() === title; })
        .map(function (i) { return { number: i.number, url: i.html_url }; });
    }).catch(function () { return []; });
    function pullsVia(gh) {
      return gh.get(R + '/pulls?state=open&per_page=100').then(function (list) {
        return (list || []).filter(function (p) {
          return p && p.head && p.head.ref === branch &&
            !!p.head.repo && String(p.head.repo.full_name || '').toLowerCase() === repoName;
        }).map(function (p) { return { number: p.number, url: p.html_url }; });
      });
    }
    var pulls = !branch ? Promise.resolve([]) :
      pullsVia(o.gh).catch(function () { return o.anon ? pullsVia(o.anon) : []; }).catch(function () { return []; });
    return Promise.all([issues, pulls]).then(function (r) { return { issues: r[0], pulls: r[1] }; });
  }

  /* Creates the issue, then one comment per further part, a second apart. o:
       gh       client(fetch, token)
       repo     'owner/name'
       issue    buildIssue() result
       state    {} kept by the caller: { number, url, posted } so a retry continues where it stopped
       onStep   (k, n) called before part k of n is sent
       sleep    injectable for tests
     Resolves to { number, url }. */
  function sendIssue(o) {
    var st = o.state, R = '/repos/' + o.repo, iss = o.issue, n = iss.parts;
    var step = o.onStep || function () {};
    var sleep = o.sleep || function (ms) { return new Promise(function (r) { setTimeout(r, ms); }); };
    var first;
    if (st.number) first = Promise.resolve();
    else {
      step(1, n);
      first = o.gh.post(R + '/issues', { title: iss.title, body: iss.body }).then(function (j) {
        st.number = j.number; st.url = j.html_url; st.posted = 0;
      });
    }
    function next() {
      if (st.posted >= iss.comments.length) return { number: st.number, url: st.url };
      step(st.posted + 2, n);
      return sleep(COMMENT_GAP).then(function () {
        return o.gh.post(R + '/issues/' + st.number + '/comments', { body: iss.comments[st.posted] });
      }).then(function () { st.posted++; return next(); });
    }
    return first.then(next);
  }

  /* ---------- authorization (browser) ---------- */

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
    var fetchFn = cfg.fetch || (W && W.fetch.bind(W));
    var token = null, user = null, waiting = null;
    var worker = String(cfg.workerUrl || '').replace(/\/+$/, '');

    function randomB64(n) { var a = new Uint8Array(n); W.crypto.getRandomValues(a); return b64url(a); }
    function challengeOf(verifier) {
      return W.crypto.subtle.digest('SHA-256', utf8(verifier)).then(function (d) { return b64url(new Uint8Array(d)); });
    }
    function session() { try { return W.sessionStorage; } catch (e) { return null; } }

    function exchange(code, verifier) {
      return fetchFn(worker + '/token', {
        method: 'POST',
        // text/plain keeps this a simple CORS request: no preflight round trip
        headers: { 'Content-Type': 'text/plain;charset=UTF-8' },
        body: JSON.stringify({ code: code, code_verifier: verifier }),
        credentials: 'omit', cache: 'no-store', referrerPolicy: 'no-referrer'
      }).then(function (r) {
        return r.json().catch(function () { return {}; }).then(function (j) { return { ok: r.ok, status: r.status, j: j }; });
      }, function () { throw err('workerNetwork'); }).then(function (res) {
        if (!res.ok || typeof res.j.access_token !== 'string') throw err(res.status === 429 ? 'busy' : 'exchange', { detail: res.j.error || '' });
        token = res.j.access_token;
        return client(fetchFn, token).get('/user');
      }).then(function (u) {
        user = { login: u.login, id: u.id };
        return user;
      }, function (e) {
        if (token) { revoke(); }
        throw e;
      });
    }

    /* Must run inside the click handler: the popup opens before anything async happens.
       extra: kept with the same-tab fallback, handed back by resume(). */
    function signIn(extra) {
      if (token && user) return Promise.resolve(user);
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
        return waitForCode(state).then(function (code) { return exchange(code, verifier); });
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

    /* Same-tab fallback: call once on page load. Resolves to { user, extra } when a sign-in just came back,
       else null. */
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
      return exchange(r.code, p.verifier).then(function (u) { return { user: u, extra: extra }; }, function (e) { e.extra = extra; throw e; });
    }

    function revoke() {
      var t = token;
      token = null; user = null;
      if (!t) return Promise.resolve();
      return fetchFn(worker + '/revoke', {
        method: 'POST', headers: { 'Content-Type': 'text/plain;charset=UTF-8' }, body: JSON.stringify({ access_token: t }),
        credentials: 'omit', keepalive: true, referrerPolicy: 'no-referrer'
      }).then(function () {}, function () {});
    }
    // Leaving the page with a live token: revoke it on the way out.
    if (W && W.addEventListener) W.addEventListener('pagehide', function () {
      if (!token) return;
      var t = token;
      token = null; user = null;
      try { W.navigator.sendBeacon(worker + '/revoke', JSON.stringify({ access_token: t })); } catch (e) { /* best effort */ }
    });

    return {
      user: function () { return user; },
      signedIn: function () { return !!token; },
      signIn: signIn,
      cancel: cancel,
      resume: resume,
      revoke: revoke,
      // the API client for the current token (null when signed out)
      client: function () { return token ? client(fetchFn, token) : null; },
      anon: function () { return client(fetchFn, null); }
    };
  }

  return {
    buildIssue: buildIssue, decodeIssue: decodeIssue, layout: layout, issueTitle: issueTitle, cleanName: cleanName,
    sendIssue: sendIssue, findExisting: findExisting, branchFor: branchFor, client: client,
    createAuth: createAuth, authorizeUrl: authorizeUrl, supported: supported,
    utf8: utf8, bytesToB64: bytesToB64, b64ToBytes: b64ToBytes, b64url: b64url, sha256Hex: sha256Hex, gzip: gzip, gunzip: gunzip,
    MARKER: MARKER, LIMIT: LIMIT, LINE: LINE, MAX_FILE: MAX_FILE, CHANNEL: CHANNEL, PENDING_KEY: PENDING_KEY, RESULT_KEY: RESULT_KEY
  };
});
