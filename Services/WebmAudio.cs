namespace MiBitacora.Services;

internal static class WebmAudio
{
    // Inspect EBML metadata, never compressed media blocks. Same bounded header is used
    // when uploading and serving a preview. TrackType 2 means audio (Matroska / WebM).
    public static bool HasOnlyAudioTracks(ReadOnlySpan<byte> bytes)
    {
        bytes = bytes[..Math.Min(bytes.Length, 4096)];
        if (!Find(bytes, 0x18538067, out var segment) || !Find(segment, 0x1654AE6B, out var tracks)) return false;
        var found = false;
        while (!tracks.IsEmpty)
        {
            if (!Read(ref tracks, out var id, out var entry)) return false;
            if (id != 0xAE) continue;
            if (!Find(entry, 0x83, out var type) || type.IsEmpty || type.Length > 8) return false;
            ulong value = 0;
            foreach (var b in type) value = (value << 8) | b;
            if (value != 2) return false;
            found = true;
        }
        return found;
    }

    private static bool Find(ReadOnlySpan<byte> bytes, ulong wanted, out ReadOnlySpan<byte> payload)
    {
        while (Read(ref bytes, out var id, out payload)) if (id == wanted) return true;
        payload = default;
        return false;
    }

    private static bool Read(scoped ref ReadOnlySpan<byte> bytes, out ulong id, out ReadOnlySpan<byte> payload)
    {
        payload = default;
        if (!Integer(ref bytes, true, out id) || !Integer(ref bytes, false, out var length)) return false;
        // A Segment is commonly open-ended in browser recordings. Its metadata can be
        // inspected from the available prefix; individual Tracks must be complete.
        if (length > (ulong)bytes.Length && id != 0x18538067) return false;
        var count = (int)Math.Min(length, (ulong)bytes.Length);
        payload = bytes[..count];
        bytes = bytes[count..];
        return true;
    }

    private static bool Integer(ref ReadOnlySpan<byte> bytes, bool identifier, out ulong value)
    {
        value = 0;
        if (bytes.IsEmpty || bytes[0] == 0) return false;
        var length = 1; var mask = 0x80;
        while ((bytes[0] & mask) == 0) { mask >>= 1; length++; }
        if (length > (identifier ? 4 : 8) || length > bytes.Length) return false;
        value = (ulong)(identifier ? bytes[0] : bytes[0] & (mask - 1));
        for (var i = 1; i < length; i++) value = (value << 8) | bytes[i];
        bytes = bytes[length..];
        return true;
    }
}
