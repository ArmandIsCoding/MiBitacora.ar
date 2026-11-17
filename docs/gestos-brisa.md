# Gestos de Brisa

Brisa pasa por reposo, lectura, pensamiento y respuesta, según fases reales de la conversación. No se agregan llamadas de IA ni esperas artificiales. La sonrisa al responder ocurre una vez y vuelve a la calma; las respuestas históricas quedan quietas.

El módulo `brisa-motion.js` pausa los retratos fuera de pantalla o con la pestaña oculta y libera observadores cuando Blazor retira tarjetas. Usa CSS y una hoja transparente de seis expresiones.

Las animaciones se activan automáticamente, sin selector ni aviso en el inicio o Mi cuenta. Las preferencias antiguas guardadas en `mibitacora.brisa.motion` ya no se consultan ni se modifican. Los retratos se animan por defecto; las tarjetas históricas de respuestas conservan `Animated=false` para que la lectura del historial siga tranquila.

La ilustración tiene un balanceo más perceptible; los retratos respiran, parpadean y acompañan las fases reales de la conversación. En el inicio, la burbuja flota y la estrella cambia suavemente de escala y ángulo; la escena usa el mismo observador de visibilidad que los retratos. La entrada del texto y la respuesta al pasar sobre las tarjetas completan el movimiento sin ocultar contenido ni agregar esperas.

Se respeta automáticamente `prefers-reduced-motion` del dispositivo y se detiene el movimiento al imprimir. Las expresiones informativas permanecen visibles. No se agregan llamadas de IA, preferencias en el servidor ni escrituras en el almacenamiento del navegador.

## Recurso y reproducción

- Final: `wwwroot/brisa/brisa-expressions.png`, PNG transparente de 1536×1024, tres columnas y dos filas.
- Referencia: `wwwroot/brisa/brisa-cozy.png`.
- Modo: edición mediante la herramienta integrada de generación de imágenes, conservando identidad y vestimenta. Sin claves del proyecto.
- Orden: reposo, lectura, pensamiento; respuesta, medio parpadeo, parpadeo completo.
- Componentes: `BrisaPortrait.razor`, `Home.razor.css`, `brisa.css`, `brisa-motion.js`.

Prompt exacto:

```text
Use case: identity-preserve.
Asset type: animation expression sprite sheet for the existing Brisa character, six aligned portrait frames in a strict 3 columns by 2 rows grid.
Input image 1 is the EDIT TARGET and exact character identity reference. Preserve Brisa's adult face, very large round dark cocoa eyeglasses, warm brown eyes, chestnut wavy bob, gold hoop earrings, cream shirt, sage green knitted cardigan, gentle storybook gouache and colored pencil texture, and warm coloring exactly. Do not redesign her.
Create a new transparent sheet of six close-up portraits of this SAME Brisa, head and upper shoulders only, no armchair, no notebook, no hands. Every tile has the same size, scale, head position, hair silhouette, glasses position, neckline and shoulder crop. Only the eyelids, eye direction, eyebrows and mouth expression change. Face and glasses must align perfectly from tile to tile for crossfades. Leave 8 percent transparent margin around each portrait inside its cell. No text, no numbers, no borders, no separators, no shadows connecting cells.
Canvas: landscape, exact 3:2 aspect ratio, requested 1536 by 1024 pixels, six equal square 512 by 512 cells. Ordered LEFT TO RIGHT:
TOP LEFT: resting/welcoming, eyes open looking toward us, a small relaxed closed mouth smile.
TOP MIDDLE: reading/listening attentively, eyes visibly looking gently downward through the glasses, mouth relaxed, compassionate and engaged.
TOP RIGHT: thinking, eyes glancing slightly upward and to one side, one eyebrow very subtly lifted, thoughtful and kind, no exaggerated confusion.
BOTTOM LEFT: responding warmly, eyes looking toward us, a soft slightly open smile, only a hint of teeth, encouraging but calm, not laughing.
BOTTOM MIDDLE: half blink of TOP LEFT, identical in every way to TOP LEFT except eyelids half closed.
BOTTOM RIGHT: full blink of TOP LEFT, identical in every way to TOP LEFT except eyes softly closed, mouth remains the same calm small smile.
Keep adult proportions, complete hair silhouette, all glasses and shoulders inside each cell. Genuine transparent background throughout. No extra objects, no speech bubbles, no watermarks, no anime or 3D.
```

## Conversación e interfaz

Los diarios están visibles como tarjetas con nombre, cantidad de entradas, borrador y selección. «Crear otro diario» es una acción explícita y las herramientas de organización siguen disponibles. La bienvenida deja aire entre la ilustración y el texto; el título se distribuye en varias líneas en celulares.

Todas las respuestas terminan con una propuesta visible para llevar a terapia. Las nuevas piden una propuesta específica en la misma llamada al modelo; las históricas usan sus temas, pregunta o una invitación general. No se regeneran ni reescriben respuestas guardadas. Brisa se identifica como IA y no reemplaza a un terapeuta.

El favicon es un SVG con sus lentes. La marca dice «tu espacio con Brisa». Escribir el diario deja de restringirse a mayores de 18; privacidad explica por separado el procesamiento externo y sus condiciones. «Tus conversaciones no son públicas» es consistente con la ausencia de cifrado de extremo a extremo y el acceso técnico al servidor.

## Verificación

`tests/brisa-motion.test.mjs` cubre visibilidad, fases, retorno a la calma, desmontaje de escenas con retratos y compatibilidad con preferencias antiguas sin aplicarlas. Las comprobaciones .NET verifican fases sin llamadas extra, cancelación y tarjetas antiguas/nuevas. Las capturas de verificación usan páginas públicas o contenido ficticio y quedan en `.artifacts`, fuera de Git.

