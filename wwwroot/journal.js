export function showDialog(dialog) {
    if (dialog?.isConnected && !dialog.open) {
        if (!dialog.dataset.cancelBound) {
            dialog.addEventListener("cancel", event => event.preventDefault());
            dialog.dataset.cancelBound = "true";
        }
        dialog.showModal();
    }
}

export function reveal(element) {
    if (!element) return;
    const composer = element.nextElementSibling;
    if (composer?.classList.contains("composer-wrap")) {
        element.style.scrollMarginBottom = `${Math.ceil(composer.getBoundingClientRect().height) + 16}px`;
    }
    element?.scrollIntoView({ block: "nearest" });
}

export function centerReply(target) {
    const host = typeof target === "string" ? document.getElementById(target) : target;
    if (!host?.isConnected) return;
    const card = host.querySelector(".reflection-card") ?? host;
    const modal = document.querySelector("dialog[open]");
    if (modal && !modal.contains(card)) return;
    const header = document.querySelector(".journal-top");
    const composer = document.querySelector(".composer-wrap");
    const top = header ? header.getBoundingClientRect().height + 16 : 16;
    const bottom = composer ? composer.getBoundingClientRect().height + 16 : 16;
    let available = Math.max(120, window.innerHeight - top - bottom);
    let scrollParent = null;
    for (let parent = card.parentElement; parent; parent = parent.parentElement) {
        if (/(auto|scroll)/.test(getComputedStyle(parent).overflowY) && parent.scrollHeight > parent.clientHeight) {
            available = Math.min(available, parent.clientHeight - 32);
            scrollParent ??= parent;
        }
    }
    const nested = scrollParent && scrollParent !== document.documentElement && scrollParent !== document.body;
    const padding = scrollParent ?? document.documentElement;
    const style = padding ? getComputedStyle(padding) : null;
    card.style.scrollMarginTop = `${Math.max(0, (nested ? 16 : top) - (parseFloat(style?.scrollPaddingTop) || 0))}px`;
    card.style.scrollMarginBottom = `${Math.max(0, (nested ? 16 : bottom) - (parseFloat(style?.scrollPaddingBottom) || 0))}px`;
    card.scrollIntoView({ block: card.getBoundingClientRect().height > available ? "start" : "center",
        behavior: window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth" });
}

export function focusSection(heading) {
    window.scrollTo({ top: 0 });
    heading?.focus({ preventScroll: true });
}

export function focusComposer(element) {
    if (!element) return;
    element.scrollIntoView({ block: "nearest" });
    element.focus({ preventScroll: true });
}

export async function copyText(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        const field = document.createElement("textarea");
        field.value = text;
        field.style.position = "fixed";
        field.style.opacity = "0";
        document.body.append(field);
        const previous = document.activeElement;
        field.select();
        try {
            return document.execCommand("copy");
        } finally {
            field.remove();
            previous?.focus();
        }
    }
}

export function printSheet() {
    window.print();
}
