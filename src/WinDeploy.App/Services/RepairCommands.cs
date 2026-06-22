using System.ComponentModel;
using System.Diagnostics;

namespace WinDeploy.App.Services;

/// <summary>One built-in Windows repair/maintenance command. Most require elevation; they run in their own
/// console window (cmd /k) so the user sees live output and the window stays open with the result.</summary>
public sealed record RepairAction(string Id, string Title, string Detail, string Command, bool Elevate, bool Risky = false);

/// <summary>The repair technician's toolbox: SFC / DISM / chkdsk / network reset / Windows Update cache /
/// icon cache rebuild — the commands every Windows repair starts with, one click each.</summary>
public static class RepairCommands
{
    public static readonly IReadOnlyList<RepairAction> All = new[]
    {
        new RepairAction("sfc", "系统文件检查 (SFC)",
            "扫描并修复受损的系统文件：sfc /scannow", "sfc /scannow", true),
        new RepairAction("dism", "DISM 修复系统映像",
            "修复 Windows 组件存储：DISM /Online /Cleanup-Image /RestoreHealth", "DISM /Online /Cleanup-Image /RestoreHealth", true),
        new RepairAction("dism-clean", "DISM 清理组件存储",
            "清理旧版组件、缩减 WinSxS：DISM /Online /Cleanup-Image /StartComponentCleanup", "DISM /Online /Cleanup-Image /StartComponentCleanup", true),
        new RepairAction("chkdsk", "磁盘检查 (chkdsk C:)",
            "只读检查系统盘错误（加 /f 修复需重启）：chkdsk C:", "chkdsk C:", true),
        new RepairAction("net-reset", "重置网络栈",
            "刷新 DNS + 重置 Winsock + 重置 TCP/IP（重置后建议重启）", "ipconfig /flushdns & netsh winsock reset & netsh int ip reset", true),
        new RepairAction("flushdns", "刷新 DNS 缓存",
            "ipconfig /flushdns（无需管理员）", "ipconfig /flushdns", false),
        new RepairAction("wu-cache", "清理 Windows Update 缓存",
            "停止服务→清空 SoftwareDistribution\\Download→重启服务",
            "net stop wuauserv & net stop bits & rd /s /q %windir%\\SoftwareDistribution\\Download & net start wuauserv & net start bits", true),
        new RepairAction("icon-cache", "重建图标缓存",
            "删除图标缓存并重启资源管理器（桌面会闪烁一次）",
            "ie4uinit.exe -show & taskkill /f /im explorer.exe & del /a /q \"%localappdata%\\IconCache.db\" & del /a /q \"%localappdata%\\Microsoft\\Windows\\Explorer\\iconcache*\" & start explorer.exe", true, Risky: true),
        new RepairAction("gpupdate", "刷新组策略",
            "gpupdate /force", "gpupdate /force", true),
    };

    /// <summary>Launch an action in its own (optionally elevated) cmd window. Returns a friendly status.</summary>
    public static (bool Ok, string Msg) Run(RepairAction a)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                // /k keeps the window open after the command finishes so the user can read the result.
                Arguments = $"/k \"{a.Command} & echo. & echo ===== 完成，可关闭此窗口 =====\"",
                UseShellExecute = true,
            };
            if (a.Elevate) psi.Verb = "runas";
            Process.Start(psi);
            AuditLog.Action($"系统维护：{a.Title}");
            return (true, "已在新窗口中执行");
        }
        catch (Win32Exception) { return (false, "已取消管理员授权（UAC）或启动失败"); }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
