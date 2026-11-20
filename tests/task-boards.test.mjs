import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const { create, resolveDrop } = await import('data:text/javascript;base64,' + Buffer.from(await readFile(new URL('../wwwroot/task-boards.js', import.meta.url))).toString('base64'));

class Element {
    constructor(dataset = {}, rect = { top: 0, bottom: 100, left: 0, right: 900, width: 280, height: 100 }) { this.dataset = dataset; this.rect = rect; this.children = []; this.events = new Map(); this.style = {}; this.draggable = true; this.classes = new Set(); this.scrollLeft = 0; this.scrollTop = 0; this.classList = { add: (...v) => v.forEach(x => this.classes.add(x)), remove: (...v) => v.forEach(x => this.classes.delete(x)), contains: v => this.classes.has(v) }; }
    appendChild(child) { child.parentElement = this; this.children.push(child); }
    remove() { if (this.parentElement) this.parentElement.children = this.parentElement.children.filter(c => c !== this); this.parentElement = null; }
    get nextElementSibling() { return this.parentElement?.children[this.parentElement.children.indexOf(this) + 1] ?? null; }
    contains(node) { return node === this || this.children.some(c => c.contains(node)); }
    matches(selector) { return selector.split(',').some(s => s.startsWith('.') ? this.classes.has(s.slice(1)) : ({ '[data-task-card]': 'taskCard', '[data-task-column]': 'taskColumn', '[data-task-list]': 'taskList', '[data-task-grip]': 'taskGrip', '[data-task-columns]': 'taskColumns' }[s]) in this.dataset); }
    closest(selector) { return this.matches(selector) ? this : this.parentElement?.closest(selector) ?? null; }
    querySelectorAll(selector) { return this.children.flatMap(c => [...(c.matches(selector) ? [c] : []), ...c.querySelectorAll(selector)]); }
    querySelector(selector) { if (selector === '.tasks-search input') return this.search ?? null; return this.querySelectorAll(selector)[0] ?? null; }
    getBoundingClientRect() { return this.rect; }
    addEventListener(type, handler) { if (!this.events.has(type)) this.events.set(type, new Set()); this.events.get(type).add(handler); }
    removeEventListener(type, handler) { this.events.get(type)?.delete(handler); }
    dispatch(type, data = {}) { const e = { target: this, preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; }, ...data }; for (const handler of this.events.get(type) ?? []) handler(e); return e; }
    cloneNode() { return new Element({ ...this.dataset }, this.rect); }
    setAttribute(name, value) { this[name] = value; } removeAttribute(name) { if (name === 'data-task-card') delete this.dataset.taskCard; else delete this[name]; }
    setPointerCapture(id) { this.capture = id; } hasPointerCapture(id) { return this.capture === id; } releasePointerCapture() { this.capture = null; }
}
function fixture() {
    const host = new Element({ readonly: 'false', revision: '17' }), columns = new Element({ taskColumns: '' }); host.search = { value: '' }; host.appendChild(columns);
    const left = new Element({ taskColumn: 'left' }), right = new Element({ taskColumn: 'right' }); columns.appendChild(left); columns.appendChild(right);
    const listA = new Element({ taskList: 'left' }), listB = new Element({ taskList: 'right' }); left.appendChild(listA); right.appendChild(listB);
    const a = new Element({ taskCard: 'a', taskColumnId: 'left' }), b = new Element({ taskCard: 'b', taskColumnId: 'right' }); listA.appendChild(a); listB.appendChild(b);
    const grip = new Element({ taskGrip: '' }); a.appendChild(grip);
    const body = new Element(), window = new Element(), calls = [], frames = new Map(); let sequence = 0;
    const original = { document: globalThis.document, window: globalThis.window, raf: globalThis.requestAnimationFrame, caf: globalThis.cancelAnimationFrame };
    globalThis.document = { body, elementFromPoint: () => b }; globalThis.window = window;
    globalThis.requestAnimationFrame = cb => { const id = ++sequence; frames.set(id, cb); return id; }; globalThis.cancelAnimationFrame = id => frames.delete(id);
    const bridge = { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } };
    const control = create(host, bridge);
    return { host, left, right, listA, listB, a, b, grip, calls, body, window, frames, control,
        frame() { const [id, callback] = frames.entries().next().value; frames.delete(id); callback(); },
        cleanup() { control.dispose(); globalThis.document = original.document; globalThis.window = original.window; globalThis.requestAnimationFrame = original.raf; globalThis.cancelAnimationFrame = original.caf; } };
}
const transfer = () => ({ effectAllowed: '', dropEffect: '', setData(type, value) { this[type] = value; } });

