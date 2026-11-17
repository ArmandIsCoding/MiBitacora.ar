// One observer for the page. Motion never makes a request or reports anything to the server.
const selector = '[data-brisa-animate="true"]';
const watched = new Set();
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
            for (const node of record.addedNodes) visit(node, watch);
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
