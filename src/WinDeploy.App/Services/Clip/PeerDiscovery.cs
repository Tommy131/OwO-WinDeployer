using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WinDeploy.App.Services.Clip;

/// <summary>The UDP presence beacon: periodically announces "I'm here" (instance id, device name, TCP
/// pairing port, version) and listens for the same from other OwO! WinDeploy instances on the LAN, so only
/// devices actually running the app surface as peers. Discovery reveals presence only — no clipboard data
/// crosses this channel; that requires PIN pairing over the TCP link.
///
/// Robustness on real Windows boxes: a dev machine usually has several NICs (Wi-Fi/Ethernet plus virtual
/// adapters from VMware / Hyper-V / WSL / VirtualBox / Docker). A single socket using the OS "default"
/// multicast interface frequently picks a virtual adapter, so peers never see each other. We therefore JOIN
/// the group and SEND the beacon on EVERY usable interface, and also send a limited broadcast as a fallback
/// for networks where multicast is filtered.</summary>
public sealed class PeerDiscovery : IDisposable
{
    // Admin-scoped multicast group. Stays on the local subnet (multicast TTL 1).
    private static readonly IPAddress Group = IPAddress.Parse("239.255.71.84");
    // Stop a UDP receive from throwing (WSAECONNRESET) when a prior send drew an ICMP port-unreachable.
    private const int SIO_UDP_CONNRESET = -1744830452;   // 0x9800000C

    private readonly ConcurrentDictionary<string, ClipPeer> _peers = new();
    private UdpClient? _rx;                          // bound to the discovery port, joined on every NIC
    private readonly List<UdpClient> _tx = new();    // one sender per NIC (multicast + broadcast)
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

        var ifaces = LocalIPv4Interfaces();

        // ── receiver: one socket on the port, joined to the group on EVERY interface ──
        var rx = new UdpClient(AddressFamily.InterNetwork);
        rx.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try { rx.Client.IOControl(SIO_UDP_CONNRESET, new byte[4], null); } catch { /* not fatal */ }
        rx.EnableBroadcast = true;
        rx.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
        var joined = 0;
        foreach (var ip in ifaces)
            try { rx.JoinMulticastGroup(Group, ip); joined++; } catch { /* this NIC can't multicast — broadcast still covers it */ }
        if (joined == 0) try { rx.JoinMulticastGroup(Group); } catch { /* fall back to the default interface */ }
        _rx = rx;

        // ── senders: one per interface so the beacon leaves EVERY NIC (multicast + limited broadcast) ──
        foreach (var ip in ifaces)
        {
            try
            {
                var tx = new UdpClient(new IPEndPoint(ip, 0)) { EnableBroadcast = true };
                try { tx.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ip.GetAddressBytes()); } catch { }
                try { tx.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1); } catch { }
                _tx.Add(tx);
            }
            catch { /* skip a NIC we can't bind a sender on */ }
        }
        if (_tx.Count == 0)
            try { _tx.Add(new UdpClient { EnableBroadcast = true }); } catch { /* last-resort default sender */ }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Running = true;
        _ = ReceiveLoopAsync(ct);
        _ = BeaconLoopAsync(ct);
        var ifaceList = ifaces.Count > 0 ? string.Join(", ", ifaces) : "(无可用网卡)";
        Log?.Invoke($"发现服务已启动 · 多播 {Group}:{_port} + 广播 · 网卡 {ifaceList}");
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        try { _cts?.Cancel(); } catch { }
        try { _rx?.Dispose(); } catch { }
        _rx = null;
        foreach (var tx in _tx) { try { tx.Dispose(); } catch { } }
        _tx.Clear();
        _peers.Clear();
        PeersChanged?.Invoke();
        Log?.Invoke("发现服务已停止");
    }

    /// <summary>Update the advertised device name without a restart (the user renamed this device).</summary>
    public void UpdateName(string name) => _selfName = name;

    private async Task BeaconLoopAsync(CancellationToken ct)
    {
        var mcast = new IPEndPoint(Group, _port);
        var bcast = new IPEndPoint(IPAddress.Broadcast, _port);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Presence beacon — decoupled from the pairing handshake; carries only id/name/TCP port/version.
                var beacon = ClipProtocol.ToJson(new ClipBeacon
                {
                    Id = _selfId, Name = _selfName, Port = _tcpPort, Version = _version,
                });
                foreach (var tx in _tx)
                {
                    try { tx.Send(beacon, beacon.Length, mcast); } catch { }
                    try { tx.Send(beacon, beacon.Length, bcast); } catch { }
                }
            }
            catch { /* transient send error — keep beaconing */ }

            Prune();
            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _rx != null)
        {
            UdpReceiveResult res;
            try { res = await _rx.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }   // transient — keep listening

            try
            {
                var b = ClipProtocol.FromJson<ClipBeacon>(res.Buffer);
                if (b == null || b.Magic != ClipEdition.Magic || string.IsNullOrEmpty(b.Id)) continue;
                if (b.Id == _selfId) continue;   // ignore our own beacon (multicast/broadcast loops back)

                var addr = res.RemoteEndPoint.Address.ToString();
                var isNew = !_peers.ContainsKey(b.Id);
                var changed = isNew;
                var peer = _peers.GetOrAdd(b.Id, _ => new ClipPeer { InstanceId = b.Id });
                if (peer.DeviceName != b.Name || peer.Address != addr || peer.Port != b.Port) changed = true;
                peer.DeviceName = b.Name;
                peer.Address = addr;
                peer.Port = b.Port;
                peer.Version = b.Version;
                peer.Proto = b.Proto;
                peer.LastSeen = DateTime.Now;
                if (isNew) Log?.Invoke($"发现设备「{b.Name}」（{addr}:{b.Port}）");
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

    /// <summary>Every up, non-loopback IPv4 interface address. Multicast join is attempted per-NIC (failures
    /// skipped); the broadcast fallback still reaches NICs that can't multicast.</summary>
    private static List<IPAddress> LocalIPv4Interfaces()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        list.Add(ua.Address);
            }
        }
        catch { /* best effort */ }
        return list.Distinct().ToList();
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
