# Suscripciones y operación comercial

Oferta inicial: **Profundo, ARS 15.000 finales por mes**, renovación automática con Mercado Pago. Incluye 200 créditos por período como valor inicial configurable. El código de producción conserva `Provider=Disabled` y `AllowLiveRequests=false` hasta configurar la cuenta. El simulador de Development es propio: no demuestra que una operación real de Mercado Pago funcione. La puesta en marcha está en [lanzamiento-mercado-pago.md](lanzamiento-mercado-pago.md).

## Acceso y consumo

| Situación | Acceso |
| --- | --- |
| Gratuita, vencida o autorización sin pago | IA local |
| Pago aprobado que cubre el momento actual | OpenAI o IA local, a elección |
| Renovación rechazada | Se conserva solo el período ya abonado |
| Baja | Se detienen renovaciones; se conserva lo ya abonado |
| Reintegro total o contracargo | Ese pago deja de habilitar Profundo |
| Cortesía administrativa vigente | OpenAI con cupo, sin cobros |

`AI:OpenAiForAllAccounts` es falso por defecto y se ignora fuera de Development aunque una configuración privada histórica diga `true`. La decisión se revalida antes de enviar contenido a OpenAI. No hay cambio silencioso de proveedor. Un usuario puede elegir IA local al agotar créditos sin perder el período pago. El cuaderno, los archivos guardados, las notitas y los informes anteriores siguen disponibles.

Una respuesta o regeneración consume 1 crédito; cada audio que requiere transcripción, 1 adicional; un informe completo, 10. Reutilizar transcripciones no consume. La reserva se registra en una transacción de escritura SQLite antes del proveedor: incluye operaciones concurrentes y se libera si falla. Una transcripción terminada conserva su consumo aunque después falle la respuesta. Los trabajos abandonados reservan saldo hasta una hora. No hay acumulación ni cobro por excedentes. El crédito de informe corresponde a toda su generación, aunque internamente use varios pedidos al modelo.

El período de uso sigue la cuota aprobada actual. La cortesía usa el mes calendario de Buenos Aires. El precio y `MonthlyCredits` se guardan al contratar; cambiar la oferta no altera suscripciones existentes. No modificar `ReportCredits`, plazos comerciales ni condiciones aceptadas de cuentas vigentes sin diseñar y comunicar la nueva aceptación correspondiente. El aviso al 80% aparece en Mi plan y en el cuaderno; Mi plan siempre consulta el saldo actual. El aviso del cuaderno se actualiza al volver a renderizar, con un máximo de una lectura cada cinco segundos.

El cupo limita acciones, no el costo exacto de tokens: controlar también el presupuesto del proyecto OpenAI, los tamaños de contexto y el margen después de comisiones de Mercado Pago e impuestos. No se envían cobros por sobreconsumo.

## Contratación y conciliación

El checkout es individual mediante `POST /preapproval`, estado `pending`, mensual en ARS, sin plan compartido. El servidor fija el importe y una referencia opaca. Guarda correo de pago, precio, cupo, fecha y versión de condiciones. El POST exige sesión, correo confirmado, antiforgery, aceptación y coincidencia con la oferta mostrada; una página vieja no puede aceptar un precio nuevo sin revisarlo. `LaunchUserIds` permite restringir las primeras contrataciones a cuentas concretas. No se guarda tarjeta.

Autorizar o volver del checkout no acredita acceso. Se consulta el recurso remoto y se verifican vendedor, referencia, moneda, importe y frecuencia. Las cuotas se consultan con paginación, se preservan los intentos rechazados/reemplazados y se revisan también pagos antiguos para detectar reintegros. Los meses mantienen el ancla, incluso al pasar del día 31 a febrero. Los estados terminales no resucitan con un evento atrasado.

Una intención ambigua por timeout se conserva: se busca por referencia y no se crea un segundo débito recurrente. Un rechazo definitivo de creación permite reintentar. Una restricción única de SQLite impide dos intenciones activas de la misma cuenta y proveedor durante un reciclado de IIS.

El webhook exige cuerpo acotado, coincidencia de `data.id`, tipo admitido, HMAC `x-signature`, `x-request-id` y timestamp de hasta diez minutos. Primero se guarda en bandeja durable y después se responde 200. Las entregas idénticas no duplican eventos. El trabajador hace las consultas remotas y reintenta con espera creciente. Recursos que llegan antes de la intención local se reintentan; tras cinco intentos sin coincidencia se descartan como ajenos. La conciliación periódica incluye canceladas y es respaldo independiente de las notificaciones. Si falta clave de firma, el webhook devuelve 404: nunca se habilitan notificaciones sin verificar. Configurar **Planes y suscripciones** y **Pagos (legacy)** en los Webhooks de la aplicación de Mercado Pago; consultar la guía de lanzamiento para verificar la entrega.

`AcceptNewSubscriptions=false` detiene altas, **no** desactiva las consultas, bajas, reintegros o conciliación. No apagar `AllowLiveRequests` para detener ventas si existen cobros activos. Requiere un reciclado de la app porque se carga configuración al iniciar.

## Atención, baja y reintegros

`/baja`, `/arrepentimiento` y `/soporte` son públicos y entregan un código inmediato de recepción. La gestión automática requiere sesión del titular o confirmación de un enlace de correo aleatorio, de un solo uso y válido tres días. Abrir el enlace no modifica cobros; se confirma por POST con antiforgery. La fecha del pedido original se conserva aunque la verificación sea posterior. Sin acceso al correo, soporte verifica por otra vía y gestiona el caso. No se exige registrar una cuenta. Los códigos tienen 96 bits aleatorios; su página pública solo muestra código, tipo, fecha y estado, nunca correo, diario ni resolución privada.

