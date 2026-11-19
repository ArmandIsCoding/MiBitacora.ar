# Compartir para terapia

Disponible en los planes gratis y Profundo desde **Compartir para terapia** en el menú del cuaderno, el mapa o el informe. Permite preparar una versión fija y completa del recorrido, revisarla en privado y crear un enlace de lectura sin acceso a la cuenta.

## Uso

1. Elegir un título y **Preparar vista previa completa**. No publica nada ni consume créditos.
2. Revisar las pestañas **Mapa de ideas** e **Informe**. La copia incluye todas las entradas de todos los diarios, sus devoluciones guardadas, notitas, contexto personal y textos disponibles de documentos; no aplica el límite de 120 entradas del mapa de trabajo. No incluye archivos originales ni enlaces privados para descargarlos.
3. Opcionalmente, **Preparar / actualizar informe con Brisa** para incorporar el informe integral en diez apartados. Utiliza el proveedor y los créditos de informe ya configurados para la cuenta; no cambia de proveedor. Se puede cancelar. Si no hay un informe vigente, se presentan los registros y las devoluciones existentes sin inventar una síntesis nueva.
4. Elegir vencimiento, aceptar expresamente el contenido revisado y crear el enlace. Se ofrecen accesos directos al mapa y al informe de la misma versión, para compartir juntos o abrir en pestañas separadas. Quien tenga cualquiera de los enlaces puede navegar ambas pestañas.
5. **Preparar actualización manual** conserva la dirección del enlace y su vencimiento, salvo que se elija otro. Requiere otra vista previa y confirmación. Una entrada nueva, una edición o un borrado del cuaderno no alteran una versión ya publicada.
6. **Revocar enlace** bloquea nuevas aperturas y elimina el contenido de esa copia en el servidor. Las vistas abiertas comprueban disponibilidad y versión cada 30 segundos y antes de abrir registros o imprimir. No permite retirar capturas, impresiones o descargas realizadas por quien tenía acceso.

Hay hasta cinco enlaces activos por cuenta, con vencimientos de 7, 30, 90 o 365 días, o sin vencimiento hasta revocarlos. Para publicar una actualización debe seguir activo el enlace. Un contenido fuente de más de 24 MiB o una copia final de más de 24 MiB se rechaza con un mensaje; nunca se publica una versión truncada silenciosamente.

## Qué se puede recorrer

El mapa ocupa el espacio disponible bajo el encabezado de la marca y conserva búsqueda, zoom, foco, filtros visuales y pantalla completa. Las entradas y preguntas enlazan a un visor de su registro original y su devolución, dentro de la copia compartida.

El informe muestra actividad de escritura por mes, temas y conexiones, preguntas, material marcado para sesión, registros originales consultables y el informe integral vigente con sus fuentes. Las cantidades describen registros, no bienestar. Las lecturas de Brisa son hipótesis para revisar con la persona y su terapeuta: no son un diagnóstico, una evaluación clínica ni una medición del estado mental actual. Un informe integral anterior desactualizado queda fuera de la copia; las lecturas guardadas siguen disponibles y fechadas.

**Guardar / imprimir PDF** prepara la pestaña Informe, expande sus fuentes y oculta controles privados y enlaces de administración al imprimir. El PDF se genera con la función de impresión del navegador. El mapa se comparte como experiencia navegable; la impresión no exporta su lienzo interactivo.

## Privacidad y operación

- El enlace es un acceso público por posesión, sin autenticación ni contraseña adicional: sólo debe enviarse a personas de confianza. No se crea ni envía ningún enlace automáticamente.
- La clave aleatoria de 256 bits viaja en el fragmento `#` del enlace, no en rutas, consultas HTTP, referencias a otros sitios ni registros de acceso web. Se valida su formato y se busca por SHA-256. No debe pegarse en incidencias, capturas públicas o Git.
- El contenido, el título y la clave recuperable se protegen mediante ASP.NET Core Data Protection con propósitos separados por cuenta y copia. Es necesario preservar las claves privadas de Data Protection junto a la base al respaldar o migrar el servidor.
- La página pública inicial no incluye el contenido compartido. Se carga después de leer el fragmento en el navegador. Usa `private, no-store`, `noindex, nofollow, noarchive`, política sin referencias y protección frente a embebido. Esto no impide que un destinatario conserve una copia.
- El servidor vuelve a construir y comparar la vista previa antes de publicar. Si cambió el cuaderno, exige revisarla de nuevo; no acepta un contenido enviado por el cliente. Las actualizaciones utilizan una revisión para evitar sobrescribir cambios concurrentes.
- Cada registro público usa identificadores propios de la copia y citas verificadas contra sus fuentes. No expone identificadores internos de cuentas, entradas, adjuntos, rutas de almacenamiento ni antecedentes de otra cuenta.
- Vencimiento y revocación se comprueban en el servidor. Revocar borra el contenido protegido y la clave recuperable; al publicar se limpian también las copias vencidas de esa cuenta. Borrar la cuenta elimina todas sus copias mediante clave foránea con cascada.
- La migración `20261119195428_AddTherapyShares` agrega una tabla; no modifica los registros del cuaderno. No requiere nuevas claves de proveedores, pagos ni cambios de configuración de IA.

## Verificación

Las comprobaciones usan cuentas, textos y documentos sintéticos: aislamiento entre cuentas, mapa de más de 120 entradas, evidencia propia, consentimiento, cambios entre revisión y publicación, protección de datos, copia fija, actualización con el mismo enlace, concurrencia, vencimiento, revocación, límites y borrado de cuenta.

```powershell
dotnet build tests/MiBitacora.Checks -c Release
dotnet tests/MiBitacora.Checks/bin/Release/net10.0/MiBitacora.Checks.dll --sharing-only
node --test tests/therapy-share.test.mjs tests/thought-map.test.mjs tests/thought-map-fullscreen.test.mjs
```

La prueba de navegador debe usar una base aislada y datos ficticios, sin publicar contenido real. Para la instalación actual se conserva OpenAI y el análisis local de adjuntos desactivado.
