// Now playing: any app that shows in the Windows media flyout (Spotify, YouTube in a browser, VLC...).
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Notchy;

sealed class Media
{
    GlobalSystemMediaTransportControlsSessionManager mgr;
    string lastKey = "", art = "";
    public string Title = "", Artist = "", App = "";
    public bool Playing;
    public double Pos, Dur;
    DateTime posAt = DateTime.Now;
    public event Action TrackChanged;

    public async void Start()
    {
        try
        {
            mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            mgr.CurrentSessionChanged += (_, _) => Refresh();
            mgr.SessionsChanged += (_, _) => Refresh();
            Refresh();
        }
        catch { }
    }

    GlobalSystemMediaTransportControlsSession Session
    {
        get
        {
            try
            {
                var cur = mgr?.GetCurrentSession();
                if (cur != null) return cur;
                foreach (var s in mgr?.GetSessions() ?? (IReadOnlyList<GlobalSystemMediaTransportControlsSession>)Array.Empty<GlobalSystemMediaTransportControlsSession>()) return s;
            }
            catch { }
            return null;
        }
    }

    GlobalSystemMediaTransportControlsSession hooked;
    public async void Refresh()
    {
        var s = Session;
        if (s == null) { Title = Artist = App = art = ""; Playing = false; return; }
        if (!ReferenceEquals(s, hooked))
        {
            hooked = s;
            s.MediaPropertiesChanged += (_, _) => Refresh();
            s.PlaybackInfoChanged += (_, _) => Refresh();
            s.TimelinePropertiesChanged += (_, _) => Refresh();
        }
        try
        {
            var info = s.GetPlaybackInfo();
            Playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var tl = s.GetTimelineProperties();
            Pos = tl.Position.TotalSeconds; Dur = (tl.EndTime - tl.StartTime).TotalSeconds; posAt = DateTime.Now;
            App = Friendly(s.SourceAppUserModelId);
            var p = await s.TryGetMediaPropertiesAsync();
            string key = p.Title + "|" + p.Artist;
            Title = p.Title ?? ""; Artist = p.Artist ?? "";
            if (string.IsNullOrEmpty(Artist)) Artist = p.AlbumArtist ?? "";
            if (key != lastKey)
            {
                lastKey = key;
                art = await Thumb(p.Thumbnail);
                if (Playing && Title != "") TrackChanged?.Invoke();
            }
        }
        catch { }
    }

    static string Friendly(string aumid)
    {
        aumid ??= "";
        string a = aumid.ToLowerInvariant();
        if (a.Contains("spotify")) return "Spotify";
        if (a.Contains("chrome")) return "Chrome";
        if (a.Contains("msedge") || a.Contains("edge")) return "Edge";
        if (a.Contains("firefox")) return "Firefox";
        if (a.Contains("zunemusic") || a.Contains("media player")) return "Media Player";
        if (a.Contains("vlc")) return "VLC";
        int i = aumid.LastIndexOf('!'); if (i > 0) aumid = aumid[..i];
        aumid = aumid.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        int d = aumid.LastIndexOfAny(new[] { '.', '\\', '_' });
        return d >= 0 && d < aumid.Length - 1 ? aumid[(d + 1)..] : aumid;
    }

    static async Task<string> Thumb(IRandomAccessStreamReference r)
    {
        if (r == null) return "";
        try
        {
            using var s = await r.OpenReadAsync();
            using var net = s.AsStreamForRead();
            using var ms = new MemoryStream();
            await net.CopyToAsync(ms);
            if (ms.Length == 0 || ms.Length > 3_000_000) return "";
            string mime = s.ContentType is { Length: > 0 } ct ? ct : "image/png";
            return "data:" + mime + ";base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch { return ""; }
    }

    public object State()
    {
        double pos = Pos + (Playing ? (DateTime.Now - posAt).TotalSeconds : 0);
        if (Dur > 0) pos = Math.Min(pos, Dur);
        return new { title = Title, artist = Artist, app = App, playing = Playing, pos, dur = Dur, art };
    }

    public async void Do(string action)
    {
        var s = Session; if (s == null) return;
        try
        {
            switch (action)
            {
                case "toggle": await s.TryTogglePlayPauseAsync(); break;
                case "next": await s.TrySkipNextAsync(); break;
                case "prev": await s.TrySkipPreviousAsync(); break;
            }
        }
        catch { }
        Refresh();
    }

    public async void Seek(double seconds)
    {
        var s = Session; if (s == null) return;
        try { await s.TryChangePlaybackPositionAsync((long)(seconds * 10_000_000)); } catch { }
        Refresh();
    }
}
