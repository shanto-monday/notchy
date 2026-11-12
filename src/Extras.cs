// Displays (DDC/CI brightness + power per monitor), Bluetooth devices with battery, Windows notifications.
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using Windows.Devices.Enumeration.Pnp;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Notchy;

// ------------------------------------------------------------------ displays
sealed class Displays : IDisposable
{
    sealed class Mon { public string Id = "", Name = "", Device = ""; public bool Primary; public int W, H; public IntPtr Phys; public int Bright = -1, Max = 100; public bool Off; public double Diag; }
    readonly List<Mon> mons = new();
    readonly object gate = new();
    DateTime refreshed = DateTime.MinValue;

    public void Refresh(bool force = false)
    {
        if (!force && (DateTime.Now - refreshed).TotalSeconds < 20) return;
        refreshed = DateTime.Now;
        lock (gate)
        {
            var keepOff = mons.Where(m => m.Off).Select(m => m.Device).ToHashSet();
            foreach (var m in mons) if (m.Phys != IntPtr.Zero) DestroyPhysicalMonitor(m.Phys);
            mons.Clear();
            foreach (var s in Screen.AllScreens.OrderBy(s => s.Bounds.Left))
            {
                var m = new Mon { Device = s.DeviceName, Primary = s.Primary, W = s.Bounds.Width, H = s.Bounds.Height, Off = keepOff.Contains(s.DeviceName) };
                m.Id = m.Device.Replace("\\\\.\\", "");
                m.Name = FriendlyName(s.DeviceName, out m.Diag);
                var hmon = MonitorFromPoint(new POINT { X = s.Bounds.Left + s.Bounds.Width / 2, Y = s.Bounds.Top + s.Bounds.Height / 2 }, 2);
                if (GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out uint n) && n > 0)
                {
                    var arr = new PHYSICAL_MONITOR[n];
                    if (GetPhysicalMonitorsFromHMONITOR(hmon, n, arr))
                    {
                        m.Phys = arr[0].h;
                        for (int i = 1; i < arr.Length; i++) DestroyPhysicalMonitor(arr[i].h);
                        if (GetVCPFeatureAndVCPFeatureReply(m.Phys, 0x10, IntPtr.Zero, out uint cur, out uint max) && max > 0) { m.Bright = (int)cur; m.Max = (int)max; }
                    }
                }
                mons.Add(m);
            }
        }
    }

    public object List()
    {
        Refresh();
        lock (gate)
            return mons.Select((m, i) => new
            {
                idx = i, name = m.Name, primary = m.Primary, res = m.W + " × " + m.H,
                bright = m.Bright < 0 ? -1 : (int)Math.Round(m.Bright * 100.0 / m.Max), ddc = m.Bright >= 0, off = m.Off
            }).ToList();
    }

    // DDC/CI is slow (~40 ms per call): run off the UI thread, newest value wins
    readonly Dictionary<int, int> pending = new();
    bool working;
    public void SetBrightness(int idx, int pct)
    {
        lock (gate)
        {
            if (idx < 0) { for (int i = 0; i < mons.Count; i++) pending[i] = pct; }
            else pending[idx] = pct;
            if (working) return;
            working = true;
        }
        Task.Run(() =>
        {
            while (true)
            {
                int i, v;
                lock (gate)
                {
                    if (pending.Count == 0) { working = false; return; }
                    var kv = pending.First(); pending.Remove(kv.Key); i = kv.Key; v = kv.Value;
                    if (i >= mons.Count || mons[i].Bright < 0) continue;
                }
                var m = mons[i];
                uint val = (uint)Math.Round(Math.Clamp(v, 0, 100) * m.Max / 100.0);
                if (SetVCPFeature(m.Phys, 0x10, val)) m.Bright = (int)val;
            }
        });
    }

    // turn one monitor off/on through DDC/CI power mode (VCP D6: 1 = on, 5 = off)
    public void SetPower(int idx, bool on)
    {
        Mon m;
        lock (gate) { if (idx < 0 || idx >= mons.Count) return; m = mons[idx]; }
        if (m.Phys == IntPtr.Zero) return;
        Task.Run(() => { if (SetVCPFeature(m.Phys, 0xD6, on ? 1u : 5u)) m.Off = !on; });
    }

    // name from the monitor's EDID (registry), e.g. "VY279HGR"
    static string FriendlyName(string device, out double diag)
    {
        diag = 0;
        try
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(device, 0, ref dd, 1)) // EDD_GET_DEVICE_INTERFACE_NAME
            {
                // \\?\DISPLAY#ACI27A1#5&...#{guid}
                var parts = dd.DeviceID.Split('#');
                if (parts.Length > 2)
                {
                    using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + parts[1] + "\\" + parts[2] + @"\Device Parameters");
                    if (k?.GetValue("EDID") is byte[] edid && edid.Length >= 128)
                    {
                        if (edid[21] > 0 && edid[22] > 0) diag = Math.Sqrt(edid[21] * edid[21] + edid[22] * edid[22]) / 2.54; // cm -> inches
                        for (int o = 54; o <= 108; o += 18)
                            if (edid[o] == 0 && edid[o + 1] == 0 && edid[o + 3] == 0xFC)
                                return System.Text.Encoding.ASCII.GetString(edid, o + 5, 13).Split('\n')[0].Trim();
                    }
                    return parts[1];
                }
            }
        }
        catch { }
        return device.Replace("\\\\.\\", "");
    }

    public List<(string name, double diag, int bright, bool off)> Snapshot()
    {
        lock (gate) return mons.Select(m => (m.Name, m.Diag, m.Bright < 0 ? -1 : (int)Math.Round(m.Bright * 100.0 / m.Max), m.Off)).ToList();
    }

    public void Dispose() { lock (gate) foreach (var m in mons) if (m.Phys != IntPtr.Zero) DestroyPhysicalMonitor(m.Phys); }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct PHYSICAL_MONITOR { public IntPtr h; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string d; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE { public int cb; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString; public int StateFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint i, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
    [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PHYSICAL_MONITOR[] a);
    [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitor(IntPtr h);
    [DllImport("dxva2.dll")] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type, out uint cur, out uint max);
    [DllImport("dxva2.dll")] static extern bool SetVCPFeature(IntPtr h, byte code, uint value);
}

// ------------------------------------------------------------------ bluetooth devices + battery
sealed class Bluetooth
{
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"; // DEVPKEY_Bluetooth_Battery
    object cached = new List<object>();
    DateTime at = DateTime.MinValue; bool busy;

    public object Read() { Refresh(false); return cached; }

    public void Refresh(bool force)
    {
        if (busy || (!force && (DateTime.Now - at).TotalSeconds < 12)) return;
        busy = true;
        Task.Run(async () =>
        {
            try
            {
                var nodes = await PnpObject.FindAllAsync(PnpObjectType.Device,
                    new[] { BatteryKey, "System.Devices.ContainerId", "System.Devices.DeviceInstanceId", "System.ItemNameDisplay" },
                    "System.Devices.DeviceInstanceId:~<\"BTH\"");
                var byContainer = new Dictionary<string, int>();
                var names = new Dictionary<string, List<string>>();
                foreach (var n in nodes)
                {
                    if (!(n.Properties.TryGetValue("System.Devices.ContainerId", out var c) && c is Guid g)) continue;
                    string cid = g.ToString("B");
                    int bat = -1;
                    if (n.Properties.TryGetValue(BatteryKey, out var b) && b != null) try { bat = Convert.ToInt32(b); } catch { }
                    byContainer[cid] = Math.Max(byContainer.GetValueOrDefault(cid, -1), bat);
                    if (n.Properties.TryGetValue("System.ItemNameDisplay", out var nn) && nn is string ns && !Generic.IsMatch(ns))
                    { if (!names.ContainsKey(cid)) names[cid] = new(); names[cid].Add(ns); }
                }
                // second opinion on "connected": the Bluetooth APIs themselves
                var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sel in new[] {
                    Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected),
                    Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected) })
                {
                    try
                    {
                        foreach (var d in await DeviceInformation.FindAllAsync(sel, new[] { "System.Devices.Aep.ContainerId" }))
                            if (d.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var lc) && lc is Guid lg) live.Add(lg.ToString("B"));
                    }
                    catch { }
                }
                var list = new List<(string name, bool on, int bat, string kind)>();
                foreach (var (cid, bat) in byContainer)
                {
                    if (cid.Contains("ffffffffffff", StringComparison.OrdinalIgnoreCase)) continue; // this PC
                    try
                    {
                        var c = await PnpObject.CreateFromIdAsync(PnpObjectType.DeviceContainer, cid,
                            new[] { "System.ItemNameDisplay", "System.Devices.Connected", "System.Devices.Category" });
                        // the device's own parts carry the real name; adapters only have generic driver entries
                        if (!names.TryGetValue(cid, out var own) || own.Count == 0) continue;
                        string name = own.OrderBy(x => x.Length).First();
                        bool on = live.Contains(cid) || (c.Properties.TryGetValue("System.Devices.Connected", out var cn) && cn is bool bb && bb);
                        string cat = c.Properties.TryGetValue("System.Devices.Category", out var ct) && ct is string[] arr ? string.Join(",", arr) : "";
                        Dbg.Log("bt " + cid + " " + name + " on=" + on + " bat=" + bat);
                        list.Add((name, on, bat, Kind(name, cat)));
                    }
                    catch { }
                }
                cached = list.OrderByDescending(x => x.on).ThenBy(x => x.name)
                    .Select(x => (object)new { name = x.name, connected = x.on, battery = x.bat, kind = x.kind }).ToList();
                at = DateTime.Now;
            }
            catch { }
            finally { busy = false; }
        });
    }

    static readonly System.Text.RegularExpressions.Regex Generic = new(@"^(Bluetooth Device|Microsoft Bluetooth|Standard Serial|RFCOMM|Spp\d*$|Bluetooth LE Generic|Generic Attribute|Device Information Service|BESOTA|Wireless_Device|Bluetooth Radio)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static string Kind(string name, string cat)
    {
        string s = (name + " " + cat).ToLowerInvariant();
        if (s.Contains("headphone") || s.Contains("headset") || s.Contains("bud") || s.Contains("neckband") || s.Contains("airpod") || s.Contains("audio")) return "headphones";
        if (s.Contains("mouse")) return "mouse";
        if (s.Contains("keyboard")) return "keyboard";
        if (s.Contains("phone") || s.Contains("galaxy") || s.Contains("iphone") || s.Contains("pixel")) return "phone";
        if (s.Contains("speaker")) return "speaker";
        if (s.Contains("controller") || s.Contains("gamepad") || s.Contains("xbox")) return "gamepad";
        if (s.Contains("watch")) return "watch";
        return "device";
    }
}

