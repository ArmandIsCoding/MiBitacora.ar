# HEIC/HEIF con vista JPG local

Cada foto conserva su archivo original en `uploads/<cuenta>/<id>.bin`. La aplicación genera una copia `uploads/<cuenta>/<id>.jpg` para mostrarla en el navegador y enviarla a Ollama. Ambas representan el mismo adjunto: no se duplican entradas ni fotos en la página y la descarga sigue entregando el HEIC original.

La conversión se ejecuta dentro del proceso .NET con Magick.NET 15.1.0, incluido como dependencia del proyecto. No necesita Python, aplicaciones de conversión ni servicios externos durante el uso normal. [Magick.NET documenta su instalación y variantes](https://github.com/dlemstra/Magick.NET/blob/main/docs/Readme.md); el proyecto usa Q8 y probó la lectura de HEIC en Windows.

## Calidad y privacidad

- Se lee la imagen principal, se corrige la orientación y se convierte su perfil de color a sRGB cuando existe.
- El JPG tiene calidad 90 y hasta 2048 píxeles en su lado mayor. No se agrandan imágenes pequeñas. El original mantiene su resolución, formato y metadatos completos.
- La copia no incluye EXIF, GPS ni XMP. El procesamiento aplica la orientación **antes** de quitar esos datos, como indica [la documentación de auto-orient](https://imagemagick.org/command-line-options/#auto-orient).
- La conversión limita dimensiones, píxeles y memoria de caché; no usa caché de píxeles en disco. Se procesa una foto por vez dentro del servidor.
- Las copias quedan fuera de `wwwroot`, de Git y de la publicación. La ruta `/adjuntos/{id}/vista` verifica al propietario antes de leer o convertir una foto y responde con `image/jpeg` y `private, no-store`.

Los JPG se generan al solicitar una vista previa o incluir una foto en el contexto de IA. Una copia existente se reutiliza. Si la conversión falla, el original sigue disponible y la imagen no se envía al modelo como si hubiera sido leída correctamente. Las copias se escriben mediante un archivo temporal y un cambio de nombre al terminar; una conversión y un borrado comparten el mismo bloqueo para evitar archivos huérfanos por carreras entre solicitudes.

## Preparar todas las fotos importadas

Con la instancia web detenida, ejecutá desde la carpeta del proyecto:

```powershell
dotnet run --project MiBitacora.csproj --no-launch-profile -- --prepare-local-images
```

El comando aplica las migraciones pendientes, prepara o reutiliza los JPG y finaliza con código distinto de cero si falta alguna conversión. No inicia la web ni genera devoluciones de IA. Después iniciá la aplicación normalmente:

```powershell
dotnet run --launch-profile http
```

## Mantenimiento y pruebas

Los originales son la fuente de verdad. Los JPG pueden volver a generarse; no hay columnas BLOB ni duplicación de adjuntos en SQLite. Al borrar una entrada o una cuenta, se eliminan ambas versiones. El arranque limpia copias y temporales huérfanos. Las cuotas de cuenta se calculan sobre los originales; reservá espacio adicional para sus copias de visualización.

```powershell
dotnet run --project tests/MiBitacora.Checks
dotnet run --project tests/MiBitacora.Checks -- --live-heic
```

Las pruebas usan una imagen HEIC sintética con dos colores y orientación EXIF, creada para este repositorio. Verifican orientación, colores, ausencia de EXIF en el JPG, conservación exacta del original, solicitudes concurrentes, permisos, errores de conversión, limpieza y envío del JPEG a Ollama. `--live-heic` pide al modelo local que reconozca los colores de esa imagen; no lee fotografías personales.
