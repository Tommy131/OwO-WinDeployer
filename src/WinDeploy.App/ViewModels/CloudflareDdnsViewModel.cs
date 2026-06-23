using System.Collections.ObjectModel;
using System.Windows;
using WinDeploy.App.Services;

namespace WinDeploy.App.ViewModels;

/// <summary>One zone (domain) row for the picker.</summary>
public sealed class CfZoneItem
{
    public CfZone Z { get; }
    public CfZoneItem(CfZone z) => Z = z;
    public string Id => Z.Id;
    public string Name => Z.Name;
    public string Display => Z.Status is "active" or "" ? Z.Name : $"{Z.Name}（{Z.Status}）";
    // The themed ComboBox template renders the closed selection box from SelectionBoxItem (no generated
    // SelectionBoxItemTemplate), so it falls back to ToString() — return the domain name, not the type name.
    public override string ToString() => Display;
}

/// <summary>One DNS record card, with its current DDNS-bound state.</summary>
public sealed class CfRecordItem : ObservableObject
{
    public CfDnsRecord R { get; private set; }
    public CfRecordItem(CfDnsRecord r, bool bound) { R = r; _bound = bound; }

    public string Type => R.Type;
    public string Name => R.Name;
    public string Content => R.Content;
    public bool Proxied => R.Proxied;
    public string TtlText => R.Ttl <= 1 ? "自动" : $"{R.Ttl}s";
    public bool IsDdnsEligible => R.Type is "A" or "AAAA";

    private bool _bound;
    public bool IsBound { get => _bound; set { if (Set(ref _bound, value)) OnPropertyChanged(nameof(BindLabel)); } }
    public string BindLabel => _bound ? "取消绑定" : "绑定 DDNS";
}

/// <summary>One active DDNS binding row (with its last-applied IP + enable toggle).</summary>
public sealed class DdnsBindingItem : ObservableObject
{
    public DdnsBinding B { get; }
    public DdnsBindingItem(DdnsBinding b) { B = b; _enabled = b.Enabled; }

    public string RecordName => B.RecordName;
    public string Type => B.Type;
    public string ZoneName => B.ZoneName;
    public string LastIpText => string.IsNullOrEmpty(B.LastIp) ? "（尚未应用）" : B.LastIp!;
    public string LastUpdateText =>
        string.IsNullOrEmpty(B.LastUpdate) ? "" :
        DateTime.TryParse(B.LastUpdate, out var d) ? d.ToString("MM-dd HH:mm:ss") : B.LastUpdate!;

    private bool _enabled;
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) { B.Enabled = value; EnabledChanged?.Invoke(); } } }
    public event Action? EnabledChanged;

    public void RefreshState() { OnPropertyChanged(nameof(LastIpText)); OnPropertyChanged(nameof(LastUpdateText)); }
}

/// <summary>The 「Cloudflare DDNS」 page (开发人员模式): saves a scoped API token (DPAPI-encrypted), lists the
/// account's zones and DNS records, creates / edits records, and binds A/AAAA records to a background monitor
/// that keeps them pointed at this device's public IP. Dev-only.</summary>
public sealed class CloudflareDdnsViewModel : ObservableObject
{
    private readonly CloudflareDdnsMonitor _monitor = new();
    private CloudflareConfig _cfg;

    public ObservableCollection<CfZoneItem> Zones { get; } = new();
    public ObservableCollection<CfRecordItem> Records { get; } = new();
    public ObservableCollection<DdnsBindingItem> Bindings { get; } = new();

    public RelayCommand VerifyTokenCommand { get; }
    public RelayCommand RefreshZonesCommand { get; }
    public RelayCommand RefreshRecordsCommand { get; }
    public RelayCommand NewRecordCommand { get; }
    public RelayCommand EditRecordCommand { get; }
    public RelayCommand UpdateToCurrentIpCommand { get; }
    public RelayCommand ToggleBindCommand { get; }
    public RelayCommand RemoveBindingCommand { get; }
    public RelayCommand StartMonitorCommand { get; }
    public RelayCommand StopMonitorCommand { get; }
    public RelayCommand RunOnceCommand { get; }
    public RelayCommand OpenTokenHelpCommand { get; }
    public RelayCommand ShowPermsCommand { get; }

