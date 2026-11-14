# OpenAI para las devoluciones

Por ahora la web usa **OpenAI por defecto para todas las cuentas**, actuales y futuras, con `AI:OpenAiForAllAccounts=true`. Desde **Mi cuenta → Mi plan** se puede elegir Ollama y conservar esa preferencia. Esto habilita el proveedor sin simular pagos ni cambiar el plan. Con el indicador en `false`, OpenAI vuelve a requerir un período pago vigente o una concesión administrativa; las cuentas gratuitas usan Ollama. `AI:Provider` permanece por compatibilidad con herramientas anteriores. Ver [acceso y planes](suscripciones.md). No hay cambio automático de proveedor si falla una solicitud.

OpenAI usa exclusivamente `https://api.openai.com/v1/responses`, con HTTPS, redirecciones y proxy deshabilitados. El modelo inicial es `gpt-6.1-terra`, esfuerzo `low`, un máximo de 4096 tokens de salida y 180 segundos incluyendo la espera en cola. Los límites y el modelo son configurables en el servidor. Una cola compartida permite una generación simultánea y ocho pendientes por proceso, igual que Ollama. No hay reintentos automáticos que puedan duplicar consumo.

## Credencial y configuración

En la organización que recibe los créditos de ChatGPT, crear un proyecto **Mi Bitácora** y una cuenta de servicio **mibitacora-servidor**. La clave de esta cuenta debe tener permisos **Restricted → Model capabilities → Responses → Write** (la plataforma incluye los permisos de lectura asociados). No necesita listar modelos, archivos, agentes ni administrar la organización.

El piloto usa una clave sin vencimiento por decisión del propietario. Si se pierde o se filtra, crear una sustituta, actualizar ambas instalaciones, verificarla y revocar la anterior. No pegar claves en chats, incidencias, capturas, código cliente, repositorios o registros.

La aplicación admite `App_Data/ai.settings.json`, fuera de `wwwroot`, Git y la publicación. Proteger el archivo con ACL: lectura para la identidad del application pool, administración para el propietario, administradores y SYSTEM. No heredar permisos de lectura de Todos/Usuarios en este archivo. También se pueden usar secretos de .NET en desarrollo o variables de entorno `AI__Provider`, `OpenAI__ApiKey`, `OpenAI__ProjectId`, etc. Las variables de entorno prevalecen; en Development los secretos de .NET prevalecen sobre el archivo privado. Reiniciar la aplicación para aplicar cambios.

Ejemplo sin credencial real:

```json
{
  "AI": { "Provider": "OpenAI" },
  "OpenAI": {
    "ApiKey": "REEMPLAZAR_EN_LA_CONFIGURACION_PRIVADA",
    "ProjectId": "proj_DEL_PROYECTO",
    "Model": "gpt-6.1-terra",
    "ReasoningEffort": "low",
    "MaxOutputTokens": 4096,
    "TimeoutSeconds": 180
  }
}
```

Nunca incluir este archivo en un paquete de publicación. Los respaldos de `App_Data` pueden incluirlo: tratarlos como secretos y preservar sus permisos. El despliegue de código debe conservar `App_Data` y `uploads`; no copiar una base de desarrollo sobre producción. Una clave o proyecto ausentes impiden las solicitudes pagas a OpenAI, conservando las entradas y sin impedir que las cuentas gratuitas usen la web y Ollama.

## Qué se envía

