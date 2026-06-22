using System.Text.Json;
using WinDeploy.Core.Util;

namespace WinDeploy.App.Services;

public sealed record DiskInfo(string Drive, long SizeBytes, long FreeBytes, string? Label);
public sealed record PhysDiskInfo(string Name, string? Media, string? Health, long SizeGb);

/// <summary>A one-shot snapshot of the machine's health: OS, CPU, RAM, drives (+ SMART), battery, activation.</summary>
public sealed class SystemSnapshot
{
    public string? OsCaption { get; set; }
    public string? OsVersion { get; set; }
    public string? Arch { get; set; }
    public string? CpuName { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? User { get; set; }
    public int CpuLoad { get; set; }
    public int Cores { get; set; }
    public int Threads { get; set; }
    public long TotalMemKb { get; set; }
    public long FreeMemKb { get; set; }
    public double UptimeHours { get; set; }
    public int? BatteryCharge { get; set; }
    public int? Activation { get; set; }   // 1 = licensed
    public List<DiskInfo> Disks { get; } = new();
    public List<PhysDiskInfo> PhysicalDisks { get; } = new();
}

/// <summary>Reads machine health via CIM/WMI (through PowerShell, so the App needs no extra dependency).
/// Read-only. Used by the 系统概览 page.</summary>
public static class SystemInfo
{
    private const string Ps = """
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $os = Get-CimInstance Win32_OperatingSystem
        $cs = Get-CimInstance Win32_ComputerSystem
        $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
        $disks = @(Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" | Select-Object DeviceID,Size,FreeSpace,VolumeName)
        $bat = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
        $phys = @()
        try { $phys = @(Get-PhysicalDisk -ErrorAction Stop | Select-Object FriendlyName,MediaType,HealthStatus,@{n='SizeGB';e={[math]::Round($_.Size/1GB,0)}}) } catch {}
        $act = $null
        try { $act = (Get-CimInstance SoftwareLicensingProduct -Filter "ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL" -ErrorAction Stop | Select-Object -First 1).LicenseStatus } catch {}
        $uptime = [math]::Round(((Get-Date) - $os.LastBootUpTime).TotalHours, 1)
        [pscustomobject]@{
          OsCaption=$os.Caption; OsVersion=$os.Version; Arch=$os.OSArchitecture;
          FreeMemKb=$os.FreePhysicalMemory; TotalMemKb=$os.TotalVisibleMemorySize; UptimeHours=$uptime;
          Manufacturer=$cs.Manufacturer; Model=$cs.Model; User=$cs.UserName;
          CpuName=$cpu.Name; CpuLoad=$cpu.LoadPercentage; Cores=$cpu.NumberOfCores; Threads=$cpu.NumberOfLogicalProcessors;
          Disks=$disks; BatteryCharge=$(if($bat){$bat.EstimatedChargeRemaining}else{$null});
          PhysicalDisks=$phys; Activation=$act
        } | ConvertTo-Json -Depth 4 -Compress
        """;

    public static async Task<SystemSnapshot> GetAsync(CancellationToken ct = default)
    {
        var snap = new SystemSnapshot();
        ProcResult r;
        try { r = await Proc.RunAsync("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", Ps }, ct: ct); }
        catch { return snap; }
        if (string.IsNullOrWhiteSpace(r.StdOut)) return snap;

        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            var e = doc.RootElement;
            snap.OsCaption = Str(e, "OsCaption");
            snap.OsVersion = Str(e, "OsVersion");
            snap.Arch = Str(e, "Arch");
            snap.CpuName = Str(e, "CpuName")?.Trim();
            snap.Manufacturer = Str(e, "Manufacturer");
            snap.Model = Str(e, "Model");
            snap.User = Str(e, "User");
            snap.CpuLoad = (int)Num(e, "CpuLoad");
            snap.Cores = (int)Num(e, "Cores");
            snap.Threads = (int)Num(e, "Threads");
            snap.TotalMemKb = Num(e, "TotalMemKb");
            snap.FreeMemKb = Num(e, "FreeMemKb");
            snap.UptimeHours = Dbl(e, "UptimeHours");
            if (e.TryGetProperty("BatteryCharge", out var bc) && bc.ValueKind == JsonValueKind.Number) snap.BatteryCharge = bc.GetInt32();
            if (e.TryGetProperty("Activation", out var ac) && ac.ValueKind == JsonValueKind.Number) snap.Activation = ac.GetInt32();

            foreach (var d in Items(e, "Disks"))
                snap.Disks.Add(new DiskInfo(Str(d, "DeviceID") ?? "?", Num(d, "Size"), Num(d, "FreeSpace"), Str(d, "VolumeName")));
            foreach (var p in Items(e, "PhysicalDisks"))
                snap.PhysicalDisks.Add(new PhysDiskInfo(Str(p, "FriendlyName") ?? "?", Str(p, "MediaType"), Str(p, "HealthStatus"), Num(p, "SizeGB")));
        }
        catch { /* partial / unparseable */ }
        return snap;
    }

    private static IEnumerable<JsonElement> Items(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) yield break;
        if (v.ValueKind == JsonValueKind.Array) { foreach (var x in v.EnumerateArray()) yield return x; }
        else if (v.ValueKind == JsonValueKind.Object) yield return v;
    }

    private static string? Str(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long Num(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
    private static double Dbl(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : 0;
}
