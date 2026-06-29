using System.IO;
using System.Net.Sockets;
using System.Text;

namespace WinDeploy.App.Services.Clip;

/// <summary>One authenticated, AES-GCM-encrypted link to a single peer.
///
/// Pairing direction: the <b>initiator</b> (who generated and reads out the PIN) dials the peer and runs
/// <see cref="ConnectAsync"/>; the <b>joiner</b> accepts the inbound socket and runs <see cref="AcceptAsync"/>,
/// prompting the user for the PIN (with a few retries on the same connection). After a successful mutual
/// HMAC proof both sides derive the session key and the link carries <see cref="ClipWire"/> messages.</summary>
public sealed class ClipLink : IDisposable
{
    private const int MaxAttempts = 5;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendGate = new(1, 1);   // serialize writes so concurrent sends don't interleave frames
    private byte[] _key = Array.Empty<byte>();
    private CancellationTokenSource? _pumpCts;

    public string PeerId { get; private set; } = "";
    public string PeerName { get; private set; } = "";
    public string Remote { get; }
    public bool IsInitiator { get; }
    public DateTime PairedAt { get; private set; } = DateTime.Now;

    /// <summary>Raised (background thread) for each decrypted message — marshal to the UI.</summary>
    public event Action<ClipLink, ClipWire>? MessageReceived;
    public event Action<ClipLink>? Closed;
    public event Action<string>? Log;

    private ClipLink(TcpClient client, bool initiator)
    {
        _client = client;
        _stream = client.GetStream();
        IsInitiator = initiator;
        Remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
    }

    // ── pairing: initiator (knows the PIN, dials the peer) ───────────────────────────────────────────
    public static async Task<ClipLink> ConnectAsync(ClipPeer peer, string pin, string selfId, string selfName, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(System.Net.IPAddress.Parse(peer.Address), peer.Port, ct);
        }
        catch { client.Dispose(); throw; }

