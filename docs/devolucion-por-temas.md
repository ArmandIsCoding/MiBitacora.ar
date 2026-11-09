# Devolución por temas

La pestaña **Devolución** reúne una devolución por cada tema detectado automáticamente en el texto del cuaderno. **Generar devolución** hace el primer análisis; **Actualizar devolución** vuelve a recorrer el contenido y reemplaza la versión guardada cuando termina correctamente. Abrir la pestaña solo consulta el resultado: no dispara inferencias.

Cada tarjeta muestra una síntesis, preguntas para seguir escribiendo y fragmentos originales con fecha y fuente. Los enlaces del índice desplazan la vista a la tarjeta correspondiente sin salir de Devolución. La detección y las interpretaciones son aproximaciones del modelo que la persona puede revisar; no son diagnósticos.

## Alcance de esta versión

- Usa todas las entradas de la cuenta, sin depender de las 30 entradas cargadas en Conversación, y el texto que ya se pudo extraer de sus documentos. No vuelve a extraer archivos ni hace OCR. No usa notitas, contexto personal ni devoluciones anteriores como evidencia.
- Las fotos, videos y adjuntos sin texto extraído no participan. La pantalla informa cuántas entradas tienen texto y cuántos documentos y adjuntos sin texto hay. Las imágenes siguen disponibles en la devolución individual.
- El procesamiento usa el proveedor configurado en `AI:Provider`: Ollama local u OpenAI. Comparte la cola de inferencia con las devoluciones individuales. Con OpenAI se envían los textos a su API; ver [configuración y privacidad](openai.md).
- El botón trabaja a demanda y puede tardar varios minutos. Se puede recorrer el resto del cuaderno mientras trabaja. Cancelar o cerrar la página detiene el trabajo asociado al componente; no hay un trabajo durable ni un proceso programado todavía. Una desconexión breve puede conservar el circuito hasta su expiración.
- Se conserva la última versión completa. Un error o cancelación no publica resultados parciales. Si cambia el cuaderno durante la generación, se pide volver a generarla. Entradas nuevas posteriores marcan la versión como desactualizada.
- Eliminar una entrada retira la devolución general para no conservar fragmentos borrados. La importación con reemplazo hace lo mismo para esa cuenta. Eliminar la cuenta elimina sus devoluciones en cascada. La descarga de datos personales incluye la devolución guardada y su vigencia.

## Implementación y prompts

`Services/JournalAiTopics.cs` contiene las instrucciones y los esquemas JSON para detectar temas, resumir evidencia extensa y escribir cada devolución. Los identificadores permitidos de temas y fuentes se incorporan al esquema de cada solicitud. Se verifican las referencias y las citas contra las fuentes originales; una cita que no es literal se reemplaza por un fragmento real de esa fuente. Los registros no incluyen textos ni respuestas.

`JournalTopicService.GenerateAsync(userId, progress, cancellationToken)` concentra el proceso y puede invocarse desde un futuro worker con un scope de servicios, sin depender de HTTP ni de Blazor. `JournalTopics.razor` aporta la interfaz. `JournalTopicJobs` evita dos generaciones simultáneas de la misma cuenta dentro del proceso.

Los textos se dividen en fragmentos contiguos de hasta 2400 caracteres y lotes de hasta 6500 caracteres y cuatro fuentes. Cada lote detecta hasta seis temas relevantes y recibe el catálogo construido hasta ese momento para reutilizar temas existentes. No se recorta el final del cuaderno. Si el catálogo supera los 6500 caracteres, se interrumpe explícitamente y se conserva la versión anterior; ese caso requiere ampliar la estrategia de agrupación antes de escalar el piloto. El modelo puede separar asuntos similares o pasar por alto un tema: la detección no es una clasificación exhaustiva garantizada.

Las observaciones de cada tema se sintetizan por partes cuando exceden el contexto, conservando todas las observaciones como entrada a esa etapa. Después se genera la devolución final. Los fragmentos originales de apoyo se guardan por separado de los resúmenes del modelo.

La tabla `JournalTopicOverviews` guarda un JSON por cuenta y una huella SHA-256 del contenido fuente. El guardado compara nuevamente esa huella dentro de una transacción antes de reemplazar el resultado. La migración agrega la tabla sin modificar entradas ni archivos. Los originales siguen en `uploads/<cuenta>`.

## Verificación

```powershell
dotnet build
dotnet run --project tests/MiBitacora.Checks
dotnet run --project tests/MiBitacora.Checks -- --live-topics
```

Las comprobaciones usan una base temporal y datos sintéticos. Cubren lectura de más de una página, textos largos sin recortes, documentos, síntesis intermedia, persistencia, aislamiento por cuenta, citas, errores, cancelación, concurrencia, invalidación al borrar y renderizado de tarjetas con HTML escapado. También renderizan la página Bitácora completa para comprobar que entrega al componente de temas el identificador de la cuenta autenticada, no un texto literal. `--live-topics` agrega llamadas reales a Ollama con textos inventados; no lee el cuaderno del usuario.
