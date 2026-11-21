export function openDialog(dialog) { if (dialog && !dialog.open) { dialog.showModal(); dialog.querySelector('input:not([type=hidden])')?.focus(); } }
export function closeDialog(dialog) { if (dialog?.open) dialog.close(); }

export function resolveDrop(host, target, y, movingId) {
    if (!target || !host.contains(target)) return null;
    const column = target.closest('[data-task-column]');
    if (!column) return null;
    const card = target.closest('[data-task-card]');
    let before = null;
    if (card) {
        if (card.dataset.taskCard === movingId) return null;
        const rect = card.getBoundingClientRect();
        if (y < rect.top + rect.height / 2) before = card.dataset.taskCard;
        else {
            let next = card.nextElementSibling;
            while (next && (!next.dataset.taskCard || next.dataset.taskCard === movingId)) next = next.nextElementSibling;
            before = next?.dataset.taskCard ?? null;
        }
    }
    return { column: column.dataset.taskColumn, before };
}

export function create(host, bridge) {
    let drag = null, ghost = null, pointer = null, frame = 0, suppressedUntil = 0, disposed = false, saving = false;
    const listeners = [];
    const listen = (element, event, handler, options) => { element.addEventListener(event, handler, options); listeners.push(() => element.removeEventListener(event, handler, options)); };
    const locked = () => disposed || saving || host.dataset.readonly === 'true' || host.querySelector('.tasks-search input')?.value?.length > 0;
    const clearMark = () => host.querySelectorAll('.task-drop-before,.task-drop-column').forEach(el => el.classList.remove('task-drop-before', 'task-drop-column'));
    const cleanup = () => {
        cancelAnimationFrame(frame); frame = 0; clearMark();
        drag?.element.classList.remove('task-is-dragging'); ghost?.remove(); ghost = null; drag = null;
        if (pointer?.handle.hasPointerCapture?.(pointer.id)) pointer.handle.releasePointerCapture(pointer.id);
        pointer = null;
    };
    const mark = drop => {
        clearMark(); if (!drop) return;
        const col = [...host.querySelectorAll('[data-task-column]')].find(el => el.dataset.taskColumn === drop.column);
        col?.classList.add('task-drop-column');
        if (drop.before) [...host.querySelectorAll('[data-task-card]')].find(el => el.dataset.taskCard === drop.before)?.classList.add('task-drop-before');
    };
    const start = element => {
        if (locked() || !element || element.draggable === false) return false;
        drag = { element, id: element.dataset.taskCard, revision: Number(host.dataset.revision), drop: null };
        element.classList.add('task-is-dragging'); return true;
    };
    const finish = async () => {
        const result = drag && { id: drag.id, revision: drag.revision, drop: drag.drop };
        suppressedUntil = performance.now() + 350; cleanup();
        if (!result?.drop || locked()) return;
        saving = true;
        try { await bridge.invokeMethodAsync('MoveTaskCard', result.id, result.drop.column, result.drop.before, result.revision); }
        catch { /* A disconnected Blazor circuit displays its own reconnect control. */ }
        finally { saving = false; }
    };
    const scrollAt = (x, y) => {
        const columns = host.querySelector('[data-task-columns]');
        if (columns) { const rect = columns.getBoundingClientRect(); if (x > rect.right - 48) columns.scrollLeft += 12; else if (x < rect.left + 48) columns.scrollLeft -= 12; }
        const target = document.elementFromPoint(x, y)?.closest('[data-task-column]')?.querySelector('[data-task-list]');
        if (target) { const rect = target.getBoundingClientRect(); if (y > rect.bottom - 45) target.scrollTop += 10; else if (y < rect.top + 45) target.scrollTop -= 10; }
    };
    listen(host, 'dragstart', event => {
        if (pointer) { event.preventDefault(); return; }
        const element = event.target.closest('[data-task-card]');
        if (!start(element)) { event.preventDefault(); return; }
        event.dataTransfer.effectAllowed = 'move'; event.dataTransfer.setData('text/plain', drag.id);
    });
    listen(host, 'dragover', event => {
        if (!drag || locked()) return;
        event.preventDefault(); event.dataTransfer.dropEffect = 'move';
        drag.drop = resolveDrop(host, event.target, event.clientY, drag.id); mark(drag.drop); scrollAt(event.clientX, event.clientY);
    });
    listen(host, 'drop', event => {
        if (!drag) return; event.preventDefault(); drag.drop = resolveDrop(host, event.target, event.clientY, drag.id); void finish();
    });
    listen(host, 'dragend', cleanup);
    listen(host, 'pointerdown', event => {
        if (event.button !== 0 || pointer || locked()) return;
        const handle = event.target.closest('[data-task-grip]'); if (!handle) return;
        const element = handle.closest('[data-task-card]');
        pointer = { id: event.pointerId, handle, element, startX: event.clientX, startY: event.clientY, x: event.clientX, y: event.clientY };
        event.preventDefault();
        handle.setPointerCapture(event.pointerId);
    });
    const tick = () => {
        if (!pointer || !drag) return;
        ghost.style.left = `${pointer.x - 30}px`; ghost.style.top = `${pointer.y - 24}px`;
        scrollAt(pointer.x, pointer.y);
        drag.drop = resolveDrop(host, document.elementFromPoint(pointer.x, pointer.y), pointer.y, drag.id); mark(drag.drop);
        frame = requestAnimationFrame(tick);
    };
    listen(host, 'pointermove', event => {
        if (!pointer || pointer.id !== event.pointerId) return;
        pointer.x = event.clientX; pointer.y = event.clientY;
        if (!drag && Math.hypot(pointer.x - pointer.startX, pointer.y - pointer.startY) < 8) return;
        event.preventDefault();
        if (!drag) {
            if (!start(pointer.element)) { cleanup(); return; }
            ghost = pointer.element.cloneNode(true); ghost.classList.remove('task-is-dragging'); ghost.classList.add('task-touch-ghost'); ghost.setAttribute('aria-hidden', 'true'); ghost.removeAttribute('data-task-card'); ghost.style.width = `${pointer.element.getBoundingClientRect().width}px`;
            ghost.querySelectorAll('button,input,select').forEach(el => { el.disabled = true; el.tabIndex = -1; });
            document.body.appendChild(ghost); frame = requestAnimationFrame(tick);
        }
    }, { passive: false });
    listen(host, 'pointerup', event => { if (pointer?.id === event.pointerId) { if (drag) void finish(); else cleanup(); } });
    listen(host, 'pointercancel', cleanup);
    listen(host, 'keydown', event => { if (event.key === 'Escape' && drag) cleanup(); });
    listen(host, 'click', event => { if (event.detail > 0 && performance.now() < suppressedUntil) { event.preventDefault(); event.stopPropagation(); } }, true);
    listen(window, 'blur', cleanup);
    return { dispose() { disposed = true; cleanup(); listeners.forEach(remove => remove()); } };
}
