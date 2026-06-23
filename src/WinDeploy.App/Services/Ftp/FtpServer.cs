using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace WinDeploy.App.Services.Ftp;

/// <summary>Live state of one connected control session, surfaced to the monitor UI.</summary>
public sealed class FtpConnectionInfo
{
    public int Id { get; init; }
    public string Remote { get; init; } = "";
    public DateTime ConnectedAt { get; init; } = DateTime.Now;
    public volatile string User = "(未登录)";
    public volatile string Activity = "已连接";
    public long BytesUp;     // received from client (uploads)
    public long BytesDown;   // sent to client (downloads)
}

/// <summary>A single line for the live server log.</summary>
public readonly record struct FtpLogEntry(DateTime Time, int ConnId, string Text);

/// <summary>A zero-dependency FTP / FTPS control server. Binds the control port (and, in implicit mode, the
/// implicit-TLS port), accepts connections, enforces the configured concurrency caps, and hands each socket
/// to an <see cref="FtpSession"/>. Per-connection state and a rolling log are exposed for the monitor page.</summary>
public sealed class FtpServer : IDisposable
{
    private TcpListener? _control;
    private TcpListener? _implicit;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<int, FtpConnectionInfo> _sessions = new();
    private int _idSeq;
    private int _passiveCursor;
    private readonly object _passiveLock = new();

    public FtpServerConfig Config { get; private set; } = new();
    public X509Certificate2? Certificate { get; private set; }
    public bool Running { get; private set; }
    public DateTime? StartedAt { get; private set; }

    /// <summary>Raised (on a background thread) for every protocol/diagnostic line — marshal to the UI.</summary>
    public event Action<FtpLogEntry>? Logged;
    /// <summary>Raised (on a background thread) when a session is added or removed.</summary>
    public event Action? ConnectionsChanged;

    public IReadOnlyList<FtpConnectionInfo> Connections => _sessions.Values.OrderBy(c => c.Id).ToList();
    public int ConnectionCount => _sessions.Count;

    /// <summary>Bind the listeners and begin accepting. Throws on bind failure (port in use / invalid cert).</summary>
    public void Start(FtpServerConfig cfg)
    {
        if (Running) return;
        Config = cfg;

        if (cfg.TlsEnabled)
        {
            try { Certificate = FtpCertificate.Resolve(cfg); }
            catch (Exception ex) { throw new InvalidOperationException("加载/生成 SSL 证书失败：" + ex.Message, ex); }
        }

        var bindIp = ParseBindAddress(cfg.ListenAddress);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Control port (plain or explicit-TLS). In implicit mode this same port is unused by clients but we
        // still bind it for explicit AUTH TLS compatibility unless implicit-only is desired; bind both.
        _control = new TcpListener(bindIp, cfg.Port);
        try { _control.Start(); }
        catch (Exception ex)
        {
            _control = null;
            throw new InvalidOperationException($"无法监听控制端口 {cfg.Port}：{ex.Message}", ex);
        }
        _ = AcceptLoopAsync(_control, implicitTls: false, token);

        if (cfg.ImplicitTls)
        {
            _implicit = new TcpListener(bindIp, cfg.ImplicitPort);
            try { _implicit.Start(); }
            catch (Exception ex)
            {
                StopListeners();
                throw new InvalidOperationException($"无法监听隐式 TLS 端口 {cfg.ImplicitPort}：{ex.Message}", ex);
            }
            _ = AcceptLoopAsync(_implicit, implicitTls: true, token);
        }

        Running = true;
        StartedAt = DateTime.Now;
        Log(0, $"服务端已启动 · 控制端口 {cfg.Port}" +
               (cfg.TlsEnabled ? $" · TLS {(cfg.ImplicitTls ? $"隐式 {cfg.ImplicitPort}" : "显式 AUTH TLS")}" : " · 明文") +
               $" · 被动端口 {cfg.PassiveMin}-{cfg.PassiveMax}");
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        try { _cts?.Cancel(); } catch { }
        StopListeners();
        foreach (var c in _sessions.Values) c.Activity = "服务端停止，断开";
        _sessions.Clear();
        ConnectionsChanged?.Invoke();
        Certificate?.Dispose();
        Certificate = null;
        StartedAt = null;
        Log(0, "服务端已停止");
    }

