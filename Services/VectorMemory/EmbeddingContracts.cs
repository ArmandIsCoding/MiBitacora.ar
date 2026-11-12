using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MiBitacora.Services;

public sealed class EmbeddingOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "embeddinggemma:latest";
    public int Dimensions { get; set; } = 768;
    public int TimeoutSeconds { get; set; } = 90;
    public int BatchSize { get; set; } = 4;
    // Leave the GPU available for the conversational model on small local machines.
    public bool UseCpu { get; set; } = true;
    public bool IsValid() => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == "http" && uri.Host is "127.0.0.1" or "[::1]" && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && Model is "embeddinggemma" or "embeddinggemma:latest" && Dimensions is 128 or 256 or 512 or 768
        && TimeoutSeconds is >= 5 and <= 300 && BatchSize is >= 1 and <= 16;
}

public sealed record EmbeddingModel(string Key, int Dimensions);
public sealed class EmbeddingUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
public interface IJournalEmbeddingClient
{
    Task<EmbeddingModel> GetModelAsync(CancellationToken ct);
    Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingModel model, IReadOnlyList<string> inputs,
        bool interactive, CancellationToken ct);
}

public sealed class EmbeddingGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _queries;
    public async Task<IDisposable> EnterAsync(bool interactive, CancellationToken ct)
    {
        if (interactive) Interlocked.Increment(ref _queries);
        try
        {
            while (true)
            {
                await _gate.WaitAsync(ct);
                if (interactive || Volatile.Read(ref _queries) == 0) return new Lease(_gate);
                _gate.Release();
                await Task.Delay(25, ct);
            }
        }
        finally { if (interactive) Interlocked.Decrement(ref _queries); }
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _held = gate;
        public void Dispose() => Interlocked.Exchange(ref _held, null)?.Release();
    }
    public void Dispose() => _gate.Dispose();
}

public static class JournalVectors
{
    public const string Format = "embeddinggemma-text-v1";
    public const int ChunkBytes = 1200;
    public static IEnumerable<TopicSource> Split(TopicSource source)
    {
        for (var offset = 0; offset < source.Text.Length;)
        {
            var text = Utf8Prefix(source.Text[offset..], ChunkBytes);
            yield return source with { Id = $"{source.Id}/v1:{offset}", Text = text };
            offset += text.Length;
        }
    }
    public static string Utf8Prefix(string text, int bytes)
    {
        var length = 0;
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > bytes) break;
            used += rune.Utf8SequenceLength;
            length += rune.Utf16SequenceLength;
        }
        return text[..length];
    }
    public static string Document(TopicSource source) => $"title: {Utf8Prefix(source.Label, 180)} | text: {source.Text}";
    public static string Query(string text) => "task: search result | query: " + Utf8Prefix(text, 1500);
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static float[] Normalize(float[] vector, int dimensions)
    {
        if (vector.Length != dimensions || vector.Any(v => !float.IsFinite(v)))
            throw new EmbeddingUnavailableException("El modelo devolvió un vector incompatible.");
        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        if (!double.IsFinite(norm) || norm < 1e-12) throw new EmbeddingUnavailableException("El modelo devolvió un vector vacío.");
        return vector.Select(v => (float)(v / norm)).ToArray();
    }
    public static byte[] Pack(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), vector[i]);
        return bytes;
    }
    public static float[] Unpack(byte[] bytes, int dimensions)
    {
        if (bytes.Length != dimensions * 4) throw new EmbeddingUnavailableException("El vector guardado es incompatible.");
        var vector = new float[dimensions];
        for (var i = 0; i < dimensions; i++) vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
        return Normalize(vector, dimensions);
    }
    public static double Similarity(byte[] bytes, float[] query)
    {
        if (bytes.Length != query.Length * 4) return -1;
        double sum = 0;
        for (var i = 0; i < query.Length; i++)
        {
            var value = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
            if (!float.IsFinite(value)) return -1;
            sum += value * (double)query[i];
        }
        return sum;
    }
}
