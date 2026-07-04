using Windows.Media.Control;

var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
foreach (var s in mgr.GetSessions())
{
    string id = s.SourceAppUserModelId;
    try
    {
        var p = await s.TryGetMediaPropertiesAsync();
        var tl = s.GetTimelineProperties();
        var pb = s.GetPlaybackInfo();
        Console.WriteLine($"== {id} ==");
        Console.WriteLine($"  Title={p?.Title}  Artist={p?.Artist}");
        Console.WriteLine($"  Status={pb?.PlaybackStatus}");
        Console.WriteLine($"  Start={tl.StartTime}  End={tl.EndTime}  Pos={tl.Position}");
        Console.WriteLine($"  MinSeek={tl.MinSeekTime}  MaxSeek={tl.MaxSeekTime}  LastUpdated={tl.LastUpdatedTime}");
    }
    catch (Exception ex) { Console.WriteLine($"  {id}: {ex.Message}"); }
}
var cur = mgr.GetCurrentSession();
Console.WriteLine($"current = {cur?.SourceAppUserModelId ?? "(none)"}");
