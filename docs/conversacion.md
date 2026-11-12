# Conversación y entradas originales

**Enviar** guarda la entrada y pide una respuesta al proveedor del plan activo. Si falla, el texto queda guardado y aparece **Reintentar respuesta**; no se genera un turno ficticio ni se reintenta la API automáticamente. **Solo guardar** no pide una devolución ni transcribe audio; los textos disponibles se indexan localmente en segundo plano. Abrir el cuaderno o una entrada no inicia una generación.

La conversación se organiza en [diarios creados por la persona](diarios.md). Cada diario tiene sus propios turnos recientes; la memoria relevante y el informe para terapia siguen abarcando el cuaderno completo. Las entradas previas quedan en Mi diario y pueden moverse con sus respuestas y adjuntos.

Sólo el editor y sus controles permanecen pegados al borde inferior al recorrer la conversación. Los avisos de privacidad, límites de adjuntos y alcance de la IA están fuera de ese bloque, en el flujo normal de la página: se pueden leer al llegar al final y no restan altura al chat durante el desplazamiento. El marcador `.journal-end` sigue inmediatamente antes de `.composer-wrap`, para que `journal.js` reserve únicamente la altura del editor al mostrar el último mensaje.

La respuesta habla de «vos» como interlocutora, retoma un detalle concreto y aporta una distinción o lectura tentativa. Hay como máximo una pregunta visible para continuar. Las citas y apuntes complementarios quedan plegados. La síntesis por temas conserva la primera persona para leerla como propia en terapia. Ambas son propuestas de una IA, no diagnósticos ni tratamiento. El aviso que recomienda llevar el chat a terapia aparece junto al encabezado y al compositor.

`JournalAiVoice.cs` contiene la voz de conversación. `JournalAiReflection.cs` define el contrato y valida citas y fuentes. La [memoria vectorial local](memoria-vectorial.md) combina turnos recientes con búsqueda por significado y palabras en los originales anteriores. Se reutilizan los embeddings y se hace una sola generación por devolución, además de transcribir audios pendientes de la entrada elegida. Las respuestas previas se identifican como propuestas de la IA, no como vivencias confirmadas. **Antecedentes consultados** permite abrir los originales recuperados. Si faltó parte de la memoria semántica, el aviso queda guardado junto a la respuesta.

La [revisión de profundidad y calidez](conversacion-profunda.md) desarrolla el enfoque de la conversación: una lectura específica, con apoyo en el texto y antecedentes pertinentes, que pueda discutirse o descartarse. El cuerpo admite hasta 4200 caracteres; los intercambios sencillos siguen siendo breves. Las respuestas ya guardadas se conservan. El [visor de adjuntos](visor-de-adjuntos.md) permite revisar fotos, reproducir medios y abrir documentos desde la conversación, el calendario y la entrada completa, sin iniciar una descarga por defecto.

OpenAI recibe hasta 76.000 caracteres de fuentes, priorizando el texto actual completo y seis turnos recientes, con hasta ocho antecedentes recuperados. Ollama conserva su presupuesto de 6500 caracteres, dos turnos recientes y hasta tres antecedentes. Ambos admiten hasta dos imágenes compatibles y fragmentos de documentos. La selección puede omitir detalles: no equivale a una memoria ilimitada.

Las respuestas nuevas guardan `ConversationVersion=1` y `GeneratedUtc`; el JSON anterior sigue siendo legible. La interfaz muestra la fecha real de generación, también cuando responde una entrada histórica. No se cambia la fecha del original.

Desde **Ver fragmentos de apoyo → Abrir entrada completa** se abre `/entrada/{id}` en otra pestaña, con texto íntegro, adjuntos y texto extraído de documentos. Se filtra por la cuenta autenticada. Entradas ajenas o borradas muestran el mismo estado no disponible; no basta conocer un ID. El enlace de regreso abre el tema correspondiente. Las notitas y el perfil conservan su identidad y no inventan enlaces a entradas.

## Completar respuestas existentes

El comando de mantenimiento se ejecuta explícitamente, nunca al arrancar la web. Requiere autorización para procesar el cuaderno con OpenAI y consumir los créditos del proyecto. Usar la instalación que contiene la base y sus originales; no copiar una base de desarrollo sobre producción.

```powershell
dotnet build tools/MiBitacora.Backfill -p:UseAppHost=false
dotnet tools/MiBitacora.Backfill/bin/Debug/net10.0/MiBitacora.Backfill.dll --root 'C:\ruta\MiBitacora' --user '<código de cuenta>' --topics
```

Lee la clave del archivo privado de esa instalación, nunca de argumentos ni del repositorio. Respalda las respuestas anteriores y la síntesis por temas en `App_Data/conversation-backups`, que contiene datos privados y debe protegerse igual que la base. Procesa cronológicamente sólo entradas sin respuesta conversacional vigente. No cambia textos, fechas ni adjuntos. Para entradas históricas omite el perfil actual, que no prueba qué era cierto en aquella fecha; las notitas posteriores tampoco se incluyen.

Cada respuesta se guarda por separado. Un fallo de proveedor detiene el proceso; volver a ejecutarlo retoma las pendientes sin repetir las completadas. Antes de guardar vuelve a comprobar la existencia, texto y versión de la entrada. La opción `--topics` actualiza después la síntesis temática con el filtro de contexto pertinente. El resumen final indica el número pendiente y devuelve código 2 si falta alguna; no considerar completo un proceso con pendientes. Los respaldos no se sirven por HTTP y no deben subirse a Git.

## Verificación

Las comprobaciones en `tests/MiBitacora.Checks` cubren continuidad de una respuesta breve, exclusión de notas futuras o ajenas, versión de respuesta, filtro de contexto por tema, enlace a originales y aislamiento entre cuentas. Las pruebas de renderizado leen el texto completo sin invocar IA. La evaluación real utiliza textos ficticios y verifica por separado respuesta conversacional y síntesis temática.
