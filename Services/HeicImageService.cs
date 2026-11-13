using ImageMagick;

namespace MiBitacora.Services;

/// <summary>Reproducible JPEG copies. The HEIC original is always left untouched.</summary>
public sealed class HeicImageService(UploadFileStore uploads, ILogger<HeicImageService>? logger = null)
{
    public const uint MaxDimension = 1920;
    private const ulong MaxPixels = 64_000_000;
    private static readonly SemaphoreSlim PhotoCodec = new(1, 1);
    private static readonly Lazy<bool> ConfigureCodec = new(() =>
    {
        ResourceLimits.Memory = 512 * 1024 * 1024;
        ResourceLimits.Disk = 0;
        ResourceLimits.Thread = 1;
        ResourceLimits.Width = 16_384;
        ResourceLimits.Height = 16_384;
        return true;
    });

    public static bool IsHeic(string contentType) => contentType is "image/heic" or "image/heif";
    public static string AnalysisContentType(string contentType) => IsHeic(contentType) ? "image/jpeg" : contentType;
    public static bool HasPreview(string contentType) => IsHeic(contentType)
        || contentType is "image/jpeg" or "image/png" or "image/webp" or "image/gif" or "image/avif";

    public async Task<Stream?> OpenJpegAsync(string userId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        // Share the mutation lock with entry/account deletion: a conversion cannot
        // recreate files after deletion, and concurrent requests cannot write twice.
        using var lease = await uploads.LockAsync(cancellationToken);
        var cached = uploads.OpenJpeg(userId, attachmentId);
        if (cached is not null) return cached;
        await using var original = uploads.OpenRead(userId, attachmentId);
        if (original is null) return null;
        try
        {
            var jpeg = await NormalizePhotoAsync(original, "image/heic", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await uploads.WriteJpegAsync(userId, attachmentId, jpeg, cancellationToken);
            return uploads.OpenJpeg(userId, attachmentId);
        }
        catch (Exception ex) when (ex is MagickException or IOException)
        {
            logger?.LogWarning(ex, "No se pudo preparar la vista JPG del adjunto {AttachmentId}. El original se conserva.", attachmentId);
            throw new JournalInputException("No pudimos preparar la imagen JPG. El archivo original sigue disponible para descargar.");
        }
    }

    public static async Task<byte[]> NormalizePhotoAsync(Stream original, string contentType, CancellationToken ct = default)
    {
        await PhotoCodec.WaitAsync(ct);
        try
        {
            var result = await Task.Run(() => NormalizePhoto(original, contentType), ct);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        finally { PhotoCodec.Release(); }
    }

    // Used at upload time and for the bounded AI copy of historical photos.
    public static byte[] NormalizePhoto(Stream original, string contentType)
    {
        _ = ConfigureCodec.Value;
        var settings = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };
        if (IsHeic(contentType))
        {
            settings.Format = MagickFormat.Heic;
            settings.SetDefine(MagickFormat.Heic, "primary-image", true);
        }
        using (var information = new MagickImage())
        {
            information.Ping(original, settings);
            if ((ulong)information.Width * information.Height > MaxPixels)
                throw new JournalInputException("La foto supera los 64 megapíxeles. Exportala con una resolución menor e intentá de nuevo.");
        }
        original.Position = 0;
        using var image = new MagickImage(original, settings);
        image.AutoOrient();
        if (image.GetColorProfile() is not null) image.TransformColorSpace(ColorProfiles.SRGB);
        else image.ColorSpace = ColorSpace.sRGB;
        var width = image.Width >= image.Height ? 1920u : 1080u;
        var height = image.Width >= image.Height ? 1080u : 1920u;
        if (image.Width > width || image.Height > height)
            image.Resize(new MagickGeometry(width, height) { Greater = true });
        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaAction.Remove);
        image.Strip(); // No EXIF/GPS/XMP in the web/AI copy; originals retain all metadata.
        image.Quality = 82;
        var jpeg = image.ToByteArray(MagickFormat.Jpeg);
        if (jpeg.Length > JournalStore.MaxFileBytes)
            throw new JournalInputException("La copia JPG supera el tamaño admitido. El original se conserva.");
        return jpeg;
    }
}
