# Tableros de pendientes

Disponible en ambos planes desde **Pendientes** en el menú del cuaderno o `/pendientes`. Organizar tableros y tarjetas no llama a una IA ni consume créditos.

## Organización

- Crear tableros con nombre, descripción y color, por ejemplo «Mi semana» o «Proyectos DIY». Los nombres sugeridos abren el editor; no crean tarjetas automáticamente.
- Cada tablero empieza con **Por hacer**, **En marcha** y **Hecho**. Se pueden crear, renombrar, reordenar y retirar estados vacíos. Cada estado permite indicar si representa un cierre para el resumen del tablero.
- Cada tarjeta tiene título, descripción, color y fecha objetivo opcional. Las fechas son referencias personales y no producen notificaciones, cobros ni conclusiones sobre el cumplimiento.
- Arrastrar tarjetas entre estados o dentro de un estado. En pantallas táctiles se arrastran desde el asa de puntos. También se pueden mover con el selector de estado y los botones de subir/bajar, usando teclado o toque.
- La búsqueda filtra títulos y descripciones. Durante una búsqueda se desactiva el arrastre y la reordenación, para evitar posiciones ambiguas entre tarjetas ocultas; el selector permite cambiar de estado.
- Archivar conserva el contenido. Los tableros se restauran desde **Tableros archivados** y las tarjetas desde **Ver tarjetas archivadas**. Los estados con tarjetas archivadas tampoco se pueden retirar hasta moverlas.

Se admiten hasta 100 tableros por cuenta, 12 estados por tablero y 2000 tarjetas por tablero, incluidos los elementos archivados. Nombres de hasta 80 caracteres, títulos de hasta 160 y descripciones de tarjetas de hasta 4000. La revisión del tablero protege frente a cambios simultáneos: una edición con revisión antigua se rechaza sin sobrescribir la nueva. El editor conserva el borrador y permite cerrar y recargar para revisar.

## Contexto para Brisa

Cada tablero tiene **Incluir este tablero en el contexto de Brisa**, activado inicialmente y explicado en el editor antes de guardarlo. Las tarjetas activas de tableros incluidos y no archivados se incorporan como fuentes originales propias, separadas de entradas y notitas. Se recuperan por pertinencia mediante la memoria existente; no se envía cada tablero en todas las respuestas. Se conserva el proveedor de IA elegido y el consumo habitual de créditos de devoluciones e informes.

Una tarea es un plan o un estado declarado. Marcar «Hecho» no verifica que algo ocurrió y una fecha pasada no demuestra incumplimiento, malestar o productividad. Las instrucciones de conversación, memoria, temas e informe refuerzan esta distinción. El contexto actual no se proyecta sobre entradas de días anteriores. Las fuentes consultadas enlazan a la tarjeta actual, que puede haber cambiado desde la devolución; también aparecen en el mapa privado.

Editar el texto o el estado, archivar, o cambiar la inclusión invalida la memoria correspondiente dentro de la misma transacción de SQLite. Se purgan los fragmentos afectados, se cambia la revisión y se reconstruye el catálogo sin reutilizar fuentes obsoletas. Los vectores de otras entradas y de otras cuentas se conservan. Una devolución en curso con una revisión anterior no puede guardarse como si tuviera contexto vigente. Cambiar sólo el orden de las tarjetas o columnas no altera sus textos de contexto.

Las respuestas ya guardadas no se reescriben al excluir una tarjeta. Las lecturas de Brisa pueden conservar referencias a tareas y aparecer en una versión para terapia aprobada por su dueño. Los tableros y las tarjetas originales no se publican como parte de esa versión; su vista previa y la política de privacidad explican el alcance. La revisión de publicación detecta cambios del contexto que puedan dejar desactualizado un informe integral.

## Datos y verificación

La migración `20261120142408_AddTaskBoards` agrega tres tablas y un identificador opcional de tarjeta en las dos memorias existentes. Mantiene las entradas, fuentes y relaciones previas. Las claves foráneas impiden asignar una tarjeta a una columna de otro tablero. La cuenta es dueña de los tableros; todo acceso se verifica en el servidor. El borrado de cuenta elimina tableros, estados y tarjetas en cascada. La descarga de datos personales incluye también los archivados.

```powershell
dotnet build tests/MiBitacora.Checks -c Release
dotnet tests/MiBitacora.Checks/bin/Release/net10.0/MiBitacora.Checks.dll --tasks-only
dotnet tests/MiBitacora.Checks/bin/Release/net10.0/MiBitacora.Checks.dll --task-interactions-only
node --test tests/task-boards.test.mjs
```

Las pruebas usan datos ficticios en bases aisladas: aislamiento entre cuentas, validación, orden y movimiento, concurrencia, archivo/restauración, exclusión, recuperación de contexto sin Ollama, fuentes de una devolución, mapa, exportación y borrado. La revisión visual debe usar también una cuenta ficticia, sin enviar información real a modelos. OpenAI y el análisis local de adjuntos conservan su configuración de despliegue.

Se comprobó la interfaz en escritorio y anchos de 390 y 320 px: creación de tablero y tarjeta, fecha y color, movimiento por selector y por arrastre desde el asa, archivo/restauración de ambos tipos, control de contexto y bienvenida de una cuenta vacía. El editor y el documento no presentan desbordamiento horizontal en esos anchos. Las pruebas de JavaScript cubren también el gesto táctil, cancelación, sólo lectura y liberación de observadores. Se verificó además que revertir y reaplicar la migración conserva el índice de texto anterior, y que cambiar tareas no publica tarjetas ni modifica copias fijas ya aprobadas para terapia.

Las llamadas de arrastre llegan por `JSInvokable`, fuera del ciclo de eventos del DOM que provoca renderizados automáticos en Blazor. El componente entra explícitamente al dispatcher y renderiza tanto el bloqueo inicial como el resultado y los errores. El puente bloquea otro arrastre mientras espera la respuesta, y cada movimiento siguiente toma la revisión del DOM actualizado. La prueba de interacción reproduce el fallo anterior (el movimiento se guardaba pero no cambiaban la columna ni la revisión visibles) y verifica cuatro movimientos consecutivos, la visualización de conflictos y el guardado con sólo título.

El resultado de cada escritura se lee en su misma transacción, antes de confirmar. Tras guardarlo, la página actualiza el tablero y su resumen con ese resultado y retira el editor mediante el render, sin otra consulta ni una llamada JavaScript de cierre que pueda presentar un guardado exitoso como error. No reescribe tarjetas existentes ni modifica el proveedor de IA.
