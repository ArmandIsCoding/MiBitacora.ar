import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';

const source = await readFile(new URL('../wwwroot/brisa-motion.js', import.meta.url), 'utf8');
function page({ intersection = true, reduced = false, savedMode = null } = {}) {
    class Element {
        nodeType = 1;
        constructor(live = false) { this.dataset = live ? { brisaAnimate: 'true', brisaActivity: 'idle' } : {}; this.children = []; }
        matches(selector) {
            return selector === '[data-brisa-motion-control]' ? 'brisaMotionControl' in this.dataset :
                selector === '[data-brisa-motion-note]' ? 'brisaMotionNote' in this.dataset : this.dataset.brisaAnimate === 'true';
        }
        querySelectorAll(selector) { return this.children.flatMap(child => [ ...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector) ]); }
    }
    const portrait = new Element(true), root = new Element(), control = new Element(), note = new Element();
    control.dataset.brisaMotionControl = ''; note.dataset.brisaMotionNote = '';
    root.children = [portrait, control, note];
    const storage = new Map(savedMode ? [['mibitacora.brisa.motion', savedMode]] : []);
    const listeners = new Map(), observed = [], unobserved = [];
    const document = { documentElement: root, hidden: false, addEventListener: (name, callback) => listeners.set(name, callback) };
    let intersect, mutate;
    runInNewContext(source, {
        document,
        localStorage: { getItem: key => storage.get(key), setItem: (key, value) => storage.set(key, value) },
        matchMedia: () => ({ matches: reduced, addEventListener() {} }),
        IntersectionObserver: intersection ? class {
            constructor(callback) { intersect = callback; }
            observe(element) { observed.push(element); }
            unobserve(element) { unobserved.push(element); }
        } : undefined,
        MutationObserver: class { constructor(callback) { mutate = callback; } observe() {} }
    });
    return { portrait, root, document, listeners, observed, unobserved, intersect, control, note, storage,
        mutate, Element };
}

test('Brisa pausa el movimiento fuera de pantalla y cuando la pestaña no está visible', () => {
    const p = page();
    assert.equal(p.portrait.dataset.brisaVisible, undefined);
    p.intersect([{ target: p.portrait, isIntersecting: true }]);
    assert.equal(p.portrait.dataset.brisaVisible, 'true');
    p.intersect([{ target: p.portrait, isIntersecting: false }]);
    assert.equal(p.portrait.dataset.brisaVisible, 'false');
    p.document.hidden = true; p.listeners.get('visibilitychange')();
    assert.equal(p.root.dataset.brisaPageHidden, 'true');
});
test('Cambiar de expresión conserva un único observador y reinicia la transición anterior', () => {
    const p = page(); p.portrait.dataset.brisaSettled = 'true';
    p.portrait.dataset.brisaActivity = 'thinking';
    p.mutate([{ type: 'attributes', target: p.portrait }]);
    assert.equal(p.portrait.dataset.brisaSettled, undefined);
    assert.equal(p.observed.length, 1);
    delete p.portrait.dataset.brisaAnimate;
    p.mutate([{ type: 'attributes', target: p.portrait }]);
    assert.equal(p.unobserved.length, 1);
});
test('Los retratos retirados por Blazor liberan el observador y se pueden volver a montar', () => {
    const p = page();
    p.mutate([{ type: 'childList', removedNodes: [p.portrait], addedNodes: [] }]);
    assert.equal(p.unobserved.length, 1);
    p.mutate([{ type: 'childList', removedNodes: [], addedNodes: [p.portrait] }]);
    assert.equal(p.observed.length, 2);
});
test('La sonrisa al responder vuelve a la calma y no pisa una nueva lectura', () => {
    const p = page(), finish = p.listeners.get('animationend');
    p.portrait.dataset.brisaActivity = 'responding';
    const event = { animationName: 'brisa-respond-face', target: { closest: () => p.portrait } };
    finish(event); assert.equal(p.portrait.dataset.brisaSettled, 'true');
    p.portrait.dataset.brisaActivity = 'reading';
    p.mutate([{ type: 'attributes', target: p.portrait }]); finish(event);
    assert.equal(p.portrait.dataset.brisaSettled, undefined);
});
test('La presencia sigue funcionando cuando el navegador no tiene IntersectionObserver', () => {
    const p = page({ intersection: false });
    assert.equal(p.portrait.dataset.brisaVisible, 'true');
});
test('Los gestos respetan el dispositivo hasta que la persona elige otra preferencia', () => {
    const p = page({ reduced: true });
    assert.equal(p.root.dataset.brisaMotion, 'system');
    assert.match(p.note.textContent, /sin animarse/);
    p.control.value = 'on'; p.listeners.get('change')({ target: p.control });
    assert.equal(p.root.dataset.brisaMotion, 'on');
    assert.equal(p.storage.get('mibitacora.brisa.motion'), 'on');
    p.control.value = 'off'; p.listeners.get('change')({ target: p.control });
    assert.equal(p.root.dataset.brisaMotion, 'off');
    assert.match(p.note.textContent, /conserva sus expresiones/);
});
test('La elección guardada se recupera y un valor desconocido vuelve a seguir el dispositivo', () => {
    assert.equal(page({ savedMode: 'off' }).control.value, 'off');
    assert.equal(page({ savedMode: 'invalid' }).root.dataset.brisaMotion, 'system');
});
