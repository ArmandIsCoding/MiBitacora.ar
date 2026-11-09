# Archivos locales por cuenta

Los originales se guardan bajo la raíz de contenido de la aplicación:

```text
C:\Dev\MiBitacora\
  App_Data\mi-bitacora.db
  uploads\
    d2aceee6-64e8-4cb4-89b1-31797ef10478\
      <UUID del adjunto>.bin
```

La carpeta de cada cuenta se crea cuando recibe su primer archivo. El nombre físico se genera en el servidor; el nombre original, tipo de contenido, tamaño, orden y texto extraído se conservan en SQLite. Dos archivos con el mismo nombre pueden convivir. `.bin` conserva los bytes originales, sin convertirlos: las descargas recuperan el nombre y el tipo apropiados desde los metadatos.

`uploads` está fuera de `wwwroot`: no es una URL pública ni debe mapearse como contenido estático en IIS o un proxy. Las descargas y vistas previas siguen pasando por `/adjuntos/{id}` y `/adjuntos/{id}/vista`, con autenticación, comprobación de propietario y `private, no-store`. Los archivos se transmiten desde disco; solo se cargan en memoria las imágenes seleccionadas para el contexto de Ollama. El servicio rechaza rutas de cuenta que no sean UUID y enlaces simbólicos en las carpetas de uploads.

## Límites y borrado

Se quitó el máximo de cuatro archivos tanto del selector como del servidor. Se puede elegir más de diez archivos en una selección. Siguen vigentes los límites de 8 MiB por archivo y 16 MiB por entrada. La configuración incluida permite 1 GiB o 2000 archivos por cuenta, mediante `Journal:MaxAccountAttachmentBytes` y `Journal:MaxAccountFiles`; sin esa configuración, los valores por defecto son 80 MiB y 250 archivos. Son límites de almacenamiento, independientes de las dos imágenes que el modelo puede analizar por devolución. El importador administrativo de Apple conserva originales que superen los límites del selector; no recorta ni comprime los archivos históricos.

Una entrada se confirma en SQLite después de escribir todos sus originales. Si una escritura falla, se revierte la entrada y se eliminan los archivos recién creados. Al borrar una entrada se borran sus originales; al borrar una cuenta se elimina su carpeta. Las escrituras y los borrados se serializan dentro de la instancia del servidor para evitar carreras entre pestañas y sesiones.

Si Windows impide un borrado por permisos o bloqueo de archivo, se registra un error. Al siguiente arranque se reintenta la limpieza de archivos sin registro en la base y de escrituras temporales interrumpidas. Revisá los permisos y reiniciá después de resolver el problema. Ejecutá una sola instancia de la aplicación contra el mismo par de base y carpeta de uploads.

## Actualizar una instalación con BLOBs

1. Detené la versión anterior y respaldá `App_Data` y `uploads` si existe.
2. Arrancá la nueva aplicación normalmente. Antes de aplicar la migración `StoreUploadsOnDisk`, exporta los BLOBs a la carpeta de cada cuenta y verifica cualquier archivo que ya se haya copiado. No cambia los códigos de cuenta ni requiere resetear la base.
3. Cada copia completada vacía su BLOB como punto de reanudación. Si se interrumpe el proceso, conservá **ambas carpetas** y volvé a arrancar. La aplicación verifica tamaño y contenido antes de continuar; no sobrescribe originales existentes.
4. La migración elimina la columna `Content` y el arranque compacta SQLite con `VACUUM`. Un `dotnet ef database update` directo se rechaza si todavía hay BLOBs sin exportar: usá el arranque de la aplicación para esta actualización.

No se admite volver a la versión BLOB con `database update` hacia atrás. Para revertir, restaurá el respaldo anterior junto con el código anterior. Un respaldo anterior puede contener originales dentro de SQLite; protegelo igual que la carpeta de uploads.

## Despliegue y respaldo

Git y `dotnet publish` excluyen `App_Data` y `uploads`. Asegurá que la cuenta del proceso tenga permisos de lectura, escritura y borrado en ambas carpetas. Conservá las carpetas cuando reemplaces los binarios de la aplicación. No las sincronices con servicios cloud si el despliegue debe permanecer on-premise.

Para un respaldo consistente, detené la aplicación y copiá **la base junto con los uploads de ese mismo momento**. Restaurá ambas juntas antes de arrancar: la limpieza inicial usa los metadatos de SQLite para identificar los archivos válidos. Las copias contienen información privada, incluido texto extraído; aplicales los mismos controles de acceso que al servidor.

## Comprobaciones

```powershell
dotnet run --project tests\MiBitacora.Checks
```

Las pruebas usan una base y una carpeta temporales. Cubren migración reanudable desde BLOBs, descarga idéntica al original, doce adjuntos en una entrada, separación entre cuentas, rutas manipuladas, límites de tamaño, errores de disco, limpieza tras interrupciones y borrado de entradas y cuentas.
