import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../wwwroot/voice-recorder.js', import.meta.url), 'utf8');
const { create, encodeWav } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

test('Audio local: WAV PCM mono, frecuencia y mezcla estéreo correctas', async () => {
    const wav = encodeWav({ length: 3, sampleRate: 16000, numberOfChannels: 2,
        getChannelData: channel => channel === 0 ? new Float32Array([-1, .5, 2]) : new Float32Array([-1, -.5, 2]) }, 100);
    const bytes = await wav.arrayBuffer(); const view = new DataView(bytes);
    assert.equal(wav.type, 'audio/wav'); assert.equal(bytes.byteLength, 50);
    assert.equal(new TextDecoder().decode(bytes.slice(0, 4)), 'RIFF');
    assert.equal(view.getUint16(22, true), 1); assert.equal(view.getUint32(24, true), 16000);
    assert.equal(view.getInt16(44, true), -32768); assert.equal(view.getInt16(46, true), 0); assert.equal(view.getInt16(48, true), 32767);
    assert.throws(() => encodeWav({ length: 1000 }, 100), /supera/);
});

function environment(format = 'audio/webm', preferWav = false) {
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
    const recorder = create(root, { async invokeMethodAsync(method, value) { messages.push({ method, value }); } }, 100_000, preferWav);
    return { recorder, messages, root, stream, stops: () => stops };
}

test('Plan gratuito: la grabación se convierte a WAV mono 16 kHz antes de adjuntarse', async () => {
    const e = environment('audio/webm', true); let closed = false;
    window.AudioContext = class { async decodeAudioData() { return { duration: .01 }; } async close() { closed = true; } };
    window.OfflineAudioContext = class {
        constructor(channels, length, rate) { this.channels = channels; this.length = length; this.rate = rate; }
        createBufferSource() { return { connect() {}, start() {} }; }
        async startRendering() { return { length: this.length, sampleRate: this.rate, numberOfChannels: this.channels,
            getChannelData: () => new Float32Array(this.length) }; }
    };
    await e.recorder.start(); e.recorder.stop();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(e.messages.length, 1); assert.equal(e.messages[0].method, 'RecordingReady');
    assert.match(e.messages[0].value.fileName, /\.wav$/); assert.equal(e.stops(), 1); assert.equal(closed, true);
    const wav = e.recorder.read(); const view = new DataView(await wav.arrayBuffer());
    assert.equal(wav.type, 'audio/wav'); assert.equal(view.getUint32(24, true), 16000); assert.equal(view.getUint16(22, true), 1);
    e.recorder.dispose();
});

test('Cerrar durante la conversión local no publica un audio ni retiene el micrófono', async () => {
    const e = environment('audio/webm', true); let complete;
    window.AudioContext = class { decodeAudioData() { return new Promise(resolve => { complete = resolve; }); } async close() {} };
    window.OfflineAudioContext = class {
        createBufferSource() { return { connect() {}, start() {} }; }
        async startRendering() { return { length: 160, sampleRate: 16000, numberOfChannels: 1, getChannelData: () => new Float32Array(160) }; }
    };
    await e.recorder.start(); e.recorder.stop();
    await new Promise(resolve => setImmediate(resolve));
    e.recorder.dispose(); complete({ duration: .01 });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(e.messages.length, 0); assert.equal(e.stops(), 1); assert.throws(() => e.recorder.read());
});

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
