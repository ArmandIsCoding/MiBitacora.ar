# Ollama y Gemma 4 12B en Mi Bitácora

Esta guía aplica a las cuentas gratuitas. Las cuentas con un período pago vigente usan [OpenAI](openai.md); la aplicación muestra el plan activo y sus condiciones de privacidad. Ver [suscripciones](suscripciones.md) para la selección por cuenta y el simulador.

El backend Blazor (.NET 10) llama a `http://127.0.0.1:11434/api/chat`. Los navegadores se conectan a Mi Bitácora: los usuarios no necesitan instalar Ollama ni pueden elegir el servidor o el modelo desde el cliente.

La configuración fija `gemma4:12b`. Se conserva `qwen3-vl:4b-instruct` como alternativa de compatibilidad para instalaciones anteriores, sin audio local. Las mediciones históricas al final corresponden al modelo anterior; la [evaluación de memoria vectorial](memoria-vectorial.md) incluye una devolución completa con Gemma 4 y EmbeddingGemma reales, con datos ficticios.

## Configuración del piloto

```json
"Ollama": {
  "Enabled": true,
  "BaseUrl": "http://127.0.0.1:11434",
  "Model": "gemma4:12b",
  "ContextLength": 8192,
  "MaxOutputTokens": 2300,
  "TimeoutSeconds": 300
}
```

`Ollama__Enabled=false` activa la muestra determinista identificada como tal. Con la IA activada, los errores se muestran para reintentar: no se sustituyen silenciosamente por muestras ni se envía contenido a la nube. Un semáforo compartido permite una generación y hasta ocho solicitudes en espera por proceso de aplicación. El tiempo máximo incluye la espera; la cancelación del componente libera su lugar en la cola. Si se despliegan varios procesos, este límite debe coordinarse entre ellos.

Se envían fragmentos acotados de la entrada, antecedentes, notas, perfil y texto extraído. La selección textual tiene un presupuesto de 6500 caracteres, además de etiquetas e instrucciones; no incluye el cuaderno completo. Las imágenes JPEG, PNG y WebP se envían como base64: hasta dos y 12 MiB combinados por devolución. Para HEIC/HEIF se utiliza una copia JPG generada localmente, con el mismo identificador de adjunto y conservando el original. La copia se limita a 2048 píxeles de lado mayor y no incluye EXIF/GPS. Más detalles en [conversión de fotos](heic-jpg.md). Gemma puede transcribir WAV mediante `/v1/audio/transcriptions`; el grabador convierte el audio a WAV en el navegador. El piloto no convierte PDF a imágenes ni analiza videos, GIF o AVIF.

El modelo devuelve JSON con un esquema explícito y `think=false`, para reservar el presupuesto de salida a la devolución. Las citas se contrastan con las fuentes enviadas y las fuentes inexistentes se descartan. Se rechazan respuestas incompletas o cortadas por el límite de generación. Esta validación no garantiza que toda interpretación del modelo sea correcta: la interfaz permite revisar las devoluciones.

## Windows

1. Instalar Ollama y el controlador de la gráfica Intel.
2. Desactivar **Expose Ollama to the network** en Ollama; fijar el contexto de su app en 8192. Las preferencias de la app pueden prevalecer sobre variables de entorno.
3. Ejecutar `scripts/Configure-LocalOllama.ps1 -DownloadModel` desde PowerShell. Reinicia el Ollama del usuario actual; si hay modelos cargados, se detiene antes de modificar la configuración. Conserva la carpeta existente de modelos.
4. Ejecutar `dotnet run --launch-profile http` y abrir `http://localhost:5234`.
5. Pedir una devolución y ejecutar `ollama ps` para ver si usa GPU. La NPU no forma parte de esta configuración.

Si PowerShell impide ejecutar scripts locales, revisá su política y las reglas de tu organización antes de ejecutar el script. No es necesario desactivar políticas de seguridad para configurar las mismas variables manualmente.

Variables persistidas para el usuario de Windows:

| Variable | Valor |
| --- | --- |
| `OLLAMA_HOST` | `127.0.0.1:11434` |
| `OLLAMA_NO_CLOUD` | `1` |
| `OLLAMA_CONTEXT_LENGTH` | `8192` |
| `OLLAMA_NUM_PARALLEL` | `1` |
| `OLLAMA_MAX_LOADED_MODELS` | `2` |
| `OLLAMA_MAX_QUEUE` | `8` |
| `OLLAMA_VULKAN` | `1` |
| `OLLAMA_IGPU_ENABLE` | `1` |
| `OLLAMA_DEBUG_LOG_REQUESTS` | `false` |

