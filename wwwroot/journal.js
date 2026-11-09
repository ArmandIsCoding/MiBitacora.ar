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
