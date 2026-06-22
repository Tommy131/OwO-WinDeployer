using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace WinDeploy.App.ViewModels;

/// <summary>An embedded interactive shell (PowerShell / cmd) for quick command debugging.
/// stdout/stderr stream into a log; typed commands are echoed and written to the shell's stdin.
/// The console codepage is switched to UTF-8 on start so CJK output isn't garbled.</summary>
public sealed class TerminalViewModel : ObservableObject, IDisposable
{
    private const int MaxChars = 200_000;
    private Process? _proc;

    public RelayCommand SendCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand RestartCommand { get; }

    public TerminalViewModel()
    {
        SendCommand = new RelayCommand(_ => Send(), _ => !string.IsNullOrWhiteSpace(_input));
        ClearCommand = new RelayCommand(_ => Output = "");
        RestartCommand = new RelayCommand(_ => Restart());
    }

    public string WorkingDir { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private bool _isPowerShell = true;
    public bool IsPowerShell { get => _isPowerShell; set { if (Set(ref _isPowerShell, value) && value) Restart(); } }

    private bool _isCmd;
    public bool IsCmd { get => _isCmd; set { if (Set(ref _isCmd, value) && value) Restart(); } }

    private string _output = "";
    public string Output { get => _output; set => Set(ref _output, value); }

    private string _input = "";
    public string Input { get => _input; set => Set(ref _input, value); }

    private string _status = "未启动";
    public string Status { get => _status; private set => Set(ref _status, value); }

    public void EnsureStarted()
    {
        if (_proc is { HasExited: false }) return;
        Start();
    }

    private void Start()
    {
        try
        {
            var dir = Directory.Exists(WorkingDir) ? WorkingDir : Environment.CurrentDirectory;
            var psi = new ProcessStartInfo
            {
                FileName = _isCmd ? "cmd.exe" : "powershell.exe",
                Arguments = _isCmd ? "/Q /K chcp 65001 >nul" : "-NoLogo -NoProfile",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = dir,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false),
            };
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (_, e) => { if (e.Data != null) Append(e.Data + "\n"); };
            _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Append(e.Data + "\n"); };
            _proc.Exited += (_, _) => Append("\n[shell 已退出]\n");
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            if (!_isCmd)
                _proc.StandardInput.WriteLine("chcp 65001 > $null; $OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()");
            Status = (_isCmd ? "cmd.exe" : "PowerShell") + " · " + dir;
        }
        catch (Exception ex) { Append("启动失败：" + ex.Message + "\n"); Status = "启动失败"; }
    }

    private void Send()
    {
        EnsureStarted();
        if (_proc == null) return;
        var cmd = _input;
        Append((_isCmd ? "> " : "PS> ") + cmd + "\n");
        try { _proc.StandardInput.WriteLine(cmd); _proc.StandardInput.Flush(); }
        catch (Exception ex) { Append("发送失败：" + ex.Message + "\n"); }
        Input = "";
    }

    private void Restart()
    {
        Kill();
        Output = "";
        Start();
    }

    private void Append(string s)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess()) { disp.BeginInvoke(() => Append(s)); return; }
        var text = _output + s;
        if (text.Length > MaxChars) text = text[^MaxChars..];
        Output = text;
    }

    private void Kill()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { /* best effort */ }
        try { _proc?.Dispose(); } catch { /* ignore */ }
        _proc = null;
    }

    public void Dispose() => Kill();
}
