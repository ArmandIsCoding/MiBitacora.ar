using System.Diagnostics;
using System.Text.Json;
using ImageMagick;
using MiBitacora.Services;

internal static class AttachmentPreparationChecks
{
    public static async Task RunAsync(Action<bool, string> check, bool codecs)
    {
        var root = Path.Combine(Path.GetTempPath(), "mibitacora-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tools = Path.GetFullPath("media-tools");
            var staging = Path.Combine(root, "staging");
            var prepare = new AttachmentPreparationService(staging, tools);
            byte[] large;
            using (var photo = new MagickImage(MagickColors.LightSalmon, 4032, 3024))
            {
                photo.AddNoise(NoiseType.Random);
                photo.Quality = 100;
                var exif = new ExifProfile();
                exif.SetValue(ExifTag.Orientation, (ushort)6);
                exif.SetValue(ExifTag.GPSLatitudeRef, "S");
                photo.SetProfile(exif);
                photo.Orientation = Orientation.RightTop;
                large = photo.ToByteArray(MagickFormat.Jpeg);
            }
            check(large.Length > 8 * 1024 * 1024, "Fotos grandes: la prueba supera el límite anterior de 8 MB");
            var progress = new Messages();
            var optimized = await prepare.PrepareAsync("iPhone-prueba.JPG", large.Length, new MemoryStream(large), false, progress);
            using (var photo = new MagickImage(optimized.Attachment.Content))
                check(photo.Format == MagickFormat.Jpeg && photo.Width <= 1080 && photo.Height <= 1920 && photo.Height > photo.Width
                    && photo.GetExifProfile() is null && optimized.Attachment.Content.Length < large.Length / 4,
                    $"Fotos grandes: JPG orientado Full HD sin GPS/EXIF ({photo.Width}x{photo.Height}, EXIF={photo.GetExifProfile() is not null}, {large.Length} → {optimized.Attachment.Content.Length} bytes)");
            check(optimized.Optimized && optimized.Attachment.FileName.EndsWith(".jpg") && progress.Items.Any(m => m.Contains("Optimizando"))
                && !Directory.EnumerateFiles(staging).Any(), "Adjuntos: informa la optimización y elimina el archivo temporal de entrada");
            var artifacts = Path.GetFullPath(".artifacts/media-tests"); Directory.CreateDirectory(artifacts);
            await File.WriteAllBytesAsync(Path.Combine(artifacts, "iphone-grande.jpg"), large);
            using (var photo = new MagickImage(MagickColors.SeaGreen, 8064, 6048))
            {
                var raw = photo.ToByteArray(MagickFormat.Jpeg);
                var result = await prepare.PrepareAsync("48-megapixeles.jpg", raw.Length, new MemoryStream(raw), false);
                using var normalized = new MagickImage(result.Attachment.Content);
                check(normalized.Width == 1440 && normalized.Height == 1080, "Fotos grandes: admite las fotos de 48 megapíxeles y conserva su proporción");
            }
            var heic = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "oriented-colors.heic"));
            var heicResult = await prepare.PrepareAsync("iPhone.HEIC", heic.Length, new MemoryStream(heic), false);
            using (var photo = new MagickImage(heicResult.Attachment.Content))
                check(photo.Width == 240 && photo.Height == 320 && photo.GetExifProfile() is null,
                    "Fotos grandes: un HEIC nuevo se guarda como JPG orientado y liviano");
            await Reject(() => prepare.PrepareAsync("enorme.jpg", AttachmentPreparationService.MaxIncomingBytes + 1, Stream.Null, false), "256 MB");
            await Reject(() => prepare.PrepareAsync("incompleto.jpg", large.Length + 1, new MemoryStream(large), false), "incompleto");
            await Reject(() => prepare.PrepareAsync("roto.heic", 32, new MemoryStream(heic[..32]), false), "foto");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try { await prepare.PrepareAsync("cancelado.jpg", large.Length, new MemoryStream(large), false, cancellationToken: canceled.Token); throw new Exception("No canceló"); }
                catch (OperationCanceledException) { check(!Directory.EnumerateFiles(staging).Any(), "Adjuntos: cancelar la carga no deja archivos temporales privados"); }
            }
            var interrupted = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".input");
            await File.WriteAllTextAsync(interrupted, "interrumpido");
            var unrelated = Path.Combine(staging, "keep.txt"); await File.WriteAllTextAsync(unrelated, "conservar");
            prepare.CleanupInterrupted();
            check(!File.Exists(interrupted) && File.Exists(unrelated), "Adjuntos: el arranque recupera una carga interrumpida y conserva nombres ajenos");
            File.Delete(unrelated);
            if (!codecs) return;
            var ffmpeg = Path.Combine(tools, "ffmpeg.exe"); var ffprobe = Path.Combine(tools, "ffprobe.exe");
            var video = Path.Combine(root, "video.mov");
            await Run(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "color=c=green:s=3840x2160:d=1:r=5", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:v", "libx264", "-threads", "2", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", video]);
            await using (var stream = File.OpenRead(video))
            {
                var normalized = await prepare.PrepareAsync("iPhone-video.mov", stream.Length, stream, false);
                var target = Path.Combine(root, "normalized.mp4"); await File.WriteAllBytesAsync(target, normalized.Attachment.Content);
                using var info = JsonDocument.Parse(await Run(ffprobe, ["-v", "error", "-show_entries", "stream=width,height,codec_name", "-of", "json", target]));
                var v = info.RootElement.GetProperty("streams")[0];
                check(normalized.Attachment.FileName.EndsWith(".mp4") && v.GetProperty("width").GetInt32() == 1920 && v.GetProperty("height").GetInt32() == 1080
                    && v.GetProperty("codec_name").GetString() == "h264", "Videos: convierte un MOV 4K a MP4 H.264 Full HD reproducible");
            }
            var sound = Path.Combine(root, "audio.wav");
            await Run(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=330:duration=1", "-ac", "2", "-ar", "48000", sound]);
            var audio = await File.ReadAllBytesAsync(sound);
            var local = await prepare.PrepareAsync("grabacion.wav", audio.Length, new MemoryStream(audio), true);
            var cloud = await prepare.PrepareAsync("grabacion.wav", audio.Length, new MemoryStream(audio), false);
            check(local.Attachment.FileName.EndsWith(".wav") && local.Attachment.Content.Length < audio.Length / 2
                && cloud.Attachment.FileName.EndsWith(".mp3") && cloud.Attachment.Content.Length < audio.Length / 2,
                "Audios: prepara WAV mono de 16 kHz para Ollama y MP3 comprimido para OpenAI");
            var longAudio = Path.Combine(root, "largo.wav");
            await Run(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono", "-t", "601", longAudio]);
            await using (var stream = File.OpenRead(longAudio))
                await Reject(() => prepare.PrepareAsync("largo.wav", stream.Length, stream, false), "10 minutos");
            check(!Directory.EnumerateFiles(staging).Any(), "Multimedia: incluso un rechazo del procesador limpia los archivos temporales");

            async Task Reject(Func<Task<PreparedAttachment>> action, string reason)
            {
                try { await action(); throw new Exception("Se aceptó un adjunto inválido"); }
                catch (JournalInputException ex) { check(ex.Message.Contains(reason), $"Adjuntos: rechazo visible y específico ({reason})"); }
            }
        }
        finally
        {
            // Exactly the unique test directory, never the production staging path.
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }
    private sealed class Messages : IProgress<string>
    {
        public List<string> Items { get; } = [];
        public void Report(string value) => Items.Add(value);
    }
    private static async Task<string> Run(string executable, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception(await error);
        return await output;
    }
}
