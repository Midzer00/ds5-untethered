/* Volunteer translation page. Plain DOM, no framework. Pure logic lives in core.js, UI text in strings.js. */
(function () {
  'use strict';

  var T = window.TrStrings.en;
  var C = window.TrCore;

  var REPO = 'https://github.com/Midzer00/ds5-untethered';
  var DIR = 'src/Kopru.Core/Localization/strings';
  var RAW = 'https://raw.githubusercontent.com/Midzer00/ds5-untethered/main/' + DIR + '/';
  var GUIDE = REPO + '/blob/main/docs/TRANSLATING.md';
  // The language list is the home menu's (assets/js/translate-menu.js), so the menu, the landing and the picker agree
  var Langs = window.DS5TranslateLangs || null;
  var SUGGESTED = Langs ? Langs.SUGGESTED : ['zh-Hans', 'ru', 'es', 'pt-BR', 'de', 'ja', 'fr', 'pl', 'ko'];

  var nf = new Intl.NumberFormat(T.numberLocale);
  var pf = new Intl.NumberFormat(T.numberLocale, { style: 'percent', maximumFractionDigits: 0 });
  var df = new Intl.DateTimeFormat(T.dateLocale, { day: 'numeric', month: 'short' });
  var fmt = function (n) { return nf.format(n); };

  var S = {
    en: null, enKeys: [],
    langs: {}, existing: [],
    code: null, base: {}, isNew: false,
    store: null, storeStatus: null, restored: null, confirmDiscard: false, undo: null, draftMsg: '', edited: false, persistAsked: false,
    bad: {},
    status: 'empty', error: null,
    filter: 'untranslated', query: '', group: '',
    otherOpen: false, otherError: '',
    token: 0,
    mode: 'list', lastListKey: null
  };
  var rowCache = new Map();
  var renderTok = 0;
  var growByJs = !(window.CSS && CSS.supports && CSS.supports('field-sizing', 'content'));

  /* ---------- small helpers ---------- */

  function h(tag, cls, attrs, kids) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (attrs) for (var a in attrs) {
      var v = attrs[a];
      if (v == null || v === false) continue;
      if (a === 'text') n.textContent = v;
      else if (a === 'html') n.innerHTML = v;
      else n.setAttribute(a, v === true ? '' : v);
    }
    if (kids) kids.forEach(function (k) { if (k != null) n.append(k); });
    return n;
  }
  var ICONS = {
    chev: '<path d="M6 9l6 6 6-6"/>',
    search: '<circle cx="11" cy="11" r="6.5"/><path d="M20 20l-4.2-4.2"/>',
    globe: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c2.5 2.6 3.7 5.6 3.7 9s-1.2 6.4-3.7 9c-2.5-2.6-3.7-5.6-3.7-9S9.5 5.6 12 3z"/>',
    alert: '<circle cx="12" cy="12" r="9"/><path d="M12 7.5v5.5M12 16.5v.01"/>',
    width: '<path d="M3 12h18M7 8l-4 4 4 4M17 8l4 4-4 4"/>',
    close: '<path d="M6 6l12 12M18 6L6 18"/>',
    copy: '<rect x="8.5" y="8.5" width="11.5" height="11.5" rx="2"/><path d="M15.5 8.5V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v7.5a2 2 0 0 0 2 2h2.5"/>',
    down: '<path d="M12 4v11m0 0-4.5-4.5M12 15l4.5-4.5M5 19.5h14"/>',
    upload: '<path d="M12 15V4m0 0L7.5 8.5M12 4l4.5 4.5M5 19.5h14"/>',
    out: '<path d="M14 4h6v6M20 4l-9 9M18 14v4a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h4"/>',
    retry: '<path d="M4 12a8 8 0 0 1 14-5.3M20 4v4h-4M20 12a8 8 0 0 1-14 5.3M4 20v-4h4"/>',
    left: '<path d="M15 6l-6 6 6 6"/>',
    right: '<path d="M9 6l6 6-6 6"/>',
    up: '<path d="M6 15l6-6 6 6"/>',
    clock: '<circle cx="12" cy="12" r="8.5"/><path d="M12 7.5V12l3 2"/>',
    warn: '<path d="M12 4 2.8 19.5h18.4L12 4z"/><path d="M12 10v4.2M12 17v.01"/>'
  };
  function icon(name, cls) {
    return '<svg class="au-ic' + (cls ? ' ' + cls : '') + '" viewBox="0 0 24 24" stroke-width="1.7" aria-hidden="true">' + ICONS[name] + '</svg>';
  }

  function storeGet(k) { try { return window.localStorage.getItem(k); } catch (e) { return null; } }
  function storeSet(k, v) { try { window.localStorage.setItem(k, v); return true; } catch (e) { return false; } }

  function capFirst(s, code) {
    if (!s) return s;
    var first = Array.from(s)[0];
    try { return first.toLocaleUpperCase(code) + s.slice(first.length); } catch (e) { return first.toUpperCase() + s.slice(first.length); }
  }
  function nativeName(code) {
    try { var n = new Intl.DisplayNames([code], { type: 'language' }).of(code); if (n && n !== code) return capFirst(n, code); } catch (e) { /* unknown */ }
    return code;
  }
  function uiName(code) {
    try { var n = new Intl.DisplayNames([T.numberLocale], { type: 'language' }).of(code); if (n) return capFirst(n, T.numberLocale); } catch (e) { /* unknown */ }
    return code;
  }
  function langName(code) {
    var L = S.langs[code];
    return (L && C.hasText(L.name)) ? L.name : nativeName(code);
  }

  function fetchJson(url) {
    return fetch(url, { cache: 'no-cache' }).then(function (r) {
      if (!r.ok) throw { status: r.status };
      return r.text().then(function (t) {
        try { return JSON.parse(t); } catch (e) { throw { parse: true }; }
      });
    }, function () { throw { network: true }; });
  }

  /* ---------- data ---------- */

  var enPromise = null;
  function loadEnglish() {
    if (!enPromise) {
      enPromise = fetch(RAW + 'en.json', { cache: 'no-cache' }).then(function (r) {
        if (!r.ok) throw { status: r.status };
        return r.arrayBuffer().then(function (buf) {
          var j;
          try { j = JSON.parse(new TextDecoder().decode(buf)); } catch (e) { throw { parse: true }; }
          S.enBytes = buf;
          S.en = j; S.enKeys = Object.keys(j);
          return j;
        });
      }, function () { throw { network: true }; }).catch(function (e) { enPromise = null; throw e; });
    }
    return enPromise;
  }

  function ensureLang(code, exists) {
    var L = S.langs[code] || (S.langs[code] = { code: code, exists: false, pct: null, name: null, data: null });
    if (exists && !L.exists) { L.exists = true; S.existing.push(code); }
    return L;
  }

  function setLangData(code, data) {
    var L = ensureLang(code, true);
    L.data = data;
    L.name = typeof data['@name'] === 'string' ? data['@name'] : null;
    if (S.enKeys.length) L.pct = C.countTranslated(S.enKeys, data, null) / S.enKeys.length;
  }

  // The shared loader lists the files (probing the likely ones when the API is rate-limited) and keeps the files
  // it fetched, so choosing one of them does not fetch it again. A list cached by the home menu this session is
  // used as it is: names and percentages, no files.
  function loadLanguageList() {
    if (!Langs) { loadEnglish().catch(function () { /* shown when a language is chosen */ }); return; }
    var cached = !!Langs.state();
    if (cached) loadEnglish().catch(function () { /* shown when a language is chosen */ });
    (cached ? (Langs.loadCredits ? Langs.loadCredits() : Promise.resolve()) : Langs.load({ en: loadEnglish })).then(function () {
      var got = Langs.state();
      if (got) got.forEach(function (l) {
        if (!C.canonicalCode(l.code).ok) return;
        var L = ensureLang(l.code, true), d = Langs.file(l.code);
        if (L.data) return;
        if (d && S.enKeys.length) { setLangData(l.code, d); return; }
        if (!C.hasText(L.name) && C.hasText(l.name)) L.name = l.name;
        if (L.pct == null && typeof l.pct === 'number') L.pct = l.pct;
      });
      renderLangChoices();
      renderLanding();
      renderCredit();
    });
  }

  function loadLangFile(code) {
    var L = S.langs[code];
    if (L && L.data) return Promise.resolve({ exists: true, data: L.data });
    return fetchJson(RAW + encodeURIComponent(code) + '.json').then(function (d) {
      return { exists: true, data: d };
    }, function (e) {
      if (e && e.status === 404) return { exists: false, data: {} };
      throw e;
    });
  }

  /* Draft: draft-store.js keeps one entry per edited string, written on every input, synced between tabs,
     with an IndexedDB copy for undo and for when localStorage fails. */
  function draftItem(k) { return S.store ? S.store.item(k) : null; }
  function val(k) {
    var d = draftItem(k);
    return d ? d.t : S.base[k];
  }
  function draftValues() { return S.store ? S.store.values() : {}; }
  function changedCount() { return S.store ? Object.keys(S.store.items()).length : 0; }
  function baseText(k) { return typeof S.base[k] === 'string' ? S.base[k] : ''; }
  // The file on GitHub changed this string after the volunteer edited it
  function ghChanged(k) { var d = draftItem(k), now = baseText(k); return !!d && d.b !== now && d.t !== now; }

  function restoredInfo() {
    var its = S.store.items(), keys = Object.keys(its);
    if (!keys.length) return null;
    var at = 0;
    keys.forEach(function (k) { if (its[k].at > at) at = its[k].at; });
    return { n: keys.length, saved: at || Date.now() };
  }

  function loadDraft(code) {
    if (S.store) S.store.close();
    S.restored = null; S.confirmDiscard = false; S.undo = null; S.draftMsg = ''; S.edited = false;
    var store = DraftStore.open({
      code: code, base: S.base,
      enHas: function (k) { return Object.prototype.hasOwnProperty.call(S.en, k); },
      onRemote: function (keys) { if (S.store === store) remoteKeys(keys); },
      onStatus: function (st) {
        if (S.store !== store) return;
        S.storeStatus = st;
        updateSubbar();
        if (draftMenu.classList.contains('open')) renderStrip(); else renderDraftItem();
      }
    });
    S.store = store;
    S.storeStatus = store.status();
    S.restored = restoredInfo();
    store.ready().then(function () {
      // the IndexedDB copy may have filled in strings localStorage had lost
      if (S.store === store && !S.edited && !S.restored && S.status === 'ready') { S.restored = restoredInfo(); renderStrip(); }
    });
  }

  // Another tab (or a loaded file, or Undo) changed these strings
  function remoteKeys(keys) {
    if (S.status !== 'ready') return;
    keys.forEach(function (k) {
      if (!Object.prototype.hasOwnProperty.call(S.en, k)) return;
      var rec = rowCache.get(k);
      if (rec) {
        var v = val(k);
        v = v == null ? '' : v;
        if (rec.ta.value !== v) {
          // never pull text out from under the caret: mark it and sync when the field is left
          if (document.activeElement === rec.ta) { rec.remote = true; rec.tabTag.hidden = false; }
          else { rec.ta.value = v; grow(rec.ta); }
        }
        refreshRow(rec);
      } else if (C.validate(S.en[k], val(k), S.code).errors.length) S.bad[k] = true;
      else delete S.bad[k];
      visualInput(k);
    });
    if (isVisual() && VS.built >= 0 && VS.key == null) renderEditor();
    updateProgress();
    updateSubbar();
    renderStrip();
  }

  /* ---------- page skeleton ---------- */

  var app = document.getElementById('app');
  var tools = document.getElementById('tools');
  var body = document.getElementById('body');
  var subbar = document.getElementById('subbar');
  var scroller = document.getElementById('scroll');
  var rules = document.getElementById('rules');
  var rulesBtn = document.getElementById('rules-btn');

  // Rules and glossary: a dialog opened from the header, so it never pushes the page down
  (function buildRules() {
    if (!rules || !rulesBtn) return;
    var gl = h('dl', 'tr-gloss');
    T.glossary.forEach(function (g) { gl.append(h('dt', null, { lang: 'en', text: g[0] }), h('dd', null, { text: g[1] })); });
    var ul = h('ul', 'tr-rules-list');
    T.rules.forEach(function (r) { ul.append(h('li', null, { html: r })); });
    var closeBtn = h('button', 'au-btn tr-icon-btn', { type: 'button', 'aria-label': T.close, html: icon('close') });
    closeBtn.addEventListener('click', function () { rules.close(); });
    var foot = document.getElementById('foot');
    var legal = foot ? Array.prototype.map.call(foot.querySelectorAll('p'), function (p) { return p.cloneNode(true); }) : [];
    rules.append(h('div', 'tr-sheet-in tr-rules-in', null, [
      h('div', 'tr-sheet-head', null, [h('h2', 'au-h1', { id: 'rules-title', text: T.rulesDialogTitle }), closeBtn]),
      h('p', 'tr-sub', { text: T.subtitle }),
      h('div', 'tr-rules-body', null, [
        h('p', 'au-section', { text: T.rulesTitle }),
        ul,
        h('p', 'au-section tr-gloss-h', { text: T.glossaryTitle }),
        gl,
        h('p', 'tr-guide', null, [h('a', null, { href: GUIDE, target: '_blank', rel: 'noopener', text: T.guide })])
      ]),
      h('div', 'tr-legal', null, legal)
    ]));
    rulesBtn.textContent = T.rulesTitle;
    rulesBtn.addEventListener('click', function () {
      closeAllCombos();
      closeDraftMenu();
      if (typeof rules.showModal === 'function') rules.showModal(); else rules.setAttribute('open', '');
      rulesBtn.setAttribute('aria-expanded', 'true');
    });
    rules.addEventListener('close', function () {
      rulesBtn.setAttribute('aria-expanded', 'false');
      if (!document.activeElement || document.activeElement === document.body) rulesBtn.focus();
    });
    rules.addEventListener('click', function (e) { if (e.target === rules) rules.close(); });
    rulesBtn.setAttribute('aria-expanded', 'false');
  })();

  /* Combo (au-combo + au-pop, opening downward) */
  function Combo(cls, ariaLabel, iconName, onPick) {
    var wrap = h('div', 'au-combo-wrap ' + cls);
    var id = 'pop-' + Math.random().toString(36).slice(2, 8);
    var btn = h('button', 'au-combo', { type: 'button', 'aria-haspopup': 'listbox', 'aria-expanded': 'false', 'aria-controls': id, 'aria-label': ariaLabel });
    btn.innerHTML = (iconName ? icon(iconName, 'sm') : '') + '<span></span><svg class="au-ic au-chev" viewBox="0 0 24 24" stroke-width="1.7" aria-hidden="true">' + ICONS.chev + '</svg>';
    var pop = h('div', 'au-pop tr-pop', { id: id, role: 'listbox', 'aria-label': ariaLabel });
    wrap.append(btn, pop);
    var self = { el: wrap, btn: btn, pop: pop };

    function opts() { return Array.prototype.slice.call(pop.querySelectorAll('[role=option]')); }
    self.open = function () {
      closeAllCombos(self);
      if (self.onOpen) self.onOpen();
      pop.classList.add('open');
      btn.setAttribute('aria-expanded', 'true');
      var sel = pop.querySelector('[aria-selected=true]') || opts()[0];
      if (sel) { sel.focus({ preventScroll: true }); sel.scrollIntoView({ block: 'nearest' }); }
    };
    self.close = function (focusBtn) {
      if (!pop.classList.contains('open')) return;
      pop.classList.remove('open');
      btn.setAttribute('aria-expanded', 'false');
      if (focusBtn) btn.focus();
    };
    self.setLabel = function (t) { btn.querySelector('span').textContent = t; };
    self.setItems = function (items) {
      pop.textContent = '';
      items.forEach(function (it) {
        if (it.heading) { pop.append(h('div', 'tr-pop-head', { role: 'presentation', text: it.heading })); return; }
        if (it.divider) { pop.append(h('div', 'au-divider tr-pop-div', { role: 'presentation' })); return; }
        var o = h('button', null, { type: 'button', role: 'option', 'aria-selected': it.selected ? 'true' : 'false', 'data-value': it.value, tabindex: '-1' });
        o.append(h('span', 'tr-opt-l', { text: it.label, lang: it.lang || null }));
        if (it.meta) o.append(h('span', 'tr-opt-m', { text: it.meta }));
        if (it.right != null) o.append(h('span', 'tr-opt-r', { text: it.right }));
        pop.append(o);
      });
    };
    btn.addEventListener('click', function () {
      if (pop.classList.contains('open')) self.close(); else self.open();
    });
    btn.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); self.open(); }
    });
    pop.addEventListener('click', function (e) {
      var o = e.target.closest('[role=option]');
      if (!o) return;
      self.close(true);
      onPick(o.getAttribute('data-value'));
    });
    pop.addEventListener('keydown', function (e) {
      var list = opts(), i = list.indexOf(document.activeElement);
      if (e.key === 'ArrowDown') { e.preventDefault(); (list[Math.min(i + 1, list.length - 1)] || list[0]).focus(); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); (list[Math.max(i - 1, 0)] || list[0]).focus(); }
      else if (e.key === 'Home') { e.preventDefault(); list[0].focus(); }
      else if (e.key === 'End') { e.preventDefault(); list[list.length - 1].focus(); }
      else if (e.key === 'Escape') { e.preventDefault(); self.close(true); }
      else if (e.key === 'Tab') { self.close(); }
    });
    combos.push(self);
    return self;
  }
  var combos = [];
  function closeAllCombos(except) { combos.forEach(function (c) { if (c !== except) c.close(); }); }

  /* Segmented radio group (au-seg) with arrow keys */
  function Seg(cls, label, opts, onPick) {
    var el = h('div', 'au-seg ' + cls, { role: 'radiogroup', 'aria-label': label });
    opts.forEach(function (o) { el.append(h('button', null, { type: 'button', role: 'radio', 'data-v': o[0], 'aria-checked': 'false', tabindex: '-1', text: o[1] })); });
    var self = { el: el, value: null };
    self.set = function (v) {
      self.value = v;
      Array.prototype.forEach.call(el.children, function (b) {
        var on = b.getAttribute('data-v') === v;
        b.setAttribute('aria-checked', on ? 'true' : 'false');
        b.tabIndex = on ? 0 : -1;
      });
    };
    el.addEventListener('click', function (e) {
      var b = e.target.closest('[data-v]');
      if (b && b.getAttribute('data-v') !== self.value) { self.set(b.getAttribute('data-v')); onPick(self.value); }
    });
    el.addEventListener('keydown', function (e) {
      if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
      e.preventDefault();
      var bs = Array.prototype.slice.call(el.children), i = bs.indexOf(document.activeElement);
      var n = bs[(i + (e.key === 'ArrowRight' ? 1 : bs.length - 1)) % bs.length];
      n.focus(); self.set(n.getAttribute('data-v')); onPick(self.value);
    });
    return self;
  }
  document.addEventListener('pointerdown', function (e) {
    combos.forEach(function (c) { if (!c.el.contains(e.target)) c.close(); });
  });

  // Toolbar
  var langCombo = Combo('tr-lang-combo', T.chooseLanguage, 'globe', function (v) {
    if (v === '__other') openOther(); else chooseLanguage(v);
  });
  langCombo.onOpen = function () { langCombo.setItems(langItems()); };
  var progText = h('span', 'tr-progress');
  var meter = h('div', 'au-meter tr-meter', { role: 'progressbar', 'aria-label': T.progressLabel, 'aria-valuemin': '0', 'aria-valuemax': '100', 'aria-valuenow': '0', style: '--v:0%' });
  var pctText = h('span', 'tr-pct', { 'aria-hidden': 'true' });
  var prog = h('div', 'tr-prog', null, [progText, pctText, meter]);
  var modeSeg = Seg('tr-mode', T.modeLabel, [['visual', T.modeVisual], ['list', T.modeList]], function (v) { setMode(v); });
  modeSeg.el.hidden = true;

  // Draft: a compact item in the toolbar row. Its small menu says where the draft is kept and holds
  // Save Draft File, Load Draft File and Discard (with Undo). Warnings change the item's label, never add a row.
  var draftBtn = h('button', 'tr-draft-btn', { type: 'button', 'aria-haspopup': 'true', 'aria-expanded': 'false', 'aria-controls': 'draft-menu' });
  var draftMenu = h('div', 'au-pop tr-draft-menu', { id: 'draft-menu', role: 'group', 'aria-label': T.draftShort });
  var draftFile = h('input', null, { type: 'file', accept: '.json,application/json', hidden: true, tabindex: '-1', 'aria-hidden': 'true' });
  var draftWrap = h('div', 'tr-draft', { hidden: true }, [draftBtn, draftMenu, draftFile]);
  draftBtn.addEventListener('click', function () {
    if (draftMenu.classList.contains('open')) closeDraftMenu(); else openDraftMenu();
  });
  draftWrap.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && draftMenu.classList.contains('open')) { e.preventDefault(); e.stopPropagation(); closeDraftMenu(true); }
  });
  document.addEventListener('pointerdown', function (e) { if (!draftWrap.contains(e.target)) closeDraftMenu(); });
  draftWrap.addEventListener('focusout', function (e) { if (e.relatedTarget && !draftWrap.contains(e.relatedTarget)) closeDraftMenu(); });
  function openDraftMenu(focusSel) {
    closeAllCombos();
    S.confirmDiscard = false;
    renderStrip();
    draftMenu.classList.add('open');
    draftBtn.setAttribute('aria-expanded', 'true');
    focusMenu(focusSel);
  }
  function focusMenu(focusSel) {
    var f = (focusSel && draftMenu.querySelector(focusSel + ':not(:disabled)')) || draftMenu.querySelector('button:not(:disabled)');
    if (f) f.focus();
  }
  function closeDraftMenu(focusBtn) {
    if (!draftMenu.classList.contains('open')) return;
    draftMenu.classList.remove('open');
    draftBtn.setAttribute('aria-expanded', 'false');
    S.confirmDiscard = false;
    S.draftMsg = '';
    renderStrip();
    if (focusBtn) draftBtn.focus();
  }
  // Who translated the open language (translators.json, through translate-menu.js); hidden when nobody is listed
  var byLine = h('p', 'tr-by', { hidden: true });
  var r1 = h('div', 'tr-r1', null, [langCombo.el, prog, byLine, draftWrap, modeSeg.el]);

  var search = h('input', 'tr-field', { type: 'search', placeholder: T.search, 'aria-label': T.searchLabel, autocomplete: 'off', spellcheck: 'false', enterkeyhint: 'search' });
  var searchWrap = h('div', 'tr-search', { html: icon('search', 'sm') }, [search]);
  var seg = h('div', 'au-seg tr-seg', { role: 'radiogroup', 'aria-label': T.filterLabel });
  [['untranslated', T.filterUntranslated], ['all', T.filterAll], ['changed', T.filterChanged]].forEach(function (f) {
    seg.append(h('button', null, { type: 'button', role: 'radio', 'data-filter': f[0], 'aria-checked': 'false', text: f[1] }));
  });
  var groupCombo = Combo('tr-group-combo', T.groupLabel, null, function (v) {
    S.group = v === '__all' ? '' : v;
    renderList();
  });
  var r2 = h('div', 'tr-r2', null, [searchWrap, seg, groupCombo.el]);
  tools.append(h('div', 'tr-tools-in', null, [r1, r2]));

  seg.addEventListener('click', function (e) {
    var b = e.target.closest('[data-filter]');
    if (b) setFilter(b.getAttribute('data-filter'));
  });
  seg.addEventListener('keydown', function (e) {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
    e.preventDefault();
    var bs = Array.prototype.slice.call(seg.children), i = bs.indexOf(document.activeElement);
    var n = bs[(i + (e.key === 'ArrowRight' ? 1 : bs.length - 1)) % bs.length];
    n.focus(); setFilter(n.getAttribute('data-filter'));
  });
  function setFilter(f) {
    S.filter = f;
    Array.prototype.forEach.call(seg.children, function (b) {
      var on = b.getAttribute('data-filter') === f;
      b.setAttribute('aria-checked', on ? 'true' : 'false');
      b.tabIndex = on ? 0 : -1;
    });
    renderList();
  }

  var searchTimer = 0;
  search.addEventListener('input', function () {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(function () { S.query = search.value; renderList(); }, 200);
  });

  // Body slots
  var otherSlot = h('div', 'tr-slot');
  var stateSlot = h('div', 'tr-slot');
  var list = h('div', 'tr-list');
  body.append(otherSlot, stateSlot, list);

  // Submit bar
  var subStatus = h('p', 'tr-status');
  var submitBtn = h('button', 'au-btn primary lg', { type: 'button', text: T.submit });
  subbar.append(h('div', 'tr-subbar-in', null, [subStatus, submitBtn]));
  submitBtn.addEventListener('click', openSheet);

  /* ---------- language choice ---------- */

  function langItems() {
    var items = [], seen = {};
    var ex = S.existing.slice();
    if (S.code && !S.langs[S.code]) ex.push(S.code);
    if (S.code && S.langs[S.code] && !S.langs[S.code].exists && SUGGESTED.indexOf(S.code) < 0) ex.push(S.code);
    if (ex.length) items.push({ heading: T.existingHeading });
    ex.forEach(function (c) {
      if (seen[c]) return; seen[c] = 1;
      var L = S.langs[c];
      items.push({ value: c, label: langName(c), lang: c, meta: uiName(c) + ' · ' + c, right: L && L.exists ? (L.pct == null ? '' : pf.format(Math.floor(L.pct * 100) / 100)) : T.newLanguage, selected: c === S.code });
    });
    var wanted = SUGGESTED.filter(function (c) { return !seen[c]; });
    if (wanted.length) items.push({ heading: T.suggestedHeading });
    wanted.forEach(function (c) {
      items.push({ value: c, label: nativeName(c), lang: c, meta: uiName(c) + ' · ' + c, right: T.newLanguage, selected: c === S.code });
    });
    items.push({ divider: true });
    items.push({ value: '__other', label: T.other });
    return items;
  }

  function renderLangChoices() {
    langCombo.setItems(langItems());
    langCombo.setLabel(S.code ? langName(S.code) + ' (' + S.code + ')' : T.chooseLanguage);
    if (S.status === 'empty') renderState();
  }

  // Replaces the current entry, keeping its state (an entry pushed from the landing stays marked as such)
  function setHash(code) {
    try { history.replaceState(history.state, '', code ? '#lang=' + code : location.pathname + location.search); } catch (e) { /* file:// */ }
  }

  function chooseLanguage(code) {
    closeOther();
    if (code === S.code && S.status === 'ready') return;
    var tok = ++S.token;
    S.code = code;
    S.status = 'loading';
    setHash(code);
    renderAll();
    Promise.all([loadEnglish(), loadLangFile(code), loadVisual()]).then(function (res) {
      if (tok !== S.token) return;
      var f = res[1];
      S.base = f.data && typeof f.data === 'object' && !Array.isArray(f.data) ? f.data : {};
      S.isNew = !f.exists;
      if (f.exists) setLangData(code, S.base); else ensureLang(code, false);
      loadDraft(code);
      S.filter = 'untranslated'; S.query = ''; S.group = ''; search.value = '';
      dropBorrowed();
      rowCache = new Map();
      visualReset();
      S.bad = {};
      S.enKeys.forEach(function (k) {
        var r = C.validate(S.en[k], val(k), code);
        if (r.errors.length) S.bad[k] = true;
      });
      S.status = 'ready';
      renderAll();
      tryResume();
    }, function (err) {
      if (tok !== S.token) return;
      S.status = 'error'; S.error = err || {};
      renderAll();
    });
  }

  /* Other language */
  function openOther() {
    S.otherOpen = true; S.otherError = '';
    renderOther();
    var inp = otherSlot.querySelector('input');
    if (inp) { inp.focus(); otherSlot.scrollIntoView({ block: 'nearest' }); }
  }
  function closeOther() {
    if (!S.otherOpen) return;
    S.otherOpen = false;
    if (location.hash === '#other') setHash(S.code);
    renderOther();
  }
  function renderOther() {
    otherSlot.textContent = '';
    if (!S.otherOpen) return;
    // One compact row: title, code field, Start, Cancel, then one .meta slot that holds the hint, the
    // language name of the typed code, or the error.
    var inp = h('input', 'tr-field tr-code', { id: 'other-code', type: 'text', placeholder: T.otherPlaceholder, autocomplete: 'off', autocapitalize: 'off', spellcheck: 'false', 'aria-label': T.otherLabel, 'aria-describedby': 'other-hint other-err' });
    var hint = h('p', 'meta tr-other-hint', { id: 'other-hint', title: T.otherHint }, [T.otherHintLead + ' ']);
    T.otherHintCodes.forEach(function (c, i) {
      if (i) hint.append(' · ');
      hint.append(h('span', 'tr-other-code', { lang: 'zxx', text: c }));
    });
    var name = h('p', 'meta tr-other-name', { 'aria-live': 'polite', hidden: true });
    var err = h('p', 'tr-warn is-danger tr-other-err', { id: 'other-err', role: 'alert', hidden: !S.otherError, html: icon('alert', 'sm') });
    err.append(h('span', null, { text: S.otherError }));
    hint.hidden = !!S.otherError;
    var cancel = h('button', 'au-btn', { type: 'button', text: T.cancel });
    var form = h('form', 'au-panel tr-other', { novalidate: true, 'aria-labelledby': 'other-title' }, [
      h('h2', 'tr-other-t', { id: 'other-title', text: T.otherTitle }),
      h('div', 'tr-other-field', null, [inp]),
      h('div', 'tr-other-btns', null, [h('button', 'au-btn primary', { type: 'submit', text: T.otherStart }), cancel]),
      h('div', 'tr-other-info', null, [hint, name, err])
    ]);
    inp.addEventListener('input', function () {
      var r = C.canonicalCode(inp.value);
      name.textContent = r.ok ? nativeName(r.code) + ' · ' + uiName(r.code) : '';
      name.hidden = !r.ok;
      hint.hidden = r.ok;
      inp.classList.remove('is-bad');
      err.hidden = true;
    });
    form.addEventListener('submit', function (e) {
      e.preventDefault();
      var r = C.canonicalCode(inp.value);
      if (!r.ok) {
        S.otherError = r.reason === 'source' ? T.codeSource : r.reason === 'maintained' ? T.codeMaintained : T.codeFormat;
        err.lastChild.textContent = S.otherError;
        err.hidden = false;
        hint.hidden = true; name.hidden = true;
        inp.classList.add('is-bad');
        inp.focus();
        return;
      }
      chooseLanguage(r.code);
    });
    cancel.addEventListener('click', function () {
      // Opened as #other from the landing, or with no language open: Cancel goes back to the landing
      var back = location.hash === '#other' && (!S.code || !!(history.state && history.state.trLanding));
      closeOther();
      if (back) toLanding(); else langCombo.btn.focus();
    });
    otherSlot.append(form);
  }

  /* ---------- rendering ---------- */

  function renderAll() {
    renderLangChoices();
    var ready = S.status === 'ready';
    prog.hidden = !ready;
    r2.hidden = !ready;
    subbar.hidden = !ready;
    app.classList.toggle('is-ready', ready);
    renderOther();
    renderState();
    renderStrip();
    renderCredit();
    if (ready) {
      setFilter(S.filter);
      updateProgress();
      updateSubbar();
    } else {
      renderTok++;
      list.textContent = '';
    }
    applyMode();
  }

  function renderCredit() {
    var names = Langs && Langs.creditsFor && S.status === 'ready' && S.code ? Langs.creditsFor(S.code) : [];
    var f = names.length ? Langs.creditNodes(T.translatedBy, names) : null;
    byLine.textContent = '';
    if (f) byLine.append(f);
    byLine.hidden = !f;
  }

  function renderState() {
    stateSlot.textContent = '';
    if (S.status === 'empty') stateSlot.append(pickPanel());
    else if (S.status === 'loading') stateSlot.append(loadingPanel());
    else if (S.status === 'error') stateSlot.append(errorPanel());
  }

  function pickPanel() {
    var p = h('section', 'au-panel tr-pick', null, [h('h2', 'au-h1', { text: T.chooseToStart }), h('p', 'tr-sub', { text: T.subtitle })]);
    var grid = null;
    langItems().forEach(function (it) {
      if (it.heading) { p.append(h('h3', 'au-section tr-pick-h', { text: it.heading })); grid = h('div', 'tr-langs'); p.append(grid); return; }
      if (it.divider || !grid) { grid = h('div', 'tr-langs'); p.append(grid); if (it.divider) return; }
      var b = h('button', 'tr-lang', { type: 'button', 'data-value': it.value }, [
        h('span', 'tr-lang-name', { text: it.label, lang: it.lang || null }),
        it.meta ? h('span', 'tr-lang-meta', { text: it.meta }) : null,
        it.right != null ? h('span', 'tr-lang-pct', { text: it.right }) : null
      ]);
      if (it.value === '__other') b.classList.add('tr-lang-other');
      grid.append(b);
    });
    p.addEventListener('click', function (e) {
      var b = e.target.closest('.tr-lang');
      if (!b) return;
      var v = b.getAttribute('data-value');
      if (v === '__other') openOther(); else chooseLanguage(v);
    });
    return p;
  }

  function loadingPanel() {
    var p = h('section', 'au-panel tr-loading', { 'aria-busy': 'true' }, [h('p', 'au-section', { role: 'status', text: T.loading })]);
    for (var i = 0; i < 6; i++) {
      if (i) p.append(h('div', 'au-divider'));
      p.append(h('div', 'tr-row tr-skel-row', { 'aria-hidden': 'true' }, [
        h('div', null, null, [h('i', 'tr-skel tr-skel-k'), h('i', 'tr-skel tr-skel-t' + (i % 3))]),
        h('div', null, null, [h('i', 'tr-skel tr-skel-in')])
      ]));
    }
    return p;
  }

  function errorPanel() {
    var e = S.error || {};
    var reason = e.status ? T.failStatus(e.status) : e.parse ? T.failParse : T.failNetwork;
    var retry = h('button', 'au-btn primary', { type: 'button', html: icon('retry', 'sm') + '<span>' + T.tryAgain + '</span>' });
    retry.addEventListener('click', function () {
      var code = S.code;
      if (S.langs[code]) S.langs[code].data = null;
      S.code = null;
      chooseLanguage(code);
    });
    var file = S.code && e.status && S.langs[S.code] && S.langs[S.code].exists ? S.code : 'en';
    return h('section', 'au-panel tr-fail', { role: 'alert' }, [
      h('h2', 'au-h1', { text: T.failTitle }),
      h('p', 'au-body', { text: reason }),
      h('div', 'tr-actions', null, [retry, h('a', 'au-btn', { href: REPO + '/blob/main/' + DIR + '/' + file + '.json', target: '_blank', rel: 'noopener', html: icon('out', 'sm') + '<span>' + T.viewOnGitHub + '</span>' })])
    ]);
  }

  // The draft item in the toolbar: its label carries the one warning that matters right now
  function draftState() {
    var st = S.storeStatus || {};
    if (st.nudge === 'unsafe') return { label: T.draftNotSavedShort, icon: 'alert', warn: true };
    if (S.undo) return { label: T.draftDiscarded, icon: 'clock' };
    if (st.nudge === 'big') return { label: T.draftBackUp, icon: 'down' };
    if (S.restored) return { label: T.draftRestoredShort, icon: 'clock' };
    return { label: T.draftShort, icon: 'clock' };
  }
  var draftShown = '';
  function renderDraftItem() {
    var on = S.status === 'ready' && !!S.store;
    draftWrap.hidden = !on;
    if (!on) return;
    var d = draftState(), sig = d.label + '|' + d.icon;
    if (sig === draftShown) return;
    draftShown = sig;
    draftBtn.innerHTML = icon(d.icon, 'sm') + '<span class="tr-draft-l"></span>' + icon('chev', 'sm au-chev');
    draftBtn.querySelector('.tr-draft-l').textContent = d.label;
    draftBtn.classList.toggle('is-warn', !!d.warn);
    draftBtn.setAttribute('aria-label', d.label);
    draftBtn.title = d.label;
  }

  // The menu: what the draft holds, where it is kept, then the file buttons and Discard / Undo
  function renderStrip() {
    renderDraftItem();
    // a re-render keeps the focused menu button focused
    var ae = document.activeElement, keep = ae && draftMenu.contains(ae) && ae.className ? '.' + ae.className.trim().split(/\s+/).pop() : null;
    draftMenu.textContent = '';
    if (draftWrap.hidden) { draftMenu.classList.remove('open'); draftBtn.setAttribute('aria-expanded', 'false'); return; }
    var st = S.store.status(), n = st.changed, all = n + st.orphans;
    var head;
    if (S.undo) head = T.discarded(fmt(S.undo.n));
    else if (S.restored && n) head = T.restored(fmt(n), df.format(new Date(S.restored.saved)));
    else head = n ? T.draftHolds(fmt(n)) : T.nothingChanged;
    var notes = [h('p', 'tr-draft-h', { role: 'status', text: head })];
    var where = st.mode === 'memory' ? T.whereMemory : st.mode === 'idb' ? T.whereIdb : T.whereLocal;
    notes.push(h('p', st.mode === 'memory' ? 'tr-draft-n is-warn' : 'tr-draft-n', { text: where }));
    if (st.nudge === 'big') notes.push(h('p', 'tr-draft-n', { text: T.nudgeBig }));
    var gh = S.store.conflicts().length;
    if (gh) notes.push(h('p', 'tr-draft-n', { text: T.ghChangedCount(fmt(gh)) }));
    if (st.orphans) notes.push(h('p', 'tr-draft-n', { text: T.orphans(fmt(st.orphans)) }));
    if (S.draftMsg) notes.push(h('p', 'tr-draft-n tr-draft-msg', { role: 'status', text: S.draftMsg }));

    var save = h('button', 'au-btn tr-draft-save', { type: 'button', disabled: !all, html: icon('down', 'sm') + '<span>' + T.saveDraftFile + '</span>' });
    var load = h('button', 'au-btn tr-draft-load', { type: 'button', html: icon('upload', 'sm') + '<span>' + T.loadDraftFile + '</span>' });
    save.addEventListener('click', saveDraftFile);
    load.addEventListener('click', function () { draftFile.click(); });
    var acts = [h('div', 'tr-actions', null, [save, load])];

    var canUndo = !!window.indexedDB;
    if (S.undo) {
      var undo = h('button', 'au-btn tr-undo', { type: 'button', text: T.undo });
      undo.addEventListener('click', undoDiscard);
      acts.push(h('div', 'tr-actions', null, [undo]));
    } else if (S.confirmDiscard) {
      var yes = h('button', 'au-btn tr-danger', { type: 'button', text: T.discard });
      var no = h('button', 'au-btn tr-keep', { type: 'button', text: T.keep });
      yes.addEventListener('click', discardDraft);
      no.addEventListener('click', function () { S.confirmDiscard = false; renderStrip(); focusMenu('.tr-discard'); });
      notes.push(h('p', 'tr-draft-n', { text: T.discardAsk(fmt(all), canUndo) }));
      acts.push(h('div', 'tr-actions', null, [yes, no]));
    } else if (all) {
      var b = h('button', 'au-btn tr-quiet tr-discard', { type: 'button', text: T.discardDraft });
      b.addEventListener('click', function () { S.confirmDiscard = true; renderStrip(); focusMenu('.tr-keep'); });
      acts.push(h('div', 'tr-actions', null, [b]));
    }
    draftMenu.append.apply(draftMenu, notes.concat(acts));
    if (keep) focusMenu(keep);
  }

  function saveDraftFile() {
    var f = S.store.exportFile();
    var url = URL.createObjectURL(new Blob([f.text], { type: 'application/json' }));
    var a = h('a', null, { href: url, download: f.name, hidden: true });
    draftWrap.append(a);
    a.click();
    a.remove();
    setTimeout(function () { URL.revokeObjectURL(url); }, 4000);
    S.draftMsg = T.savedAs(f.name);
    renderStrip();
  }

  draftFile.addEventListener('change', function () {
    var f = draftFile.files && draftFile.files[0];
    draftFile.value = '';
    if (!f || !S.store) return;
    var store = S.store;
    var rd = new FileReader();
    rd.onload = function () {
      if (store !== S.store) return;
      var r = store.importText(String(rd.result));
      if (!r.ok) S.draftMsg = r.reason === 'otherLanguage' ? T.loadOther(r.code, S.code) : T.loadBad;
      else if (!r.added && !r.replaced) S.draftMsg = r.kept ? T.loaded('0', '0', fmt(r.kept)) : T.loadNothing;
      else { S.draftMsg = T.loaded(fmt(r.added), fmt(r.replaced), r.kept ? fmt(r.kept) : ''); S.undo = null; S.edited = true; }
      if (!draftMenu.classList.contains('open')) openDraftMenu('.tr-draft-load');
      else { renderStrip(); focusMenu('.tr-draft-load'); }
    };
    rd.onerror = function () { S.draftMsg = T.loadBad; renderStrip(); };
    rd.readAsText(f);
  });

  // Rows are rebuilt from the store (after Discard or Undo)
  function rebuildRows() {
    var key = dropBorrowed();
    rowCache = new Map();
    S.bad = {};
    S.enKeys.forEach(function (k) { if (C.validate(S.en[k], val(k), S.code).errors.length) S.bad[k] = true; });
    renderList();
    updateProgress();
    updateSubbar();
    visualRefresh(key);
  }

  function discardDraft() {
    var st = S.store.status(), n = st.changed + st.orphans;
    S.store.discard();
    S.restored = null; S.confirmDiscard = false; S.draftMsg = '';
    S.undo = window.indexedDB ? { n: n } : null;
    rebuildRows();
    renderStrip();
    focusMenu('.tr-undo');
  }

  function undoDiscard() {
    var store = S.store;
    store.undoDiscard().then(function (n) {
      if (store !== S.store) return;
      S.undo = null;
      S.draftMsg = n ? T.undone(fmt(n)) : T.undoGone;
      rebuildRows();
      renderStrip();
      focusMenu('.tr-draft-save');
    });
  }

  function updateProgress() {
    var total = S.enKeys.length;
    var done = C.countTranslated(S.enKeys, S.base, draftValues());
    progText.textContent = T.progress(fmt(done), fmt(total));
    var pct = total ? Math.round(done / total * 1000) / 10 : 0;
    meter.style.setProperty('--v', pct + '%');
    pctText.textContent = pf.format(total ? Math.floor(done / total * 100) / 100 : 0);
    meter.setAttribute('aria-valuenow', String(pct));
    var L = S.langs[S.code];
    if (L && L.exists) L.pct = total ? done / total : 0;
  }

  function updateSubbar() {
    var n = changedCount();
    var kept = !S.storeStatus || S.storeStatus.mode !== 'memory';
    subStatus.textContent = n
      ? T.changedCount(fmt(n)) + ' · ' + (kept ? T.draftSaved : T.draftNotSaved)
      : (kept ? T.nothingChanged : T.draftNotSaved);
    submitBtn.disabled = n === 0;
  }

  /* Rows */

  function groupLabel(g) { return g === '@' ? T.nameGroup : g; }

  function chipsFor(en) {
    var p = C.parse(en);
    var names = p.ok ? p.names : C.placeholders(en);
    var info = C.pluralInfo(S.code);
    var needs = info.required.some(function (c) { return c !== 'other'; });
    return names.map(function (n) {
      if (p.ok && p.plurals[n] && needs) {
        var body = info.required.map(function (c) { return c + ' {#}'; }).join(' ');
        return { label: '{' + n + ', plural…}', insert: '{' + n + ', plural, ' + body + '}' };
      }
      return { label: '{' + n + '}', insert: '{' + n + '}' };
    });
  }

  function makeRow(k) {
    var en = S.en[k];
    var tag = h('span', 'au-tag tr-changed', { text: T.englishChanged, hidden: true });
    var ghTag = h('span', 'au-tag tr-changed', { text: T.ghChanged, hidden: true });
    var tabTag = h('span', 'au-tag tr-changed', { text: T.otherTab, hidden: true });
    var left = h('div', 'tr-src', null, [
      h('div', 'tr-key', null, [h('span', 'tr-keytext', { text: k }), tag, ghTag, tabTag]),
      h('p', 'tr-en', { lang: 'en', text: en })
    ]);
    if (k === '@name') left.append(h('p', 'au-caption tr-note', { text: T.nameHint }));
    var chips = chipsFor(en);
    if (chips.length) {
      var box = h('div', 'tr-phs');
      chips.forEach(function (c) {
        box.append(h('button', 'tr-ph', { type: 'button', 'data-insert': c.insert, 'aria-label': T.insert(c.label), text: c.label }));
      });
      left.append(box);
    }
    var parsed = C.parse(en);
    if (parsed.ok && Object.keys(parsed.plurals).length) {
      var info = C.pluralInfo(S.code);
      var t = T.pluralForms(langName(S.code), info.required.join(' · '));
      if (info.optional.length) t += T.pluralOptional(info.optional.join(' · '));
      left.append(h('p', 'au-caption tr-note', { text: t }));
    }
    var ghNote = h('p', 'au-caption tr-note tr-ghnow', { hidden: true });
    var seen = h('div', 'tr-seen', { hidden: true });
    left.append(ghNote, seen);
    var v = val(k);
    var ta = h('textarea', 'tr-input', { dir: 'auto', lang: S.code, rows: '1', 'data-key': k, 'aria-label': T.translationOf(k), placeholder: k === '@name' ? nativeName(S.code) : null });
    ta.value = v == null ? '' : v;
    if (growByJs) ta.rows = Math.max(1, Math.min(12, Math.ceil(Math.max(C.charLength(ta.value), C.charLength(en)) / 55)));
    var warns = h('div', 'tr-warns');
    var row = h('div', 'tr-row', { 'data-key': k }, [left, h('div', 'tr-dst', null, [ta, warns])]);
    var rec = { el: row, ta: ta, warns: warns, tag: tag, ghTag: ghTag, ghNote: ghNote, tabTag: tabTag, remote: false, key: k, seen: seen };
    refreshRow(rec);
    fillSeen(rec);
    return rec;
  }

  function getRow(k) {
    var r = rowCache.get(k);
    if (!r) { r = makeRow(k); rowCache.set(k, r); }
    return r;
  }

  function warnLine(text, danger) {
    var p = h('p', 'tr-warn' + (danger ? ' is-danger' : ''), { html: icon(danger ? 'alert' : 'width', 'sm') });
    p.append(h('span', null, { text: text }));
    return p;
  }

  function refreshRow(rec) {
    var k = rec.key, text = rec.ta.value;
    var d = draftItem(k);
    // a string loaded from a plain translation file has no English recorded
    rec.tag.hidden = !(d && d.en && d.en !== S.en[k]);
    var gh = ghChanged(k);
    rec.ghTag.hidden = !gh;
    rec.ghNote.hidden = !gh;
    rec.ghNote.textContent = gh ? T.ghNow(baseText(k)) : '';
    var r = C.validate(S.en[k], text, S.code);
    rec.warns.textContent = '';
    r.errors.forEach(function (e) {
      var t;
      if (e.kind === 'syntax') t = T.syntax[e.code] || T.syntax.badArg;
      else if (e.kind === 'missing') t = T.missing('{' + e.name + '}') + (e.quoteHint ? '. ' + T.quoteHint : '');
      else if (e.kind === 'extra') t = T.extra('{' + e.name + '}');
      else if (e.kind === 'pluralDropped') t = T.pluralDropped(e.name);
      else if (e.kind === 'html') t = T.htmlTag;
      else if (e.kind === 'url') t = T.urlAdded;
      else if (e.kind === 'tooLong') t = T.tooLong(fmt(e.max));
      rec.warns.append(warnLine(t, true));
    });
    r.warnings.forEach(function (w) {
      var t;
      if (w.kind === 'pluralMissing') t = T.pluralMissing(w.cats.join(', '));
      else if (w.kind === 'pluralUnused') t = T.pluralUnused(w.cats.join(', '));
      else if (w.kind === 'length') t = T.longer(fmt(w.len), fmt(w.enLen));
      rec.warns.append(warnLine(t, false));
    });
    rec.ta.classList.toggle('is-bad', r.errors.length > 0);
    rec.ta.setAttribute('aria-invalid', r.errors.length ? 'true' : 'false');
    if (r.errors.length) S.bad[k] = true; else delete S.bad[k];
  }

  function grow(ta) {
    if (!growByJs) return;
    ta.style.height = 'auto';
    ta.style.height = (ta.scrollHeight + 2) + 'px';
  }

  function bindRowEvents(box) {
  box.addEventListener('input', function (e) {
    var ta = e.target;
    if (!ta.classList || !ta.classList.contains('tr-input')) return;
    var k = ta.getAttribute('data-key');
    if (!S.store) return;
    // stored at once, one entry per string; this tab's latest typing wins over a change from another tab
    S.store.set(k, ta.value, S.en[k]);
    if (!S.edited) {
      S.edited = true;
      if (!S.persistAsked) { S.persistAsked = true; DraftStore.requestPersistence(); }
    }
    if (S.undo) { S.undo = null; renderDraftItem(); }
    var rec = rowCache.get(k);
    if (rec) { rec.remote = false; rec.tabTag.hidden = true; refreshRow(rec); }
    grow(ta);
    updateProgress();
    updateSubbar();
    visualInput(k);
  });
  // A string another tab changed while this field had the caret: take that text once the field is left
  box.addEventListener('focusout', function (e) {
    var ta = e.target;
    if (!ta.classList || !ta.classList.contains('tr-input')) return;
    var rec = rowCache.get(ta.getAttribute('data-key'));
    if (!rec || !rec.remote) return;
    rec.remote = false;
    rec.tabTag.hidden = true;
    var v = val(rec.key);
    ta.value = v == null ? '' : v;
    grow(ta);
    refreshRow(rec);
    visualInput(rec.key);
  });
  box.addEventListener('focusin', function (e) {
    if (e.target.classList && e.target.classList.contains('tr-input')) {
      grow(e.target);
      if (box === list) S.lastListKey = e.target.getAttribute('data-key');
    }
  });
  // Chips keep the textarea's focus and caret.
  box.addEventListener('mousedown', function (e) {
    if (e.target.closest('.tr-ph')) e.preventDefault();
  });
  box.addEventListener('click', function (e) {
    var chip = e.target.closest('.tr-ph');
    if (chip) {
      var ta = chip.closest('.tr-row').querySelector('.tr-input');
      var len = ta.value.length;
      var s = typeof ta.selectionStart === 'number' ? ta.selectionStart : len;
      var en = typeof ta.selectionEnd === 'number' ? ta.selectionEnd : s;
      ta.focus();
      ta.setRangeText(chip.getAttribute('data-insert'), s, en, 'end');
      ta.dispatchEvent(new Event('input', { bubbles: true }));
      return;
    }
    var show = e.target.closest('[data-show-all]');
    if (show) { S.query = ''; search.value = ''; S.group = ''; setFilter('all'); }
    var on = e.target.closest('[data-show-on]');
    if (on) showOnScreen(on.getAttribute('data-show-on'));
    var tog = e.target.closest('[data-seen-toggle]');
    if (tog) {
      var seenList = tog.closest('.tr-seen').querySelector('.tr-seen-list');
      var open = seenList.hidden;
      seenList.hidden = !open;
      tog.setAttribute('aria-expanded', open ? 'true' : 'false');
      tog.firstChild.textContent = open ? T.seenHide : T.seenShow;
      return;
    }
    var jump = e.target.closest('[data-show-shot]');
    if (jump) showKeyOn(jump.getAttribute('data-show-key'), +jump.getAttribute('data-show-shot'));
  });
  }
  bindRowEvents(list);

  function matches(k, q) {
    if (!q) return true;
    var v = val(k);
    return k.toLowerCase().indexOf(q) >= 0 || S.en[k].toLowerCase().indexOf(q) >= 0 || (typeof v === 'string' && v.toLowerCase().indexOf(q) >= 0);
  }

  function renderList() {
    if (S.status !== 'ready') return;
    var tok = ++renderTok;
    var q = S.query.trim().toLowerCase();
    var groups = [], byName = {}, total = 0;
    S.enKeys.forEach(function (k) {
      var v = val(k);
      if (S.filter === 'untranslated' && C.hasText(v)) return;
      if (S.filter === 'changed' && !draftItem(k)) return;
      if (!matches(k, q)) return;
      var g = C.groupOf(k);
      var G = byName[g];
      if (!G) { G = byName[g] = { name: g, keys: [] }; groups.push(G); }
      G.keys.push(k);
      total++;
    });

    // group picker with counts for the current filter and search
    var items = [{ value: '__all', label: T.allGroups, right: fmt(total), selected: !S.group }];
    groups.forEach(function (G) { items.push({ value: G.name, label: groupLabel(G.name), right: fmt(G.keys.length), selected: G.name === S.group }); });
    if (S.group && !byName[S.group]) items.push({ value: S.group, label: groupLabel(S.group), right: '0', selected: true });
    groupCombo.setItems(items);
    groupCombo.setLabel(S.group ? groupLabel(S.group) + ' (' + fmt(byName[S.group] ? byName[S.group].keys.length : 0) + ')' : T.allGroups);

    if (S.group) groups = byName[S.group] ? [byName[S.group]] : [];
    list.textContent = '';
    if (borrowed) borrowed.ph = null;
    if (!groups.length) {
      var msg, act = null;
      if (S.filter === 'untranslated' && !q) { msg = T.allDone; act = h('button', 'au-btn', { type: 'button', 'data-show-all': '1', text: T.showAll }); }
      else if (S.filter === 'changed' && !q) msg = T.noChanges;
      else msg = T.noMatch;
      list.append(h('section', 'au-panel tr-empty', null, [h('p', 'au-h2', { text: msg }), act]));
      return;
    }
    var gi = 0;
    (function step() {
      if (tok !== renderTok) return;
      var budget = 220;
      while (gi < groups.length && budget > 0) {
        var G = groups[gi++];
        list.append(makePanel(G));
        budget -= G.keys.length;
      }
      if (gi < groups.length) requestAnimationFrame(step);
    })();
  }

  function makePanel(G) {
    var p = h('section', 'au-panel tr-group', { style: 'contain-intrinsic-size:auto ' + (G.keys.length * 96 + 60) + 'px' });
    p.append(h('h2', 'au-section tr-gh', null, [h('span', null, { text: groupLabel(G.name) }), h('span', 'au-tag', { text: fmt(G.keys.length) })]));
    G.keys.forEach(function (k, i) {
      if (i) p.append(h('div', 'au-divider'));
      if (borrowed && borrowed.key === k) p.append(borrowed.ph = h('div', 'tr-row-ph', { 'data-key': k }));
      else p.append(getRow(k).el);
    });
    return p;
  }

  /* ---------- submit sheet ----------
     Three ways to send, in this order:
       1. Send with GitHub: the page opens one issue in the volunteer's name (GitHub App, Issues only); the repo's
          intake Action makes the pull request. Shown only when config.js names the App and the Worker.
       2. Paste in Browser: copy the file, paste it into GitHub's web editor, propose the change.
       3. Upload Form: download the file and attach it to the repo's translation issue form.
     The same checks run before any of them; Send with GitHub and the form wait until the problems are fixed. */

  var CFG = window.TrConfig || {};
  var GHI = window.GhIssue;
  var REPO_NAME = typeof CFG.repo === 'string' && /^[\w.-]+\/[\w.-]+$/.test(CFG.repo) ? CFG.repo : 'Midzer00/ds5-untethered';
  function ghConfigured() {
    var id = String(CFG.clientId || ''), w = String(CFG.workerUrl || '');
    if (!GHI || !GHI.supported(window)) return false;
    if (!/^Iv[A-Za-z0-9._-]{8,}$/.test(id)) return false;
    return /^https:\/\/[^\s/?#]+(\/[^\s?#]*)?$/.test(w) || /^http:\/\/(localhost|127\.0\.0\.1)(:\d+)?(\/[^\s?#]*)?$/.test(w);
  }
  var GH_ON = ghConfigured();
  var auth = GH_ON ? GHI.createAuth({ clientId: CFG.clientId, workerUrl: CFG.workerUrl, redirectUri: new URL('oauth-callback.html', location.href).href }) : null;
  // phase: idle | auth | send | done | error. progress survives a failed send so Try Again continues the same issue.
  var G = { phase: 'idle', status: '', sig: '', issueP: null, progress: {}, existing: null, result: null, error: null, code: null, blocked: false };
  var pendingResume = null;

  var sheet = document.getElementById('sheet');
  var copyBtn, dlBtn, ghBtn, step1Text, step2Text, problemBox, summary, ways = {}, wayList, ghBox, formStep1Text, formDlBtn, formLink, formNote, lastWay = null;
  // Do not list my name: Send with GitHub marks the issue; the form and a pull request need the volunteer to say so
  // (GitHub's issue-form URL parameters fill text fields only, not checkboxes)
  var UNLISTED_KEY = 'ds5-translate:unlisted';
  var unlistedBox, pasteUnlisted, formUnlisted;
  function unlisted() { return !!(unlistedBox && unlistedBox.checked); }
  (function buildSheet() {
    var closeBtn = h('button', 'au-btn tr-icon-btn', { type: 'button', 'aria-label': T.close, html: icon('close') });
    closeBtn.addEventListener('click', function () { sheet.close(); });
    copyBtn = h('button', 'au-btn primary', { type: 'button', html: icon('copy', 'sm') + '<span>' + T.copyFile + '</span>' });
    dlBtn = h('button', 'au-btn', { type: 'button', html: icon('down', 'sm') + '<span>' + T.download + '</span>' });
    ghBtn = h('a', 'au-btn', { target: '_blank', rel: 'noopener', html: icon('out', 'sm') + '<span>' + T.openOnGitHub + '</span>' });
    step1Text = h('p');
    step2Text = h('p', null, null);
    problemBox = h('div', 'tr-problems');
    summary = h('p', 'au-caption tr-sheet-sum');
    function step(n, title, text, actions) {
      return h('li', 'tr-step', null, [
        h('span', 'tr-step-n', { 'aria-hidden': 'true', text: String(n) }),
        h('div', null, null, [h('h3', null, { text: title }), text, actions ? h('div', 'tr-actions', null, actions) : null])
      ]);
    }
    function way(id, title, tag, kids) {
      var d = h('details', 'tr-way', { name: 'tr-way', 'data-way': id });
      d.append(
        h('summary', 'tr-way-sum', null, [
          h('span', 'tr-way-t', { text: title }),
          tag ? h('span', 'tr-way-tag', { text: tag }) : null,
          h('span', 'tr-way-chev', { html: icon('chev', 'sm') })
        ]),
        h('div', 'tr-way-in', null, kids)
      );
      ways[id] = d;
      return d;
    }
    ghBox = h('div', 'tr-gh');
    formStep1Text = h('p');
    formDlBtn = h('button', 'au-btn primary', { type: 'button', html: icon('down', 'sm') + '<span>' + T.download + '</span>' });
    formLink = h('a', 'au-btn', { target: '_blank', rel: 'noopener', html: icon('out', 'sm') + '<span>' + T.openForm + '</span>' });
    formNote = h('p', 'au-caption tr-way-note', { text: T.ghFixFirst, hidden: true });
    unlistedBox = h('input', null, { type: 'checkbox', id: 'unlisted' });
    unlistedBox.checked = storeGet(UNLISTED_KEY) === '1';
    pasteUnlisted = h('p', 'au-caption tr-way-note', { text: T.unlistedPaste });
    formUnlisted = h('p', 'au-caption tr-way-note', { text: T.unlistedForm });
    unlistedBox.addEventListener('change', function () {
      if (unlistedBox.checked) storeSet(UNLISTED_KEY, '1');
      else { try { window.localStorage.removeItem(UNLISTED_KEY); } catch (e) { /* storage may be blocked */ } }
      renderUnlisted();
    });
    wayList = h('div', 'tr-ways', { role: 'group', 'aria-label': T.waysLabel }, [
      GH_ON ? way('gh', T.wayGitHub, T.recommended, [ghBox]) : null,
      way('paste', T.wayPaste, null, [h('ol', 'tr-steps', null, [
        step(1, T.step1Title, step1Text, [copyBtn, dlBtn]),
        step(2, T.step2Title, step2Text, [ghBtn]),
        step(3, T.step3Title, h('p', null, { text: T.step3Text }))
      ]), pasteUnlisted]),
      way('form', T.wayForm, null, [h('ol', 'tr-steps', null, [
        step(1, T.formStep1Title, formStep1Text, [formDlBtn]),
        step(2, T.formStep2Title, h('p', null, { text: T.formStep2Text }), [formLink])
      ]), formUnlisted, formNote])
    ]);
    sheet.append(h('div', 'tr-sheet-in', null, [
      h('div', 'tr-sheet-head', null, [h('h2', 'au-h1', { id: 'sheet-title', text: T.sheetTitle }), closeBtn]),
      problemBox,
      summary,
      h('label', 'tr-unlisted', { for: 'unlisted' }, [unlistedBox, h('span', null, { text: T.unlisted })]),
      wayList,
      h('p', 'tr-guide', null, [h('a', null, { href: GUIDE, target: '_blank', rel: 'noopener', text: T.guide })])
    ]));
    sheet.setAttribute('aria-labelledby', 'sheet-title');
    sheet.addEventListener('click', function (e) { if (e.target === sheet) sheet.close(); });
    sheet.addEventListener('close', function () {
      // a token kept for Try Again goes when the sheet does (never while GitHub is still being asked)
      if (auth && G.phase !== 'auth' && G.phase !== 'send') auth.revoke();
    });
    // One way open at a time, also where <details name> is not supported
    wayList.addEventListener('toggle', function (e) {
      var d = e.target;
      if (!d.open || !d.classList || !d.classList.contains('tr-way')) return;
      lastWay = d.getAttribute('data-way');
      Object.keys(ways).forEach(function (id) { if (ways[id] !== d && ways[id].open) ways[id].open = false; });
    }, true);

    var copyTimer = 0;
    copyBtn.addEventListener('click', function () {
      var label = copyBtn.querySelector('span');
      copyText(buildFile()).then(function () {
        label.textContent = T.copied;
      }, function () {
        label.textContent = T.copyFailed;
      }).then(function () {
        clearTimeout(copyTimer);
        copyTimer = setTimeout(function () { label.textContent = T.copyFile; }, 2000);
      });
    });
    dlBtn.addEventListener('click', downloadFile);
    formDlBtn.addEventListener('click', downloadFile);
    formLink.addEventListener('click', function (e) { if (G.blocked) e.preventDefault(); });
  })();

  function renderUnlisted() {
    pasteUnlisted.hidden = formUnlisted.hidden = !unlisted();
  }

  function downloadFile() {
    var blob = new Blob([buildFile()], { type: 'application/json' });
    var url = URL.createObjectURL(blob);
    var a = h('a', null, { href: url, download: S.code + '.json' });
    sheet.append(a);
    a.click();
    a.remove();
    setTimeout(function () { URL.revokeObjectURL(url); }, 2000);
  }

  function buildFile() { return C.buildOutput(S.enKeys, S.base, draftValues()); }

  function copyText(text) {
    function legacy() {
      var ta = h('textarea', null, { readonly: true, 'aria-hidden': 'true', style: 'position:fixed;top:0;left:0;width:1px;height:1px;opacity:0' });
      ta.value = text;
      sheet.append(ta);
      ta.select();
      var ok = false;
      try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
      ta.remove();
      copyBtn.focus();
      if (!ok) throw new Error('copy');
    }
    if (navigator.clipboard && window.isSecureContext) {
      return navigator.clipboard.writeText(text).catch(function () { legacy(); });
    }
    return new Promise(function (res) { legacy(); res(); });
  }

  function badKeys() { return S.enKeys.filter(function (k) { return S.bad[k] && C.hasText(val(k)); }); }

  function openSheet() {
    var file = S.code + '.json';
    step1Text.textContent = T.step1Text(file);
    step2Text.textContent = S.isNew ? T.step2New : T.step2Edit;
    ghBtn.href = S.isNew
      ? REPO + '/new/main/' + DIR + '?filename=' + encodeURIComponent(file)
      : REPO + '/edit/main/' + DIR + '/' + encodeURIComponent(S.code) + '.json';
    formStep1Text.textContent = T.formStep1Text(file);
    var vals = draftValues();
    summary.textContent = T.fileSummary(fmt(C.countTranslated(S.enKeys, S.base, vals)), fmt(S.enKeys.length));
    problemBox.textContent = '';
    var bad = badKeys();
    if (bad.length) {
      var go = h('button', 'au-btn', { type: 'button', text: T.goToProblem });
      go.addEventListener('click', function () { sheet.close(); goToKey(bad[0]); });
      problemBox.append(warnLine(T.problems(fmt(bad.length)), true), go);
    }
    problemBox.hidden = !bad.length;
    G.blocked = bad.length > 0;
    if (G.blocked) { formLink.removeAttribute('href'); formLink.setAttribute('aria-disabled', 'true'); }
    else {
      formLink.href = 'https://github.com/' + REPO_NAME + '/issues/new?template=translation.yml&title=Translation:%20' + encodeURIComponent(S.code) + '&language=' + encodeURIComponent(S.code);
      formLink.removeAttribute('aria-disabled');
    }
    formNote.hidden = !G.blocked;
    renderUnlisted();
    if (G.phase === 'done' || (G.phase === 'error' && G.code !== S.code)) G.phase = 'idle';
    renderGh();
    var open = G.phase !== 'idle' && ways.gh ? 'gh' : (lastWay && ways[lastWay] ? lastWay : (ways.gh ? 'gh' : 'paste'));
    Object.keys(ways).forEach(function (id) { ways[id].open = id === open; });
    copyBtn.querySelector('span').textContent = T.copyFile;
    if (!sheet.open) {
      if (typeof sheet.showModal === 'function') sheet.showModal(); else sheet.setAttribute('open', '');
    }
  }

  /* Way 1 */
  function setGh(phase, status) {
    G.phase = phase;
    G.status = status || '';
    renderGh();
  }
  function ghErrorText(e) {
    var E = T.ghErr, c = e && e.code;
    if (c === 'http') return E.http(e.status);
    return E[c] && typeof E[c] === 'string' ? E[c] : E.other;
  }
  function renderGh() {
    if (!ghBox) return;
    ghBox.textContent = '';
    var p = G.phase;
    if (p === 'done') {
      var link = h('a', 'au-btn primary', { href: G.result.url, target: '_blank', rel: 'noopener', html: icon('out', 'sm') + '<span></span>' });
      link.lastChild.textContent = T.ghIssueLink(G.result.number);
      var again = h('button', 'au-btn', { type: 'button', text: T.ghSendAgain });
      again.addEventListener('click', function () { setGh('idle'); });
      ghBox.append(h('p', 'tr-gh-done', { role: 'status', text: T.ghSent }));
      var ex = existingNote();
      if (ex) ghBox.append(ex);
      ghBox.append(h('div', 'tr-actions', null, [link, again]), h('p', 'au-caption', { text: T.ghAfter }));
      return;
    }
    ghBox.append(h('p', 'tr-gh-what', { text: T.ghWhat(REPO_NAME) }));
    if (p === 'auth' || p === 'send') {
      var line = h('p', 'tr-gh-status', { role: 'status', text: p === 'auth' ? T.ghWaiting : G.status });
      ghBox.append(line);
      var ex2 = p === 'send' ? existingNote() : null;
      if (ex2) ghBox.append(ex2);
      if (p === 'auth') {
        var cancel = h('button', 'au-btn', { type: 'button', text: T.ghCancel });
        cancel.addEventListener('click', function () { auth.cancel(); });
        ghBox.append(h('div', 'tr-actions', null, [cancel]));
      }
      return;
    }
    if (p === 'error') {
      ghBox.append(warnLine(ghErrorText(G.error), true), h('p', 'au-caption', { text: T.ghErrHint }));
    }
    var send = h('button', 'au-btn primary', { type: 'button', html: icon('out', 'sm') + '<span>' + (p === 'error' ? T.ghTryAgain : T.ghSend) + '</span>' });
    send.disabled = G.blocked;
    send.addEventListener('click', sendWithGitHub);
    ghBox.append(h('div', 'tr-actions', null, [send]));
    if (G.blocked) ghBox.append(h('p', 'au-caption tr-way-note', { text: T.ghFixFirst }));
  }
  // An earlier send from the same login for this code is still open: this send updates it
  function existingNote() {
    var ex = G.existing, hit = ex && (ex.pulls[0] ? { what: T.ghWhatPr, it: ex.pulls[0] } : ex.issues[0] ? { what: T.ghWhatIssue, it: ex.issues[0] } : null);
    if (!hit) return null;
    var p = h('p', 'tr-gh-note', null, [T.ghUpdates(G.code, hit.what) + ' ']);
    p.append(h('a', null, { href: hit.it.url, target: '_blank', rel: 'noopener', text: '#' + hit.it.number }));
    return p;
  }

  function issueInfo() {
    var name = val('@name');
    return {
      code: S.code,
      name: C.hasText(name) ? name : uiName(S.code),
      translated: C.countTranslated(S.enKeys, S.base, draftValues()),
      total: S.enKeys.length,
      text: buildFile(),
      listing: unlisted() ? 'hidden' : 'shown'
    };
  }
  // The issue to send; a changed file starts a new one
  function prepareIssue() {
    var info = issueInfo(), sig = info.code + '\n' + info.listing + '\n' + info.text;
    if (sig !== G.sig || !G.issueP) {
      G.sig = sig; G.issueP = GHI.buildIssue(info); G.progress = {}; G.existing = null;
      G.issueP.catch(function () { /* reported by the send */ });
    }
    G.code = info.code;
  }

  function sendWithGitHub() {
    if (!auth || G.phase === 'auth' || G.phase === 'send') return;
    if (badKeys().length) { openSheet(); return; }
    prepareIssue();
    setGh('auth');
    // signIn opens the GitHub window first thing, still inside this click
    auth.signIn({ code: S.code }).then(deliver).catch(ghFail);
  }

  function deliver(user) {
    setGh('send', T.ghChecking);
    var gh = auth.client(), code = G.code;
    var found = G.existing ? Promise.resolve(G.existing) : GHI.findExisting({ gh: gh, anon: auth.anon(), repo: REPO_NAME, login: user.login, code: code });
    return Promise.all([G.issueP, found]).then(function (r) {
      G.existing = r[1];
      return GHI.sendIssue({
        gh: gh, repo: REPO_NAME, issue: r[0], state: G.progress,
        onStep: function (k, n) { setGh('send', T.ghPart(fmt(k), fmt(n))); }
      });
    }).then(function (res) {
      G.result = res;
      G.progress = {}; G.sig = ''; G.issueP = null;
      setGh('done');
      auth.revoke();
    });
  }

  function ghFail(e) {
    if (e && e.code === 'cancelled') { setGh('idle'); return; }
    if (e && e.code === 'http' && e.status === 401 && auth) auth.revoke();   // the token is no longer valid
    G.error = e || {};
    setGh('error');
  }

  // Same-tab fallback: the popup was blocked, GitHub sent the whole page through the callback and back here.
  if (auth) auth.resume().then(function (r) {
    if (r) { pendingResume = { user: r.user, code: r.extra && r.extra.code }; tryResume(); }
  }, function (e) {
    if (e && e.extra) { pendingResume = { error: e, code: e.extra.code }; tryResume(); }
  });
  function tryResume() {
    var r = pendingResume;
    if (!r || !r.code || S.status !== 'ready' || S.code !== r.code) return;
    pendingResume = null;
    lastWay = 'gh';
    if (r.error) { G.code = r.code; ghFail(r.error); openSheet(); return; }
    if (badKeys().length) { auth.revoke(); openSheet(); return; }
    prepareIssue();
    openSheet();
    deliver(r.user).catch(ghFail);
  }

  function goToKey(k) {
    if (isVisual() && V.home[k] != null) { selectKey(k, { focus: 'textarea', align: true }); return; }
    if (isVisual()) setMode('list');
    S.query = ''; search.value = '';
    S.group = C.groupOf(k);
    setFilter('all');
    var rec = rowCache.get(k);
    if (!rec) return;
    rec.el.scrollIntoView({ block: 'center' });
    rec.ta.focus({ preventScroll: true });
  }

  /* ---------- visual mode ----------
     Screens of the app with a hotspot per text. Both modes share one row state: the selected key's List row
     (rowCache) is moved into the editor here and put back when the selection or the mode changes. */

  var MODE_KEY = 'ds5-translate:mode';
  var V = null;                 // reconciled manifest (core.reconcile), or null when there is none
  var VS = { shot: 0, built: -1, key: null, item: -1, preview: true, peek: false, zoom: 'fit', z: 1, open: false, end: false };
  var borrowed = null;          // { key, rec, ph }: the List row shown in the editor and its place holder in the list
  var spotEls = [], paintEls = [], patches = {}, fitCache = {}, fontsReady = false, imgFull = false, preloadImg = null;
  var mqMobile = window.matchMedia('(max-width: 860px)');
  function mobile() { return mqMobile.matches; }
  (function () {
    var m = storeGet(MODE_KEY);
    S.mode = m === 'visual' || m === 'list' ? m : (window.innerWidth >= 861 ? 'visual' : 'list');
  })();

  // context/manifest.json next to this script. ?context=<url> points elsewhere, on a local host and same origin only.
  var manifestUrl = (function () {
    var self = (document.currentScript && document.currentScript.src) || location.href;
    var base = new URL('context/manifest.json', self);
    try {
      var q = new URLSearchParams(location.search).get('context');
      if (q && /^(localhost|127\.0\.0\.1|\[::1\]|.+\.localhost)$/.test(location.hostname)) {
        var u = new URL(q, location.href);
        if (u.origin === location.origin) return u;
      }
    } catch (e) { /* default */ }
    return base;
  })();

  var manPromise = fetch(manifestUrl.href, { cache: 'no-cache' }).then(function (r) {
    if (!r.ok) throw new Error('manifest ' + r.status);
    return r.json();
  }).then(function (j) {
    var m = C.normalizeManifest(j);
    if (!m.ok) throw new Error('manifest ' + m.reason);
    return m;
  });
  manPromise.catch(function () { /* List only */ });

  function sha256(buf) {
    try {
      if (!buf || !window.crypto || !crypto.subtle) return Promise.resolve(null);
      return crypto.subtle.digest('SHA-256', buf).then(function (d) {
        return Array.prototype.map.call(new Uint8Array(d), function (b) { return (b < 16 ? '0' : '') + b.toString(16); }).join('');
      }, function () { return null; });
    } catch (e) { return Promise.resolve(null); }
  }

  var visPromise = null;
  function loadVisual() {
    if (!visPromise) {
      visPromise = Promise.all([manPromise, loadEnglish()]).then(function (r) {
        return sha256(S.enBytes).then(function (hash) { return C.reconcile(r[0], S.en, hash); });
      }).then(function (v) {
        if (!v.shots.length) throw new Error('no shots');
        var gs = [], by = {};
        v.shots.forEach(function (s, i) {
          var g = by[s.group];
          if (!g) { g = by[s.group] = { name: s.group, shots: [] }; gs.push(g); }
          g.shots.push(i);
          s.groupIndex = gs.indexOf(g);
        });
        v.groups = gs;
        V = v;
        rowCache.forEach(fillSeen);
        return v;
      }).catch(function () {
        V = null;
        if (!S.en) visPromise = null;
        return null;
      });
    }
    return visPromise;
  }

  function isVisual() { return !!V && S.status === 'ready' && S.mode === 'visual'; }
  function isDone(k) { return C.hasText(val(k)); }
  function isBad(k) { return !!S.bad[k] && C.hasText(val(k)); }
  function curShot() { return V.shots[VS.shot]; }
  function itemAt(idx) { return curShot().items[idx]; }
  function rgb(c) { return 'rgb(' + c[0] + ',' + c[1] + ',' + c[2] + ')'; }
  function weightOf(it) { return it.weight >= 550 ? '600' : '400'; }
  function pc(f) { return (f * 100).toFixed(4) + '%'; }
  function iconBtn(name, label, cls) {
    return h('button', 'au-btn tr-icon-btn' + (cls ? ' ' + cls : ''), { type: 'button', 'aria-label': label, title: label, html: icon(name) });
  }

  /* Rows: where the key appears. One .meta line at the end of the row: up to three screens are named inline,
     more are counted, and a small Show button opens them as quiet chips (the current screen first). Every
     name jumps to its screen. */
  function fillSeen(rec) {
    var box = rec.seen;
    if (!box) return;
    box.textContent = '';
    box.hidden = !V;
    if (!V) return;
    var on = (V.shotsOf[rec.key] || []).slice();
    var line = h('p', 'meta tr-seen-line');
    box.append(line);
    if (!on.length) { line.textContent = T.notOnScreen; return; }
    var cur = borrowed && borrowed.rec === rec && VS.built >= 0 ? VS.shot : -1;
    var at = on.indexOf(cur);
    if (at > 0) { on.splice(at, 1); on.unshift(cur); }
    function shotBtn(i, cls) {
      return h('button', cls, { type: 'button', 'data-show-shot': String(i), 'data-show-key': rec.key, 'aria-current': i === cur ? 'true' : null, title: T.seenJump(V.shots[i].title), text: V.shots[i].title });
    }
    if (on.length <= 3) {
      line.append(T.seenOnLabel + ' ');
      on.forEach(function (i, n) { if (n) line.append(', '); line.append(shotBtn(i, 'tr-seen-name')); });
    } else {
      var list = h('div', 'tr-seen-list', { hidden: true, role: 'group', 'aria-label': T.seenCount(fmt(on.length)) });
      on.forEach(function (i) { list.append(shotBtn(i, 'tr-seen-chip')); });
      var tog = h('button', 'tr-seen-tog', { type: 'button', 'aria-expanded': 'false', 'data-seen-toggle': '', html: '<span>' + T.seenShow + '</span>' + icon('chev', 'sm') });
      line.append(h('span', null, { text: T.seenCount(fmt(on.length)) }), tog);
      box.append(list);
    }
    line.append(' ', h('button', 'au-link tr-show-on', { type: 'button', 'data-show-on': rec.key, text: T.showOnScreen }));
  }
  // A screen name in a row: Visual mode on that screen, with this key selected
  function showKeyOn(k, i) {
    if (!V || !V.shots[i] || !Object.prototype.hasOwnProperty.call(S.en, k)) return;
    if (!isVisual()) { S.mode = 'visual'; storeSet(MODE_KEY, 'visual'); applyMode(); }
    if (VS.built !== i) showShot(i, { force: true });
    selectKey(k, { focus: mobile() ? null : 'textarea', align: true });
  }

  /* DOM */
  var vm = h('section', 'vm', { id: 'visual', hidden: true, 'aria-label': T.modeVisual });
  var vmBar = h('div', 'vm-bar');
  var groupPick = Combo('vm-group', T.screenGroupLabel, null, function (v) {
    var g = V.groups[+v];
    if (g) showShot(g.shots[0]);
  });
  groupPick.onOpen = function () { groupPick.setItems(groupItems()); };
  var pills = h('div', 'vm-pills', { role: 'group', 'aria-label': T.screensLabel });
  var shotPick = Combo('vm-shot', T.screenLabel, null, function (v) { showShot(+v); });
  shotPick.onOpen = function () { shotPick.setItems(shotItems()); };
  var prevShotBtn = iconBtn('left', T.previousScreen, 'vm-prev-shot');
  var nextShotBtn = iconBtn('right', T.nextScreen, 'vm-next-shot');
  prevShotBtn.setAttribute('aria-keyshortcuts', 'Alt+ArrowLeft');
  nextShotBtn.setAttribute('aria-keyshortcuts', 'Alt+ArrowRight');
  var origSeg = Seg('vm-orig', T.previewLabel, [['orig', T.original], ['prev', T.preview]], function (v) { setPreview(v === 'prev'); });
  origSeg.el.setAttribute('aria-keyshortcuts', 'Alt+P');
  var zoomSeg = Seg('vm-zoom', T.zoomLabel, [['fit', T.fit], ['100', T.zoom100]], function (v) { setZoom(v); });
  var nextBtn = h('button', 'au-btn vm-next', { type: 'button', 'aria-keyshortcuts': 'Control+Enter', text: T.nextUntranslated });

  var view = h('div', 'vm-view');
  var padEl = h('div', 'vm-pad');
  var stage = h('div', 'vm-stage');
  var img = h('img', 'vm-img', { alt: '', decoding: 'async', loading: 'lazy', draggable: 'false' });
  var paint = h('div', 'vm-paint', { 'aria-hidden': 'true' });
  var spots = h('div', 'vm-spots', { role: 'group' });
  stage.append(img, paint, spots);
  padEl.append(stage);
  view.append(padEl);
  var canvasBox = h('div', 'vm-canvas', null, [view]);

  var ed = h('div', 'vm-ed');
  var sheetText = h('span', 'vm-sh-text');
  var sheetBtn = h('button', 'vm-sh-btn', { type: 'button', 'aria-expanded': 'false', 'aria-controls': 'vm-ed-body', 'aria-label': T.vmEditor }, [h('span', 'vm-grab', { 'aria-hidden': 'true' }), sheetText]);
  var sheetHead = h('div', 'vm-sh', null, [sheetBtn]);
  var sheetTools = h('div', 'vm-sh-tools');
  var listView = h('div', 'vm-lv');
  var backBtn = h('button', 'au-btn tr-quiet vm-back', { type: 'button', 'aria-keyshortcuts': 'Escape', html: icon('left', 'sm') + '<span>' + T.textsOnScreen + '</span>' });
  var posText = h('span', 'vm-pos');
  var prevBoxBtn = iconBtn('up', T.previous);
  var nextBoxBtn = iconBtn('chev', T.next);
  prevBoxBtn.setAttribute('aria-keyshortcuts', 'Alt+ArrowUp');
  nextBoxBtn.setAttribute('aria-keyshortcuts', 'Alt+ArrowDown');
  var rowSlot = h('div', 'vm-rowslot');
  var fitNote = h('div', 'vm-fit');
  var editView = h('div', 'vm-ev', { hidden: true }, [
    h('div', 'vm-evhead', null, [backBtn, h('div', 'vm-evnav', null, [posText, prevBoxBtn, nextBoxBtn])]),
    rowSlot, fitNote
  ]);
  var edBody = h('div', 'vm-ed-body', { id: 'vm-ed-body' }, [sheetTools, listView, editView]);
  ed.append(sheetHead, edBody);
  vm.append(vmBar, canvasBox, ed);
  app.insertBefore(vm, subbar);
  bindRowEvents(rowSlot);
  var meas = h('div', 'vm-measure', { 'aria-hidden': 'true' });
  (document.getElementById('main') || document.body).append(meas);

  // Mobile: the screen picker is the full-width combo. Desktop: it is the "+N" chip after the pills.
  function placeControls() {
    moreSig = '';
    if (mobile()) {
      shotPick.el.classList.remove('is-more', 'is-cur');
      shotPick.el.hidden = false;
      shotPick.btn.setAttribute('aria-label', T.screenLabel);
      vmBar.append(shotPick.el, prevShotBtn, nextShotBtn);
      sheetTools.append(origSeg.el);
      sheetHead.append(nextBtn);
      if (V && VS.built >= 0) shotPick.setLabel(curShot().title + ' · ' + progressOf([VS.shot]));
    } else {
      shotPick.el.classList.add('is-more');
      vmBar.append(groupPick.el, pills, shotPick.el, origSeg.el, zoomSeg.el, nextBtn);
      if (V && VS.built >= 0) layoutPills();
    }
  }
  placeControls();

  /* Bar */
  function progressOf(shotIdxs) {
    var d = 0, t = 0;
    shotIdxs.forEach(function (i) { var p = C.shotProgress(V.shots[i], isDone); d += p.done; t += p.total; });
    return fmt(d) + '/' + fmt(t);
  }
  function groupItems() {
    return V.groups.map(function (g, i) {
      return { value: String(i), label: g.name, right: progressOf(g.shots), selected: i === curShot().groupIndex };
    });
  }
  // Mobile lists every screen under its group; the desktop "+N" chip lists the current group's screens
  function shotItems() {
    var items = [], gs = mobile() ? V.groups : [V.groups[curShot().groupIndex]];
    gs.forEach(function (g) {
      if (gs.length > 1) items.push({ heading: g.name });
      g.shots.forEach(function (i) { items.push({ value: String(i), label: V.shots[i].title, right: progressOf([i]), selected: i === VS.shot }); });
    });
    return items;
  }
  // A pill names its screen without the group, which the group combo already shows: "Overview (Signals)" in the
  // Overview group is "Signals". A screen named like its group keeps the name; a bare number keeps the full title.
  function pillName(shot, g) {
    var t = shot.title, n = g.name;
    if (t === n || t.indexOf(n) !== 0 || !/^[\s(:]/.test(t.slice(n.length))) return t;
    var rest = t.slice(n.length).replace(/^[\s(:]+/, '').replace(/\)\s*$/, '').trim();
    return /[A-Za-zÀ-￿]/.test(rest) ? rest : t;
  }
  var pillsGroup = -1;
  function updateBar() {
    var shot = curShot(), g = V.groups[shot.groupIndex];
    groupPick.setLabel(g.name);
    if (pillsGroup !== shot.groupIndex) {
      pillsGroup = shot.groupIndex;
      pills.textContent = '';
      g.shots.forEach(function (i) {
        pills.append(h('button', 'vm-pill', { type: 'button', 'data-shot': i, title: V.shots[i].title }, [h('span', 'vm-pill-t', { text: pillName(V.shots[i], g) }), h('span', 'vm-pill-n')]));
      });
    }
    Array.prototype.forEach.call(pills.children, function (b) {
      var i = +b.getAttribute('data-shot');
      b.lastChild.textContent = progressOf([i]);
      if (i === VS.shot) b.setAttribute('aria-current', 'true'); else b.removeAttribute('aria-current');
    });
    if (mobile()) shotPick.setLabel(shot.title + ' · ' + progressOf([VS.shot]));
    else layoutPills();
  }

  /* Desktop: pills that are not wholly on the first row are hidden and counted in the "+N" chip. When the current
     screen is among them, the chip shows it in the current style. The chip's width changes the room the pills
     have, so the count is settled in a few passes. */
  var moreSig = '';
  function layoutPills() {
    if (mobile() || vm.hidden || !V || VS.built < 0) return;
    var chip = shotPick.el, bs = Array.prototype.slice.call(pills.children), out = [];
    pills.style.maxWidth = '';
    pills.scrollTop = pills.scrollLeft = 0;
    chip.hidden = true;
    for (var pass = 0; pass < 4; pass++) {
      var w = pills.clientWidth;
      var now = bs.filter(function (b) { return b.offsetTop > 2 || b.offsetLeft + b.offsetWidth > w + 0.5; });
      if (pass && now.length === out.length) break;
      out = now;
      if (!out.length) break;
      setMoreChip(out);
    }
    bs.forEach(function (b) { b.classList.toggle('is-out', out.indexOf(b) >= 0); });
    if (!out.length) { chip.hidden = true; chip.classList.remove('is-cur'); moreSig = ''; return; }
    // the chip follows the last whole pill
    var last = null;
    bs.forEach(function (b) { if (out.indexOf(b) < 0) last = b; });
    pills.style.maxWidth = (last ? Math.ceil(last.getBoundingClientRect().right - pills.getBoundingClientRect().left) + 1 : 0) + 'px';
  }
  function setMoreChip(out) {
    var chip = shotPick.el, cur = null, shot = curShot();
    out.forEach(function (b) { if (+b.getAttribute('data-shot') === VS.shot) cur = b; });
    var more = out.length - (cur ? 1 : 0);
    var sig = (cur ? VS.shot + '|' + progressOf([VS.shot]) : '') + '|' + more;
    chip.hidden = false;
    chip.classList.toggle('is-cur', !!cur);
    if (sig === moreSig) return;
    moreSig = sig;
    var span = shotPick.btn.querySelector('span');
    span.textContent = '';
    if (cur) {
      span.append(h('span', 'vm-pill-t', { text: cur.firstChild.textContent }), h('span', 'vm-pill-n', { text: progressOf([VS.shot]) }));
      if (more) span.append(h('span', 'vm-more-n', { text: '+' + fmt(more) }));
    } else {
      span.append(h('span', 'vm-more-n', { text: '+' + fmt(more) }));
    }
    shotPick.btn.setAttribute('aria-label', cur
      ? T.screenLabel + ': ' + shot.title + ' · ' + progressOf([VS.shot]) + (more ? ' · ' + T.moreScreens(fmt(more)) : '')
      : T.moreScreens(fmt(more)));
    shotPick.btn.title = cur ? shot.title : '';
  }
  if (window.ResizeObserver) new ResizeObserver(function () { requestAnimationFrame(layoutPills); }).observe(vmBar);
  try { document.fonts.ready.then(function () { layoutPills(); }); } catch (e) { /* no font loading API */ }

  pills.addEventListener('click', function (e) {
    var b = e.target.closest('[data-shot]');
    if (b) showShot(+b.getAttribute('data-shot'));
  });
  prevShotBtn.addEventListener('click', function () { showShot(VS.shot - 1); });
  nextShotBtn.addEventListener('click', function () { showShot(VS.shot + 1); });
  nextBtn.addEventListener('click', goNext);

  /* Shot */
  function srcFor(shot, full) { return new URL(full || !shot.image1x ? shot.image : shot.image1x, manifestUrl).href; }
  function needFull() { return VS.z * (window.devicePixelRatio || 1) > 1.1; }

  function showShot(i, opts) {
    if (!V) return;
    opts = opts || {};
    var n = V.shots.length;
    i = ((i % n) + n) % n;
    if (i === VS.built && !opts.force) return;
    clearSelection();
    VS.shot = i; VS.built = i; VS.end = false;
    var shot = curShot(), W = V.width, H = V.height;
    imgFull = !shot.image1x || needFull();
    img.alt = shot.title;
    img.src = srcFor(shot, imgFull);
    spots.textContent = ''; paint.textContent = '';
    spotEls = []; paintEls = [];
    spots.setAttribute('aria-label', shot.title);
    // smaller boxes sit on top, so where boxes overlap the smallest one wins
    var rank = shot.items.map(function (it, idx) { return idx; }).sort(function (a, b) {
      return shot.items[b].w * shot.items[b].h - shot.items[a].w * shot.items[a].h;
    });
    var zOf = {};
    rank.forEach(function (idx, r) { zOf[idx] = r + 1; });
    shot.items.forEach(function (it, idx) {
      var b = h('button', 'vm-spot' + (it.y < 30 ? ' chip-below' : ''), { type: 'button', 'data-i': idx });
      var st = b.style;
      st.left = 'calc(' + pc(it.x / W) + ' - 3px)';
      st.top = 'calc(' + pc(it.y / H) + ' - 3px)';
      st.width = 'calc(' + pc(it.w / W) + ' + 6px)';
      st.height = 'calc(' + pc(it.h / H) + ' + 6px)';
      st.zIndex = zOf[idx];
      b.append(h('span', 'vm-chip', { 'aria-hidden': 'true', text: it.key }));
      spots.append(b);
      spotEls.push(b);
    });
    if (img.complete && img.naturalWidth && !patches[i]) onImage();
    shot.items.forEach(function (it, idx) { updateSpot(idx); });
    updateBar();
    renderEditor();
    if (!vm.hidden) setZoom(mobile() ? 'fit' : VS.zoom);
    preloadNext();
  }

  function preloadNext() {
    var n = V.shots[(VS.shot + 1) % V.shots.length];
    if (n === curShot()) return;
    preloadImg = new Image();
    preloadImg.decoding = 'async';
    preloadImg.src = srcFor(n, !n.image1x || needFull());
  }

  function onImage() {
    var si = VS.shot;
    if (!V || !img.naturalWidth || img.src !== srcFor(curShot(), imgFull)) return;
    if (!patches[si]) patches[si] = sample(si);
    curShot().items.forEach(function (it, idx) { paintItem(idx); });
    if (VS.key) updateFitNote();
  }
  img.addEventListener('load', onImage);

  // Background and ink colour per box, from a same-origin canvas copy of the screen
  var sampleCv = null;
  function sample(si) {
    var shot = V.shots[si], nw = img.naturalWidth, nh = img.naturalHeight;
    var out = shot.items.map(function () { return { ok: false }; });
    var ctx, k = nw / V.width;
    try {
      sampleCv = sampleCv || document.createElement('canvas');
      sampleCv.width = nw; sampleCv.height = nh;
      ctx = sampleCv.getContext('2d', { willReadFrequently: true });
      ctx.drawImage(img, 0, 0);
    } catch (e) { return out; }
    shot.items.forEach(function (it, idx) {
      if (it.composite) return;
      try {
        var x0 = Math.max(0, Math.floor((it.x - 2) * k)), y0 = Math.max(0, Math.floor((it.y - 2) * k));
        var x1 = Math.min(nw, Math.ceil((it.x + it.w + 2) * k)), y1 = Math.min(nh, Math.ceil((it.y + it.h + 2) * k));
        var ix0 = Math.round(it.x * k), iy0 = Math.round(it.y * k), ix1 = Math.round((it.x + it.w) * k), iy1 = Math.round((it.y + it.h) * k);
        var w = x1 - x0, hh = y1 - y0;
        if (w <= 0 || hh <= 0) return;
        var d = ctx.getImageData(x0, y0, w, hh).data, ring = [], inner = [];
        for (var yy = 0; yy < hh; yy++) {
          for (var xx = 0; xx < w; xx++) {
            var gx = x0 + xx, gy = y0 + yy, o = (yy * w + xx) * 4;
            var into = gx >= ix0 && gx < ix1 && gy >= iy0 && gy < iy1 ? inner : ring;
            into.push(d[o], d[o + 1], d[o + 2], d[o + 3]);
          }
        }
        var rc = C.ringColor(ring);
        if (!rc.ok) return;
        out[idx] = { ok: true, bg: rc.rgb, ink: it.color || C.inkColor(inner, rc.rgb) };
      } catch (e) { /* tainted canvas: no preview */ }
    });
    sampleCv.width = sampleCv.height = 1;
    return out;
  }

  // Live preview: a flat patch over the English and the translation in the app's font
  function paintItem(idx) {
    var it = itemAt(idx), p = (patches[VS.shot] || [])[idx], v = val(it.key), el = paintEls[idx];
    var show = VS.preview && C.hasText(v) && p && p.ok && !it.composite;
    if (!show) { if (el) el.hidden = true; return; }
    if (!el) {
      el = paintEls[idx] = h('div', 'vm-pi');
      var patch = h('div', 'vm-patch'), tx = h('div', 'vm-text');
      patch.style.cssText = 'left:' + it.x + 'px;top:' + it.y + 'px;width:' + it.w + 'px;height:' + it.h + 'px;background:' + rgb(p.bg);
      var st = tx.style;
      st.fontSize = it.size + 'px';
      st.fontWeight = weightOf(it);
      st.lineHeight = it.lineHeight + 'px';
      if (p.ink) st.color = rgb(p.ink);
      st.textAlign = it.align;
      if (it.wrap) {
        var bw = it.maxW + 1;
        st.whiteSpace = 'pre-line';
        st.width = bw + 'px';
        st.left = (it.align === 'left' ? it.x : it.align === 'right' ? it.x + it.w - bw : it.x + it.w / 2 - bw / 2) + 'px';
        st.top = it.y + 'px';
      } else {
        st.whiteSpace = 'pre';
        st.left = (it.align === 'left' ? it.x : it.align === 'right' ? it.x + it.w : it.x + it.w / 2) + 'px';
        if (it.align !== 'left') st.transform = it.align === 'right' ? 'translateX(-100%)' : 'translateX(-50%)';
        st.top = (it.y + (it.h - it.lineHeight) / 2) + 'px';
      }
      el.append(patch, tx);
      paint.append(el);
    }
    el.hidden = false;
    el.lastChild.textContent = C.previewText(v);
  }

  // Off-screen measuring at 1x in the app's font; advisory only
  function fitOf(idx) {
    var it = itemAt(idx), v = val(it.key);
    if (!fontsReady || !C.hasText(v) || it.formatted || it.composite) return null;
    var text = C.previewText(v), ck = VS.shot + '|' + idx, c = fitCache[ck];
    if (c && c.text === text) return c.res;
    var ms = meas.style;
    ms.fontSize = it.size + 'px';
    ms.fontWeight = weightOf(it);
    ms.lineHeight = it.lineHeight + 'px';
    ms.whiteSpace = it.wrap ? 'pre-line' : 'pre';
    ms.width = it.wrap ? (it.maxW + 1) + 'px' : 'auto';
    meas.textContent = text;
    var r = meas.getBoundingClientRect();
    var res = C.fitCheck(it, it.wrap ? { lines: C.linesFromHeight(r.height, it.lineHeight) } : { width: r.width });
    fitCache[ck] = { text: text, res: res };
    return res;
  }

  function updateSpot(idx) {
    var b = spotEls[idx];
    if (!b) return;
    var k = itemAt(idx).key, done = isDone(k), bad = isBad(k), f = done ? fitOf(idx) : null;
    var side = f && (f.kind === 'wide' || (f.kind === 'lines' && !f.minor)) ? f.side : '';
    b.classList.toggle('is-done', done);
    b.classList.toggle('is-bad', bad);
    b.classList.toggle('over-right', side === 'right');
    b.classList.toggle('over-left', side === 'left');
    b.classList.toggle('over-bottom', side === 'bottom');
    var tab = b.querySelector('.vm-tab');
    if (bad && !tab) b.append(h('span', 'vm-tab', { 'aria-hidden': 'true', html: icon('warn') }));
    else if (!bad && tab) tab.remove();
    b.setAttribute('aria-label', S.en[k] + ' · ' + (bad ? T.vmProblem : done ? T.vmTranslated : T.filterUntranslated));
    paintItem(idx);
  }
  function refreshSpots() { if (V && VS.built >= 0) curShot().items.forEach(function (it, idx) { updateSpot(idx); }); }

  (function waitForFont() {
    function ready() {
      fontsReady = true;
      fitCache = {};
      if (isVisual()) { refreshSpots(); if (VS.key) updateFitNote(); }
    }
    try {
      Promise.all([document.fonts.load('400 13px "DS5 Inter"'), document.fonts.load('600 13px "DS5 Inter"')])
        .then(function () { return document.fonts.ready; }).then(ready, ready);
    } catch (e) { ready(); }
  })();

  /* Selection: the selected key's List row moves into the editor */
  function borrowRow(key) {
    if (borrowed && borrowed.key === key) return borrowed.rec;
    giveBack(false);
    var rec = getRow(key), ph = null;
    if (rec.el.parentNode) {
      ph = h('div', 'tr-row-ph', { 'data-key': key });
      rec.el.parentNode.replaceChild(ph, rec.el);
    }
    borrowed = { key: key, rec: rec, ph: ph };
    fillSeen(rec);   // the current screen goes first
    rec.warns.after(fitNote);   // fit warnings sit with the other warnings, under the text box
    rowSlot.append(rec.el);
    return rec;
  }
  function giveBack(restoreFocus) {
    if (!borrowed) return;
    var b = borrowed, ta = b.rec.ta, had = restoreFocus && document.activeElement === ta;
    var s0 = ta.selectionStart, s1 = ta.selectionEnd;
    borrowed = null;
    editView.append(fitNote);
    fillSeen(b.rec);
    if (b.ph && b.ph.parentNode) b.ph.parentNode.replaceChild(b.rec.el, b.ph);
    else b.rec.el.remove();
    if (had && b.rec.el.isConnected) {
      ta.focus({ preventScroll: true });
      try { ta.setSelectionRange(s0, s1); } catch (e) { /* not a text field */ }
    }
  }
  // Rows are about to be rebuilt: forget the borrowed one, return its key.
  function dropBorrowed() {
    if (!borrowed) return null;
    var k = borrowed.key;
    borrowed.rec.el.remove();
    if (borrowed.ph) borrowed.ph.remove();
    borrowed = null;
    return k;
  }

  function firstItem(shot, key) {
    for (var i = 0; i < shot.items.length; i++) if (shot.items[i].key === key) return i;
    return -1;
  }
  function markSelection() {
    var shot = curShot();
    spotEls.forEach(function (b, i) {
      var on = VS.key != null && shot.items[i].key === VS.key;
      b.classList.toggle('is-sel', on);
      b.classList.toggle('is-primary', on && i === VS.item);
      if (on) b.setAttribute('aria-current', 'true'); else b.removeAttribute('aria-current');
    });
    vm.classList.toggle('has-sel', VS.key != null);
  }
  function clearSelection() {
    VS.key = null; VS.item = -1;
    giveBack(false);
    if (V && VS.built >= 0) markSelection();
  }

  // opts: item (box index), focus ('textarea' | 'spot' | null), align (scroll the page to the workspace)
  function selectKey(key, opts) {
    opts = opts || {};
    if (!V) return false;
    var si = VS.built >= 0 && curShot().keys.indexOf(key) >= 0 ? VS.shot : V.home[key];
    if (si == null) return false;
    if (si !== VS.built) showShot(si, { force: true });
    var shot = curShot();
    var idx = opts.item != null && shot.items[opts.item] && shot.items[opts.item].key === key ? opts.item : firstItem(shot, key);
    VS.key = key; VS.item = idx; VS.end = false;
    markSelection();
    var rec = borrowRow(key);
    renderEditor();
    if (opts.align || mobile()) alignWorkspace();
    if (mobile()) { setSheet(true); autoZoom(idx); } else revealSpot(idx);
    if (opts.focus === 'textarea') {
      rec.ta.focus({ preventScroll: true });
      var L = rec.ta.value.length;
      try { rec.ta.setSelectionRange(L, L); } catch (e) { /* ignore */ }
      grow(rec.ta);
    } else if (opts.focus === 'spot' && spotEls[idx]) {
      spotEls[idx].focus({ preventScroll: true });
    }
    return true;
  }

  function deselect(focusSpot) {
    var idx = VS.item;
    clearSelection();
    renderEditor();
    if (focusSpot && spotEls[idx]) spotEls[idx].focus({ preventScroll: true });
  }

  spots.addEventListener('click', function (e) {
    var b = e.target.closest('.vm-spot');
    if (!b || suppressClick) return;
    var idx = +b.getAttribute('data-i');
    selectKey(itemAt(idx).key, { item: idx, focus: mobile() ? null : 'textarea' });
  });
  backBtn.addEventListener('click', function () { deselect(true); });
  prevBoxBtn.addEventListener('click', function () { stepBox(-1); });
  nextBoxBtn.addEventListener('click', function () { stepBox(1); });
  listView.addEventListener('click', function (e) {
    var li = e.target.closest('.vm-li');
    if (li) { selectKey(li.getAttribute('data-key'), { focus: mobile() ? null : 'textarea' }); return; }
    if (e.target.closest('.vm-open-list')) openListUntranslated();
  });

  function stepBox(d) {
    var keys = curShot().keys;
    var i = VS.key == null ? (d > 0 ? -1 : 0) : keys.indexOf(VS.key);
    var inTa = borrowed && document.activeElement === borrowed.rec.ta;
    var inSpot = spots.contains(document.activeElement);
    selectKey(keys[(i + d + keys.length) % keys.length], { focus: inTa ? 'textarea' : inSpot ? 'spot' : null });
  }
  function stepShot(d) {
    var inVm = vm.contains(document.activeElement);
    showShot(VS.shot + d);
    if (inVm && !vm.contains(document.activeElement) && spotEls[0]) spotEls[0].focus({ preventScroll: true });
  }

  function goNext() {
    if (!V) return;
    var r = C.nextUntranslated(V.shots, isDone, VS.shot, VS.key);
    var inTa = borrowed && document.activeElement === borrowed.rec.ta;
    if (!r) {
      clearSelection();
      VS.end = true;
      renderEditor();
      if (mobile()) setSheet(true);
      var ol = listView.querySelector('.vm-open-list');
      if (ol && !mobile()) ol.focus();
      return;
    }
    if (r.shot !== VS.built) showShot(r.shot);
    selectKey(r.key, { focus: mobile() && !inTa ? null : 'textarea', align: true });
  }

  /* Editor */
  function screenStatus() {
    var p = C.shotProgress(curShot(), isDone);
    if (VS.end) return T.vmAllScreensDone;
    if (p.total && p.done === p.total) {
      var left = 0;
      S.enKeys.forEach(function (k) { if (!isDone(k)) left++; });
      return left ? T.vmScreenDone(fmt(left)) : T.vmAllScreensDone;
    }
    return T.vmScreenCount(fmt(p.done), fmt(p.total));
  }
  function renderEditor() {
    if (!V || VS.built < 0) return;
    var sel = VS.key != null;
    listView.hidden = sel;
    editView.hidden = !sel;
    if (sel) {
      var keys = curShot().keys;
      posText.textContent = T.vmPosition(fmt(keys.indexOf(VS.key) + 1), fmt(keys.length));
      updateFitNote();
    } else {
      renderListView();
    }
    sheetText.textContent = sel ? S.en[VS.key] : screenStatus();
    sheetText.lang = sel ? 'en' : '';
  }
  function renderListView() {
    var shot = curShot();
    listView.textContent = '';
    listView.append(
      h('div', 'vm-lv-head', null, [h('h2', 'au-h2', { text: T.textsOnScreen }), h('p', 'au-caption vm-status', { role: 'status', text: screenStatus() })]),
      h('p', 'vm-hint', { text: T.vmHint })
    );
    var box = h('div', 'vm-items');
    shot.keys.forEach(function (k) {
      var v = val(k), done = isDone(k), bad = isBad(k);
      // the label carries the state, so an untranslated text is one line
      var label = S.en[k] + ' · ' + (done ? C.previewText(v) + ' · ' : '') + (bad ? T.vmProblem : done ? T.vmTranslated : T.filterUntranslated);
      var b = h('button', 'vm-li' + (done ? ' is-done' : ''), { type: 'button', 'data-key': k, 'aria-label': label }, [
        h('span', 'vm-li-en', { lang: 'en', text: S.en[k] }),
        h('span', 'vm-li-tr' + (done ? '' : ' is-empty'), { lang: done ? S.code : null, dir: done ? 'auto' : null, text: done ? C.previewText(v) : T.filterUntranslated })
      ]);
      if (bad) b.append(h('span', 'vm-li-warn', { html: icon('alert', 'sm') }));
      box.append(b);
    });
    listView.append(box);
    var off = 0;
    V.offscreen.forEach(function (k) { if (!isDone(k)) off++; });
    if (off) {
      listView.append(h('div', 'vm-note', null, [
        h('p', null, { text: T.vmOffscreen(fmt(off)) }),
        h('button', 'au-btn vm-open-list', { type: 'button', text: T.openList })
      ]));
    }
  }
  function updateFitNote() {
    fitNote.textContent = '';
    var idx = VS.item, it = idx >= 0 ? itemAt(idx) : null;
    if (!it || !isDone(it.key)) return;
    var f = fitOf(idx);
    if (f && f.kind === 'wide') fitNote.append(warnLine(T.vmWider(fmt(f.pct)), false));
    else if (f && f.kind === 'lines') fitNote.append(warnLine(T.vmLines(fmt(f.lines), fmt(f.max)), false));
    if (VS.preview && patches[VS.shot]) {
      var p = patches[VS.shot][idx];
      fitNote.append(h('p', 'au-caption', { text: p && p.ok && !it.composite ? T.vmApprox : T.vmNoPreview }));
    }
  }

  // After an edit: the key's boxes, counts and the editor notes
  function visualInput(k) {
    if (!isVisual() || VS.built < 0) return;
    curShot().items.forEach(function (it, idx) { if (it.key === k) updateSpot(idx); });
    updateBar();
    if (k === VS.key) updateFitNote();
  }

  function setPreview(on) {
    VS.preview = on;
    origSeg.set(on ? 'prev' : 'orig');
    classPreview();
    refreshSpots();
    if (VS.key) updateFitNote();
  }
  function classPreview() { vm.classList.toggle('is-preview', VS.preview && !VS.peek); }
  function setPeek(on) { VS.peek = on; classPreview(); }

  /* Zoom and panning. The stage is W*z by H*z inside a scroll box; hotspots are placed in percent, the
     preview layer is the frame at 1x scaled by z. Desktop scrolls natively; mobile pans by script. */
  function fitZ() {
    var r = view.getBoundingClientRect();
    if (!r.width || !r.height) return VS.z;
    // contain: the whole shot fits the canvas; on mobile, the part above the peeking sheet
    var hgt = mobile() ? Math.max(40, r.height - PEEK) : r.height;
    return Math.min(mobile() ? 1 : 2, r.width / V.width, hgt / V.height);
  }
  function applyZ(z) {
    VS.z = z;
    stage.style.width = Math.floor(V.width * z) + 'px';
    stage.style.height = Math.floor(V.height * z) + 'px';
    paint.style.width = V.width + 'px';
    paint.style.height = V.height + 'px';
    paint.style.transform = 'scale(' + z + ')';
    if (mobile()) {
      var r = view.getBoundingClientRect();
      padEl.style.padding = Math.round(r.height) + 'px ' + Math.round(r.width) + 'px';
    } else {
      padEl.style.padding = '';
    }
    if (!imgFull && needFull()) { imgFull = true; img.src = srcFor(curShot(), true); }
  }
  function zoomTo(z, at) {
    var r = view.getBoundingClientRect();
    var ax = at ? at.x : (r.left + r.right) / 2, ay = at ? at.y : (r.top + r.bottom) / 2;
    var fx = (view.scrollLeft + ax - r.left - stage.offsetLeft) / VS.z, fy = (view.scrollTop + ay - r.top - stage.offsetTop) / VS.z;
    applyZ(z);
    view.scrollLeft = stage.offsetLeft + fx * z - (ax - r.left);
    view.scrollTop = stage.offsetTop + fy * z - (ay - r.top);
    clampView();
  }
  function setZoom(mode) {
    if (!V || VS.built < 0) return;
    VS.zoom = mode;
    zoomSeg.set(mode);
    vm.classList.toggle('is-fit', mode === 'fit');
    if (mode === 'fit') {
      applyZ(fitZ());
      if (mobile()) { view.scrollLeft = stage.offsetLeft; clampView(); }
      else view.scrollLeft = view.scrollTop = 0;
    } else {
      zoomTo(1);
    }
    if (VS.key) revealSpot(VS.item);
  }
  function region() {
    var r = view.getBoundingClientRect(), top = Math.max(r.top, tools.getBoundingClientRect().bottom), bottom = r.bottom;
    if (mobile() && !vm.classList.contains('is-off') && ed.style.height) {
      bottom = Math.min(bottom, window.innerHeight - parseFloat(ed.style.bottom || '0') - parseFloat(ed.style.height));
    }
    var vv = window.visualViewport;
    if (vv) bottom = Math.min(bottom, vv.offsetTop + vv.height);
    if (bottom - top < 40) bottom = top + 40;
    return { top: top, bottom: bottom, view: r };
  }
  // Mobile: keep the stage covering the visible part, or centred in it when smaller.
  function clampView() {
    if (!mobile() || VS.built < 0) return;
    var g = region(), r = g.view, sw = stage.offsetWidth, sh = stage.offsetHeight, ox = stage.offsetLeft, oy = stage.offsetTop;
    var sl = view.scrollLeft, st = view.scrollTop, visT = g.top - r.top, visB = g.bottom - r.top, vh = visB - visT;
    sl = sw <= r.width ? ox - (r.width - sw) / 2 : Math.min(Math.max(sl, ox), ox + sw - r.width);
    st = sh <= vh ? oy - visT - (vh - sh) / 2 : Math.min(Math.max(st, oy - visT), oy + sh - visB);
    view.scrollLeft = sl;
    view.scrollTop = st;
  }
  function centerSpot(idx) {
    var it = itemAt(idx), g = region(), r = g.view;
    view.scrollLeft = stage.offsetLeft + (it.x + it.w / 2) * VS.z - r.width / 2;
    view.scrollTop = stage.offsetTop + (it.y + it.h / 2) * VS.z - ((g.top + g.bottom) / 2 - r.top);
    clampView();
  }
  function revealSpot(idx) {
    var b = spotEls[idx];
    if (!b) return;
    var br = b.getBoundingClientRect(), g = region();
    if (br.left < g.view.left || br.right > g.view.right || br.top < g.top || br.bottom > g.bottom) centerSpot(idx);
  }
  // Mobile: zoom so the box fills at most 70% of the width (never below fit, never above 100%)
  function autoZoom(idx) {
    var it = itemAt(idx), r = view.getBoundingClientRect(), f = fitZ();
    var z = Math.min(1, Math.max(f, 0.7 * r.width / it.w));
    applyZ(z);
    VS.zoom = z > f * 1.02 ? '100' : 'fit';
    centerSpot(idx);
  }
  // The workspace always fills the viewport now; only the mobile sheet needs placing.
  function alignWorkspace() { layoutSheet(); }

  // Touch: one finger pans, two pinch, a double tap toggles fit and 100%
  var ptrs = new Map(), gest = null, lastTap = null, suppressClick = false;
  function dist(a) { return Math.hypot(a[0].x - a[1].x, a[0].y - a[1].y) || 1; }
  function unsuppressSoon() { suppressClick = true; setTimeout(function () { suppressClick = false; }, 0); }
  view.addEventListener('pointerdown', function (e) {
    if (!mobile() || VS.built < 0 || (e.pointerType === 'mouse' && e.button !== 0)) return;
    ptrs.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (ptrs.size === 1) {
      gest = { kind: 'pan', x0: e.clientX, y0: e.clientY, sl: view.scrollLeft, st: view.scrollTop, t0: Date.now(), moved: false };
    } else if (ptrs.size === 2) {
      var a = Array.from(ptrs.values()), r = view.getBoundingClientRect();
      var mx = (a[0].x + a[1].x) / 2, my = (a[0].y + a[1].y) / 2;
      gest = { kind: 'pinch', d0: dist(a), z0: VS.z,
        fx: (view.scrollLeft + mx - r.left - stage.offsetLeft) / VS.z, fy: (view.scrollTop + my - r.top - stage.offsetTop) / VS.z };
    }
  });
  view.addEventListener('pointermove', function (e) {
    if (!gest || !ptrs.has(e.pointerId)) return;
    ptrs.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (gest.kind === 'pan') {
      var dx = e.clientX - gest.x0, dy = e.clientY - gest.y0;
      if (!gest.moved && Math.abs(dx) + Math.abs(dy) > 8) {
        gest.moved = true;
        try { view.setPointerCapture(e.pointerId); } catch (x) { /* ignore */ }
      }
      if (gest.moved) { view.scrollLeft = gest.sl - dx; view.scrollTop = gest.st - dy; clampView(); }
    } else {
      var a = Array.from(ptrs.values());
      if (a.length < 2) return;
      var r = view.getBoundingClientRect(), mx = (a[0].x + a[1].x) / 2, my = (a[0].y + a[1].y) / 2;
      var z = Math.min(2, Math.max(fitZ(), gest.z0 * dist(a) / gest.d0));
      applyZ(z);
      view.scrollLeft = stage.offsetLeft + gest.fx * z - (mx - r.left);
      view.scrollTop = stage.offsetTop + gest.fy * z - (my - r.top);
      clampView();
    }
  });
  function endPtr(e) {
    if (!ptrs.has(e.pointerId)) return;
    ptrs.delete(e.pointerId);
    if (!gest) return;
    if (gest.kind === 'pan') {
      if (ptrs.size) return;
      if (gest.moved) unsuppressSoon();
      else if (e.type === 'pointerup' && Date.now() - gest.t0 < 400) tap(e.clientX, e.clientY);
      gest = null;
    } else {
      VS.zoom = VS.z > fitZ() * 1.02 ? '100' : 'fit';
      unsuppressSoon();
      if (ptrs.size === 1) {
        var p = ptrs.values().next().value;
        gest = { kind: 'pan', x0: p.x, y0: p.y, sl: view.scrollLeft, st: view.scrollTop, t0: 0, moved: true };
      } else if (!ptrs.size) gest = null;
    }
  }
  view.addEventListener('pointerup', endPtr);
  view.addEventListener('pointercancel', endPtr);
  function tap(x, y) {
    var now = Date.now();
    if (lastTap && now - lastTap.t < 320 && Math.abs(x - lastTap.x) + Math.abs(y - lastTap.y) < 30) {
      lastTap = null;
      var toFit = VS.z > fitZ() * 1.02;
      VS.zoom = toFit ? 'fit' : '100';
      zoomTo(toFit ? fitZ() : 1, { x: x, y: y });
      return;
    }
    lastTap = { t: now, x: x, y: y };
  }

  /* Mobile bottom sheet: non-modal, peeks at 64 px, opens to about 45% of the screen, rides above the keyboard */
  var PEEK = 64;
  function setSheet(open) {
    VS.open = open;
    ed.classList.toggle('is-open', open);
    sheetBtn.setAttribute('aria-expanded', open ? 'true' : 'false');
    layoutSheet();
  }
  sheetBtn.addEventListener('click', function () { if (!sheetDragged) setSheet(!VS.open); });
  var sheetDrag = null, sheetDragged = false;
  sheetHead.addEventListener('pointerdown', function (e) { if (e.target.closest('.vm-next')) return; sheetDrag = { y: e.clientY }; sheetDragged = false; });
  sheetHead.addEventListener('pointermove', function (e) {
    if (!sheetDrag) return;
    var dy = e.clientY - sheetDrag.y;
    if (Math.abs(dy) > 24) { sheetDragged = true; setSheet(dy < 0); sheetDrag = null; }
  });
  sheetHead.addEventListener('pointerup', function () { sheetDrag = null; setTimeout(function () { sheetDragged = false; }, 0); });
  sheetHead.addEventListener('pointercancel', function () { sheetDrag = null; sheetDragged = false; });

  var lastKb = 0;
  function layoutSheet() {
    if (!isVisual() || !mobile()) {
      ed.style.height = ed.style.bottom = '';
      vm.classList.remove('is-off');
      return;
    }
    var ih = window.innerHeight, vv = window.visualViewport;
    var vvH = vv ? vv.height : ih, vvTop = vv ? vv.offsetTop : 0;
    var kb = Math.max(0, Math.round(ih - vvH - vvTop));
    var bottom = kb > 60 ? kb : Math.max(0, Math.round(ih - subbar.getBoundingClientRect().top));
    var openH = Math.round(Math.min(ih * 0.45, vvH * 0.55));
    ed.style.bottom = bottom + 'px';
    ed.style.height = (VS.open ? Math.max(openH, PEEK) : PEEK) + 'px';
    var r = vm.getBoundingClientRect();
    vm.classList.toggle('is-off', r.bottom < ih * 0.45 || r.top > ih * 0.6);
    if (Math.abs(kb - lastKb) > 40 && VS.key) centerSpot(VS.item);
    lastKb = kb;
  }
  var layoutQueued = false;
  function queueLayout() {
    if (layoutQueued) return;
    layoutQueued = true;
    requestAnimationFrame(function () { layoutQueued = false; layoutSheet(); });
  }
  window.addEventListener('scroll', queueLayout, { passive: true });
  window.addEventListener('resize', queueLayout);
  if (window.visualViewport) {
    window.visualViewport.addEventListener('resize', queueLayout);
    window.visualViewport.addEventListener('scroll', queueLayout);
  }
  if (window.ResizeObserver) {
    new ResizeObserver(function () {
      if (!isVisual() || VS.built < 0) return;
      requestAnimationFrame(function () {
        if (VS.zoom === 'fit') { applyZ(fitZ()); if (!mobile()) view.scrollLeft = view.scrollTop = 0; }
        clampView();
        layoutSheet();
      });
    }).observe(view);
  }
  function onMq() {
    if (!isVisual()) return;
    placeControls();
    setZoom('fit');
    layoutSheet();
  }
  if (mqMobile.addEventListener) mqMobile.addEventListener('change', onMq); else mqMobile.addListener(onMq);

  /* Shortcuts */
  document.addEventListener('keydown', function (e) {
    if (!isVisual() || VS.built < 0 || e.defaultPrevented || (sheet && sheet.open) || (rules && rules.open)) return;
    if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && e.key === 'Enter') { e.preventDefault(); goNext(); return; }
    if (e.altKey && !e.ctrlKey && !e.metaKey && !e.shiftKey) {
      var c = e.code || e.key, done = true;
      if (c === 'ArrowDown') stepBox(1);
      else if (c === 'ArrowUp') stepBox(-1);
      else if (c === 'ArrowRight') stepShot(1);
      else if (c === 'ArrowLeft') stepShot(-1);
      else if (c === 'KeyP') setPreview(!VS.preview);
      else if (c === 'KeyO') { if (!e.repeat) setPeek(true); }
      else done = false;
      if (done) e.preventDefault();
      return;
    }
    if (e.key === 'Escape' && VS.key != null && !e.altKey) { e.preventDefault(); deselect(true); }
  });
  document.addEventListener('keyup', function (e) { if (VS.peek && (e.code === 'KeyO' || e.key === 'Alt')) setPeek(false); });
  window.addEventListener('blur', function () { if (VS.peek) setPeek(false); });

  /* Modes */
  function applyMode() {
    var avail = !!V && S.status === 'ready';
    modeSeg.el.hidden = !avail;
    r1.classList.toggle('has-mode', avail);
    var vis = avail && S.mode === 'visual';
    modeSeg.set(vis ? 'visual' : 'list');
    app.classList.toggle('is-visual', vis);
    document.documentElement.classList.toggle('tr-vis', vis);
    vm.hidden = !vis;
    if (!vis) { giveBack(true); vm.classList.remove('is-off'); return; }
    placeControls();
    origSeg.set(VS.preview ? 'prev' : 'orig');
    classPreview();
    if (VS.built < 0 || !V.shots[VS.shot]) { VS.built = -1; showShot(V.shots[VS.shot] ? VS.shot : 0); }
    else {
      // edits made in List: bring the boxes and counts up to date
      refreshSpots();
      updateBar();
      if (VS.key) { borrowRow(VS.key); markSelection(); }
      renderEditor();
    }
    setZoom(mobile() ? 'fit' : VS.zoom);
    layoutSheet();
  }

  function setMode(m) {
    var was = isVisual();
    S.mode = m;
    storeSet(MODE_KEY, m);
    if (m === 'list' && was) {
      var rec = borrowed ? borrowed.rec : null;
      applyMode();
      if (rec && rec.el.isConnected) rec.el.scrollIntoView({ block: 'center' });
    } else if (m === 'visual' && !was) {
      var ae = document.activeElement;
      var inList = !!(ae && ae.classList && ae.classList.contains('tr-input') && list.contains(ae));
      var k = inList ? ae.getAttribute('data-key') : S.lastListKey;
      applyMode();
      if (isVisual() && k && V.home[k] != null) selectKey(k, { focus: inList && !mobile() ? 'textarea' : null });
    } else {
      applyMode();
    }
  }

  function showOnScreen(k) {
    if (!V || V.home[k] == null) return;
    S.mode = 'visual';
    storeSet(MODE_KEY, 'visual');
    applyMode();
    selectKey(k, { focus: mobile() ? null : 'textarea', align: true });
  }

  function openListUntranslated() {
    setMode('list');
    S.query = ''; search.value = ''; S.group = '';
    setFilter('untranslated');
    scroller.scrollTop = 0;
  }

  // Language changed: rows are rebuilt, start the screens afresh (same screen)
  function visualReset() {
    VS.key = null; VS.item = -1; VS.built = -1; VS.end = false;
    borrowed = null;
  }
  // Draft discarded: rebuild the screen and select the same key again
  function visualRefresh(key) {
    VS.key = null; VS.item = -1; VS.built = -1;
    if (!isVisual()) return;
    showShot(VS.shot);
    setZoom(mobile() ? 'fit' : VS.zoom);
    if (key) selectKey(key, {});
  }

  /* ---------- landing ----------
     What the page shows without #lang= or #other: what this is, how it works, the drafts kept in this browser and
     the language list of the home menu. A language picked here is a new history entry, so Back returns here. */

  var landing = document.getElementById('landing');
  var landTitle = h('h2', 'tr-land-h', { id: 'land-title', tabindex: '-1', text: T.landTitle });
  var contBox = h('section', 'tr-box tr-land-cont', { 'aria-labelledby': 'land-cont-t', hidden: true });
  var contList = h('ul', 'tr-land-drafts');
  var langList = h('ul', 'tr-land-list');
  var ldf = new Intl.DateTimeFormat(T.dateLocale, { day: 'numeric', month: 'short', year: 'numeric' });
  var hrefFor = function (code) { return code ? '#lang=' + encodeURIComponent(code) : '#other'; };

  (function buildLanding() {
    if (!landing) return;
    var steps = h('ol', 'tr-land-steps');
    T.howSteps.forEach(function (s, i) {
      steps.append(h('li', null, null, [
        h('span', 'tr-land-n', { 'aria-hidden': 'true', text: '0' + (i + 1) }),
        h('p', null, { text: i === 2 && !GH_ON ? T.howStep3Manual : s })
      ]));
    });
    contBox.append(h('h3', 'tr-box-h', { id: 'land-cont-t', text: T.continueTitle }), contList);
    landing.append(h('div', 'tr-land-in', null, [
      h('div', 'tr-land-l', null, [
        landTitle,
        h('p', 'tr-land-lead', { text: T.landLead }),
        h('section', 'tr-land-how', { 'aria-labelledby': 'land-how-t' }, [
          h('h3', 'tr-land-label', { id: 'land-how-t', text: T.howTitle }),
          steps,
          h('p', 'tr-land-guide', null, [h('a', 'meta', { href: GUIDE, target: '_blank', rel: 'noopener', text: T.guide })])
        ])
      ]),
      h('div', 'tr-land-r', null, [
        contBox,
        h('section', 'tr-box tr-land-langs', { 'aria-labelledby': 'land-langs-t' }, [
          h('h3', 'tr-box-h', { id: 'land-langs-t', text: T.chooseLanguage }),
          langList
        ])
      ])
    ]));
    // A plain click on a language, a draft or Other Language pushes its hash; a new-tab click opens it there
    landing.addEventListener('click', function (e) {
      var a = e.target.closest('a[href^="#"]');
      if (!a || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
      e.preventDefault();
      try { history.pushState({ trLanding: 1 }, '', a.getAttribute('href')); } catch (x) { location.hash = a.getAttribute('href'); return; }
      route();
    });
  })();

  function editDate(at) {
    if (!at) return '';
    var d = new Date(at);
    return d.getFullYear() === new Date().getFullYear() ? df.format(d) : ldf.format(d);
  }

  function renderLanding() {
    if (!landing || landing.hidden) return;
    // Continue: one card per language with a draft here, the newest first; only that one is the powder button
    var drafts = DraftStore.list().filter(function (d) { return C.canonicalCode(d.code).ok; });
    contList.textContent = '';
    drafts.forEach(function (d, i) {
      var name = langName(d.code), when = editDate(d.at);
      // the count and the date each stay on one line
      var line = h('p', 'meta tr-land-dm', null, [h('span', null, { text: T.draftCount(fmt(d.n)) })]);
      if (when) line.append(' · ', h('span', null, { text: T.draftWhen(when) }));
      contList.append(h('li', 'tr-land-draft', null, [
        h('div', 'tr-land-dn', null, [h('span', 'tm-name', { lang: d.code, text: name }), h('span', 'tm-code meta', { text: d.code })]),
        line,
        h('a', 'au-btn' + (i === 0 ? ' primary' : ''), { href: hrefFor(d.code), 'aria-label': T.continueLabel(name), text: T.continueAction })
      ]));
    });
    contBox.hidden = !drafts.length;
    contBox.classList.toggle('is-many', drafts.length > 2);
    landing.classList.toggle('has-drafts', drafts.length > 0);
    // Languages: the home menu's rows
    var ae = document.activeElement, keep = ae && langList.contains(ae) ? ae.getAttribute('href') : null;
    langList.textContent = '';
    if (Langs) Langs.rows(Langs.text.en, hrefFor).forEach(function (o) { langList.append(Langs.rowEl(o)); });
    if (keep) { var again = langList.querySelector('a[href="' + keep.replace(/"/g, '\\"') + '"]'); if (again) again.focus(); }
  }

  function showLanding(focus) {
    if (!landing) return;
    closeAllCombos();
    closeDraftMenu();
    if (sheet.open) sheet.close();
    app.hidden = true;
    landing.hidden = false;
    renderLanding();
    if (focus) landTitle.focus({ preventScroll: true });
  }
  function showTool() {
    if (!landing || landing.hidden) return;
    // focus inside the landing would be lost with it; the Other Language form then takes it for its field
    var main = document.getElementById('main');
    if (main && landing.contains(document.activeElement)) main.focus({ preventScroll: true });
    landing.hidden = true;
    app.hidden = false;
    if (isVisual()) { placeControls(); setZoom(mobile() ? 'fit' : VS.zoom); layoutSheet(); }
  }
  // Back to the landing from the tool: an entry the landing pushed goes back in history; a page opened with #other
  // shows the landing in place
  function toLanding() {
    if (history.state && history.state.trLanding) { history.back(); return; }
    setHash(null);
    showLanding(true);
  }

  /* ---------- start ---------- */

  function codeFromHash() {
    var m = /^#lang=([A-Za-z0-9_-]+)$/.exec(location.hash);
    if (!m) return null;
    var r = C.canonicalCode(m[1]);
    return r.ok ? r.code : null;
  }
  // #lang=<code> opens that language, #other the Other Language form, no hash the landing. Any other hash (the skip
  // link's #main) changes nothing once the page is up. Runs for both hashchange and popstate, so it is idempotent.
  function route(e) {
    var first = e === true;
    if (location.hash === '#other') {
      var fromLanding = !!landing && !landing.hidden;
      showTool();
      if (!S.otherOpen || fromLanding) openOther();
      return;
    }
    var c = codeFromHash();
    if (c) { showTool(); if (c !== S.code) chooseLanguage(c); return; }
    if (!first && location.hash && location.hash !== '#') return;
    if (first || !landing || landing.hidden) showLanding(!first);
  }
  window.addEventListener('hashchange', route);
  window.addEventListener('popstate', route);
  // the skip link moves focus without a #main entry, so Back never lands on a hash the page does not route
  var skip = document.querySelector('.skip-link');
  if (skip) skip.addEventListener('click', function (e) {
    var main = document.getElementById('main');
    if (!main) return;
    e.preventDefault();
    main.focus();
  });
  // a draft edited in another tab shows up in Continue
  var landQueued = false;
  window.addEventListener('storage', function (e) {
    if (landQueued || !landing || landing.hidden || (e.key && e.key.indexOf('ds5-translate:') !== 0)) return;
    landQueued = true;
    requestAnimationFrame(function () { landQueued = false; renderLanding(); });
  });

  renderAll();
  loadLanguageList();
  route(true);
})();
