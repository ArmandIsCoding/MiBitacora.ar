import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../wwwroot/journal.js', import.meta.url), 'utf8');
const { centerReply } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

function page({ height = 200, reduced = false, modal = null, parent = null } = {}) {
    const calls = [];
    const card = { style: {}, parentElement: parent, getBoundingClientRect: () => ({ height }), scrollIntoView: options => calls.push(options) };
    const host = { isConnected: true, querySelector: () => card };
    globalThis.window = { innerHeight: 800, matchMedia: () => ({ matches: reduced }) };
    globalThis.document = { getElementById: id => id === 'reply-requested' ? host : null,
        querySelector: selector => selector === 'dialog[open]' ? modal : selector === '.journal-top'
            ? { getBoundingClientRect: () => ({ height: 70 }) } : { getBoundingClientRect: () => ({ height: 160 }) } };
    globalThis.getComputedStyle = element => ({ overflowY: element.overflowY ?? 'visible', scrollPaddingTop: element.scrollPaddingTop, scrollPaddingBottom: element.scrollPaddingBottom });
    return { calls, host, card };
}
test('La devolución terminada vuelve a su propia tarjeta, con espacio para el encabezado y el editor', () => {
    const p = page(); centerReply('reply-requested');
    assert.deepEqual(p.calls, [{ block: 'center', behavior: 'smooth' }]);
    assert.equal(p.card.style.scrollMarginTop, '86px');
    assert.equal(p.card.style.scrollMarginBottom, '176px');
});
test('Una devolución larga se abre por el comienzo y respeta la reducción de movimiento', () => {
    const p = page({ height: 900, reduced: true }); centerReply(p.host);
    assert.deepEqual(p.calls, [{ block: 'start', behavior: 'auto' }]);
});
test('El regreso no duplica el espacio que la página ya reserva para su encabezado', () => {
    const p = page(); document.documentElement = { scrollPaddingTop: '80px' };
    centerReply(p.host); assert.equal(p.card.style.scrollMarginTop, '6px');
});
test('La vuelta considera el área de lectura de un contenedor desplazable', () => {
    const p = page({ height: 400, parent: { clientHeight: 320, scrollHeight: 900, overflowY: 'auto', parentElement: null } });
    centerReply(p.host); assert.equal(p.calls[0].block, 'start');
});
test('La respuesta no desplaza la página detrás de un editor modal ni busca una tarjeta eliminada', () => {
    const p = page({ modal: { contains: () => false } }); centerReply(p.host); centerReply('missing');
    p.host.isConnected = false; centerReply(p.host); assert.equal(p.calls.length, 0);
});
