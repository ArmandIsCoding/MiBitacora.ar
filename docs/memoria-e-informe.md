# Memoria del cuaderno e informe para terapia

La conversación mantiene el foco en el mensaje actual y puede recuperar antecedentes de cualquier fecha anterior. La pestaña **Devolución** conserva **Actualizar devolución**, que prepara un único documento integrado para conversar en terapia. Las respuestas de cada mensaje siguen en **Conversación**.

## Memoria con procedencia

`JournalCorpusReader` lee originales de la cuenta: entradas, texto extraído de documentos, notitas y perfil. Divide textos extensos en fragmentos contiguos de hasta 2400 caracteres, sin descartar el final. No incorpora respuestas de la IA como evidencia autobiográfica. No hace OCR ni interpreta imágenes para el índice.

La conversación usa la [memoria vectorial local](memoria-vectorial.md): `VectorMemoryService` guarda fragmentos y embeddings de EmbeddingGemma en SQLite, con búsqueda híbrida por significado y palabras. La indexación es incremental y se ejecuta en segundo plano, también sobre el historial existente. Usa el mismo índice en los planes gratuito y pago. No necesita la API de embeddings de OpenAI ni una base externa.

La consulta incorpora el mensaje actual y la conversación inmediata para interpretar respuestas cortas y se reutiliza si coincide su hash. La búsqueda filtra cuenta y fecha antes de leer candidatos, y recupera los fragmentos **originales**. No hay llamadas generativas para resumir ni seleccionar recuerdos al pedir una respuesta: se hace una única generación final, aparte de transcribir audios pendientes de la entrada elegida. Si el índice está incompleto o temporalmente fuera de servicio, se usan coincidencias de texto y turnos recientes y el aviso queda junto a la respuesta. No hay garantía de recuperar todo detalle pertinente. **Antecedentes consultados** permite revisar las fuentes disponibles, sin afirmar que cada una influyó.

Con OpenAI se incluyen hasta seis turnos anteriores: hasta 12.000 caracteres de los dos más recientes y 1500 de los cuatro siguientes, junto con una porción acotada de sus respuestas y preguntas. Se seleccionan hasta ocho antecedentes adicionales. El presupuesto total de fuentes es de 76.000 caracteres, priorizando mensaje e hilo reciente. Con Ollama el presupuesto sigue en 6500 caracteres, con dos turnos recientes y hasta tres antecedentes más breves. Las imágenes conservan el máximo de dos compatibles por respuesta. No es una memoria ilimitada ni un contexto completo de todos los originales en cada llamada.

Cada fuente se filtra por cuenta. Las entradas futuras y las notitas posteriores no entran al responder una entrada histórica; el perfil actual se omite para días históricos. Triggers retiran los fragmentos obsoletos al editar o borrar; cada lote verifica la revisión antes de guardar. La devolución comprueba atómicamente esa misma revisión al guardarse, de modo que un cambio concurrente conserva la anterior. Las claves foráneas eliminan el índice al borrar fuentes o cuentas. Los respaldos privados siguen teniendo su propia política de conservación.

La migración `AddVectorMemory` agrega catálogo, vectores, consultas, cola persistente y FTS5; no modifica textos, archivos ni respuestas guardadas. El índice contiene datos privados y debe protegerse igual que la base. `JournalMemories`, agregado por `AddJournalMemory`, conserva las síntesis generativas usadas por el informe completo, diferenciadas por proveedor. `JournalMemoryService.PrepareAsync` y `SelectAsync` mantienen ese flujo; la web delega la búsqueda de conversación a `VectorMemoryService`.

## Un informe integrado

`JournalReportService` prepara diez apartados: momento reciente; recorrido temporal; emociones y necesidades; conexiones posibles; repeticiones y cambios; vínculos y entorno; recursos; prioridades para terapia; preguntas; información pendiente.

