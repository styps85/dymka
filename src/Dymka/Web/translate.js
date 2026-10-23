(function () {
  // Перевод страницы «Дымки»: текст абзацев отправляется браузеру, тот переводит его через Яндекс и возвращает.
  // Куски абзаца между ссылками и выделениями идут одним сегментом через <wbr>, чтобы перевод учитывал контекст.
  if (window.__dymkaTr) return;
  var SKIP = { SCRIPT: 1, STYLE: 1, NOSCRIPT: 1, CODE: 1, PRE: 1, TEXTAREA: 1, INPUT: 1, SELECT: 1, OPTION: 1, SVG: 1, MATH: 1,
    KBD: 1, SAMP: 1, VAR: 1, IFRAME: 1, CANVAS: 1, TEMPLATE: 1, OBJECT: 1, EMBED: 1 };
  var INLINE = { A: 1, ABBR: 1, B: 1, BDI: 1, BDO: 1, CITE: 1, DFN: 1, EM: 1, I: 1, LABEL: 1, MARK: 1, Q: 1, S: 1, SMALL: 1,
    SPAN: 1, STRONG: 1, SUB: 1, SUP: 1, TIME: 1, U: 1, FONT: 1, BIG: 1, TT: 1, INS: 1, DEL: 1, DATA: 1 };
  var LETTER = /[A-Za-zÀ-ɏͰ-ϿЀ-ӿ֐-ۿऀ-ॿ฀-๿぀-ヿ㐀-鿿가-힯]/;
  var marked = [], applied = [], pending = {}, seq = 0, active = false, observer = null, timer = 0;

  function skip(el) {
    for (var e = el; e && e.nodeType === 1; e = e.parentElement) {
      if (SKIP[e.tagName] || e.isContentEditable) return true;
      if (e.getAttribute('translate') === 'no' || (e.classList && e.classList.contains('notranslate'))) return true;
    }
    return false;
  }

  function blockOf(n) {
    var e = n.parentElement;
    while (e && INLINE[e.tagName] && e.parentElement) e = e.parentElement;
    return e;
  }

  function collect(root) {
    var groups = [], byBlock = new Map();
    var w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
      acceptNode: function (n) {
        if (n.__dymkaDone) return NodeFilter.FILTER_REJECT;
        var t = n.nodeValue;
        if (!t || t.trim().length < 2 || !LETTER.test(t)) return NodeFilter.FILTER_REJECT;
        return NodeFilter.FILTER_ACCEPT;
      }
    });
    var n;
    while ((n = w.nextNode())) {
      if (skip(n.parentElement)) continue;
      var b = blockOf(n);
      if (!b) continue;
      var g = byBlock.get(b);
      if (!g) { g = []; byBlock.set(b, g); groups.push(g); }
      g.push(n);
    }
    return groups;
  }

  function esc(s) { return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); }

  function send(groups) {
    var chars = 0;
    for (var i = 0; i < groups.length; i += 120) {
      var part = groups.slice(i, i + 120), items = [];
      for (var k = 0; k < part.length; k++) {
        var g = part[k];
        for (var j = 0; j < g.length; j++) { g[j].__dymkaDone = true; marked.push(g[j]); }
        var seg = g.map(function (x) { return esc(x.nodeValue.trim()); }).join('<wbr>');
        chars += seg.length;
        items.push(seg);
      }
      var id = ++seq;
      pending[id] = part;
      window.chrome.webview.postMessage({ __dymka: 'tr', id: id, items: items });
    }
    return chars;
  }

  function scan() {
    timer = 0;
    if (!active || !document.body) return;
    var groups = collect(document.body);
    if (groups.length) send(groups);
  }

  window.__dymkaTr = {
    start: function () {
      if (active) return JSON.stringify({ groups: 0, already: true });
      active = true;
      var groups = document.body ? collect(document.body) : [];
      var chars = send(groups);
      // новые абзацы (подгрузка, бесконечная лента) переводятся по мере появления
      observer = new MutationObserver(function () { if (!timer) timer = setTimeout(scan, 700); });
      if (document.body) observer.observe(document.body, { childList: true, subtree: true });
      return JSON.stringify({ groups: groups.length, chars: chars });
    },
    apply: function (id, res) {
      var groups = pending[id];
      delete pending[id];
      if (!groups || !active || !res) return;
      for (var i = 0; i < groups.length; i++) {
        var g = groups[i], r = res[i];
        if (!r || !r.length) continue;
        if (r.length !== g.length) r = [r.join(' ')];
        for (var j = 0; j < g.length; j++) {
          var n = g[j], o = n.nodeValue;
          if (n.__dymkaOrig === undefined) n.__dymkaOrig = o;
          var lead = o.match(/^\s*/)[0], trail = o.match(/\s*$/)[0];
          n.nodeValue = j < r.length ? lead + r[j] + trail : '';
          applied.push(n);
        }
      }
    },
    restore: function () {
      active = false;
      if (observer) { observer.disconnect(); observer = null; }
      if (timer) { clearTimeout(timer); timer = 0; }
      for (var i = 0; i < applied.length; i++) {
        var n = applied[i];
        if (n.__dymkaOrig !== undefined) { n.nodeValue = n.__dymkaOrig; delete n.__dymkaOrig; }
      }
      for (var k = 0; k < marked.length; k++) delete marked[k].__dymkaDone;
      applied = []; marked = []; pending = {};
      return 'ok';
    },
    pending: function () { return Object.keys(pending).length; }
  };
})();
