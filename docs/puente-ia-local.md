# Ollama en la PC y Mi Bitácora en IIS

El servidor web puede usar los modelos de la PC mediante un agente propio. La PC realiza conexiones **salientes por HTTPS** a `https://mibitacora.ar`; Ollama sigue escuchando exclusivamente en `127.0.0.1:11434`. No se abre un puerto del firewall, se habilita administración remota ni se publica la API de Ollama.

La PC tiene que permanecer encendida, conectada y con la cuenta de Windows iniciada. Al suspenderla o apagarla, las nuevas devoluciones gratuitas quedan temporalmente sin servicio; las entradas y devoluciones guardadas siguen disponibles. No se cambia automáticamente a OpenAI. El agente arranca al iniciar sesión mediante una entrada propia en `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Si Ollama no está disponible al arrancar, inicia `ollama serve` oculto con cloud deshabilitado y los límites ya usados en desarrollo; no descarga modelos ni interrumpe uno que ya está ejecutándose.

## Instalación

Primero preparar ambos modelos con la [configuración local](ia-local.md). Luego, desde el repositorio:

```powershell
.\scripts\Install-LocalInferenceAgent.ps1
```

El script compila el agente y lo instala en `%LOCALAPPDATA%\MiBitacora\inference-agent\versions\<commit>`. Genera una credencial aleatoria de 256 bits en `agent.settings.json`, con permisos sólo para la cuenta actual y SYSTEM. Ese archivo no debe copiarse a Git, incluirse en publicaciones ni compartirse. `server-settings.json` contiene únicamente su hash SHA-256; integrar esa sección `LocalInferenceBridge` en el archivo privado **existente** `App_Data/ai.settings.json` del servidor, conservando OpenAI y las otras opciones. Configurar allí también `Ollama:Model=gemma4:12b`, los límites actuales y `Embeddings` según `appsettings.json`. Los cobros continúan deshabilitados. Reiniciar la aplicación para aplicar la configuración.

El agente valida HTTPS y certificados del sitio, no sigue redirecciones ni usa proxies del sistema. La credencial sólo se envía a ese origen y nunca a Ollama. El servidor verifica su hash con comparación de tiempo constante; los endpoints no admiten la sesión del navegador como sustituto. Se admite una instancia activa del agente; después de un cierre inesperado la concesión anterior vence en 35 segundos. Las tres conexiones de trabajo permiten atender metadatos mientras se calculan una generación y embeddings, respetando las colas de modelos existentes.

## Transporte y límites

Los endpoints autenticados `/api/local-inference/*` intercambian trabajo efímero en memoria; no almacenan otra copia del diario. Sólo se permiten `/api/tags`, `/api/chat`, `/api/embed` y `/v1/audio/transcriptions`, con métodos fijos y destino loopback. No se permiten administración o descarga de modelos, destinos arbitrarios, modelos cloud ni generación en streaming. Se conservan los bytes de las imágenes y el audio. Las comunicaciones hacia el sitio pasan por el mismo alojamiento HTTPS/CDN que la web y no ofrecen cifrado de extremo a extremo.

Hay hasta 16 solicitudes pendientes, 64 MiB de cuerpos activos, 20 MiB por solicitud y 4 MiB por respuesta. El plazo del cliente original sigue aplicándose. El agente consulta cada dos segundos si una solicitud continúa vigente y cancela Ollama si se abandona; un resultado tardío no puede completar otra solicitud. No se reintenta una generación automáticamente. Una desconexión deja la entrada guardada y permite reintentar desde la conversación.

Los registros del agente están en `agent.log`, con rotación a 1 MiB. Registran arranque y disponibilidad, sin textos, audio, respuestas, secretos ni cuerpos HTTP. `GET /api/local-inference/status` y `POST /api/local-inference/probe` exigen la misma credencial privada: el primero informa disponibilidad y contadores; el segundo verifica ambos modelos, un embedding y una generación breve con **datos ficticios fijos**, sin acceder a cuentas o entradas.

## Mantenimiento

Para actualizar, detener únicamente el ejecutable de esta instalación, ejecutar nuevamente el instalador y conservar la configuración privada. Para detener el inicio automático, quitar sólo el valor `MiBitacoraLocalInference` de la clave `Run` indicada y cerrar el agente. Esto no cambia las preferencias generales de Windows ni elimina los modelos.

Para revocar o rotar el acceso, detener el agente, deshabilitar el puente o reemplazar `TokenHash` en el archivo privado del servidor y reiniciar IIS. Para rotar, respaldar de forma privada la configuración del agente, generar otra credencial y actualizar ambos extremos. No editar archivos de credenciales mientras el agente está trabajando.

## Verificación del 12/11/2026

Compilación sin advertencias ni errores, 364 comprobaciones .NET y 14 pruebas JavaScript correctas. Las pruebas del puente cubren autenticación, aislamiento de la instancia, cuerpos y cola acotados, cancelación y resultados tardíos, destinos permitidos, HTTPS y exclusión de modelos cloud y streaming para texto/embeddings. Un servidor web aislado y el ejecutable real del agente completaron una prueba con Gemma y un embedding de 768 dimensiones en 17,47 segundos. Se usaron exclusivamente datos ficticios, sin leer un diario ni realizar llamadas pagas a OpenAI.
