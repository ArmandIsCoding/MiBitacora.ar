# Devolución por temas

**Versión actual:** la pestaña genera un [informe integrado para terapia](memoria-e-informe.md) con diez apartados y vista para imprimir. Las síntesis por temas descritas debajo corresponden al formato anterior, que se conserva plegado hasta actualizar. El servicio anterior sigue disponible para compatibilidad y no es el destino del botón actual.

La pestaña **Devolución** reúne una devolución por cada tema detectado automáticamente en el texto del cuaderno. **Generar devolución** hace el primer análisis; **Actualizar devolución** vuelve a recorrer el contenido y reemplaza la versión guardada cuando termina correctamente. Abrir la pestaña solo consulta el resultado: no dispara inferencias.

Cada tarjeta muestra una síntesis, preguntas para seguir escribiendo y fragmentos originales con fecha y fuente. Los enlaces del índice desplazan la vista a la tarjeta correspondiente sin salir de Devolución. La detección y las interpretaciones son aproximaciones del modelo que la persona puede revisar; no son diagnósticos.

## Alcance de esta versión

- Usa todas las entradas de la cuenta, sin depender de las 30 entradas cargadas en Conversación, el texto extraído de sus documentos, todas las notitas y el contexto personal del perfil. No vuelve a extraer archivos ni hace OCR. Las devoluciones anteriores no son evidencia.
- Las notitas y el perfil son contexto transversal: se consideran en cada lote; cada devolución final recibe sólo el contexto vinculado como evidencia a ese tema para reconocer preocupaciones o emociones de fondo cuando resulten pertinentes. También pueden dar lugar a un tema sin entradas. No se les asigna una entrada ficticia: las fuentes distinguen entrada, documento, notita y perfil; este último no tiene fecha de episodio. Una frase como «siempre me pasa lo mismo, estoy cansado» puede sugerir frustración, pero no demuestra frecuencia ni permite atribuir enojo a todas las vivencias.
- El contexto extenso se sintetiza por partes, sin descartar notas ni recortar su texto original antes del análisis, hasta obtener un bloque de hasta 2400 caracteres para acompañar la detección; la lectura final recibe únicamente su contexto pertinente. La síntesis puede perder matices: la pantalla conserva fragmentos originales identificados como contexto adicional y separa las citas propias de cada tema. Repetir el mismo contexto en distintas solicitudes no cuenta como nuevos episodios.
- Las fotos, videos y adjuntos sin texto extraído no participan. La pantalla informa cuántas entradas tienen texto y cuántos documentos y adjuntos sin texto hay. Las imágenes siguen disponibles en la devolución individual.
- El procesamiento usa el proveedor configurado en `AI:Provider`: Ollama local u OpenAI. Comparte la cola de inferencia con las devoluciones individuales. Con OpenAI se envían los textos a su API; ver [configuración y privacidad](openai.md).
- El botón trabaja a demanda y puede tardar varios minutos. Se puede recorrer el resto del cuaderno mientras trabaja. Cancelar o cerrar la página detiene el trabajo asociado al componente; no hay un trabajo durable ni un proceso programado todavía. Una desconexión breve puede conservar el circuito hasta su expiración.
- Se conserva la última versión completa. Un error o cancelación no publica resultados parciales. Si cambia el cuaderno durante la generación, se pide volver a generarla. Entradas nuevas posteriores marcan la versión como desactualizada.
- Agregar o editar una notita o modificar el contexto del perfil marca la devolución como desactualizada. Marcar una nota para sesión o cambiar el nombre del perfil no altera la huella. Eliminar una entrada o una notita, o vaciar el contexto del perfil, retira la devolución general para no conservar fragmentos borrados. La importación con reemplazo hace lo mismo para esa cuenta. Eliminar la cuenta elimina sus devoluciones en cascada. La descarga de datos personales incluye la devolución guardada y su vigencia.

## Implementación y prompts

`Services/JournalAiTopics.cs` contiene las instrucciones y los esquemas JSON para detectar temas, resumir evidencia extensa y escribir cada devolución. Los identificadores permitidos de temas y fuentes se incorporan al esquema de cada solicitud. Se verifican las referencias y las citas contra las fuentes originales; una cita que no es literal se reemplaza por un fragmento real de esa fuente. Los registros no incluyen textos ni respuestas.

`Services/JournalAiVoice.cs` define la voz de las devoluciones temáticas, tanto con Ollama como con OpenAI: primera persona singular, español rioplatense y un tono íntimo, cálido y emocional, pensado para leer como propio o en terapia. También las preguntas y los posibles próximos pasos están en primera persona. Las interpretaciones siguen siendo tentativas; no se agregan sentimientos, recuerdos ni decisiones ausentes de las fuentes. Los documentos y las fotos no se convierten automáticamente en vivencias propias, y las citas conservan el texto original.

