using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using WinDeploy.App.Services.Clip;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.ViewModels.Clip;

/// <summary>One row in the shared board: a text or image entry with its source device + time, a small
/// thumbnail for images, and per-row 复制到本机 / 删除 actions. Plain <see cref="ObservableObject"/> (not
/// localized) — the parent rebuilds rows on a language switch, avoiding per-row event subscriptions.</summary>
public sealed class ClipEntryRowVm : ObservableObject
{
    private readonly ClipEntry _entry;
    private BitmapImage? _image;

    public ClipEntryRowVm(ClipEntry entry, Action<ClipEntry> copy, Action<ClipEntry> delete)
    {
        _entry = entry;
        CopyCommand = new RelayCommand(_ => copy(_entry));
        DeleteCommand = new RelayCommand(_ => delete(_entry));
    }

    public string Id => _entry.Id;
    public bool IsImage => _entry.Kind == ClipKind.Image;
    public bool IsText => _entry.Kind == ClipKind.Text;
    public string OriginName => string.IsNullOrWhiteSpace(_entry.OriginName) ? "—" : _entry.OriginName;
    public string TimeText => _entry.CreatedAt.ToString("MM-dd HH:mm:ss");
    public string TypeText => IsImage ? Localizer.T("clip.kind.image") : Localizer.T("clip.kind.text");

    /// <summary>One-line list preview: trimmed text, or image dimensions/size.</summary>
    public string Preview => IsImage
        ? Localizer.Format("clip.board.imageMeta", _entry.ImageW, _entry.ImageH, (_entry.Image?.Length ?? 0) / 1024)
        : OneLine(_entry.Text ?? "", 120);

    /// <summary>Full text for the preview pane (text entries).</summary>
    public string PreviewText => _entry.Text ?? "";

    /// <summary>Decoded bitmap for the thumbnail / preview pane (image entries), lazily built once.</summary>
    public BitmapImage? Image => _image ??= Decode(_entry.Image);

    public RelayCommand CopyCommand { get; }
    public RelayCommand DeleteCommand { get; }

    private static string OneLine(string s, int max)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static BitmapImage? Decode(byte[]? png)
    {
        if (png == null || png.Length == 0) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(png);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}

/// <summary>共享剪贴板 tab: the replicated board (newest first), a preview pane for the selected entry, manual
/// text add, and central management (复制到本机 / 删除-同步 / 清空本机视图).</summary>
public sealed class ClipBoardViewModel : LocalizedObject
{
    private readonly ClipSyncManager _manager;

    public ClipBoardViewModel(ClipSyncManager manager)
    {
        _manager = manager;
        AddCommand = new RelayCommand(_ => Add(), _ => !string.IsNullOrWhiteSpace(NewText));
        ClearCommand = new RelayCommand(_ => Clear(), _ => Entries.Count > 0);
    }

    public ObservableCollection<ClipEntryRowVm> Entries { get; } = new();

    private ClipEntryRowVm? _selected;
    public ClipEntryRowVm? Selected { get => _selected; set { if (Set(ref _selected, value)) OnPropertyChanged(nameof(HasSelection)); } }
    public bool HasSelection => _selected != null;

    private string _newText = "";
    public string NewText { get => _newText; set => Set(ref _newText, value); }

    public bool NoEntries => Entries.Count == 0;
    public string CountText => Localizer.Format("clip.board.count", Entries.Count);

    public RelayCommand AddCommand { get; }
    public RelayCommand ClearCommand { get; }

    /// <summary>Rebuild the board list from the manager, preserving the current selection by id.</summary>
    public void Reload()
    {
        var keepId = _selected?.Id;
        Entries.Clear();
        foreach (var e in _manager.Board) Entries.Add(new ClipEntryRowVm(e, CopyToLocal, Delete));
        Selected = keepId != null ? Entries.FirstOrDefault(r => r.Id == keepId) : null;
        OnPropertyChanged(nameof(NoEntries));
        OnPropertyChanged(nameof(CountText));
    }

    private void Add()
    {
        var text = NewText.Trim();
        if (text.Length == 0) return;
        _manager.AddText(text);
        NewText = "";
    }

    private void CopyToLocal(ClipEntry entry)
    {
        _manager.CopyToLocal(entry);
        ToastService.TryShow(Localizer.T("clip.page.title"), Localizer.T("clip.board.copied"));
    }

    private void Delete(ClipEntry entry)
    {
        _manager.Delete(entry.Id);
        AuditLog.Action($"剪贴板共享：删除一条并同步（来源 {entry.OriginName}）");
    }

    private void Clear()
    {
        if (Dialogs.Show(Localizer.T("clip.board.clearConfirm"), Localizer.T("clip.board.clear"),
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes) return;
        _manager.ClearLocal();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        Reload();   // row preview text is localized — rebuild so it follows the language
    }
}
