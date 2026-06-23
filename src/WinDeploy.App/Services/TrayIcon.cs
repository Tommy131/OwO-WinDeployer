using System.Drawing;
using WF = System.Windows.Forms;

namespace WinDeploy.App.Services;

/// <summary>A thin wrapper over <see cref="WF.NotifyIcon"/> for background-resident mode: a system-tray
/// icon with a context menu (打开主界面 / 退出) and double-click to restore. Created lazily on first use.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private bool _tipShown;

    public TrayIcon(string tooltip, Action onOpen, Action onExit)
    {
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => onOpen());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => onExit());

        _icon = new WF.NotifyIcon
        {
            Text = tooltip.Length > 63 ? tooltip[..63] : tooltip,   // NotifyIcon.Text caps at 63 chars
            Icon = LoadIcon(),
            Visible = false,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => onOpen();
    }

    private static Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var ico = Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
        }
        catch { /* fall through */ }
        return SystemIcons.Application;
    }

    /// <summary>Show the tray icon; pops a one-time balloon hint the first time.</summary>
    public void Show()
    {
        _icon.Visible = true;
        if (!_tipShown)
        {
            _tipShown = true;
            try
            {
                _icon.BalloonTipTitle = "仍在后台运行";
                _icon.BalloonTipText = "OwO! Win Deployer 已最小化到托盘。双击图标可重新打开，右键可退出。";
                _icon.ShowBalloonTip(3000);
            }
            catch { /* balloons can be suppressed by policy */ }
        }
    }

    public void Hide() => _icon.Visible = false;

    public void Dispose()
    {
        try { _icon.Visible = false; _icon.Dispose(); } catch { /* ignore */ }
    }
}
