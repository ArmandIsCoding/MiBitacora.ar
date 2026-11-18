// Deliberately fictional stress data. Never derived from a diary or a screenshot.
export function denseThoughtMap(entries = 109) {
    const nodes = ['Crear con calma', 'Un tiempo propio', 'Pequeños cambios'].map((label, i) => ({
        id: `theme-${i}`, kind: 'theme', label, detail: 'Tema de un recorrido completamente ficticio.'
    }));
    const links = [];
    for (let i = 0; i < entries; i++) {
        const number = String(i + 1).padStart(3, '0');
        nodes.push({ id: `entry-${i}`, kind: 'entry', label: `Idea ${number}: descubrir algo nuevo`, detail: `Entrada sintética ${number} para comprobar mapas grandes.`, notebook: 'Diario de ejemplo' });
        nodes.push({ id: `question-${i}`, kind: 'question', label: `¿Qué te gustaría explorar en el paso ${number}?`, detail: 'Pregunta ficticia para comprobar la navegación.' });
        links.push({ source: `entry-${i}`, target: `question-${i}`, kind: 'question', label: 'Pregunta de ejemplo' });
        if (i < Math.ceil(entries * 32 / 109)) links.push({ source: `entry-${i}`, target: `theme-${i % 3}`, kind: 'theme', label: 'Tema de ejemplo' });
    }
    return { nodes, links, totalEntries: entries, shownEntries: entries, limited: false };
}
