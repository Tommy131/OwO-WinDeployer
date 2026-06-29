using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinDeploy.App.Services.Clip;

// ── handshake messages (plaintext JSON frames, exchanged before the session key exists) ──────────────

/// <summary>Step 1, initiator → joiner: announce identity + the public salt/nonce for the PIN handshake.</summary>
public sealed class ClipHello
{
    public string Magic { get; set; } = ClipEdition.Magic;
    public int Proto { get; set; } = ClipEdition.Proto;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Salt { get; set; } = "";     // base64
    public string NonceA { get; set; } = "";   // base64
}

/// <summary>Step 2, joiner → initiator: identity + nonceB + proof it knows the PIN.</summary>
public sealed class ClipAuthB
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string NonceB { get; set; } = "";   // base64
    public string MacB { get; set; } = "";     // base64
}

/// <summary>Step 3, initiator → joiner: accept/reject + (on accept) proof it also knows the PIN.</summary>
public sealed class ClipAuthA
{
    public bool Ok { get; set; }
    public string MacA { get; set; } = "";     // base64
}

// ── session messages (carried inside AES-GCM frames after pairing) ───────────────────────────────────

public enum ClipWireKind { Board, Entry, Delete }

/// <summary>One application message over an established (encrypted) link.</summary>
public sealed class ClipWire
{
    public ClipWireKind Kind { get; set; }
    /// <summary>Entry payload for <see cref="ClipWireKind.Entry"/>.</summary>
    public ClipEntry? Entry { get; set; }
    /// <summary>Whole board for <see cref="ClipWireKind.Board"/> (sent right after pairing).</summary>
    public List<ClipEntry>? Entries { get; set; }
    /// <summary>Target id for <see cref="ClipWireKind.Delete"/>.</summary>
    public string? EntryId { get; set; }
}

/// <summary>Length-prefixed framing + JSON (de)serialization over a stream. Each frame is a 4-byte
/// big-endian length followed by that many bytes (a plaintext JSON handshake message, or an AES-GCM
/// sealed session message). A hard cap rejects absurd lengths so a hostile/garbled peer can't OOM us.</summary>
public static class ClipProtocol
{
    /// <summary>Max single frame. Covers the largest allowed image entry (≤16 MB PNG) after base64 (~1.37×)
    /// plus JSON overhead, with headroom. A hard cap so a hostile/garbled peer can't make us allocate wildly.</summary>
    public const int MaxFrame = 32 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] ToJson<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOpts);
    public static T? FromJson<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, JsonOpts);

    public static async Task WriteFrameAsync(Stream s, byte[] payload, CancellationToken ct)
    {
        if (payload.Length > MaxFrame) throw new IOException($"frame too large: {payload.Length}");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await s.WriteAsync(header, ct);
        await s.WriteAsync(payload, ct);
        await s.FlushAsync(ct);
    }

    /// <summary>Read one frame, or null on a clean EOF (peer closed). Throws on a malformed / oversized frame.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream s, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(s, header, ct)) return null;
        var len = BinaryPrimitives.ReadInt32BigEndian(header);
        if (len < 0 || len > MaxFrame) throw new IOException($"bad frame length: {len}");
        var buf = new byte[len];
        if (len > 0 && !await ReadExactAsync(s, buf, ct)) return null;
        return buf;
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var off = 0;
        while (off < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(off), ct);
            if (n == 0) return false;   // EOF
            off += n;
        }
        return true;
    }
}
