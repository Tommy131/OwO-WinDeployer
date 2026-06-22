using WinDeploy.Core;
using WinDeploy.Core.Config;
using WinDeploy.Core.Engine;
using WinDeploy.Core.Engine.Installers;
using WinDeploy.Core.Export;
using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

var argList = args.ToList();
if (argList.Count == 0 || argList[0] is "help" or "-h" or "--help")
{
    PrintHelp();
    return 0;
}

string command = argList[0];
var opts = Opts.Parse(argList.Skip(1).ToList());

// --log <file>: tee all console output to a file (for unattended / scheduled runs).
if (opts.Get("log") is string logPath) { try { Tee.Start(logPath); } catch { /* keep running */ } }
// --silent implies --yes and plain (uncoloured) output for log friendliness.
if (opts.Has("silent")) Log.UseColor = false;

// Locate catalog/ (explicit --catalog, else walk up from cwd, else from the exe location).
string? catalogDir = opts.Get("catalog") is string cp
    ? Path.GetDirectoryName(Path.GetFullPath(cp))
    : CatalogLoader.FindCatalogDir(Directory.GetCurrentDirectory())
      ?? CatalogLoader.FindCatalogDir(AppContext.BaseDirectory);

if (catalogDir is null)
{
    Log.Err("找不到 catalog/catalog.json（用 --catalog <path> 指定）");
    return 1;
}

string catalogPath = Path.Combine(catalogDir, "catalog.json");
string repoRoot = Path.GetDirectoryName(catalogDir)!;

Catalog catalog;
try { catalog = CatalogLoader.Load(catalogPath); }
catch (Exception ex) { Log.Err($"加载 catalog 失败: {ex.Message}"); return 1; }