- Conversación: la entrada actual, seis intercambios recientes, hasta ocho antecedentes relevantes recuperados por significado, contexto personal, documentos y hasta dos imágenes, dentro de un presupuesto de texto. La [memoria vectorial](memoria-vectorial.md) usa EmbeddingGemma local y SQLite en ambos planes: sólo los originales seleccionados llegan a la llamada generativa de OpenAI. No requiere su API de embeddings ni una base externa. Los HEIC se convierten a JPG localmente antes del envío. `Solo guardar` no genera respuestas ni transcribe; los textos disponibles sí se indexan localmente.
- Devolución: un informe integrado de diez apartados. Recorre el índice de todas las entradas y documentos con texto, las notitas y el contexto personal, y desarrolla cada sección a partir de originales seleccionados. No incluye fotos ni convierte respuestas anteriores de la IA en evidencia. Comparte un esquema de lo ya redactado entre apartados para reducir repeticiones; ese esquema no sirve como fuente de hechos. Se inicia a demanda y sólo reemplaza el informe previo cuando termina y las fuentes siguen vigentes. Incluye una vista para imprimir o guardar PDF. Ver [memoria e informe](memoria-e-informe.md) para presupuestos, límites y actualización.
- Se envían los contenidos necesarios directamente en cada solicitud. No se crean archivos, conversaciones o herramientas en OpenAI ni se reutilizan respuestas de otra cuenta. Se usa `store: false`. Las respuestas y fuentes se validan antes de guardarlas en SQLite.
- Las claves, cuerpos HTTP, textos, imágenes, respuestas y errores crudos del proveedor no se registran. La interfaz informa OpenAI como proveedor y enlaza la política de privacidad actualizada.

El procesamiento de OpenAI sale del servidor propio. `store: false` no equivale a retención cero: OpenAI no entrena con datos de la API por defecto, pero los registros de prevención de abusos pueden conservar contenido hasta 30 días o más en las excepciones indicadas por su política. No activar el programa de compartir datos a cambio de tokens para este cuaderno.

## Créditos y errores

Usar una clave del proyecto perteneciente a la organización vinculada al beneficio. La vinculación no habilita una tarjeta ni recarga automática. Un error 429 puede indicar saldo agotado o límites de uso; 401/403 indica autenticación o permisos. Estos errores conservan entradas y devoluciones previas. El beneficio deshabilita Codex con claves de esa organización; Codex iniciado con una cuenta ChatGPT y su suscripción sigue disponible.

## Pruebas

```powershell
dotnet build tests/MiBitacora.Checks
dotnet tests/MiBitacora.Checks/bin/Debug/net10.0/MiBitacora.Checks.dll
$env:MIBITACORA_AI_CONFIG = 'C:\Dev\MiBitacora\App_Data\ai.settings.json'
dotnet tests/MiBitacora.Checks/bin/Debug/net10.0/MiBitacora.Checks.dll --live-openai
```

Las pruebas normales simulan OpenAI y verifican esquema estricto, MIME, citas, errores, respuestas truncadas, rechazo, cancelación, ausencia de reintentos y privacidad visible. `--live-openai` realiza cuatro solicitudes con texto y una imagen inventados, sin leer el diario ni la base de usuarios. Windows puede bloquear el ejecutable auxiliar sin firma; ejecutar la DLL con el host `dotnet` permite usar las mismas comprobaciones sin desactivar protecciones.

Los prompts están en `Services/JournalAiReflection.cs` (chat), `Services/JournalAiMemory.cs` (índice y recuperación) y `Services/JournalAiReports.cs` (informe). `Services/JournalAiTopics.cs` conserva el formato anterior por compatibilidad. El chat usa voseo; el informe, primera persona y distinción entre registros, hipótesis, propuestas e información pendiente. La selección de fuentes está en `Services/JournalAiContext.cs` y el transporte en `Services/OpenAiJournalService.cs`. Ver [conversación y actualización histórica](conversacion.md).

Fuentes oficiales consultadas el 9/11/2026: [Responses](https://developers.openai.com/api/docs/guides/text), [cuentas de servicio](https://developers.openai.com/api/docs/guides/api-keys), [GPT-6.1 Terra](https://developers.openai.com/api/docs/models/gpt-6.1-terra), [datos](https://developers.openai.com/api/docs/guides/your-data), [créditos de ChatGPT](https://developers.openai.com/api/docs/guides/chatgpt-api-credits).