// ------------------------------------------------------------------ notifications
sealed class Notifs
{
    readonly NotchForm form;
    UserNotificationListener listener;
    readonly HashSet<uint> seen = new();
    readonly Dictionary<string, string> logos = new();
    public sealed class Item { public string Id { get; set; } public string App { get; set; } public string Title { get; set; } public string Body { get; set; } public string Logo { get; set; } public string Ago { get; set; } }
    List<Item> current = new();
    bool started, first = true, busy;
    public bool Allowed;
    public Action<Item> OnNew;
    public Func<string, bool> IsMuted = _ => false;   // apps the user turned off

    public Notifs(NotchForm form) { this.form = form; }

    public async void Start()
    {
        if (started) return; started = true;
        try
        {
            listener = UserNotificationListener.Current;
            var st = await listener.RequestAccessAsync();
            Allowed = st == UserNotificationListenerAccessStatus.Allowed;
        }
        catch { Allowed = false; }
    }

    public object List() => current;

    // poll every couple of seconds (the change event needs a packaged app)
    public async void Poll()
    {
        if (!Allowed || busy) return;
        busy = true;
        try
        {
            var list = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            Dbg.Log("notifs: " + list.Count + " access=" + listener.GetAccessStatus());
            var items = new List<Item>();
            foreach (var n in list.OrderByDescending(n => n.CreationTime))
            {
                string app = "", logo = "";
                try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; logo = await Logo(n); } catch { }
                string title = "", body = "";
                try
                {
                    var bind = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric) ?? n.Notification.Visual.Bindings.FirstOrDefault();
                    var texts = bind?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new();
                    if (texts.Count > 0) title = texts[0];
                    if (texts.Count > 1) body = string.Join("\n", texts.Skip(1));
                }
                catch { }
                if (title == "" && body == "") continue;
                if (IsMuted(app)) { seen.Add(n.Id); continue; }
                var item = new Item { Id = n.Id.ToString(), App = app, Title = title, Body = body, Logo = logo, Ago = Ago(n.CreationTime.LocalDateTime) };
                items.Add(item);
                if (seen.Add(n.Id) && !first) OnNew?.Invoke(item);
            }
            first = false;
            current = items.Take(30).ToList();
            Dbg.Log("parsed " + items.Count + ": " + string.Join(" | ", items.Take(4).Select(i => i.App + ": " + i.Title)));
        }
        catch (Exception ex) { Dbg.Log("notifs error: " + ex.Message); }
        finally { busy = false; }
    }

    async Task<string> Logo(UserNotification n)
    {
        string key = n.AppInfo?.AppUserModelId ?? n.AppInfo?.Id ?? "";
        if (key == "") return "";
        if (logos.TryGetValue(key, out var v)) return v;
        string data = "";
        try
        {
            var r = n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(48, 48));
            using var s = await r.OpenReadAsync();
            using var net = s.AsStreamForRead(); using var ms = new MemoryStream();
            await net.CopyToAsync(ms);
            if (ms.Length > 0 && ms.Length < 500_000) data = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch { }
        return logos[key] = data;
    }

    public void Remove(string id) { try { if (uint.TryParse(id, out var u)) listener?.RemoveNotification(u); } catch { } current = current.Where(o => o.Id != id).ToList(); }
    public void Clear() { try { listener?.ClearNotifications(); } catch { } current = new(); }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "now";
        if (d.TotalHours < 1) return (int)d.TotalMinutes + "m ago";
        if (d.TotalDays < 1) return (int)d.TotalHours + "h ago";
        return (int)d.TotalDays + "d ago";
    }
}

static class Dbg
{
    static readonly bool On = Environment.GetEnvironmentVariable("NOTCHY_DEBUG") == "1";
    public static void Log(string s) { if (On) try { File.AppendAllText(Path.Combine(Program.DataDir, "debug.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + Environment.NewLine); } catch { } }
}
