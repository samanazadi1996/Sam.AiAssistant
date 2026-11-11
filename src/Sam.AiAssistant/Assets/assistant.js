/*
 * AI Assistant widget — کاملاً مستقل.
 * استفاده:  <script src="/ai-assistant/assistant.js" defer></script>
 * اختیاری:  data-position="left|right"  data-dir="rtl|ltr"
 * دکمه، پنجره چت و همه استایل‌ها داخل Shadow DOM ساخته می‌شود و با CSS صفحه تداخل ندارد.
 */
(function () {
    'use strict';

    if (window.__bpeAiAssistantLoaded) return;
    window.__bpeAiAssistantLoaded = true;

    var script = document.currentScript || document.querySelector('script[src*="assistant.js"]');
    if (!script) return;

    var base = script.src.replace(/assistant\.js(\?.*)?$/, '');
    var position = script.getAttribute('data-position') === 'right' ? 'right' : 'left';
    var dir = script.getAttribute('data-dir') === 'ltr' ? 'ltr' : 'rtl';
    var HEADERS = { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' };
    // وقتی ویجت در سایت دیگری است کوکی نمی‌فرستیم؛ هویت فقط با توکن می‌آید.
    var SAME_ORIGIN = (function () { try { return new URL(base).origin === location.origin; } catch (e) { return true; } })();

    /*
     * توکن هویت (اختیاری):
     *   data-token="..."            مقدار ثابت
     *   data-token-fn="getAiToken"  نام تابع سراسری (می‌تواند async باشد) که در هر درخواست صدا زده می‌شود
     * به‌صورت هدر X-Ai-Token برای سرور ارسال می‌شود و سرور با o.ResolveToken آن را به کاربر تبدیل می‌کند.
     */
    async function getToken() {
        var fn = script.getAttribute('data-token-fn');
        if (fn && typeof window[fn] === 'function') {
            var t = await window[fn]();
            return t ? String(t) : null;
        }
        return script.getAttribute('data-token');
    }

    var CSS = [
        ':host{all:initial}',
        '*{box-sizing:border-box}',
        '.root{position:fixed;bottom:20px;' + position + ':20px;z-index:2147483000;font-family:inherit;font-size:14px;color:#16204a;direction:' + dir + '}',
        '.fab{width:60px;height:60px;border:0;border-radius:50%;padding:0;cursor:pointer;display:flex;align-items:center;justify-content:center;',
        'background:linear-gradient(135deg,#2f6df6,#1e4fd0);color:#fff;box-shadow:0 10px 24px -8px rgba(47,109,246,.6);animation:float 3s ease-in-out infinite}',
        '.fab:hover .robot{transform:rotate(-8deg) scale(1.08)}',
        '.robot{transition:transform .2s}',
        '.eye{transform-box:fill-box;transform-origin:center;animation:blink 4s infinite}',
        '.antenna{animation:pulse 1.5s ease-in-out infinite}',
        '@keyframes float{0%,100%{transform:translateY(0)}50%{transform:translateY(-6px)}}',
        '@keyframes blink{0%,92%,100%{transform:scaleY(1)}96%{transform:scaleY(.1)}}',
        '@keyframes pulse{0%,100%{opacity:1}50%{opacity:.35}}',
        '@keyframes bounce{0%,80%,100%{transform:scale(.6);opacity:.5}40%{transform:scale(1);opacity:1}}',
        '@media (prefers-reduced-motion:reduce){.fab,.eye,.antenna{animation:none}}',
        '.panel{display:none;flex-direction:column;width:min(24rem,calc(100vw - 2rem));height:min(34rem,calc(100vh - 2.5rem));overflow:hidden;',
        'background:#fff;border:1px solid rgba(16,26,60,.14);border-radius:16px;box-shadow:0 24px 48px -16px rgba(16,26,60,.35)}',
        '.open .panel{display:flex}.open .fab{display:none}',
        '.head{display:flex;align-items:center;justify-content:space-between;padding:12px 16px;color:#fff;font-weight:700;background:linear-gradient(135deg,#2f6df6,#1e4fd0)}',
        '.close{border:0;background:transparent;color:inherit;font-size:24px;line-height:1;cursor:pointer}',
        '.msgs{flex:1;display:flex;flex-direction:column;gap:10px;padding:16px;overflow-y:auto;background:#f8faff}',
        '.msg{max-width:85%;padding:9px 14px;border-radius:14px;line-height:1.8;white-space:pre-wrap;word-break:break-word}',
        '.user{align-self:flex-start;background:#2f6df6;color:#fff}',
        '.bot{align-self:flex-end;background:#fff;border:1px solid rgba(16,26,60,.14)}',
        '.err{align-self:flex-end;background:rgba(227,64,59,.08);border:1px solid #e3403b;color:#e3403b}',
        '.dots{display:inline-flex;gap:5px;align-items:center}',
        '.dots i{width:7px;height:7px;border-radius:50%;background:#98a2c0;animation:bounce 1s infinite ease-in-out}',
        '.dots i:nth-child(2){animation-delay:.15s}.dots i:nth-child(3){animation-delay:.3s}',
        '.form{display:flex;align-items:flex-end;gap:8px;padding:10px;border-top:1px solid rgba(16,26,60,.14);background:#fff}',
        'textarea{flex:1;max-height:120px;min-height:40px;padding:8px 12px;border:1px solid rgba(16,26,60,.2);border-radius:12px;resize:none;font:inherit;color:inherit;background:#fff;direction:' + dir + '}',
        'textarea:focus{outline:2px solid rgba(47,109,246,.25);border-color:#2f6df6}',
        '.send{width:40px;height:40px;border:0;border-radius:50%;background:#2f6df6;color:#fff;cursor:pointer;display:flex;align-items:center;justify-content:center}',
        '.send:disabled{opacity:.5;cursor:not-allowed}',
        dir === 'rtl' ? '.send svg{transform:scaleX(-1)}' : '',
        '@media (max-width:575px){.open .panel{position:fixed;inset:0;width:100%;height:100%;border-radius:0}}'
    ].join('');

    var ROBOT =
        '<svg class="robot" viewBox="0 0 48 48" width="34" height="34" aria-hidden="true">' +
        '<line x1="24" y1="5" x2="24" y2="12" stroke="currentColor" stroke-width="2.5" stroke-linecap="round"/>' +
        '<circle class="antenna" cx="24" cy="5" r="3" fill="#ffd166"/>' +
        '<rect x="8" y="12" width="32" height="26" rx="9" fill="#fff"/>' +
        '<rect x="3" y="21" width="5" height="9" rx="2.5" fill="#fff"/><rect x="40" y="21" width="5" height="9" rx="2.5" fill="#fff"/>' +
        '<circle class="eye" cx="17" cy="25" r="3.5" fill="#2f6df6"/><circle class="eye" cx="31" cy="25" r="3.5" fill="#2f6df6"/>' +
        '<path d="M18 32q6 4 12 0" fill="none" stroke="#2f6df6" stroke-width="2.5" stroke-linecap="round"/></svg>';

    var SEND =
        '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
        '<path d="M22 2 11 13M22 2l-7 20-4-9-9-4z"/></svg>';

    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) e.className = cls;
        if (text != null) e.textContent = text;
        return e;
    }

    async function api(path, options) {
        var headers = Object.assign({}, HEADERS);
        var token = await getToken();
        if (token) headers['X-Ai-Token'] = token;
        return fetch(base + 'api/' + path, Object.assign({ credentials: SAME_ORIGIN ? 'same-origin' : 'omit', headers: headers }, options));
    }

    function start(cfg) {
        var host = document.createElement('div');
        host.setAttribute('data-ai-assistant', '');
        document.body.appendChild(host);
        var shadow = host.attachShadow({ mode: 'open' });

        var style = document.createElement('style');
        style.textContent = CSS;
        shadow.appendChild(style);

        var root = el('div', 'root');
        shadow.appendChild(root);

        var fab = el('button', 'fab');
        fab.type = 'button';
        fab.title = cfg.buttonTitle || '';
        fab.setAttribute('aria-label', cfg.buttonTitle || 'AI Assistant');
        fab.innerHTML = ROBOT;

        var panel = el('section', 'panel');
        panel.setAttribute('role', 'dialog');
        var head = el('div', 'head');
        head.appendChild(el('span', null, cfg.panelTitle || ''));
        var close = el('button', 'close', '×');
        close.type = 'button';
        close.setAttribute('aria-label', 'close');
        head.appendChild(close);

        var msgs = el('div', 'msgs');
        msgs.setAttribute('role', 'log');
        msgs.setAttribute('aria-live', 'polite');

        var form = el('form', 'form');
        var input = el('textarea');
        input.rows = 1;
        input.maxLength = cfg.maxLength || 1000;
        input.placeholder = cfg.inputPlaceholder || '';
        var send = el('button', 'send');
        send.type = 'submit';
        send.setAttribute('aria-label', 'send');
        send.innerHTML = SEND;
        form.appendChild(input);
        form.appendChild(send);

        panel.appendChild(head);
        panel.appendChild(msgs);
        panel.appendChild(form);
        root.appendChild(panel);
        root.appendChild(fab);

        function add(text, kind) {
            var m = el('div', 'msg ' + kind, text); // textContent: خروجی مدل هرگز HTML تفسیر نمی‌شود
            msgs.appendChild(m);
            msgs.scrollTop = msgs.scrollHeight;
            return m;
        }

        if (cfg.welcomeMessage) add(cfg.welcomeMessage, 'bot');

        function setOpen(open) {
            root.classList.toggle('open', open);
            if (open) input.focus();
        }

        function grow() {
            input.style.height = 'auto';
            input.style.height = Math.min(input.scrollHeight, 120) + 'px';
        }

        // شناسه گفتگو: هر بار بارگذاری صفحه یک گفتگوی جدید (حافظه سمت سرور نگه‌داری می‌شود)
        var conversationId = (window.crypto && crypto.randomUUID) ? crypto.randomUUID()
            : String(Date.now()) + '-' + Math.random().toString(36).slice(2);

        var busy = false;
        async function submit() {
            var text = input.value.trim();
            if (!text || busy) return;
            add(text, 'user');
            input.value = '';
            grow();
            busy = true;
            send.disabled = true;
            var loading = add('', 'bot');
            loading.innerHTML = '<span class="dots"><i></i><i></i><i></i></span>';
            try {
                var res = await api('chat', { method: 'POST', body: JSON.stringify({ message: text, conversationId: conversationId }) });
                var data = null;
                try { data = await res.json(); } catch (e) { /* غیر JSON */ }
                loading.remove();
                if (data && data.success) add(data.answer, 'bot');
                else add((data && data.error) || (res.status === 401 ? 'Session expired.' : 'Error.'), 'err');
            } catch (e) {
                loading.remove();
                add('Network error.', 'err');
            } finally {
                busy = false;
                send.disabled = false;
                input.focus();
            }
        }

        fab.addEventListener('click', function () { setOpen(true); });
        close.addEventListener('click', function () { setOpen(false); fab.focus(); });
        form.addEventListener('submit', function (e) { e.preventDefault(); submit(); });
        input.addEventListener('input', grow);
        input.addEventListener('keydown', function (e) {
            // Enter = ارسال، Shift+Enter = خط جدید؛ هنگام IME نادیده گرفته می‌شود.
            if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); submit(); }
        });
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && root.classList.contains('open')) setOpen(false);
        });
    }

    function init() {
        api('config', { method: 'GET' })
            .then(function (r) { return r.ok ? r.json() : { enabled: false }; })
            .then(function (cfg) { if (cfg && cfg.enabled) start(cfg); })
            .catch(function () { /* بدون نمایش دکمه */ });
    }

    if (document.body) init();
    else document.addEventListener('DOMContentLoaded', init);
})();