Mi plan también registra el pedido de baja y redirige a su constancia. Se persiste el intento de cancelación antes de consultar MP. Ante fallo queda pendiente, y el trabajador vuelve a intentar. La garantía comercial devuelve el último cargo solicitado dentro de diez días corridos de aprobación, incluso con uso del servicio; fuera de plazo se deriva a revisión. El administrador puede autorizar ese reintegro para una identidad verificada. La devolución consulta el pago antes y después, usa una clave estable por pago y no repite el intento si la devolución ya se confirmó tras un corte de conexión. Un cargo aprobado después del pedido de baja crea su propio reintegro pendiente.

`/admin/suscripciones` requiere la política de administración existente (`Administration:UserIds`). Muestra suscripciones con renovación y acceso actuales, ingreso mensual contratado, movimientos conciliados del mes, colas demoradas, solicitudes y facturas pendientes. Permite reintentar, autorizar reintegros fuera del plazo comercial y responder/cerrar casos. Cerrar manualmente no ejecuta bajas ni reintegros: se usa después de resolverlos. El servidor valida sesión administrativa y antiforgery; las acciones quedan auditadas con código de administrador. No muestra textos del diario.

La política, condiciones y datos de COGNITIVA están en `/condiciones`, `/privacidad` y `/planes`. Atención comercial declarada: lunes a viernes de 9 a 17, Argentina; recepción web permanente. El propietario debe sostener ese horario y revisar solicitudes a diario.

## Correo, identidad y facturación

La atención comercial usa `contacto@cognitiva.ar`. MailKit soporta el puerto 465 con TLS implícito y otros puertos con STARTTLS obligatorio, validando el certificado del servidor y sin permitir un descenso a texto plano.

El correo soporta `Disabled`, `Local` solo en Development, o SMTP autenticado con TLS. La bandeja de salida es durable, con clave única, bloqueo temporal de entrega y reintentos. Después de entregar se elimina el cuerpo con sus tokens; los metadatos entregados se purgan a los 30 días, excepto las claves de correos de pago que evitan reenviar constancias históricas al conciliar. SMTP ofrece entrega al menos una vez: si el proceso cae después de enviar y antes de guardar, un mensaje puede repetirse con el mismo Message-ID. Revisar la cola y el proveedor, no solo que la configuración sea válida. No se incluyen entradas ni contraseñas en correos ni logs.

Recuperación de contraseña y confirmación de correo usan tokens de Identity con vencimiento de dos horas, origen público configurado y respuesta genérica para no enumerar cuentas. Hay límites por IP/cuenta y por correo. La sesión gratuita no exige correo confirmado; la contratación sí. Las claves de Data Protection se conservan en `App_Data/data-protection` y se protegen con DPAPI en Windows. La primera publicación con el nombre estable de aplicación puede cerrar sesiones existentes; conservar claves y la identidad de IIS en adelante. Recuperar una contraseña invalida las sesiones previas al revalidarse.

Las constancias de operación tienen acceso del propietario (`/Account/Subscription/Receipt/{id}`), y no son facturas fiscales. **La emisión y entrega de factura C se hace en ARCA o el sistema fiscal del titular**, para todos los cobros que corresponda; no depende de que el cliente la solicite. El administrador registra el número ya emitido. Ante reintegros revisa la nota de crédito. La integración no inventa CAE ni emite facturas electrónicas.

Al borrar una cuenta, se confirma primero el cese de renovaciones; se eliminan el diario, archivos, memoria, preferencias y consumo. Se conservan suscripciones, pagos, auditoría y solicitudes comerciales mínimas para obligaciones fiscales y reclamos, y para devolver cobros tardíos. Esos registros no restauran acceso al diario ni permiten recrear la cuenta. Definir con el contador los plazos legales de conservación y revisar/purgar mensajes de soporte y colas vencidas según su necesidad. Respaldos, credenciales y correos temporales requieren permisos de servidor y una política de conservación real.

## Pruebas locales

```powershell
.\scripts\Run-SubscriptionsLocal.ps1
```

Base separada: `.artifacts/local-subscriptions.db`. Los correos se escriben en `App_Data/mail-outbox` y no se envían. Confirmar la cuenta con el enlace de ese archivo antes del checkout. La simulación de pagos no consume dinero; si luego se pide IA y hay credenciales reales configuradas, sí puede consumir OpenAI. Las pruebas automáticas usan transportes falsos:

```powershell
dotnet build tests/MiBitacora.Checks -p:UseAppHost=false
dotnet tests/MiBitacora.Checks/bin/Debug/net10.0/MiBitacora.Checks.dll
node --test tests/voice-recorder.test.mjs tests/journal-scroll.test.mjs tests/brisa-motion.test.mjs
```

Se cubren autorización sin pago, repetición, intento ambiguo, moneda/vendedor/referencia, fin de mes, aislamiento, firma y replay, antiforgery, selección de IA, recuperación, reservas concurrentes, reintegros y aprobaciones posteriores a la baja/borrado. Las pruebas de actualización parten de esquemas anteriores y conservan diarios, hilos, búsquedas y archivos. No equivalen a una validación del checkout ni de una renovación en la cuenta real de MP.