Ollama debe ejecutarse con esa cuenta de Windows. En un servicio de Windows independiente hay que configurar el entorno de su cuenta de servicio. El script inicia `ollama serve` oculto y no instala un servicio del sistema. La app gráfica puede aplicar sus propias preferencias de red y carga; el comando evita esas sustituciones. `-ModelsPath` permite conservar una carpeta de modelos personalizada y persistirla en `OLLAMA_MODELS`. Si el equipo se reinicia o el proceso se cierra, volvé a iniciar el servidor configurado; si se suspende, las devoluciones quedan temporalmente fuera de servicio.

La [memoria vectorial](memoria-vectorial.md) añade `embeddinggemma` en CPU y permite mantenerlo cargado junto a Gemma 4. La cola persistente indexa sólo textos nuevos o modificados y recupera antecedentes sin llamadas generativas. Un clic de conversación hace una sola generación; los informes completos conservan varias llamadas. `--live-embeddings` evalúa el modelo de embeddings con textos ficticios.

## Privacidad y acceso compartido

- Solo el backend obtiene el contexto de la cuenta autenticada; cada lectura de entradas y archivos se filtra por usuario.
- Las llamadas de IA se restringen a una dirección IP de loopback, sin proxy, sin redirecciones y con modelo fijo. La aplicación no conserva estado conversacional en el motor entre usuarios.
- Ollama tiene las funciones cloud desactivadas. Los registros propios de la integración no incluyen prompts, imágenes ni respuestas. No actives registro de cuerpos HTTP para este contenido.
- SQLite vive en `App_Data`; los originales están en `uploads/<código de cuenta>`, fuera de Git y de `wwwroot`. Los metadatos y el texto extraído siguen en SQLite. Respaldá ambas carpetas juntas, como explica [la guía de uploads](uploads.md). Los archivos de modelos y registros de Ollama se guardan en su configuración local. Mantené estos datos fuera de carpetas sincronizadas con servicios cloud y controlá las copias de seguridad.
- Para usuarios en otras máquinas, publicá únicamente la aplicación con autenticación y HTTPS dentro de la red o VPN definida para el piloto. El puerto 11434 permanece en loopback y no requiere abrirse en el firewall.
- Desactivar cloud no bloquea por sí solo las descargas ni actualizaciones. Para un despliegue sin salida a Internet, descargá antes todos los paquetes y modelos y aplicá la política de egreso del servidor; verificá el flujo completo con esa política activa. Esta integración no modifica las reglas globales del firewall.
- La administración técnica del servidor puede acceder a los archivos y copias; no se ofrece cifrado de extremo a extremo.

El campo histórico `GeminiConsent` se conserva para mantener compatibles las bases y exportaciones existentes. Ya no habilita llamadas externas ni condiciona el análisis local. No se reinterpretan permisos antiguos como una autorización a otro servicio cloud.

## Verificación

La pestaña **Devolución** usa el mismo modelo y la misma cola local para analizar los textos del cuaderno completo por partes. Sus prompts están en `Services/JournalAiTopics.cs`; los de una entrada individual siguen en `Services/JournalAiReflection.cs`. El análisis por temas se inicia únicamente con el botón y guarda una versión por cuenta. Ver [la guía de devolución por temas](devolucion-por-temas.md). Para probarlo con textos inventados: `dotnet run --project tests/MiBitacora.Checks -- --live-topics`.

```powershell
dotnet build MiBitacora.csproj
dotnet run --project tests/MiBitacora.Checks
dotnet run --project tests/MiBitacora.Checks -- --live
Get-NetTCPConnection -State Listen -LocalPort 11434
ollama ps
```

La escucha debe ser `127.0.0.1` (o `::1`), nunca `0.0.0.0` ni `::`. Las pruebas automáticas comprueban transporte local, imágenes, formatos no compatibles, citas, errores, cancelación de la cola y aislamiento entre cuentas. Las pruebas reales usan una nota inventada y una imagen de un cuadrado rojo, sin leer el cuaderno del usuario.

Verificado el 9 de noviembre de 2026 en Windows con Core Ultra 7 268V, Arc 140V integrada y 32 GB de RAM, Ollama 0.40.15 y `qwen3-vl:4b-instruct` con digest corto `84f7ed8042fa`. `ollama ps` informó 100 % GPU, 4,4 GB y contexto 8192. Las dos devoluciones sintéticas tardaron 16,3 s (texto) y 18,2 s (imagen); son mediciones puntuales, no una garantía de latencia o concurrencia. La web y Ollama escucharon exclusivamente en `127.0.0.1`; se comprobaron las redirecciones al inicio de sesión para la bitácora y los adjuntos. No se realizó una prueba con todo el egreso bloqueado por firewall.

Referencias: [Windows](https://docs.ollama.com/windows), [modo local](https://docs.ollama.com/faq#how-do-i-disable-ollamas-cloud-features), [visión](https://docs.ollama.com/capabilities/vision), [modelo](https://ollama.com/library/qwen3-vl:4b-instruct).
