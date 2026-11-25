// Translate menu: the nav's Translate item and the hero's Help translate pill open a small language list instead of
// going straight to the translation page. Without JavaScript both stay plain links to translate/.
// Progress is fetched only when the menu first opens, then kept for the session.
// The list itself (which files exist, their names, how much is translated, the rows) is shared with the translation
// page's landing screen as window.DS5TranslateLangs; that page loads this file without any [data-tm] trigger.
// The translator credits (translators.json on the repo's translation-data branch) load with the list and show as a
// small "by @a, @b +N" line under a language. Without that file (a 404 before the branch exists) there is no line.
(function () {
  'use strict';

  var DIR = 'src/Kopru.Core/Localization/strings';
  var API = 'https://api.github.com/repos/Midzer00/ds5-untethered/contents/' + DIR;
  var RAW = 'https://raw.githubusercontent.com/Midzer00/ds5-untethered/main/' + DIR + '/';
  var SUGGESTED = ['zh-Hans', 'ru', 'es', 'pt-BR', 'de', 'ja', 'fr', 'pl', 'ko'];
  var MAINTAINED = 'tr';
  var CACHE_KEY = 'ds5-translate-menu';
  var CODE = /^([A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)\.json$/;
  var CREDITS_URL = 'https://raw.githubusercontent.com/Midzer00/ds5-untethered/translation-data/translators.json';
  var CREDITS_KEY = 'ds5-translate-credits';
  var CREDITS_SHOWN = 3;
  var LOGIN = /^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$/;

  var TEXT = {
    en: {
      title: 'Translate DS5 Untethered', line: 'Pick a language. A few lines already help.',
      pct: function (n) { return n + '% translated'; }, under: '<1% translated',
      notStarted: 'Not started', maintained: 'Maintained by the project', by: 'by',
      other: 'Other Language…', open: 'Open the Translation Page', close: 'Close'
    },
    tr: {
      title: 'DS5 Untethered’ı Çevir', line: 'Bir dil seç. Birkaç satır bile işe yarar.',
      pct: function (n) { return '%' + n + ' çevrildi'; }, under: '<%1 çevrildi',
      notStarted: 'Başlanmadı', maintained: 'Projenin kendi çevirisi', by: 'çeviren:',
      other: 'Başka Bir Dil…', open: 'Çeviri Sayfasını Aç', close: 'Kapat'
    }
  };

  /* ---------- data ---------- */

  // langs: null while loading, false after a failure, else [{ code, name, pct }] of the files that exist.
  // files: the parsed files of this load (not cached), so the translation page need not fetch them again.
  var langs = null, loading = null, files = {};
  try { var c = JSON.parse(sessionStorage.getItem(CACHE_KEY)); if (c && Array.isArray(c.langs)) langs = c.langs; } catch (e) { /* none */ }

  function getJson(url, ms) {
    var ctl = window.AbortController ? new AbortController() : null;
    var t = setTimeout(function () { if (ctl) ctl.abort(); }, ms || 6000);
    return fetch(url, { credentials: 'omit', cache: 'no-cache', signal: ctl ? ctl.signal : undefined }).then(function (r) {
      if (!r.ok) { var err = new Error(String(r.status)); err.status = r.status; throw err; }
      return r.json();
    }).then(function (j) { clearTimeout(t); return j; }, function (e) { clearTimeout(t); throw e; });
  }
  var isObj = function (j) { return j && typeof j === 'object' && !Array.isArray(j); };

  // en.json keys without @name; a language counts the keys it fills with text
  function percent(en, data) {
    var keys = Object.keys(en).filter(function (k) { return k !== '@name'; }), n = 0;
    keys.forEach(function (k) { var v = data[k]; if (typeof v === 'string' && v.trim() !== '') n++; });
    return keys.length ? n / keys.length : 0;
  }

  // opts.en: a function returning a promise of en.json, for a page that loads it anyway. Never rejects;
  // afterwards state() is the list, or false when it failed (the next call tries again).
  function load(opts) {
    if (loading) return loading;
    var listed = getJson(API).then(function (list) {
      if (!Array.isArray(list)) throw new Error('list');
      var codes = [];
      list.forEach(function (f) { var m = f && f.type === 'file' && CODE.exec(f.name); if (m && m[1].toLowerCase() !== 'en') codes.push(m[1]); });
      return codes.map(function (code) { return getJson(RAW + code + '.json').then(function (d) { return { code: code, data: d }; }, function () { return { code: code, data: null }; }); });
    }, function () {
      // The listing is rate-limited; probe the likely files instead. A 404 means the file does not exist.
      return SUGGESTED.concat(MAINTAINED).map(function (code) {
        return getJson(RAW + code + '.json').then(function (d) { return { code: code, data: d }; }, function (e) {
          if (e && e.status === 404) return null;
          throw e;
        });
      });
    }).then(function (jobs) { return Promise.all(jobs); });

    var en = opts && opts.en ? Promise.resolve().then(opts.en) : getJson(RAW + 'en.json');
    loading = Promise.all([en, listed, loadCredits()]).then(function (r) {
      var en = r[0];
      if (!isObj(en)) throw new Error('en');
      files = {};
      langs = r[1].filter(Boolean).map(function (f) {
        var ok = isObj(f.data);
        if (ok) files[f.code] = f.data;
        return { code: f.code, name: ok && typeof f.data['@name'] === 'string' ? f.data['@name'] : null, pct: ok ? percent(en, f.data) : null };
      });
      try { sessionStorage.setItem(CACHE_KEY, JSON.stringify({ langs: langs })); } catch (e) { /* storage may be blocked */ }
    }).catch(function () {
      langs = false; loading = null; // the next opening tries again
    });
    return loading;
  }

  /* ---------- translator credits ---------- */

  // credits: null until loaded, false when there is no file (404) or it failed, else { code: [login, ...] }.
  // A 404 is kept for the session; any other failure is tried again on the next page.
  var credits = null, creditsLoading = null;
  try { var cc = JSON.parse(sessionStorage.getItem(CREDITS_KEY)); if (cc && (cc.none || isObj(cc.langs))) credits = cc.none ? false : cc.langs; } catch (e) { /* none */ }

  // translators.json -> { code: [login, ...] }: valid GitHub logins only, no bots, no duplicates, the file's order
  function parseCredits(j) {
    var out = {};
    if (!isObj(j) || !isObj(j.languages)) return out;
    Object.keys(j.languages).forEach(function (code) {
      var list = j.languages[code], seen = {}, names = [];
      if (!Array.isArray(list) || code.toLowerCase() === MAINTAINED || code.toLowerCase() === 'en') return;
      list.forEach(function (p) {
        var login = p && typeof p.login === 'string' ? p.login.trim() : '';
        if (!LOGIN.test(login) || seen[login.toLowerCase()]) return;
        seen[login.toLowerCase()] = 1;
        names.push(login);
      });
      if (names.length) out[code.toLowerCase()] = names;
    });
    return out;
  }

  // Never rejects; afterwards creditsFor() answers
  function loadCredits() {
    if (credits !== null) return Promise.resolve();
    if (creditsLoading) return creditsLoading;
    creditsLoading = getJson(CREDITS_URL).then(function (j) {
      credits = parseCredits(j);
      try { sessionStorage.setItem(CREDITS_KEY, JSON.stringify({ langs: credits })); } catch (e) { /* storage may be blocked */ }
    }, function (e) {
      credits = false;
      if (e && e.status === 404) { try { sessionStorage.setItem(CREDITS_KEY, JSON.stringify({ none: true })); } catch (x) { /* blocked */ } }
    }).then(function () { creditsLoading = null; });
    return creditsLoading;
  }

  function creditsFor(code) {
    if (!credits || !code || code.toLowerCase() === MAINTAINED) return [];
    return credits[code.toLowerCase()] || [];
  }

  // The first few names and how many more: { shown: [login], more: n, all: '@a, @b, …' }, or null for nobody
  function creditParts(logins, max) {
    if (!Array.isArray(logins) || !logins.length) return null;
    var n = max || CREDITS_SHOWN;
    return { shown: logins.slice(0, n), more: Math.max(0, logins.length - n), all: logins.map(function (l) { return '@' + l; }).join(', ') };
  }

  // The line's content: lead text, then each name a link to its GitHub profile, then +N with all names in its title.
  // lead: 'by', 'çeviren:' or 'Translated by'. Returns a fragment, or null when nobody is listed.
  function creditNodes(lead, logins) {
    var c = creditParts(logins);
    if (!c) return null;
    var f = document.createDocumentFragment();
    f.append(lead + ' ');
    c.shown.forEach(function (l, i) {
      if (i) f.append(', ');
      var a = document.createElement('a');
      a.href = 'https://github.com/' + encodeURIComponent(l);
      a.rel = 'noopener'; a.target = '_blank';
      a.textContent = '@' + l;
      f.append(a);
    });
    if (c.more) {
      var m = document.createElement('span');
      m.className = 'tm-more';
      m.title = c.all;
      m.textContent = '+' + c.more;
      f.append(' ', m);
    }
    return f;
  }

  /* ---------- names and labels ---------- */

  function nativeName(code, known) {
    if (known && known.trim()) return known.trim();
    try {
      var n = new Intl.DisplayNames([code], { type: 'language' }).of(code);
      if (n && n !== code) { var f = Array.from(n)[0]; return f.toLocaleUpperCase(code) + n.slice(f.length); }
    } catch (e) { /* unknown */ }
    return code;
  }
  function pctText(T, p) {
    var n = Math.floor(p * 100 + 1e-9);
    return n === 0 && p > 0 ? T.under : T.pct(n);
  }

  /* ---------- rows ---------- */

  // The list as row descriptions: the existing files by progress (Turkish greyed), the suggested ones not started,
  // then Other Language. hrefFor(code) links a language; hrefFor(null) links Other Language.
  function rows(T, hrefFor) {
    var out = [], seen = {};
    var push = function (code, name, pct, state) {
      seen[code.toLowerCase()] = 1;
      var tr = code.toLowerCase() === MAINTAINED;
      out.push({
        code: code, name: nativeName(code, name), off: tr, note: tr ? T.maintained : null, pct: typeof pct === 'number' ? pct : null,
        status: state === 'loading' ? '…' : typeof pct === 'number' ? pctText(T, pct) : state === 'new' ? T.notStarted : '',
        href: tr ? null : hrefFor(code), by: tr ? [] : creditsFor(code), byLead: T.by
      });
    };
    if (langs) {
      langs.filter(function (l) { return l.code.toLowerCase() !== 'en'; })
        .sort(function (a, b) { return (b.pct == null ? -1 : b.pct) - (a.pct == null ? -1 : a.pct); })
        .forEach(function (l) { push(l.code, l.name, l.pct, 'known'); });
      SUGGESTED.forEach(function (c) { if (!seen[c.toLowerCase()]) push(c, null, null, 'new'); });
    } else {
      // Before the data arrives (or without it) the static list still works
      [MAINTAINED].concat(SUGGESTED).forEach(function (c) { push(c, null, null, langs === null ? 'loading' : 'unknown'); });
    }
    out.push({ name: T.other, href: hrefFor(null), other: true });
    return out;
  }

  var el = function (tag, cls, text) { var n = document.createElement(tag); if (cls) n.className = cls; if (text != null) n.textContent = text; return n; };

  function rowEl(o) {
    var li = el('li');
    var item = el(o.href ? 'a' : 'div', 'tm-row' + (o.off ? ' is-off' : '') + (o.other ? ' tm-other' : ''));
    if (o.href) item.href = o.href; else item.setAttribute('aria-disabled', 'true');
    var name = el('span', 'tm-name', o.name);
    if (o.code) { name.lang = o.code; item.append(name, el('span', 'tm-code meta', o.code)); } else item.append(name);
    if (o.status != null) item.append(el('span', 'tm-stat meta', o.status));
    if (o.note) item.append(el('span', 'tm-note meta', o.note));
    if (o.pct != null) { var bar = el('i', 'tm-bar'); bar.style.width = Math.max(0, Math.min(100, o.pct * 100)) + '%'; item.append(bar); }
    li.append(item);
    // The names are links of their own, so the line sits beside the row's link, not in it
    var by = o.by && o.by.length ? creditNodes(o.byLead || TEXT.en.by, o.by) : null;
    if (by) { var p = el('p', 'tm-by meta'); p.append(by); li.className = 'tm-has-by'; li.append(p); }
    return li;
  }

  window.DS5TranslateLangs = {
    SUGGESTED: SUGGESTED, MAINTAINED: MAINTAINED, text: TEXT,
    state: function () { return langs; },
    file: function (code) { return Object.prototype.hasOwnProperty.call(files, code) ? files[code] : null; },
    load: load, rows: rows, rowEl: rowEl, nativeName: nativeName, pctText: pctText,
    loadCredits: loadCredits, credits: function () { return credits; }, creditsFor: creditsFor,
    creditParts: creditParts, creditNodes: creditNodes, parseCredits: parseCredits
  };

  /* ---------- the home pages' menu ---------- */

  var triggers = [].slice.call(document.querySelectorAll('[data-tm]'));
  if (!triggers.length || !window.fetch) return;

  var page = (document.documentElement.lang || 'en').toLowerCase().split('-')[0] === 'tr' ? 'tr' : 'en';
  var T = TEXT[page];

  // translate/ resolved from the trigger's own link, so both home pages work from their folders
  var base = triggers[0].href.split('#')[0];
  var hrefFor = function (code) { return base + (code ? '#lang=' + encodeURIComponent(code) : '#other'); };

  /* ---------- panel ---------- */

  var panel = el('div', 'tm-panel');
  panel.id = 'tm-panel'; panel.hidden = true;
  panel.setAttribute('role', 'dialog'); panel.setAttribute('aria-labelledby', 'tm-t');
  var close = el('button', 'dl-close', '×');
  close.type = 'button'; close.setAttribute('aria-label', T.close);
  var title = el('h2', null, T.title); title.id = 'tm-t';
  var list = el('ul', 'tm-list');
  var foot = el('a', 'tm-open btn primary', T.open + ' →'); foot.href = base;
  var top = el('div', 'tm-top'); top.append(foot);
  panel.append(close, title, el('p', 'tm-line', T.line), top, list);
  document.body.appendChild(panel);

  function render() {
    var keep = document.activeElement && panel.contains(document.activeElement) ? document.activeElement.getAttribute('href') : null;
    list.textContent = '';
    rows(T, hrefFor).forEach(function (o) { list.append(rowEl(o)); });
    if (keep) { var again = panel.querySelector('a[href="' + keep.replace(/"/g, '\\"') + '"]'); if (again) again.focus(); }
    if (open) place();
  }

  /* ---------- open, close, place ---------- */

  var open = null; // the trigger that opened the panel
  var narrow = window.matchMedia('(max-width:860px)');
  var items = function () { return [].slice.call(panel.querySelectorAll('a[href]')); };

  function place() {
    var nav = open.closest('.osd-nav');
    var a = (narrow.matches && nav ? nav : open).getBoundingClientRect();
    var vw = document.documentElement.clientWidth, vh = window.innerHeight, gap = 8, edge = 16;
    var s = panel.style;
    s.top = s.bottom = s.left = s.maxHeight = '';
    if (!narrow.matches) {
      var w = panel.offsetWidth, inset = 42;
      s.left = Math.round(Math.max(inset, Math.min(a.left + a.width / 2 - w / 2, vw - inset - w))) + 'px';
    }
    // Under the anchor; above it only when the room under it is too short for the list and there is more room above
    var full = panel.scrollHeight;
    var below = vh - (nav && narrow.matches ? a.top : a.bottom + gap) - edge, above = a.top - gap - edge;
    if (nav || full <= below || below >= above) {
      s.top = Math.round(nav && narrow.matches ? a.top : a.bottom + gap) + 'px';
      s.maxHeight = Math.max(160, below) + 'px';
    } else {
      s.bottom = Math.round(vh - a.top + gap) + 'px';
      s.maxHeight = Math.max(160, above) + 'px';
    }
  }

  function show(trigger, focusLast) {
    if (open && open !== trigger) hide(false);
    open = trigger;
    trigger.setAttribute('aria-expanded', 'true');
    if (!langs) load().then(function () { if (open) render(); });
    else if (credits === null) loadCredits().then(function () { if (open) render(); });
    render();
    panel.hidden = false;
    panel.scrollTop = 0;
    place();
    var all = items();
    var first = focusLast ? all[all.length - 1] : all[0];
    if (first) first.focus({ preventScroll: true });
  }

  function hide(refocus) {
    if (!open) return;
    var t = open;
    open = null;
    panel.hidden = true;
    t.setAttribute('aria-expanded', 'false');
    if (refocus && t.getClientRects().length) t.focus({ preventScroll: true });
  }

  var plain = function (e) { return e.button === 0 && !e.metaKey && !e.ctrlKey && !e.shiftKey && !e.altKey; };

  triggers.forEach(function (t) {
    t.setAttribute('aria-haspopup', 'dialog');
    t.setAttribute('aria-expanded', 'false');
    t.setAttribute('aria-controls', 'tm-panel');
    t.addEventListener('click', function (e) {
      if (!plain(e)) return; // a new-tab click still opens the page
      e.preventDefault();
      e.stopPropagation(); // keeps the phone Menu open under the panel
      if (open === t) hide(true); else show(t, false);
    });
    t.addEventListener('keydown', function (e) {
      if (e.key === ' ' || e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        show(t, e.key === 'ArrowUp');
      }
    });
  });

  close.addEventListener('click', function () { hide(true); });

  panel.addEventListener('keydown', function (e) {
    var all = items(), i = all.indexOf(document.activeElement);
    if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation(); // only the panel closes; the phone Menu stays open
      hide(true);
    } else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      var n = all.length, next = i < 0 ? (e.key === 'ArrowDown' ? 0 : n - 1) : (i + (e.key === 'ArrowDown' ? 1 : -1) + n) % n;
      all[next].focus();
    } else if (e.key === 'Home' || e.key === 'End') {
      e.preventDefault();
      all[e.key === 'Home' ? 0 : all.length - 1].focus();
    } else if (e.key === 'Tab') {
      // Tab leaves the panel from its trigger, so focus moves on to the trigger's neighbour
      hide(true);
    }
  });

  document.addEventListener('pointerdown', function (e) {
    if (!open || panel.contains(e.target) || open.contains(e.target)) return;
    hide(false);
  }, true);
  addEventListener('keydown', function (e) { if (e.key === 'Escape' && open) hide(true); });

  var queued = false;
  var follow = function () {
    if (!open || queued) return;
    queued = true;
    requestAnimationFrame(function () {
      queued = false;
      if (!open) return;
      var r = open.getBoundingClientRect();
      // a pill scrolled out of view, or a trigger hidden by the Menu closing, takes the panel with it
      if (!open.getClientRects().length || r.bottom < 0 || r.top > window.innerHeight) hide(false); else place();
    });
  };
  addEventListener('scroll', follow, { passive: true });
  addEventListener('resize', follow);
  if (window.ResizeObserver) new ResizeObserver(follow).observe(document.body);
})();
