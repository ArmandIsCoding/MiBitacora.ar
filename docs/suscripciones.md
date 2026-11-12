# Suscripciones y planes de IA

Implementación preparada en una rama separada, sin despliegue, credenciales de Mercado Pago ni operaciones en la cuenta del propietario. La configuración publicada mantiene `Subscriptions:Provider=Disabled` y `AllowLiveRequests=false`. Development usa un simulador propio; no es el entorno de pruebas de Mercado Pago.

## Comportamiento

| Cuenta | Nuevas devoluciones, informes y transcripción |
| --- | --- |
| Gratuita, sin pago o período vencido | Ollama local, `gemma4:12b` |
| Con una cuota aprobada que cubre el momento actual | OpenAI, con el modelo y las credenciales ya configurados |
| Suscripción autorizada, sin pago aprobado | Continúa gratuita |
| Renovación rechazada | Conserva únicamente los períodos que ya pagó |
| Renovación cancelada | Conserva el período abonado; termina la renovación |
| Pago devuelto o con contracargo | Ese pago deja de acreditar acceso |

Todas las funciones del cuaderno siguen disponibles para cuentas gratuitas. El aviso del plan gratuito y **Mi cuenta → Mi plan** explican las diferencias. No se promete exactitud de ninguna IA. Las devoluciones ya guardadas siguen disponibles al cambiar de plan.

Cada operación obtiene la cuenta desde la sesión autenticada y consulta el acceso en SQLite. El cliente no decide si una cuenta es paga. Antes de cada solicitud a OpenAI se vuelve a verificar la vigencia; si cambió durante un informe, la operación se interrumpe conservando el informe previo. No hay cambio silencioso entre proveedores. Las colas de Ollama y OpenAI son independientes. La [memoria de conversación usa embeddings locales](memoria-vectorial.md) en ambos planes y no se reconstruye al cambiar de plan. El informe completo conserva síntesis generativas diferenciadas por proveedor.

`AI:Provider` se conserva por compatibilidad con herramientas anteriores; la web elige por plan. Configurar `AI:Provider=OpenAI` no convierte cuentas gratuitas en pagas.

## Probar sin dinero ni cuenta de Mercado Pago

Requisitos: SDK .NET 10; Node para las comprobaciones de JavaScript. Ollama y Gemma sólo son necesarios si se quieren generar devoluciones reales.

Desde la carpeta de esta rama:

```powershell
.\scripts\Run-SubscriptionsLocal.ps1
```

