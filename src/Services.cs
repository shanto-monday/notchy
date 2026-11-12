// Everything the notch shows: system monitor, clipboard history, shelf, notes, timer, AI usage, quick toggles.
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Notchy;

sealed class Services : IDisposable
{
    readonly NotchForm form;
    public readonly Media Media = new();
    public readonly Audio Audio = new();
    public readonly SysMon Sys = new();
    public readonly Clips Clips;
    public readonly Shelf Shelf = new();
    public readonly AiUsage Ai = new();
    public readonly Prefs Prefs = Prefs.Load();
    string notes;
    bool keepAwake;
    // timer: countdown or stopwatch
    DateTime? timerEnd, swStart; double timerLeft, swElapsed; double timerTotal;

    static string NotesPath => Path.Combine(Program.DataDir, "notes.txt");

    public Services(NotchForm form)
    {
        this.form = form;
        Clips = new Clips(form);
        notes = File.Exists(NotesPath) ? File.ReadAllText(NotesPath) : "";
        Media.TrackChanged += () => { if (Prefs.PeekOnSong) form.BeginInvoke(form.Peek); };
        Media.Start();
    }

    public object InitState() => new
    {
        type = "init",
        version = Program.Version,
        notes,
        hasGpu = Sys.HasGpu,
        prefs = new { peekOnSong = Prefs.PeekOnSong, startWithWindows = SafeStart() }
    };

    static bool SafeStart() { try { return Program.StartWithWindows; } catch { return false; } }

    public object LiveState(string tab)
    {
        var (mv, mm) = Audio.Master();
        bool sys = tab is "home" or "system";
        return new
        {
            type = "live",
            media = Media.State(),
            timer = TimerState(),
            master = new { vol = mv, mute = mm, output = tab == "mixer" ? Audio.OutputName : "" },
            sys = sys ? Sys.Read(tab == "system") : null,
            apps = tab == "mixer" ? Audio.Apps() : null,
            clips = tab == "clipboard" ? Clips.List() : null,
            shelf = tab is "shelf" or "home" ? Shelf.List() : null,
            ai = tab is "ai" or "home" ? Ai.Read() : null,
            toggles = new { keepAwake, mic = Audio.MicMuted, dark = DarkMode }
        };
    }

    object TimerState()
    {
        double left = timerEnd.HasValue ? Math.Max(0, (timerEnd.Value - DateTime.Now).TotalSeconds) : timerLeft;
        double sw = swElapsed + (swStart.HasValue ? (DateTime.Now - swStart.Value).TotalSeconds : 0);
        return new { running = timerEnd.HasValue, left, total = timerTotal, sw, swRunning = swStart.HasValue };
    }

    public void Tick()
    {
        if (timerEnd.HasValue && DateTime.Now >= timerEnd.Value)
        {
            timerEnd = null; timerLeft = 0;
            System.Media.SystemSounds.Asterisk.Play();
            form.Notify?.Invoke("Time's up", "Your " + Fmt(timerTotal) + " timer is done.");
            form.Peek();
            form.Send(new { type = "timerDone" });
        }
    }

    static string Fmt(double s) { var t = TimeSpan.FromSeconds(Math.Round(s)); return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.Minutes > 0 ? $"{t.Minutes} min" : $"{t.Seconds} s"; }

