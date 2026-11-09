# Ollama y Qwen3-VL 4B en Mi Bitácora

El backend Blazor (.NET 10) llama a `http://127.0.0.1:11434/api/chat`. Los navegadores se conectan a Mi Bitácora: los usuarios no necesitan instalar Ollama ni pueden elegir el servidor o el modelo desde el cliente.

Se fija la variante **Instruct** de Qwen3-VL 4B. Al verificar este despliegue, la etiqueta genérica `qwen3-vl:4b` resolvía a Thinking y agotaba el presupuesto de salida antes de entregar una devolución; `think=false` no evitó ese comportamiento. La variante explícita hace reproducible la elección.

## Configuración del piloto

```json
"Ollama": {
  "Enabled": true,
  "BaseUrl": "http://127.0.0.1:11434",
  "Model": "qwen3-vl:4b-instruct",
  "ContextLength": 8192,
  "MaxOutputTokens": 1536,
  "TimeoutSeconds": 300
}
```

`Ollama__Enabled=false` activa la muestra determinista identificada como tal. Con la IA activada, los errores se muestran para reintentar: no se sustituyen silenciosamente por muestras ni se envía contenido a la nube. Un semáforo compartido permite una generación y hasta ocho solicitudes en espera por proceso de aplicación. El tiempo máximo incluye la espera; la cancelación del componente libera su lugar en la cola. Si se despliegan varios procesos, este límite debe coordinarse entre ellos.

Se envían fragmentos acotados de la entrada, antecedentes, notas, perfil y texto extraído. La selección textual tiene un presupuesto de 6500 caracteres, además de etiquetas e instrucciones; no incluye el cuaderno completo. Las imágenes JPEG, PNG y WebP se envían como base64: hasta dos y 12 MiB combinados por devolución. Las imágenes grandes pueden requerir más memoria y tiempo. El piloto no convierte PDF ni HEIC/HEIF a imágenes. Tampoco analiza audio, GIF o AVIF.

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
| `OLLAMA_MAX_LOADED_MODELS` | `1` |
| `OLLAMA_MAX_QUEUE` | `8` |
| `OLLAMA_VULKAN` | `1` |
| `OLLAMA_IGPU_ENABLE` | `1` |
| `OLLAMA_DEBUG_LOG_REQUESTS` | `false` |

Ollama debe ejecutarse con esa cuenta de Windows. En un servicio de Windows independiente hay que configurar el entorno de su cuenta de servicio. Este piloto usa la app de Ollama de la sesión del usuario; no instala un servicio del sistema. Revisá que Ollama arranque al iniciar sesión. Si el equipo se suspende o el proceso se cierra, las devoluciones quedan temporalmente fuera de servicio.

## Privacidad y acceso compartido

- Solo el backend obtiene el contexto de la cuenta autenticada; cada lectura de entradas y archivos se filtra por usuario.
- Las llamadas de IA se restringen a una dirección IP de loopback, sin proxy, sin redirecciones y con modelo fijo. La aplicación no conserva estado conversacional en el motor entre usuarios.
- Ollama tiene las funciones cloud desactivadas. Los registros propios de la integración no incluyen prompts, imágenes ni respuestas. No actives registro de cuerpos HTTP para este contenido.
- SQLite y adjuntos viven en `App_Data`, fuera de Git. Los archivos de modelos y registros de Ollama se guardan en su configuración local. Mantené ambos fuera de carpetas sincronizadas con servicios cloud y controlá las copias de seguridad.
- Para usuarios en otras máquinas, publicá únicamente la aplicación con autenticación y HTTPS dentro de la red o VPN definida para el piloto. El puerto 11434 permanece en loopback y no requiere abrirse en el firewall.
- Desactivar cloud no bloquea por sí solo las descargas ni actualizaciones. Para un despliegue sin salida a Internet, descargá antes todos los paquetes y modelos y aplicá la política de egreso del servidor; verificá el flujo completo con esa política activa. Esta integración no modifica las reglas globales del firewall.
- La administración técnica del servidor puede acceder a los archivos y copias; no se ofrece cifrado de extremo a extremo.

El campo histórico `GeminiConsent` se conserva para mantener compatibles las bases y exportaciones existentes. Ya no habilita llamadas externas ni condiciona el análisis local. No se reinterpretan permisos antiguos como una autorización a otro servicio cloud.

## Verificación

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
