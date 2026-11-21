# Mapa de ideas

`/mapa` permite recorrer una constelación 2D de entradas, preguntas, temas propuestos por Brisa y notitas/contexto consultados. Está incluido íntegramente en **Gratuito y Profundo**. No llama a un modelo, no consume créditos y funciona con OpenAI o IA local a partir de las devoluciones que ya están guardadas.

Se abre desde el menú del cuaderno o desde **Explorar esta conversación en mi mapa**, junto a los antecedentes de cada devolución. El enlace contextual centra la exploración en esa entrada, incluso si es antigua. Cada punto muestra su texto, sus relaciones dirigidas y un enlace para volver al original. La vista admite búsqueda sin distinción de acentos, zoom, desplazamiento, exploración de una rama a dos saltos y filtros de diario/fechas.

## Mapas grandes y pantalla completa

La vista general conserva todos los puntos y conexiones, con rótulos progresivos: prioriza los temas y revela entradas y preguntas al acercarse, buscarlas, enfocarlas con teclado o elegirlas. Los textos se distribuyen según el espacio real en pantalla, con detección de superposiciones; los rótulos que no entran se pueden leer completos en el panel de selección. Los puntos y sus textos mantienen tamaños acotados al usar zoom, y **Ver todo** encuadra el recorrido con las dimensiones reales de la vista.

**Pantalla completa** abre el mapa en todo el espacio disponible. Usa la API nativa cuando está disponible y una vista expandida cuando el navegador no la admite. Sin una selección, aprovecha también el espacio del panel lateral; al elegir un punto, muestra su detalle. **Salir** o **Escape** regresan al cuaderno, conservando el estado de búsqueda/rama y restaurando el foco y el desplazamiento de la página. El foco de teclado queda dentro de la vista expandida. En celular el detalle ocupa una franja inferior acotada.

## Qué significan las flechas

| Dirección | Relación guardada |
| --- | --- |
| Entrada → tema | Una devolución de Brisa propuso ese título como tema. |
| Entrada → pregunta | Brisa dejó esa pregunta en la devolución. |
| Pregunta → respuesta | Una entrada respondió esa pregunta y abrió una rama. |
| Antecedente → entrada | La devolución guardó ese original entre sus antecedentes consultados. |

Los títulos de temas se agrupan únicamente por espacios, mayúsculas y acentos. No se usa semejanza semántica ni se inventan relaciones, diagnósticos o causas. Los antecedentes señalan material que Brisa tuvo disponible, no una demostración de que lo haya usado para cada afirmación. Los archivos se representan por su entrada contenedora; no se procesa ni se envía el archivo para dibujar el mapa. Las devoluciones de muestra no generan temas personales.

Cuando una respuesta pertenece a una pregunta histórica, se conserva la pregunta efectivamente respondida aunque después se regenere la devolución. Las preguntas enlazan a la conversación original: abrirlas no genera ni envía una respuesta.

## Privacidad, alcance y rendimiento

- La página requiere autenticación. Todas las consultas, incluso las referencias guardadas dentro de las devoluciones, se restringen a la cuenta autenticada. No existe un endpoint público para obtener mapas privados.
- Se recuperan originales actuales; no se reutilizan etiquetas o citas antiguas del registro de antecedentes para reconstruir contenido borrado. Fuentes ajenas, inexistentes o eliminadas quedan fuera del mapa.
- La vista es calculada al abrir o aplicar filtros, sin migraciones ni una copia persistente nueva del diario. Una pestaña ya abierta conserva su fotografía hasta actualizarla, como otras vistas del cuaderno.
- Se cargan hasta 120 entradas recientes del diario/período elegido y hasta 80 antecedentes originales adicionales. El contador informa la cobertura y los filtros permiten recorrer etapas anteriores. Las fechas respetan la zona horaria argentina configurada por la aplicación. Se incluyen hasta ocho temas y cuatro preguntas por devolución.
- El grafo se dibuja en SVG con geometría determinista y resolución de colisiones por celdas; no tiene un bucle continuo de simulación. El desplazamiento se actualiza por `requestAnimationFrame`, y la instancia libera listeners, observador de tamaño y frames al salir o cambiar datos.
- La vista sigue las preferencias de movimiento reducido. Los controles tienen nombre accesible, los puntos usan tabulación itinerante y flechas del teclado, y hay una lista alternativa de entradas fuera del SVG. En celular, el panel pasa debajo del mapa y admite arrastre y pellizco.

El landing incluye una muestra interactiva con **contenido completamente ficticio**, creada en `ThoughtMapDemo`. Nunca recibe datos de usuarios. Las páginas de planes explicitan la disponibilidad del mapa en Gratuito y, por extensión, en Profundo.

## Verificación

```powershell
dotnet build tests/MiBitacora.Checks -c Release -p:UseAppHost=false
dotnet tests/MiBitacora.Checks/bin/Release/net10.0/MiBitacora.Checks.dll
node --test tests/thought-map.test.mjs tests/thought-map-fullscreen.test.mjs tests/brisa-motion.test.mjs tests/journal-scroll.test.mjs tests/voice-recorder.test.mjs
```

Las comprobaciones cubren aislamiento entre cuentas, referencias eliminadas, títulos equivalentes, preguntas regeneradas, filtros, límites, fechas locales, entradas antiguas, integridad del ejemplo público, búsqueda/ramas, geometría de 1.000 puntos y rechazo de enlaces externos o ejecutables. La revisión visual usa exclusivamente una base y cuentas sintéticas.

También se comprueban un recorrido sintético de 221 puntos y 141 conexiones, la ausencia de rótulos superpuestos con distintos zooms y desplazamientos, y la entrada/salida de pantalla completa nativa o alternativa, incluyendo permisos rechazados, Escape, navegación durante una apertura pendiente y liberación de foco/fondo al cerrar.
