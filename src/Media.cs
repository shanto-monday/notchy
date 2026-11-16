// Now playing: every app that shows in the Windows media flyout (Spotify, YouTube in a browser, VLC...).
// Several players can be open at once; the UI slides between them.
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Notchy;

sealed class Media
{
    sealed class Info
    {
        public string Id = "", Aumid = "", Title = "", Artist = "", App = "", Art = "", Key = "";
        public bool Playing; public double Pos, Dur; public DateTime PosAt = DateTime.Now;
        public GlobalSystemMediaTransportControlsSession S;
    }

    GlobalSystemMediaTransportControlsSessionManager mgr;
    readonly object gate = new();
    List<Info> list = new();
    readonly Dictionary<string, string> artCache = new(); // track key -> data url
    readonly HashSet<GlobalSystemMediaTransportControlsSession> hooked = new();
    string currentId = "";
    public event Action TrackChanged;

    public bool AnyPlaying { get { lock (gate) return list.Any(i => i.Playing); } }
    Info Cur { get { lock (gate) return list.FirstOrDefault(i => i.Id == currentId) ?? list.FirstOrDefault(); } }

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

    int refreshing;
    volatile bool pendingRefresh;
    public async void Refresh()
    {
        if (mgr == null) return;
        if (Interlocked.Exchange(ref refreshing, 1) == 1) { pendingRefresh = true; return; }
        try
        {
            do
            {
                pendingRefresh = false;
                var sessions = mgr.GetSessions().ToList();
                string curAumid = null;
                try { curAumid = mgr.GetCurrentSession()?.SourceAppUserModelId; } catch { }
                var fresh = new List<Info>();
                var seen = new Dictionary<string, int>();
                string changedId = null;
                foreach (var s in sessions)
                {
                    var i = new Info { S = s };
                    try
                    {
                        if (hooked.Add(s))
                        {
                            s.MediaPropertiesChanged += (_, _) => Refresh();
                            s.PlaybackInfoChanged += (_, _) => Refresh();
                            s.TimelinePropertiesChanged += (_, _) => Refresh();
                        }
                        i.Aumid = s.SourceAppUserModelId ?? "";
                        seen[i.Aumid] = seen.TryGetValue(i.Aumid, out var n) ? n + 1 : 0;
                        i.Id = i.Aumid + "#" + seen[i.Aumid];
                        i.App = Friendly(i.Aumid);
                        i.Playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                        var tl = s.GetTimelineProperties();
                        i.Pos = tl.Position.TotalSeconds; i.Dur = (tl.EndTime - tl.StartTime).TotalSeconds; i.PosAt = DateTime.Now;
                        var p = await s.TryGetMediaPropertiesAsync();
                        i.Title = p.Title ?? ""; i.Artist = string.IsNullOrEmpty(p.Artist) ? p.AlbumArtist ?? "" : p.Artist;
                        i.Key = i.Aumid + "|" + i.Title + "|" + i.Artist;
                        if (!artCache.TryGetValue(i.Key, out var art))
                        {
                            art = await Thumb(p.Thumbnail);
                            if (artCache.Count > 30) artCache.Clear();
                            artCache[i.Key] = art;
                            if (i.Playing && i.Title != "") changedId = i.Id;
                        }
                        i.Art = art;
                    }
                    catch { }
                    if (i.Title != "" || i.Playing) fresh.Add(i);
                }
                hooked.RemoveWhere(h => !sessions.Contains(h));
                lock (gate)
                {
                    list = fresh;
                    // the one Windows calls "current", else the one playing
                    var cur = fresh.FirstOrDefault(i => i.Playing && i.Aumid == curAumid) ?? fresh.FirstOrDefault(i => i.Playing) ?? fresh.FirstOrDefault(i => i.Aumid == curAumid) ?? fresh.FirstOrDefault();
                    currentId = changedId ?? cur?.Id ?? "";
                }
                if (changedId != null) TrackChanged?.Invoke();
            } while (pendingRefresh);
        }
        catch { }
        finally { Interlocked.Exchange(ref refreshing, 0); }
    }

    static string Friendly(string aumid)
    {
        aumid ??= "";
        string a = aumid.ToLowerInvariant();
        if (a.Contains("spotify")) return "Spotify";
        if (a.Contains("brave")) return "Brave";
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
        lock (gate)
        {
            var items = list.Select(i =>
            {
                double pos = i.Pos + (i.Playing ? (DateTime.Now - i.PosAt).TotalSeconds : 0);
                if (i.Dur > 0) pos = Math.Min(pos, i.Dur);
                return new { id = i.Id, title = i.Title, artist = i.Artist, app = i.App, playing = i.Playing, pos, dur = i.Dur, art = i.Art };
            }).ToList();
            return new { current = currentId, items };
        }
    }

    Info Find(string id) { lock (gate) return list.FirstOrDefault(i => i.Id == id) ?? Cur; }

    public async void Do(string id, string action)
    {
        var i = Find(id); if (i?.S == null) return;
        try
        {
            switch (action)
            {
                case "toggle": await i.S.TryTogglePlayPauseAsync(); break;
                case "next": await i.S.TrySkipNextAsync(); break;
                case "prev": await i.S.TrySkipPreviousAsync(); break;
            }
        }
        catch { }
        Refresh();
    }

    public async void Seek(string id, double seconds)
    {
        var i = Find(id); if (i?.S == null) return;
        try { await i.S.TryChangePlaybackPositionAsync((long)(seconds * 10_000_000)); } catch { }
        Refresh();
    }

    // bring the app (or the browser window) that is playing to the front
    public void Focus(string id)
    {
        var i = Find(id); if (i == null) return;
        string a = i.Aumid.ToLowerInvariant();
        string exe = a.EndsWith(".exe") ? Path.GetFileNameWithoutExtension(a) : null;
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string n = p.ProcessName.ToLowerInvariant();
                if (n.Length >= 3 && n != "notchy" && (n == exe || a.Contains(n))) pids.Add((uint)p.Id);
            }
            catch { }
            finally { p.Dispose(); }
        }
        IntPtr best = IntPtr.Zero, any = IntPtr.Zero;
        string t = i.Title.ToLowerInvariant();
        if (pids.Count > 0)
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (!pids.Contains(pid)) return true;
                var sb = new System.Text.StringBuilder(512); GetWindowText(h, sb, 512);
                string title = sb.ToString().ToLowerInvariant();
                if (t.Length > 0 && title.Contains(t)) { best = h; return false; }
                if (any == IntPtr.Zero) any = h;
                return true;
            }, IntPtr.Zero);
        var w = best != IntPtr.Zero ? best : any;
        if (w != IntPtr.Zero)
        {
            if (IsIconic(w)) ShowWindow(w, 9); // SW_RESTORE
            SetForegroundWindow(w);
            return;
        }
        // packaged app with a different process name (Media Player...)
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + i.Aumid) { UseShellExecute = true }); } catch { }
    }

    delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
}