test('el punto de inserción distingue antes, después y columnas vacías sin usar contenido de otras superficies', () => {
    const f = fixture();
    try { assert.deepEqual(resolveDrop(f.host, f.b, 10, 'a'), { column: 'right', before: 'b' }); assert.deepEqual(resolveDrop(f.host, f.b, 90, 'a'), { column: 'right', before: null }); assert.deepEqual(resolveDrop(f.host, f.listA, 50, 'b'), { column: 'left', before: null }); assert.equal(resolveDrop(f.host, f.a, 10, 'a'), null); assert.equal(resolveDrop(f.host, new Element(), 10, 'a'), null); }
    finally { f.cleanup(); }
});
test('el arrastre guarda tarjeta, destino y revisión originales, y limpia las marcas al soltar', async () => {
    const f = fixture();
    try { const dataTransfer = transfer(); f.host.dispatch('dragstart', { target: f.a, dataTransfer }); assert.equal(dataTransfer['text/plain'], 'a'); f.host.dataset.revision = '18'; f.host.dispatch('dragover', { target: f.b, dataTransfer, clientX: 150, clientY: 10 }); assert(f.b.classes.has('task-drop-before')); f.host.dispatch('drop', { target: f.b, clientY: 10 }); await Promise.resolve(); assert.deepEqual(f.calls, [['MoveTaskCard', 'a', 'right', 'b', 17]]); assert(!f.a.classes.has('task-is-dragging')); assert(!f.b.classes.has('task-drop-before')); }
    finally { f.cleanup(); }
});
test('un tablero bloqueado o una búsqueda activa no permite arrastrar tarjetas ocultas', () => {
    const f = fixture();
    try { f.host.dataset.readonly = 'true'; assert(f.host.dispatch('dragstart', { target: f.a, dataTransfer: transfer() }).prevented); f.host.dataset.readonly = 'false'; f.host.search.value = 'filtro'; assert(f.host.dispatch('dragstart', { target: f.a, dataTransfer: transfer() }).prevented); assert.equal(f.calls.length, 0); }
    finally { f.cleanup(); }
});
test('cancelar o salir de la ventana retira el arrastre sin guardar un movimiento', () => {
    const f = fixture();
    try { f.host.dispatch('dragstart', { target: f.a, dataTransfer: transfer() }); f.host.dispatch('keydown', { key: 'Escape' }); assert(!f.a.classes.has('task-is-dragging')); f.host.dispatch('dragstart', { target: f.a, dataTransfer: transfer() }); f.window.dispatch('blur'); assert(!f.a.classes.has('task-is-dragging')); assert.equal(f.calls.length, 0); }
    finally { f.cleanup(); }
});
test('un toque breve en el asa no mueve la tarjeta ni deja una captura activa', () => {
    const f = fixture();
    try { f.host.dispatch('pointerdown', { target: f.grip, pointerType: 'touch', button: 0, pointerId: 1, clientX: 100, clientY: 50 }); f.host.dispatch('pointermove', { pointerId: 1, clientX: 102, clientY: 52 }); assert.equal(f.body.children.length, 0); f.host.dispatch('pointerup', { pointerId: 1 }); assert.equal(f.grip.capture, null); assert.equal(f.calls.length, 0); }
    finally { f.cleanup(); }
});
test('el arrastre táctil usa un umbral, desplaza un fantasma y guarda el mismo destino que en escritorio', async () => {
    const f = fixture();
    try { f.host.dispatch('pointerdown', { target: f.grip, pointerType: 'touch', button: 0, pointerId: 7, clientX: 100, clientY: 50 }); f.host.dispatch('pointermove', { pointerId: 7, clientX: 150, clientY: 10 }); assert.equal(f.body.children.length, 1); assert.equal(f.body.children[0]['aria-hidden'], 'true'); assert.equal(f.body.children[0].dataset.taskCard, undefined); f.frame(); assert.equal(f.body.children[0].style.left, '120px'); f.host.dispatch('pointerup', { pointerId: 7 }); await Promise.resolve(); assert.deepEqual(f.calls, [['MoveTaskCard', 'a', 'right', 'b', 17]]); assert.equal(f.body.children.length, 0); assert.equal(f.frames.size, 0); assert.equal(f.grip.capture, null); }
    finally { f.cleanup(); }
});
test('la cancelación táctil y dispose liberan fantasmas, animación, capturas y todos los observadores', () => {
    const f = fixture();
    try { f.host.dispatch('pointerdown', { target: f.grip, pointerType: 'touch', button: 0, pointerId: 4, clientX: 100, clientY: 50 }); f.host.dispatch('pointermove', { pointerId: 4, clientX: 150, clientY: 10 }); f.host.dispatch('pointercancel'); assert.equal(f.body.children.length, 0); assert.equal(f.frames.size, 0); f.control.dispose(); assert([...f.host.events.values(), ...f.window.events.values()].every(v => v.size === 0)); assert.equal(f.calls.length, 0); }
    finally { f.cleanup(); }
});
test('el asa también mueve con mouse sin depender del arrastre nativo del navegador', async () => {
    const f = fixture();
    try { f.host.dispatch('pointerdown', { target: f.grip, pointerType: 'mouse', button: 0, pointerId: 8, clientX: 100, clientY: 50 }); assert(f.host.dispatch('dragstart', { target: f.a, dataTransfer: transfer() }).prevented); f.host.dispatch('pointermove', { pointerId: 8, clientX: 150, clientY: 10 }); f.frame(); f.host.dispatch('pointerup', { pointerId: 8 }); await Promise.resolve(); assert.deepEqual(f.calls, [['MoveTaskCard', 'a', 'right', 'b', 17]]); assert.equal(f.body.children.length, 0); }
    finally { f.cleanup(); }
});
test('un cambio a sólo lectura durante el arrastre impide enviar el movimiento', () => {
    const f = fixture();
    try { const dataTransfer = transfer(); f.host.dispatch('dragstart', { target: f.a, dataTransfer }); f.host.dispatch('dragover', { target: f.b, dataTransfer, clientX: 150, clientY: 10 }); f.host.dataset.readonly = 'true'; f.host.dispatch('drop', { target: f.b, clientY: 10 }); assert.equal(f.calls.length, 0); assert(!f.a.classes.has('task-is-dragging')); }
    finally { f.cleanup(); }
});