    public CloudflareDdnsViewModel()
    {
        _cfg = CloudflareConfigStore.Load();
        _token = CloudflareConfigStore.LoadToken();
        _email = _cfg.Email ?? "";
        _useGlobalKey = !string.IsNullOrWhiteSpace(_cfg.Email);   // a saved email means the last credential was a Global API Key
        _intervalText = _cfg.IntervalSeconds.ToString();
        _autoStart = _cfg.AutoStart;

        VerifyTokenCommand = new RelayCommand(_ => _ = VerifyAndLoadAsync(), _ => !_busy);
        RefreshZonesCommand = new RelayCommand(_ => _ = LoadZonesAsync(), _ => HasToken && !_busy);
        RefreshRecordsCommand = new RelayCommand(_ => _ = LoadRecordsAsync(), _ => SelectedZone != null && !_busy);
        NewRecordCommand = new RelayCommand(_ => _ = NewRecordAsync(), _ => SelectedZone != null && !_busy);
        EditRecordCommand = new RelayCommand(p => { if (p is CfRecordItem r) _ = EditRecordAsync(r); }, _ => !_busy);
        UpdateToCurrentIpCommand = new RelayCommand(p => { if (p is CfRecordItem r) _ = UpdateToCurrentIpAsync(r); }, _ => !_busy);
        ToggleBindCommand = new RelayCommand(p => { if (p is CfRecordItem r) ToggleBind(r); });
        RemoveBindingCommand = new RelayCommand(p => { if (p is DdnsBindingItem b) RemoveBinding(b); });
        StartMonitorCommand = new RelayCommand(_ => StartMonitor(), _ => !MonitorRunning);
        StopMonitorCommand = new RelayCommand(_ => StopMonitor(), _ => MonitorRunning);
        RunOnceCommand = new RelayCommand(_ => _ = RunOnceAsync(), _ => !_busy);
        OpenTokenHelpCommand = new RelayCommand(_ => Open("https://dash.cloudflare.com/profile/api-tokens"));
        ShowPermsCommand = new RelayCommand(_ => ShowPerms());

        _monitor.Changed += OnMonitorChanged;
        _monitor.Updated += (title, body) => ToastService.TryShow(title, body);

        LoadBindings();

        // Resident auto-start: pick up monitoring on launch if configured and ready (independent of the page
        // ever being opened — that's the "常驻" behaviour).
        if (_cfg.AutoStart && _token.Length > 0 && _cfg.Bindings.Any(b => b.Enabled))
            _monitor.Start();
    }

    private bool _loadedOnce;
    /// <summary>Page opened — lazily fetch the zone list once (avoids a network call at every app launch when
    /// the page is never visited). Called from the view's Loaded.</summary>
    public void Activate()
    {
        if (_loadedOnce || !HasToken) return;
        _loadedOnce = true;
        _ = LoadZonesAsync();
    }

    // ── credential ─────────────────────────────────────────────────────────────────
    private string _token;
    /// <summary>The API token (or Global API Key). Mirrored to/from the view's PasswordBox (the box pushes here
    /// on change; the view reads this to pre-fill the decrypted value on load).</summary>
    public string Token
    {
        get => _token;
        set { if (Set(ref _token, value ?? "")) { OnPropertyChanged(nameof(HasToken)); TokenStatus = ""; } }
    }
    public bool HasToken => !string.IsNullOrWhiteSpace(_token);

    // ── auth mode (explicit, so a token is never accidentally sent as a Global API Key) ──────────────
    private bool _useGlobalKey;
    /// <summary>True → 全局 API Key 模式（X-Auth-Email / X-Auth-Key，需邮箱）；false → API 令牌模式（Bearer，推荐）。</summary>
    public bool UseGlobalKey
    {
        get => _useGlobalKey;
        set { if (Set(ref _useGlobalKey, value)) { OnPropertyChanged(nameof(UseApiToken)); OnPropertyChanged(nameof(ShowEmail)); OnPropertyChanged(nameof(TokenLabel)); TokenStatus = ""; } }
    }
    public bool UseApiToken { get => !_useGlobalKey; set { if (value) UseGlobalKey = false; } }
    /// <summary>Email row is only relevant (and only sent) in Global API Key mode.</summary>
    public bool ShowEmail => _useGlobalKey;
    public string TokenLabel => _useGlobalKey ? "全局 API Key：" : "API 令牌：";

