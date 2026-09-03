using System.IO;
using OwOWinDeployer.App.Services.Ftp;
using OwOWinDeployer.Core.I18n;

namespace OwOWinDeployer.App.ViewModels.Ftp;

public sealed class FtpTransferRowViewModel : ObservableObject
{
    private readonly FtpTransferJob _job;
    private long _lastBytes;
    private long _lastTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    private double _bytesPerSecond;

    public FtpTransferRowViewModel(FtpTransferJob job) => _job = job;

    public Guid Id => _job.Id;
    public FtpTransferState State => _job.State;
    public string Name => Path.GetFileName(_job.Request.Direction == FtpTransferDirection.Download
        ? _job.Request.SourcePath.TrimEnd('/', '\\')
        : _job.Request.SourcePath.TrimEnd('/', '\\'));
    public string Target => _job.Request.DestinationPath;
    public string DirectionIcon => _job.Request.Direction == FtpTransferDirection.Download ? "" : "";
    public string DirectionText => Localizer.T(_job.Request.Direction == FtpTransferDirection.Download
        ? "ftp.transfer.download" : "ftp.transfer.upload");
    public string SizeText => _job.Request.Kind == FtpTransferEntryKind.Directory
        ? Localizer.T("ftp.client.typeDir") : FtpRemoteRowVm.Human(_job.Request.Size);
    public double ProgressValue => _job.Request.Size > 0
        ? Math.Min(100, _job.BytesTransferred * 100.0 / _job.Request.Size)
        : State == FtpTransferState.Completed ? 100 : 0;
    public string ProgressText => _job.Request.Kind == FtpTransferEntryKind.Directory
        ? "—" : $"{ProgressValue:0}%";
    public string SpeedText => State == FtpTransferState.Transferring ? FormatSpeed(_bytesPerSecond) : "—";
    public string EtaText
    {
        get
        {
            if (State != FtpTransferState.Transferring || _bytesPerSecond <= 1 || _job.Request.Size <= 0) return "—";
            return HumanTime(Math.Max(0, _job.Request.Size - _job.BytesTransferred) / _bytesPerSecond);
        }
    }
    public string StateText => State switch
    {
        FtpTransferState.Scanning => Localizer.T("ftp.transfer.scanning"),
        FtpTransferState.Queued => Localizer.T("ftp.transfer.queued"),
        FtpTransferState.Connecting => Localizer.T("ftp.transfer.connecting"),
        FtpTransferState.Transferring => Localizer.T("ftp.transfer.transferring"),
        FtpTransferState.Completing => Localizer.T("ftp.transfer.completing"),
        FtpTransferState.Completed => Localizer.T("ftp.transfer.completed"),
        FtpTransferState.Canceling => Localizer.T("ftp.transfer.canceling"),
        FtpTransferState.Canceled => Localizer.T("ftp.transfer.canceled"),
        FtpTransferState.Failed => _job.ResidualPath is { } path
            ? Localizer.Format("ftp.transfer.uploadCleanupFailed", path)
            : Localizer.Format("ftp.transfer.failed", _job.Error ?? Localizer.T("ftp.transfer.failedGeneric")),
        _ => State.ToString(),
    };
    public bool CanCancel => State is FtpTransferState.Scanning or FtpTransferState.Queued
        or FtpTransferState.Connecting or FtpTransferState.Transferring or FtpTransferState.Completing;
    public bool IsFailed => State == FtpTransferState.Failed;
    public double BytesPerSecond => _bytesPerSecond;

    public void Refresh()
    {
        if (State != FtpTransferState.Transferring)
        {
            _bytesPerSecond = 0;
            _lastBytes = _job.BytesTransferred;
            _lastTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            RaiseAllPropertiesChanged();
            return;
        }
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = (now - _lastTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
        var bytes = _job.BytesTransferred;
        if (elapsed > 0.1)
        {
            _bytesPerSecond = Math.Max(0, (bytes - _lastBytes) / elapsed);
            _lastBytes = bytes;
            _lastTimestamp = now;
        }
        RaiseAllPropertiesChanged();
    }

    internal static string FormatSpeed(double bytesPerSecond)
        => bytesPerSecond >= 1024 * 1024 ? $"{bytesPerSecond / 1024 / 1024:0.0} MB/s"
         : bytesPerSecond >= 1024 ? $"{bytesPerSecond / 1024:0.0} KB/s"
         : $"{bytesPerSecond:0} B/s";

    private static string HumanTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "—";
        if (seconds < 1) return Localizer.T("ftp.time.aboutOneSecond");
        if (seconds < 60) return Localizer.Format("ftp.time.aboutSeconds", (int)Math.Round(seconds));
        if (seconds < 3600) return Localizer.Format("ftp.time.aboutMinutes", (int)(seconds / 60), (int)(seconds % 60));
        return Localizer.Format("ftp.time.aboutHours", (int)(seconds / 3600), (int)(seconds % 3600 / 60));
    }
}
