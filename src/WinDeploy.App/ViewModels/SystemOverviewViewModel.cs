using System.Collections.ObjectModel;
using System.Security.Principal;
using System.Windows.Threading;
using WinDeploy.App.Services;
using WinDeploy.Core.Engine;

namespace WinDeploy.App.ViewModels;

public sealed class DiskRowViewModel
{
    public string Drive { get; init; } = "";
    public string Label { get; init; } = "";
    public string Text { get; init; } = "";
    public double UsedPercent { get; init; }
    public bool Low { get; init; }   // < 10% free
}

public sealed class PhysDiskRowViewModel
{
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Health { get; init; } = "";
    public bool Healthy { get; init; }
    public string? DeviceId { get; init; }
}

public sealed class PowerRowViewModel
{
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public double Watts { get; init; }
    public string WattsText { get; init; } = "";
    public double BarPercent { get; init; }
}

/// <summary>The "系统概览" page: a one-glance health board — OS, CPU, RAM, drives (+ SMART), battery,
/// Windows activation — plus live CPU/memory/power telemetry and a one-click software-inventory export.
/// CPU/memory/power refresh on a 2-second timer while the page is visible (see <see cref="StartLive"/>).</summary>
public sealed class SystemOverviewViewModel : ObservableObject
{
    public ObservableCollection<DiskRowViewModel> Disks { get; } = new();
    public ObservableCollection<PhysDiskRowViewModel> PhysicalDisks { get; } = new();
    public ObservableCollection<PowerRowViewModel> Powers { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ExportInventoryCommand { get; }
    public RelayCommand ShowSmartCommand { get; }

    private DispatcherTimer? _timer;
    private bool _sampling;
    private long _totalMemBytes;

    public SystemOverviewViewModel()
    {
        RefreshCommand = new RelayCommand(_ => _ = LoadAsync());
        ExportInventoryCommand = new RelayCommand(_ => _ = ExportInventoryAsync());
        ShowSmartCommand = new RelayCommand(p =>
        {
            if (p is PhysDiskRowViewModel r)
                new Views.SmartDialog(r.Name, r.DeviceId) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
        });
        _ = LoadAsync();
    }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; set { if (Set(ref _isLoading, value)) OnPropertyChanged(nameof(IsReady)); } }
    public bool IsReady => !_isLoading;

    private string _os = "", _cpu = "", _ram = "", _machine = "", _uptime = "", _activation = "", _battery = "";
    public string Os { get => _os; set => Set(ref _os, value); }
    public string Cpu { get => _cpu; set => Set(ref _cpu, value); }
    public string Ram { get => _ram; set => Set(ref _ram, value); }
    public string Machine { get => _machine; set => Set(ref _machine, value); }
    public string Uptime { get => _uptime; set => Set(ref _uptime, value); }
    public string Activation { get => _activation; set => Set(ref _activation, value); }
    public string Battery { get => _battery; set => Set(ref _battery, value); }

    // Live telemetry
    private string _cpuLive = "负载 —", _ramLive = "—";
    public string CpuLive { get => _cpuLive; set => Set(ref _cpuLive, value); }
    public string RamLive { get => _ramLive; set => Set(ref _ramLive, value); }

    private string _totalPowerText = "—";
    public string TotalPowerText { get => _totalPowerText; set => Set(ref _totalPowerText, value); }

    private bool _hasPower;
    public bool HasPower { get => _hasPower; set { if (Set(ref _hasPower, value)) OnPropertyChanged(nameof(NoPower)); } }
    public bool NoPower => !_hasPower;

    private string _powerNote = "正在读取功耗传感器 …";
    public string PowerNote { get => _powerNote; set => Set(ref _powerNote, value); }

    private string _inventoryNote = "";
    public string InventoryNote { get => _inventoryNote; set => Set(ref _inventoryNote, value); }

    // ── live timer ──────────────────────────────────────────────────────────
    public void StartLive()
    {
        HardwareMonitor.TryInit();
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        Tick();
        _timer.Start();
    }

    public void StopLive() => _timer?.Stop();

    private void OnTick(object? sender, EventArgs e) => Tick();

