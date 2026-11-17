# Lanzar Profundo con Mercado Pago

El código ofrece Profundo a **ARS 15.000 finales por mes**, con 200 créditos por período. No se realizaron cobros ni operaciones en tu cuenta Mercado Pago y el repositorio conserva los cobros deshabilitados. Hace falta completar esta configuración y validar el circuito externo antes de abrir las ventas.

## Lo que debe hacer Armando en Mercado Pago

1. Ingresar con la cuenta argentina que cobrará por COGNITIVA y completar identidad, datos fiscales y habilitación comercial que solicite Mercado Pago.
2. Abrir [Tus integraciones](https://www.mercadopago.com.ar/developers/panel/app), crear o seleccionar la aplicación de Mi Bitácora y habilitar el producto de **Suscripciones** disponible en la cuenta. Configurar `https://mibitacora.ar` como sitio. No hace falta crear un plan de suscripción compartido de $15.000: el backend crea cada suscripción individual con ese importe y frecuencia mensual.
3. Obtener el **Access Token de producción** de esa aplicación y el **ID del usuario vendedor / collector**. Guardarlos solo en la configuración privada del servidor, con acceso del administrador y de la identidad de IIS. No enviarlos por chat ni guardarlos en Git. El token debe permitir gestionar las suscripciones, consultar pagos y devolverlos. El control `--check-merchant` comprueba que pertenece al collector configurado y al sitio argentino `MLA` mediante una consulta de lectura.
4. Revisar la modalidad de **notificaciones de Suscripciones** que muestre tu aplicación. URL receptora: `https://mibitacora.ar/api/mercadopago/webhook`. Tipos que procesa el código: `payment`, `subscription_preapproval`, `subscription_authorized_payment`. Si MP proporciona una clave secreta de firma para esa modalidad, guardarla como `Subscriptions:WebhookSecret` y comprobar una entrega real firmada.
5. Preparar una prueba con cuentas/medios de prueba admitidos por Suscripciones, separados del vendedor. Después de comprobar el entorno de prueba, realizar una prueba productiva controlada con un comprador autorizado, verificando el importe final, primer cargo, baja y reintegro. No usar el propio vendedor como comprador ni asumir que el simulador local prueba MP.

**Particularidad vigente en la documentación:** la [guía de notificaciones de Suscripciones](https://www.mercadopago.com.ar/developers/es/docs/subscriptions/additional-content/your-integrations/notifications/webhooks) indica que la configuración por “Tus integraciones” no está disponible para Suscripciones y remite a la creación del pago, aunque su tabla también enumera eventos de suscripciones. El código envía `notification_url` al crear la preaprobación, pero ese campo no aparece en el esquema publicado de creación. Verificar su aceptación y entrega en tu aplicación; si no ofrece clave o no notifica, confirmar la modalidad con soporte de MP. **No inventar una clave ni aceptar webhooks sin firma**. Puede dejarse `WebhookSecret` vacío: el endpoint queda deshabilitado y se continúa conciliando por API, automáticamente y desde “Actualizar estado”. Para ese modo conviene `ReconcileMinutes=1` al lanzar, vigilar latencia y consumo de API. La falta de notificación no debe habilitar acceso sin pago.

Las [suscripciones pendientes sin plan asociado](https://www.mercadopago.com.ar/developers/es/docs/subscriptions/integration-configuration/subscription-no-associated-plan/pending-payments) usan el checkout alojado de Mercado Pago. Los reintegros emplean [Refunds API](https://www.mercadopago.com.ar/developers/en/reference/online-payments/checkout-api-payments/create-refund/post) con clave de idempotencia y verificación posterior del pago.

## Configuración privada

Combinar estas secciones en `App_Data/ai.settings.json`, conservando OpenAI, Ollama y el puente de inferencia existentes. Alternativamente usar variables de entorno con doble guion bajo. No reemplazar el archivo completo ni copiar valores reales a un ejemplo versionado. Los siguientes valores entre `<...>` son marcadores, no credenciales válidas:

```json
{
  "AI": { "OpenAiForAllAccounts": false },
  "Subscriptions": {
    "Provider": "MercadoPago",
    "AllowLiveRequests": true,
    "AcceptNewSubscriptions": false,
    "LaunchUserIds": ["<ID de la cuenta de lanzamiento>"],
    "AccessToken": "<ACCESS TOKEN PRIVADO>",
    "CollectorId": "<ID NUMERICO DEL VENDEDOR>",
    "WebhookSecret": "",
    "PublicBaseUrl": "https://mibitacora.ar",
    "MonthlyPriceArs": 15000,
    "MonthlyCredits": 200,
    "ReportCredits": 10,
    "WithdrawalDays": 10,
    "ReconcileMinutes": 1
  },
  "Mail": {
    "Delivery": "Smtp",
    "Host": "<SERVIDOR SMTP>",
    "Port": 587,
    "EnableSsl": true,
    "Username": "<USUARIO SMTP>",
    "Password": "<CLAVE SMTP>",
    "From": "<REMITENTE AUTORIZADO POR ESE SMTP>"
  },
  "Administration": { "UserIds": ["<ID de la cuenta administradora>"] }
}
```

Se necesita un proveedor SMTP con STARTTLS (habitualmente 587), remitente autorizado y SPF/DKIM/DMARC configurados según ese proveedor. El puerto 465 con TLS implícito no está soportado por `SmtpClient`; usar el puerto STARTTLS del proveedor. No asumir que el correo iCloud de contacto sirve automáticamente como servicio SMTP transaccional. Verificar entrega efectiva de recuperación, confirmación y constancia comercial en una casilla externa y en correo no deseado.

`AcceptNewSubscriptions=false` permite preparar la instalación manteniendo la gestión de clientes existentes. Para probar checkout productivo, ponerlo en `true` **con `LaunchUserIds` limitado a los usuarios de prueba autorizados**. El servidor aplica esa lista, incluso ante un POST manipulado. Cuando se complete la aceptación, vaciar la lista y mantener `true` para abrir al público. Reiniciar IIS después de cada cambio.

## Comprobación previa sin modificar datos

Desde la carpeta publicada, bajo la misma identidad/configuración de producción:

```powershell
dotnet MiBitacora.dll --check-billing
dotnet MiBitacora.dll --check-merchant
```

El primer comando no abre SQLite ni llama servicios externos. El segundo agrega solamente `GET https://api.mercadopago.com/users/me`; no crea ni cancela suscripciones. No imprimen claves ni datos del diario. Salida 2 indica configuración pendiente. Estos controles no verifican por sí mismos SMTP, modelos, checkout, notificaciones, renovación ni reintegros.

## Publicación y aceptación

1. Compilar Release y ejecutar la suite. Crear respaldo consistente de SQLite con la app detenida (o API de backup), uploads y claves de Data Protection. Conservar configuración privada y códecs. `dotnet publish -c Release -o <carpeta de staging>` no debe incluir App_Data, uploads ni credenciales.
2. Publicar con IIS detenido o `app_offline.htm`, preservando `App_Data`, `uploads`, `media-tools` y sus ACL. La migración `LaunchRecurringSubscriptions` agrega consumo, solicitudes, correos, auditoría y campos de aceptación. `PreserveBillingAfterAccountDeletion` reconstruye únicamente la tabla comercial para separar su conservación del borrado de cuenta. No reconstruye las tablas de entradas ni sus índices FTS. No volver a un binario anterior sobre una base nueva sin restaurar el respaldo completo.
3. Configurar Application Pool en **AlwaysRunning**, preload y sin suspensión por inactividad, según la versión instalada de IIS/Application Initialization. Mantener una sola aplicación escritora y respaldos probados. Los trabajadores de conciliación y correo viven dentro de la web: si está detenida, no procesan pendientes. Monitorizar el sitio, los logs y `/admin/suscripciones`; los errores no deben quedarse pendientes sin atención.
4. Verificar HTTPS, correo transaccional, inicio/cierre de sesión, recuperación y confirmación. Las claves estables en `App_Data/data-protection` deben sobrevivir despliegues, y la cuenta de IIS debe poder abrirlas con DPAPI. El primer cambio del nombre de aplicación puede cerrar sesiones anteriores.
5. Mantener datos ficticios separados de producción. Verificar la IA local del plan gratuito y una respuesta/informe de OpenAI con cuenta autorizada y saldo. Revisar también agotamiento y cambio a IA local. Configurar el presupuesto/alertas del proyecto OpenAI.
6. Con ventas limitadas por `LaunchUserIds`, aceptar expresamente ARS 15.000 mensuales, completar el checkout y comprobar que la autorización sola no activa el plan, el primer pago aprobado sí, y su período/constancia coinciden. Doble clic y regreso del checkout no deben generar otra suscripción.
7. Verificar el rechazo/reintento, la conciliación sin webhook, entrega firmada si está disponible, cancelación normal, pedido público sin sesión y reintegro al medio original. Confirmar ausencia de nuevas renovaciones después de la baja. Verificar que un cargo tardío se devuelve y que los avisos llegan. Validar una renovación real/controlada antes de afirmar que el ciclo completo de recurrencia fue probado; no simularlo modificando fechas de pagos productivos.
8. Revisar con el titular los datos, condiciones y política publicados. Sostener atención de lunes a viernes 9–17. Preparar con el contador el punto de venta, emisión y entrega de **factura C** y notas de crédito, y el plazo de conservación fiscal. Registrar el número emitido en el panel. La constancia del sitio no sustituye esa emisión ni se debe esperar a que el cliente la pida.
9. Registrar fecha, IDs de operación y resultado de la aceptación sin secretos. Vaciar `LaunchUserIds` para abrir ventas cuando corresponda. Para detener nuevas altas usar `AcceptNewSubscriptions=false`; conservar `Provider=MercadoPago` y `AllowLiveRequests=true` mientras existan obligaciones de conciliación, baja o reintegro.

## Operación diaria

Revisar pagos, movimientos de caja reales de MP (comisiones incluidas), solicitudes pendientes, colas con varios intentos, conciliaciones demoradas, facturas C y notas de crédito. El panel informa movimientos según fecha de conciliación y MRR contratado, no reemplaza el extracto de MP ni la contabilidad. Un reintegro parcial o una disputa compleja iniciada fuera del sitio se revisa en MP y soporte; la conciliación conserva el importe reintegrado y los estados finales. Conservar evidencia comercial mínima separada del diario, y limitar acceso y retención de correos/tokens, soporte y respaldos. Verificar restauraciones periódicamente.

La recepción sin registro, constancia y verificación razonable de identidad se implementó tomando como referencia la [Disposición 954/2025](https://www.argentina.gob.ar/normativa/nacional/norma-417152/texto), su [aclaración 3/2026](https://www.boletinoficial.gov.ar/detalleAviso/primera/338248/20260206) y la [Ley 24.240](https://www.argentina.gob.ar/normativa/nacional/638/actualizacion). La emisión fiscal se apoya en la [guía oficial de comprobantes para monotributistas de ARCA](https://www.arca.gob.ar/facturacion/monotributo/comprobantes.asp). La garantía comercial de diez días sobre el último cargo es voluntariamente favorable y no sustituye otros derechos aplicables. El titular debe revisar estos textos y la operación fiscal antes de abrir ventas.