        var link = new ClipLink(client, initiator: true);
        try
        {
            var salt = ClipCrypto.RandomBytes(16);
            var nonceA = ClipCrypto.RandomBytes(16);
            var key = ClipCrypto.DeriveKey(pin, salt);

            await link.WriteMsgAsync(new ClipHello
            {
                Id = selfId, Name = selfName,
                Salt = Convert.ToBase64String(salt), NonceA = Convert.ToBase64String(nonceA),
            }, ct);

            // Verify successive joiner attempts until one proves the PIN or we exhaust the cap.
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var authB = await link.ReadMsgAsync<ClipAuthB>(ct);
                var nonceB = Convert.FromBase64String(authB.NonceB);
                var expect = Proof("B", key, nonceA, nonceB, selfId, authB.Id);
                var got = Convert.FromBase64String(authB.MacB);
                if (!ClipCrypto.MacEquals(expect, got))
                {
                    await link.WriteMsgAsync(new ClipAuthA { Ok = false }, ct);
                    continue;   // wrong PIN — let the joiner re-prompt and try again
                }

                var macA = Proof("A", key, nonceA, nonceB, selfId, authB.Id);
                await link.WriteMsgAsync(new ClipAuthA { Ok = true, MacA = Convert.ToBase64String(macA) }, ct);
                link._key = ClipCrypto.DeriveSessionKey(key, nonceA, nonceB);
                link.PeerId = authB.Id;
                link.PeerName = string.IsNullOrWhiteSpace(authB.Name) ? peer.DeviceName : authB.Name;
                link.PairedAt = DateTime.Now;
                return link;
            }
            throw new InvalidOperationException("配对失败：PIN 多次错误");
        }
        catch { link.Dispose(); throw; }
    }

    // ── pairing: joiner (prompts the user for the PIN) ───────────────────────────────────────────────
    /// <param name="pinPrompt">Prompts for the PIN; receives (peerName, attemptIndex) and returns the
    /// entered PIN, or null to cancel pairing.</param>
    public static async Task<ClipLink?> AcceptAsync(TcpClient client, Func<string, int, Task<string?>> pinPrompt,
        string selfId, string selfName, CancellationToken ct)
    {
        var link = new ClipLink(client, initiator: false);
        try
        {
            var hello = await link.ReadMsgAsync<ClipHello>(ct);
            if (hello.Magic != ClipEdition.Magic) throw new InvalidOperationException("非 OwO 剪贴板连接");
            if (hello.Proto != ClipEdition.Proto) throw new InvalidOperationException("协议版本不兼容");
            var salt = Convert.FromBase64String(hello.Salt);
            var nonceA = Convert.FromBase64String(hello.NonceA);
            var nonceB = ClipCrypto.RandomBytes(16);

            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var pin = await pinPrompt(hello.Name, attempt);
                if (pin == null) { link.Dispose(); return null; }   // user cancelled

                var key = ClipCrypto.DeriveKey(pin, salt);
                var macB = Proof("B", key, nonceA, nonceB, hello.Id, selfId);
                await link.WriteMsgAsync(new ClipAuthB
                {
                    Id = selfId, Name = selfName,
                    NonceB = Convert.ToBase64String(nonceB), MacB = Convert.ToBase64String(macB),
                }, ct);

                var authA = await link.ReadMsgAsync<ClipAuthA>(ct);
                if (!authA.Ok) continue;   // initiator rejected — wrong PIN, re-prompt

                var expectA = Proof("A", key, nonceA, nonceB, hello.Id, selfId);
                if (!ClipCrypto.MacEquals(expectA, Convert.FromBase64String(authA.MacA)))
                    throw new InvalidOperationException("对端校验失败（可能遭到中间人攻击）");

                link._key = ClipCrypto.DeriveSessionKey(key, nonceA, nonceB);
                link.PeerId = hello.Id;
                link.PeerName = string.IsNullOrWhiteSpace(hello.Name) ? link.Remote : hello.Name;
                link.PairedAt = DateTime.Now;
                return link;
            }
            link.Dispose();
            return null;   // attempts exhausted
        }
        catch { link.Dispose(); throw; }
    }

    /// <summary>HMAC proof binding the role, both nonces, and both identities under the PIN-derived key.</summary>
    private static byte[] Proof(string role, byte[] key, byte[] nonceA, byte[] nonceB, string initiatorId, string joinerId)
        => ClipCrypto.Mac(key, Encoding.ASCII.GetBytes(role), nonceA, nonceB,
            Encoding.UTF8.GetBytes(initiatorId), Encoding.UTF8.GetBytes(joinerId));

    // ── session ──────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Begin the receive pump (after pairing). Raises <see cref="MessageReceived"/> per message and
    /// <see cref="Closed"/> when the peer disconnects or errors.</summary>
    public void StartPump()
    {
        _pumpCts = new CancellationTokenSource();
        _ = PumpAsync(_pumpCts.Token);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await ClipProtocol.ReadFrameAsync(_stream, ct);
                if (frame == null) break;                       // peer closed
                var plain = ClipCrypto.Decrypt(_key, frame);
                if (plain == null) { Log?.Invoke($"丢弃来自 {PeerName} 的损坏数据帧"); continue; }
                var wire = ClipProtocol.FromJson<ClipWire>(plain);
                if (wire != null) MessageReceived?.Invoke(this, wire);
            }
        }
        catch (OperationCanceledException) { /* closing */ }
        catch (Exception ex) { Log?.Invoke($"与 {PeerName} 的连接中断：{ex.Message}"); }
        finally { Closed?.Invoke(this); }
    }

    public async Task SendAsync(ClipWire msg, CancellationToken ct = default)
    {
        var json = ClipProtocol.ToJson(msg);
        var blob = ClipCrypto.Encrypt(_key, json);
        await _sendGate.WaitAsync(ct);
        try { await ClipProtocol.WriteFrameAsync(_stream, blob, ct); }
        finally { _sendGate.Release(); }
    }

    private async Task WriteMsgAsync<T>(T msg, CancellationToken ct)
        => await ClipProtocol.WriteFrameAsync(_stream, ClipProtocol.ToJson(msg), ct);

    private async Task<T> ReadMsgAsync<T>(CancellationToken ct)
    {
        var frame = await ClipProtocol.ReadFrameAsync(_stream, ct)
            ?? throw new IOException("握手期间对端关闭了连接");
        return ClipProtocol.FromJson<T>(frame) ?? throw new IOException("握手消息格式错误");
    }

    public void Close()
    {
        try { _pumpCts?.Cancel(); } catch { }
        try { _client.Close(); } catch { }
    }

    public void Dispose()
    {
        try { _pumpCts?.Cancel(); } catch { }
        try { _pumpCts?.Dispose(); } catch { }
        try { _sendGate.Dispose(); } catch { }
        try { _stream.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
    }
}
