# PDF.js vendorizado

- Paquete oficial `pdfjs-dist`, versión **6.5.438**, distribución `legacy` para mayor compatibilidad de navegadores.
- Descarga: https://registry.npmjs.org/pdfjs-dist/-/pdfjs-dist-6.5.438.tgz
- Integridad npm SHA-512 verificada antes de extraer: `vg8Pw1x+AFKy4AMDvhuWl5cOYV8d698UoEANoFFhc79hqXDnZQqnOXoxw3HG907WHOg1JYOvLhP756boddFxpA==`.
- Fuentes: https://github.com/mozilla/pdf.js y https://mozilla.github.io/pdf.js/getting_started/.
- Licencia Apache 2.0 en `6.5.438/LICENSE`; fuentes y decodificadores conservan sus licencias incluidas.

Se distribuyen `legacy/build/pdf.min.mjs`, `legacy/build/pdf.worker.min.mjs`, `cmaps/`, `standard_fonts/` y `wasm/`. No se incluyen mapas de código ni el visor completo. `attachment-pdf.js` implementa los controles propios sin ejecutar JavaScript del documento, activar XFA, abrir sus enlaces o cargar dependencias desde CDNs.

Para actualizar, descargar una versión oficial, comprobar su integridad publicada, copiar los mismos archivos bajo una carpeta con la nueva versión y cambiar ambas referencias en `attachment-pdf.js`. Repetir las comprobaciones de PDF multipágina, texto, zoom, cierre durante carga y vista móvil. Mantener la versión fijada; no usar una URL `latest` en producción.