Abrir [Mi plan local](http://127.0.0.1:5264/Account/Manage/Subscription). El script escucha sólo en loopback, fuerza `Simulated` y `AllowLiveRequests=false`, y usa `.artifacts/local-subscriptions.db`. No modifica variables permanentes, inicia descargas, reinicia Ollama ni copia la base de producción. Los adjuntos de estas cuentas de prueba se guardan en el `uploads` de este checkout. Ctrl+C cierra la aplicación.

1. Registrar una cuenta local nueva. Verificar el aviso gratuito y el acceso a conversación, informes y demás secciones.
2. Abrir **Mi plan**, aceptar las condiciones y elegir **Probar contratación simulada**.
3. **Autorizar sin pagar** conserva el plan gratuito. **Aprobar primera cuota** habilita Profundo para esa cuenta.
4. Volver a **Mi plan** para ver la vigencia y el historial. **Abrir simulador local** permite agregar una renovación, rechazarla o devolver el último pago.
5. Cancelar desde **Cancelar renovación → Confirmar**. La cuenta conserva el período abonado y luego vuelve automáticamente al acceso gratuito. No hace falta una tarea que cambie banderas de usuarios.
6. Abrir otra cuenta en una ventana privada y verificar que siga gratuita.

**ARS 8000 es exclusivamente un precio de prueba.** El precio comercial queda por decidir. El simulador modifica el acceso local: si se pide una devolución desde una cuenta simulada paga y hay credenciales OpenAI configuradas, esa devolución utiliza realmente OpenAI. Las pruebas automáticas usan transportes falsos y no consumen IA ni realizan cobros.

Las acciones del simulador exigen Development, configuración Simulated, conexión loopback, sesión de la cuenta propietaria y antiforgery. Un registro simulado nunca acredita acceso en Production. El formulario de prueba no está disponible en producción.

## Implementación de Mercado Pago

Se usa **suscripción sin plan asociado, con pago pendiente**. No necesita crear previamente un plan comercial en la cuenta de Mercado Pago: cada contratación crea su propia suscripción con el importe mensual definido en el servidor. No se capturan tarjetas en Mi Bitácora.

- El POST autenticado `/Account/Subscription/Subscribe` exige consentimiento y antiforgery. Guarda una intención con referencia opaca `mb_<UUID>` antes de llamar a `POST /preapproval`. El importe se toma de la configuración, nunca del formulario.
- El backend envía correo del pagador, referencia, razón, recurrencia mensual ARS y URL de retorno. No envía entradas, informes ni notas a Mercado Pago. Redirige al checkout HTTPS validado de Mercado Pago Argentina.
- Volver desde el checkout **no habilita** el plan. Tampoco lo habilita el estado `authorized`: hace falta un pago aprobado, consultado en la API y relacionado con una cuota de esa suscripción.
- El webhook `POST /api/mercadopago/webhook` comprueba `x-signature`, `x-request-id`, `data.id` y antigüedad de diez minutos. Liga el identificador firmado de la URL al cuerpo. Acepta `payment`, `subscription_preapproval` y `subscription_authorized_payment` y guarda una bandeja durable antes de responder 200.
- El trabajador procesa esa bandeja y consulta la API para verificar comerciante, referencia, moneda, importe, recurrencia y relación cuota/pago/suscripción. No confía en estados enviados por el navegador ni en el cuerpo del webhook como prueba de pago.
- La conciliación periódica y **Actualizar estado** recuperan notificaciones perdidas, aprobaciones tardías, cancelaciones, devoluciones y contracargos. Incluye las suscripciones canceladas y los intentos de pago anteriores.
- Índices únicos y verificaciones de versión evitan duplicar pagos y meses. SQLite impide dos contrataciones activas del mismo proveedor para una cuenta, incluso si se superponen procesos durante un reinicio de IIS. Si una cuota cambia de identificador de pago al reintentarse, se conservan ambos intentos, con un único período cubierto. Los períodos contiguos se unen, sin extenderse por recibir otra vez el mismo evento. Se conserva el ancla mensual del día 31 al atravesar febrero.
- Un error de red o 5xx al crear deja la intención pendiente: no se inicia otra suscripción. La conciliación busca la referencia exacta en todas las páginas y recupera el enlace. Un rechazo definitivo 400/401/403/422 deja la intención rechazada y permite volver a contratar. Una búsqueda ambigua queda para revisión, sin iniciar un segundo cobro.
- Cancelar persiste primero el pedido; confirma la respuesta de Mercado Pago antes de marcar la cancelación completa. El trabajador reintenta si falla. Se bloquean nuevas contrataciones mientras haya una anterior activa o quede un período pago, para evitar renovaciones superpuestas.
- Borrar la cuenta requiere confirmar primero la cancelación remota. La contratación y el borrado comparten un bloqueo por cuenta. Una falla conserva la cuenta y el pedido pendiente. La exportación JSON incluye el estado de la suscripción y su historial de pagos.

El trabajador procesa hasta 25 eventos y 25 suscripciones pendientes por vuelta. Los errores de webhook tienen espera progresiva hasta una hora. Los intentos de conciliación se registran para que una suscripción que falla no postergue indefinidamente las demás. El transporte tiene plazo de 30 segundos por solicitud, host fijo, sin proxy ni redirecciones y respuestas acotadas. No registra credenciales ni cuerpos HTTP. Los límites de búsqueda son de 10.000 resultados y las entregas se limitan a 16 KiB.

## Configuración futura, todavía sin activar

La activación requiere una decisión posterior del propietario. No se realizó ninguno de estos pasos en la cuenta de Mercado Pago.

| Configuración privada | Uso |
| --- | --- |
| `Subscriptions:Provider=MercadoPago` | Selecciona el gateway real |
| `Subscriptions:AllowLiveRequests=true` | Habilitación explícita adicional |
| `Subscriptions:AccessToken` | Token del comerciante, sólo backend |
| `Subscriptions:CollectorId` | ID numérico del comerciante esperado |
| `Subscriptions:WebhookSecret` | Clave de firma de notificaciones, mínimo 16 caracteres |
| `Subscriptions:PublicBaseUrl` | Origen HTTPS público, sin rutas ni parámetros |
| `Subscriptions:MonthlyPriceArs` | Precio mensual positivo, hasta dos decimales |
| `Subscriptions:ReconcileMinutes` | Intervalo periódico, 15 por defecto |

Las opciones admiten el archivo privado `App_Data/ai.settings.json`, secretos de .NET en desarrollo y variables `Subscriptions__...`. No colocar secretos en `appsettings.json`, Git, comandos compartidos ni código cliente. La aplicación valida la configuración al iniciar. Desactivar nuevas contrataciones no elimina períodos abonados ya acreditados; desactivar el gateway también impide cancelaciones remotas, por lo que no debe hacerse como sustituto de cancelar suscripciones activas.

Para una validación externa posterior se necesitarán cuentas de prueba separadas, credenciales del vendedor de prueba, un origen HTTPS de prueba y las notificaciones configuradas para `/api/mercadopago/webhook`. Verificar checkout, firma, primer pago, renovación y cancelación de extremo a extremo antes de habilitar usuarios reales. Nunca hacer esta validación con los ahorros del propietario.

La actualización agrega `BillingSubscriptions`, `BillingPayments` y `BillingWebhooks` mediante una migración EF; no altera el contenido del cuaderno. La app aplica migraciones al iniciar. Antes de un eventual despliegue, respaldar juntos SQLite y `uploads`, según la guía existente. Esta rama no se despliega automáticamente.

## IA local y topología pendiente

El valor predeterminado es `gemma4:12b`. No se ejecutó la descarga ni se reinició el Ollama que estaba descargándolo. La transcripción usa `/v1/audio/transcriptions` con WAV; el grabador y su botón **Subir audio** convierten a WAV mono 16 kHz en el navegador para las cuentas gratuitas. Un archivo de audio adjuntado por otra vía necesita WAV para transcripción local. Si el navegador no puede convertirlo o el modelo no admite audio, el original sigue disponible y se muestra un error; no se envía a OpenAI.

La dirección de Ollama sigue restringida a loopback. El servidor web de bajo consumo y la notebook son equipos diferentes: antes de producción falta definir un canal privado y protegido entre ellos, o un proxy local autenticado sobre un túnel privado. No se abrió el puerto de Ollama, no se modificó la red y no se resolvió esa topología en esta rama.

## Verificación de esta entrega

```powershell
.\scripts\Test-Subscriptions.ps1
```

Incluye la suite previa y pruebas de SQLite/migración, acceso por cuenta, idempotencia, primer pago, fechas de renovación, rechazo, devolución, timeout ambiguo, reintentos, cancelación y borrado. Ejecuta los delegados HTTP reales en proceso con antiforgery y firma, y renderiza el componente Razor de Mi plan. Verifica transporte de Mercado Pago con respuestas de contrato simuladas, incluido un cuerpo que no entrega datos después de las cabeceras: el plazo HTTP debe interrumpir también esa lectura y permitir reintentar la conciliación. Comprueba texto/audio por plan, ausencia de fallback externo para cuentas gratuitas, WAV y el ciclo del grabador con Node.

El entorno de trabajo bloqueó la conexión al puerto local de Ollama y el acceso del navegador a Kestrel. Por eso no se afirma una prueba visual en navegador ni una prueba real de inferencia con Gemma; faltan esos recorridos y la integración externa con Mercado Pago. Las comprobaciones de transporte y de interfaz en proceso sí se ejecutaron con datos inventados. No se leyó ni modificó el diario de producción para estas pruebas.

Verificación final del 11/11/2026 en el repositorio de desarrollo: compilación sin advertencias ni errores, 279 comprobaciones .NET y 9 pruebas de JavaScript correctas. Incluye la corrección del plazo de espera durante la lectura del cuerpo de Mercado Pago. Los cobros reales permanecen deshabilitados; esta verificación no sustituye el recorrido externo pendiente.

Referencias oficiales consultadas el 11/11/2026: [crear suscripción con pago pendiente](https://www.mercadopago.com.ar/developers/es/docs/subscriptions/integration-configuration/subscription-no-associated-plan/pending-payments), [gestión](https://www.mercadopago.com.ar/developers/es/docs/subscriptions/subscription-management), [API de cuotas](https://www.mercadopago.com.ar/developers/es/reference/online-payments/subscriptions/authorized-payment-search/get), [webhooks](https://www.mercadopago.com.ar/developers/es/docs/subscriptions/additional-content/your-integrations/notifications/webhooks), [audio Ollama](https://docs.ollama.com/api/openai-compatibility), [alcance del plazo de espera con ResponseHeadersRead](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0).
