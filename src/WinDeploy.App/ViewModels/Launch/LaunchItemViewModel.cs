using System.Windows.Media;
using WinDeploy.App.Services.Launch;
using WinDeploy.App.Services.Software;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.ViewModels.Launch;

/// <summary>One row in the 快速启动 list. Wraps a <see cref="LaunchItem"/> and surfaces localized labels;
/// toggling <see cref="Done"/> raises <see cref="Changed"/> so the parent persists the change.</summary>
public sealed class LaunchItemViewModel : ObservableObject
{
    public LaunchItem Model { get; }

    /// <summary>Raised when a user-editable, persisted field changes on this row (currently the Done flag).</summary>
    public event Action? Changed;

    public LaunchItemViewModel(LaunchItem model) => Model = model;

    public string Title => string.IsNullOrWhiteSpace(Model.Title) ? Model.Target : Model.Title;

    /// <summary>Second line: the target, with arguments appended when present.</summary>
    public string Subtitle => string.IsNullOrWhiteSpace(Model.Args) ? Model.Target : $"{Model.Target}  {Model.Args}";

    public string KindLabel => Model.Kind switch
    {
        LaunchKind.Url => Localizer.T("launcher.kind.url"),
        LaunchKind.Command => Localizer.T("launcher.kind.command"),
        _ => Localizer.T("launcher.kind.app"),
    };

    public string KindGlyph => Model.Kind switch
    {
        LaunchKind.Url => "🌐",
        LaunchKind.Command => "⌨",
        _ => "🚀",
    };

    // ── real application icon (App-kind items only) ──────────────────────────────
    private ImageSource? _icon;
    private bool _iconResolved;

    /// <summary>The target application's real icon (extracted from its .exe / .lnk), or null. Only resolved for
    /// <see cref="LaunchKind.App"/> items; URLs and commands fall back to <see cref="KindGlyph"/>.</summary>
    public ImageSource? Icon { get { EnsureIcon(); return _icon; } }

    /// <summary>True when a real icon was resolved — the view shows the image; otherwise it shows the glyph.</summary>
    public bool HasIcon { get { EnsureIcon(); return _icon != null; } }
    public bool ShowGlyph => !HasIcon;

    private void EnsureIcon()
    {
        if (_iconResolved) return;
        _iconResolved = true;
        if (Model.Kind != LaunchKind.App) return;
        try
        {
            var target = Environment.ExpandEnvironmentVariables(Model.Target ?? "").Trim();
            _icon = IconExtractor.FromExeAnyIcon(target);
        }
        catch { /* fall back to the glyph */ }
    }

    public bool RunAsAdmin => Model.RunAsAdmin;

    public bool Done
    {
        get => Model.Done;
        set
        {
            if (Model.Done == value) return;
            Model.Done = value;
            OnPropertyChanged();
            Changed?.Invoke();
        }
    }

    /// <summary>Re-read every binding after an edit (title/target/kind may all have changed) or a language switch.
    /// Resets the icon cache so a changed target re-resolves its application icon.</summary>
    public void Refresh()
    {
        _iconResolved = false;
        _icon = null;
        RaiseAllPropertiesChanged();
    }
}
