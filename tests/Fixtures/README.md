# Archivos de prueba

`red-square.png` es una figura roja para probar la inferencia local.

`oriented-colors.heic` es una imagen de 320 × 240 creada para las pruebas: rojo con el cuarto izquierdo verde. Contiene orientación EXIF 6 (90 grados en sentido horario) y un autor ficticio. Al decodificarla debe quedar en 240 × 320, con verde arriba y rojo debajo. Fue codificada con Pillow/pillow-heif como herramienta de desarrollo; esas bibliotecas no son dependencias de la aplicación.

Estas imágenes no provienen del diario del usuario ni contienen fotografías personales.

`10x10.xls` y `10x10.xlsb` son matrices de prueba de ExcelDataReader, tomadas del [commit c025e3d783dbbe059fa15388f422a7a3dd265a85](https://github.com/ExcelDataReader/ExcelDataReader/tree/c025e3d783dbbe059fa15388f422a7a3dd265a85/src/TestData). Se conserva su licencia MIT en `ExcelDataReader-LICENSE.txt`.

- XLS SHA-256: `4F6504DE3F8D5A11577B9C1B634F49A01FEC261B63EAE9FA0897096C851AD9B9`.
- XLSB SHA-256: `8990F6B16FD3023DDEFA385039F5B036282DCFA1491A2A6AAA4154DB5B755996`.

Los PDF, contenedores Office, audios y videos adicionales se generan durante las pruebas con texto, figuras y tonos sintéticos. Para la prueba real de habla se usa una voz del sistema que lee una frase inventada mediante `scripts/New-LocalAttachmentTestAudio.ps1`; no se graba el micrófono ni se usa contenido de una cuenta real.
