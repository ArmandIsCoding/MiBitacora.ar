using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailKit.Security;
using MiBitacora.Services;

internal static class SmtpChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var options = Options(465);
        check(options.IsValid(false), "SMTP: admite el puerto 465 con TLS y credenciales completas");
        options.EnableSsl = false;
        check(!options.IsValid(false), "SMTP: rechaza una configuración que permita enviar sin TLS");
        foreach (var offerTls in new[] { false, true }) await VerifyRejectionAsync(offerTls, check);
    }

    private static MailOptions Options(int port) => new() { Delivery = "Smtp", Host = "127.0.0.1", Port = port,
        Username = "synthetic@example.test", Password = "synthetic-only-password", From = "sender@example.test", EnableSsl = true };

    private static async Task VerifyRejectionAsync(bool offerTls, Action<bool, string> check)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var commands = new List<string>();
        var tlsRequested = false;
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var server = Task.Run(async () => {
            try {
                using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                await using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
                await writer.WriteLineAsync("220 fake.example.test ESMTP");
                commands.Add(await reader.ReadLineAsync(deadline.Token) ?? "");
                await writer.WriteAsync(offerTls ? "250-fake.example.test\r\n250-STARTTLS\r\n250 AUTH PLAIN LOGIN\r\n" : "250-fake.example.test\r\n250 AUTH PLAIN LOGIN\r\n");
                var command = await reader.ReadLineAsync(deadline.Token);
                if (command is not null) commands.Add(command);
                if (command == "STARTTLS") {
                    tlsRequested = true;
                    await writer.WriteLineAsync("220 Ready to start TLS");
                    using var secure = new SslStream(stream, leaveInnerStreamOpen: true);
                    await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, deadline.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException) { }
        });
        try {
            var rejected = false;
            try { await SmtpDelivery.CheckConnectionAsync(Options(((IPEndPoint)listener.LocalEndpoint).Port), deadline.Token); }
            catch (NotSupportedException) when (!offerTls) { rejected = true; }
            catch (SslHandshakeException) when (offerTls) { rejected = true; }
            await server;
            check(rejected && (!offerTls || tlsRequested) && commands.All(command => !command.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase) && !command.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase)),
                offerTls ? "SMTP: un certificado no confiable se rechaza antes de enviar usuario, clave o mensajes" :
                    "SMTP: un servidor sin STARTTLS no recibe credenciales ni mensajes en texto plano");
        }
        finally { deadline.Cancel(); listener.Stop(); }
    }
}
