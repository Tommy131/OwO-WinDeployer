using WinDeploy.Core.Engine;
using WinDeploy.Core.I18n;
using WinDeploy.Core.Models;

namespace WinDeploy.Core;

public enum IssueLevel { Error, Warn }

public sealed record CatalogIssue(IssueLevel Level, string ItemId, string Message);

/// <summary>Static linter for catalog.json — catches the mistakes that break an apply only on a fresh
/// machine: missing method fields, placeholder URLs, dangling depends, missing icons. Run in CI.</summary>
public static class CatalogValidator
{
    public static List<CatalogIssue> Validate(Catalog cat, string repoRoot)
    {
        var issues = new List<CatalogIssue>();
        var knownMethods = new HashSet<string>(new InstallEngine().Methods, StringComparer.OrdinalIgnoreCase);
        var categories = new HashSet<string>(cat.Categories, StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var it in cat.Items)
        {
            var id = string.IsNullOrWhiteSpace(it.Id) ? Localizer.T("engine.validate.noId") : it.Id;

            if (string.IsNullOrWhiteSpace(it.Id)) issues.Add(new(IssueLevel.Error, id, Localizer.T("engine.validate.noIdField")));
            else if (!ids.Add(it.Id)) issues.Add(new(IssueLevel.Error, id, Localizer.T("engine.validate.dupId")));
            if (string.IsNullOrWhiteSpace(it.Name)) issues.Add(new(IssueLevel.Warn, id, Localizer.T("engine.validate.noName")));

            if (!string.IsNullOrWhiteSpace(it.Category) && categories.Count > 0 && !categories.Contains(it.Category))
                issues.Add(new(IssueLevel.Warn, id, Localizer.Format("engine.validate.unknownCategory", it.Category)));

            var m = it.Install.Method;
            if (string.IsNullOrWhiteSpace(m)) issues.Add(new(IssueLevel.Error, id, Localizer.T("engine.validate.noMethod")));
            else if (!knownMethods.Contains(m)) issues.Add(new(IssueLevel.Error, id, Localizer.Format("engine.validate.unknownMethod", m)));
            else ValidateMethod(it, issues, id, repoRoot);

            // Icon: the GUI loads assets/icons/<id>.png (by id), so check that first; also accept the
            // declared icon path if present. Only warn when neither resolves.
            if (!string.IsNullOrWhiteSpace(it.Id))
            {
                var byId = Path.Combine(repoRoot, "assets", "icons", it.Id + ".png");
                var declared = string.IsNullOrWhiteSpace(it.Icon)
                    ? null : Path.Combine(repoRoot, it.Icon.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(byId) && (declared == null || !File.Exists(declared)))
                    issues.Add(new(IssueLevel.Warn, id, Localizer.Format("engine.validate.iconMissing", it.Id)));
            }

            if (it.Config?.Source is { } cs)
            {
                var dir = Path.Combine(repoRoot, cs.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(dir))
                    issues.Add(new(IssueLevel.Warn, id, Localizer.Format("engine.validate.configSourceMissing", cs)));
            }
        }

        // depends resolution (second pass once all ids are known)
        foreach (var it in cat.Items)
            foreach (var dep in it.Depends ?? new List<string>())
                if (!ids.Contains(dep))
                    issues.Add(new(IssueLevel.Error, it.Id, Localizer.Format("engine.validate.dependMissing", dep)));

        return issues;
    }

    private static void ValidateMethod(CatalogItem it, List<CatalogIssue> issues, string id, string repoRoot)
    {
        var ins = it.Install;
        switch (ins.Method)
        {
            case "winget":
                if (string.IsNullOrWhiteSpace(ins.Id)) issues.Add(new(IssueLevel.Error, id, Localizer.T("engine.validate.wingetNoId")));
                break;
            case "winget-bundle":
                if (ins.Ids is not { Count: > 0 }) issues.Add(new(IssueLevel.Error, id, Localizer.T("engine.validate.wingetBundleNoIds")));
                break;
            case "portable":
                if (string.IsNullOrWhiteSpace(ins.Url) || ins.Url == "…")
                    issues.Add(new(IssueLevel.Warn, id, Localizer.T("engine.validate.portableUrlEmpty")));
                if (string.IsNullOrWhiteSpace(ins.ExtractTo))
                    issues.Add(new(IssueLevel.Error, id, Localizer.T("engine.validate.portableNoExtractTo")));
                if (!string.IsNullOrWhiteSpace(ins.Url) && ins.Url != "…" && (string.IsNullOrWhiteSpace(ins.Sha256) || ins.Sha256 == "…"))
                    issues.Add(new(IssueLevel.Warn, id, Localizer.T("engine.validate.portableNoSha256")));
                break;
            case "git":
                if (string.IsNullOrWhiteSpace(ins.Repo)) issues.Add(new(IssueLevel.Error, id, Localizer.T("engine.validate.gitNoRepo")));
                if (string.IsNullOrWhiteSpace(ins.Dest)) issues.Add(new(IssueLevel.Error, id, Localizer.T("engine.validate.gitNoDest")));
                break;
            case "exe":
                if (string.IsNullOrWhiteSpace(ins.Url)) issues.Add(new(IssueLevel.Error, id, Localizer.T("engine.validate.exeNoUrl")));
                break;
            case "local":
                if (string.IsNullOrWhiteSpace(ins.LocalPackage)) issues.Add(new(IssueLevel.Warn, id, Localizer.T("engine.validate.localNoPackage")));
                break;
            case "conda":
                if (string.IsNullOrWhiteSpace(ins.EnvFile)) issues.Add(new(IssueLevel.Error, id, Localizer.T("engine.validate.condaNoEnvFile")));
                else if (!File.Exists(Path.Combine(repoRoot, ins.EnvFile.Replace('/', Path.DirectorySeparatorChar))))
                    issues.Add(new(IssueLevel.Warn, id, Localizer.Format("engine.validate.condaEnvFileMissing", ins.EnvFile)));
                break;
            case "vscode-ext":
                if (string.IsNullOrWhiteSpace(ins.Extensions)) issues.Add(new(IssueLevel.Error, id, Localizer.T("engine.validate.vscodeNoExtensions")));
                break;
            case "script":
                if (string.IsNullOrWhiteSpace(ins.Run)) issues.Add(new(IssueLevel.Error, id, Localizer.T("engine.validate.scriptNoRun")));
                break;
        }
    }
}
