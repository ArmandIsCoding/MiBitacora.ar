// One observer for the page. Motion never makes a request or reports anything to the server.
const selector = '[data-brisa-animate="true"]';
const watched = new Set();
const preferenceKey = 'mibitacora.brisa.motion';
const modes = ['system', 'on', 'off'];
let mode = 'system';
try { const saved = localStorage.getItem(preferenceKey); if (modes.includes(saved)) mode = saved; } catch { /* Storage may be unavailable. */ }
const reduced = typeof matchMedia === 'function' ? matchMedia('(prefers-reduced-motion: reduce)') : null;
function syncControls(node) {
    document.documentElement.dataset.brisaMotion = mode;
    for (const control of node.querySelectorAll('[data-brisa-motion-control]')) control.value = mode;
    const note = mode === 'on' ? 'Animaciones suaves activadas. Podés detenerlas cuando quieras.' :
        mode === 'off' ? 'Sin animaciones. Brisa conserva sus expresiones.' :
        reduced?.matches ? 'Tu dispositivo pide reducir el movimiento. Las expresiones cambian sin animarse.' :
        'Brisa se mueve suavemente y se pausa cuando no está a la vista.';
    for (const element of node.querySelectorAll('[data-brisa-motion-note]')) element.textContent = note;
}
syncControls(document.documentElement);
reduced?.addEventListener?.('change', () => syncControls(document.documentElement));
document.addEventListener('change', event => {
    if (!event.target.matches?.('[data-brisa-motion-control]') || !modes.includes(event.target.value)) return;
    mode = event.target.value;
    try { localStorage.setItem(preferenceKey, mode); } catch { /* The choice still works for this page. */ }
    syncControls(document.documentElement);
});
globalThis.addEventListener?.('storage', event => {
    if (event.key !== preferenceKey) return;
    mode = modes.includes(event.newValue) ? event.newValue : 'system';
    syncControls(document.documentElement);
});
const observer = typeof IntersectionObserver === 'function' ? new IntersectionObserver(entries => {
    for (const entry of entries) entry.target.dataset.brisaVisible = String(entry.isIntersecting);
}, { threshold: 0.01 }) : null;

function visit(node, action) {
    if (node.nodeType !== 1) return;
    if (node.matches(selector)) action(node);
    for (const portrait of node.querySelectorAll(selector)) action(portrait);
}
function watch(portrait) {
    if (watched.has(portrait)) return;
    watched.add(portrait);
    if (observer) observer.observe(portrait);
    else portrait.dataset.brisaVisible = 'true';
}
function unwatch(portrait) {
    if (!watched.delete(portrait)) return;
    observer?.unobserve(portrait);
}
function syncVisibility() { document.documentElement.dataset.brisaPageHidden = String(document.hidden); }
syncVisibility();
document.addEventListener('visibilitychange', syncVisibility);
visit(document.documentElement, watch);
new MutationObserver(records => {
    for (const record of records) {
        if (record.type === 'attributes') {
            delete record.target.dataset.brisaSettled;
            // Blazor can reuse a static avatar as a live one after a reply arrives.
            if (record.target.matches(selector)) watch(record.target);
            else unwatch(record.target);
        } else {
            for (const node of record.removedNodes) visit(node, unwatch);
            for (const node of record.addedNodes) {
                visit(node, watch);
                if (node.nodeType === 1) syncControls(node.parentElement ?? node);
            }
        }
    }
}).observe(document.documentElement, { childList: true, subtree: true, attributes: true,
    attributeFilter: ['data-brisa-activity', 'data-brisa-animate'] });
document.addEventListener('animationend', event => {
    if (event.animationName === 'brisa-respond-face') {
        const portrait = event.target.closest(selector);
        if (portrait?.dataset.brisaActivity === 'responding') portrait.dataset.brisaSettled = 'true';
    }
});
