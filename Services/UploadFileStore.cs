using Microsoft.EntityFrameworkCore;
using MiBitacora.Data;

namespace MiBitacora.Services;

/// <summary>Private originals, outside wwwroot. Only server-generated IDs form paths.</summary>
public sealed class UploadFileStore(string rootPath, ILogger<UploadFileStore> logger) : IDisposable
{
    private readonly string root = Path.GetFullPath(rootPath);
    private readonly SemaphoreSlim mutations = new(1, 1);

    // Serialize database/file mutations, including account deletion, within this server.
    public async Task<IDisposable> LockAsync(CancellationToken cancellationToken = default)
    {
        await mutations.WaitAsync(cancellationToken);
        return new Lease(mutations);
    }

    private string AccountPath(string userId)
    {
        if (!Guid.TryParseExact(userId, "D", out var id))
            throw new ArgumentException("El código de cuenta debe ser un UUID.", nameof(userId));
        var path = Path.Combine(root, id.ToString("D"));
        RejectLink(root);
        RejectLink(path);
        return path;
    }

    private string FilePath(string userId, Guid attachmentId, string extension = ".bin")
    {
        var path = Path.Combine(AccountPath(userId), $"{attachmentId:D}{extension}");
        RejectLink(path);
        return path;
    }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path))
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("La carpeta de uploads no admite enlaces simbólicos.");
    }

    public Task WriteAsync(string userId, Guid attachmentId, byte[] content,
        CancellationToken cancellationToken = default) =>
        WriteFileAsync(FilePath(userId, attachmentId), content, cancellationToken);

    public Task WriteJpegAsync(string userId, Guid attachmentId, byte[] content,
        CancellationToken cancellationToken = default) =>
        WriteFileAsync(FilePath(userId, attachmentId, ".jpg"), content, cancellationToken);

    private async Task WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        TryDelete(temporary); // Retry a write interrupted before the atomic rename.
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path); // Never replace an existing original.
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public FileStream? OpenRead(string userId, Guid attachmentId) => OpenFile(FilePath(userId, attachmentId));

    public FileStream? OpenJpeg(string userId, Guid attachmentId) => OpenFile(FilePath(userId, attachmentId, ".jpg"));

    private static FileStream? OpenFile(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public void Delete(string userId, IEnumerable<Guid> attachmentIds)
    {
        foreach (var id in attachmentIds)
        {
            TryDelete(FilePath(userId, id));
            TryDelete(FilePath(userId, id, ".jpg"));
        }
        DeleteEmptyDirectory(AccountPath(userId));
    }

    public void DeleteAccount(string userId)
    {
        var directory = AccountPath(userId);
        if (!Directory.Exists(directory)) return;
        // No recursive deletion: uploads never contain user-controlled directories.
        foreach (var path in Directory.EnumerateFiles(directory)) TryDelete(path);
        DeleteEmptyDirectory(directory);
    }

    // Recover uncommitted writes and retry deletions interrupted by a crash or file lock.
    // Called at startup, after migration and before serving requests.
    public async Task CleanupOrphansAsync(ApplicationDbContext db, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(root);
        RejectLink(root);
        var originals = await db.JournalAttachments
            .Select(row => new { row.Id, row.Entry.UserId }).ToListAsync(cancellationToken);
        var keep = originals.SelectMany(row => new[] { FilePath(row.UserId, row.Id), FilePath(row.UserId, row.Id, ".jpg") })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "D", out _)) continue;
            RejectLink(directory);
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                // Only remove names created by this store.
                var name = Path.GetFileName(path);
                if ((name.EndsWith(".bin.tmp", StringComparison.Ordinal) || name.EndsWith(".jpg.tmp", StringComparison.Ordinal))
                    && Guid.TryParseExact(name[..^8], "D", out _)) TryDelete(path);
                else if ((name.EndsWith(".bin", StringComparison.Ordinal) || name.EndsWith(".jpg", StringComparison.Ordinal))
                    && Guid.TryParseExact(name[..^4], "D", out _) && !keep.Contains(path)) TryDelete(path);
            }
            DeleteEmptyDirectory(directory);
        }
    }

    private void TryDelete(string path)
    {
        try { RejectLink(path); File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "No se pudo borrar un upload. Se reintentará al reiniciar el servidor.");
        }
    }

    private void DeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "No se pudo quitar una carpeta de uploads vacía.");
        }
    }

    public void Dispose() => mutations.Dispose();

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? current = semaphore;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Release();
    }
}