    private void StopListeners()
    {
        try { _control?.Stop(); } catch { }
        try { _implicit?.Stop(); } catch { }
        _control = null;
        _implicit = null;
    }

    private async Task AcceptLoopAsync(TcpListener listener, bool implicitTls, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            _ = HandleAcceptedAsync(client, implicitTls, token);
        }
    }

    private async Task HandleAcceptedAsync(TcpClient client, bool implicitTls, CancellationToken token)
    {
        var remote = (client.Client.RemoteEndPoint as IPEndPoint);
        var remoteText = remote?.ToString() ?? "?";

        // Total-connection cap: refuse politely and close.
        if (Config.MaxConnections > 0 && _sessions.Count >= Config.MaxConnections)
        {
            await RejectAsync(client, "421 服务器连接数已达上限，请稍后再试。\r\n");
            Log(0, $"拒绝 {remoteText}：达到总连接上限 {Config.MaxConnections}");
            return;
        }
        // Per-IP cap.
        if (Config.MaxConnectionsPerIp > 0 && remote != null &&
            _sessions.Values.Count(s => s.Remote.StartsWith(remote.Address.ToString() + ":", StringComparison.Ordinal)) >= Config.MaxConnectionsPerIp)
        {
            await RejectAsync(client, "421 同一 IP 的连接数已达上限。\r\n");
            Log(0, $"拒绝 {remoteText}：达到单 IP 上限 {Config.MaxConnectionsPerIp}");
            return;
        }

        var id = Interlocked.Increment(ref _idSeq);
        var info = new FtpConnectionInfo { Id = id, Remote = remoteText };
        _sessions[id] = info;
        ConnectionsChanged?.Invoke();
        Log(id, $"连接来自 {remoteText}" + (implicitTls ? "（隐式 TLS）" : ""));

        var session = new FtpSession(this, id, client, info, implicitTls);
        try { await session.RunAsync(token); }
        catch (Exception ex) { Log(id, "会话异常：" + ex.Message); }
        finally
        {
            _sessions.TryRemove(id, out _);
            ConnectionsChanged?.Invoke();
            Log(id, "连接关闭");
            try { client.Dispose(); } catch { }
        }
    }

    private static async Task RejectAsync(TcpClient client, string line)
    {
        try
        {
            using (client)
            {
                var bytes = System.Text.Encoding.ASCII.GetBytes(line);
                await client.GetStream().WriteAsync(bytes);
            }
        }
        catch { /* ignore */ }
    }

    // ── helpers used by sessions ─────────────────────────────────────────────
    internal int CountForUser(string user) =>
        _sessions.Values.Count(s => string.Equals(s.User, user, StringComparison.OrdinalIgnoreCase));

    internal void Log(int connId, string text)
        => Logged?.Invoke(new FtpLogEntry(DateTime.Now, connId, text));

    /// <summary>Bind a passive-data listener on the next free port in the configured range.</summary>
    internal TcpListener CreatePassiveListener(IPAddress bindIp, out int port)
    {
        lock (_passiveLock)
        {
            var min = Math.Max(1, Config.PassiveMin);
            var max = Math.Max(min, Config.PassiveMax);
            var span = max - min + 1;
            for (var i = 0; i < span; i++)
            {
                var p = min + ((_passiveCursor++ % span + span) % span);
                try
                {
                    var l = new TcpListener(bindIp, p);
                    l.Start();
                    port = p;
                    return l;
                }
                catch (SocketException) { /* in use — try next */ }
            }
        }
        throw new IOException($"被动端口范围 {Config.PassiveMin}-{Config.PassiveMax} 内没有可用端口。");
    }

    private static IPAddress ParseBindAddress(string addr)
    {
        if (string.IsNullOrWhiteSpace(addr) || addr is "0.0.0.0" or "*") return IPAddress.Any;
        return IPAddress.TryParse(addr, out var ip) ? ip : IPAddress.Any;
    }

    public void Dispose() => Stop();
}
