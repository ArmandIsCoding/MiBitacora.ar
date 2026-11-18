using System.Diagnostics;
using System.Text;

namespace MiBitacora.Services;

// One private directory per operation; no user-controlled names or shell commands.
internal sealed class PrivateMediaJob : IDisposable
{
    private readonly string root;
    public string DirectoryPath { get; }
    public PrivateMediaJob(string stagingPath)
    {
        root = Path.GetFullPath(stagingPath).TrimEnd(Path.DirectorySeparatorChar);
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("La preparación local no admite enlaces simbólicos.");
        DirectoryPath = Path.Combine(root, "local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }
    public string PathFor(string name) => Path.Combine(DirectoryPath, name);
    public async Task<string> CopyAsync(Stream source, CancellationToken ct)
    {
        var path = PathFor("source.bin");
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920]; long total = 0; int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += count;
            if (total > JournalStore.MaxFileBytes) throw new JournalInputException("El adjunto supera los 64 MB de lectura local.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (total == 0) throw new JournalInputException("El adjunto está vacío. El original se conserva.");
        return path;
    }
    public void Dispose()
    {
        var resolved = Path.GetFullPath(DirectoryPath);
        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("local-", StringComparison.Ordinal))
            throw new IOException("Carpeta temporal inválida.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, int timeoutSeconds, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("No se pudo iniciar la lectura local.");
        try
        {
            var output = ReadAsync(process.StandardOutput.BaseStream);
            var error = ReadAsync(process.StandardError.BaseStream);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error);
            if (process.ExitCode != 0) throw new JournalInputException("No pudimos leer este archivo. Puede estar dañado, protegido o tener un formato incompatible. El original se conserva.");
            return await output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new JournalInputException("La lectura local demoró demasiado. El original se conserva; probá con un archivo más corto."); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
        async Task<string> ReadAsync(Stream stream)
        {
            try { return Encoding.UTF8.GetString(await BridgeProtocol.ReadBoundedAsync(stream, 64 * 1024, deadline.Token)); }
            catch (InvalidDataException) { deadline.Cancel(); throw; }
        }
    }
}
