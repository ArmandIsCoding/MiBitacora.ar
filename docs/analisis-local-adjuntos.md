# Análisis local de adjuntos

La lectura ampliada está implementada detrás de `LocalAttachments:Enabled=false`. No cambia el proveedor elegido por la cuenta ni la configuración privada del servidor: OpenAI se conserva hasta decidir el cambio de infraestructura. La función solo interviene cuando está habilitada, se pidió una respuesta y el proveedor efectivo es IA local. **Solo guardar** no inicia decodificadores ni transcripciones.

## Formatos y alcance

| Contenido | Lectura local |
| --- | --- |
| JPG, PNG, WebP, HEIC/HEIF, GIF y AVIF | Se normaliza una imagen JPEG para el modelo con visión. En imágenes animadas se usa una imagen estática. |
| DOCX, PPTX y ODT | Extracción de párrafos sin ejecutar macros ni contenido activo. |
| XLS, XLSX, XLSM y XLSB | Extracción de celdas, hojas y valores disponibles; no se ejecutan macros ni se recalculan fórmulas. |
| TXT, MD/Markdown, CSV, TSV, JSON/JSONL, XML, YAML/YML, LOG e INI | Extracción acotada del texto. |
| PDF con texto | Texto identificado por página y muestra de páginas renderizadas como JPEG. |
| PDF escaneado | Páginas renderizadas para lectura visual de Gemma; los números de página no se presentan como contenido extraído. |
| MP3, M4A, AAC, WAV, OGG/Opus, FLAC y WebA/WebM de audio | Conversión local a WAV mono de 16 kHz y transcripción con Gemma. También permite leer audios anteriores guardados como MP3. |
| MP4/M4V, MOV, WebM, OGV, 3GP, MKV, AVI y WMV | Muestra de fotogramas JPEG más transcripción de la pista de audio, cuando existe. Los videos silenciosos no generan una transcripción ficticia. |

Los formatos sin un lector compatible, archivos protegidos o dañados se conservan y se informa que no pudieron leerse. Admitir una carga no implica poder interpretar cualquier archivo binario. La lectura de imágenes y transcripciones puede contener errores y no constituye una evaluación clínica.

La preparación no modifica los bytes del adjunto guardado. La optimización existente durante la subida continúa vigente. El texto del PDF y las transcripciones se guardan para búsquedas y contexto; las interpretaciones del modelo no se guardan como texto original del archivo. Las voces de un video se identifican como hablantes no confirmados. Al regenerar se reutiliza la transcripción; una pregunta posterior puede recuperar las muestras de hasta dos PDF/videos ya preparados sin volver a subirlos.

## Límites explícitos

- Archivos guardados de hasta 64 MiB; los límites de subida, duración, cuota y cantidad existentes siguen vigentes.
- Audio y video de hasta 10 minutos. Audio para transcribir: WAV de hasta 20 MiB.
- PDF: lectura de hasta 80 páginas de texto y 16.000 caracteres; por defecto se renderizan la primera y la última página, o la única página existente. Se rechazan documentos de más de 2.000 páginas.
- Video: dos muestras visuales distribuidas por la duración, con posiciones aproximadas. No se afirma haber examinado todos los cuadros.
- Hasta cuatro imágenes y 12 MiB visuales en una respuesta local; OpenAI conserva el límite anterior de dos imágenes. El contexto textual local sigue acotado a 6.500 caracteres, con espacio reservado para el documento actual y fragmentos de hasta 3.000 caracteres por adjunto actual.
- Las etiquetas entregadas a Brisa indican páginas, momentos y cobertura parcial. El texto completo del documento puede superar lo que entra en una respuesta.

Los PDF se procesan en un proceso hijo con plazo de ejecución, sin arrancar la aplicación web ni cargar su configuración privada. Las imágenes se limitan a 1.600 px por lado. No se ejecutan formularios interactivos. Los videos usan FFmpeg/FFprobe con protocolos y formatos permitidos, argumentos separados, un hilo por decodificación/imagen y fotogramas de hasta 1.280 px por lado. Las copias temporales son privadas y se eliminan al terminar, fallar o cancelar. No se usan servicios de conversión externos ni una alternativa automática a OpenAI si falla Ollama.