    public void Handle(string cmd, JsonNode m)
    {
        switch (cmd)
        {
            case "media": Media.Do((string)m["action"]); break;
            case "seek": Media.Seek((double)m["pos"]); break;
            case "master": Audio.SetMaster((float)(double)m["vol"]); break;
            case "masterMute": Audio.ToggleMasterMute(); break;
            case "appVol": Audio.SetApp((string)m["id"], (float)(double)m["vol"], null); break;
            case "appMute": Audio.SetApp((string)m["id"], null, true); break;
            case "clipPin": Clips.Pin((string)m["id"]); break;
            case "clipDel": Clips.Delete((string)m["id"]); break;
            case "clipClear": Clips.Clear(); break;
            case "shelfOpen": Shelf.Open((string)m["path"], false); break;
            case "shelfReveal": Shelf.Open((string)m["path"], true); break;
            case "shelfCopy": Shelf.Copy((string)m["path"]); break;
            case "shelfRemove": Shelf.Remove((string)m["path"]); break;
            case "shelfClear": Shelf.Clear(); break;
            case "notes":
                notes = (string)m["text"] ?? "";
                try { File.WriteAllText(NotesPath, notes); } catch { }
                break;
            case "timerStart":
                {
                    double secs = (double?)m["secs"] ?? timerLeft;
                    if (secs <= 0) break;
                    if ((double?)m["secs"] != null) timerTotal = secs;
                    timerEnd = DateTime.Now.AddSeconds(secs); break;
                }
            case "timerPause": if (timerEnd.HasValue) { timerLeft = Math.Max(0, (timerEnd.Value - DateTime.Now).TotalSeconds); timerEnd = null; } break;
            case "timerReset": timerEnd = null; timerLeft = 0; timerTotal = 0; break;
            case "swToggle":
                if (swStart.HasValue) { swElapsed += (DateTime.Now - swStart.Value).TotalSeconds; swStart = null; } else swStart = DateTime.Now;
                break;
            case "swReset": swStart = null; swElapsed = 0; break;
            case "toggle": Toggle((string)m["name"]); break;
            case "pref":
                {
                    string name = (string)m["name"]; bool on = (bool)m["on"];
                    if (name == "peekOnSong") { Prefs.PeekOnSong = on; Prefs.Save(); }
                    if (name == "startWithWindows") try { Program.StartWithWindows = on; } catch { }
                    break;
                }
            case "aiRefresh": Ai.Refresh(force: true); break;
            case "openUrl": try { Process.Start(new ProcessStartInfo((string)m["url"]) { UseShellExecute = true }); } catch { } break;
        }
    }

    // ---- quick toggles
    void Toggle(string name)
    {
        switch (name)
        {
            case "lock": form.Collapse(); LockWorkStation(); break;
            case "screenOff": form.Collapse(); Task.Delay(500).ContinueWith(_ => SendMessage((IntPtr)0xFFFF, 0x112, (IntPtr)0xF170, (IntPtr)2)); break;
            case "keepAwake":
                keepAwake = !keepAwake;
                SetThreadExecutionState(keepAwake ? 0x80000000u | 0x1u | 0x2u : 0x80000000u); // CONTINUOUS | SYSTEM | DISPLAY
                break;
            case "mic": Audio.ToggleMic(); break;
            case "dark": DarkMode = !DarkMode; break;
            case "recycle": SHEmptyRecycleBin(IntPtr.Zero, null, 0); break; // Windows asks to confirm
            case "desktop": form.Collapse(); Task.Delay(250).ContinueWith(_ => { keybd_event(0x5B, 0, 0, 0); keybd_event(0x44, 0, 0, 0); keybd_event(0x44, 0, 2, 0); keybd_event(0x5B, 0, 2, 0); }); break;
            case "screenshot": form.Collapse(); Task.Delay(300).ContinueWith(_ => { keybd_event(0x5B, 0, 0, 0); keybd_event(0x10, 0, 0, 0); keybd_event(0x53, 0, 0, 0); keybd_event(0x53, 0, 2, 0); keybd_event(0x10, 0, 2, 0); keybd_event(0x5B, 0, 2, 0); }); break;
        }
    }

    const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    static bool DarkMode
    {
        get { try { using var k = Registry.CurrentUser.OpenSubKey(Personalize); return (int)(k?.GetValue("AppsUseLightTheme") ?? 1) == 0; } catch { return false; } }
        set
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(Personalize);
                k.SetValue("AppsUseLightTheme", value ? 0 : 1, RegistryValueKind.DWord);
                k.SetValue("SystemUsesLightTheme", value ? 0 : 1, RegistryValueKind.DWord);
                SendMessageTimeout((IntPtr)0xFFFF, 0x1A, IntPtr.Zero, "ImmersiveColorSet", 2, 200, out _);
            }
            catch { }
        }
    }

    public void Dispose() { Clips.Dispose(); if (keepAwake) SetThreadExecutionState(0x80000000u); }

    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, string l, int flags, int timeout, out IntPtr result);
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr h, string root, int flags);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, int flags, int extra);
}

sealed class Prefs
{
    public bool PeekOnSong { get; set; } = true;
    static string P => Path.Combine(Program.DataDir, "prefs.json");
    public static Prefs Load() { try { return JsonSerializer.Deserialize<Prefs>(File.ReadAllText(P)) ?? new(); } catch { return new(); } }
    public void Save() { try { File.WriteAllText(P, JsonSerializer.Serialize(this)); } catch { } }
}

