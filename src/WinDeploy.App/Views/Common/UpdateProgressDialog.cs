using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Common;

/// <summary>Minimal themed download-progress window for self-update (non-modal — Show(), not ShowDialog(), so
/// there's no nested-dispatcher subtlety around Progress&lt;T&gt; callbacks arriving while it's open). Cancel
/// signals the passed CancellationTokenSource; the caller awaits its own download task and closes this window
/// in a finally block regardless of outcome.</summary>
public sealed class UpdateProgressDialog : Window
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _status;
    private readonly CancellationTokenSource _cts;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public UpdateProgressDialog(string title, CancellationTokenSource cts)
    {
        _cts = cts;
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        var root = new StackPanel { Margin = new Thickness(18) };
        _status = new TextBlock
        {
            Text = Localizer.T("update.auto.preparing"), FontSize = 13, Foreground = Brush("TextSecondary"),
            Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap,
        };
        root.Children.Add(_status);

        _bar = new ProgressBar { Height = 8, Minimum = 0, Maximum = 100, Value = 0 };
        if (Application.Current.TryFindResource("Accent") is Brush accent) _bar.Foreground = accent;
        root.Children.Add(_bar);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = Localizer.T("common.cancel"), MinWidth = 80 };
        if (Application.Current.TryFindResource("MiniButton") is Style caS) cancel.Style = caS;
        cancel.Click += (_, _) => { try { _cts.Cancel(); } catch { /* already disposed */ } };
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    /// <summary>Called via Progress&lt;T&gt; — always marshaled to the UI thread by its captured context.</summary>
    public void Report(long done, long total)
    {
        if (total > 0) _bar.Value = Math.Min(100, done * 100.0 / total);
        var secs = _sw.Elapsed.TotalSeconds;
        var rate = secs > 0.2 ? done / secs : 0;
        var rateText = rate > 0 ? " · " + Mb((long)rate) + "/s" : "";
        var etaText = rate > 0 && total > done ? FormatEta((total - done) / rate) : "…";
        var pct = total > 0 ? (done * 100.0 / total).ToString("0") : "0";
        _status.Text = Localizer.Format("engine.download.progress", Mb(done), Mb(total), pct, rateText, etaText);
    }

    private static string FormatEta(double seconds) => seconds >= 60
        ? Localizer.Format("engine.download.minSec", (int)(seconds / 60), (int)(seconds % 60))
        : Localizer.Format("engine.download.sec", ((int)seconds).ToString());

    private static string Mb(long b) => b >= 1024L * 1024 * 1024 ? $"{b / 1024.0 / 1024 / 1024:0.0} GB" : $"{b / 1024.0 / 1024:0.0} MB";

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