var resolver = new PathResolver(catalog.PathVars);
Profile? profile = opts.Get("profile") is string pn ? CatalogLoader.LoadProfile(catalogDir, pn) : null;
var only = opts.Get("only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
bool all = opts.Has("all");
string? category = opts.Get("category");

// ⑪ 主机名 → 预设：未显式指定任何选择时，按 catalog/hosts.json 匹配本机预设。
if (profile == null && (only is null || only.Length == 0) && !all && category == null
    && command is "apply" or "plan" or "sync" or "download-only" or "export-dsc")
{
    if (HostProfiles.Resolve(catalogDir) is string hp)
    {
        try { profile = CatalogLoader.LoadProfile(catalogDir, hp); Log.Info($"按主机名 {Environment.MachineName} 匹配预设：{hp}"); }
        catch { Log.Warn($"hosts.json 指定的预设 '{hp}' 不存在"); }
    }
}

// ① 锁定版本：apply/plan 时套用 lock.json 中钉定的版本。
if (opts.Has("locked") && command is "apply" or "plan")
{
    var lf = Lockfile.Load(catalogDir);
    if (lf != null) Log.Info($"已加载 lock.json：钉定 {lf.ApplyTo(catalog)} 个版本");
    else Log.Warn("未找到 catalog/lock.json（先运行 windeploy lock 生成）");
}

return command switch
{
    "list" => CmdList(catalog),
    "plan" => await CmdPlan(catalog, resolver, profile, only, all, category),
    "apply" => await CmdApply(catalog, resolver, repoRoot, profile, only, all, category, opts.Has("yes") || opts.Has("silent")),
    "apply-config" => await CmdApplyConfig(catalog, resolver, repoRoot, opts.Has("yes")),
    "export" => await CmdExport(catalog, resolver, repoRoot),
    "ssh-setup" => await CmdSshSetup(repoRoot, opts.Has("register")),
    "sync" => await CmdSync(catalog, resolver, repoRoot, profile, only, all, category),
    "save" => await CmdSave(repoRoot, opts.Get("message"), opts.Has("push")),
    "doctor" => await CmdDoctor(catalog, resolver),
    "validate" => CmdValidate(catalog, repoRoot),
    "lock" => await CmdLock(catalog, catalogDir),
    "export-dsc" => CmdExportDsc(catalog, profile, only, all, category, opts.Get("out")),
    "inventory" => await CmdInventory(opts.Get("format"), opts.Get("out")),
    "download-only" => await CmdDownloadOnly(catalog, resolver, repoRoot, profile, only, all, category, opts.Get("out")),
    "migrate" => await CmdMigrate(catalog, resolver, repoRoot, opts),
    _ => Unknown(command),
};

async Task<int> CmdApplyConfig(Catalog cat, PathResolver pr, string root, bool includeAsk)
{
    var ctx = new EngineContext { Path = pr, RepoRoot = root, Ct = CancellationToken.None };
    var results = await new ConfigEngine().ApplyAsync(cat, ctx, it => Detection.IsInstalledAsync(it, pr), includeAsk);
    PrintConfig(results);
    return results.Any(r => r.Status == StepStatus.Failed) ? 1 : 0;
}

async Task<int> CmdExport(Catalog cat, PathResolver pr, string root)
{
    var ctx = new EngineContext { Path = pr, RepoRoot = root, Ct = CancellationToken.None };
    var results = await new ConfigEngine().ExportAsync(cat, ctx, it => Detection.IsInstalledAsync(it, pr));
    PrintConfig(results);
    Log.Info("已写回 configs/，记得 git commit");
    return 0;
}

async Task<int> CmdSshSetup(string root, bool register)
{
    var results = await SshSetup.RunAsync(root, register, CancellationToken.None);
    PrintConfig(results);
    return results.Any(r => r.Status == StepStatus.Failed) ? 1 : 0;
}

void PrintConfig(List<ConfigResult> results)
{
    Console.WriteLine();
    foreach (var r in results)
    {
        var tag = r.Status switch { StepStatus.Ok => "✓", StepStatus.Failed => "✗", _ => "·" };
        Console.WriteLine($"    {tag} {r.Name}  {r.Message}");
    }
    Console.WriteLine();
}

int Unknown(string c)
{
    Log.Err($"未知命令: {c}");
    PrintHelp();
    return 1;
}

int CmdList(Catalog cat)
{
    foreach (var grp in cat.Items.GroupBy(i => i.Category))
    {
        Console.WriteLine();
        Log.Step(grp.Key);
        foreach (var i in grp)
            Console.WriteLine($"    {(i.Default ? "●" : "○")} {i.Id,-18} {i.Summary ?? i.Name}");
    }
    Console.WriteLine();
    Log.Info("● = 默认强制安装   ○ = 可选");
    return 0;
}

async Task<int> CmdPlan(Catalog cat, PathResolver pr, Profile? prof,
    IReadOnlyCollection<string>? sel, bool selAll, string? cat2)
{
    var items = Selection.Resolve(cat, prof, sel, selAll, cat2);
    if (items.Count == 0) { Log.Warn("没有匹配的软件"); return 0; }

    var engine = new InstallEngine();
    var plan = await engine.BuildPlanAsync(items, pr);

    Console.WriteLine();
    foreach (var pi in plan)
    {
        var tag = pi.Status == PlanStatus.Installed ? "已装" : "待装";
        Console.WriteLine($"    [{tag}] {pi.Item.Install.Method,-13} {pi.Item.Name}");
    }
    var todo = plan.Count(p => p.Status == PlanStatus.ToInstall);
    Console.WriteLine();
    Log.Info($"共 {plan.Count} 项 · 待装 {todo} · 已装 {plan.Count - todo}");
    return 0;
}

async Task<int> CmdApply(Catalog cat, PathResolver pr, string root, Profile? prof,
    IReadOnlyCollection<string>? sel, bool selAll, string? cat2, bool yes)
{
    var items = Selection.Resolve(cat, prof, sel, selAll, cat2);
    if (items.Count == 0) { Log.Warn("没有匹配的软件"); return 0; }

    var engine = new InstallEngine();
    var plan = await engine.BuildPlanAsync(items, pr);
    var todo = plan.Where(p => p.Status == PlanStatus.ToInstall).ToList();

    Log.Info($"待装 {todo.Count} 项；已装 {plan.Count - todo.Count} 项将跳过");
    foreach (var pi in todo)
        Console.WriteLine($"    + {pi.Item.Name} ({pi.Item.Install.Method})");

    if (todo.Count == 0) { Log.Ok("全部就绪，无需安装"); return 0; }

    if (!yes)
    {
        Console.Write("\n  确认开始安装? [y/N] ");
        var answer = Console.ReadLine();
        if (answer?.Trim().ToLowerInvariant() is not ("y" or "yes")) { Log.Warn("已取消"); return 0; }
    }

    var ctx = new EngineContext { Path = pr, RepoRoot = root, Ct = CancellationToken.None };
    var summary = await engine.ApplyAsync(plan, ctx, dryRun: false,
        onStart: pi => Log.Step($"安装 {pi.Item.Name} …"));

    Console.WriteLine();
    foreach (var r in summary.Results.Where(r => r.Status == StepStatus.Failed))
        Log.Err($"{r.Item.Name}: {r.Message}");
    Log.Info($"完成 · 成功 {summary.Ok} · 跳过 {summary.Skipped} · 失败 {summary.Failed}");
    return summary.Failed > 0 ? 1 : 0;
}

async Task<int> CmdSync(Catalog cat, PathResolver pr, string root, Profile? prof,
    IReadOnlyCollection<string>? sel, bool selAll, string? cat2)
{
    Log.Step("git pull --ff-only");
    var pull = await Proc.RunAsync("git", new[] { "-C", root, "pull", "--ff-only" });
    Log.Info(pull.Ok ? "已拉取最新" : "拉取未成功（可能无远程 / 有冲突）");

    Log.Step("套用配置");
    var ctx = new EngineContext { Path = pr, RepoRoot = root, Ct = CancellationToken.None };
    var cfg = await new ConfigEngine().ApplyAsync(cat, ctx, it => Detection.IsInstalledAsync(it, pr), includeAsk: false);
    PrintConfig(cfg);

    Log.Step("安装计划（如需安装，运行 apply）");
    return await CmdPlan(cat, pr, prof, sel, selAll, cat2);
}

async Task<int> CmdSave(string root, string? message, bool push)
{
    await Proc.RunAsync("git", new[] { "-C", root, "add", "-A" });
    var msg = message ?? $"sync from {Environment.MachineName} {DateTime.Now:yyyy-MM-dd HH:mm}";
    var commit = await Proc.RunAsync("git", new[] { "-C", root, "commit", "-m", msg });
    Log.Info(commit.Ok ? $"已提交：{msg}" : "无改动或提交失败");
    if (push)
    {
        var p = await Proc.RunAsync("git", new[] { "-C", root, "push" });
        Log.Info(p.Ok ? "已 push" : "push 失败（检查远程）");
    }
    return 0;
}

async Task<int> CmdDoctor(Catalog cat, PathResolver pr)
{
    Log.Step("环境体检 …");
    var findings = await Doctor.RunAsync(cat, pr);
    Console.WriteLine();
    foreach (var f in findings)
    {
        var tag = f.Level switch { HealthLevel.Error => "✗", HealthLevel.Warn => "!", _ => "✓" };
        Console.WriteLine($"  {tag} {f.Title}");
        foreach (var line in f.Detail.Split('\n')) Console.WriteLine($"      {line}");
        if (f.Fix != null) Console.WriteLine($"      → {f.Fix}");
    }
    Console.WriteLine();
    var errors = findings.Count(f => f.Level == HealthLevel.Error);
    var warns = findings.Count(f => f.Level == HealthLevel.Warn);
    Log.Info($"完成 · 错误 {errors} · 警告 {warns}");
    return errors > 0 ? 1 : 0;
}

int CmdValidate(Catalog cat, string root)
{
    var issues = CatalogValidator.Validate(cat, root);
    Console.WriteLine();
    foreach (var i in issues.OrderBy(i => i.Level))
        Console.WriteLine($"  {(i.Level == IssueLevel.Error ? "✗" : "!")} [{i.ItemId}] {i.Message}");
    var errors = issues.Count(i => i.Level == IssueLevel.Error);
    var warns = issues.Count(i => i.Level == IssueLevel.Warn);
    Console.WriteLine();
    if (issues.Count == 0) Log.Ok("catalog.json 校验通过，无问题");
    else Log.Info($"校验完成 · 错误 {errors} · 警告 {warns}");
    return errors > 0 ? 1 : 0;
}

async Task<int> CmdLock(Catalog cat, string catDir)
{
    Log.Step("采集已装版本 → lock.json …");
    var lf = await Lockfile.CaptureAsync(cat, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    lf.Save(catDir);
    Log.Ok($"已写入 {Lockfile.DefaultPath(catDir)} · 钉定 {lf.Versions.Count} 个版本");
    Log.Info("提交 lock.json 后，其它机器可用 windeploy apply --locked 复刻相同版本");
    return 0;
}

int CmdExportDsc(Catalog cat, Profile? prof, IReadOnlyCollection<string>? sel, bool selAll, string? cat2, string? outPath)
{
    var items = Selection.Resolve(cat, prof, sel, selAll, cat2);
    var yaml = DscExport.Build(items);
    var path = outPath ?? "windeploy.dsc.yaml";
    File.WriteAllText(path, yaml);
    Log.Ok($"已导出 winget configure 配置：{Path.GetFullPath(path)}");
    Log.Info($"在目标机运行：winget configure -f \"{path}\"");
    return 0;
}

async Task<int> CmdInventory(string? format, string? outPath)
{
    Log.Step("读取已装软件清单 …");
    var items = await Inventory.ListAsync();
    var fmt = (format ?? "csv").ToLowerInvariant();
    var text = fmt switch
    {
        "json" => Inventory.ToJson(items),
        "html" => Inventory.ToHtml(items),
        _ => Inventory.ToCsv(items),
    };
    if (outPath != null) { File.WriteAllText(outPath, text); Log.Ok($"已导出 {items.Count} 项 → {Path.GetFullPath(outPath)}"); }
    else Console.WriteLine(text);
    return 0;
}

async Task<int> CmdDownloadOnly(Catalog cat, PathResolver pr, string root, Profile? prof,
    IReadOnlyCollection<string>? sel, bool selAll, string? cat2, string? outDir)
{
    var items = Selection.Resolve(cat, prof, sel, selAll, cat2);
    if (items.Count == 0) { Log.Warn("没有匹配的软件"); return 0; }
    var dir = outDir ?? Path.Combine(Directory.GetCurrentDirectory(), "windeploy-offline");
    Log.Step($"预下载 {items.Count} 项 → {dir}");
    var ctx = new EngineContext
    {
        Path = pr, RepoRoot = root, Ct = CancellationToken.None,
        Report = Log.Info,
    };
    var results = await OfflineKit.DownloadAsync(items, dir, ctx);
    PrintConfig(results);
    Log.Info($"完成 · 文件位于 {Path.GetFullPath(dir)}");
    return results.Any(r => r.Status == StepStatus.Failed) ? 1 : 0;
}

async Task<int> CmdMigrate(Catalog cat, PathResolver pr, string root, Opts o)
{
    var sub = o.Get("export") != null ? "export" : o.Get("import") != null ? "import" : null;
    var dir = o.Get("export") ?? o.Get("import");
    if (sub == null || dir == null)
    {
        Log.Err("用法: windeploy migrate --export <目录>  |  windeploy migrate --import <目录>");
        return 1;
    }
    var ctx = new EngineContext { Path = pr, RepoRoot = root, Ct = CancellationToken.None };
    if (sub == "export")
    {
        Log.Step($"导出迁移工具包 → {dir}");
        var results = await MigrationKit.ExportAsync(cat, ctx, dir, it => Detection.IsInstalledAsync(it, pr));
        PrintConfig(results);
        Log.Ok($"迁移工具包已生成：{Path.GetFullPath(dir)}（含 configs/、manifest.json、RESTORE.txt）");
        return 0;
    }
    else
    {
        Log.Step($"从迁移工具包还原 ← {dir}");
        var (results, manifest) = MigrationKit.Import(dir, root);
        PrintConfig(results);
        if (manifest is { InstalledIds.Count: > 0 })
            Log.Info($"还原软件：windeploy apply --only {string.Join(",", manifest.InstalledIds)}");
        return 0;
    }
}

void PrintHelp()
{
    Console.WriteLine("""
    OwO! Win Deployer — Windows 环境复刻器 (M1 / CLI)

      windeploy <命令> [选项]

    命令:
      list                    列出 catalog 中的全部软件
      plan                    显示将安装/已装的计划（不执行）
      apply                   执行安装
      apply-config            套用配置（VS Code/Git/env…，按 applyWhen）
      export                  采集本机配置回写仓库
      ssh-setup [--register]  生成本机 SSH 密钥并套用 ssh 配置
      sync                    git pull → 套用配置 + 显示安装计划
      save [--message m] [--push]   提交 configs 改动（--push 推送到远程）
      doctor                  环境体检（PATH 重复/失效、*_HOME 失效、已装但不在 PATH）
      validate                校验 catalog.json（CI 友好；有错误时退出码 1）
      lock                    采集已装版本写入 catalog/lock.json（可复现）
      export-dsc [--out f]    导出为 winget configure (DSC) YAML
      inventory [--format csv|json|html] [--out f]   导出本机已装软件清单
      download-only [--out d] 仅预下载所选软件安装包（离线/U 盘部署）
      migrate --export <目录> | --import <目录>      迁移工具包导出 / 还原

    选项:
      --profile <名称>        使用预设 (catalog/profiles/<名称>.json)
      --only <id,id>          仅这些 id
      --category <类别>       仅该类别
      --all                   全部
      --yes / --silent        apply 时跳过确认（--silent 另关闭彩色输出）
      --locked                apply/plan 时套用 lock.json 钉定的版本
      --log <文件>            将输出同时写入日志文件（无人值守）
      --catalog <路径>        指定 catalog.json

    示例:
      windeploy plan  --profile dev
      windeploy apply --profile dev --silent --log deploy.log
      windeploy apply --profile dev --locked
      windeploy doctor
      windeploy validate
      windeploy inventory --format html --out inventory.html
      windeploy export-dsc --profile full --out full.dsc.yaml
      windeploy migrate --export D:\kit
    """);
}

sealed class Opts
{
    private readonly Dictionary<string, string?> _d = new(StringComparer.OrdinalIgnoreCase);

    public static Opts Parse(List<string> a)
    {
        var o = new Opts();
        for (var i = 0; i < a.Count; i++)
        {
            var t = a[i];
            if (!t.StartsWith("--")) continue;
            var key = t[2..];
            string? val = null;
            if (i + 1 < a.Count && !a[i + 1].StartsWith("--")) val = a[++i];
            o._d[key] = val;
        }
        return o;
    }

    public bool Has(string k) => _d.ContainsKey(k);
    public string? Get(string k) => _d.TryGetValue(k, out var v) ? v : null;
}

/// <summary>Tees Console.Out to a file as well as the terminal (for --log / unattended runs).</summary>
static class Tee
{
    public static void Start(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var writer = new System.IO.StreamWriter(full, append: true) { AutoFlush = true };
        writer.WriteLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} windeploy ====");
        Console.SetOut(new TeeWriter(Console.Out, writer));
    }

    private sealed class TeeWriter(System.IO.TextWriter a, System.IO.TextWriter b) : System.IO.TextWriter
    {
        public override System.Text.Encoding Encoding => a.Encoding;
        public override void Write(char c) { a.Write(c); b.Write(c); }
        public override void Write(string? s) { a.Write(s); b.Write(s); }
        public override void WriteLine(string? s) { a.WriteLine(s); b.WriteLine(s); }
    }
}
