using System.Security.Cryptography;
using System.Text;

namespace WinDeploy.App.Services.Clip;

/// <summary>
/// Crypto primitives for the PIN-based pairing handshake and the encrypted link.
///
/// Threat model: a LAN with passive sniffers and possible active attackers, a short-lived 6-digit PIN
/// conveyed out-of-band (the initiator reads it off-screen to the joiner). We never put the PIN on the
/// wire — both sides derive a key from it (PBKDF2) and prove knowledge via a mutual HMAC challenge /
/// response over public nonces. A correct exchange then derives an ephemeral AES-GCM session key (HKDF).
/// A wrong PIN fails the MAC, so it leaks nothing; online guessing is bounded by the joiner's attempt
/// cap and the invite's short validity window. (A full PAKE — SPAKE2/J-PAKE — would also defeat an
/// active MITM that online-guesses the PIN; that is noted as future hardening in the design doc.)
/// </summary>
public static class ClipCrypto
{
    private const int PbkdfIterations = 200_000;
    private const int KeySize = 32;     // 256-bit
    private const int GcmNonce = 12;
    private const int GcmTag = 16;

    /// <summary>A random 6-digit PIN (zero-padded), e.g. "048213".</summary>
    public static string NewPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static byte[] RandomBytes(int n) => RandomNumberGenerator.GetBytes(n);

    /// <summary>Derive the shared pairing key from the PIN and the initiator's random salt.</summary>
    public static byte[] DeriveKey(string pin, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(
        Encoding.UTF8.GetBytes(pin ?? ""), salt, PbkdfIterations, HashAlgorithmName.SHA256, KeySize);

    /// <summary>HMAC-SHA256 of the concatenation of the given parts under <paramref name="key"/>.</summary>
    public static byte[] Mac(byte[] key, params byte[][] parts)
    {
        using var h = new HMACSHA256(key);
        foreach (var p in parts) h.TransformBlock(p, 0, p.Length, null, 0);
        h.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return h.Hash!;
    }

    /// <summary>Constant-time MAC comparison.</summary>
    public static bool MacEquals(byte[] a, byte[] b) => CryptographicOperations.FixedTimeEquals(a, b);

    /// <summary>Derive the per-session AES key from the pairing key and both nonces (HKDF-SHA256).</summary>
    public static byte[] DeriveSessionKey(byte[] pairingKey, byte[] nonceA, byte[] nonceB)
    {
        var info = Concat(Encoding.ASCII.GetBytes("owo-clip-session"), nonceA, nonceB);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, pairingKey, KeySize, salt: null, info: info);
    }

    /// <summary>AES-256-GCM seal: returns nonce(12) ‖ tag(16) ‖ ciphertext.</summary>
    public static byte[] Encrypt(byte[] key, byte[] plaintext)
    {
        var nonce = RandomBytes(GcmNonce);
        var tag = new byte[GcmTag];
        var ct = new byte[plaintext.Length];
        using var gcm = new AesGcm(key, GcmTag);
        gcm.Encrypt(nonce, plaintext, ct, tag);
        return Concat(nonce, tag, ct);
    }

    /// <summary>AES-256-GCM open of a blob produced by <see cref="Encrypt"/>; null on tamper / bad key.</summary>
    public static byte[]? Decrypt(byte[] key, byte[] blob)
    {
        try
        {
            if (blob.Length < GcmNonce + GcmTag) return null;
            var nonce = blob.AsSpan(0, GcmNonce);
            var tag = blob.AsSpan(GcmNonce, GcmTag);
            var ct = blob.AsSpan(GcmNonce + GcmTag);
            var pt = new byte[ct.Length];
            using var gcm = new AesGcm(key, GcmTag);
            gcm.Decrypt(nonce, ct, tag, pt);
            return pt;
        }
        catch { return null; }   // authentication failed → drop the frame
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var len = 0;
        foreach (var p in parts) len += p.Length;
        var r = new byte[len];
        var o = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
        return r;
    }
}
