// Notchy - a MacBook-style notch for Windows. Hover the top middle of the screen and it opens.
using System.Diagnostics;
using Microsoft.Win32;

namespace Notchy;

static class Program
{
    public static string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notchy");
    public static string Version => Application.ProductVersion.Split('+')[0];

    [STAThread]
    static void Main(string[] args)
    {
        Directory.CreateDirectory(DataDir);
        Updater.CleanupOld();
        ApplicationConfiguration.Initialize();

        bool updated = args.Length > 0 && args[0] == "--updated";
        using var mutex = new Mutex(false, "Notchy_SingleInstance");
        bool mine;
        try { mine = mutex.WaitOne(updated ? 15000 : 0); } catch (AbandonedMutexException) { mine = true; }
        if (!mine) { MessageBox.Show("Notchy is already running. Move your mouse to the top middle of the screen.", "Notchy"); return; }

        var shot = args.Length >= 2 && args[0] == "--shot" ? args[1] : null; // QA: --shot out.png [tab]
        var form = new NotchForm(shot, args.Length > 2 ? args[2] : "overview");
        var tray = new Tray(form, updated ? Version : null);
        form.Start();
        Application.Run(new ApplicationContext());
        tray.Dispose();
        mutex.ReleaseMutex();
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool StartWithWindows
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("Notchy") != null; }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("Notchy", "\"" + Environment.ProcessPath + "\"");
            else if (k.GetValue("Notchy") != null) k.DeleteValue("Notchy");
        }
    }
}

sealed class Tray : IDisposable
{
    readonly NotifyIcon icon;
    readonly NotchForm form;
    readonly System.Windows.Forms.Timer housekeeping = new() { Interval = 60_000 };
    DateTime lastCheck = DateTime.MinValue;

    public Tray(NotchForm form, string updatedTo)
    {
        this.form = form;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open notch", null, (_, _) => form.Expand(pinned: true));
        var start = new ToolStripMenuItem("Start with Windows") { Checked = SafeStart() };
        start.Click += (_, _) => { try { Program.StartWithWindows = !start.Checked; start.Checked = !start.Checked; } catch { } };
        menu.Items.Add(start);
        menu.Items.Add("Check for updates", null, async (_, _) =>
        {
            bool got = await Updater.Check(true);
            if (got) TryApply(); else icon.ShowBalloonTip(2500, "Notchy " + Program.Version, Updater.Status, ToolTipIcon.None);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Notchy", null, (_, _) => form.Quit());
        icon = new NotifyIcon { Icon = AppIcon(), Visible = true, Text = "Notchy - hover the top middle of the screen", ContextMenuStrip = menu };
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) form.Expand(pinned: true); };
        if (updatedTo != null) icon.ShowBalloonTip(3000, "Notchy updated to " + updatedTo, "Hover the top middle of your screen.", ToolTipIcon.None);
        else if (!File.Exists(Path.Combine(Program.DataDir, "seen.txt")))
        {
            File.WriteAllText(Path.Combine(Program.DataDir, "seen.txt"), "1");
            icon.ShowBalloonTip(5000, "Notchy is running", "Move your mouse to the top middle of the screen to open it. Ctrl + Alt + N works too.", ToolTipIcon.None);
        }
        housekeeping.Tick += async (_, _) =>
        {
            if ((DateTime.Now - lastCheck).TotalHours >= 6) { lastCheck = DateTime.Now; await Updater.Check(false); }
            TryApply();
        };
        housekeeping.Start();
        var first = new System.Windows.Forms.Timer { Interval = 30_000 };
        first.Tick += async (_, _) => { first.Stop(); lastCheck = DateTime.Now; await Updater.Check(false); TryApply(); };
        first.Start();
        form.Notify = (t, m) => icon.ShowBalloonTip(4000, t, m, ToolTipIcon.None);
    }

    static bool SafeStart() { try { return Program.StartWithWindows; } catch { return false; } }

    void TryApply()
    {
        if (Updater.ReadyTag == null || form.IsOpen) return;
        if (Updater.Apply()) form.Quit();
    }

    public static Icon AppIcon()
    {
        try { var i = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); if (i != null) return i; } catch { }
        return SystemIcons.Application;
    }

    public void Dispose() { housekeeping.Stop(); icon.Visible = false; icon.Dispose(); }
}
