export function readToken() {
    const value = location.hash.slice(1);
    return value.length === 43 && /^[A-Za-z0-9_-]{43}$/.test(value) ? value : null;
}
export function openDialog(dialog) { if (dialog && !dialog.open) dialog.showModal(); }
export function closeDialog(dialog) { if (dialog?.open) dialog.close(); }
export function scrollToSection(id) { document.getElementById(id)?.scrollIntoView({ behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth', block: 'start' }); }
export function printReport(host) {
    if (!host || host.isConnected === false) return;
    const originalUrl = location.href;
    const hidden = [], ancestors = [];
    for (let current = host; current.parentElement; current = current.parentElement) {
        for (const sibling of current.parentElement.children) if (sibling !== current && !sibling.classList.contains('therapy-print-hidden')) { hidden.push(sibling); sibling.classList.add('therapy-print-hidden'); }
        if (!current.parentElement.classList.contains('therapy-print-ancestor')) { ancestors.push(current.parentElement); current.parentElement.classList.add('therapy-print-ancestor'); }
        if (current.parentElement === document.body) break;
    }
    const details = [...host.querySelectorAll('details[data-print-expand]')].map(el => [el, el.open]);
    details.forEach(([el]) => { el.open = true; });
    const restore = () => { hidden.forEach(el => el.classList.remove('therapy-print-hidden')); ancestors.forEach(el => el.classList.remove('therapy-print-ancestor')); details.forEach(([el, open]) => { if (el.isConnected) el.open = open; }); if (location.href !== originalUrl) history.replaceState(history.state, '', originalUrl); };
    window.addEventListener('afterprint', restore, { once: true });
    requestAnimationFrame(() => requestAnimationFrame(() => {
        try { if (host.isConnected === false) return; if (location.hash) history.replaceState(history.state, '', location.pathname + location.search); window.print(); } finally { restore(); }
    }));
}
export async function copyLink(input) {
    if (!input) return false;
    try { await navigator.clipboard.writeText(input.value); return true; }
    catch { input.focus(); input.select(); return false; }
}
