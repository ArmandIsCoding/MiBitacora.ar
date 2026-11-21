// Native fullscreen where available; an accessible viewport overlay elsewhere (including mobile).
export function createFullscreen(host, button, onChange, doc = document) {
    const controller = new AbortController(), signal = controller.signal;
    let expanded = false, disposed = false, pending = false, returnFocus = null;
    let originalOverflow = '', originalRole = null, originalModal = null, inertElements = [];
    let originalScroll = null;
    function enterView() {
        if (expanded || disposed) return;
        expanded = true;
        originalOverflow = doc.documentElement.style.overflow;
        originalRole = host.getAttribute('role'); originalModal = host.getAttribute('aria-modal');
        doc.documentElement.style.overflow = 'hidden';
        for (let current = host; current.parentElement; current = current.parentElement) {
            for (const sibling of current.parentElement.children) if (sibling !== current && !sibling.inert) {
                inertElements.push(sibling); sibling.inert = true;
            }
            if (current.parentElement === doc.body) break;
        }
        host.classList.add('is-expanded'); host.setAttribute('role', 'dialog'); host.setAttribute('aria-modal', 'true');
        button.setAttribute('aria-label', 'Salir de pantalla completa'); button.setAttribute('aria-pressed', 'true'); button.title = 'Salir de pantalla completa';
        host.querySelector('[data-fullscreen-open]').hidden = true;
        host.querySelector('[data-fullscreen-close]').hidden = false;
        host.querySelector('[data-fullscreen-label]').textContent = 'Salir';
        button.focus({ preventScroll: true }); onChange(true);
    }
    function leaveView(restoreFocus = true) {
        if (!expanded) return;
        expanded = false;
        host.classList.remove('is-expanded'); doc.documentElement.style.overflow = originalOverflow;
        for (const element of inertElements) element.inert = false;
        inertElements = [];
        if (originalRole === null) host.removeAttribute('role'); else host.setAttribute('role', originalRole);
        if (originalModal === null) host.removeAttribute('aria-modal'); else host.setAttribute('aria-modal', originalModal);
        button.setAttribute('aria-label', 'Abrir mapa a pantalla completa'); button.setAttribute('aria-pressed', 'false'); button.title = 'Pantalla completa';
        host.querySelector('[data-fullscreen-open]').hidden = false;
        host.querySelector('[data-fullscreen-close]').hidden = true;
        host.querySelector('[data-fullscreen-label]').textContent = 'Pantalla completa';
        if (restoreFocus && returnFocus?.isConnected) returnFocus.focus({ preventScroll: true });
        if (restoreFocus && originalScroll) doc.defaultView?.scrollTo({ left: originalScroll.x, top: originalScroll.y, behavior: 'instant' });
        onChange(false);
    }
    async function close() {
        if (doc.fullscreenElement === host && doc.exitFullscreen) {
            try { await doc.exitFullscreen(); } catch { /* A browser transition may already be closing it. */ }
        }
        leaveView();
    }
    async function toggle() {
        if (pending || disposed) return;
        if (expanded) { await close(); return; }
        pending = true;
        returnFocus = doc.activeElement;
        originalScroll = doc.defaultView ? { x: doc.defaultView.scrollX, y: doc.defaultView.scrollY } : null;
        // Request synchronously in the trusted button event, before any asynchronous work.
        try {
            if (host.requestFullscreen && doc.fullscreenEnabled !== false && !doc.fullscreenElement)
                await host.requestFullscreen();
        } catch { /* The viewport overlay works when fullscreen is unavailable or denied. */ }
        finally { pending = false; }
        if (disposed) { if (doc.fullscreenElement === host) { try { await doc.exitFullscreen(); } catch { } } return; }
        enterView();
    }
    button.addEventListener('click', toggle, { signal });
    doc.addEventListener('fullscreenchange', () => {
        if (doc.fullscreenElement === host) enterView();
        else if (expanded) leaveView();
    }, { signal });
    host.addEventListener('keydown', event => {
        if (!expanded) return;
        if (event.key === 'Escape') { event.preventDefault(); event.stopImmediatePropagation(); void close(); return; }
        if (event.key !== 'Tab') return;
        const controls = [...host.querySelectorAll('button,input,a[href],[tabindex="0"]')]
            .filter(el => !el.disabled && el.getClientRects().length > 0);
        const first = controls[0], last = controls.at(-1);
        if (event.shiftKey && (doc.activeElement === first || !host.contains(doc.activeElement))) { event.preventDefault(); last?.focus(); }
        else if (!event.shiftKey && (doc.activeElement === last || !host.contains(doc.activeElement))) { event.preventDefault(); first?.focus(); }
    }, { signal, capture: true });
    return {
        close,
        dispose() {
            if (disposed) return;
            disposed = true; controller.abort(); leaveView(false);
            if (doc.fullscreenElement === host && doc.exitFullscreen) void doc.exitFullscreen().catch(() => {});
        }
    };
}
