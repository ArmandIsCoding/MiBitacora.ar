using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ImageMagick;

namespace MiBitacora.Services;

/// <summary>Bounded, private staging; only the optimized result enters the diary.</summary>
public sealed class AttachmentPreparationService(string stagingPath, string toolsPath)
{
    public const long MaxIncomingBytes = 256L * 1024 * 1024;
    public const int MaxAudioBytes = 20 * 1024 * 1024;
    public const int MaxDurationSeconds = 600;
    private static readonly SemaphoreSlim Codec = new(1, 1);

    // Run before accepting requests: recover only this service's interrupted writes.
    public void CleanupInterrupted()
    {
        if (!Directory.Exists(stagingPath)) return;
        if ((File.GetAttributes(stagingPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("La carpeta de preparación multimedia no admite enlaces simbólicos.");
        foreach (var path in Directory.EnumerateFiles(stagingPath))
            if (Path.GetExtension(path) is ".input" or ".output" &&
                Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path);
    }

    public async Task<PreparedAttachment> PrepareAsync(string fileName, long size, Stream source,
        bool localAudio, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (size is <= 0 or > MaxIncomingBytes)
            throw new JournalInputException("Elegí un archivo de hasta 256 MB. Las fotos grandes se comprimen automáticamente.");
        Directory.CreateDirectory(stagingPath);
        var id = Guid.NewGuid().ToString("N");
        var input = Path.Combine(stagingPath, id + ".input");
        var output = Path.Combine(stagingPath, id + ".output");
        try
        {
            await using (var destination = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long received = 0;
                var lastUpdate = Stopwatch.StartNew();
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += read;
                    if (received > size || received > MaxIncomingBytes)
                        throw new JournalInputException("El archivo recibido supera el tamaño anunciado. Volvé a seleccionarlo.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (lastUpdate.ElapsedMilliseconds >= 500)
                    {
                        progress?.Report($"Recibiendo {fileName} · {received * 100 / size}%");
                        lastUpdate.Restart();
                    }
                }
                if (received != size) throw new JournalInputException("El archivo llegó incompleto. Volvé a seleccionarlo.");
            }
            var header = new byte[(int)Math.Min(size, 32 * 1024)];
            await using (var stream = File.OpenRead(input)) await stream.ReadExactlyAsync(header, cancellationToken);
            var type = AttachmentExtractor.DetectMediaType(header, fileName);
            var name = AttachmentExtractor.CleanFileName(fileName);
            byte[] content;
            var optimized = false;
            await Codec.WaitAsync(cancellationToken);
            try
            {
                if (type?.StartsWith("image/", StringComparison.Ordinal) == true)
                {
                    progress?.Report($"Optimizando {name} · preparando una foto Full HD…");
                    await using var stream = File.OpenRead(input);
                    content = await HeicImageService.NormalizePhotoAsync(stream, type, cancellationToken);
                    name = Path.ChangeExtension(name, ".jpg");
                    optimized = true;
                }
                else if (type?.StartsWith("video/", StringComparison.Ordinal) == true ||
                         type?.StartsWith("audio/", StringComparison.Ordinal) == true)
                {
                    progress?.Report($"Optimizando {name} · esto puede llevar un momento…");
                    var audio = await TranscodeAsync(input, output, localAudio, cancellationToken);
                    content = await File.ReadAllBytesAsync(output, cancellationToken);
                    name = Path.ChangeExtension(name, audio ? localAudio ? ".wav" : ".mp3" : ".mp4");
                    optimized = true;
                }
                else
                {
                    if (size > JournalStore.MaxFileBytes)
                        throw new JournalInputException("Los documentos pueden pesar hasta 64 MB. Las fotos, videos y audios admiten hasta 256 MB antes de optimizarlos.");
                    content = await File.ReadAllBytesAsync(input, cancellationToken);
                }
            }
            finally { Codec.Release(); }
            cancellationToken.ThrowIfCancellationRequested();
            if (content.Length is <= 0 or > JournalStore.MaxFileBytes)
                throw new JournalInputException("No pudimos reducir el archivo a 64 MB. Probá recortar el video o exportarlo con menor calidad.");
            return new(new PendingAttachment(name, content), size, optimized);
        }
        catch (MagickException)
        {
            throw new JournalInputException("No pudimos abrir esa foto. Probá exportarla como JPG o HEIC y adjuntarla otra vez.");
        }
        finally
        {
            DeleteTemporary(input);
            DeleteTemporary(output);
        }
    }

    private async Task<bool> TranscodeAsync(string input, string output, bool localAudio, CancellationToken ct)
    {
        var ffmpeg = Path.Combine(toolsPath, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        var probe = Path.Combine(toolsPath, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (!File.Exists(ffmpeg) || !File.Exists(probe))
            throw new JournalInputException("El procesador de audio y video todavía no está disponible en este servidor. Podés adjuntar fotos o documentos mientras lo configuramos.");
        var metadata = await RunAsync(probe, ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries",
            "format=duration:stream=codec_type,width,height", "-of", "json", input], TimeSpan.FromSeconds(30), ct);
        using var document = JsonDocument.Parse(metadata);
        if (!document.RootElement.TryGetProperty("format", out var format) ||
            !format.TryGetProperty("duration", out var durationValue) ||
            !double.TryParse(durationValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ||
            !double.IsFinite(duration) || duration <= 0 || duration > MaxDurationSeconds)
            throw new JournalInputException("Elegí un audio o video de hasta 10 minutos. Podés dividir uno más largo en varias entradas.");
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video" &&
            s.TryGetProperty("width", out var w) && w.GetInt32() > 0);
        var audioOnly = video.ValueKind == JsonValueKind.Undefined;
        if (audioOnly && !streams.Any(s => s.GetProperty("codec_type").GetString() == "audio"))
            throw new JournalInputException("No encontramos audio ni video que se pueda reproducir en este archivo.");
        var args = new List<string> { "-nostdin", "-hide_banner", "-v", "error", "-y", "-threads", "2",
            "-protocol_whitelist", "file,pipe", "-i", input, "-t", "600", "-map_metadata", "-1", "-map_chapters", "-1" };
        if (audioOnly)
        {
            args.AddRange(["-map", "0:a:0", "-vn", "-ac", "1"]);
            args.AddRange(localAudio
                ? ["-ar", "16000", "-c:a", "pcm_s16le", "-f", "wav"]
                : ["-ar", "44100", "-c:a", "libmp3lame", "-b:a", "96k", "-f", "mp3"]);
        }
        else
        {
            var width = video.GetProperty("width").GetInt32();
            var height = video.GetProperty("height").GetInt32();
            if ((long)width * height > 64_000_000)
                throw new JournalInputException("El video supera los 64 megapíxeles por cuadro. Exportalo con una resolución menor.");
            var bitrate = Math.Clamp((int)((JournalStore.MaxFileBytes - 1024 * 1024) * 8 / duration / 1000) - 160, 256, 4000);
            args.AddRange(["-map", "0:v:0", "-map", "0:a:0?", "-vf",
                "scale=w='if(gte(iw,ih),min(1920,iw),min(1080,iw))':h='if(gte(iw,ih),min(1080,ih),min(1920,ih))':force_original_aspect_ratio=decrease:force_divisible_by=2,setsar=1,fps=30",
                "-c:v", "libx264", "-threads", "2", "-preset", "veryfast", "-b:v", $"{bitrate}k", "-maxrate", $"{bitrate}k", "-bufsize", $"{bitrate * 2}k",
                "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", "-f", "mp4"]);
        }
        args.Add(output);
        await RunAsync(ffmpeg, args, TimeSpan.FromMinutes(8), ct);
        var resultSize = new FileInfo(output).Length;
        if (resultSize > (audioOnly ? MaxAudioBytes : JournalStore.MaxFileBytes))
            throw new JournalInputException("El archivo optimizado todavía es demasiado grande. Probá un fragmento más corto.");
        return audioOnly;
    }

    private static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("No se pudo iniciar el procesador multimedia.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            await stderr;
            if (process.ExitCode != 0)
                throw new JournalInputException("No pudimos procesar ese audio o video. Probá exportarlo como MP4 o MP3 y adjuntarlo otra vez.");
            return await stdout;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new JournalInputException("La optimización demoró demasiado. Probá con un fragmento más corto."); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    private static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed record PreparedAttachment(PendingAttachment Attachment, long OriginalBytes, bool Optimized);
