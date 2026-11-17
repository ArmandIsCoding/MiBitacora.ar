# Inicio, planes y acompañamiento entre sesiones

La portada presenta a Brisa como una asistente de IA para registrar experiencias, ordenar preguntas y preparar temas para conversar en terapia. Invita expresamente a buscar un profesional si todavía no se comenzó un proceso y aclara que la IA puede equivocarse, no diagnostica ni reemplaza a un terapeuta.

## Dos opciones con la misma jerarquía

El inicio muestra desde el primer bloque que existe un plan Gratuito de $0 sin tarjeta y un plan Profundo en pesos argentinos. Las dos tarjetas comparten tamaño, tipografía, bordes, fondo y estilo de botón; ninguna aparece como recomendada o preseleccionada. En pantallas pequeñas se apilan en el mismo orden: Gratuito y Profundo.

`Components/Shared/PlanComparison.razor` se utiliza tanto en `/` como en `/planes`, para conservar una sola presentación de sus características. El precio y el cupo se obtienen de `SubscriptionOptions`, igual que en la contratación: inicialmente ARS 15.000 finales por mes y 200 créditos por período. Se explicitan la renovación automática, la cancelación desde Mi plan y el aviso de consumo al 80%. Los detalles del cupo, transcripciones, informes, reintegros y almacenamiento siguen en `/planes`.

El acceso gratuito lleva al registro; el acceso a Profundo lleva a Mi plan y requiere iniciar sesión. La portada no autoriza suscripciones ni inicia cobros. Los enlaces de condiciones, privacidad, soporte, baja y arrepentimiento siguen disponibles.

## Marca Mercado Pago

`wwwroot/brand/mercado-pago.svg` proviene del [paquete oficial de logos de Mercado Pago](https://www.mercadopago.com.ar/mp/logo-oficial), descargado el 17 de noviembre de 2026. Archivo original dentro del paquete: `Logos Mercado Pago 2025/Uso digital - RGB/SVGs/MP_RGB_HANDSHAKE_color_horizontal.svg`.

Se conserva el SVG original, sin cambios de colores, proporciones ni trazados, sobre fondo blanco y con texto alternativo «Mercado Pago». Su SHA-256 es `91EEBACEAF93EEDDF67B3A157B3FB9E2D0F741F2CA69FF4D8BB131A7E43AA6D4`. `.gitattributes` preserva sus bytes al clonar en Windows. Se sirve desde el propio sitio; la visualización del logo no realiza solicitudes a Mercado Pago.

## Verificación

Se revisaron el inicio y `/planes` en escritorio, tablet de 768 px y móviles de 390 y 320 px: sin desbordamiento horizontal, tarjetas de igual tamaño en escritorio, botones de igual énfasis y logo cargado. Se comprobaron los accesos al registro, al inicio de sesión previo a Mi plan y las anclas de planes y terapia. La revisión utiliza una base local vacía, sin correos, llamadas de IA ni cobros reales.

Estos cambios modifican la presentación pública. No cambian permisos de los planes, créditos, procesamiento de pagos ni configuración privada.
