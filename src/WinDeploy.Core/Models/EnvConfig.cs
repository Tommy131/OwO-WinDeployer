namespace WinDeploy.Core.Models;

/// <summary>configs/env/env.json — declarative user environment variables and PATH entries.</summary>
public sealed class EnvConfig
{
    public Dictionary<string, string> Vars { get; set; } = new();
    public List<string> Path { get; set; } = new();
}
