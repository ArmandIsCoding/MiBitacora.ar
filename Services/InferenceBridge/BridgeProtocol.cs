using System.Text.Json;

namespace MiBitacora.Services;

// Shared with the desktop agent. This protocol never accepts an arbitrary destination URL.
public static class BridgeProtocol
{
    public const string Prefix = "/api/local-inference";
    public const string AgentHeader = "X-MiBitacora-Agent";
    public const int MaxRequestBytes = 20 * 1024 * 1024;
    public const int MaxResponseBytes = 4 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static bool Allows(string method, string path) => (method, path) is
        ("GET", "/api/tags") or ("POST", "/api/chat") or
        ("POST", "/api/embed") or ("POST", "/v1/audio/transcriptions");

    public static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + read > limit) throw new InvalidDataException("Inference payload exceeds its limit.");
            buffer.Write(bytes, 0, read);
        }
        return buffer.ToArray();
    }
}

public sealed record BridgeRequest(Guid Id, string Method, string Path, string? ContentType,
    byte[] Body, DateTime ExpiresUtc);
public sealed record BridgeResponse(int Status, string ContentType, byte[] Body);
