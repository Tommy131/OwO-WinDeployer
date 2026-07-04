namespace OwOWinDeployer.App.Services.Launch;

/// <summary>What a launch item points at, deciding how <see cref="LaunchRunner"/> opens it.</summary>
public enum LaunchKind
{
    /// <summary>An application, file, or shortcut (.lnk) — opened via ShellExecute (default program / verb).</summary>
    App,
    /// <summary>A website — opened in the default browser.</summary>
    Url,
    /// <summary>A shell command line — run through <c>cmd.exe /c</c>.</summary>
    Command,
}

/// <summary>One entry in the 快速启动 list: a clickable target the user curates like a to-do item.
/// Persisted as-is to launcher.json — keep it a plain serializable record of primitives.</summary>
public sealed class LaunchItem
{
    /// <summary>Stable identity (GUID string) so reorders / edits don't lose per-item state.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>User-facing name shown in the list.</summary>
    public string Title { get; set; } = "";

    /// <summary>Path / URL / command line, interpreted per <see cref="Kind"/>.</summary>
    public string Target { get; set; } = "";

    /// <summary>Optional arguments passed to the app / command (ignored for URLs).</summary>
    public string? Args { get; set; }

    /// <summary>Optional working directory to launch from.</summary>
    public string? WorkingDir { get; set; }

    public LaunchKind Kind { get; set; } = LaunchKind.App;

    /// <summary>Launch elevated (ShellExecute verb "runas"); triggers a UAC prompt.</summary>
    public bool RunAsAdmin { get; set; }

    /// <summary>To-do "done" checkbox state (visual only — a done item is still launchable).</summary>
    public bool Done { get; set; }

    /// <summary>A detached copy (same Id/Done), so an editor can mutate a working copy and only commit on OK.</summary>
    public LaunchItem Clone() => new()
    {
        Id = Id, Title = Title, Target = Target, Args = Args,
        WorkingDir = WorkingDir, Kind = Kind, RunAsAdmin = RunAsAdmin, Done = Done,
    };

    /// <summary>Copy the editable fields from another item into this one (Id/Done are preserved).</summary>
    public void CopyEditableFrom(LaunchItem o)
    {
        Title = o.Title; Target = o.Target; Args = o.Args;
        WorkingDir = o.WorkingDir; Kind = o.Kind; RunAsAdmin = o.RunAsAdmin;
    }

    /// <summary>Duplicate key for the "no repeated items" rule: same kind + same target + same args
    /// (whitespace/case-insensitive). Titles may differ — the launch behavior is what must be unique.</summary>
    public string DupKey()
    {
        static string N(string? s) => (s ?? "").Trim().ToLowerInvariant();
        return $"{Kind}{N(Target)}{N(Args)}";
    }
}

/// <summary>The whole 快速启动 list, versioned for forward-compatible schema changes.</summary>
public sealed class LaunchConfig
{
    public int Version { get; set; } = 1;
    public List<LaunchItem> Items { get; set; } = new();
}