La detección de temas y la compresión de evidencia conservan su función de síntesis fiel, sin adoptar la voz de la devolución final. Cambiar el prompt no reescribe resultados guardados: **Actualizar devolución** genera nuevamente los temas con el tono actual; las devoluciones individuales ya guardadas se conservan.

El 9/11/2026 se verificó este tono con cinco llamadas reales a OpenAI usando datos ficticios: una entrada con sentimientos encontrados, una entrada neutra con una entrevista ajena y el recorrido de detección, compresión y devolución temática. Se revisó la primera persona en el texto final y las preguntas, la conservación de citas y la atribución de vivencias. También pasaron las 93 comprobaciones existentes del proyecto. El tono sigue siendo una instrucción al modelo, no una garantía determinista para cada generación.

`JournalTopicService.GenerateAsync(userId, progress, cancellationToken)` concentra el proceso y puede invocarse desde un futuro worker con un scope de servicios, sin depender de HTTP ni de Blazor. `JournalTopics.razor` aporta la interfaz. `JournalTopicJobs` evita dos generaciones simultáneas de la misma cuenta dentro del proceso.

Los textos se dividen en fragmentos contiguos de hasta 2400 caracteres y lotes de hasta 6500 caracteres y cuatro fuentes. Cada lote detecta hasta seis temas relevantes y recibe el catálogo construido hasta ese momento para reutilizar temas existentes. No se recorta el final del cuaderno. Si el catálogo supera los 6500 caracteres, se interrumpe explícitamente y se conserva la versión anterior; ese caso requiere ampliar la estrategia de agrupación antes de escalar el piloto. El modelo puede separar asuntos similares o pasar por alto un tema: la detección no es una clasificación exhaustiva garantizada.

Las observaciones de cada tema se sintetizan por partes cuando exceden el contexto, conservando todas las observaciones como entrada a esa etapa. Después se genera la devolución final. Los fragmentos originales de apoyo se guardan por separado de los resúmenes del modelo.

La tabla `JournalTopicOverviews` guarda un JSON por cuenta y una huella SHA-256 del contenido fuente, incluidas las notitas y el contexto personal. El guardado compara nuevamente esa huella dentro de una transacción antes de reemplazar el resultado. La versión 3 de la huella marca los resultados anteriores como desactualizados. Los nuevos campos de fuente y contexto son opcionales al leer JSON anterior: este cambio no necesita migración de base de datos. Los originales siguen en `uploads/<cuenta>`.

## Verificación

```powershell
dotnet build tests/MiBitacora.Checks -p:UseAppHost=false
dotnet tests/MiBitacora.Checks/bin/Debug/net10.0/MiBitacora.Checks.dll
dotnet tests/MiBitacora.Checks/bin/Debug/net10.0/MiBitacora.Checks.dll --live-topics
```

Las comprobaciones usan una base temporal y datos sintéticos. Cubren lectura de más de una página, textos largos sin recortes, documentos, síntesis intermedia, persistencia, aislamiento por cuenta, citas, errores, cancelación, concurrencia, invalidación al borrar y renderizado de tarjetas con HTML escapado. También renderizan la página Bitácora completa para comprobar que entrega al componente de temas el identificador de la cuenta autenticada, no un texto literal. `--live-topics` agrega llamadas reales a Ollama con textos inventados; no lee el cuaderno del usuario.

Las pruebas de contexto comprueban que llega a los lotes y sólo a las lecturas finales que lo citan, conserva la procedencia sin inflar el contador de entradas, permite analizar un cuaderno con solo notitas o perfil, procesa contexto extenso completo antes de sintetizarlo, detecta cambios concurrentes, elimina citas al borrar fuentes y sigue leyendo JSON de versiones anteriores.

El diálogo de Bitácora se monta solo cuando hay una acción abierta y se retira completo al cerrar, guardar o eliminar. Antes, cerrar el diálogo por JavaScript antes de limpiar el estado permitía que otro render lo reabriera vacío, dejando el fondo bloqueado. El módulo además ignora referencias a diálogos ya retirados y se importa con su nombre versionado para evitar usar JavaScript antiguo tras publicar.

Verificación del 9/11/2026: 109 comprobaciones automáticas correctas; prueba completa con OpenAI y un cuaderno ficticio (dos entradas, tres notitas y perfil), con cuatro temas que identifican sus fuentes y conservan tanto el cansancio como la alegría. En navegador, sobre una base aislada, se verificaron el aviso de desactualización al editar una nota, el guardado, la eliminación consecutiva de tres notas y la reapertura y cancelación del diálogo con Escape, sin refrescar la página.

Los fragmentos de apoyo de entradas y documentos permiten abrir el original completo en otra pestaña, con adjuntos y un enlace de regreso al tema. La [conversación](conversacion.md) usa una voz distinta: habla de vos y responde al último mensaje. Las notitas no se agregan como cierre genérico a cada tema.
