# Privacidad del repositorio público

El repositorio de Mi Bitácora en GitHub es público. La configuración real de Mercado Pago, SMTP, OpenAI y el puente de inferencia se guarda fuera de Git, en la configuración privada de la instalación. `App_Data/`, `uploads/`, `.artifacts/`, archivos de entorno, bases y resultados de compilación están excluidos del repositorio. Agregar un archivo a `.gitignore` no elimina versiones anteriores que ya se hubieran publicado.

Los datos comerciales autorizados son el nombre de COGNITIVA, su CUIT, el contacto comercial y **(3000) Santa Fe, Argentina**. La ubicación pública se centraliza en `BusinessIdentity.Address` y se usa en páginas, constancias y correos. Se omiten calle y altura del domicilio particular. Los datos fiscales que necesite una factura deben gestionarse en el sistema fiscal privado del titular.

Las capturas y ejemplos deben usar datos sintéticos. No publicar diarios reales, adjuntos de usuarios, pantallas con claves visibles, respaldos ni enlaces que contengan tokens de acceso o recuperación. Los metadatos de autor de los commits también son públicos.

## Revisión del 17/11/2026

Se consultaron las tres ramas públicas remotas y su historial completo disponible, con 48 commits alcanzables antes de esta revisión. Gitleaks 8.30.1, usando reglas estándar y sin excepciones por comentarios, examinó 46 commits con cambios y no detectó credenciales.

Además se contrastaron las credenciales reales de la instalación y la contraseña de ingreso proporcionada para la comprobación, sin imprimir sus valores: seis valores privados o sus huellas, en 1.425 objetos de Git y 996 versiones de archivos. Se buscaron también representaciones codificadas. No hubo coincidencias ni archivos de bases, adjuntos o configuración privada versionados. Se revisaron las capturas de documentación; las de conversaciones corresponden a una cuenta y textos de prueba, y la firma de Mercado Pago aparece oculta. Los informes completos de la revisión quedan en `.artifacts/security-audit`, fuera de Git.

El retiro de la calle y altura de la versión actual no las elimina de los commits anteriores. Una limpieza histórica requiere reescribir las referencias afectadas y verificar nuevamente el remoto; no debe confundirse con un commit que modifica el archivo actual. Los escáneres de credenciales tampoco certifican la ausencia de todo dato personal o de copias externas del repositorio.

Para repetir el examen de credenciales con Gitleaks instalado:

```powershell
gitleaks git --log-opts="--all --full-history" --ignore-gitleaks-allow --redact=100 --no-banner .
```

Revisar por separado capturas, datos personales y el conjunto exacto de archivos que se agregará al commit. No copiar credenciales reales a pruebas o documentación para demostrar una configuración.
