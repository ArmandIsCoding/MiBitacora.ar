export function create(root, receiver, maxBytes) {
    let disposed = false, stream, recorder, chunks = [], size = 0, blob, url, timer, started, discard = false;
    const notify = (method, value) => { if (!disposed) receiver.invokeMethodAsync(method, value).catch(dispose); };
    const release = () => { clearInterval(timer); stream?.getTracks().forEach(track => track.stop()); stream = null; };
    const clearPreview = () => {
        root.querySelectorAll('audio').forEach(audio => { audio.pause(); audio.removeAttribute('src'); audio.load(); });
        if (url) URL.revokeObjectURL(url);
        url = null; blob = null; chunks = []; size = 0;
    };
    async function start() {
        if (disposed) return 'El grabador se cerró.';
        if (!globalThis.isSecureContext || !navigator.mediaDevices?.getUserMedia || !globalThis.MediaRecorder)
            return 'Este navegador no permite grabar aquí. Abrí Mi Bitácora por HTTPS o usá Subir audio.';
        if (recorder?.state === 'recording') return null;
        clearPreview(); discard = false;
        try {
            stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
            if (disposed) { release(); return 'El grabador se cerró.'; }
            const mimeType = ['audio/mp4', 'audio/webm;codecs=opus', 'audio/webm', 'audio/ogg;codecs=opus']
                .find(type => MediaRecorder.isTypeSupported(type));
            if (!mimeType) { release(); return 'Este navegador no ofrece un formato de grabación compatible. Podés subir un archivo.'; }
            recorder = new MediaRecorder(stream, { mimeType, audioBitsPerSecond: 64000 });
            recorder.addEventListener('dataavailable', event => {
                if (event.data.size > 0 && !discard) { chunks.push(event.data); size += event.data.size; }
                if (size >= maxBytes - 65536) stop();
            });
            recorder.addEventListener('error', () => {
                discard = true; stop(); release();
                notify('RecordingFailed', 'Se interrumpió la grabación. Probá otra vez o subí un archivo.');
            });
            recorder.addEventListener('stop', () => {
                release();
                if (disposed || discard) { clearPreview(); return; }
                blob = new Blob(chunks, { type: recorder.mimeType }); chunks = [];
                if (!blob.size || blob.size > maxBytes) { clearPreview(); notify('RecordingFailed', 'La grabación quedó vacía o superó los 8 MB. Probá con un audio más corto.'); return; }
                url = URL.createObjectURL(blob);
                const ext = recorder.mimeType.includes('mp4') ? 'm4a' : recorder.mimeType.includes('ogg') ? 'ogg' : 'weba';
                notify('RecordingReady', { fileName: `Mensaje de voz ${new Date().toISOString().replace(/[:.]/g, '-')}.${ext}`, url, size: blob.size });
            });
            recorder.start(1000); started = Date.now();
            timer = setInterval(() => {
                const elapsed = Math.floor((Date.now() - started) / 1000);
                const display = root.querySelector('.voice-time');
                if (display) display.textContent = `${Math.floor(elapsed / 60)}:${String(elapsed % 60).padStart(2, '0')}`;
            }, 1000);
            return null;
        } catch (error) {
            release();
            return error?.name === 'NotAllowedError' ? 'El micrófono no tiene permiso. Podés habilitarlo en tu navegador o subir un audio.'
                : error?.name === 'NotFoundError' ? 'No encontramos un micrófono. Podés subir un audio desde tus archivos.'
                : 'No pudimos iniciar el micrófono. Probá otra vez o subí un audio.';
        }
    }
    function stop() { if (recorder && recorder.state !== 'inactive') recorder.stop(); }
    function reset() { discard = true; stop(); release(); clearPreview(); }
    function dispose() {
        if (disposed) return;
        disposed = true; reset(); observer.disconnect(); window.removeEventListener('pagehide', dispose);
    }
    const observer = new MutationObserver(() => { if (!root.isConnected) dispose(); });
    observer.observe(document.body, { childList: true, subtree: true });
    window.addEventListener('pagehide', dispose);
    return { start, stop, reset, dispose, read() {
        if (!blob || disposed) throw new Error('No hay una grabación disponible.');
        return DotNet.createJSStreamReference(blob);
    } };
}
