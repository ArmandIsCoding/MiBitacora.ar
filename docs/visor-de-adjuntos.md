# Ver y reproducir adjuntos dentro del cuaderno

Al tocar un adjunto se abre un visor dentro de Mi Bitácora. Se comparte entre Conversación, Calendario y Entrada completa; en esta última, que usa renderizado estático, sólo el componente del visor agrega interactividad de servidor.

- Fotos: galería con miniaturas, anterior/siguiente, flechas de teclado, gesto horizontal y ampliación. HEIC/HEIF se muestra mediante la copia JPG local y conserva el original.
- Audio y video: controles nativos del navegador, sin reproducción automática; se detienen al cambiar de adjunto o cerrar. La compatibilidad concreta depende del códec, no sólo de la extensión.
- PDF: lector integrado con páginas, zoom y texto seleccionable por página. Usa PDF.js 6.5.438 alojado en el sitio, con biblioteca, worker, fuentes y mapas de caracteres locales. No carga un visor externo ni envía documentos a otro servicio. No ejecuta scripts del PDF ni activa formularios XFA. Los documentos protegidos con contraseña muestran un aviso para abrir el original.
- Documentos con extracción: presenta el texto disponible como texto escapado, conservando el original con su formato. No pretende reproducir la maquetación de Word o Excel.
- Otros formatos: explica que no hay vista previa y deja **Descargar original** como una acción explícita. Los formatos o archivos que el navegador no puede reproducir muestran una alternativa, sin descargar automáticamente.

El diálogo nativo mantiene el foco dentro del visor y permite cerrar con Escape. En móvil ocupa la pantalla disponible con `100dvh` y respeta las áreas seguras. Las miniaturas pueden desplazarse horizontalmente sin ensanchar la página.

## Entrega privada de archivos

`AttachmentEndpoints` conserva las rutas autenticadas `/adjuntos/{id}` y `/adjuntos/{id}/vista`. La primera descarga el original; la segunda admite GET y HEAD y sirve contenido inline con rangos HTTP para reproducción y búsqueda en medios. Ambas resuelven el adjunto por ID **y cuenta autenticada**. Un ID ajeno no permite obtener el archivo.

Antes de servir una vista, `JournalStore` comprueba la firma del archivo y permite sólo MIME de imágenes raster, medios y PDF. Una extensión `.mp3` no convierte HTML en contenido activo. Las respuestas usan `private, no-store`, `nosniff`, una política CSP restringida y `Content-Disposition: inline`. No se sirven HTML, SVG o XML subidos como páginas ejecutables. El texto extraído también se escapa al mostrarlo.

Los audios y videos antiguos con MIME genérico eligen un reproductor por extensión y validan su contenido al entregar la vista. No hay migración, modificación de originales ni cambio de ubicación de uploads. Tampoco se agrega transcripción ni análisis de audio/video por la IA: **poder reproducir un archivo no significa que el modelo pueda interpretarlo**.

## Validación

`AttachmentViewerChecks` cubre clasificación, audios con metadatos antiguos, rechazo de HTML disfrazado, aislamiento entre cuentas, rangos 206, HEAD, rangos inválidos 416, descarga original y renderizado seguro. Las pruebas existentes verifican conversión HEIC, orientación, preservación de originales y borrado coherente.

Se verificaron manualmente con archivos de prueba: galería HEIC/PNG, zoom y teclado, audio WAV y video MP4 en reproducción, cierre sin medios activos, PDF de dos páginas y texto, documentos y formato no soportado. Se comprobaron las tres superficies y anchos de 320, 390 y 430 píxeles, además de orientación horizontal y escritorio. Estas pruebas responsivas se realizaron en Chromium; no equivalen a una ejecución en Safari físico.

PDF.js usa la API de documento, páginas y canvas descrita en sus [ejemplos oficiales](https://mozilla.github.io/pdf.js/examples/). La versión y procedencia de los archivos están en `wwwroot/vendor/pdfjs/README.md`.
