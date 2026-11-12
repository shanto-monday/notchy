// Auto update from GitHub Releases: github.com/<repo>/releases/latest -> Notchy.exe
using System.Diagnostics;

namespace Notchy;

static class Updater
{
    public static string Status = "";
    public static string ReadyTag;
    static string Exe => Environment.ProcessPath!;
    static string NewExe => Exe + ".new";
    static string OldExe => Exe + ".old";

    static string repo;
    static string Repo
    {
        get
        {
            if (repo != null) return repo;
            try { using var s = typeof(Updater).Assembly.GetManifestResourceStream("repo.txt")!; using var r = new StreamReader(s); repo = r.ReadToEnd().Trim(); }
            catch { repo = ""; }
            return repo;
        }
    }

    public static void CleanupOld()
    {
        for (int i = 0; i < 10 && File.Exists(OldExe); i++) { try { File.Delete(OldExe); } catch { Thread.Sleep(300); } }
    }

    static Version Parse(string t) => Version.TryParse((t ?? "").TrimStart('v', 'V'), out var v) ? v : null;

    public static async Task<bool> Check(bool manual)
    {
        if (string.IsNullOrEmpty(Repo) || Repo.Contains("OWNER")) { Status = "Updates not set up yet"; return false; }
        if (ReadyTag != null) return true;
        try
        {
            using var h = new HttpClientHandler { AllowAutoRedirect = false };
            using var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Notchy/" + Program.Version);
            var res = await c.GetAsync("https://github.com/" + Repo + "/releases/latest");
            string loc = res.Headers.Location?.ToString() ?? "";
            int i = loc.IndexOf("/tag/");
            string tag = i < 0 ? null : Uri.UnescapeDataString(loc[(i + 5)..]);
            var latest = Parse(tag); var mine = Parse(Program.Version);
            if (latest == null || mine == null || latest <= mine) { Status = "Up to date (" + Program.Version + ")"; return false; }
            using var d = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            d.DefaultRequestHeaders.UserAgent.ParseAdd("Notchy/" + Program.Version);
            var bytes = await d.GetByteArrayAsync("https://github.com/" + Repo + "/releases/download/" + tag + "/Notchy.exe");
            if (bytes.Length < 1_000_000 || bytes[0] != 'M' || bytes[1] != 'Z') throw new Exception("bad download");
            await File.WriteAllBytesAsync(NewExe, bytes);
            ReadyTag = tag; Status = tag + " ready";
            return true;
        }
        catch (Exception ex) { Status = manual ? "Check failed: " + ex.Message : "Up to date"; return false; }
    }

    public static bool Apply()
    {
        if (ReadyTag == null || !File.Exists(NewExe)) return false;
        try
        {
            if (File.Exists(OldExe)) File.Delete(OldExe);
            File.Move(Exe, OldExe);
            File.Move(NewExe, Exe);
            Process.Start(Exe, "--updated");
            return true;
        }
        catch
        {
            try { if (!File.Exists(Exe) && File.Exists(OldExe)) File.Move(OldExe, Exe); } catch { }
            ReadyTag = null; Status = "Update failed"; return false;
        }
    }
}
