using System.Globalization;
using WinDeploy.Core;
using WinDeploy.Core.Config;
using WinDeploy.Core.Engine;
using WinDeploy.Core.Engine.Installers;
using WinDeploy.Core.Export;
using WinDeploy.Core.I18n;
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

// UI language: --lang <code> wins, then WINDEPLOY_LANG, then the OS culture (invariant → en under
// InvariantGlobalization, so rely on --lang/env for non-English CLI output).
Localizer.SetLanguage(
    opts.Get("lang")
    ?? Environment.GetEnvironmentVariable("WINDEPLOY_LANG")
    ?? Lang.FromCulture(CultureInfo.CurrentUICulture));

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
    Log.Err(Localizer.T("cli.error.noCatalog"));
    return 1;
}

string catalogPath = Path.Combine(catalogDir, "catalog.json");
string repoRoot = Path.GetDirectoryName(catalogDir)!;

Catalog catalog;
try { catalog = CatalogLoader.Load(catalogPath); }
catch (Exception ex) { Log.Err(Localizer.Format("cli.error.loadCatalog", ex.Message)); return 1; }

// Localize item summaries so list/plan output prints in the active language.
CatalogLoader.ApplyLocalizedSummaries(catalog, catalogDir, Localizer.Current);

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
        try { profile = CatalogLoader.LoadProfile(catalogDir, hp); Log.Info(Localizer.Format("cli.hostProfile.matched", Environment.MachineName, hp)); }
        catch { Log.Warn(Localizer.Format("cli.hostProfile.missing", hp)); }
    }
}

// ① 锁定版本：apply/plan 时套用 lock.json 中钉定的版本。
if (opts.Has("locked") && command is "apply" or "plan")
{
    var lf = Lockfile.Load(catalogDir);
    if (lf != null) Log.Info(Localizer.Format("cli.lock.loaded", lf.ApplyTo(catalog)));
    else Log.Warn(Localizer.T("cli.lock.notFound"));
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
    Log.Info(Localizer.T("cli.export.done"));
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
    Log.Err(Localizer.Format("cli.error.unknownCommand", c));
    PrintHelp();
    return 1;
}

int CmdList(Catalog cat)
{
    var lang = Localizer.Current;
    foreach (var grp in cat.Items.GroupBy(i => i.Category))
    {
        Console.WriteLine();
        Log.Step(grp.Key);
        foreach (var i in grp)
            Console.WriteLine($"    {(i.Default ? "●" : "○")} {i.Id,-18} {i.SummaryFor(lang) ?? i.Name}");
    }
    Console.WriteLine();
    Log.Info(Localizer.T("cli.list.legend"));
    return 0;
}

async Task<int> CmdPlan(Catalog cat, PathResolver pr, Profile? prof,
    IReadOnlyCollection<string>? sel, bool selAll, string? cat2)
{
    var items = Selection.Resolve(cat, prof, sel, selAll, cat2);
    if (items.Count == 0) { Log.Warn(Localizer.T("cli.noMatch")); return 0; }

    var engine = new InstallEngine();
    var plan = await engine.BuildPlanAsync(items, pr);

    Console.WriteLine();
    foreach (var pi in plan)
    {
        var tag = pi.Status == PlanStatus.Installed ? Localizer.T("cli.plan.installed") : Localizer.T("cli.plan.toInstall");
        Console.WriteLine($"    [{tag}] {pi.Item.Install.Method,-13} {pi.Item.Name}");
    }
    var todo = plan.Count(p => p.Status == PlanStatus.ToInstall);
    Console.WriteLine();
    Log.Info(Localizer.Format("cli.plan.summary", plan.Count, todo, plan.Count - todo));
    return 0;
}

