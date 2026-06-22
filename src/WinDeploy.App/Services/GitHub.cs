using System.Net.Http;
using System.Text.Json;

namespace WinDeploy.App.Services;

/// <summary>Fetches a repo's latest-release assets from the GitHub API, cached per repo for 30 minutes
/// to avoid hammering the (unauthenticated, 60/hr) API on repeated installs / cancels.</summary>
public static class GitHub
{
    private static readonly Dictionary<string, (DateTime At, List<(string Name, string Url)> Assets)> Cache = new();
    private static readonly object Gate = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    public static async Task<List<(string Name, string Url)>> LatestAssetsAsync(string repo)
    {
        lock (Gate)
            if (Cache.TryGetValue(repo, out var c) && DateTime.Now - c.At < Ttl)
                return c.Assets;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WinDeploy");
        var json = await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest");

        var list = new List<(string, string)>();
        using var doc = JsonDocument.Parse(json);
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            list.Add((a.GetProperty("name").GetString() ?? "", a.GetProperty("browser_download_url").GetString() ?? ""));

        lock (Gate) Cache[repo] = (DateTime.Now, list);
        return list;
    }
}
