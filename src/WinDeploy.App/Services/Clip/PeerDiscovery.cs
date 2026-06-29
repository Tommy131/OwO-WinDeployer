using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace WinDeploy.App.Services.Clip;

/// <summary>The UDP presence beacon: periodically multicasts "I'm here" (instance id, device name, TCP
/// pairing port, version) and listens for the same from other OwO! WinDeploy instances on the LAN, so only
/// devices actually running the app surface as peers. Discovery reveals presence only — no clipboard data
/// crosses this channel; that requires PIN pairing over the TCP link.</summary>
public sealed class PeerDiscovery : IDisposable
{
    // Admin-scoped multicast group + the configured port. Stays on the LAN (TTL 1, not routed off-subnet).
    private static readonly IPAddress Group = IPAddress.Parse("239.255.71.84");

    private readonly ConcurrentDictionary<string, ClipPeer> _peers = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private string _selfId = "";
    private string _selfName = "";
    private string _version = "";
    private int _tcpPort;
    private int _port;

    /// <summary>Raised (on a background thread) whenever the peer set changes — marshal to the UI.</summary>
    public event Action? PeersChanged;
    public event Action<string>? Log;

    public bool Running { get; private set; }
    public IReadOnlyList<ClipPeer> Peers => _peers.Values.OrderBy(p => p.DeviceName, StringComparer.OrdinalIgnoreCase).ToList();

    public void Start(ClipSyncConfig cfg, string instanceId, string version)
    {
        if (Running) return;
        _selfId = instanceId;
        _selfName = cfg.DeviceName;
        _version = version;
        _tcpPort = cfg.Port;
        _port = cfg.DiscoveryPort;

        var udp = new UdpClient { EnableBroadcast = true };
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
        udp.JoinMulticastGroup(Group);
        udp.Ttl = 1;   // keep beacons on the local subnet
        _udp = udp;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Running = true;
        _ = ReceiveLoopAsync(ct);
        _ = BeaconLoopAsync(ct);
        Log?.Invoke($"发现服务已启动 · 多播 {Group}:{_port}");
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        try { _cts?.Cancel(); } catch { }
        try { _udp?.DropMulticastGroup(Group); } catch { }
        try { _udp?.Dispose(); } catch { }
        _udp = null;
        _peers.Clear();
        PeersChanged?.Invoke();
        Log?.Invoke("发现服务已停止");
    }

    /// <summary>Update the advertised device name without a restart (the user renamed this device).</summary>
    public void UpdateName(string name) => _selfName = name;

    private async Task BeaconLoopAsync(CancellationToken ct)
    {
        var ep = new IPEndPoint(Group, _port);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Presence beacon — decoupled from the pairing handshake; carries only id/name/TCP port/version.
                var beacon = ClipProtocol.ToJson(new ClipBeacon
                {
                    Id = _selfId, Name = _selfName, Port = _tcpPort, Version = _version,
                });
                if (_udp != null) await _udp.SendAsync(beacon, beacon.Length, ep);
            }
            catch { /* transient send error — keep beaconing */ }

            Prune();
            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp != null)
        {
            UdpReceiveResult res;
            try { res = await _udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }   // transient — keep listening

            try
            {
                var b = ClipProtocol.FromJson<ClipBeacon>(res.Buffer);
                if (b == null || b.Magic != ClipEdition.Magic || string.IsNullOrEmpty(b.Id)) continue;
                if (b.Id == _selfId) continue;   // ignore our own beacon

                var addr = res.RemoteEndPoint.Address.ToString();
                var changed = !_peers.ContainsKey(b.Id);
                var peer = _peers.GetOrAdd(b.Id, _ => new ClipPeer { InstanceId = b.Id });
                if (peer.DeviceName != b.Name || peer.Address != addr || peer.Port != b.Port) changed = true;
                peer.DeviceName = b.Name;
                peer.Address = addr;
                peer.Port = b.Port;
                peer.Version = b.Version;
                peer.Proto = b.Proto;
                peer.LastSeen = DateTime.Now;
                if (changed) PeersChanged?.Invoke();
            }
            catch { /* not one of ours / malformed — ignore */ }
        }
    }

    /// <summary>Drop peers whose beacon has gone silent (offline / left the network).</summary>
    private void Prune()
    {
        var cutoff = DateTime.Now.AddSeconds(-12);
        var removed = false;
        foreach (var kv in _peers)
            if (kv.Value.LastSeen < cutoff && _peers.TryRemove(kv.Key, out _)) removed = true;
        if (removed) PeersChanged?.Invoke();
    }

    public void Dispose() => Stop();
}

/// <summary>The UDP beacon payload — presence only (no clipboard data).</summary>
public sealed class ClipBeacon
{
    public string Magic { get; set; } = ClipEdition.Magic;
    public int Proto { get; set; } = ClipEdition.Proto;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public string Version { get; set; } = "";
}