// ------------------------------------------------------------------ system monitor
sealed class SysMon
{
    long idle0, kern0, user0;
    long rx0, tx0; DateTime netAt = DateTime.MinValue;
    readonly Queue<float> cpuHist = new(), gpuHist = new(), netHist = new();
    IntPtr nvDev; bool nvTried;
    public bool HasGpu { get { InitNv(); return nvDev != IntPtr.Zero; } }
    string gpuName = "";

    void InitNv()
    {
        if (nvTried) return; nvTried = true;
        try
        {
            if (nvmlInit_v2() != 0) return;
            if (nvmlDeviceGetHandleByIndex_v2(0, out nvDev) != 0) { nvDev = IntPtr.Zero; return; }
            var sb = new System.Text.StringBuilder(96);
            if (nvmlDeviceGetName(nvDev, sb, 96) == 0) gpuName = sb.ToString().Replace("NVIDIA ", "").Replace("GeForce ", "");
        }
        catch { nvDev = IntPtr.Zero; }
    }

    static void Push(Queue<float> q, float v) { q.Enqueue(v); while (q.Count > 60) q.Dequeue(); }

    public object Read(bool detail)
    {
        // CPU
        GetSystemTimes(out long idle, out long kern, out long user);
        long di = idle - idle0, dk = kern - kern0, du = user - user0;
        idle0 = idle; kern0 = kern; user0 = user;
        float cpu = dk + du > 0 ? Math.Clamp(100f * (dk + du - di) / (dk + du), 0, 100) : 0;
        // RAM
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref ms);
        double ramUsed = (ms.ullTotalPhys - ms.ullAvailPhys) / 1073741824.0, ramTotal = ms.ullTotalPhys / 1073741824.0;
        // GPU (NVIDIA via NVML, comes with the driver)
        InitNv();
        float gpu = -1, gtemp = -1; double vUsed = 0, vTotal = 0;
        if (nvDev != IntPtr.Zero)
        {
            try
            {
                if (nvmlDeviceGetUtilizationRates(nvDev, out var u) == 0) gpu = u.gpu;
                if (nvmlDeviceGetTemperature(nvDev, 0, out uint t) == 0) gtemp = t;
                if (nvmlDeviceGetMemoryInfo(nvDev, out var mi) == 0) { vUsed = mi.used / 1073741824.0; vTotal = mi.total / 1073741824.0; }
            }
            catch { }
        }
        // network
        long rx = 0, tx = 0;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                if (n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                { var st = n.GetIPStatistics(); rx += st.BytesReceived; tx += st.BytesSent; }
        }
        catch { }
        double secs = Math.Max(0.2, (DateTime.Now - netAt).TotalSeconds);
        double down = netAt == DateTime.MinValue ? 0 : Math.Max(0, (rx - rx0) / secs), up = netAt == DateTime.MinValue ? 0 : Math.Max(0, (tx - tx0) / secs);
        rx0 = rx; tx0 = tx; netAt = DateTime.Now;
        Push(cpuHist, cpu); Push(gpuHist, Math.Max(0, gpu)); Push(netHist, (float)down);
        // disk + battery
        double diskFree = 0, diskTotal = 0;
        try { var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!); diskFree = d.AvailableFreeSpace / 1073741824.0; diskTotal = d.TotalSize / 1073741824.0; } catch { }
        var ps = SystemInformation.PowerStatus;
        bool hasBattery = (ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryLifePercent <= 1;
        object top = null;
        if (detail)
        {
            try
            {
                top = Process.GetProcesses()
                    .Select(p => { try { return (name: p.ProcessName, mem: p.WorkingSet64); } catch { return (name: "", mem: 0L); } finally { p.Dispose(); } })
                    .Where(x => x.name != "" && x.name != "Idle" && x.name != "Memory Compression")
                    .GroupBy(x => x.name).Select(g => new { name = g.Key, mem = g.Sum(x => x.mem) / 1048576.0 })
                    .OrderByDescending(x => x.mem).Take(5).ToList();
            }
            catch { }
        }
        return new
        {
            cpu, ramUsed, ramTotal, gpu, gpuTemp = gtemp, gpuName, vramUsed = vUsed, vramTotal = vTotal,
            down, up, diskFree, diskTotal,
            battery = hasBattery ? ps.BatteryLifePercent * 100 : -1, charging = ps.PowerLineStatus == PowerLineStatus.Online,
            cpuHist = detail ? cpuHist.ToArray() : null, gpuHist = detail ? gpuHist.ToArray() : null, netHist = detail ? netHist.ToArray() : null,
            top
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [StructLayout(LayoutKind.Sequential)] struct NvUtil { public uint gpu, memory; }
    [StructLayout(LayoutKind.Sequential)] struct NvMem { public ulong total, free, used; }
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("nvml.dll")] static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out IntPtr dev);
    [DllImport("nvml.dll", CharSet = CharSet.Ansi)] static extern int nvmlDeviceGetName(IntPtr dev, System.Text.StringBuilder name, uint len);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(IntPtr dev, out NvUtil u);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr dev, int sensor, out uint t);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetMemoryInfo(IntPtr dev, out NvMem m);
}