    private async void Tick()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var hw = await Task.Run(HardwareMonitor.Sample);
            ApplyLive(hw);
        }
        catch { /* sensor read can throw transiently */ }
        finally { _sampling = false; }
    }

    private void ApplyLive(HwSample hw)
    {
        // CPU
        if (hw.CpuLoad is double load)
            CpuLive = hw.CpuTemp is double tc ? $"负载 {load:0}%  ·  {tc:0} °C" : $"负载 {load:0}%";

        // Memory
        if (hw.MemUsedGb is double used && hw.MemAvailGb is double avail && used + avail > 0)
        {
            var total = used + avail;
            RamLive = $"{used:0.0} / {total:0.0} GB  ·  {used / total * 100:0}%";
        }

        // Power — one curated row per device kind (CPU / GPU / 硬盘 / 内存 / 其他)
        Powers.Clear();
        double sum = 0;
        var order = new[] { "CPU", "GPU", "硬盘", "内存", "其他" };
        foreach (var grp in hw.Powers.GroupBy(p => p.Kind).OrderBy(g => Array.IndexOf(order, g.Key) is var i && i >= 0 ? i : 99))
        {
            // prefer the package/total reading; otherwise the largest draw in that group
            var primary = grp.FirstOrDefault(p => p.Name.Contains("Package") || p.Name.Contains("Total"))
                          ?? grp.OrderByDescending(p => p.Watts).First();
            sum += primary.Watts;
            Powers.Add(new PowerRowViewModel
            {
                Kind = grp.Key,
                Name = primary.Name,
                Watts = primary.Watts,
                WattsText = $"{primary.Watts:0.0} W",
                BarPercent = 0, // filled below once total is known
            });
        }

        HasPower = Powers.Count > 0;
        if (HasPower)
        {
            // scale each bar against the largest single draw for a quick visual comparison
            var max = Powers.Max(p => p.Watts);
            var scaled = Powers.Select(p => new PowerRowViewModel
            {
                Kind = p.Kind, Name = p.Name, Watts = p.Watts, WattsText = p.WattsText,
                BarPercent = max > 0 ? p.Watts / max * 100 : 0,
            }).ToList();
            Powers.Clear();
            foreach (var p in scaled) Powers.Add(p);

            TotalPowerText = $"{sum:0.0} W";
            PowerNote = "整机功耗为各部件功耗之和的估算值（不含主板 / 风扇等无传感器部件）。";
        }
        else
        {
            TotalPowerText = "不可用";
            PowerNote = HardwareMonitor.Available
                ? (IsElevated()
                    ? "未读取到功耗传感器。部分主板 / 显卡 / 笔记本不提供功耗传感器。"
                    : "未读取到功耗传感器。CPU 封装功耗需以管理员身份运行本程序才能读取。")
                : "硬件监控驱动未能加载（功耗 / 温度不可用）。";
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        var s = await SystemInfo.GetAsync();

        Os = $"{s.OsCaption} · {s.Arch} · {s.OsVersion}";
        Cpu = $"{s.CpuName}  ·  {s.Cores} 核 {s.Threads} 线程";
        _totalMemBytes = s.TotalMemKb * 1024;
        var usedKb = Math.Max(0, s.TotalMemKb - s.FreeMemKb);
        var memPct = s.TotalMemKb > 0 ? usedKb * 100.0 / s.TotalMemKb : 0;
        Ram = $"{Gb(usedKb * 1024)} / {Gb(s.TotalMemKb * 1024)}  ·  {memPct:0}%";
        RamLive = Ram;
        if (s.CpuLoad > 0) CpuLive = $"负载 {s.CpuLoad}%";
        Machine = $"{s.Manufacturer} {s.Model}".Trim() + (string.IsNullOrWhiteSpace(s.User) ? "" : $"  ·  {s.User}");
        Uptime = s.UptimeHours >= 24 ? $"已运行 {(int)(s.UptimeHours / 24)} 天 {(int)(s.UptimeHours % 24)} 小时" : $"已运行 {s.UptimeHours:0.0} 小时";
        Activation = s.Activation switch { 1 => "Windows 已激活", null => "激活状态未知", _ => "Windows 未激活" };
        Battery = s.BatteryCharge is int c ? $"电池 {c}%" : "无电池（台式机 / 未检测）";

        Disks.Clear();
        foreach (var d in s.Disks)
        {
            var used = Math.Max(0, d.SizeBytes - d.FreeBytes);
            var pct = d.SizeBytes > 0 ? used * 100.0 / d.SizeBytes : 0;
            var freePct = d.SizeBytes > 0 ? d.FreeBytes * 100.0 / d.SizeBytes : 100;
            Disks.Add(new DiskRowViewModel
            {
                Drive = d.Drive,
                Label = string.IsNullOrWhiteSpace(d.Label) ? "" : d.Label,
                Text = $"{Gb(d.FreeBytes)} 可用 / {Gb(d.SizeBytes)}",
                UsedPercent = pct,
                Low = freePct < 10,
            });
        }

        PhysicalDisks.Clear();
        foreach (var p in s.PhysicalDisks)
            PhysicalDisks.Add(new PhysDiskRowViewModel
            {
                Name = p.Name,
                Detail = $"{p.Media} · {p.SizeGb} GB",
                Health = string.IsNullOrWhiteSpace(p.Health) ? "未知" : p.Health,
                Healthy = string.Equals(p.Health, "Healthy", StringComparison.OrdinalIgnoreCase),
                DeviceId = p.DeviceId,
            });

        IsLoading = false;
    }

    private async Task ExportInventoryAsync()
    {
        InventoryNote = "正在读取已装软件清单 …";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出软件清单",
            FileName = $"软件清单-{Environment.MachineName}.html",
            Filter = "HTML 网页 (*.html)|*.html|CSV 表格 (*.csv)|*.csv|JSON (*.json)|*.json",
        };
        if (dlg.ShowDialog() != true) { InventoryNote = ""; return; }

        var items = await Inventory.ListAsync();
        var ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
        var text = ext switch { ".csv" => Inventory.ToCsv(items), ".json" => Inventory.ToJson(items), _ => Inventory.ToHtml(items) };
        try
        {
            await System.IO.File.WriteAllTextAsync(dlg.FileName, text);
            AuditLog.Action($"导出软件清单：{items.Count} 项 → {dlg.FileName}");
            InventoryNote = $"已导出 {items.Count} 项 → {dlg.FileName}";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (Exception ex) { InventoryNote = "导出失败：" + ex.Message; }
    }

    private static string Gb(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GB" : $"{bytes / 1024.0 / 1024:0.0} MB";
}
