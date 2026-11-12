# Brisa, la compañera de Mi Bitácora

Brisa es el personaje y la voz de la IA de Mi Bitácora: adulta, amable, atenta y esperanzadora, con lentes grandes. Su presencia une la bienvenida, la conversación, las respuestas y su espera, el calendario, los informes, el perfil, los planes y el acceso a la cuenta. La identidad visual conserva papel cálido, salvia y terracota, con tipografía Fraunces y DM Sans servida desde el propio sitio.

La conversación distingue los mensajes propios, en salvia, de la respuesta de Brisa, en papel con su retrato y nombre. El compositor la mantiene presente y la espera conserva la animación suave y la vuelta a la respuesta ya implementadas. Se respeta la preferencia de movimiento reducido y la navegación en móvil. Los diarios, adjuntos, edición, memoria y planes conservan su comportamiento.

Brisa habla con voseo, atiende primero lo concreto y no se presenta en cada mensaje. Su identidad se aplica al prompt común y a la voz de conversación, para Ollama y OpenAI. El contrato de generación usa `message` para la respuesta directa, evitando que el saludo termine convertido en un resumen sobre la persona o escondido en un tema. Se guarda en el campo existente y se siguen aceptando respuestas anteriores con `overview`, sin migrar datos. Los informes conservan la primera persona de quien escribe; su presentación identifica a Brisa como la ayuda para prepararlos. Las respuestas ya guardadas se conservan y no se regeneran por el cambio de identidad.

La interfaz y los prompts aclaran que Brisa es una compañera de IA, sin atribuirle credenciales profesionales, humanidad, disponibilidad permanente ni un vínculo exclusivo. No se cambian los proveedores de los planes ni se habilitan cobros. La conexión con Ollama mantiene el puente HTTPS existente.

## Assets

- Ilustración original: `wwwroot/brisa/brisa-cozy.png`, con transparencia, reutilizada en hero, retratos y estados. Generada con la herramienta integrada `image_gen.imagegen`, no con la API del proyecto. El original se conserva en la carpeta de imágenes de Codex.
- Símbolo para pestañas: `wwwroot/brisa/brisa-symbol.svg`, dibujado en SVG con los lentes del personaje.
- Estilos compartidos: `wwwroot/brisa.css`.
- Componente: `Components/Shared/BrisaPortrait.razor`.

## Prompt exacto de la ilustración

```text
Use case: illustration-story.
Asset type: original character illustration for the Spanish journaling app Mi Bitácora, transparent cutout used as a welcoming hero and recurring avatar.
Primary request: Brisa, a very kind, comforting adult woman character, cozy, tender and quietly hopeful, with very large round eyeglasses. A calm companion who listens, reads and thinks together with you.
Subject and composition: a single adult woman around her mid thirties, expressive warm brown eyes visible behind oversized round dark cocoa eyeglass frames, softly wavy chestnut bob, gentle genuine smile, a little natural blush. Wearing a soft sage green knitted cardigan over a cream shirt, sitting comfortably in a rounded terracotta armchair, holding a small open notebook in her lap. One hand rests naturally on the notebook and the other hand makes a small welcoming gesture. Full upper body and cozy chair visible, face large enough to read at small sizes, centered in a square canvas with generous clear margins. Adult proportions, no childlike facial anatomy.
Style/medium: beautifully crafted cozy editorial storybook illustration, soft gouache and colored pencil texture, warm cream, terracotta, sage and honey, clean deliberate hand drawn details, charming but refined, soft daylight, reassuring and unhurried.
Text: none.
Constraints: genuinely transparent background; no surrounding room, no background rectangle, no lettering, no logos, no watermarks, no clinical coat or medical symbols, no 3D, no photorealism, no anime. Keep complete silhouette and chair uncut. Create a polished production ready character at 1024 by 1024 pixels.
```

## Verificación

Compilación Release sin advertencias ni errores y suite de 365 comprobaciones .NET y 14 pruebas JavaScript. Incluye compatibilidad del mensaje nuevo con respuestas existentes. Se probó la presentación de Brisa con Gemma real y una pregunta ficticia, sin OpenAI ni acceso a diarios. Revisión con cuenta y contenido ficticios: bienvenida, conversación, envío, espera y respuesta y móvil sin desborde horizontal. Capturas y pruebas locales quedan fuera de Git en `.artifacts/brisa`. El despliegue usa respaldo consistente de SQLite y adjuntos, comparación de huellas de los datos originales y verificación de HTTP, assets y el puente de modelos.

