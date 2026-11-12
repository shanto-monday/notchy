// Per-app volume mixer and master volume, using Windows Core Audio (no driver needed).
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Notchy;

sealed class Audio
{
    static Guid Ctx = Guid.Empty;
    readonly Dictionary<string, string> icons = new();

    IMMDevice Device(int flow)
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            en.GetDefaultAudioEndpoint(flow, 1, out var dev); // eMultimedia
            return dev;
        }
        catch { return null; }
    }

    IAudioEndpointVolume Endpoint(int flow)
    {
        var dev = Device(flow); if (dev == null) return null;
        var iid = typeof(IAudioEndpointVolume).GUID;
        dev.Activate(ref iid, 23, IntPtr.Zero, out object o);
        return o as IAudioEndpointVolume;
    }

    public string OutputName
    {
        get
        {
            try
            {
                var dev = Device(0); if (dev == null) return "";
                dev.OpenPropertyStore(0, out var store);
                var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 }; // PKEY_Device_FriendlyName
                store.GetValue(ref key, out var pv);
                string name = Marshal.PtrToStringUni(pv.p) ?? "";
                return name;
            }
            catch { return ""; }
        }
    }

    public (float vol, bool mute) Master()
    {
        try { var ep = Endpoint(0); ep.GetMasterVolumeLevelScalar(out float v); ep.GetMute(out bool m); return (v, m); } catch { return (0, false); }
    }
    public void SetMaster(float v) { try { Endpoint(0)?.SetMasterVolumeLevelScalar(Math.Clamp(v, 0, 1), ref Ctx); } catch { } }
    public void ToggleMasterMute() { try { var ep = Endpoint(0); ep.GetMute(out bool m); ep.SetMute(!m, ref Ctx); } catch { } }

    public bool MicMuted { get { try { Endpoint(1).GetMute(out bool m); return m; } catch { return false; } } }
    public void ToggleMic() { try { var ep = Endpoint(1); ep.GetMute(out bool m); ep.SetMute(!m, ref Ctx); } catch { } }

    IEnumerable<(IAudioSessionControl2 ctl, uint pid)> Sessions()
    {
        var dev = Device(0); if (dev == null) yield break;
        var iid = typeof(IAudioSessionManager2).GUID;
        IAudioSessionEnumerator en = null;
        try
        {
            dev.Activate(ref iid, 23, IntPtr.Zero, out object o);
            ((IAudioSessionManager2)o).GetSessionEnumerator(out en);
        }
        catch { yield break; }
        en.GetCount(out int n);
        for (int i = 0; i < n; i++)
        {
            IAudioSessionControl2 c = null; uint pid = 0;
            try { en.GetSession(i, out c); c.GetState(out int st); if (st == 2) continue; c.GetProcessId(out pid); } catch { continue; } // 2 = expired
            yield return (c, pid);
        }
    }

    public List<object> Apps()
    {
        var list = new List<object>();
        var seen = new HashSet<string>();
        foreach (var (c, pid) in Sessions())
        {
            try
            {
                bool system = c.IsSystemSoundsSession() == 0;
                string name, key;
                if (system) { name = "System sounds"; key = "system"; }
                else
                {
                    using var p = Process.GetProcessById((int)pid);
                    key = p.ProcessName.ToLowerInvariant();
                    name = Pretty(p);
                    if (!icons.ContainsKey(key)) icons[key] = IconOf(p);
                }
                if (!seen.Add(key)) continue; // one row per app (browsers open many sessions)
                var v = (ISimpleAudioVolume)c;
                v.GetMasterVolume(out float vol); v.GetMute(out bool mute);
                list.Add(new { id = key, name, vol, mute, icon = icons.GetValueOrDefault(key, "") });
            }
            catch { }
        }
        return list;
    }

    public List<object> Meters()
    {
        var peaks = new Dictionary<string, float>();
        foreach (var (c, pid) in Sessions())
        {
            try
            {
                string key = c.IsSystemSoundsSession() == 0 ? "system" : ProcName(pid);
                ((IAudioMeterInformation)c).GetPeakValue(out float pk);
                peaks[key] = Math.Max(peaks.GetValueOrDefault(key), pk);
            }
            catch { }
        }
        float master = 0;
        try { var dev = Device(0); var iid = typeof(IAudioMeterInformation).GUID; dev.Activate(ref iid, 23, IntPtr.Zero, out object o); ((IAudioMeterInformation)o).GetPeakValue(out master); } catch { }
        var r = peaks.Select(kv => (object)new { id = kv.Key, peak = kv.Value }).ToList();
        r.Add(new { id = "_master", peak = master });
        return r;
    }

    // set volume / mute for every session of an app
    public void SetApp(string id, float? vol, bool? toggleMute)
    {
        bool? newMute = null;
        foreach (var (c, pid) in Sessions())
        {
            try
            {
                string key = c.IsSystemSoundsSession() == 0 ? "system" : ProcName(pid);
                if (key != id) continue;
                var v = (ISimpleAudioVolume)c;
                if (vol.HasValue) v.SetMasterVolume(Math.Clamp(vol.Value, 0, 1), ref Ctx);
                if (toggleMute == true) { if (newMute == null) { v.GetMute(out bool m); newMute = !m; } v.SetMute(newMute.Value, ref Ctx); }
            }
            catch { }
        }
    }

    static string ProcName(uint pid) { try { using var p = Process.GetProcessById((int)pid); return p.ProcessName.ToLowerInvariant(); } catch { return "pid" + pid; } }

    static string Pretty(Process p)
    {
        try { var d = p.MainModule?.FileVersionInfo.FileDescription; if (!string.IsNullOrWhiteSpace(d)) return d.Trim(); } catch { }
        var n = p.ProcessName; return n.Length > 0 ? char.ToUpper(n[0]) + n[1..] : n;
    }

    static string IconOf(Process p)
    {
        try
        {
            var path = p.MainModule?.FileName; if (path == null) return "";
            using var ico = Icon.ExtractAssociatedIcon(path); if (ico == null) return "";
            using var bmp = ico.ToBitmap(); using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch { return ""; }
    }

    // ---- COM interop (Core Audio)
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        int OpenPropertyStore(int access, out IPropertyStore store);
    }

    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
    [StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public short vt, r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        int GetCount(out int n);
        int GetAt(int i, out PROPERTYKEY k);
        int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr ctl);
        int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr vol);
        int GetSessionEnumerator(out IAudioSessionEnumerator en);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        int GetCount(out int n);
        int GetSession(int i, out IAudioSessionControl2 s);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName(string name, ref Guid ctx);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath(string path, ref Guid ctx);
        int GetGroupingParam(out Guid g);
        int SetGroupingParam(ref Guid g, ref Guid ctx);
        int RegisterAudioSessionNotification(IntPtr n);
        int UnregisterAudioSessionNotification(IntPtr n);
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid ctx);
        int GetMasterVolume(out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        int GetPeakValue(out float peak);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr n);
        int UnregisterControlChangeNotify(IntPtr n);
        int GetChannelCount(out uint n);
        int SetMasterVolumeLevel(float db, ref Guid ctx);
        int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        int GetMasterVolumeLevel(out float db);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        int GetChannelVolumeLevel(uint ch, out float db);
        int GetChannelVolumeLevelScalar(uint ch, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
