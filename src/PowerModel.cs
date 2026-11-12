// Whole-setup power: CPU and GPU are measured; the rest is estimated from this PC's real parts
// (drives and how busy they are, RAM type and sticks, monitor size and brightness, power supply efficiency).
using System.Diagnostics;
using System.Management;
using System.Text.Json;

namespace Notchy;

sealed class PowerModel
{
    sealed class Disk { public string Num = "", Name = ""; public bool Hdd, Nvme; public PerformanceCounter Idle; }
    readonly List<Disk> disks = new();
    int ramSticks = 1; bool ddr5;
    bool ready;
    readonly object gate = new();
    readonly Queue<float> hist = new();
    double whToday; string day = DateTime.Today.ToString("yyyy-MM-dd");
    DateTime lastSample = DateTime.MinValue;
    static string EnergyPath => Path.Combine(Program.DataDir, "energy.json");

    public PowerModel()
    {
        try { var j = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(EnergyPath)); if (j != null && j.TryGetValue(day, out var v)) whToday = v; } catch { }
        Task.Run(Detect);
    }

    void Detect()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DeviceId, FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk");
            var cat = new PerformanceCounterCategory("PhysicalDisk");
            var inst = cat.GetInstanceNames().Where(i => i != "_Total").ToList();
            foreach (ManagementObject o in s.Get())
            {
                var d = new Disk { Num = Convert.ToString(o["DeviceId"]) ?? "", Name = Convert.ToString(o["FriendlyName"]) ?? "Drive" };
                int media = Convert.ToInt32(o["MediaType"] ?? 0), bus = Convert.ToInt32(o["BusType"] ?? 0);
                d.Hdd = media == 3; d.Nvme = bus == 17;
                string pi = inst.FirstOrDefault(i => i.Split(' ')[0] == d.Num);
                if (pi != null) { d.Idle = new PerformanceCounter("PhysicalDisk", "% Idle Time", pi, true); d.Idle.NextValue(); }
                disks.Add(d);
            }
        }
        catch { }
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Capacity, SMBIOSMemoryType FROM Win32_PhysicalMemory");
            int n = 0; foreach (ManagementObject o in s.Get()) { n++; if (Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0) >= 34) ddr5 = true; }
            if (n > 0) ramSticks = n;
        }
        catch { }
        ready = true;
    }

    public sealed record Part(string Name, double W, bool Measured, string Note);

    // cpuW / gpuW measured (W, -1 = unknown); monitors from Displays (diag inches, brightness %, off)
    public (List<Part> parts, double pc, double screens, double total) Estimate(float cpuW, float gpuW, float cpuPct, IEnumerable<(string name, double diag, int bright, bool off)> monitors)
    {
        var parts = new List<Part>();
        double cpu = cpuW >= 0 ? cpuW : 15 + cpuPct * 0.9;
        parts.Add(new Part("Processor", cpu, cpuW >= 0, ""));
        if (gpuW >= 0) parts.Add(new Part("Graphics card", gpuW, true, ""));
        parts.Add(new Part("Motherboard & chipset", 22 + cpu * 0.08, false, "board, chipset, power stages"));
        parts.Add(new Part("Memory", ramSticks * (ddr5 ? 4.5 : 3.0), false, ramSticks + (ramSticks == 1 ? " stick " : " sticks ") + (ddr5 ? "DDR5" : "DDR4")));
        double drives = 0; int busyMax = 0;
        lock (gate)
            foreach (var d in disks)
            {
                double busy = 0;
                try { if (d.Idle != null) busy = Math.Clamp(100 - d.Idle.NextValue(), 0, 100) / 100.0; } catch { }
                busyMax = Math.Max(busyMax, (int)(busy * 100));
                drives += d.Hdd ? 4.5 + 2.0 * busy : d.Nvme ? 1.0 + 4.0 * busy : 0.6 + 2.4 * busy;
            }
        if (!ready || disks.Count == 0) drives = 4;
        int hdd = disks.Count(d => d.Hdd), ssd = disks.Count - hdd;
        parts.Add(new Part("Drives", drives, false, disks.Count == 0 ? "" : (ssd > 0 ? ssd + " SSD" : "") + (ssd > 0 && hdd > 0 ? " + " : "") + (hdd > 0 ? hdd + " hard drive" : "")));
        parts.Add(new Part("Fans, cooler, USB, lights", 10, false, ""));
        double dc = parts.Sum(p => p.W);
        double eff = dc < 120 ? 0.86 : dc < 450 ? 0.90 : 0.88;  // typical 80 Plus Gold curve
        double loss = dc / eff - dc;
        parts.Add(new Part("Power supply loss", loss, false, "about " + Math.Round(eff * 100) + "% efficient"));
        double pc = dc + loss;
        double screens = 0;
        foreach (var m in monitors)
        {
            double k = Math.Pow((m.diag > 10 ? m.diag : 24) / 27.0, 2);
            double b = m.bright >= 0 ? m.bright : 50;
            double w = m.off ? 0.5 : k * (7 + 0.23 * b);
            screens += w;
            parts.Add(new Part(m.name, w, false, m.off ? "off" : "monitor · " + (m.bright >= 0 ? m.bright + "% brightness" : "brightness unknown")));
        }
        double total = pc + screens;
        Track(total);
        return (parts, pc, screens, total);
    }

    void Track(double total)
    {
        var now = DateTime.Now;
        string today = DateTime.Today.ToString("yyyy-MM-dd");
        if (today != day) { day = today; whToday = 0; }
        if (lastSample != DateTime.MinValue)
        {
            double h = (now - lastSample).TotalHours;
            if (h > 0 && h < 0.05) whToday += total * h; // ignore gaps (sleep)
        }
        lastSample = now;
        hist.Enqueue((float)total); while (hist.Count > 60) hist.Dequeue();
        if (now.Second % 30 == 0) Save();
    }

    public void Save() { try { File.WriteAllText(EnergyPath, JsonSerializer.Serialize(new Dictionary<string, double> { [day] = whToday })); } catch { } }
    public double KwhToday => whToday / 1000.0;
    public float[] History => hist.ToArray();
}
