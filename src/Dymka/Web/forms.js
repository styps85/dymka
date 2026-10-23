(function () {
  // Пароли и автозаполнение «Дымки». Страница только сообщает браузеру, что вы встали в поле входа или анкеты,
  // и что форма входа отправлена. Подставляет данные браузер - и только когда вы выбрали их в подсказке.
  if (window.top !== window || window.__dymkaForms) return;
  if (location.hostname === 'dymka.start') return;   // своя стартовая страница: в поле поиска подсказки карточек не нужны
  function post(m) { try { window.chrome.webview.postMessage(m); } catch (e) { } }
  var SKIP = { hidden: 1, submit: 1, button: 1, reset: 1, image: 1, file: 1, checkbox: 1, radio: 1, range: 1, color: 1, search: 1 };
  var filling = false, lastFocus = null, lastFields = [], lastSent = '', lastSentAt = 0;

  function typeOf(el) { return (el.getAttribute('type') || el.type || 'text').toLowerCase(); }
  function visible(el) {
    var r = el.getBoundingClientRect();
    if (r.width <= 0 || r.height <= 0) return false;
    var s = getComputedStyle(el);
    return s.visibility !== 'hidden' && s.display !== 'none';
  }
  function text(el) { return el ? (el.innerText || el.textContent || '').replace(/\s+/g, ' ').trim() : ''; }

  function labelOf(el) {
    var t = '';
    try {
      if (el.id) { var l = document.querySelector('label[for="' + CSS.escape(el.id) + '"]'); if (l) t += text(l) + ' '; }
    } catch (e) { }
    var p = el.closest('label');
    if (p) t += text(p) + ' ';
    if (el.getAttribute('aria-label')) t += el.getAttribute('aria-label') + ' ';
    var lb = el.getAttribute('aria-labelledby');
    if (lb) lb.split(/\s+/).forEach(function (id) { var x = document.getElementById(id); if (x) t += text(x) + ' '; });
    if (!t.trim()) {
      // подпись рядом: предыдущий элемент или ячейка таблицы слева, заголовок над полем
      var prev = el.previousElementSibling;
      if (prev && !/^(INPUT|SELECT|TEXTAREA|BUTTON)$/.test(prev.tagName) && text(prev).length < 90) t = text(prev);
      var par = el.parentElement;
      for (var k = 0; k < 3 && par && !t.trim(); k++) {
        var pv = par.previousElementSibling;
        if (pv && text(pv).length > 0 && text(pv).length < 90 && !pv.querySelector('input,select,textarea')) t = text(pv);
        par = par.parentElement;
      }
    }
    return t.replace(/\s+/g, ' ').trim().slice(0, 140);
  }

  // поле поиска сайта - не анкета
  function isSearch(el) {
    var t = typeOf(el), role = (el.getAttribute('role') || '').toLowerCase();
    if (t === 'search' || role === 'searchbox' || role === 'combobox') return true;
    if (el.closest('[role=search],form[role=search]')) return true;
    var s = ((el.name || '') + ' ' + (el.id || '') + ' ' + (el.getAttribute('placeholder') || '') + ' ' + (el.getAttribute('aria-label') || '')).toLowerCase();
    return /(^|[^a-z])(q|query|search|searchtext)([^a-z]|$)|поиск|искать|найти/.test(s);
  }

  function fieldsOf(scope) {
    return [].slice.call((scope || document).querySelectorAll('input,textarea')).filter(function (el) {
      var t = typeOf(el);
      return !SKIP[t] && t !== 'password' && !el.disabled && !el.readOnly && visible(el);
    });
  }
  function describe(list) {
    lastFields = list;
    return list.map(function (el, i) {
      return { i: i, type: typeOf(el), name: el.name || '', id: el.id || '', ac: el.getAttribute('autocomplete') || '',
        label: labelOf(el), ph: el.getAttribute('placeholder') || '' };
    });
  }
  function pwFields(scope) {
    return [].slice.call((scope || document).querySelectorAll('input[type=password]')).filter(function (e) { return visible(e) && !e.disabled; });
  }
  function userFor(pw) {
    var scope = pw.form || document;
    var cands = [].slice.call(scope.querySelectorAll('input')).filter(function (el) {
      var t = typeOf(el);
      return (t === 'text' || t === 'email' || t === 'tel' || t === 'number') && visible(el) && !el.disabled;
    });
    var marked = cands.filter(function (e) { return /username|email/.test(e.getAttribute('autocomplete') || ''); });
    if (marked.length) return marked[0];
    var best = null;
    for (var i = 0; i < cands.length; i++)
      if (cands[i].compareDocumentPosition(pw) & Node.DOCUMENT_POSITION_FOLLOWING) best = cands[i];
    return best;
  }
  function rectOf(el) { var r = el.getBoundingClientRect(); return { x: r.left, y: r.bottom, w: r.width, top: r.top }; }

  function setVal(el, v) {
    var proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    var setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
    try { el.focus(); } catch (e) { }
    setter.call(el, v);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    el.setAttribute('data-dymka-filled', '1');
  }

  document.addEventListener('focusin', function (e) {
    if (filling) return;
    var el = e.target;
    if (!(el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement)) return;
    lastFocus = el;
    var t = typeOf(el);
    var pws = pwFields(el.form || document);
    if (t === 'password' || (pws.length > 0 && userFor(pws[0]) === el)) {
      post({ __dymka: 'pw-focus', rect: rectOf(el) });
      return;
    }
    if (SKIP[t] || isSearch(el)) return;
    var list = fieldsOf(el.form || document);
    var idx = list.indexOf(el);
    if (idx < 0) return;
    post({ __dymka: 'card-focus', rect: rectOf(el), focus: idx, fields: describe(list) });
  }, true);
  document.addEventListener('focusout', function () { if (!filling) post({ __dymka: 'form-blur' }); }, true);

  // после входа: предложить сохранить пароль
  function capture(scope) {
    var filled = pwFields(scope).filter(function (p) { return p.value; });
    if (!filled.length) return;
    var pw = filled[0];
    // форма смены пароля: новый пароль - последний (он же повтор)
    if (filled.length >= 2 && filled[filled.length - 1].value === filled[filled.length - 2].value) pw = filled[filled.length - 1];
    var u = userFor(filled[0]);
    var key = (u ? u.value : '') + '\n' + pw.value;
    if (key === lastSent && Date.now() - lastSentAt < 30000) return;
    lastSent = key; lastSentAt = Date.now();
    post({ __dymka: 'pw-submit', user: u ? u.value : '', pass: pw.value });
  }
  var SUBMIT_TEXT = /войти|вход|log\s?in|sign\s?in|продолжить|далее|отправить|submit|continue|next|ok\b|авториз/i;
  document.addEventListener('submit', function (e) { capture(e.target); }, true);
  document.addEventListener('click', function (e) {
    var b = e.target && e.target.closest && e.target.closest('button,input[type=submit],input[type=image],[role=button],a');
    if (!b) return;
    var isSubmit = (b.tagName === 'BUTTON' && (!b.getAttribute('type') || b.type === 'submit')) || b.type === 'submit' || b.type === 'image';
    if (!isSubmit && !SUBMIT_TEXT.test(text(b) || b.value || '')) return;
    var f = b.form || b.closest('form');
    if (f || pwFields(document).length) capture(f || document);
  }, true);
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Enter' && e.target && e.target.tagName === 'INPUT') capture(e.target.form || document);
  }, true);

  window.__dymkaForms = {
    // «Заполнить карточкой» из меню правой кнопки: поля формы, в которой стоит курсор
    describeFocused: function () {
      var el = document.activeElement;
      var scope = el && el.form ? el.form : document;
      if (el && (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement)) lastFocus = el;
      var list = fieldsOf(scope);
      var idx = el ? list.indexOf(el) : -1;
      return JSON.stringify({ focus: idx, rect: el ? rectOf(el) : { x: 40, y: 40 }, fields: describe(list) });
    },
    fillLogin: function (u, p) {
      var pws = pwFields(lastFocus && lastFocus.form || document);
      if (!pws.length) pws = pwFields(document);
      if (!pws.length) return 'nopw';
      filling = true;
      try {
        var pw = pws[0], uf = userFor(pw);
        if (uf && u) setVal(uf, u);
        setVal(pw, p);
      } finally { filling = false; }
      return 'ok';
    },
    fill: function (pairs) {
      var n = 0;
      filling = true;
      try {
        pairs.forEach(function (x) {
          var el = lastFields[x.i];
          if (el && (!el.value || el.getAttribute('data-dymka-filled') || el === lastFocus)) { setVal(el, x.v); n++; }
        });
        if (lastFocus) try { lastFocus.focus(); } catch (e) { }
      } finally { filling = false; }
      return n;
    }
  };
})();
