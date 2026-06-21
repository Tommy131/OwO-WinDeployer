using WinDeploy.Core.Models;

namespace WinDeploy.Core.Engine;

public enum PlanStatus { ToInstall, Installed }

public sealed class PlanItem
{
    public required CatalogItem Item { get; init; }
    public required PlanStatus Status { get; init; }
}

public enum StepStatus { Ok, Skipped, Failed }

public sealed class StepOutcome
{
    public StepStatus Status { get; init; }
    public string? Message { get; init; }

    public static StepOutcome Done(string? m = null) => new() { Status = StepStatus.Ok, Message = m };
    public static StepOutcome Skip(string? m = null) => new() { Status = StepStatus.Skipped, Message = m };
    public static StepOutcome Fail(string? m = null) => new() { Status = StepStatus.Failed, Message = m };
}

public sealed class RunResult
{
    public CatalogItem Item { get; init; } = null!;
    public StepStatus Status { get; set; }
    public string? Message { get; set; }
    public TimeSpan Duration { get; set; }
}

public sealed class RunSummary
{
    public List<RunResult> Results { get; } = new();
    public int Ok => Results.Count(r => r.Status == StepStatus.Ok);
    public int Skipped => Results.Count(r => r.Status == StepStatus.Skipped);
    public int Failed => Results.Count(r => r.Status == StepStatus.Failed);
}
