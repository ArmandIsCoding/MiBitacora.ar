import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../wwwroot/voice-recorder.js', import.meta.url), 'utf8');
const { create } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

function environment(format = 'audio/webm') {
    let stops = 0;
    const stream = { getTracks: () => [{ stop() { stops++; } }] };
    const messages = [];
    const root = { isConnected: true, querySelector: () => ({ textContent: '' }), querySelectorAll: () => [] };
    globalThis.window = new EventTarget();
    globalThis.document = { body: {} };
    globalThis.isSecureContext = true;
    Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { mediaDevices: { getUserMedia: async () => stream } } });
    globalThis.MutationObserver = class { constructor(callback) { this.callback = callback; environment.observer = this; } observe() {} disconnect() {} };
    globalThis.DotNet = { createJSStreamReference: blob => blob };
    globalThis.MediaRecorder = class extends EventTarget {
        static isTypeSupported(type) { return type.startsWith(format); }
        constructor(media, options) { super(); this.mimeType = options.mimeType; this.state = 'inactive'; environment.recorder = this; }
        start() { this.state = 'recording'; }
        data(size = 100) { const event = new Event('dataavailable'); event.data = new Blob([new Uint8Array(size)], { type: this.mimeType }); this.dispatchEvent(event); }
        stop() { this.state = 'inactive'; this.data(); this.dispatchEvent(new Event('stop')); }
    };
    const recorder = create(root, { async invokeMethodAsync(method, value) { messages.push({ method, value }); } }, 100_000);
    return { recorder, messages, root, stream, stops: () => stops };
}

test('Grabación compatible con Safari: MP4/M4A, vista local y liberación del micrófono', async () => {
    const e = environment('audio/mp4');
    assert.equal(await e.recorder.start(), null);
    e.recorder.stop();
    assert.equal(e.stops(), 1);
    assert.equal(e.messages[0].method, 'RecordingReady');
    assert.match(e.messages[0].value.fileName, /\.m4a$/);
    assert.match(e.messages[0].value.url, /^blob:/);
    assert.equal(e.recorder.read().size, 100);
    e.recorder.dispose();
    assert.throws(() => e.recorder.read());
});
test('Grabación WebM, descarte y cierre por navegación liberan el dispositivo', async () => {
    const e = environment();
    await e.recorder.start(); e.recorder.stop();
    assert.match(e.messages[0].value.fileName, /\.weba$/);
    e.recorder.reset(); assert.throws(() => e.recorder.read());
    await e.recorder.start(); e.root.isConnected = false; environment.observer.callback();
    assert.equal(e.stops(), 2); assert.equal(e.messages.length, 1);
});
test('Denegación de permiso ofrece subir un archivo y no conserva un micrófono abierto', async () => {
    const e = environment(); navigator.mediaDevices.getUserMedia = async () => { throw { name: 'NotAllowedError' }; };
    assert.match(await e.recorder.start(), /permiso/); assert.equal(e.stops(), 0); e.recorder.dispose();
});
test('Cerrar mientras se espera permiso detiene la captura si el permiso llega después', async () => {
    const e = environment(); let grant;
    navigator.mediaDevices.getUserMedia = () => new Promise(resolve => { grant = resolve; });
    const starting = e.recorder.start(); e.recorder.dispose(); grant(e.stream);
    assert.match(await starting, /cerró/); assert.equal(e.stops(), 1); assert.equal(e.messages.length, 0);
});
test('Sin HTTPS o soporte de grabación se ofrece la carga de archivos', async () => {
    const e = environment(); globalThis.isSecureContext = false;
    assert.match(await e.recorder.start(), /HTTPS/); assert.equal(e.stops(), 0); e.recorder.dispose();
});
test('Una grabación que supera el límite se detiene, se descarta y no se adjunta', async () => {
    const e = environment(); await e.recorder.start(); environment.recorder.data(120_000);
    assert.equal(e.stops(), 1); assert.equal(e.messages[0].method, 'RecordingFailed');
    assert.throws(() => e.recorder.read()); e.recorder.dispose();
});