async Task<int> CmdApply(Catalog cat, PathResolver pr, string root, Profile? prof,
    IReadOnlyCollection<string>? sel, bool selAll, string? cat2, bool yes)
{
    var items = Selection.Resolve(cat, prof, sel, selAll, cat2);
    if (items.Count == 0) { Log.Warn(Localizer.T("cli.noMatch")); return 0; }

    var engine = new InstallEngine();
    var plan = await engine.BuildPlanAsync(items, pr);
    var todo = plan.Where(p => p.Status == PlanStatus.ToInstall).ToList();

    Log.Info(Localizer.Format("cli.apply.todo", todo.Count, plan.Count - todo.Count));
    foreach (var pi in todo)
        Console.WriteLine($"    + {pi.Item.Name} ({pi.Item.Install.Method})");

    if (todo.Count == 0) { Log.Ok(Localizer.T("cli.apply.nothingToDo")); return 0; }

    if (!yes)
    {
        Console.Write(Localizer.T("cli.apply.confirm"));
        var answer = Console.ReadLine();
        if (answer?.Trim().ToLowerInvariant() is not ("y" or "yes")) { Log.Warn(Localizer.T("cli.cancelled")); return 0; }
    }

    var ctx = new EngineContext { Path = pr, RepoRoot = root, Ct = CancellationToken.None };
    var summary = await engine.ApplyAsync(plan, ctx, dryRun: false,
        onStart: pi => Log.Step(Localizer.Format("cli.apply.installing", pi.Item.Name)));

    Console.WriteLine();
    foreach (var r in summary.Results.Where(r => r.Status == StepStatus.Failed))
        Log.Err($"{r.Item.Name}: {r.Message}");
    Log.Info(Localizer.Format("cli.apply.done", summary.Ok, summary.Skipped, summary.Failed));
    return summary.Failed > 0 ? 1 : 0;
}

async Task<int> CmdSync(Catalog cat, PathResolver pr, string root, Profile? prof,
    IReadOnlyCollection<string>? sel, bool selAll, string? cat2)
{
    Log.Step("git pull --ff-only");
    var pull = await Proc.RunAsync("git", new[] { "-C", root, "pull", "--ff-only" });
    Log.Info(pull.Ok ? Localizer.T("cli.sync.pulled") : Localizer.T("cli.sync.pullFailed"));

    Log.Step(Localizer.T("cli.sync.applyConfig"));
    var ctx = new EngineContext { Path = pr, RepoRoot = root, Ct = CancellationToken.None };
    var cfg = await new ConfigEngine().ApplyAsync(cat, ctx, it => Detection.IsInstalledAsync(it, pr), includeAsk: false);
    PrintConfig(cfg);

    Log.Step(Localizer.T("cli.sync.planStep"));
    return await CmdPlan(cat, pr, prof, sel, selAll, cat2);
}

async Task<int> CmdSave(string root, string? message, bool push)
{
    await Proc.RunAsync("git", new[] { "-C", root, "add", "-A" });
    var msg = message ?? $"sync from {Environment.MachineName} {DateTime.Now:yyyy-MM-dd HH:mm}";
    var commit = await Proc.RunAsync("git", new[] { "-C", root, "commit", "-m", msg });
    Log.Info(commit.Ok ? Localizer.Format("cli.save.committed", msg) : Localizer.T("cli.save.noChange"));
    if (push)
    {
        var p = await Proc.RunAsync("git", new[] { "-C", root, "push" });
        Log.Info(p.Ok ? Localizer.T("cli.save.pushed") : Localizer.T("cli.save.pushFailed"));
    }
    return 0;
}

async Task<int> CmdDoctor(Catalog cat, PathResolver pr)
{
    Log.Step(Localizer.T("cli.doctor.checking"));
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
    Log.Info(Localizer.Format("cli.doctor.done", errors, warns));
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
    if (issues.Count == 0) Log.Ok(Localizer.T("cli.validate.passed"));
    else Log.Info(Localizer.Format("cli.validate.done", errors, warns));
    return errors > 0 ? 1 : 0;
}

