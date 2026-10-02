// Small browser-side helpers the Blazor pages can't do from C# alone.
window.lemon = {
    // Enter sends, Shift+Enter inserts a newline (matches the desktop apps).
    // Has to be a real DOM listener: a Blazor @onkeydown can't conditionally
    // preventDefault in time, since the round trip to the server is async.
    initChatInput(element, dotNetRef) {
        if (!element || element.__lemonInit) return;
        element.__lemonInit = true;
        element.addEventListener('keydown', e => {
            if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
                e.preventDefault();
                dotNetRef.invokeMethodAsync('OnEnterPressed');
            }
        });
        element.addEventListener('input', () => window.lemon.autoGrow(element));
    },

    // Grows with its content up to a max height, then scrolls.
    autoGrow(element) {
        if (!element) return;
        element.style.height = 'auto';
        element.style.height = Math.min(element.scrollHeight, 140) + 'px';
    },

    // Theme: 'light' | 'dark' | 'system'. Stored per browser (localStorage);
    // 'system' removes the override so the OS preference applies.
    getTheme() {
        try { return localStorage.getItem('lemon-theme') || 'system'; } catch (e) { return 'system'; }
    },

    setTheme(theme) {
        try {
            if (theme === 'light' || theme === 'dark') {
                localStorage.setItem('lemon-theme', theme);
                document.documentElement.setAttribute('data-theme', theme);
            } else {
                localStorage.removeItem('lemon-theme');
                document.documentElement.removeAttribute('data-theme');
            }
        } catch (e) { }
    },

    async copyText(text) {
        await navigator.clipboard.writeText(text ?? '');
    },

    // Clipboard images must be PNG - re-encode through a canvas if needed.
    async copyImage(url) {
        const response = await fetch(url);
        let blob = await response.blob();
        if (blob.type !== 'image/png') {
            const bitmap = await createImageBitmap(blob);
            const canvas = document.createElement('canvas');
            canvas.width = bitmap.width;
            canvas.height = bitmap.height;
            canvas.getContext('2d').drawImage(bitmap, 0, 0);
            blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
        }
        await navigator.clipboard.write([new ClipboardItem({ 'image/png': blob })]);
    },
};

// Chat/log lists follow new content, but only while the user is already at
// (or near) the bottom - scrolling up to read must not get yanked back down.
(function () {
    const followSelectors = ['.messages', '.log-entries'];
    // Last scrollHeight seen per element. Whether the user was at the bottom
    // is decided against the PREVIOUS height (before this change grew the
    // content) - independent of scroll events, which browsers throttle.
    const lastHeight = new WeakMap();

    function scrollIfStuck(element) {
        const previous = lastHeight.get(element);
        const wasAtBottom = previous === undefined
            || previous - element.scrollTop - element.clientHeight < 40;
        if (wasAtBottom) element.scrollTop = element.scrollHeight;
        lastHeight.set(element, element.scrollHeight);
    }

    function check() {
        for (const selector of followSelectors) {
            document.querySelectorAll(selector).forEach(scrollIfStuck);
        }
    }

    new MutationObserver(check).observe(document.documentElement, { childList: true, subtree: true, characterData: true });
})();

// Keep the saved theme applied across Blazor's in-app (enhanced) navigation,
// which patches the document and can drop the <html data-theme> attribute the
// inline script in App.razor set on first load - so re-assert it whenever it
// disappears or changes behind our back.
(function () {
    function saved() { try { return localStorage.getItem('lemon-theme'); } catch (e) { return null; } }
    function apply() {
        const t = saved();
        const want = (t === 'light' || t === 'dark') ? t : null;
        if (document.documentElement.getAttribute('data-theme') !== want) {
            if (want) document.documentElement.setAttribute('data-theme', want);
            else document.documentElement.removeAttribute('data-theme');
        }
    }
    apply();
    new MutationObserver(apply).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    if (window.Blazor && Blazor.addEventListener) Blazor.addEventListener('enhancedload', apply);
})();

// Resizable side panes (sidebar / right panel): drag a .resize-handle, double-click to reset. The widths live in CSS
// variables on <html> and are remembered per browser (localStorage 'lemon-layout'); re-applied whenever in-app
// navigation drops the style attribute (same reason as the theme above).
(function () {
    const LIMITS = { '--sidebar-w': [200, 560], '--rpanel-w': [220, 520] };
    let dragging = false;
    function load() { try { return JSON.parse(localStorage.getItem('lemon-layout') || '{}'); } catch (e) { return {}; } }
    function save(o) { try { localStorage.setItem('lemon-layout', JSON.stringify(o)); } catch (e) { } }
    function apply() {
        if (dragging) return;
        const o = load(), root = document.documentElement;
        for (const k in LIMITS) {
            const want = o[k] ? o[k] + 'px' : '';
            if (root.style.getPropertyValue(k) !== want) {
                if (want) root.style.setProperty(k, want); else root.style.removeProperty(k);
            }
        }
    }
    apply();
    new MutationObserver(apply).observe(document.documentElement, { attributes: true, attributeFilter: ['style'] });

    document.addEventListener('pointerdown', e => {
        const handle = e.target.closest && e.target.closest('.resize-handle');
        if (!handle) return;
        e.preventDefault();
        const varName = handle.dataset.var, side = handle.dataset.side;
        const pane = document.querySelector(side === 'left' ? '.sidebar' : '.right-panel');
        const [min, max] = LIMITS[varName];
        dragging = true;
        try { handle.setPointerCapture(e.pointerId); } catch (x) { }
        handle.classList.add('dragging');
        document.body.style.userSelect = 'none';
        const move = ev => {
            const r = pane.getBoundingClientRect();
            const w = side === 'left' ? ev.clientX - r.left : r.right - ev.clientX;
            document.documentElement.style.setProperty(varName, Math.round(Math.min(max, Math.max(min, w))) + 'px');
        };
        const up = ev => {
            handle.removeEventListener('pointermove', move);
            handle.removeEventListener('pointerup', up);
            try { handle.releasePointerCapture(ev.pointerId); } catch (x) { }
            handle.classList.remove('dragging');
            document.body.style.userSelect = '';
            const o = load();
            o[varName] = parseInt(document.documentElement.style.getPropertyValue(varName));
            save(o);
            dragging = false;
        };
        handle.addEventListener('pointermove', move);
        handle.addEventListener('pointerup', up);
    });

    document.addEventListener('dblclick', e => {
        const handle = e.target.closest && e.target.closest('.resize-handle');
        if (!handle) return;
        const o = load();
        delete o[handle.dataset.var];
        save(o);
        document.documentElement.style.removeProperty(handle.dataset.var);
    });
})();
