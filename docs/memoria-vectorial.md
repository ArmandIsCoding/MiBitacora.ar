# Memoria local con embeddings

La conversación usa EmbeddingGemma para buscar antecedentes y hace **una sola generación** con Gemma 4 u OpenAI por devolución. No resume el historial ni le pide a un modelo generativo que seleccione recuerdos al responder. La transcripción de audios pendientes de la entrada elegida sigue siendo una operación aparte. El informe completo para terapia conserva su [flujo por apartados](memoria-e-informe.md).

## Arranque

```powershell
Set-Location 'C:\Dev\MiBitacora'
ollama pull embeddinggemma
# Sin consultas ni modelos cargados; preserva las descargas y respalda los ajustes anteriores:
.\scripts\Configure-LocalOllama.ps1
dotnet run --launch-profile http
```

El script inicia `ollama serve` oculto, sin la app gráfica: ésta puede sobrescribir los límites de modelos y la dirección de escucha. El servidor debe escuchar exclusivamente en `127.0.0.1:11434`. Se permiten dos modelos cargados, una generación simultánea por modelo y funciones cloud desactivadas. Los embeddings se calculan en CPU para dejar la GPU a Gemma; no hay que volver a descargarlos al cambiar de plan. Si usás una carpeta personalizada, pasá `-ModelsPath 'C:\OllamaModels'` la primera vez: se persiste en `OLLAMA_MODELS`, sin mover ni duplicar los modelos. El script no se ejecuta automáticamente al iniciar la web. Si reiniciás Windows o cerrás el servidor, volvé a iniciarlo con esta configuración; abrir la app de Ollama puede aplicar sus propias preferencias.

La sección `Embeddings` de `appsettings.json` contiene `Enabled`, `BaseUrl`, `Model`, `Dimensions`, `BatchSize`, `UseCpu` y `TimeoutSeconds`. Valores iniciales: `embeddinggemma:latest`, 768 dimensiones, lotes de 4, CPU y 90 segundos por lote, incluida la espera en cola. La consulta del catálogo de modelos tiene un límite de 5 segundos. Se validan loopback y el modelo local; proxy y redirecciones están deshabilitados. `Enabled=false` conserva búsqueda por palabras y turnos recientes. No vuelve a activar los resúmenes generativos para la conversación.

## Guardado e indexación

La migración `AddVectorMemory` agrega tres tablas a la misma base SQLite privada:

| Tabla | Contenido |
| --- | --- |
| `JournalVectorStates` | Revisión del cuaderno, avance del catálogo/índice, modelo y reintentos por cuenta. Es la cola persistente. |
| `JournalVectorChunks` | Fragmentos originales, procedencia, fecha, hash y vector normalizado en float32 little endian. |
| `JournalVectorQueries` | Última consulta vectorial por entrada, hash del mensaje y conversación inmediata y modelo. |

`JournalVectorText` agrega búsqueda FTS5 sobre esos mismos originales, con acentos normalizados. Triggers de SQLite invalidan fragmentos y marcan la cuenta pendiente ante inserciones, ediciones o borrados de entradas, notitas, perfil y texto extraído/transcrito de adjuntos. Cubren también importaciones directas a la base. Cambiar la devolución, diario o selección para sesión no reindexa originales. Cambiar la pregunta anterior sí cambia el hash de la consulta conversacional.

El worker se inicia con la web, después de las migraciones. Cada cinco segundos atiende hasta ocho cuentas, con hasta cuatro lotes por cuenta; los pendientes se alternan. Construye un catálogo sin inferencia y calcula únicamente vectores ausentes, modificados o de otro modelo. Guarda cada lote antes de continuar. Cede entre lotes mientras Gemma genera una respuesta o informe y da prioridad a embeddings de consulta frente al backfill. Los fallos tienen espera creciente hasta cinco minutos; perder conexión, cancelar o reiniciar conserva los lotes terminados. El modelo ausente se vuelve a consultar cada 30 segundos. No se generan devoluciones ni se transcriben audios antiguos automáticamente.

El catálogo parte de `JournalCorpusReader`: sólo entradas, documentos con texto disponible, notitas, perfil y transcripciones existentes; nunca respuestas de la IA. Sus partes se subdividen en hasta 1200 bytes UTF-8 sin perder caracteres ni el final. No se analizan bytes de fotos/PDF ni se hace OCR. Un cambio provoca una lectura del catálogo de originales de la cuenta, sin cargar todos los blobs vectoriales; la inferencia sólo procesa lo que falta.

Las llamadas usan `/api/embed`, lotes de textos, `truncate=false`, prefijos de búsqueda/documento y validación de cantidad, dimensiones, valores finitos y norma. La clave del espacio incluye formato de texto, dimensiones y digest SHA-256 del modelo instalado. Un cambio del tag obliga a reconstruir los vectores; cambiar entre plan gratuito y pago no lo hace. No se mezclan espacios. No se registran textos ni cuerpos de respuestas del servicio de embeddings.

## Recuperación al pedir una devolución

