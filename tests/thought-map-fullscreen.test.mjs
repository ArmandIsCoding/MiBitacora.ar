import test from 'node:test';
import assert from 'node:assert/strict';
import { createFullscreen } from '../wwwroot/thought-map-fullscreen.mjs';

class Element extends EventTarget {
    constructor(doc) { super(); this.doc = doc; this.children = []; this.attrs = new Map(); this.style = {}; this.inert = false; this.isConnected = true; this.disabled = false; const names = new Set(); this.classList = { add: n => names.add(n), remove: n => names.delete(n), contains: n => names.has(n) }; }
    append(child) { this.children.push(child); child.parentElement = this; return child; }
    getAttribute(name) { return this.attrs.get(name) ?? null; }
    setAttribute(name, value) { this.attrs.set(name, value); }
    removeAttribute(name) { this.attrs.delete(name); }
    focus() { this.doc.activeElement = this; }
    getClientRects() { return [{}]; }
    contains(element) { return this === element || this.children.some(child => child.contains(element)); }
}
function fixture(native = false) {
    const doc = new EventTarget(); doc.documentElement = { style: { overflow: 'clip' } }; doc.body = new Element(doc);
    doc.defaultView = { scrollX: 0, scrollY: 312, scrollTo: value => { doc.defaultView.scrollX = value.left; doc.defaultView.scrollY = value.top; } };
    const background = doc.body.append(new Element(doc)), alreadyInert = doc.body.append(new Element(doc)); alreadyInert.inert = true;
    const parent = doc.body.append(new Element(doc)), sibling = parent.append(new Element(doc)), host = parent.append(new Element(doc));
    const search = host.append(new Element(doc)), button = host.append(new Element(doc)), last = host.append(new Element(doc));
    const labels = new Map(['[data-fullscreen-open]', '[data-fullscreen-close]', '[data-fullscreen-label]'].map(key => [key, {}]));
    host.querySelector = selector => labels.get(selector);
    host.querySelectorAll = () => [search, button, last];
    background.focus(); doc.fullscreenEnabled = native;
    if (native) {
        host.requestFullscreen = async () => { doc.fullscreenElement = host; doc.dispatchEvent(new Event('fullscreenchange')); };
        doc.exitFullscreen = async () => { doc.fullscreenElement = null; doc.dispatchEvent(new Event('fullscreenchange')); };
    }
    const changes = [], control = createFullscreen(host, button, value => changes.push(value), doc);
    return { doc, host, button, search, last, background, alreadyInert, sibling, changes, control };
}
const flush = () => new Promise(resolve => setImmediate(resolve));
function key(target, value, shiftKey = false) {
    const event = new Event('keydown', { cancelable: true }); Object.defineProperties(event, { key: { value }, shiftKey: { value: shiftKey } }); target.dispatchEvent(event); return event;
}

for (const native of [false, true]) test(`${native ? 'native fullscreen' : 'viewport fallback'} restores focus, background and page scrolling`, async () => {
    const f = fixture(native);
    f.button.dispatchEvent(new Event('click')); await flush();
    assert.equal(f.host.classList.contains('is-expanded'), true);
    assert.equal(f.host.getAttribute('aria-modal'), 'true'); assert.equal(f.button.getAttribute('aria-pressed'), 'true');
    assert.equal(f.doc.documentElement.style.overflow, 'hidden'); assert.equal(f.background.inert, true); assert.equal(f.sibling.inert, true);
    assert.equal(f.doc.activeElement, f.button);
    f.doc.defaultView.scrollY = 0;
    f.last.focus(); assert.equal(key(f.host, 'Tab').defaultPrevented, true); assert.equal(f.doc.activeElement, f.search);
    assert.equal(key(f.host, 'Tab', true).defaultPrevented, true); assert.equal(f.doc.activeElement, f.last);
    key(f.host, 'Escape'); await flush();
    assert.equal(f.host.classList.contains('is-expanded'), false); assert.equal(f.host.getAttribute('aria-modal'), null);
    assert.equal(f.background.inert, false); assert.equal(f.sibling.inert, false); assert.equal(f.alreadyInert.inert, true);
    assert.equal(f.doc.documentElement.style.overflow, 'clip'); assert.equal(f.doc.activeElement, f.background);
    assert.equal(f.doc.defaultView.scrollY, 312);
    assert.deepEqual(f.changes, [true, false]);
    f.control.dispose();
});

test('denied fullscreen still opens the map and cleanup removes the overlay and listeners', async () => {
    const f = fixture(true); f.host.requestFullscreen = async () => { throw new Error('Denied'); };
    f.button.dispatchEvent(new Event('click')); await flush();
    assert.equal(f.host.classList.contains('is-expanded'), true);
    f.control.dispose(); f.control.dispose();
    assert.equal(f.doc.documentElement.style.overflow, 'clip'); assert.equal(f.background.inert, false);
    f.button.dispatchEvent(new Event('click')); await flush();
    assert.equal(f.host.classList.contains('is-expanded'), false); assert.deepEqual(f.changes, [true, false]);
});

test('the browser exiting native fullscreen also restores the map', async () => {
    const f = fixture(true); f.button.dispatchEvent(new Event('click')); await flush();
    await f.doc.exitFullscreen();
    assert.equal(f.host.classList.contains('is-expanded'), false); assert.equal(f.background.inert, false);
    assert.equal(f.doc.documentElement.style.overflow, 'clip'); f.control.dispose();
});

test('navigating away while fullscreen permission is pending does not leave the page locked', async () => {
    const f = fixture(true); let resolve;
    f.host.requestFullscreen = async () => { await new Promise(done => { resolve = done; }); f.doc.fullscreenElement = f.host; f.doc.dispatchEvent(new Event('fullscreenchange')); };
    f.button.dispatchEvent(new Event('click')); f.control.dispose(); resolve(); await flush();
    assert.equal(f.doc.fullscreenElement, null); assert.equal(f.doc.documentElement.style.overflow, 'clip'); assert.equal(f.background.inert, false);
    assert.deepEqual(f.changes, []);
});
