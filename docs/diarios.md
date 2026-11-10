# Diarios dentro de Conversación

**Mis diarios** permite crear y buscar diarios por nombre, abrir su conversación y renombrarlos. No hay un cupo de diarios. El icono de libro junto al editor abre el mismo selector cuando estás al final del chat; el selector del encabezado se desplaza con la página. En el teléfono, tocar **Conversación** también vuelve al encabezado.

Las entradas existentes e importadas quedan en **Mi diario**, el diario inicial permanente de cada cuenta. No se clasifican automáticamente. El botón de libro de cada entrada permite moverla a otro diario, junto con sus adjuntos, respuesta y marca para terapia. Mantiene su identificador y fecha originales, así que los enlaces desde el informe siguen funcionando.

**Quitar diario** pide confirmación y pasa sus entradas a Mi diario. No elimina mensajes ni archivos. El diario inicial no se renombra ni se quita. Los nombres propios admiten hasta 80 caracteres y deben distinguirse de los demás diarios de esa cuenta, sin diferencias sólo de mayúsculas o espacios.

Cada diario conserva su borrador y los adjuntos todavía sin enviar mientras se cambia de conversación en la misma pestaña. Son borradores en memoria de la sesión, no un guardado automático: recargar o cerrar la página puede perderlos. Si se quita un diario con un borrador, se conserva en Mi diario; si ya había otro allí, se reúnen ambos para que el usuario pueda revisarlos antes de enviar. Siguen vigentes los límites por entrada.

La URL `/bitacora?diario=<id>` vuelve al diario seleccionado al recargar. Se valida que pertenezca a la cuenta autenticada. Un diario inexistente o ajeno abre Mi diario con un aviso, sin revelar datos de otra cuenta.

## Continuidad y memoria

Los turnos recientes y las preguntas anteriores de la IA proceden del **mismo diario** que el mensaje actual. El nombre del diario se envía como etiqueta de organización, no como instrucción. La recuperación semántica sigue recorriendo los textos originales de **todos los diarios de esa cuenta**, incluso antecedentes antiguos. La IA sólo debe relacionarlos cuando sean pertinentes.

El corpus, sus identificadores de fuentes y sus huellas no dependen de la organización en diarios. Mover una entrada o renombrar un diario no reindexa textos intactos ni invalida el informe para terapia. No genera llamadas a la IA. El calendario, la racha, las notitas, el contexto del perfil, Para retomar, el informe y la exportación de datos continúan siendo del cuaderno completo.

## Datos y despliegue

`20261110041051_AddJournalNotebooks` agrega `JournalNotebooks` y `JournalEntries.NotebookId` nullable. `null` representa Mi diario: los registros previos y el importador conservan sus valores, sin reescritura de textos, respuestas o archivos. La clave foránea compuesta por cuenta y diario impide asociaciones entre usuarios también en SQLite. La eliminación de un diario mueve sus entradas en una transacción; el borrado de cuenta incluye los diarios.

Antes de publicar, respaldar SQLite de forma consistente con su API de backup o con la aplicación detenida y todos sus archivos auxiliares. El arranque aplica la migración. No reemplazar App_Data, uploads ni la configuración privada con los de desarrollo. El índice por cuenta, diario y fecha sostiene la paginación independiente; `GetEntriesAsync` sigue devolviendo la vista global para exportaciones y herramientas existentes.

## Verificación

`NotebookChecks` prueba migración desde la versión anterior, conservación de respuestas, nombres, paginación entre hilos intercalados, aislamiento en el servicio y en la clave foránea, memoria entre diarios, informe global, reutilización del índice, movimiento, retirada de diarios y borrado de cuenta. Todas las pruebas usan bases temporales y respuestas simuladas, sin consumir API. La revisión visual usa una cuenta ficticia y comprueba creación, cambio, borradores, renombrado, búsqueda, movimiento, recarga y tamaños móviles.