1. Se comprueba la cuenta y la entrada. Si el catálogo está pendiente se sincroniza sin llamadas a IA; la solicitud **no espera el backfill de embeddings**.
2. Se forma una consulta acotada con el mensaje, texto inmediato, pregunta y propuesta previa de la IA, identificadas como tales. Se reutiliza el vector si coincide su hash. Si hay vectores disponibles, sólo se necesita un embedding de consulta nuevo. La búsqueda por palabras prioriza el mensaje actual y recurre al contexto inmediato para respuestas sin un tema explícito.
3. Antes de cargar vectores u originales se filtran cuenta, fecha y los turnos que ya aportan continuidad. Entradas: fecha estrictamente anterior; notitas: hasta esa fecha. El perfil actual se omite al responder a días históricos. Se puede recuperar de cualquier diario de esa misma cuenta.
4. Se combina similitud coseno con FTS5 mediante Reciprocal Rank Fusion. Se consideran hasta 32 candidatos de cada búsqueda, se limita la repetición de una misma fuente y se entregan hasta 3 fragmentos al plan gratuito u 8 al pago, además de 2 o 6 turnos recientes. La generación decide si un antecedente aporta; no se afirma que todos influyan.
5. Se hace una sola generación conversacional. Sólo se guarda en la entrada elegida y se compara atómicamente la revisión dentro de la transacción final. Una edición o eliminación concurrente conserva la devolución anterior. Si faltó parte de la memoria semántica, el aviso queda guardado junto a la respuesta y aparece también al recargar.

Si faltan vectores, Ollama está caído o el modelo cambia, se usan las coincidencias de texto y la conversación reciente y se informa el alcance pendiente. No se envía el historial a otro proveedor para suplir embeddings. La selección es acotada y puede omitir antecedentes; no promete cobertura exhaustiva ni tiempos fijos.

## Por qué SQLite

Para este piloto se guarda el índice vectorial propio en SQLite y se calcula el coseno exacto en C#, después de filtrar la cuenta y fecha en SQL. FTS5 utiliza el módulo incluido en el proveedor SQLite existente. No se añaden servicios, extensiones nativas ni una base externa. **No es un índice ANN**: el costo vectorial crece con los fragmentos elegibles de esa cuenta. Si el volumen o concurrencia lo exige, `VectorMemoryService` es el punto para incorporar un motor vectorial especializado, conservando filtros, hashes, cola y procedencia. Hay que medir con un corpus representativo antes de asumir escalabilidad.

Los vectores, el texto del índice y las consultas derivadas son privados. Se respaldan con `App_Data`; las claves foráneas y triggers los retiran al borrar fuentes/cuentas. Los respaldos anteriores siguen su propia retención. Los índices son reconstruibles desde los originales y no sustituyen el respaldo de entradas y archivos. No se expone una API pública de búsqueda ni IDs de otras cuentas.

## Verificación

```powershell
dotnet build tests/MiBitacora.Checks -c Release
dotnet tests/MiBitacora.Checks/bin/Release/net10.0/MiBitacora.Checks.dll
# Sólo datos ficticios en una base temporal; no usa OpenAI ni tu diario:
dotnet tests/MiBitacora.Checks/bin/Release/net10.0/MiBitacora.Checks.dll --live-embeddings
node --test tests/voice-recorder.test.mjs
```

`VectorMemoryChecks` verifica migración y reversión, backfill y worker, reutilización e invalidación, aislamiento por cuenta/fecha, UTF-8, una generación por devolución, planes, consultas breves, transcripciones, edición/borrado concurrentes, reinicio tras avance parcial, plazos HTTP y recuperación lexical cuando falta el modelo. La evaluación optativa usa `embeddinggemma` y `gemma4:12b` instalados, con un cuaderno ficticio temporal; mide indexación, consulta, caché y generación sin enviar nada a OpenAI ni leer el diario real.

Medición del 12/11/2026 en este equipo, Ollama 0.40.16, 768 dimensiones, embeddings en CPU y Gemma en GPU, con ocho fragmentos ficticios:

| Operación | Tiempo observado |
| --- | --- |
| Indexación inicial del cuaderno | 1,15 s; 0,41 s con el modelo ya cargado |
| Búsqueda con embedding de consulta nuevo | 0,23 s |
| Consulta reutilizada | 0,08 s |
| Devolución completa de Gemma, una llamada | 86,68 s inicial; 36,53 s con el modelo cargado |

La segunda generación usó 2892 tokens de entrada y 438 de salida; su carga fue de 0,01 s. Son mediciones de ese caso, no objetivos garantizados ni un ensayo de carga. El cambio elimina las llamadas generativas para organizar y seleccionar el historial; la generación de Gemma sigue siendo la mayor espera. El log separa recuperación, cobertura del índice y métricas de tokens/carga/lectura/generación sin mostrar contenido privado.

Referencias del contrato: [API de embeddings de Ollama](https://docs.ollama.com/api/embed), [capacidad de embeddings](https://docs.ollama.com/capabilities/embeddings), [EmbeddingGemma](https://ollama.com/library/embeddinggemma), [SQLite FTS5](https://sqlite.org/fts5.html).
