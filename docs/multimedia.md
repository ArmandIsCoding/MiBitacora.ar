# Fotos grandes y multimedia

Las cargas nuevas aceptan hasta 256 MiB por archivo antes de optimizarse. La selección muestra inmediatamente «Recibiendo», progreso por archivo, «Optimizando» y el tamaño antes/después. Los errores aparecen dentro del editor, con `role=alert`; se puede volver a seleccionar el mismo archivo. Los archivos válidos de una selección parcial permanecen adjuntos. El espacio por cuenta se comprueba al guardar, sobre los bytes ya optimizados.

El servidor recibe el archivo por streaming a `App_Data/media-staging/<UUID>.input`, fuera de `wwwroot`, sin reservar un buffer del tamaño original. Usa nombres generados, elimina los temporales en éxito, error y cancelación, y serializa los codecs para acotar el uso de CPU y memoria. No cambia los adjuntos históricos ni los originales de importaciones.

Al arrancar, antes de aceptar solicitudes, recupera las cargas interrumpidas de esa carpeta: elimina únicamente sus nombres UUID con sufijos `.input`/`.output`, sin recorrer otras carpetas ni seguir enlaces simbólicos.

- Fotos JPEG, PNG, HEIC/HEIF, WebP, GIF y AVIF reconocidas por sus bytes: primer cuadro, orientación EXIF corregida, sRGB, fondo blanco para transparencia, sin EXIF/GPS/XMP, JPG calidad 82. Hasta 64 megapíxeles; encajan en 1920×1080 o 1080×1920 según su orientación, sin ampliar ni deformar. Un cuadrado encaja en 1080×1080.
- Video: MP4 H.264/AAC, hasta Full HD y 30 fps, sin metadatos. Se conserva la proporción, con duración máxima de 10 minutos y límite de 64 MiB del resultado. Un archivo que no pueda reducirse se rechaza con explicación; no se guarda un video truncado para aparentar éxito.
- Audio: hasta 10 minutos, MP3 mono 96 kbps para OpenAI o WAV PCM mono de 16 kHz para Ollama, hasta 20 MiB para transcripción. Los audios ya grabados en el navegador mantienen la marca de mensaje de voz.
- Documentos: hasta 64 MiB sin conversión; se mantiene la extracción acotada de texto y los límites contra documentos comprimidos expansivos.

El total guardado admite 128 MiB y el editor hasta 30 archivos por mensaje. Los valores de la interfaz dicen MB por simplicidad; los límites se calculan en MiB. La cuenta mantiene su cuota configurada, por defecto en `appsettings.json` 1 GiB/2000 adjuntos.

## Codecs de audio y video

Las fotos usan Magick.NET, ya incluido. Audio/video requieren FFmpeg y FFprobe privados en `media-tools`, fuera de `wwwroot`. Preparalos antes de publicar:

```powershell
./scripts/Prepare-MediaTools.ps1
dotnet publish -c Release
```

El script descarga la compilación Essentials 9.0.2 de Gyan, un proveedor enlazado por [FFmpeg](https://www.ffmpeg.org/download.html), verifica el SHA256 publicado `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`, y conserva licencia y procedencia. Los ejecutables se excluyen de Git y se incluyen en el publish; no se instalan globalmente ni requieren abrir puertos. El código fuente y la información de la compilación quedan enlazados en `media-tools/provenance.json`. En Linux se requieren binarios equivalentes llamados `ffmpeg` y `ffprobe` en esa carpeta. Si faltan, se muestra un error explícito al intentar preparar audio/video; las fotos siguen funcionando.

FFmpeg se ejecuta sin shell ni ventanas, con argumentos separados, protocolos de entrada limitados a archivos/pipes, dos hilos, una conversión a la vez, plazo de ocho minutos y cancelación del proceso propio. Los errores no publican stderr ni rutas privadas.

## Contexto de la IA y costos

Ambos proveedores reciben hasta dos imágenes seleccionadas y acotadas. También se normalizan las copias de análisis de fotos históricas sin reescribir sus originales. El video completo no se envía a la IA. Reducir bytes baja transferencia y almacenamiento; el costo visual de OpenAI depende también de resolución, modelo y `detail`, según su [documentación de visión](https://developers.openai.com/api/docs/guides/images-vision). Se mantiene el modelo configurado y `detail=auto` para no degradar por defecto la lectura de hojas de diarios fotografiadas.

## Verificación

```powershell
./scripts/Prepare-MediaTools.ps1
./scripts/Test-Subscriptions.ps1 -Configuration Release -MediaCodecs
```

Las pruebas generan una foto sintética de más de 8 MiB con orientación y GPS, una de 48 megapíxeles, HEIC y MOV 4K. Comprueban orientación, proporción, eliminación de metadatos, compresión, codecs reales, WAV/MP3, duración, rechazos, cancelación y limpieza. No utilizan fotos personales ni llamadas pagas.
