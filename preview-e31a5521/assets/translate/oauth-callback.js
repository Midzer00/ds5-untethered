/* GitHub sends the volunteer back here (the GitHub App's callback URL) with ?code=&state=, or ?error=.
   This page only hands the result to the translation page and goes away. The code is useless without the
   PKCE verifier (held by the translation page) and the client secret (held by the Worker). */
(function () {
  'use strict';
  var CHANNEL = 'ds5-gh-oauth', PENDING_KEY = 'ds5-gh-oauth', RESULT_KEY = 'ds5-gh-oauth-result';
  var q = new URLSearchParams(location.search);
  var msg = { type: CHANNEL, code: q.get('code'), state: q.get('state'), error: q.get('error') };
  // Drop the code from the address bar and the history.
  try { history.replaceState(null, '', location.pathname); } catch (e) { /* ignore */ }
  var out = document.getElementById('msg');

  var pending = null;
  try { pending = JSON.parse(sessionStorage.getItem(PENDING_KEY) || 'null'); } catch (e) { pending = null; }

  // Same-tab fallback (the popup was blocked): keep the result for this tab and go back to the page.
  if (pending && pending.mode === 'redirect' && pending.state === msg.state) {
    var home = new URL('./', location.href);
    var back = home.pathname;
    try {
      var u = new URL(pending.returnTo, location.href);
      if (u.origin === location.origin && u.pathname.indexOf(home.pathname) === 0) back = u.pathname + u.search + u.hash;
    } catch (e) { /* default */ }
    try { sessionStorage.setItem(RESULT_KEY, JSON.stringify(msg)); } catch (e) { /* the page reports it */ }
    location.replace(back);
    return;
  }

  // Popup: tell the translation page (same origin) and close.
  var sent = false;
  try { var bc = new BroadcastChannel(CHANNEL); bc.postMessage(msg); bc.close(); sent = true; } catch (e) { /* older browser */ }
  try { localStorage.setItem(RESULT_KEY, JSON.stringify(msg)); localStorage.removeItem(RESULT_KEY); sent = true; } catch (e) { /* storage blocked */ }
  out.textContent = !sent
    ? 'This Browser Blocked the Hand-Over. Close This Window and Use Paste in Browser or Upload Form.'
    : msg.error ? 'Sign-In Cancelled. You Can Close This Window.' : 'Signed In. You Can Close This Window.';
  setTimeout(function () { window.close(); }, 300);
})();
