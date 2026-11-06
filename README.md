# Mi Bitácora

Piloto local de un cuaderno conversacional para escribir, ordenar ideas y preparar una hoja para retomar temas. Usa Blazor Web App con interactividad en el servidor, ASP.NET Core Identity, .NET 10 y SQLite.

![Inicio de Mi Bitácora, con un cuaderno ilustrado y una vista de cómo funciona el piloto](docs/inicio-mi-bitacora.png)

## Abrir el piloto

Con el SDK de .NET 10 instalado, abrí PowerShell y ejecutá:

```powershell
Set-Location 'C:\Dev\MiBitacora'
dotnet run --launch-profile http
```

Abrí [localhost:5234](http://localhost:5234), elegí **Crear mi bitácora** y registrá una cuenta. La base se crea en `App_Data/mi-bitacora.db` en el primer arranque. Podés abrir [la solución](MiBitacora.sln) en Visual Studio y ejecutar el perfil **http**.

Para una primera vuelta, en **Conversación** tocá **Probar con un ejemplo** y después **Ordenar mis ideas**. El modo de muestra arma una devolución local identificada, basada en fragmentos de tu texto. También podés guardar entradas sin pedir una devolución.

## Activar Gemini gratuito

1. Creá una clave para un proyecto de **nivel gratuito**, sin facturación vinculada, en [Google AI Studio](https://aistudio.google.com/apikey).
2. Guardala como secreto local del proyecto desde PowerShell:

   ```powershell
   Set-Location 'C:\Dev\MiBitacora'
   dotnet user-secrets set 'Gemini:ApiKey' '<tu clave de Google AI Studio>'
   ```

3. Reiniciá la web. En **Mi cuenta → Tu perfil**, activá **Devoluciones con Gemini** y guardá. La bitácora va a pedir confirmación cada vez que pueda enviar contenido.

La clave vive en el servidor. También se puede configurar con la variable de entorno `GEMINI_API_KEY`. El modelo predeterminado es [Gemini 3.5 Flash-Lite](https://aistudio.google.com/docs/models/gemini-3.5-flash-lite) y se puede cambiar con `Gemini__Model`. Si falla la red, se alcanza la cuota o Gemini devuelve una respuesta incompleta, la entrada queda guardada y la devolución se puede reintentar.

El piloto con Gemini está pensado para mayores de 18 años y material ficticio, libre de datos personales, sensibles o confidenciales. El envío puede incluir fragmentos del contexto y del cuaderno, texto extraído de adjuntos e imágenes o PDF completos. [Claves de Gemini](https://aistudio.google.com/docs/api-key) · [Términos](https://aistudio.google.com/docs/terms) · [Precios y nivel gratuito](https://aistudio.google.com/docs/pricing#gemini-3.5-flash-lite)

## Qué podés probar

- **Conversación.** Un hilo por cuenta con tus originales y las devoluciones por separado. Para cada devolución se recuperan antecedentes recientes o relacionados, notitas y el contexto personal o cultural que hayas escrito en tu perfil.
- **Adjuntos.** El selector nativo abre archivos y fotos. En el teléfono, **Foto** solicita la cámara cuando el navegador admite la captura nativa. La extracción local cubre documentos de Word modernos, Excel, CSV, texto y otros formatos de oficina. Gemini puede analizar imágenes y PDF compatibles. Los demás archivos quedan disponibles para descargar. Cada entrada admite 4 archivos, con un máximo de 8 MiB por archivo y 16 MiB en conjunto. La cuota por cuenta es de 80 MiB o 250 archivos.
- **Notitas.** Creá, editá o borrá papelitos. Podés guardar una idea a partir de una devolución y marcarla para retomar.
- **Para retomar.** Marcá entradas y notitas para armar una hoja. El texto de la hoja se puede editar para la copia o la impresión. La selección queda guardada en tu cuenta.
- **Mi cuenta.** Perfil y contexto, contraseña, consentimiento revocable, exportación en JSON y borrado de la cuenta y su cuaderno. Descargá los archivos originales desde las entradas. Las cuentas del piloto son locales y el alta se completa en el momento.

Para probar desde un teléfono, el dispositivo necesita poder acceder a la instancia. El arranque anterior escucha en el equipo local. Para abrir el piloto a amigos, servilo por HTTPS y mantené `App_Data` en almacenamiento persistente del servidor. La app aplica las migraciones de SQLite al arrancar.
