using WinDeploy.Core.Engine;

namespace WinDeploy.Core.Config;

public sealed class ConfigResult
{
    public string Name { get; init; } = "";
    public StepStatus Status { get; init; }
    public string? Message { get; init; }

    public static ConfigResult Ok(string name, string? msg = null) => new() { Name = name, Status = StepStatus.Ok, Message = msg };
    public static ConfigResult Skip(string name, string? msg = null) => new() { Name = name, Status = StepStatus.Skipped, Message = msg };
    public static ConfigResult Fail(string name, string? msg = null) => new() { Name = name, Status = StepStatus.Failed, Message = msg };
}
