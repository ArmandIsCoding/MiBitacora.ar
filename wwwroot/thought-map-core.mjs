// Pure, deterministic geometry. Account data stays in the authenticated page.
export const kinds = {
    entry: { label: 'Tu entrada', color: '#a9cbb1' },
    theme: { label: 'Un tema propuesto por Brisa', color: '#e8c487' },
    question: { label: 'Una pregunta de Brisa', color: '#c3b7ed' },
    note: { label: 'Tus notitas y contexto', color: '#eeaa94' }
};
export const normalize = value => String(value ?? '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLocaleLowerCase('es');
const hash = value => [...value].reduce((n, c) => ((n * 31 + c.charCodeAt(0)) >>> 0), 7);

export function buildGraph(data) {
    const nodes = new Map();
    for (const raw of data.nodes ?? []) {
        if (!raw.id || !kinds[raw.kind] || nodes.has(raw.id)) continue;
        nodes.set(raw.id, { ...raw, neighbors: new Set(), degree: 0, x: 0, y: 0 });
    }
    const keys = new Set();
    const links = (data.links ?? []).filter(link => {
        const key = `${link.source}|${link.target}|${link.kind}`;
        if (link.source === link.target || !nodes.has(link.source) || !nodes.has(link.target) || keys.has(key)) return false;
        keys.add(key);
        nodes.get(link.source).neighbors.add(link.target);
        nodes.get(link.target).neighbors.add(link.source);
        return true;
    });
    for (const n of nodes.values()) n.degree = n.neighbors.size;
    return { nodes, links };
}

export function layout(graph) {
    const nodes = [...graph.nodes.values()];
    const hubs = nodes.filter(n => n.kind === 'theme').sort((a, b) => b.degree - a.degree || a.id.localeCompare(b.id)).slice(0, 8);
    if (!hubs.length && nodes.length) hubs.push(nodes.find(n => n.kind === 'entry') ?? nodes[0]);
    const hubIds = new Set(hubs.map(n => n.id));
    hubs.forEach((n, i) => {
        const angle = (i * Math.PI * 2 / hubs.length) - Math.PI * .9;
        const radius = hubs.length === 1 ? 0 : 175 + hubs.length * 20;
        n.x = Math.cos(angle) * radius * 1.25;
        n.y = Math.sin(angle) * radius * .85;
        n.anchorX = n.x; n.anchorY = n.y;
    });
    const counts = new Map();
    const place = (n, anchor) => {
        const count = counts.get(anchor.id) ?? 0;
        counts.set(anchor.id, count + 1);
        const angle = count * 2.399963 + (hash(n.id) % 25) * .03;
        const distance = (n.kind === 'question' ? 75 : 110) + Math.sqrt(count) * 42;
        n.x = anchor.x + Math.cos(angle) * distance;
        n.y = anchor.y + Math.sin(angle) * distance;
        n.anchorX = n.x; n.anchorY = n.y;
    };
    for (const n of nodes.filter(n => n.kind === 'entry' && !hubIds.has(n.id)))
        place(n, hubs.find(h => n.neighbors.has(h.id)) ?? hubs[hash(n.id) % hubs.length]);
    for (const n of nodes.filter(n => n.kind !== 'entry' && !hubIds.has(n.id))) {
        const entry = [...n.neighbors].map(id => graph.nodes.get(id)).find(other => other.kind === 'entry');
        place(n, entry ?? hubs[hash(n.id) % hubs.length]);
    }
    // Nearby-cell collisions keep the layout stable and bounded for large journals.
    const gap = 108;
    for (let pass = 0; pass < 70; pass++) {
        const grid = new Map();
        for (const n of nodes) {
            const key = `${Math.floor(n.x / gap)},${Math.floor(n.y / gap)}`;
            if (!grid.has(key)) grid.set(key, []);
            grid.get(key).push(n);
        }
        for (const n of nodes) {
            const gx = Math.floor(n.x / gap), gy = Math.floor(n.y / gap);
            for (let x = gx - 1; x <= gx + 1; x++) for (let y = gy - 1; y <= gy + 1; y++) {
                for (const other of grid.get(`${x},${y}`) ?? []) {
                    if (n === other) continue;
                    const dx = n.x - other.x, dy = n.y - other.y;
                    const distance = Math.hypot(dx, dy) || .1;
                    const min = n.kind === 'theme' || other.kind === 'theme' ? 125 : 99;
                    if (distance < min) {
                        const force = (min - distance) * .15;
                        const angle = distance < 1 ? (hash(n.id) % 100) : Math.atan2(dy, dx);
                        n.x += Math.cos(angle) * force; n.y += Math.sin(angle) * force;
                    }
                }
            }
            n.x += (n.anchorX - n.x) * .012;
            n.y += (n.anchorY - n.y) * .012;
        }
    }
    return graph;
}

export function neighborhood(graph, id, depth = 1) {
    if (!graph.nodes.has(id)) return new Set();
    const found = new Set([id]);
    let frontier = [id];
    for (let step = 0; step < depth; step++) {
        const next = [];
        for (const current of frontier) for (const neighbor of graph.nodes.get(current).neighbors)
            if (!found.has(neighbor)) { found.add(neighbor); next.push(neighbor); }
        frontier = next;
    }
    return found;
}

export function visibleNodes(graph, search = '', branch = null) {
    let visible = branch ? neighborhood(graph, branch, 2) : new Set(graph.nodes.keys());
    const term = normalize(search.trim());
    if (!term) return visible;
    const matches = [...graph.nodes.values()].filter(n => visible.has(n.id) &&
        normalize(`${n.label} ${n.detail} ${n.notebook ?? ''}`).includes(term));
    const result = new Set();
    for (const match of matches) for (const id of neighborhood(graph, match.id)) if (visible.has(id)) result.add(id);
    return result;
}

export function bounds(nodes) {
    const values = [...nodes];
    if (!values.length) return { x: -250, y: -200, width: 500, height: 400 };
    const minX = Math.min(...values.map(n => n.x)) - 110;
    const minY = Math.min(...values.map(n => n.y)) - 70;
    return { x: minX, y: minY, width: Math.max(...values.map(n => n.x)) + 110 - minX,
        height: Math.max(...values.map(n => n.y)) + 90 - minY };
}

export function fitCamera(nodes, width, height) {
    const box = bounds(nodes);
    return { x: box.x + box.width / 2, y: box.y + box.height / 2,
        scale: Math.max(.001, Math.min(1.6, Math.max(1, width - 36) / box.width, Math.max(1, height - 90) / box.height)) };
}

// Screen-space glyphs and labels remain readable without growing into neighboring ideas.
export function nodeRadius(node, scale) {
    const base = node.kind === 'theme' ? 23 : node.kind === 'entry' ? 8 + Math.min(node.degree, 5) : 8;
    return Math.max(node.kind === 'theme' ? 7 : 3, Math.min(node.kind === 'theme' ? 27 : 14, base * Math.sqrt(scale)));
}

export function labelPlacements(graph, visible, camera, width, height, metrics, priorityIds = [], search = '') {
    const projected = [...visible].map(id => graph.nodes.get(id)).filter(Boolean).map(node => ({
        node, x: width / 2 + (node.x - camera.x) * camera.scale,
        y: height / 2 + (node.y - camera.y) * camera.scale,
        radius: nodeRadius(node, camera.scale)
    })).filter(p => p.x > -40 && p.x < width + 40 && p.y > -40 && p.y < height + 40);
    const priorities = new Map(priorityIds.filter(Boolean).map((id, i) => [id, 100000 - i * 1000]));
    const term = normalize(search.trim());
    const areaBudget = Math.max(3, Math.floor(width * height / 11000));
    const crowded = projected.length > areaBudget * 1.5;
    const grid = new Map(), cell = 80;
    const cells = rect => {
        const keys = [];
        for (let x = Math.floor(rect.x / cell); x <= Math.floor((rect.x + rect.width) / cell); x++)
            for (let y = Math.floor(rect.y / cell); y <= Math.floor((rect.y + rect.height) / cell); y++) keys.push(`${x},${y}`);
        return keys;
    };
    const occupy = rect => { for (const key of cells(rect)) { if (!grid.has(key)) grid.set(key, []); grid.get(key).push(rect); } };
    const intersects = (a, b) => a.x < b.x + b.width && a.x + a.width > b.x && a.y < b.y + b.height && a.y + a.height > b.y;
    const collides = rect => cells(rect).some(key => (grid.get(key) ?? []).some(other => intersects(rect, other)));
    // Tiny distant points must not crowd every label out of the overview.
    for (const p of projected) if (p.node.kind === 'theme' || priorities.has(p.node.id) || p.radius >= 12)
        occupy({ x: p.x - p.radius - 3, y: p.y - p.radius - 3, width: p.radius * 2 + 6, height: p.radius * 2 + 6 });
    const score = p => priorities.get(p.node.id) ?? ((term && normalize(`${p.node.label} ${p.node.detail} ${p.node.notebook ?? ''}`).includes(term) ? 50000 : 0)
        + (p.node.kind === 'theme' ? 20000 : p.node.kind === 'entry' ? 3000 : 1000) + p.node.degree * 10);
    projected.sort((a, b) => score(b) - score(a) || a.node.id.localeCompare(b.node.id));
    const placed = new Map();
    for (const p of projected) {
        const important = priorities.has(p.node.id) || (term && score(p) >= 50000);
        if (crowded && !important && camera.scale < .7 && p.node.kind !== 'theme') continue;
        const size = metrics.get(p.node.id);
        if (!size) continue;
        const gap = p.radius + 9, w = size.width + 12, h = size.height + 8;
        const candidates = [
            { x: p.x - w / 2, y: p.y + gap, width: w, height: h },
            { x: p.x + gap, y: p.y - h / 2, width: w, height: h },
            { x: p.x - w / 2, y: p.y - gap - h, width: w, height: h },
            { x: p.x - gap - w, y: p.y - h / 2, width: w, height: h }
        ];
        const within = rect => rect.x >= 12 && rect.y >= 36 && rect.x + rect.width <= width - 12 && rect.y + rect.height <= height - 32;
        const chosen = candidates.find(rect => within(rect) && !collides(rect));
        // Selection is always readable in the detail panel if no label fits safely here.
        if (chosen) { placed.set(p.node.id, chosen); occupy(chosen); }
    }
    return placed;
}

export function curve(source, target, targetInset = null) {
    const dx = target.x - source.x, dy = target.y - source.y;
    const distance = Math.hypot(dx, dy) || 1;
    const inset = Math.min(targetInset ?? (target.kind === 'theme' ? 30 : 17), distance * .8);
    const endX = target.x - dx / distance * inset, endY = target.y - dy / distance * inset;
    const bend = Math.min(35, distance * .1);
    return `M${source.x},${source.y} Q${(source.x + endX) / 2 - dy / distance * bend},${(source.y + endY) / 2 + dx / distance * bend} ${endX},${endY}`;
}

export function safeUrl(value) {
    return typeof value === 'string' && /^\/(entrada\/[0-9a-f-]+|bitacora|Account\/Manage)(\?|$)/i.test(value)
        && !/[\r\n\\]/.test(value) ? value : null;
}