// ------------------------------------------------------------------ clipboard history
sealed class Clips : NativeWindow, IDisposable
{
    sealed class Item { public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10]; public string Kind { get; set; } = "text"; public string Text { get; set; } = ""; public string[] Files { get; set; } public string Thumb { get; set; } = ""; public bool Pinned { get; set; } public DateTime At { get; set; } = DateTime.Now; }
    readonly List<Item> items;
    readonly Dictionary<string, Image> images = new(); // full images kept in memory only
    bool ignoreNext;
    static string P => Path.Combine(Program.DataDir, "clipboard.json");

    public Clips(Form owner)
    {
        try { items = JsonSerializer.Deserialize<List<Item>>(File.ReadAllText(P)) ?? new(); } catch { items = new(); }
        items.RemoveAll(i => i.Kind == "image"); // images are not saved to disk
        CreateHandle(new CreateParams { Parent = (IntPtr)(-3) }); // message-only window
        AddClipboardFormatListener(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x031D) Capture(); // WM_CLIPBOARDUPDATE
        base.WndProc(ref m);
    }

    void Capture()
    {
        if (ignoreNext) { ignoreNext = false; return; }
        try
        {
            var d = Clipboard.GetDataObject();
            if (d == null) return;
            // password managers mark secrets with these formats: never store them
            if (d.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || d.GetDataPresent("Clipboard Viewer Ignore")) return;
            Item it = null;
            if (d.GetDataPresent(DataFormats.FileDrop) && d.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                it = new Item { Kind = "files", Files = files, Text = string.Join("\n", files.Select(Path.GetFileName)) };
            else if (Clipboard.ContainsText())
            {
                string t = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(t) || t.Length > 100_000) return;
                it = new Item { Text = t };
            }
            else if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage(); if (img == null) return;
                it = new Item { Kind = "image", Text = img.Width + " × " + img.Height, Thumb = ThumbOf(img) };
                images[it.Id] = img;
            }
            if (it == null) return;
            var same = items.FirstOrDefault(x => x.Kind == it.Kind && x.Text == it.Text && it.Kind != "image");
            if (same != null) { items.Remove(same); it.Pinned = same.Pinned; it.Id = same.Id; }
            items.Insert(0, it);
            // keep pinned + last 150
            var extra = items.Where(x => !x.Pinned).Skip(150).ToList();
            foreach (var x in extra) { items.Remove(x); images.Remove(x.Id); }
            Save();
        }
        catch { }
    }

    static string ThumbOf(Image img)
    {
        double k = Math.Min(1, 160.0 / Math.Max(img.Width, img.Height));
        using var b = new Bitmap(img, Math.Max(1, (int)(img.Width * k)), Math.Max(1, (int)(img.Height * k)));
        using var ms = new MemoryStream(); b.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }

    void Save() { try { File.WriteAllText(P, JsonSerializer.Serialize(items.Where(i => i.Kind != "image").ToList())); } catch { } }

    public object List() => items.OrderByDescending(i => i.Pinned).Take(80).Select(i => new
    {
        id = i.Id, kind = i.Kind, text = i.Text.Length > 400 ? i.Text[..400] : i.Text, thumb = i.Thumb, pinned = i.Pinned,
        ago = Ago(i.At)
    }).ToList();

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "now";
        if (d.TotalHours < 1) return (int)d.TotalMinutes + "m";
        if (d.TotalDays < 1) return (int)d.TotalHours + "h";
        return (int)d.TotalDays + "d";
    }

    // put an item on the clipboard so it can be pasted
    public string Prepare(string id)
    {
        var it = items.FirstOrDefault(i => i.Id == id); if (it == null) return "";
        ignoreNext = true;
        try
        {
            if (it.Kind == "files") { var sc = new System.Collections.Specialized.StringCollection(); sc.AddRange(it.Files); Clipboard.SetFileDropList(sc); }
            else if (it.Kind == "image" && images.TryGetValue(id, out var img)) Clipboard.SetImage(img);
            else Clipboard.SetText(it.Text);
        }
        catch { ignoreNext = false; }
        items.Remove(it); it.At = DateTime.Now; items.Insert(0, it); Save();
        return it.Text;
    }

    public void Pin(string id) { var it = items.FirstOrDefault(i => i.Id == id); if (it != null) { it.Pinned = !it.Pinned; Save(); } }
    public void Delete(string id) { items.RemoveAll(i => i.Id == id); images.Remove(id); Save(); }
    public void Clear() { items.RemoveAll(i => !i.Pinned); images.Clear(); Save(); }

    public void Dispose() { try { RemoveClipboardFormatListener(Handle); DestroyHandle(); } catch { } }

    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr h);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr h);
}

