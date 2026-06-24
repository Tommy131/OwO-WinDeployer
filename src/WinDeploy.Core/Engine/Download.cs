using System.Diagnostics;
using WinDeploy.Core.Engine.Installers;
using WinDeploy.Core.I18n;

namespace WinDeploy.Core.Engine;

/// <summary>Streams an HTTP download to a file while reporting live progress (bytes / % / rate / ETA)
/// through <see cref="EngineContext.Live"/>. Used by the portable / exe installers.</summary>
public static class Download
{
    public static async Task ToFileAsync(string url, string dest, EngineContext ctx, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var f = File.Create(dest);

        var buf = new byte[81920];
        long read = 0, lastMs = 0;
        var sw = Stopwatch.StartNew();
        int n;
        while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
        {
            await f.WriteAsync(buf.AsMemory(0, n), ct);
            read += n;
            var ms = sw.ElapsedMilliseconds;
            if (ms - lastMs >= 300 || (total > 0 && read >= total))
            {
                lastMs = ms;
                ctx.Live(Format(read, total, sw.Elapsed));
            }
        }
        ctx.Live(Format(read, total, sw.Elapsed));
    }

    private static string Format(long read, long total, TimeSpan elapsed)
    {
        var secs = elapsed.TotalSeconds;
        var rate = secs > 0.1 ? read / secs : 0;          // bytes/s
        var rateText = rate > 0 ? $" · {Mb(rate)}/s" : "";
        if (total > 0)
        {
            var pct = read * 100.0 / total;
            var eta = rate > 0 ? (total - read) / rate : 0;
            return Localizer.Format("engine.download.progress", Mb(read), Mb(total), pct.ToString("0"), rateText, Eta(eta));
        }
        return Localizer.Format("engine.download.downloaded", Mb(read) + rateText);
    }

    private static string Mb(double bytes) => bytes >= 1024.0 * 1024 * 1024
        ? $"{bytes / 1024 / 1024 / 1024:0.0} GB"
        : $"{bytes / 1024 / 1024:0.0} MB";

    private static string Eta(double seconds) =>
        seconds >= 60
            ? Localizer.Format("engine.download.minSec", (int)(seconds / 60), (int)(seconds % 60))
            : Localizer.Format("engine.download.sec", ((int)seconds).ToString());
}
