using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.Core.Engine;

public enum HealthLevel { Ok, Warn, Error }

/// <summary>One finding from a <see cref="Doctor"/> run: a severity, a short title, a human detail,
/// and an optional one-line suggested fix.</summary>
public sealed record HealthFinding(HealthLevel Level, string Title, string Detail, string? Fix = null);

/// <summary>Environment health check ("体检"): verifies PATH integrity and the env vars / toolchains this
/// tool manipulates. Pure reads — never mutates. Used by the CLI <c>doctor</c> command and the GUI.</summary>
public static class Doctor
{
    // Env vars commonly pointing at an install root; if set but the dir is gone, builds break confusingly.
    private static readonly string[] HomeVars =
    {
        "JAVA_HOME", "GOROOT", "GOPATH", "GCC_HOME", "ANDROID_HOME", "ANDROID_SDK_ROOT",
        "DOTNET_ROOT", "CMAKE_HOME", "MAVEN_HOME", "GRADLE_HOME", "PYTHONHOME",
    };

    public static async Task<List<HealthFinding>> RunAsync(Catalog? catalog, PathResolver pr)
    {
        var f = new List<HealthFinding>();
        CheckPath(f);
        CheckHomeVars(f);
        if (catalog != null) await CheckInstalledOnPathAsync(catalog, pr, f);

        if (f.All(x => x.Level == HealthLevel.Ok))
            f.Add(new HealthFinding(HealthLevel.Ok, "环境正常", "未发现 PATH / 环境变量问题。"));
        return f;
    }

    /// <summary>Duplicate PATH entries (within or across User+Machine) and entries pointing at missing dirs.</summary>
    private static void CheckPath(List<HealthFinding> f)
    {
        var user = Split(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
        var machine = Split(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));

        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in user.Concat(machine))
        {
            var key = Norm(p);
            if (key.Length == 0) continue;
            seen[key] = seen.GetValueOrDefault(key) + 1;
        }

        var dups = seen.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
        if (dups.Count > 0)
            f.Add(new HealthFinding(HealthLevel.Warn, "PATH 重复项",
                $"{dups.Count} 个目录在 PATH 中重复：\n" + string.Join("\n", dups.Take(8).Select(d => "· " + d)),
                "去重可加快命令解析；可在「环境变量」页清理。"));

        var missing = user.Concat(machine)
            .Select(Norm).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !p.Contains('%') && !Directory.Exists(ExpandSafe(p)))
            .ToList();
        if (missing.Count > 0)
            f.Add(new HealthFinding(HealthLevel.Warn, "PATH 失效目录",
                $"{missing.Count} 个 PATH 目录不存在：\n" + string.Join("\n", missing.Take(8).Select(d => "· " + d)),
                "删除这些条目可消除 shell 启动告警。"));
    }

    private static void CheckHomeVars(List<HealthFinding> f)
    {
        foreach (var name in HomeVars)
        {
            var val = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                      ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
            if (string.IsNullOrWhiteSpace(val)) continue;
            var path = ExpandSafe(val);
            // GOPATH/ANDROID_HOME may legitimately not exist yet; only flag the *_HOME roots that must exist.
            if (!Directory.Exists(path) && !File.Exists(path))
                f.Add(new HealthFinding(HealthLevel.Error, $"{name} 指向无效路径",
                    $"{name} = {val}\n该目录不存在，依赖它的工具会报错。",
                    $"更新或删除环境变量 {name}。"));
        }
    }

    /// <summary>Catalog items detected as installed but whose command can't be resolved on PATH — the most
    /// common "it's installed but my terminal can't find it" failure.</summary>
    private static async Task CheckInstalledOnPathAsync(Catalog catalog, PathResolver pr, List<HealthFinding> f)
    {
        foreach (var item in catalog.Items)
        {
            var cmd = item.Detect?.Cmd;
            if (string.IsNullOrWhiteSpace(cmd)) continue;
            if (CommandFinder.Exists(cmd!)) continue;
            if (!await Detection.IsInstalledAsync(item, pr)) continue;
            f.Add(new HealthFinding(HealthLevel.Warn, $"{item.Name} 不在 PATH",
                $"检测到已安装，但命令 `{cmd}` 无法解析。",
                "重新登录或将其 bin 目录加入 PATH。"));
        }
    }

    private static string[] Split(string? path)
        => (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Norm(string p) => p.Trim().TrimEnd('\\', '/');

    private static string ExpandSafe(string p)
    {
        try { return Environment.ExpandEnvironmentVariables(p); } catch { return p; }
    }
}
