import { getDocument, GlobalWorkerOptions } from "/vendor/pdfjs/6.5.438/pdf.min.mjs";

const assets = "/vendor/pdfjs/6.5.438/";
GlobalWorkerOptions.workerSrc = assets + "pdf.worker.min.mjs";

export function open(root, url) {
    let disposed = false, pdf, pageNumber = 1, zoom = 1, pending = false, rendering = false, resizeTimer;
    let renderTask;
    const make = (tag, text, className) => {
        const element = document.createElement(tag);
        if (text) element.textContent = text;
        if (className) element.className = className;
        return element;
    };
    const toolbar = make("div", null, "pdf-toolbar");
    toolbar.setAttribute("aria-label", "Páginas y zoom del documento");
    const status = make("span", "Abriendo documento…", "pdf-status");
    status.setAttribute("role", "status");
    const button = (label, text, action) => {
        const item = make("button", text, "viewer-control");
        item.type = "button"; item.setAttribute("aria-label", label); item.title = label;
        item.addEventListener("click", action); toolbar.append(item); return item;
    };
    const previous = button("Página anterior", "‹", () => { if (pageNumber > 1) { pageNumber--; requestRender(); } });
    toolbar.append(status);
    const next = button("Página siguiente", "›", () => { if (pdf && pageNumber < pdf.numPages) { pageNumber++; requestRender(); } });
    const smaller = button("Reducir documento", "−", () => { zoom = Math.max(.75, zoom - .25); requestRender(); });
    const larger = button("Ampliar documento", "+", () => { zoom = Math.min(2, zoom + .25); requestRender(); });
    const scroll = make("div", null, "pdf-pages");
    const canvas = make("canvas");
    canvas.setAttribute("role", "img");
    const textView = make("details", null, "pdf-text");
    textView.append(make("summary", "Leer texto de esta página"));
    const text = make("p", null, "preserved-text");
    textView.append(text);
    scroll.append(canvas, textView);
    root.replaceChildren(toolbar, scroll);
    const controls = [previous, next, smaller, larger];
    controls.forEach(b => b.disabled = true);
    const task = getDocument({ url, withCredentials: true, isEvalSupported: false, enableXfa: false,
        cMapUrl: assets + "cmaps/", cMapPacked: true, standardFontDataUrl: assets + "standard_fonts/", wasmUrl: assets + "wasm/" });

    function fail(error) {
        if (disposed) return;
        controls.forEach(b => b.disabled = true);
        status.textContent = "No pudimos mostrar el documento.";
        scroll.replaceChildren(make("p", error?.name === "PasswordException"
            ? "Este PDF requiere una contraseña. Podés abrir el original con una aplicación compatible."
            : "El archivo no se pudo leer. Podés descargar el original desde el botón del visor.", "pdf-error"));
    }
    async function requestRender() {
        pending = true;
        if (rendering || !pdf || disposed) return;
        rendering = true;
        try {
            while (pending && !disposed) {
                pending = false;
                const current = pageNumber;
                status.textContent = `Página ${current} de ${pdf.numPages}`;
                previous.disabled = current === 1; next.disabled = current === pdf.numPages;
                smaller.disabled = zoom <= .75; larger.disabled = zoom >= 2;
                const page = await pdf.getPage(current);
                if (disposed) break;
                const original = page.getViewport({ scale: 1 });
                const width = Math.max(200, Math.min(960, scroll.clientWidth - 24));
                const viewport = page.getViewport({ scale: width / original.width * zoom });
                const pixels = Math.min(window.devicePixelRatio || 1, 2, Math.sqrt(4_000_000 / (viewport.width * viewport.height)));
                canvas.width = Math.floor(viewport.width * pixels); canvas.height = Math.floor(viewport.height * pixels);
                canvas.style.width = `${viewport.width}px`; canvas.style.height = `${viewport.height}px`;
                canvas.setAttribute("aria-label", `Página ${current} del documento. Texto disponible debajo.`);
                renderTask = page.render({ canvasContext: canvas.getContext("2d"), viewport, transform: [pixels, 0, 0, pixels, 0, 0] });
                await renderTask.promise;
                const content = await page.getTextContent();
                if (disposed) break;
                text.textContent = content.items.filter(item => "str" in item).map(item => item.str + (item.hasEOL ? "\n" : " ")).join("").trim() || "Esta página contiene una imagen sin texto disponible.";
                scroll.scrollTop = 0; scroll.scrollLeft = 0;
                page.cleanup();
            }
        } catch (error) { if (!disposed && error?.name !== "RenderingCancelledException") fail(error); }
        finally { rendering = false; }
    }
    task.promise.then(document => { pdf = document; if (!disposed) requestRender(); }).catch(fail);
    const resize = new ResizeObserver(() => {
        clearTimeout(resizeTimer);
        resizeTimer = setTimeout(requestRender, 120);
    });
    resize.observe(root);
    return { dispose() {
        disposed = true; clearTimeout(resizeTimer); resize.disconnect(); renderTask?.cancel();
        task.destroy().catch(() => {});
    } };
}
