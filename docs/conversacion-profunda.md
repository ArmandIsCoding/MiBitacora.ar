# Conversaciones con más comprensión y calidez

El objetivo de `ConversationVoiceInstruction`, en `Services/JournalAiVoice.cs`, es aportar una lectura útil y específica que ayude a comprender lo contado. Una paráfrasis más larga no cumple ese objetivo. La IA responde con voseo, distingue hechos de hipótesis y mantiene el hilo del diario actual, consultando antecedentes relevantes de cualquier diario de la misma cuenta.

La orientación psicodinámica toma como lentes posibles la ambivalencia, las necesidades en tensión, las formas de protegerse, la autocrítica, los vínculos y sus cambios. La descripción de [Tavistock sobre psicoterapia psicodinámica](https://tavistockeducation.ac.uk/articles/what-is-psychodynamic-psychotherapy/) y la de [APsA sobre psicoterapia psicoanalítica](https://apsa.org/about-psychoanalysis/psychoanalytic-psychotherapy/) ofrecen el marco conceptual. Esto no convierte a la aplicación en psicoterapia ni demuestra eficacia clínica; no se agregó una búsqueda de literatura por mensaje ni una base de evidencia clínica validada.

## Criterios de respuesta

- Apoyarse en un detalle concreto y explicar una conexión que agregue comprensión: una necesidad, una regla exigente, una reacción y su posible función o costo. Formular una hipótesis precisa y revisable, no revelar supuestas verdades ocultas.
- Considerar condiciones reales como agotamiento, dinero o trato recibido. No atribuir dificultades externas a defectos personales ni inventar infancia, traumas, apego o motivos inconscientes.
- Conectar antecedentes cuando aportan algo y conservar las excepciones y contradicciones. No convertir propuestas previas de la IA en hechos, ni usar una notita como explicación universal.
- Hacer comprensible lo humano sin confirmar cualquier explicación: distinguir emoción, intención y conducta, acompañar la ambivalencia y permitir alegría sin buscarle un conflicto oculto.
- Aceptar correcciones y no interpretarlas como resistencia. No asumir nombres, género, identidad ni vivencias desde archivos de otra persona. No fingir haber oído audios o visto videos no analizados.
- Ajustar la extensión al mensaje. Una entrada rica puede merecer varios párrafos; una respuesta breve, una corrección o una alegría pueden necesitar menos. Como máximo una pregunta abierta, sin tareas obligatorias ni preguntas repetidas por rutina.
- Ante riesgo inmediato, priorizar seguridad y apoyo humano. El chat no sustituye al terapeuta y la interfaz conserva esa explicación.

El prompt incluye un ejemplo expresamente ficticio, separado de las fuentes de la persona. El cuerpo principal admite hasta 4200 caracteres, frente al corte anterior de 1800. Los dos turnos recientes conservan hasta 4200 caracteres de la respuesta anterior más su pregunta para continuar el hilo; los otros turnos mantienen el límite anterior. Ollama conserva sus presupuestos y recibe una instrucción de extensión más contenida. El campo de pregunta descarta fragmentos incompletos y texto suelto posterior al signo de cierre.

El cambio se aplica a las próximas respuestas y a las que se pidan de nuevo con el icono [Volver a generar](audio-y-regeneracion.md). No incrementa `ConversationVersion`, no ejecuta backfill ni reescribe entradas. No cambia proveedor, modelo, clave ni esfuerzo de razonamiento. Las respuestas existentes y el informe se conservan hasta que se solicite su actualización.

## Evaluación reproducible

Las pruebas automáticas comprueban que se conserve una respuesta superior al antiguo límite, que el siguiente turno incluya su conclusión, que no se rellenen campos opcionales y que la pregunta no contenga restos de generación. No pretenden medir profundidad mediante coincidencias de palabras.

`tools/MiBitacora.ChatEval` ejecuta ocho casos **ficticios** sin leer la base de usuarios: sobrecarga con antecedentes y excepción, envidia/vergüenza, ayuda deseada y rechazada, corrección de la IA, alegría, audio sin transcripción, texto de un personaje e indicio de peligro inmediato. Guarda cada resultado y los criterios de revisión al terminar la llamada, para conservar avances si falla una posterior.

```powershell
$env:MIBITACORA_AI_CONFIG = 'C:\ruta\privada\ai.settings.json'
dotnet run --project tools/MiBitacora.ChatEval -- --live --output .artifacts/chat-depth-evaluation.json
```

`--live` autoriza llamadas reales que consumen API; la configuración y resultados quedan fuera del repositorio. `--baseline-voice ruta/al/JournalAiVoice-anterior.cs` permite comparar las dos primeras situaciones con la voz y descripción del contrato anteriores. Esa comparación aísla principalmente las instrucciones: usa el servicio actual y no es una reproducción íntegra de la versión anterior.

En la revisión manual del 10/11/2026, con `gpt-6.1-terra` y razonamiento `low`, las respuestas conectaron hechos con hipótesis revisables, consideraron una excepción histórica, aceptaron una corrección y respetaron el límite de audio no transcrito y texto ajeno. El caso de ayuda pudo distinguir cuidar a un familiar de tener que realizar personalmente cada tarea. Se detectaron restos al final de alguna pregunta; se agregó una comprobación determinista del campo y su prueba de regresión.

Es una revisión pequeña, de una generación por caso en la pasada final, no una garantía de calidad constante ni una evaluación clínica. Mantener revisión cualitativa con ejemplos diversos y conversaciones de varios turnos antes de generalizar. Los resultados pueden variar, incluso con el mismo prompt. La guía de [prompt engineering de OpenAI](https://developers.openai.com/api/docs/guides/prompt-engineering) recomienda evaluar las salidas para seguir mejorando las instrucciones.
