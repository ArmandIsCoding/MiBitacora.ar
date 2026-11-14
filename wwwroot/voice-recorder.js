export function encodeWav(buffer, maxBytes) {
    const length = buffer.length;
    if (length * 2 + 44 > maxBytes) throw new Error('El audio WAV supera el tamaño permitido. Probá con un audio de hasta 10 minutos.');
    const data = new ArrayBuffer(44 + length * 2), view = new DataView(data);
    const ascii = (offset, value) => [...value].forEach((c, i) => view.setUint8(offset + i, c.charCodeAt(0)));
    ascii(0, 'RIFF'); view.setUint32(4, 36 + length * 2, true); ascii(8, 'WAVE'); ascii(12, 'fmt ');
    view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
    view.setUint32(24, buffer.sampleRate, true); view.setUint32(28, buffer.sampleRate * 2, true);
    view.setUint16(32, 2, true); view.setUint16(34, 16, true); ascii(36, 'data'); view.setUint32(40, length * 2, true);
    const channels = Array.from({ length: buffer.numberOfChannels }, (_, i) => buffer.getChannelData(i));
    for (let i = 0; i < length; i++) {
        const sample = Math.max(-1, Math.min(1, channels.reduce((sum, channel) => sum + channel[i], 0) / channels.length));
        view.setInt16(44 + i * 2, Math.round(sample * (sample < 0 ? 32768 : 32767)), true);
    }
    return new Blob([data], { type: 'audio/wav' });
}

async function localWav(blob, maxBytes) {
    const Context = window.AudioContext || window.webkitAudioContext;
    if (!Context || !window.OfflineAudioContext) throw new Error('Este navegador no puede preparar el audio local. Podés subir un WAV.');
    const context = new Context();
    let decoded;
    try { decoded = await context.decodeAudioData(await blob.arrayBuffer()); }
    finally { await context.close(); }
    const length = Math.ceil(decoded.duration * 16000);
    if (length * 2 + 44 > maxBytes) throw new Error('El audio convertido supera el tamaño permitido. Probá con un audio de hasta 10 minutos.');
    const offline = new window.OfflineAudioContext(1, length, 16000);
    const source = offline.createBufferSource(); source.buffer = decoded; source.connect(offline.destination); source.start();
    return encodeWav(await offline.startRendering(), maxBytes);
}

export async function convertFile(inputId, maxBytes) {
    const file = document.getElementById(inputId)?.files?.[0];
    if (!file || file.size > maxBytes) throw new Error('El audio no está disponible o supera el tamaño permitido.');
    return DotNet.createJSStreamReference(await localWav(file, maxBytes));
}

export function create(root, receiver, maxBytes, preferWav = false) {
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
                if (!blob.size || blob.size > maxBytes) { clearPreview(); notify('RecordingFailed', 'La grabación quedó vacía o superó el tamaño permitido. Probá con un audio de hasta 10 minutos.'); return; }
                const recorded = blob;
                const publish = (ready, ext) => {
                    if (disposed || discard || blob !== recorded) return;
                    blob = ready; url = URL.createObjectURL(blob);
                    notify('RecordingReady', { fileName: `Mensaje de voz ${new Date().toISOString().replace(/[:.]/g, '-')}.${ext}`, url, size: blob.size });
                };
                if (preferWav) localWav(recorded, maxBytes).then(ready => publish(ready, 'wav'))
                    .catch(() => { if (!disposed && !discard && blob === recorded) { clearPreview(); notify('RecordingFailed', 'No pudimos preparar el audio para la IA local. Probá con un WAV o una grabación más corta.'); } });
                else publish(recorded, recorder.mimeType.includes('mp4') ? 'm4a' : recorder.mimeType.includes('ogg') ? 'ogg' : 'weba');
            });
            recorder.start(1000); started = Date.now();
            timer = setInterval(() => {
                const elapsed = Math.floor((Date.now() - started) / 1000);
                const display = root.querySelector('.voice-time');
                if (display) display.textContent = `${Math.floor(elapsed / 60)}:${String(elapsed % 60).padStart(2, '0')}`;
                if (elapsed >= 600) stop();
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
