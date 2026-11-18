import test from 'node:test';
import assert from 'node:assert/strict';
import { buildGraph, layout, visibleNodes, neighborhood, bounds, curve, safeUrl } from '../wwwroot/thought-map-core.mjs';

const data = () => ({ nodes: [
    { id: 'a', kind: 'entry', label: 'Volví a dibujar', detail: 'Una mañana tranquila', notebook: 'Mi diario' },
    { id: 'b', kind: 'theme', label: 'Creatividad', detail: 'Propuesta de Brisa' },
    { id: 'c', kind: 'question', label: '¿Qué querés crear?', detail: '' },
    { id: 'd', kind: 'entry', label: 'Quiero tocar la guitarra', detail: '' },
    { id: 'e', kind: 'note', label: 'Otra historia', detail: '' }
], links: [
    { source: 'a', target: 'b', kind: 'theme' },
    { source: 'a', target: 'c', kind: 'question' },
    { source: 'c', target: 'd', kind: 'reply' }
] });

test('only existing, distinct relationships can enter the graph', () => {
    const input = data();
    input.links.push({ source: 'a', target: 'b', kind: 'theme' }, { source: 'a', target: 'missing' }, { source: 'e', target: 'e' });
    const graph = buildGraph(input);
    assert.equal(graph.links.length, 3);
    assert.equal(graph.nodes.get('a').degree, 2);
    assert.equal(graph.nodes.get('e').degree, 0);
    assert.deepEqual([...neighborhood(graph, 'a', 2)].sort(), ['a', 'b', 'c', 'd']);
});

test('search respects accents, originals and notebooks and keeps useful neighbors', () => {
    const graph = buildGraph(data());
    assert.deepEqual([...visibleNodes(graph, ' VOLVI ')].sort(), ['a', 'b', 'c']);
    assert.deepEqual([...visibleNodes(graph, 'tranquila')].sort(), ['a', 'b', 'c']);
    assert.deepEqual([...visibleNodes(graph, 'mi diario')].sort(), ['a', 'b', 'c']);
    assert.equal(visibleNodes(graph, 'inexistente').size, 0);
    assert.equal(visibleNodes(graph, '', 'a').has('e'), false);
    assert.equal(visibleNodes(graph, 'Otra', 'a').size, 0);
});

test('geometry is deterministic, finite and includes isolated entries', () => {
    const one = layout(buildGraph(data())), two = layout(buildGraph(data()));
    for (const n of one.nodes.values()) {
        assert.ok(Number.isFinite(n.x) && Number.isFinite(n.y));
        assert.equal(n.x, two.nodes.get(n.id).x); assert.equal(n.y, two.nodes.get(n.id).y);
    }
    const box = bounds(one.nodes.values());
    for (const n of one.nodes.values()) assert.ok(n.x >= box.x && n.x <= box.x + box.width && n.y >= box.y && n.y <= box.y + box.height);
    assert.equal(/NaN|Infinity/.test(curve(one.nodes.get('a'), one.nodes.get('b'))), false);
    assert.equal(layout(buildGraph({ nodes: [], links: [] })).nodes.size, 0);
    const isolated = layout(buildGraph({ nodes: [{ id: 'x', kind: 'entry', label: 'Primera entrada' }] }));
    assert.ok(bounds(isolated.nodes.values()).width > 0);
});

test('large maps remain bounded and do not drop connections or nodes', () => {
    const input = { nodes: [], links: [] };
    for (let i = 0; i < 1000; i++) {
        input.nodes.push({ id: `n${i}`, kind: i < 30 ? 'theme' : 'entry', label: `Idea ${i}` });
        if (i >= 30) input.links.push({ source: `n${i}`, target: `n${i % 30}`, kind: 'theme' });
    }
    const graph = layout(buildGraph(input));
    assert.equal(graph.nodes.size, 1000); assert.equal(graph.links.length, 970);
    for (const n of graph.nodes.values()) assert.ok(Number.isFinite(n.x) && Number.isFinite(n.y));
});

test('detail links reject script, external and protocol-relative URLs', () => {
    for (const value of ['javascript:alert(1)', '//example.com', 'https://example.com', '/\\example.com', '/Account/Manage\n', '/bitacorax', '/entrada/bad<script>']) assert.equal(safeUrl(value), null);
    for (const value of ['/entrada/123abc-456?origen=mapa', '/bitacora?seccion=notas', '/Account/Manage']) assert.equal(safeUrl(value), value);
});
