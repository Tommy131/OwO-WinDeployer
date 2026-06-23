using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.App.Services;

namespace WinDeploy.App.Views;

/// <summary>Themed dialog to create or edit a Cloudflare DNS record. Exposes the chosen type / name / content /
/// proxied / TTL; <see cref="FullName"/> resolves the entered subdomain (or @) against the zone. The
/// 「填入本机 IP」buttons fetch the current public IP so binding a device is one click.</summary>
public sealed class CloudflareRecordDialog : Window
{
    private readonly ComboBox _type;
    private readonly TextBox _name;
    private readonly TextBox _content;
    private readonly CheckBox _proxied;
    private readonly ComboBox _ttl;
    private readonly string _zoneName;

    public string RecordType => (_type.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "A";
    public string RecordContent => _content.Text.Trim();
    public bool Proxied => _proxied.IsChecked == true;
    public int Ttl => (_ttl.SelectedItem as ComboBoxItem)?.Tag is string s && int.TryParse(s, out var v) ? v : 1;

    private static readonly (string Label, int Seconds)[] TtlChoices =
    {
        ("自动", 1), ("1 分钟", 60), ("2 分钟", 120), ("5 分钟", 300),
        ("10 分钟", 600), ("30 分钟", 1800), ("1 小时", 3600),
    };

    /// <summary>Create-mode: optionally pre-fill the content with the device's current public IPv4.</summary>
    public CloudflareRecordDialog(string zoneName, string? prefillIp = null)
        : this(zoneName, record: null, prefillIp) { }

    /// <summary>Edit-mode: pre-fill all fields from an existing record.</summary>
    public CloudflareRecordDialog(string zoneName, CfDnsRecord record)
        : this(zoneName, record, prefillIp: null) { }

    private CloudflareRecordDialog(string zoneName, CfDnsRecord? record, string? prefillIp)
    {
        _zoneName = zoneName;
        var editing = record != null;
        Title = editing ? "编辑解析记录" : "新建解析记录";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        root.Children.Add(new TextBlock
        {
            Text = editing ? $"修改 {zoneName} 的解析记录" : $"在 {zoneName} 下新建解析记录",
            FontSize = 13, Foreground = Brush("TextSecondary"), Margin = new Thickness(0, 0, 0, 14),
            TextWrapping = TextWrapping.Wrap,
        });

        // 类型
        _type = new ComboBox { Height = 32 };
        foreach (var t in new[] { "A", "AAAA", "CNAME", "TXT" })
            _type.Items.Add(new ComboBoxItem { Content = t });
        _type.SelectedIndex = 0;
        root.Children.Add(Labeled("记录类型", _type));

        // 名称
        _name = Input();
        root.Children.Add(Labeled($"名称（子域，根域填 @；将解析为 *.{zoneName}）", _name));

        // 内容 + 填入本机 IP
        _content = Input();
        var ipRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        ipRow.Children.Add(MiniBtn("填入本机 IPv4", () => _ = FillIpAsync(ipv6: false)));
        ipRow.Children.Add(MiniBtn("填入本机 IPv6", () => _ = FillIpAsync(ipv6: true), leftMargin: 8));
        var contentPanel = new StackPanel();
        contentPanel.Children.Add(_content);
        contentPanel.Children.Add(ipRow);
        root.Children.Add(Labeled("内容（A/AAAA 填 IP；CNAME 填目标域名；TXT 填文本）", contentPanel));

        // TTL
        _ttl = new ComboBox { Height = 32 };
        foreach (var (label, seconds) in TtlChoices)
            _ttl.Items.Add(new ComboBoxItem { Content = label, Tag = seconds.ToString() });
        _ttl.SelectedIndex = 0;
        root.Children.Add(Labeled("TTL（开启 Cloudflare 代理时强制为「自动」）", _ttl));

        // 代理
        _proxied = new CheckBox
        {
            Content = "通过 Cloudflare 代理（橙云；隐藏源站 IP）",
            Margin = new Thickness(0, 14, 0, 0), FontSize = 13,
        };
        root.Children.Add(_proxied);

        // Prefill
        if (record != null)
        {
            SelectType(record.Type);
            _name.Text = ToSubdomain(record.Name, zoneName);
            _content.Text = record.Content;
            _proxied.IsChecked = record.Proxied;
            SelectTtl(record.Ttl);
        }
        else if (!string.IsNullOrEmpty(prefillIp))
        {
            _content.Text = prefillIp;
        }

        // Buttons
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        var ok = new Button { Content = editing ? "保存" : "创建", MinWidth = 84, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "取消", MinWidth = 72, IsCancel = true };
        if (Application.Current.TryFindResource("PrimaryButton") is Style okS) ok.Style = okS;
        if (Application.Current.TryFindResource("MiniButton") is Style caS) cancel.Style = caS;
        ok.Click += (_, _) =>
        {
            if (RecordContent.Length == 0) { MessageBox.Show("请填写内容（IP / 目标）。", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            DialogResult = true;
        };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => { _name.Focus(); };
        SourceInitialized += (_, _) => Services.ThemeManager.ApplyTitleBar(this);
    }

    /// <summary>Resolve the entered name to a full record name under the zone.</summary>
    public string FullName()
    {
        var n = _name.Text.Trim();
        if (n.Length == 0 || n == "@") return _zoneName;
        if (n.Equals(_zoneName, StringComparison.OrdinalIgnoreCase)) return _zoneName;
        if (n.EndsWith("." + _zoneName, StringComparison.OrdinalIgnoreCase)) return n;
        return $"{n}.{_zoneName}";
    }

    private async Task FillIpAsync(bool ipv6)
    {
        _content.Text = "获取中 …";
        var ip = await PublicIp.GetAsync(ipv6);
        _content.Text = ip ?? "";
        if (ip == null)
            MessageBox.Show($"未能获取本机公网 {(ipv6 ? "IPv6" : "IPv4")} 地址。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        else if (!ipv6) SelectType("A");
        else SelectType("AAAA");
    }

    private static string ToSubdomain(string fullName, string zone)
    {
        if (fullName.Equals(zone, StringComparison.OrdinalIgnoreCase)) return "@";
        if (fullName.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
            return fullName[..^(zone.Length + 1)];
        return fullName;
    }

    private void SelectType(string type)
    {
        foreach (ComboBoxItem it in _type.Items)
            if (string.Equals(it.Content?.ToString(), type, StringComparison.OrdinalIgnoreCase)) { _type.SelectedItem = it; return; }
    }

    private void SelectTtl(int seconds)
    {
        foreach (ComboBoxItem it in _ttl.Items)
            if (it.Tag is string s && int.TryParse(s, out var v) && v == seconds) { _ttl.SelectedItem = it; return; }
        _ttl.SelectedIndex = 0;   // unlisted TTL → 自动
    }

    private static TextBox Input() => new()
    {
        FontSize = 13.5, Height = 32, Padding = new Thickness(8, 5, 8, 5),
        Background = Brush("CardBg"), Foreground = Brush("TextPrimary"), BorderBrush = Brush("BorderStrong"),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static StackPanel Labeled(string label, UIElement field)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        p.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brush("TextTertiary"), Margin = new Thickness(0, 0, 0, 4) });
        p.Children.Add(field);
        return p;
    }

    private Button MiniBtn(string text, Action onClick, double leftMargin = 0)
    {
        var b = new Button { Content = text, Margin = new Thickness(leftMargin, 0, 0, 0) };
        if (Application.Current.TryFindResource("MiniButton") is Style s) b.Style = s;
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
