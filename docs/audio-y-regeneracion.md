# Mensajes de voz y nuevas lecturas

En **Conversación**, el micrófono abre **Grabar** y **Subir audio**. Al grabar, se puede detener, escuchar el borrador, descartarlo o adjuntarlo con **Usar audio**. Nada se envía a OpenAI hasta tocar **Enviar**. El navegador pide permiso de micrófono y requiere HTTPS (o localhost); el selector de archivos sigue disponible si no hay micrófono o permiso.

**Enviar** guarda el original, transcribe los audios pendientes de esa entrada, actualiza la memoria y genera la respuesta. **Solo guardar** conserva los archivos sin transcribir ni analizar. Luego se puede tocar **Pedir una respuesta**. Los límites siguen siendo 8 MiB por archivo y 16 MiB por entrada, sin cupo de cantidad.

## Contexto y privacidad

La transcripción automática queda visible junto al audio, y también dentro del reproductor. Se guarda en `JournalAttachments.ExtractedText`, con texto de búsqueda; el original sigue en `uploads/<cuenta>/<adjunto>.bin`. No se sustituye el texto escrito ni se guardan los bytes del audio en SQLite.

El texto transcrito forma parte del turno actual, del hilo reciente, de la memoria entre diarios y del corpus completo del informe para terapia. La memoria conserva fechas y fuentes; las respuestas anteriores de la IA no se vuelven recuerdos. Como con los mensajes escritos, el contexto de una respuesta tiene un presupuesto y recupera antecedentes pertinentes, no garantiza incluir literalmente todo el historial. El informe se actualiza con su botón habitual.

La aplicación distingue una grabación realizada en el chat de un archivo subido cuyo hablante no está confirmado (`IsVoiceMessage`). Los prompts preservan esa diferencia para no atribuir al usuario las palabras de otra persona. Las transcripciones pueden contener errores; siguen disponibles el texto y el audio original para contrastarlos. No se infieren emociones por el sonido de la voz.

## API y formatos

Con `AI:Provider=OpenAI`, el servidor usa `POST https://api.openai.com/v1/audio/transcriptions`, modelo `gpt-transcribe`, y la misma clave y proyecto privados ya configurados. La clave necesita acceso al endpoint de transcripción además de Responses. El navegador nunca recibe la clave. La [guía oficial de OpenAI](https://developers.openai.com/api/docs/guides/speech-to-text) describe el servicio. La transcripción y la respuesta consumen el saldo de API.

`AudioTranscription:Model` tiene como valor predeterminado `gpt-transcribe`; `AudioTranscription:TimeoutSeconds` es 180 (admite 30–600). No hace falta agregar configuración para activar estos valores. No se cambian el modelo de conversación, sus límites ni el esfuerzo de razonamiento.

Se transcriben MP3, M4A, WAV, OGG/Opus, FLAC y WebM de audio. El grabador selecciona un formato compatible con el navegador (M4A o WebM/Opus, con OGG como alternativa). AAC suelto se puede guardar y reproducir, pero requiere convertirlo a un formato admitido para transcribirlo. Los videos no se transcriben. El detector de WebM revisa sus pistas en el encabezado; un archivo de video no se toma como un mensaje de voz por usar la misma extensión.

Con Ollama no se envían audios a OpenAI: se conserva el archivo y se informa que la transcripción necesita el proveedor configurado. No hay cambio automático de proveedor.

## Volver a generar

Cada respuesta tiene un icono circular **Volver a generar respuesta**, disponible en Conversación, Calendario y Entrada completa. Prepara una nueva lectura del mismo mensaje con el prompt vigente y los antecedentes pertinentes. La reemplaza en su lugar, conservando fecha de la entrada, diario, adjuntos y selección para terapia. La respuesta anterior permanece visible durante el proceso y si ocurre un error. No hay historial de versiones de respuestas.

Las transcripciones completadas se reutilizan. Si falla el segundo audio o la respuesta, el texto ya transcrito del primero queda guardado para el siguiente intento. Se evita generar dos veces en simultáneo sobre la misma entrada y no hay reintentos automáticos que puedan duplicar consumo. Regenerar no cambia las demás respuestas ni actualiza automáticamente el informe.

No se procesan en lote los audios ni las respuestas existentes. Los audios antiguos se transcriben cuando se pide o regenera la respuesta de su entrada. La migración `AddVoiceMessageOrigin` sólo agrega una columna booleana con valor predeterminado falso; conserva entradas, respuestas y archivos.

## Verificación

`AudioConversationChecks` cubre el contrato HTTP con un proveedor simulado, aislamiento por cuenta, original intacto, caché de transcripción, recuperación ante errores, concurrencia, memoria entre diarios, corpus del informe y reconocimiento de WebM. `node --test tests/voice-recorder.test.mjs` verifica la selección de formato, permisos denegados, cierre durante la solicitud de micrófono, liberación de pistas y límite de tamaño, sin usar el micrófono real.

La verificación del 10/11/2026 pasó 205 comprobaciones .NET y las 6 pruebas del grabador. La integración con OpenAI usó una grabación de voz sintética y una cuenta local ficticia: guardó y reprodujo el original intacto, mostró su transcripción, generó una respuesta y la reemplazó desde Conversación y Entrada completa. Varias regeneraciones mantuvieron una sola llamada de transcripción; una respuesta antigua conservó su posición en pantalla. Calendario mostró el mismo control y texto guardado.

Se revisaron 320, 390 y 430 píxeles sin desbordamiento del editor. Estas pruebas en Chromium y las simulaciones de formatos no equivalen a una grabación real en un iPhone físico; la compatibilidad de Safari depende del navegador y de los permisos del dispositivo.