// ------------------------------------------------------------------ shelf (park files)
sealed class Shelf
{
    readonly List<string> paths;
    readonly Dictionary<string, string> icons = new();
    static string P => Path.Combine(Program.DataDir, "shelf.json");
    public Shelf() { try { paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(P)) ?? new(); } catch { paths = new(); } }
    void Save() { try { File.WriteAllText(P, JsonSerializer.Serialize(paths)); } catch { } }

    public void Add(IEnumerable<string> list) { foreach (var p in list) if (!paths.Contains(p, StringComparer.OrdinalIgnoreCase)) paths.Add(p); Save(); }
    public void Remove(string p) { paths.Remove(p); Save(); }
    public void Clear() { paths.Clear(); Save(); }

    public object List()
    {
        paths.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p));
        return paths.Select(p => new { path = p, name = Path.GetFileName(p.TrimEnd('\\')), dir = Directory.Exists(p), size = SizeOf(p), icon = IconOf(p) }).ToList();
    }

    static string SizeOf(string p)
    {
        try { if (Directory.Exists(p)) return "Folder"; long b = new FileInfo(p).Length; return b > 1 << 30 ? (b / 1073741824.0).ToString("0.0") + " GB" : b > 1 << 20 ? (b / 1048576.0).ToString("0.0") + " MB" : Math.Max(1, b / 1024) + " KB"; }
        catch { return ""; }
    }

    string IconOf(string p)
    {
        string ext = Directory.Exists(p) ? "<dir>" : Path.GetExtension(p).ToLowerInvariant();
        bool own = ext is ".exe" or ".lnk" or ".ico";
        string key = own ? p : ext;
        if (icons.TryGetValue(key, out var v)) return v;
        string data = "";
        try
        {
            var sfi = new SHFILEINFO();
            SHGetFileInfo(p, 0, ref sfi, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100); // SHGFI_ICON (large)
            if (sfi.hIcon != IntPtr.Zero)
            {
                using (var ico = Icon.FromHandle(sfi.hIcon)) using (var bmp = ico.ToBitmap()) using (var ms = new MemoryStream())
                { bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); data = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray()); }
                DestroyIcon(sfi.hIcon);
            }
        }
        catch { }
        return icons[key] = data;
    }

    public void Open(string p, bool reveal)
    {
        try
        {
            if (reveal) Process.Start("explorer.exe", "/select,\"" + p + "\"");
            else Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
        }
        catch { }
    }

    public void Copy(string p)
    {
        try { var sc = new System.Collections.Specialized.StringCollection { p }; Clipboard.SetFileDropList(sc); } catch { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO { public IntPtr hIcon; public int iIcon; public uint attr; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string name; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string type; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO info, uint size, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}

// ------------------------------------------------------------------ AI usage (from local Claude Code + Codex logs)
sealed class AiUsage
{
    object cached = new { claude = (object)null, codex = (object)null };
    DateTime at = DateTime.MinValue;
    bool busy;
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public object Read() { Refresh(false); return cached; }

    public void Refresh(bool force)
    {
        if (busy || (!force && (DateTime.Now - at).TotalSeconds < 60)) return;
        busy = true;
        Task.Run(() =>
        {
            try { cached = new { claude = Claude(), codex = Codex() }; at = DateTime.Now; }
            catch { }
            finally { busy = false; }
        });
    }

    static readonly Regex MsgId = new("\"id\":\"(msg_[A-Za-z0-9]+)\"");
    static readonly Regex Ts = new("\"timestamp\":\"([^\"]+)\"");
    static long Num(string line, string key)
    {
        int i = line.IndexOf("\"" + key + "\":", StringComparison.Ordinal); if (i < 0) return 0;
        i += key.Length + 3; long v = 0;
        while (i < line.Length && char.IsDigit(line[i])) v = v * 10 + (line[i++] - '0');
        return v;
    }

    static object Claude()
    {
        string root = Path.Combine(Home, ".claude", "projects");
        if (!Directory.Exists(root)) return null;
        var since = DateTime.Now.Date; var from = DateTime.Now.AddHours(-10); // look back far enough to find the current 5-hour window
        if (from > since) from = since;
        long outT = 0, inT = 0, cacheT = 0, msgs = 0;
        var seen = new HashSet<string>();
        var recent = new List<(DateTime t, long o)>();
        foreach (var f in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTime(f) < from) continue;
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs);
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (!line.Contains("\"usage\"") || !line.Contains("\"assistant\"")) continue;
                    var tm = Ts.Match(line); if (!tm.Success || !DateTime.TryParse(tm.Groups[1].Value, out var t)) continue;
                    t = t.ToLocalTime(); if (t < from) continue;
                    var id = MsgId.Match(line); if (id.Success && !seen.Add(id.Groups[1].Value)) continue; // a message is logged once per block
                    long o = Num(line, "output_tokens");
                    recent.Add((t, o));
                    if (t < since) continue;
                    outT += o; inT += Num(line, "input_tokens") + Num(line, "cache_creation_input_tokens"); cacheT += Num(line, "cache_read_input_tokens"); msgs++;
                }
            }
            catch { }
        }
        // Claude plans count usage in 5-hour windows: a window starts with the first message after the last one ended
        recent.Sort((x, y) => x.t.CompareTo(y.t));
        DateTime? start = null;
        foreach (var x in recent) if (start == null || x.t >= start.Value.AddHours(5)) start = x.t;
        long out5 = 0, msgs5 = 0; double resetsIn = 0;
        if (start.HasValue && DateTime.Now < start.Value.AddHours(5))
        {
            foreach (var x in recent) if (x.t >= start.Value) { out5 += x.o; msgs5++; }
            resetsIn = (start.Value.AddHours(5) - DateTime.Now).TotalMinutes;
        }
        return new { outTokens = outT, inTokens = inT, cacheTokens = cacheT, msgs, out5, msgs5, resetsIn };
    }

    static object Codex()
    {
        string root = Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Home, ".codex"), "sessions");
        if (!Directory.Exists(root)) return null;
        var files = new DirectoryInfo(root).EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
            .Where(f => f.LastWriteTime > DateTime.Now.AddDays(-8)).OrderByDescending(f => f.LastWriteTime).ToList();
        JsonNode limits = null; long today = 0; int sessions = 0;
        foreach (var f in files)
        {
            string lastLimits = null, lastTotal = null;
            try
            {
                using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs);
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (!line.Contains("token_count")) continue;
                    if (line.Contains("\"rate_limits\":{")) lastLimits = line;
                    if (line.Contains("\"total_token_usage\"")) lastTotal = line;
                }
            }
            catch { }
            if (limits == null && lastLimits != null)
            {
                try { limits = FindKey(JsonNode.Parse(lastLimits), "rate_limits"); } catch { }
            }
            if (f.LastWriteTime >= DateTime.Now.Date && lastTotal != null)
            {
                sessions++;
                try { var tu = FindKey(JsonNode.Parse(lastTotal), "total_token_usage"); today += (long?)tu?["total_tokens"] ?? 0; } catch { }
            }
        }
        object W(JsonNode w)
        {
            if (w == null) return null;
            double used = (double?)w["used_percent"] ?? 0;
            double left = w["resets_at"] != null ? (DateTimeOffset.FromUnixTimeSeconds((long)w["resets_at"]).LocalDateTime - DateTime.Now).TotalMinutes : ((double?)w["resets_in_seconds"] ?? 0) / 60;
            if (left <= 0) { used = 0; left = (double?)w["window_minutes"] ?? 0; } // window ended: fresh start
            return new { used, windowMin = (double?)w["window_minutes"] ?? 0, resetsIn = left };
        }
        return new { primary = W(limits?["primary"]), weekly = W(limits?["secondary"]), today, sessions };
    }

    static JsonNode FindKey(JsonNode n, string key)
    {
        if (n is JsonObject o)
        {
            foreach (var kv in o) { if (kv.Key == key) return kv.Value; var r = FindKey(kv.Value, key); if (r != null) return r; }
        }
        else if (n is JsonArray a) foreach (var x in a) { var r = FindKey(x, key); if (r != null) return r; }
        return null;
    }
}
