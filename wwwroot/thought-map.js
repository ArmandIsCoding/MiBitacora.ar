import { buildGraph, layout, kinds, visibleNodes, neighborhood, bounds, curve, safeUrl } from './thought-map-core.mjs';

const ns = 'http://www.w3.org/2000/svg';
let instance = 0;
const svgElement = (tag, attributes = {}, text) => {
    const el = document.createElementNS(ns, tag);
    for (const [key, value] of Object.entries(attributes)) el.setAttribute(key, value);
    if (text != null) el.textContent = text;
    return el;
};
const words = (label, max) => {
    const parts = String(label).split(/\s+/), lines = [''];
    for (const word of parts) {
        if ((lines.at(-1) + ' ' + word).length > max && lines.at(-1)) {
            if (lines.length === 2) { lines[1] = lines[1].slice(0, max - 1) + '…'; break; }
            lines.push('');
        }
        lines[lines.length - 1] += (lines.at(-1) ? ' ' : '') + word;
    }
    return lines;
};

export function create(host, data, demo = false) {
    const controller = new AbortController(), signal = controller.signal;
    const get = selector => host.querySelector(selector);
    const stage = get('[data-map-stage]'), svg = get('[data-map-svg]');
    const graph = layout(buildGraph(data));
    const prefix = `thought-${++instance}`;
    let disposed = false, selected = null, branch = null, search = '', width = 800, height = 560;
    let camera = { x: 0, y: 0, scale: 1 }, pointers = new Map(), gesture = null, moved = false;
    let visible = new Set(graph.nodes.keys()), frame = 0, roving = graph.nodes.keys().next().value, labelScale = 0;
    const nodeElements = new Map(), edgeElements = [];
    svg.replaceChildren();
    const defs = svgElement('defs');
    for (const [kind, value] of Object.entries(kinds)) {
        const gradient = svgElement('radialGradient', { id: `${prefix}-glow-${kind}` });
        gradient.append(svgElement('stop', { offset: '0', 'stop-color': value.color, 'stop-opacity': '.16' }),
            svgElement('stop', { offset: '1', 'stop-color': value.color, 'stop-opacity': '0' }));
        const marker = svgElement('marker', { id: `${prefix}-arrow-${kind}`, viewBox: '0 0 10 10', refX: '8', refY: '5',
            markerWidth: '5', markerHeight: '5', orient: 'auto-start-reverse', markerUnits: 'userSpaceOnUse' });
        marker.append(svgElement('path', { d: 'M1 1L9 5L1 9', fill: 'none', stroke: value.color, 'stroke-width': '1.5' }));
        defs.append(gradient, marker);
    }
    svg.append(defs);
    const world = svgElement('g', { class: 'thought-world' }), nebula = svgElement('g', { 'aria-hidden': 'true' });
    const edges = svgElement('g', { class: 'thought-edges', 'aria-hidden': 'true' }), points = svgElement('g');
    world.append(nebula, edges, points); svg.append(world);
    [...graph.nodes.values()].filter(n => n.kind === 'theme').sort((a, b) => b.degree - a.degree).slice(0, 8).forEach(n => {
        nebula.append(svgElement('circle', { cx: n.x, cy: n.y, r: 250, fill: `url(#${prefix}-glow-theme)` }));
        nebula.append(svgElement('circle', { cx: n.x, cy: n.y, r: 94, class: 'thought-orbit' }));
        nebula.append(svgElement('circle', { cx: n.x, cy: n.y, r: 170, class: 'thought-orbit outer' }));
    });
    for (const link of graph.links) {
        const a = graph.nodes.get(link.source), b = graph.nodes.get(link.target);
        const colorKind = link.kind === 'context' ? 'entry' : link.kind === 'reply' ? 'note' : b.kind;
        const path = svgElement('path', { d: curve(a, b), class: `thought-edge is-${link.kind}`,
            stroke: kinds[colorKind].color, 'marker-end': `url(#${prefix}-arrow-${colorKind})` });
        edges.append(path); edgeElements.push({ path, link });
    }
    for (const n of graph.nodes.values()) {
        const theme = n.kind === 'theme', radius = theme ? 23 : n.kind === 'entry' ? 8 + Math.min(n.degree, 5) : 8;
        const group = svgElement('g', { class: `thought-node is-${n.kind}`, transform: `translate(${n.x} ${n.y})`,
            role: 'button', tabindex: n.id === roving ? '0' : '-1', 'aria-label': `${kinds[n.kind].label}: ${n.label}. ${n.degree} conexiones.`,
            'aria-pressed': 'false', 'data-node-id': n.id, style: `--node-color:${kinds[n.kind].color}` });
        group.append(svgElement('title', {}, n.label));
        group.append(svgElement('circle', { r: radius + 13, class: 'thought-node-halo' }));
        group.append(svgElement('circle', { r: radius + 6, class: 'thought-node-ring' }));
        if (n.kind === 'question') group.append(svgElement('path', { d: 'M0 -8L8 0L0 8L-8 0Z', class: 'thought-node-core' }));
        else group.append(svgElement('circle', { r: radius, class: 'thought-node-core' }));
        if (theme) group.append(svgElement('path', { d: 'M-7 0H7M0 -7V7M-4 -4L4 4M4 -4L-4 4', class: 'thought-node-spark' }));
        else if (n.kind === 'entry') group.append(svgElement('circle', { r: 2.4, class: 'thought-node-center' }));
        const text = svgElement('text', { y: radius + 23, 'text-anchor': 'middle', class: 'thought-node-label' });
        words(n.label, theme ? 23 : 25).forEach((line, i) => text.append(svgElement('tspan', { x: 0, dy: i ? '1.3em' : 0 }, line)));
        group.append(text); points.append(group); nodeElements.set(n.id, group);
        group.addEventListener('click', event => { event.stopPropagation(); if (event.detail === 0 && !moved) select(n.id); }, { signal });
        group.addEventListener('keydown', event => {
            if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); event.stopPropagation(); select(n.id); }
            else if (event.key.startsWith('Arrow')) {
                event.preventDefault(); event.stopPropagation();
                const dx = event.key === 'ArrowRight' ? 1 : event.key === 'ArrowLeft' ? -1 : 0;
                const dy = event.key === 'ArrowDown' ? 1 : event.key === 'ArrowUp' ? -1 : 0;
                const next = [...graph.nodes.values()].filter(other => visible.has(other.id) && other !== n &&
                    (other.x - n.x) * dx + (other.y - n.y) * dy > 0)
                    .sort((a, b) => directionalDistance(n, a, dx, dy) - directionalDistance(n, b, dx, dy))[0];
                if (next) { setRoving(next.id); nodeElements.get(next.id).focus(); center(next); }
            }
        }, { signal });
    }
    function directionalDistance(a, b, dx, dy) { return Math.hypot(b.x - a.x, b.y - a.y) + Math.abs((b.x - a.x) * dy - (b.y - a.y) * dx) * 2; }
    function setRoving(id) {
        if (roving) nodeElements.get(roving)?.setAttribute('tabindex', '-1');
        roving = id; nodeElements.get(id)?.setAttribute('tabindex', '0');
    }
    function draw() {
        frame = 0;
        if (disposed) return;
        world.setAttribute('transform', `translate(${width / 2 - camera.x * camera.scale} ${height / 2 - camera.y * camera.scale}) scale(${camera.scale})`);
        host.classList.toggle('is-distant', camera.scale < .48);
        if (labelScale !== camera.scale) {
            labelScale = camera.scale;
            for (const [id, el] of nodeElements) {
                const isTheme = graph.nodes.get(id).kind === 'theme';
                el.querySelector('text').style.fontSize = Math.max(isTheme ? 18 : 11, (isTheme ? 15 : 10.5) / camera.scale) + 'px';
            }
        }
    }
    function schedule() { if (!frame && !disposed) frame = requestAnimationFrame(draw); }
    function fit() {
        const box = bounds([...visible].map(id => graph.nodes.get(id)));
        camera = { x: box.x + box.width / 2, y: box.y + box.height / 2,
            scale: Math.max(.08, Math.min(1.6, (width - 30) / box.width, (height - 70) / box.height)) };
        schedule();
    }
    function center(n) { camera.x = n.x; camera.y = n.y; schedule(); }
    function zoom(factor, clientX = width / 2, clientY = height / 2) {
        const old = camera.scale, next = Math.min(3.8, Math.max(.08, old * factor));
        camera.x += (clientX - width / 2) * (1 / old - 1 / next);
        camera.y += (clientY - height / 2) * (1 / old - 1 / next);
        camera.scale = next; schedule();
    }
    function paint() {
        visible = visibleNodes(graph, search, branch);
        const related = selected ? neighborhood(graph, selected) : null;
        for (const [id, el] of nodeElements) {
            el.style.display = visible.has(id) ? '' : 'none';
            el.classList.toggle('is-selected', id === selected);
            el.classList.toggle('is-related', !!related?.has(id));
            el.classList.toggle('is-muted', !!related && !related.has(id));
            el.setAttribute('aria-pressed', id === selected ? 'true' : 'false');
        }
        let connections = 0;
        for (const { path, link } of edgeElements) {
            const show = visible.has(link.source) && visible.has(link.target);
            if (show) connections++;
            path.style.display = show ? '' : 'none';
            path.classList.toggle('is-highlighted', selected === link.source || selected === link.target);
            path.classList.toggle('is-muted', !!selected && selected !== link.source && selected !== link.target);
        }
        if (!visible.has(roving)) setRoving(visible.values().next().value);
        get('[data-map-empty]').hidden = visible.size > 0;
        get('[data-map-count]').textContent = `${visible.size} ideas · ${connections} conexiones`;
        get('[data-map-hint]').textContent = branch ? 'Explorando una rama · «Ver todo» vuelve al recorrido completo' : 'Arrastrá para explorar · elegí un punto para seguir su historia';
    }
    function select(id) {
        if (!graph.nodes.has(id)) return;
        selected = id; setRoving(id);
        const n = graph.nodes.get(id);
        get('[data-map-welcome]').hidden = true; get('[data-map-detail]').hidden = false;
        get('.thought-detail').scrollTop = 0;
        get('[data-map-kind]').textContent = kinds[n.kind].label;
        get('[data-map-title]').textContent = n.label;
        get('[data-map-text]').textContent = n.detail;
        let date = '';
        if (n.date) { const parsed = new Date(n.date.endsWith('Z') ? n.date : n.date + 'Z'); if (!isNaN(parsed)) date = parsed.toLocaleDateString('es-AR', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'America/Argentina/Buenos_Aires' }); }
        get('[data-map-meta]').textContent = [n.notebook, date].filter(Boolean).join(' · ');
        const url = safeUrl(n.url), source = get('[data-map-source]');
        source.hidden = !url || demo; if (url) source.setAttribute('href', url); else source.removeAttribute('href');
        source.firstChild.textContent = n.kind === 'question' ? 'Ver conversación original ' : n.kind === 'note' ? 'Abrir en mi cuaderno ' : 'Abrir entrada original ';
        get('[data-map-degree]').textContent = `(${n.degree})`;
        const list = get('[data-map-connections]'); list.replaceChildren();
        for (const link of graph.links.filter(l => l.source === id || l.target === id)) {
            const other = graph.nodes.get(link.source === id ? link.target : link.source);
            const li = document.createElement('li'), button = document.createElement('button'); button.type = 'button';
            const description = document.createElement('small'), title = document.createElement('span');
            description.textContent = `${link.source === id ? '→' : '←'} ${link.label}`; title.textContent = other.label;
            button.append(description, title); button.style.setProperty('--connection-color', kinds[other.kind].color);
            button.dataset.connectionId = other.id;
            li.append(button); list.append(li);
        }
        if (!list.children.length) { const li = document.createElement('li'); li.textContent = 'Este es un punto de partida. Las conexiones aparecen al conversar con Brisa.'; list.append(li); }
        get('[data-map-action="branch"]').setAttribute('aria-pressed', String(!!branch));
        paint();
    }
    function clear() {
        selected = null; branch = null;
        get('[data-map-welcome]').hidden = false; get('[data-map-detail]').hidden = true;
        paint();
    }
    get('[data-map-connections]').addEventListener('click', event => {
        const id = event.target.closest('[data-connection-id]')?.dataset.connectionId;
        if (!id || !graph.nodes.has(id)) return;
        if (branch && !neighborhood(graph, branch, 2).has(id)) branch = id;
        select(id); center(graph.nodes.get(id)); get('[data-map-title]').focus({ preventScroll: true });
    }, { signal });
    get('[data-map-search]').addEventListener('input', event => { search = event.target.value; paint(); fit(); }, { signal });
    for (const button of host.querySelectorAll('[data-map-action]')) button.addEventListener('click', () => {
        const action = button.dataset.mapAction;
        if (action === 'in') zoom(1.3);
        if (action === 'out') zoom(1 / 1.3);
        if (action === 'fit') { clear(); search = ''; get('[data-map-search]').value = ''; paint(); fit(); }
        if (action === 'clear') { clear(); stage.focus({ preventScroll: true }); }
        if (action === 'branch' && selected) { branch = branch ? null : selected; select(selected); fit(); }
    }, { signal });
    stage.addEventListener('keydown', event => {
        if (event.target !== stage && event.target !== svg && !['Escape', 'Home', '+', '=', '-'].includes(event.key)) return;
        const step = 65 / camera.scale;
        if (event.key === '+' || event.key === '=') zoom(1.3);
        else if (event.key === '-') zoom(1 / 1.3);
        else if (event.key === 'Home') fit();
        else if (event.key === 'Escape') clear();
        else if (event.key === 'ArrowLeft') camera.x -= step;
        else if (event.key === 'ArrowRight') camera.x += step;
        else if (event.key === 'ArrowUp') camera.y -= step;
        else if (event.key === 'ArrowDown') camera.y += step;
        else return;
        event.preventDefault(); schedule();
    }, { signal });
    stage.addEventListener('wheel', event => {
        // Preserve ordinary page scrolling; modifier + wheel is an explicit map zoom.
        if (!event.ctrlKey && !event.metaKey) return;
        event.preventDefault(); const rect = stage.getBoundingClientRect();
        zoom(Math.exp(-Math.max(-100, Math.min(100, event.deltaY)) * .008), event.clientX - rect.left, event.clientY - rect.top);
    }, { signal, passive: false });
    const distance = () => { const p = [...pointers.values()]; return p.length > 1 ? Math.hypot(p[0].x - p[1].x, p[0].y - p[1].y) : 0; };
    function startGesture() {
        const p = [...pointers.values()];
        gesture = { x: p.reduce((s, n) => s + n.x, 0) / p.length, y: p.reduce((s, n) => s + n.y, 0) / p.length,
            distance: distance(), camera: { ...camera } };
    }
    stage.addEventListener('pointerdown', event => {
        if (event.button !== 0) return;
        moved = false; pointers.set(event.pointerId, { x: event.clientX, y: event.clientY }); startGesture();
        stage.setPointerCapture(event.pointerId);
    }, { signal });
    stage.addEventListener('pointermove', event => {
        if (!pointers.has(event.pointerId) || !gesture) return;
        pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
        const p = [...pointers.values()], x = p.reduce((s, n) => s + n.x, 0) / p.length, y = p.reduce((s, n) => s + n.y, 0) / p.length;
        if (Math.hypot(x - gesture.x, y - gesture.y) > 4 || (p.length > 1 && Math.abs(distance() - gesture.distance) > 4)) moved = true;
        camera.x = gesture.camera.x - (x - gesture.x) / gesture.camera.scale;
        camera.y = gesture.camera.y - (y - gesture.y) / gesture.camera.scale;
        if (p.length > 1 && gesture.distance > 0) camera.scale = Math.max(.08, Math.min(3.8, gesture.camera.scale * distance() / gesture.distance));
        stage.classList.toggle('is-dragging', moved); schedule();
    }, { signal });
    function endPointer(event) {
        const wasTracked = pointers.delete(event.pointerId);
        if (!wasTracked) return;
        if (stage.hasPointerCapture(event.pointerId)) stage.releasePointerCapture(event.pointerId);
        // Pointer capture retargets clicks: resolve the node under the released pointer.
        if (!moved && event.type === 'pointerup') {
            const target = document.elementFromPoint(event.clientX, event.clientY)?.closest('[data-node-id]');
            if (target && host.contains(target)) select(target.getAttribute('data-node-id'));
        }
        if (pointers.size) startGesture(); else { gesture = null; stage.classList.remove('is-dragging'); }
    }
    stage.addEventListener('pointerup', endPointer, { signal });
    stage.addEventListener('pointercancel', endPointer, { signal });
    stage.addEventListener('lostpointercapture', event => { pointers.delete(event.pointerId); if (!pointers.size) gesture = null; }, { signal });
    const observer = new ResizeObserver(entries => {
        const rect = entries[0].contentRect; width = Math.max(1, rect.width); height = Math.max(1, rect.height);
        svg.setAttribute('viewBox', `0 0 ${width} ${height}`); fit();
    });
    observer.observe(stage); paint();
    if (data.focus && graph.nodes.has(data.focus)) { branch = data.focus; select(data.focus); }
    host.classList.add('is-ready');
    return { dispose() {
        if (disposed) return;
        disposed = true; controller.abort(); observer.disconnect();
        if (frame) cancelAnimationFrame(frame);
        pointers.clear(); svg.replaceChildren(); host.classList.remove('is-ready');
    } };
}
