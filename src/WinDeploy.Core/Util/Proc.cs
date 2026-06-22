using System.Diagnostics;
using System.Text;

namespace WinDeploy.Core.Util;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Thin async process runner. Resolves bare commands on PATH and wraps .cmd/.bat/.ps1.</summary>
public static class Proc
{
    public static async Task<ProcResult> RunAsync(string file, IEnumerable<string> args,
        string? cwd = null, CancellationToken ct = default)
    {
        // Resolve a bare command name (e.g. "winget") to a full path on PATH.
        if (!Path.IsPathRooted(file) && string.IsNullOrEmpty(Path.GetExtension(file)))
            file = CommandFinder.Find(file) ?? file;

        var ext = Path.GetExtension(file);
        ProcessStartInfo psi;

        if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo("cmd.exe");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(file);
        }
        else if (ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo("powershell.exe");
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", file })
                psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo(file);
        }

        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8; // winget/git emit UTF-8; needed for CJK names
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        if (cwd != null) psi.WorkingDirectory = cwd;

        using var p = new Process { StartInfo = psi };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) se.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        return new ProcResult(p.ExitCode, so.ToString(), se.ToString());
    }
}