Primero procesa el índice de todo el corpus y planifica las fuentes pertinentes para cada apartado. Luego vuelve a los originales y redacta cada sección por separado, con hasta catorce fragmentos por sección en OpenAI (cuatro en Ollama). La selección y las síntesis pueden perder matices; el informe informa alcance, fechas y adjuntos no leídos. No hay una tarjeta por cada mensaje, ni se incorpora indiscriminadamente la misma notita en todos los apartados.

Cada sección recibe también un esquema acotado de los apartados ya redactados para aportar una perspectiva diferente y evitar repetir relatos y preguntas. Ese contexto editorial se identifica como borrador de IA, nunca como evidencia. Las preguntas explícitas se concentran en prioridades, preguntas e incertidumbres; el aviso sobre el alcance del informe queda en su portada.

Los puntos se distinguen como registro, hipótesis, propuesta o límite. Cada afirmación personal requiere referencias; las citas se validan literalmente contra originales. Una referencia o cita inválida impide publicar el informe. Las conexiones no se presentan como causas demostradas. No se generan diagnósticos, puntuaciones clínicas, examen mental, tratamientos ni una evaluación automática de riesgo. El panorama reciente indica cuántas entradas con texto hay en los últimos 30 días, sin deducir el estado actual a partir de registros antiguos. El texto conserva la primera persona para leer y matizar con un terapeuta.

El documento completo se guarda en el JSON de `JournalTopicOverviews`, campo opcional `Report`, sólo después de verificar de nuevo la huella del corpus en una transacción. Un fallo, cancelación o cambio concurrente conserva el anterior. La exportación de datos personales incluye el informe mediante ese mismo estado. Las síntesis antiguas siguen siendo legibles, plegadas, hasta actualizar.

`TherapyReportView` presenta portada, alcance, índice, apartados numerados y citas enlazadas. `/informe` ofrece una vista autenticada para imprimir o guardar como PDF con las fuentes desplegadas y estilos A4. Abrir o imprimir no llama a la IA. Tanto `/informe` como `/entrada/{id}` tienen respuesta `private, no-store`.

## Preparar un informe desde mantenimiento

Se requiere autorización para procesar los textos con OpenAI y consumir créditos. Usar la instalación real con su configuración privada; no copiar una base de desarrollo a producción.

```powershell
dotnet build tools/MiBitacora.Report -p:UseAppHost=false
# Respaldo consistente de SQLite antes de publicar una migración:
dotnet tools/MiBitacora.Report/bin/Debug/net10.0/MiBitacora.Report.dll --root 'C:\ruta\MiBitacora' --user '<código de cuenta>' --backup-only
# Publicar/iniciar la web actualizada para aplicar su migración; luego:
dotnet tools/MiBitacora.Report/bin/Debug/net10.0/MiBitacora.Report.dll --root 'C:\ruta\MiBitacora' --user '<código de cuenta>'
```

El comando comprueba la cuenta y la base existente. Lee la clave de `App_Data/ai.settings.json` sin imprimirla. Respalda el informe anterior en `App_Data/report-backups`, prepara el índice y el nuevo documento e informa su vigencia. No reescribe respuestas de conversación ni originales. No hay programación automática; el botón de la web y el comando son explícitos.

## Pruebas

Las comprobaciones incluyen un antecedente de nueve meses fuera de los turnos recientes; continuidad de una entrada larga y su pregunta; exclusión de otras cuentas, fechas futuras y afirmaciones de la IA; reutilización e invalidación del índice; borrado en cascada; cambios concurrentes; diez apartados integrados; citas inválidas; HTML escapado; errores, cancelación y conservación de la versión anterior. La evaluación real usa un cuaderno ficticio, una consulta semántica y el informe completo antes de aplicarlo al cuaderno del usuario.

La memoria se administra en el servidor propio y las solicitudes mantienen `store: false`. La documentación oficial distingue la [gestión manual del contexto](https://developers.openai.com/api/docs/guides/conversation-state) del estado persistido por el proveedor; los [esquemas estructurados](https://developers.openai.com/api/docs/guides/structured-outputs) ayudan a validar el formato, pero no garantizan la veracidad de las interpretaciones.