Dependencias PDF: [PDFtoImage 5.4.0](https://github.com/sungaila/PDFtoImage) y [PdfPig 0.1.16](https://github.com/UglyToad/PdfPig), distribuidas por NuGet. Los binarios multimedia se preparan con `scripts/Prepare-MediaTools.ps1`, que verifica la procedencia y el SHA-256.

## Modelos en D:\OllamaModels desde CMD

Los modelos utilizados son `gemma4:12b` para texto, visión y transcripción, y `embeddinggemma` para la memoria vectorial. La implementación se verificó con Ollama 0.40.16. No hace falta otro modelo para PDF o video: se preparan entradas que Gemma ya entiende.

Primero cerrá Ollama desde su icono de la bandeja y terminá cualquier `ollama serve` anterior del puerto 11434. En una ventana de **CMD**:

```bat
if not exist "D:\OllamaModels" mkdir "D:\OllamaModels"
setx OLLAMA_MODELS "D:\OllamaModels"
set "OLLAMA_MODELS=D:\OllamaModels"
set "OLLAMA_HOST=127.0.0.1:11434"
set "OLLAMA_NO_CLOUD=1"
ollama serve
```

Dejá esa ventana abierta. En una **segunda ventana de CMD**:

```bat
set "OLLAMA_HOST=127.0.0.1:11434"
ollama pull gemma4:12b
ollama pull embeddinggemma
ollama list
dir "D:\OllamaModels\manifests\registry.ollama.ai\library"
```

`setx` configura futuras sesiones; `set` configura el proceso que se inicia en esa ventana. **La carpeta la determina el servidor `ollama serve`, no el cliente que ejecuta `ollama pull`.** Cambiar la variable no mueve modelos existentes. En esta instalación ambos modelos ya estaban completos en D:, con unos 7,47 GiB y 0,58 GiB de archivos, respectivamente. `pull` verifica o completa la versión publicada. Ver [configuración oficial de Ollama](https://docs.ollama.com/faq#setting-environment-variables-on-windows).

El script existente `scripts/Configure-LocalOllama.ps1 -DownloadModel` también usa `D:\OllamaModels` por defecto; permite indicar otra carpeta con `-ModelsPath` y crea la carpeta si falta. Rechaza reiniciar mientras haya modelos cargados. Configurar Ollama no modifica el proveedor de Mi Bitácora.

## Verificación reproducible

Desde la raíz del repositorio, las pruebas de formatos y flujo usan archivos y cuentas sintéticos, sin cobros ni llamadas externas:

```bat
dotnet build tests\MiBitacora.Checks -c Release -p:UseAppHost=false
dotnet tests\MiBitacora.Checks\bin\Release\net10.0\MiBitacora.Checks.dll --local-attachments-only --media-codecs
```

Para las pruebas con el Ollama real, prepará primero el audio sintético con una voz de Windows. Este comando de CMD ejecuta el script local de prueba sin modificar la política de ejecución:

```bat
powershell.exe -NoProfile -Command "& ([scriptblock]::Create((Get-Content -Raw 'scripts\New-LocalAttachmentTestAudio.ps1'))) -Destination '.artifacts\local-attachments\speech.wav'"
dotnet tests\MiBitacora.Checks\bin\Release\net10.0\MiBitacora.Checks.dll --local-attachments-live --ollama-url=http://127.0.0.1:11434
```

Se puede ejecutar un solo caso con `--case=pdf-text`, `--case=pdf-scan`, `--case=audio` o `--case=video`. Los resultados y respuestas sintéticas quedan en `.artifacts/local-attachments/live`, fuera de Git. Los archivos numéricos XLS/XLSB de referencia conservan su [procedencia y licencia](../tests/Fixtures/README.md).

El 18 de noviembre de 2026 pasaron las cuatro pruebas reales en una instancia aislada de Ollama 0.40.16, puerto 11436, usando los modelos de D: y sin cambiar la configuración de producción:

| Prueba real | Resultado | Tiempo medido |
| --- | --- | --- |
| PDF con texto y páginas visuales | Identificó viernes y cuaderno verde | 238,9 s |
| PDF escaneado | Identificó la figura roja de las páginas | 268,9 s |
| MP3 con una frase sintética en español | Transcribió cuaderno verde y reunión el viernes | 36,3 s |
| Video con imagen y voz | Distinguió imagen roja, cuaderno verde y viernes | 160,7 s |

Los tiempos de PDF y video corresponden a la respuesta de Brisa; el del audio corresponde a conversión y transcripción. El video además requiere preparar los fotogramas y transcribir su pista de audio. Son mediciones de este equipo y estos archivos, no garantías de latencia ni de exactitud para otros contenidos. La función permanece desactivada por defecto mientras se mantiene OpenAI y se mejora el hardware.

## Activación posterior

Cuando se decida habilitarla, la configuración privada puede incluir:

```json
"LocalAttachments": {
  "Enabled": true,
  "PdfTextPages": 80,
  "PdfVisualPages": 2,
  "VideoFrames": 2,
  "PreparationTimeoutSeconds": 60
}
```

Los máximos configurables son 200 páginas de texto, cuatro páginas visuales, cuatro fotogramas y 10–120 segundos por proceso de preparación. La selección de IA local u OpenAI sigue las reglas de [acceso y suscripciones](suscripciones.md); habilitar este bloque no cambia esa selección. Si se usa el [puente de IA local](puente-ia-local.md), se conservan sus rutas autorizadas y sus límites de tamaño.
