using System.Diagnostics;

namespace OwOWinDeployer.App.Services.Launch;

/// <summary>Opens a <see cref="LaunchItem"/> via ShellExecute so applications, files, shortcuts (.lnk),
/// URLs and shell commands all work uniformly (and elevation via the "runas" verb is available).</summary>
public static class LaunchRunner
{
    /// <summary>Launch the item. Returns (ok, detail) — detail carries the failure reason on error
    /// (e.g. the UAC prompt was cancelled), and is empty on success.</summary>
    public static (bool Ok, string Detail) Run(LaunchItem item)
    {
        try
        {
            var psi = Build(item);
            if (psi == null) return (false, "empty target");
            Process.Start(psi);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static ProcessStartInfo? Build(LaunchItem item)
    {
        var target = Expand(item.Target);
        if (string.IsNullOrWhiteSpace(target)) return null;

        var psi = new ProcessStartInfo { UseShellExecute = true };
        var workDir = Expand(item.WorkingDir);
        if (!string.IsNullOrWhiteSpace(workDir)) psi.WorkingDirectory = workDir;
        if (item.RunAsAdmin) psi.Verb = "runas";   // triggers UAC

        switch (item.Kind)
        {
            case LaunchKind.Url:
                psi.FileName = NormalizeUrl(target);
                break;

            case LaunchKind.Command:
                // Run through the shell so PATH lookup, built-ins and redirection behave as typed.
                psi.FileName = "cmd.exe";
                var tail = string.IsNullOrWhiteSpace(item.Args) ? "" : " " + item.Args;
                psi.Arguments = $"/c {target}{tail}";
                break;

            default: // App / file / shortcut
                psi.FileName = target;
                if (!string.IsNullOrWhiteSpace(item.Args)) psi.Arguments = item.Args;
                break;
        }
        return psi;
    }

    /// <summary>Prepend https:// when the user typed a bare host (no scheme), so the browser opens it.</summary>
    private static string NormalizeUrl(string url)
        => url.Contains("://", StringComparison.Ordinal) || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            ? url
            : "https://" + url;

    private static string Expand(string? s)
        => string.IsNullOrWhiteSpace(s) ? "" : Environment.ExpandEnvironmentVariables(s).Trim();
}