    private string _email;
    /// <summary>Account email — used ONLY in Global API Key mode (X-Auth-Email).</summary>
    public string Email
    {
        get => _email;
        set { if (Set(ref _email, value ?? "")) TokenStatus = ""; }
    }

    private string _tokenStatus = "";
    public string TokenStatus { get => _tokenStatus; set => Set(ref _tokenStatus, value); }

    private bool _tokenOk;
    public bool TokenOk { get => _tokenOk; set => Set(ref _tokenOk, value); }

    /// <summary>A client for the current credential. Email (Global API Key auth) is passed ONLY in Global-Key
    /// mode; in API-Token mode it's omitted so the token always goes out as a Bearer header.</summary>
    private CloudflareClient Client() => new((Token ?? "").Trim(), _useGlobalKey ? (Email ?? "").Trim() : null);

    private async Task VerifyAndLoadAsync()
    {
        var token = (Token ?? "").Trim();
        var email = _useGlobalKey ? (Email ?? "").Trim() : "";   // token mode → clear any saved email
        CloudflareConfigStore.SaveCredential(token, email);
        _cfg = CloudflareConfigStore.Load();
        if (token.Length == 0) { TokenStatus = _useGlobalKey ? "请输入全局 API Key" : "请输入 API 令牌"; TokenOk = false; return; }
        if (_useGlobalKey && email.Length == 0) { TokenStatus = "全局 API Key 模式需要填写账户邮箱"; TokenOk = false; return; }

        TokenStatus = "正在验证 …";
        var v = await Client().VerifyAsync();

        // The real test is whether the credential can do the job — list zones. A correctly-scoped DDNS token
        // (Zone:Read + DNS:Edit, no User scope) is rejected by /user/tokens/verify (#1000) yet reads zones
        // perfectly, so treat a successful zones fetch as valid even when the verify endpoint says otherwise.
        var zonesOk = await LoadZonesAsync();

        if (v.Valid || zonesOk)
        {
            TokenOk = true;
            TokenStatus = v.Valid
                ? "✓ 凭证有效"
                : $"✓ 凭证可用（令牌验证接口未通过，但已成功读取 {Zones.Count} 个域名，可正常使用 DDNS）";
            AuditLog.Action("Cloudflare 凭证：" + (v.Valid ? "验证有效" : "verify 未过但可读取域名，按可用处理"));
        }
        else
        {
            TokenOk = false;
            TokenStatus = InvalidHint(v.Error ?? v.Status);
            AuditLog.Action("Cloudflare 凭证验证：无效 — " + (v.Error ?? v.Status));
        }
    }

