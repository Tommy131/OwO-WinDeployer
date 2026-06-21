using WinDeploy.Core.Engine;

namespace WinDeploy.Core.Config;

/// <summary>Details of one captured config file (for the export page's expandable view).</summary>
public sealed class ConfigFileInfo
{
    public string Path { get; init; } = "";   // repo-relative, e.g. configs/vscode/settings.json
    public long Size { get; init; }
    public string Preview { get; init; } = "";
}

public sealed class ConfigResult
{
    public string Name { get; init; } = "";
    public StepStatus Status { get; init; }
    public string? Message { get; init; }
    public List<ConfigFileInfo>? Files { get; init; }

    public static ConfigResult Ok(string name, string? msg = null, List<ConfigFileInfo>? files = null)
        => new() { Name = name, Status = StepStatus.Ok, Message = msg, Files = files };
    public static ConfigResult Skip(string name, string? msg = null) => new() { Name = name, Status = StepStatus.Skipped, Message = msg };
    public static ConfigResult Fail(string name, string? msg = null) => new() { Name = name, Status = StepStatus.Failed, Message = msg };
}
