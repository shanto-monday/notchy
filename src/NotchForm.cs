// The notch: a hidden WebView2 panel that grows out of the top middle of the screen,
// plus a tiny click-through pill that marks where it lives.
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Notchy;

sealed class NotchForm : Form
{
    // sizes in 96-dpi units
    const int OpenW = 820, OpenH = 340, PeekW = 400, PeekH = 74, Hidden = 12; // Hidden = part above the screen edge (hides top corners)

    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    readonly Pill pill = new();
    readonly System.Windows.Forms.Timer poll = new() { Interval = 30 };
    readonly System.Windows.Forms.Timer anim = new() { Interval = 10 };
    readonly System.Windows.Forms.Timer live = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer meters = new() { Interval = 100 };
    readonly string shotPath, shotTab;

    public Action<string, string> Notify;
    public Services Svc;
    public bool IsOpen => state != State.Closed;
    enum State { Closed, Opening, Open, Peek, Closing }
    State state = State.Closed;
    bool pinned, typing, ready, quitting, fullscreen;
    string tab = "home";
    DateTime zoneSince = DateTime.MaxValue, leftSince = DateTime.MaxValue, peekUntil;
    IntPtr prevForeground;
    Rectangle animFrom, animTo; DateTime animStart; int animMs;

    public NotchForm(string shotPath, string shotTab)
    {
        this.shotPath = shotPath; this.shotTab = shotTab;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black; Text = "Notchy";
        Controls.Add(web);
        Svc = new Services(this);
        poll.Tick += (_, _) => Poll();
        anim.Tick += (_, _) => Animate();
        live.Tick += (_, _) => { if (ready && state == State.Open) PushLive(); Svc.Tick(); };
        meters.Tick += (_, _) => { if (ready && state == State.Open && tab == "mixer") Send(new { type = "meters", items = Svc.Audio.Meters() }); };
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x8; return cp; } // TOOLWINDOW | TOPMOST
    }

    float S => DeviceDpi / 96f;
    Rectangle Scr => Screen.PrimaryScreen!.Bounds;
    Rectangle RectFor(int w, int h)
    {
        int pw = (int)(w * S), ph = (int)((h + Hidden) * S);
        return new Rectangle(Scr.Left + (Scr.Width - pw) / 2, Scr.Top - (int)(Hidden * S), pw, ph);
    }

    // created off screen so WebView2 can start, then hidden until the notch opens
    public async void Start()
    {
        Bounds = new Rectangle(-30000, -30000, (int)(OpenW * S), (int)(OpenH * S));
        Show();
        await Init();
        if (shotPath == null) Hide();
    }

    async Task Init()
    {
        int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);          // rounded corners (Win 11)
        int none = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(Handle, 34, ref none, 4); // no border line
        RegisterHotKey(Handle, 1, 0x1 | 0x2 | 0x4000, (int)Keys.N); // Ctrl+Alt+N
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Notchy", "web"));
        await web.EnsureCoreWebView2Async(env);
        var cw = web.CoreWebView2;
        cw.Settings.AreDefaultContextMenusEnabled = false;
        cw.Settings.IsZoomControlEnabled = false;
        cw.Settings.AreDevToolsEnabled = false;
        cw.Settings.IsStatusBarEnabled = false;
        cw.WebMessageReceived += (_, e) => OnMessage(e);
        using (var st = typeof(NotchForm).Assembly.GetManifestResourceStream("index.html")!)
        using (var r = new StreamReader(st)) cw.NavigateToString(r.ReadToEnd());

        if (shotPath != null) { await Shot(); return; }
        pill.Place(Scr, S);
        poll.Start(); live.Start(); meters.Start();
    }

    // QA: render the open notch and save a PNG
    async Task Shot()
    {
        Bounds = RectFor(OpenW, OpenH); Show();
        for (int i = 0; i < 100 && !ready; i++) await Task.Delay(50);
        state = State.Open; tab = shotTab;
        Send(new { type = "open", tab = shotTab });
        PushLive(); Send(new { type = "meters", items = Svc.Audio.Meters() });
        await Task.Delay(1800);
        PushLive();
        await Task.Delay(400);
        using (var fs = File.Create(shotPath)) await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
        Application.Exit();
    }

    // ---------------------------------------------------------------- hover logic
    void Poll()
    {
        var p = Cursor.Position;
        bool fs = IsFullscreenApp();
        if (fs != fullscreen) { fullscreen = fs; pill.Visible2 = !fs && state == State.Closed; }
        if (state == State.Closed)
        {
            if (fullscreen) return;
            int half = Math.Max((int)(Pill.W * S / 2) + (int)(60 * S), (int)(130 * S));
            bool inZone = p.Y <= Scr.Top + 2 && Math.Abs(p.X - (Scr.Left + Scr.Width / 2)) <= half && Scr.Contains(p.X, Scr.Top);
            if (inZone) { if (zoneSince == DateTime.MaxValue) zoneSince = DateTime.Now; else if ((DateTime.Now - zoneSince).TotalMilliseconds > 110) Expand(false); }
            else zoneSince = DateTime.MaxValue;
            return;
        }
        if (state == State.Peek)
        {
            if (Bounds.Contains(p)) { Expand(false); return; }
            if (DateTime.Now > peekUntil) Collapse();
            return;
        }
        if (state != State.Open) return;
        var r = Bounds; r.Inflate((int)(24 * S), (int)(24 * S));
        bool keep = pinned || (typing && ContainsFocus) || Control.MouseButtons != MouseButtons.None;
        if (r.Contains(p) || keep) { leftSince = DateTime.MaxValue; return; }
        if (leftSince == DateTime.MaxValue) leftSince = DateTime.Now;
        else if ((DateTime.Now - leftSince).TotalMilliseconds > 280) Collapse();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (state == State.Open && (pinned || typing)) { pinned = false; typing = false; Collapse(); }
    }

    public void Expand(bool pinned)
    {
        if (fullscreen && !pinned) return;
        this.pinned |= pinned;
        if (state == State.Open || state == State.Opening) { if (pinned) Activate(); return; }
        var fg = GetForegroundWindow();
        if (fg != Handle) prevForeground = fg;
        var from = state == State.Closed ? RectFor(Pill.W, Pill.H) : Bounds;
        state = State.Opening; leftSince = DateTime.MaxValue;
        pill.Visible2 = false;
        Bounds = from;
        if (!Visible) Show();
        if (pinned) Activate();
        if (ready) { Send(new { type = "open", tab }); PushLive(); }
        StartAnim(from, RectFor(OpenW, OpenH), 210);
    }

    public void Peek()
    {
        if (state != State.Closed || fullscreen || !ready) return;
        state = State.Peek; peekUntil = DateTime.Now.AddSeconds(3);
        pill.Visible2 = false;
        var from = RectFor(Pill.W, Pill.H);
        Bounds = from; Show();
        Send(new { type = "peek" }); PushLive();
        StartAnim(from, RectFor(PeekW, PeekH), 180);
    }

    public void Collapse()
    {
        if (state == State.Closed || state == State.Closing) return;
        state = State.Closing; pinned = false; typing = false;
        if (ready) Send(new { type = "close" });
        StartAnim(Bounds, RectFor(Pill.W, Pill.H), 150);
    }

    void StartAnim(Rectangle from, Rectangle to, int ms) { animFrom = from; animTo = to; animStart = DateTime.Now; animMs = ms; anim.Start(); }

    void Animate()
    {
        double t = Math.Min(1, (DateTime.Now - animStart).TotalMilliseconds / animMs);
        double e = state == State.Closing ? t * t : 1 - Math.Pow(1 - t, 3); // ease out on open, ease in on close
        int L(int a, int b) => (int)Math.Round(a + (b - a) * e);
        var r = new Rectangle(L(animFrom.X, animTo.X), L(animFrom.Y, animTo.Y), L(animFrom.Width, animTo.Width), L(animFrom.Height, animTo.Height));
        SetWindowPos(Handle, (IntPtr)(-1), r.X, r.Y, r.Width, r.Height, 0x10 | 0x400); // NOACTIVATE | NOSENDCHANGING
        if (t < 1) return;
        anim.Stop();
        if (state == State.Opening) state = State.Open;
        else if (state == State.Closing) { state = State.Closed; Hide(); if (!fullscreen) pill.Visible2 = true; }
    }

    // games / videos in fullscreen: stay out of the way
    static bool IsFullscreenApp()
    {
        if (SHQueryUserNotificationState(out int s) != 0) return false;
        return s == 2 || s == 3 || s == 4; // BUSY, D3D full screen, presentation mode
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312) { if (state == State.Open && pinned) Collapse(); else Expand(true); } // WM_HOTKEY
        base.WndProc(ref m);
    }

    // ---------------------------------------------------------------- bridge
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public void Send(object o)
    {
        if (!ready || IsDisposed) return;
        try { web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(o, Json)); } catch { }
    }

    void PushLive() => Send(Svc.LiveState(tab));

    void OnMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode m;
        try { m = JsonNode.Parse(e.WebMessageAsJson); } catch { return; }
        string cmd = (string)m?["cmd"] ?? "";
        switch (cmd)
        {
            case "ready":
                ready = true;
                Send(Svc.InitState());
                break;
            case "tab": tab = (string)m["name"] ?? "home"; PushLive(); break;
            case "typing": typing = (bool?)m["on"] ?? false; break;
            case "pin": pinned = (bool?)m["on"] ?? false; if (pinned) Activate(); break;
            case "close": Collapse(); break;
            case "shelfDrop":
                var paths = new List<string>();
                if (e.AdditionalObjects != null)
                    foreach (var o in e.AdditionalObjects) if (o is CoreWebView2File f) paths.Add(f.Path);
                Svc.Shelf.Add(paths); PushLive();
                break;
            case "dragOut":
                {
                    var path = (string)m["path"];
                    if (File.Exists(path) || Directory.Exists(path))
                    {
                        pinned = true;
                        DoDragDrop(new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy | DragDropEffects.Move);
                        pinned = false;
                    }
                    break;
                }
            case "paste":
                {
                    string text = Svc.Clips.Prepare((string)m["id"]);
                    Collapse();
                    PasteInto(prevForeground);
                    break;
                }
            default:
                Svc.Handle(cmd, m);
                PushLive();
                break;
        }
    }

    async void PasteInto(IntPtr target)
    {
        await Task.Delay(60);
        if (target != IntPtr.Zero) SetForegroundWindow(target);
        await Task.Delay(90);
        SendKeys.SendWait("^v");
    }

    public void Quit()
    {
        quitting = true;
        poll.Stop(); live.Stop(); meters.Stop();
        Svc.Dispose();
        pill.Close();
        UnregisterHotKey(Handle, 1);
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Collapse(); return; }
        base.OnFormClosing(e);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, int mods, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
}

