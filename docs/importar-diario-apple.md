# Importar Diario de Apple

El importador de `tools/apple-journal-import/import_journal.py` trabaja localmente con la exportación HTML de Diario. Requiere Python 3.11 o posterior y usa solo su biblioteca estándar. No llama a Ollama ni a servicios externos.

## Revisar la exportación

La carpeta debe contener `index.html`, `Entries` y `Resources`. También se acepta la carpeta exterior si contiene otra llamada `AppleJournalEntries`. Se ignora `__MACOSX` del ZIP.

```powershell
python tools/apple-journal-import/import_journal.py 'C:\Exportaciones\AppleJournalEntries'
```

Sin `--replace` solo informa cantidades, fechas y tamaños. Comprueba que el índice incluya todas las entradas, normaliza nombres Unicode y resuelve el caso de nombres UTF-8 extraídos como CP437/CP850 en Windows. Lee el HTML de Cocoa por eventos, porque sus etiquetas de párrafo pueden estar mal anidadas.

Conserva el título, el cuerpo, los saltos de párrafo, emojis, enlaces y las leyendas de recursos. El formato visual original se conserva en la copia completa de la exportación. Asocia fotos, videos y archivos complementarios mediante las referencias HTML y JSON; bloquea el reemplazo si queda algún archivo multimedia sin una entrada identificada o si falta un archivo referenciado.

Detecta el formato HEIC por las marcas de formato del archivo, incluso cuando Apple lo exporta con nombre `.jpeg`, `.jpg` o `.png`. Conserva el nombre y los bytes originales, pero registra el tipo real para que la aplicación genere la vista JPG y pueda enviarla al modelo local. No hace falta volver a importar todo el cuaderno para corregir metadatos de archivos ya guardados.

## Reemplazar el cuaderno de una cuenta

1. Actualizá la aplicación para aplicar la migración `AddEntryDatePrecision` y detenela. No ejecutes el importador mientras otra instancia escribe en esa base o carpeta de uploads.
2. Elegí el código de cuenta existente. El reemplazo borra las entradas, devoluciones, adjuntos y notitas anteriores de **esa cuenta**. Conserva credenciales, perfil y datos de otras cuentas.
3. Ejecutá indicando rutas absolutas:

```powershell
python tools/apple-journal-import/import_journal.py 'C:\Exportaciones\AppleJournalEntries' --replace --database 'C:\Dev\MiBitacora\App_Data\mi-bitacora.db' --uploads 'C:\Dev\MiBitacora\uploads' --account '<código de cuenta>' --backups 'C:\Dev\MiBitacora\App_Data\imports\apple-journal'
```

Antes de reemplazar datos, crea una carpeta fechada con una copia consistente de SQLite, los uploads anteriores y la exportación completa (`apple-export`). Los JSON de Apple se conservan allí junto con sus fotos y HTML, sin llenar el listado de adjuntos de la página con archivos auxiliares. El resultado incluye la ubicación del respaldo.

Los cambios de SQLite se hacen en una transacción. Cada original se copia a `uploads/<cuenta>/<UUID>.bin` y se verifica mediante SHA-256. Si falla una copia antes de confirmar, se revierten los registros y se eliminan los archivos recién escritos. Los originales anteriores se borran solo después de confirmar la importación. La carpeta de respaldo contiene `manifest.json` con la correspondencia entre fuentes, entradas, adjuntos y huellas de verificación.

El importador no aplica los límites de 8 MiB por archivo y 16 MiB por entrada del selector: debe conservar todo el histórico. Configurá una cuota de cuenta suficiente para el material importado y los futuros uploads; `appsettings.json` incluye 1 GiB y 2000 archivos. No se generan devoluciones de IA durante el proceso.

## Fechas y formatos

La exportación HTML contiene el día, pero no una hora de creación de la entrada. Se conserva el día y se marca `IsDateOnly`. Internamente se usa el mediodía de Buenos Aires (UTC−03:00), con diferencias de microsegundos para ordenar entradas del mismo día según el índice; esa hora técnica no se muestra como hora original. El calendario y la paginación siguen funcionando.

Los archivos originales se conservan sin recomprimir ni modificar. JPEG y PNG pueden verse y usarse con la IA local. Para HEIC/HEIF, la aplicación prepara copias JPG al abrir la vista previa o pedir una devolución. También podés [preparar todos los JPG de antemano](heic-jpg.md) después de importar. Videos y otros formatos no admitidos por el análisis quedan disponibles como originales descargables.

## Restaurar y verificar

Para volver al estado previo, detené la aplicación y restaurá **la base y la carpeta `uploads` del mismo respaldo** antes de arrancar. El respaldo contiene datos privados; debe permanecer fuera de Git, de `wwwroot` y de servicios de sincronización si el despliegue es on-premise. El importador, sus pruebas sintéticas y esta guía pueden versionarse; los datos y manifiestos personales quedan bajo `App_Data`.

```powershell
python -m unittest discover -s tools/apple-journal-import -v
dotnet run --project tests/MiBitacora.Checks
```

Las pruebas cubren texto y enlaces, HTML de Cocoa, nombres dañados por codificación, archivos complementarios, respaldo, rollback ante errores de disco, aislamiento entre cuentas, rutas inválidas, fechas sin hora y cuotas configurables.