    /// <summary>Turn Cloudflare's terse failure into an actionable hint. #1000 = credential reached Cloudflare
    /// well-formed but was rejected (wrong value / type); #6003 = a header is malformed (usually a token sent
    /// as a Global API Key, or vice-versa).</summary>
    private string InvalidHint(string error)
    {
        var msg = "✗ 验证失败：" + error;
        if (error.Contains("6003") || error.Contains("Invalid request headers", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Invalid format", StringComparison.OrdinalIgnoreCase))
            msg += "\n#6003 表示请求头格式不被接受 —— 凭证类型与上方所选模式不匹配：" +
                   "\n→ 若你用的是「API 令牌」：把模式切到「API 令牌」（无需邮箱）后重试。" +
                   "\n→ 若你用的是「全局 API Key」：它应为 37 位十六进制（个人资料 → API 令牌 → Global API Key → 查看），并填写正确的账户邮箱。";
        else if (error.Contains("1000") || error.Contains("Invalid API Token", StringComparison.OrdinalIgnoreCase))
            msg += "\nCloudflare 拒绝了该凭证（请求格式正确，是凭证值本身的问题）。请检查：" +
                   "\n① 使用「API 令牌」（个人资料 → API 令牌 → 创建令牌），需 区域:Zone:Read + 区域:DNS:Edit 权限。" +
                   "\n② 令牌完整、无多余空格/换行（已自动去除不可见字符），属于同一账户。" +
                   "\n③ 若只有「全局 API Key」，请切到「全局 API Key」模式并填写账户邮箱。";
        else if (error.Contains("9103") || error.Contains("Unknown X-Auth", StringComparison.OrdinalIgnoreCase))
            msg += "\n全局 API Key 或账户邮箱不正确，请到「个人资料 → API 令牌 → Global API Key」核对。";
        return msg;
    }

    // ── zones / records ────────────────────────────────────────────────────────────
    private bool _busy;
    private void SetBusy(bool b) { _busy = b; System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }

    private CfZoneItem? _selectedZone;
    public CfZoneItem? SelectedZone
    {
        get => _selectedZone;
        set { if (Set(ref _selectedZone, value)) { System.Windows.Input.CommandManager.InvalidateRequerySuggested(); _ = LoadRecordsAsync(); } }
    }

    private string _note = "";
    public string Note { get => _note; set => Set(ref _note, value); }

    /// <summary>Fetch the account's zones into the picker. Returns true on success — this doubles as the real
    /// proof a credential works (a correctly-scoped DDNS token can read zones even when /user/tokens/verify
    /// denies it).</summary>
    private async Task<bool> LoadZonesAsync()
    {
        var token = (Token ?? "").Trim();
        if (token.Length == 0) { Note = "请先填写并验证凭证"; return false; }
        SetBusy(true);
        Note = "正在获取域名列表 …";
        try
        {
            var zones = await Client().ListZonesAsync();
            var keepId = _selectedZone?.Id;
            Zones.Clear();
            foreach (var z in zones) Zones.Add(new CfZoneItem(z));
            Note = zones.Count == 0 ? "未找到任何域名（确认令牌具备 Zone:Read 权限）" : $"共 {zones.Count} 个域名";
            var restore = Zones.FirstOrDefault(z => z.Id == keepId) ?? Zones.FirstOrDefault();
            if (restore != null) SelectedZone = restore;
            else { Records.Clear(); }
            return true;
        }
        catch (Exception ex) { Note = "获取域名失败：" + ex.Message; return false; }
        finally { SetBusy(false); }
    }

    private async Task LoadRecordsAsync()
    {
        var z = SelectedZone;
        var token = (Token ?? "").Trim();
        Records.Clear();
        if (z == null || token.Length == 0) return;
        SetBusy(true);
        Note = $"正在获取 {z.Name} 的解析记录 …";
        try
        {
            var recs = await Client().ListRecordsAsync(z.Id);
            var boundIds = _cfg.Bindings.Select(b => b.RecordId).ToHashSet();
            Records.Clear();
            foreach (var r in recs) Records.Add(new CfRecordItem(r, boundIds.Contains(r.Id)));
            Note = $"{z.Name}：{recs.Count} 条解析记录";
        }
        catch (Exception ex) { Note = "获取解析失败：" + ex.Message; }
        finally { SetBusy(false); }
    }

    private async Task NewRecordAsync()
    {
        var z = SelectedZone;
        if (z == null) { Note = "请先选择域名"; return; }
        var dlg = new Views.CloudflareRecordDialog(z.Name, prefillIp: _monitor.CurrentIpv4) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        SetBusy(true);
        try
        {
            var (ok, msg, _) = await Client()
                .CreateRecordAsync(z.Id, dlg.RecordType, dlg.FullName(), dlg.RecordContent, dlg.Proxied, dlg.Ttl);
            Note = msg;
            if (ok) { AuditLog.Action($"Cloudflare 新建解析：{dlg.FullName()} {dlg.RecordType} → {dlg.RecordContent}"); await LoadRecordsAsync(); }
            else MessageBox.Show(msg, "新建解析失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private async Task EditRecordAsync(CfRecordItem item)
    {
        var z = SelectedZone;
        if (z == null) return;
        var dlg = new Views.CloudflareRecordDialog(z.Name, item.R) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        SetBusy(true);
        try
        {
            var (ok, msg) = await Client()
                .UpdateRecordAsync(z.Id, item.R.Id, dlg.RecordType, dlg.FullName(), dlg.RecordContent, dlg.Proxied, dlg.Ttl);
            Note = msg;
            if (ok) { AuditLog.Action($"Cloudflare 修改解析：{dlg.FullName()} → {dlg.RecordContent}"); await LoadRecordsAsync(); }
            else MessageBox.Show(msg, "修改解析失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private async Task UpdateToCurrentIpAsync(CfRecordItem item)
    {
        var z = SelectedZone;
        if (z == null) return;
        if (!item.IsDdnsEligible) { Note = "仅 A / AAAA 记录可指向本机 IP"; return; }

        SetBusy(true);
        try
        {
            var v6 = item.R.Type == "AAAA";
            Note = $"正在获取本机公网 {(v6 ? "IPv6" : "IPv4")} …";
            var ip = await PublicIp.GetAsync(v6);
            if (ip == null) { Note = $"未能获取本机公网 {(v6 ? "IPv6" : "IPv4")} 地址"; return; }

            var (ok, msg) = await Client()
                .UpdateRecordAsync(z.Id, item.R.Id, item.R.Type, item.R.Name, ip, item.R.Proxied, item.R.Ttl);
            Note = ok ? $"已将 {item.R.Name} 指向本机 IP {ip}" : "更新失败：" + msg;
            if (ok)
            {
                AuditLog.Action($"Cloudflare 解析指向本机IP：{item.R.Name} → {ip}");
                // If this record is DDNS-bound, remember the applied IP so the monitor won't redo it.
                CloudflareConfigStore.ApplyResults(new[] { (item.R.Id, ip, DateTime.Now.ToString("s")) });
                _cfg = CloudflareConfigStore.Load();
                RefreshBindingState();
                await LoadRecordsAsync();
            }
        }
        finally { SetBusy(false); }
    }

    // ── DDNS bindings ────────────────────────────────────────────────────────────
    private void ToggleBind(CfRecordItem item)
    {
        if (!item.IsDdnsEligible) { Note = "仅支持对 A / AAAA 记录启用 DDNS"; return; }
        var z = SelectedZone;
        if (z == null) return;

        if (_cfg.Bindings.Any(b => b.RecordId == item.R.Id))
        {
            CloudflareConfigStore.RemoveBinding(item.R.Id);
            item.IsBound = false;
            Note = $"已取消 DDNS 绑定：{item.R.Name}";
        }
        else
        {
            CloudflareConfigStore.AddBinding(new DdnsBinding
            {
                ZoneId = z.Id, ZoneName = z.Name, RecordId = item.R.Id, RecordName = item.R.Name,
                Type = item.R.Type, Proxied = item.R.Proxied, Ttl = item.R.Ttl, Enabled = true, LastIp = item.R.Content,
            });
            item.IsBound = true;
            Note = $"已绑定 DDNS：{item.R.Name}（{item.R.Type}）。启动监听后将自动跟随本机公网 IP。";
        }
        _cfg = CloudflareConfigStore.Load();
        LoadBindings();
    }

    private void RemoveBinding(DdnsBindingItem item)
    {
        CloudflareConfigStore.RemoveBinding(item.B.RecordId);
        _cfg = CloudflareConfigStore.Load();
        LoadBindings();
        // Reflect on any matching record card currently shown.
        foreach (var r in Records.Where(r => r.R.Id == item.B.RecordId)) r.IsBound = false;
        Note = $"已移除 DDNS 绑定：{item.B.RecordName}";
    }

    private void LoadBindings()
    {
        Bindings.Clear();
        foreach (var b in _cfg.Bindings)
        {
            var item = new DdnsBindingItem(b);
            var recordId = b.RecordId;
            item.EnabledChanged += () => CloudflareConfigStore.SetBindingEnabled(recordId, item.Enabled);
            Bindings.Add(item);
        }
        OnPropertyChanged(nameof(HasBindings));
        OnPropertyChanged(nameof(NoBindings));
    }

    private void RefreshBindingState()
    {
        var byId = _cfg.Bindings.ToDictionary(b => b.RecordId);
        foreach (var item in Bindings)
            if (byId.TryGetValue(item.B.RecordId, out var fresh))
            {
                item.B.LastIp = fresh.LastIp;
                item.B.LastUpdate = fresh.LastUpdate;
                item.RefreshState();
            }
    }

    public bool HasBindings => Bindings.Count > 0;
    public bool NoBindings => Bindings.Count == 0;

    // ── monitor control (also driven by the system-tray menu) ────────────────────
    private string _intervalText;
    public string IntervalText
    {
        get => _intervalText;
        set
        {
            if (!Set(ref _intervalText, value)) return;
            if (int.TryParse(value, out var n) && n >= 30) CloudflareConfigStore.SetInterval(n);
        }
    }

    private bool _autoStart;
    public bool AutoStart
    {
        get => _autoStart;
        set { if (Set(ref _autoStart, value)) CloudflareConfigStore.SetAutoStart(value); }
    }

    public bool MonitorRunning => _monitor.Running;
    public string StatusText => _monitor.Running ? "监听中" : "已停止";
    public string LastResultText => _monitor.LastResult;
    public string CurrentIpText
    {
        get
        {
            var v4 = _monitor.CurrentIpv4;
            var v6 = _monitor.CurrentIpv6;
            // The last-check time rides right after the IP, e.g. "IPv4 1.2.3.4（最近检查: 21:41:31）".
            var when = _monitor.LastCheck is DateTime t ? $"（最近检查: {t:HH:mm:ss}）" : "";
            if (v4 == null && v6 == null)
                return "本机公网 IP：未知（启动监听或点「立即检查」后显示）" + when;
            var bits = new List<string>();
            if (v4 != null) bits.Add("IPv4 " + v4);
            if (v6 != null) bits.Add("IPv6 " + v6);
            return "本机公网 IP：" + string.Join("   ", bits) + when;
        }
    }

    /// <summary>One-line status for the tray submenu.</summary>
    public string TrayStatusLine => _monitor.Running
        ? "监听中 · " + (_monitor.CurrentIpv4 ?? _monitor.CurrentIpv6 ?? "IP 未知")
        : "已停止";

    public void StartMonitor()
    {
        if (CloudflareConfigStore.LoadToken().Length == 0) { Note = "请先保存并验证 API 令牌"; return; }
        if (!CloudflareConfigStore.Load().Bindings.Any(b => b.Enabled))
        { Note = "没有启用的 DDNS 绑定，请先在解析列表中「绑定 DDNS」"; return; }
        _monitor.Start();
        AuditLog.Action("Cloudflare DDNS 监听已启动");
    }

    public void StopMonitor()
    {
        _monitor.Stop();
        AuditLog.Action("Cloudflare DDNS 监听已停止");
    }

    private async Task RunOnceAsync()
    {
        SetBusy(true);
        Note = "正在检查并更新 …";
        try { Note = await _monitor.RunOnceAsync(); }
        finally { SetBusy(false); }
    }

    /// <summary>Fire-and-forget immediate check from the tray.</summary>
    public void RunOnceFromTray() => _ = _monitor.RunOnceAsync();

    private void OnMonitorChanged()
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null) return;
        disp.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(MonitorRunning));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(LastResultText));
            OnPropertyChanged(nameof(CurrentIpText));
            OnPropertyChanged(nameof(TrayStatusLine));
            _cfg = CloudflareConfigStore.Load();
            RefreshBindingState();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        });
    }

    /// <summary>App is closing — stop the resident monitor.</summary>
    public void Shutdown() => _monitor.Stop();

    /// <summary>Open the themed "required permissions" help dialog.</summary>
    private static void ShowPerms()
        => new Views.CloudflarePermsDialog { Owner = Application.Current.MainWindow }.ShowDialog();

    private static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }
}
