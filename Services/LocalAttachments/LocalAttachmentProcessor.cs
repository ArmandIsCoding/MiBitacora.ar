using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MiBitacora.Services;

public sealed class LocalAttachmentProcessor(string stagingPath, string toolsPath, IOptions<LocalAttachmentOptions> options)
{
    private static readonly SemaphoreSlim Preparation = new(1, 1);
    private const string Formats = "mov,matroska,webm,ogg,avi,asf,wav,mp3,flac,aac,aiff";
    public bool Enabled => options.Value.Enabled;

    public async Task<PreparedLocalAttachment> PrepareAsync(AttachmentContent file, bool includeAudio = true, CancellationToken ct = default)
    {
        if (!options.Value.IsValid()) throw new JournalInputException("La configuración de lectura local no es válida.");
        await Preparation.WaitAsync(ct);
        try
        {
            using var job = new PrivateMediaJob(stagingPath);
            var input = await job.CopyAsync(file.Content, ct);
            var header = new byte[(int)Math.Min(new FileInfo(input).Length, 32768)];
            await using (var stream = File.OpenRead(input)) await stream.ReadExactlyAsync(header, ct);
            var type = AttachmentExtractor.DetectMediaType(header, file.FileName);
            if (type == "application/pdf") return await PdfAsync(job, input, ct);
            if (type is not null && (AttachmentPresentation.IsInlineAudio(type) || AttachmentPresentation.IsInlineVideo(type)))
                return await MediaAsync(job, input, AttachmentPresentation.IsInlineVideo(type), includeAudio, ct);
            throw new JournalInputException("Este archivo no contiene un PDF, audio o video compatible. El original se conserva.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        { throw new JournalInputException("No pudimos preparar el archivo para su lectura local. El original se conserva."); }
        finally { Preparation.Release(); }
    }

    private async Task<PreparedLocalAttachment> PdfAsync(PrivateMediaJob job, string input, CancellationToken ct)
    {
        var assembly = typeof(LocalPdfWorker).Assembly.Location;
        var runtime = Path.ChangeExtension(assembly, ".runtimeconfig.json");
        var dependencies = Path.ChangeExtension(assembly, ".deps.json");
        if (!File.Exists(runtime))
        {
            var entry = Assembly.GetEntryAssembly()?.Location ?? throw new IOException("Falta el host de lectura.");
            runtime = Path.ChangeExtension(entry, ".runtimeconfig.json");
            dependencies = Path.ChangeExtension(entry, ".deps.json");
        }
        var host = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        await PrivateMediaJob.RunAsync(host, ["exec", "--runtimeconfig", runtime, "--depsfile", dependencies,
            assembly, "--local-pdf-worker", input, job.DirectoryPath,
            options.Value.PdfTextPages.ToString(CultureInfo.InvariantCulture), options.Value.PdfVisualPages.ToString(CultureInfo.InvariantCulture)],
            options.Value.PreparationTimeoutSeconds, ct);
        var resultPath = job.PathFor("result.json");
        if (!File.Exists(resultPath) || new FileInfo(resultPath).Length > 128 * 1024) throw new IOException("Lectura incompleta.");
        var result = JsonSerializer.Deserialize<PdfWorkerResult>(await File.ReadAllTextAsync(resultPath, ct)) ?? throw new IOException("Lectura incompleta.");
        if (result.Images.Count > options.Value.PdfVisualPages || result.Text.Length > 16010) throw new IOException("Lectura demasiado extensa.");
        var images = new List<PreparedLocalVisual>();
        foreach (var image in result.Images)
        {
            if (Path.GetFileName(image.File) != image.File || !image.File.StartsWith("page-", StringComparison.Ordinal)
                || !image.File.EndsWith(".jpg", StringComparison.Ordinal)) throw new IOException("Imagen inválida.");
            images.Add(new(await ReadImageAsync(job.PathFor(image.File), ct), image.SourcePart));
        }
        return new(result.Text, result.Coverage, images);
    }

    private async Task<PreparedLocalAttachment> MediaAsync(PrivateMediaJob job, string input, bool video, bool includeAudio, CancellationToken ct)
    {
        var ffmpeg = Tool("ffmpeg"); var ffprobe = Tool("ffprobe");
        var json = await PrivateMediaJob.RunAsync(ffprobe, ["-v", "error", "-protocol_whitelist", "file,pipe",
            "-format_whitelist", Formats, "-show_entries", "format=duration:stream=codec_type,width,height:stream_disposition=attached_pic", "-of", "json", input], 20, ct);
        using var info = JsonDocument.Parse(json);
        if (!info.RootElement.TryGetProperty("format", out var format) || !format.TryGetProperty("duration", out var value)
            || !double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds) || seconds <= 0 || seconds > AttachmentPreparationService.MaxDurationSeconds)
            throw new JournalInputException("La lectura local admite audios y videos de hasta 10 minutos. El original se conserva.");
        var streams = info.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var hasAudio = streams.Any(s => s.GetProperty("codec_type").GetString() == "audio");
        var visual = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video"
            && (!s.TryGetProperty("disposition", out var d) || !d.TryGetProperty("attached_pic", out var a) || a.GetInt32() == 0));
        if (video && (visual.ValueKind == JsonValueKind.Undefined || !visual.TryGetProperty("width", out var width)
            || !visual.TryGetProperty("height", out var height) || width.GetInt64() <= 0 || height.GetInt64() <= 0
            || width.GetInt64() * height.GetInt64() > 64_000_000))
            throw new JournalInputException("El video no tiene una imagen compatible o supera los 64 megapíxeles por cuadro.");
        var images = new List<PreparedLocalVisual>();
        for (var i = 0; video && i < options.Value.VideoFrames; i++)
        {
            var timestamp = seconds * (i + .5) / options.Value.VideoFrames;
            var target = job.PathFor($"frame-{i}.jpg");
            await PrivateMediaJob.RunAsync(ffmpeg, ["-nostdin", "-hide_banner", "-v", "error", "-y", "-threads", "1",
                "-protocol_whitelist", "file,pipe", "-format_whitelist", Formats, "-ss", timestamp.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", input, "-map", "0:V:0", "-an", "-map_metadata", "-1", "-map_chapters", "-1", "-frames:v", "1", "-vf", "scale=w=1280:h=1280:force_original_aspect_ratio=decrease:out_range=full",
                "-threads", "1", "-pix_fmt", "yuvj420p", "-color_range", "pc", "-q:v", "3", "-update", "1", target], options.Value.PreparationTimeoutSeconds, ct);
            // VFR containers can store one frame and hold it for several seconds.
            // Decode from the beginning and retain that last frame if seeking yielded none.
            if (!File.Exists(target) || new FileInfo(target).Length == 0)
                await PrivateMediaJob.RunAsync(ffmpeg, ["-nostdin", "-hide_banner", "-v", "error", "-y", "-threads", "1",
                    "-protocol_whitelist", "file,pipe", "-format_whitelist", Formats, "-i", input,
                    "-ss", timestamp.ToString("0.###", CultureInfo.InvariantCulture), "-map", "0:V:0", "-an", "-map_metadata", "-1", "-map_chapters", "-1",
                    "-frames:v", "1", "-vf", $"tpad=stop_mode=clone:stop_duration={seconds.ToString("0.###", CultureInfo.InvariantCulture)},scale=w=1280:h=1280:force_original_aspect_ratio=decrease:out_range=full",
                    "-threads", "1", "-pix_fmt", "yuvj420p", "-color_range", "pc", "-q:v", "3", "-update", "1", target], options.Value.PreparationTimeoutSeconds, ct);
            images.Add(new(await ReadImageAsync(target, ct), $"Muestra visual cercana a los {timestamp:0.##} s de un video de {seconds:0.##} s; no representa todos los cuadros"));
        }
        byte[]? audio = null;
        if (hasAudio && includeAudio)
        {
            var target = job.PathFor("audio.wav");
            await PrivateMediaJob.RunAsync(ffmpeg, ["-nostdin", "-hide_banner", "-v", "error", "-y", "-threads", "1",
                "-protocol_whitelist", "file,pipe", "-format_whitelist", Formats, "-i", input, "-map", "0:a:0", "-vn",
                "-map_metadata", "-1", "-map_chapters", "-1", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "wav", target],
                options.Value.PreparationTimeoutSeconds, ct);
            var length = new FileInfo(target).Length;
            if (length is <= 0 or > AttachmentPreparationService.MaxAudioBytes) throw new IOException("Audio demasiado extenso.");
            audio = await File.ReadAllBytesAsync(target, ct);
        }
        if (!video && !hasAudio) throw new JournalInputException("No encontramos una pista de audio para transcribir.");
        return new(null, video
            ? $"Video de {seconds:0.##} s · {images.Count} fotogramas de muestra, no todos los cuadros. {(hasAudio ? "Incluye pista de audio." : "Sin pista de audio.")}"
            : $"Audio de {seconds:0.##} s preparado como WAV mono de 16 kHz.", images, audio);
    }
    private string Tool(string name)
    {
        var path = Path.Combine(toolsPath, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (!File.Exists(path)) throw new JournalInputException("Falta preparar las herramientas de audio y video en este servidor. El original se conserva.");
        return path;
    }
    private static async Task<byte[]> ReadImageAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 3 * 1024 * 1024) throw new IOException("Imagen incompleta o demasiado grande.");
        var image = await File.ReadAllBytesAsync(path, ct);
        if (image.Length < 3 || image[0] != 0xff || image[1] != 0xd8 || image[2] != 0xff) throw new IOException("La imagen no es un JPEG.");
        return image;
    }
}