// small black pill at the top middle (click-through, anti-aliased, layered window)
sealed class Pill : Form
{
    public const int W = 190, H = 8;
    bool shown;
    public Pill()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8 | 0x08000000; return cp; } // LAYERED TRANSPARENT TOOLWINDOW TOPMOST NOACTIVATE
    }
    public bool Visible2
    {
        set { if (value == shown) return; shown = value; if (value) Show(); else Hide(); }
    }
    public void Place(Rectangle scr, float s)
    {
        int w = (int)(W * s), h = (int)(H * s) + 2;
        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float r = h - 1;
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddLine(0, -1, w, -1);
            path.AddArc(w - 2 * r, -r - 1, 2 * r, 2 * r, 0, 90);
            path.AddArc(0, -r - 1, 2 * r, 2 * r, 90, 90);
            path.CloseFigure();
            g.FillPath(Brushes.Black, path);
        }
        var pos = new POINT { X = scr.Left + (scr.Width - w) / 2, Y = scr.Top };
        Bounds = new Rectangle(pos.X, pos.Y, w, h);
        Visible2 = true;
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hb);
        var size = new SIZE { W = w, H = h }; var src = new POINT();
        var blend = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
        UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2);
        SelectObject(mem, old); DeleteObject(hb); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
    }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE sz, IntPtr src, ref POINT srcPt, int key, ref BLEND bf, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
}