async Task<int> CmdLock(Catalog cat, string catDir)
{
    Log.Step(Localizer.T("cli.lock.capturing"));
    var lf = await Lockfile.CaptureAsync(cat, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    lf.Save(catDir);
    Log.Ok(Localizer.Format("cli.lock.written", Lockfile.DefaultPath(catDir), lf.Versions.Count));
    Log.Info(Localizer.T("cli.lock.hint"));
    return 0;
}

int CmdExportDsc(Catalog cat, Profile? prof, IReadOnlyCollection<string>? sel, bool selAll, string? cat2, string? outPath)
{
    var items = Selection.Resolve(cat, prof, sel, selAll, cat2);
    var yaml = DscExport.Build(items);
    var path = outPath ?? "windeploy.dsc.yaml";
    File.WriteAllText(path, yaml);
    Log.Ok(Localizer.Format("cli.exportDsc.done", Path.GetFullPath(path)));
    Log.Info(Localizer.Format("cli.exportDsc.hint", path));
    return 0;
}

async Task<int> CmdInventory(string? format, string? outPath)
{
    Log.Step(Localizer.T("cli.inventory.reading"));
    var items = await Inventory.ListAsync();
    var fmt = (format ?? "csv").ToLowerInvariant();
    var text = fmt switch
    {
        "json" => Inventory.ToJson(items),
        "html" => Inventory.ToHtml(items),
        _ => Inventory.ToCsv(items),
    };
    if (outPath != null) { File.WriteAllText(outPath, text); Log.Ok(Localizer.Format("cli.inventory.exported", items.Count, Path.GetFullPath(outPath))); }
    else Console.WriteLine(text);
    return 0;
}

async Task<int> CmdDownloadOnly(Catalog cat, PathResolver pr, string root, Profile? prof,
    IReadOnlyCollection<string>? sel, bool selAll, string? cat2, string? outDir)
{
    var items = Selection.Resolve(cat, prof, sel, selAll, cat2);
    if (items.Count == 0) { Log.Warn(Localizer.T("cli.noMatch")); return 0; }
    var dir = outDir ?? Path.Combine(Directory.GetCurrentDirectory(), "windeploy-offline");
    Log.Step(Localizer.Format("cli.downloadOnly.predownload", items.Count, dir));
    var ctx = new EngineContext
    {
        Path = pr, RepoRoot = root, Ct = CancellationToken.None,
        Report = Log.Info,
    };
    var results = await OfflineKit.DownloadAsync(items, dir, ctx);
    PrintConfig(results);
    Log.Info(Localizer.Format("cli.downloadOnly.done", Path.GetFullPath(dir)));
    return results.Any(r => r.Status == StepStatus.Failed) ? 1 : 0;
}

async Task<int> CmdMigrate(Catalog cat, PathResolver pr, string root, Opts o)
{
    var sub = o.Get("export") != null ? "export" : o.Get("import") != null ? "import" : null;
    var dir = o.Get("export") ?? o.Get("import");
    if (sub == null || dir == null)
    {
        Log.Err(Localizer.T("cli.migrate.usage"));
        return 1;
    }
    var ctx = new EngineContext { Path = pr, RepoRoot = root, Ct = CancellationToken.None };
    if (sub == "export")
    {
        Log.Step(Localizer.Format("cli.migrate.exporting", dir));
        var results = await MigrationKit.ExportAsync(cat, ctx, dir, it => Detection.IsInstalledAsync(it, pr));
        PrintConfig(results);
        Log.Ok(Localizer.Format("cli.migrate.exportDone", Path.GetFullPath(dir)));
        return 0;
    }
    else
    {
        Log.Step(Localizer.Format("cli.migrate.importing", dir));
        var (results, manifest) = MigrationKit.Import(dir, root);
        PrintConfig(results);
        if (manifest is { InstalledIds.Count: > 0 })
            Log.Info(Localizer.Format("cli.migrate.importDone", string.Join(",", manifest.InstalledIds)));
        return 0;
    }
}

void PrintHelp()
{
    Console.WriteLine(Localizer.T("cli.help"));
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
