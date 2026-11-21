import test from 'node:test';
import assert from 'node:assert/strict';
import { buildGraph, layout, visibleNodes, neighborhood, bounds, fitCamera, nodeRadius, labelPlacements, curve, safeUrl } from '../wwwroot/thought-map-core.mjs';
import { denseThoughtMap } from './Fixtures/thought-map-data.mjs';

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

const labelMetrics = graph => new Map([...graph.nodes.values()].map(n => [n.id, { width: n.kind === 'theme' ? 170 : 152, height: n.kind === 'theme' ? 42 : 29 }]));
function checkLabels(labels, width, height) {
    const boxes = [...labels.values()];
    for (const box of boxes) assert.ok(box.x >= 12 && box.y >= 36 && box.x + box.width <= width - 12 && box.y + box.height <= height - 32);
    for (let i = 0; i < boxes.length; i++) for (let j = i + 1; j < boxes.length; j++) {
        const a = boxes[i], b = boxes[j];
        assert.equal(a.x < b.x + b.width && a.x + a.width > b.x && a.y < b.y + b.height && a.y + a.height > b.y, false, 'labels must not overlap');
    }
}

test('221 ideas and 141 relationships fit the real viewport without losing data', () => {
    const graph = layout(buildGraph(denseThoughtMap()));
    assert.equal(graph.nodes.size, 221); assert.equal(graph.links.length, 141);
    for (const [width, height] of [[854, 610], [1280, 630], [320, 420]]) {
        const camera = fitCamera(graph.nodes.values(), width, height), box = bounds(graph.nodes.values());
        assert.ok(box.width * camera.scale <= width - 35);
        assert.ok(box.height * camera.scale <= height - 89);
        const labels = labelPlacements(graph, new Set(graph.nodes.keys()), camera, width, height, labelMetrics(graph));
        checkLabels(labels, width, height);
        assert.ok(labels.size < 40);
        if (width >= 500) assert.ok([...labels.keys()].some(id => id.startsWith('theme-')), 'the overview must retain readable themes');
        assert.equal(graph.nodes.size, 221);
    }
});

test('labels stay separate while zooming, panning, selecting and searching a dense map', () => {
    const graph = layout(buildGraph(denseThoughtMap(300))), visible = new Set(graph.nodes.keys()), metrics = labelMetrics(graph);
    for (const scale of [.1, .4, .75, 1.5, 3.8]) for (const x of [-400, 0, 500]) {
        const camera = { x, y: 100, scale };
        checkLabels(labelPlacements(graph, visible, camera, 854, 610, metrics, ['entry-42']), 854, 610);
        checkLabels(labelPlacements(graph, visible, camera, 854, 610, metrics, [], 'paso 043'), 854, 610);
    }
    const point = graph.nodes.get('entry-42');
    const centered = { x: point.x, y: point.y, scale: 3.8 };
    const labels = labelPlacements(graph, visible, centered, 854, 610, metrics, ['entry-42']);
    assert.ok(labels.has('entry-42'));
});

test('glyph size stays bounded at both ends of zoom and arrows remain finite', () => {
    for (const kind of ['entry', 'theme', 'question', 'note']) for (const scale of [.005, .1, 1, 3.8]) {
        const radius = nodeRadius({ kind, degree: 80 }, scale);
        assert.ok(radius >= 3 && radius <= 27);
        assert.equal(/NaN|Infinity/.test(curve({ x: 0, y: 0 }, { x: 1, y: 1, kind }, (radius + 5) / scale)), false);
    }
});
