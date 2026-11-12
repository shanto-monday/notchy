// The notch: one always-alive WebView2 window at the top middle of the screen.
// Only its visible shape (window region) changes: a slim pill when closed, a pop-down panel when open.
// Changing the region is cheap, so the open/close animation stays smooth (no WebView resize per frame).
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Notchy;

sealed class NotchForm : Form
{
    // sizes in 96-dpi units
    const int PanelW = 440, PanelMax = 720, PillW = 200, PillH = 9, PeekW = 400, PeekH = 76, Hidden = 12, Radius = 22;

    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    readonly System.Windows.Forms.Timer poll = new() { Interval = 30 };
    readonly System.Windows.Forms.Timer anim = new() { Interval = 8 };
    readonly System.Windows.Forms.Timer live = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer meters = new() { Interval = 100 };
    readonly string shotPath, shotTab;

    public Action<string, string> Notify;
    public Services Svc;
    public bool IsOpen => state != State.Closed;
    enum State { Closed, Opening, Open, Peek, Closing }
    State state = State.Closed;
    bool pinned, typing, ready, quitting, fullscreen;
    string tab = "overview";
    DateTime zoneSince = DateTime.MaxValue, leftSince = DateTime.MaxValue, peekUntil;
    IntPtr prevForeground;
    RectangleF animFrom, animTo, cur; DateTime animStart; int animMs;

    public NotchForm(string shotPath, string shotTab)
    {
        this.shotPath = shotPath; this.shotTab = shotTab;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black; Text = "Notchy";
        Controls.Add(web);
        Svc = new Services(this);
        poll.Tick += (_, _) => Poll();
        anim.Tick += (_, _) => Animate();
        live.Tick += (_, _) => { if (ready && state is State.Open or State.Peek) PushLive(); Svc.Tick(); };
        meters.Tick += (_, _) => { if (ready && state == State.Open && tab == "volume") Send(new { type = "meters", items = Svc.Audio.Meters() }); };
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x8; return cp; } // TOOLWINDOW | TOPMOST
    }

    float S => DeviceDpi / 96f;
    // tall enough for the overview, but never taller than 88% of the screen
    int PanelH => (int)Math.Min(PanelMax, Screen.PrimaryScreen!.Bounds.Height / S * 0.88f - Hidden);
    Rectangle Scr => Screen.PrimaryScreen!.Bounds;
    int Wpx => (int)(PanelW * S);
    int Hpx => (int)((PanelH + Hidden) * S);

    // shapes inside the window (window coordinates, px)
    RectangleF Shape(float w, float h) => new((Wpx - w * S) / 2f, 0, w * S, (h + Hidden) * S);
    RectangleF PillShape => Shape(PillW, PillH);
    RectangleF PanelShape => Shape(PanelW, PanelH);
    RectangleF PeekShape => Shape(PeekW, PeekH);

    public async void Start()
    {
        Bounds = new Rectangle(Scr.Left + (Scr.Width - Wpx) / 2, Scr.Top - (int)(Hidden * S), Wpx, Hpx);
        SetShape(RectangleF.Empty);
        Show();
        await Init();
    }

    async Task Init()
    {
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
        SetShape(PillShape); cur = PillShape;
        poll.Start(); live.Start(); meters.Start();
    }

    // QA: render the open panel and save a PNG
    async Task Shot()
    {
        for (int i = 0; i < 100 && !ready; i++) await Task.Delay(50);
        SetShape(PanelShape);
        state = State.Open; tab = shotTab;
        Send(new { type = "open", tab = shotTab });
        PushLive(); Send(new { type = "meters", items = Svc.Audio.Meters() });
        await Task.Delay(1500); PushLive(); await Task.Delay(1200); PushLive(); await Task.Delay(500);
        using (var fs = File.Create(shotPath)) await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
        Application.Exit();
    }

    // ---------------------------------------------------------------- shape + animation
    static readonly bool Dbg = Environment.GetEnvironmentVariable("NOTCHY_DEBUG") == "1";
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    static void Log(string s) { if (Dbg) try { File.AppendAllText(Path.Combine(Program.DataDir, "debug.log"), Clock.ElapsedMilliseconds + " " + s + "\n"); } catch { } }

    void SetShape(RectangleF r)
    {
        Log("shape " + state + " " + r.Width.ToString("0") + "x" + r.Height.ToString("0") + " win " + Width + "x" + Height);
        IntPtr rgn;
        if (r.Width < 1 || r.Height < 1) rgn = CreateRectRgn(0, 0, 0, 0);
        else
        {
            int rad = (int)(Math.Min(Radius * S, Math.Min(r.Width, r.Height - Hidden * S)) * 2);
            rgn = CreateRoundRectRgn((int)r.Left, (int)r.Top - rad, (int)Math.Ceiling(r.Right) + 1, (int)Math.Ceiling(r.Bottom) + 1, Math.Max(2, rad), Math.Max(2, rad));
        }
        SetWindowRgn(Handle, rgn, true); // Windows owns the region now
    }

    void StartAnim(RectangleF to, int ms) { animFrom = cur; animTo = to; animStart = DateTime.Now; animMs = ms; anim.Start(); Animate(); }

    void Animate()
    {
        double t = Math.Min(1, (DateTime.Now - animStart).TotalMilliseconds / animMs);
        // springy ease-out when opening, quick ease-in when closing
        double e = state == State.Closing ? t * t * t : 1 - Math.Pow(1 - t, 4);
        float L(float a, float b) => (float)(a + (b - a) * e);
        cur = new RectangleF(L(animFrom.X, animTo.X), 0, L(animFrom.Width, animTo.Width), L(animFrom.Height, animTo.Height));
        SetShape(cur);
        if (t < 1) return;
        anim.Stop();
        if (state == State.Opening) state = State.Open;
        else if (state == State.Closing) { state = State.Closed; SetShape(fullscreen ? RectangleF.Empty : PillShape); }
    }

    // ---------------------------------------------------------------- hover logic
    void Poll()
    {
        var p = Cursor.Position;
        bool fs = IsFullscreenApp();
        if (fs != fullscreen)
        {
            fullscreen = fs;
            if (state == State.Closed) SetShape(fs ? RectangleF.Empty : PillShape);
        }
        var b = Bounds;
        if (state == State.Closed)
        {
            if (fullscreen) return;
            int half = (int)(PillW * S / 2) + (int)(50 * S);
            bool inZone = p.Y <= Scr.Top + 2 && Math.Abs(p.X - (Scr.Left + Scr.Width / 2)) <= half;
            if (inZone) { if (zoneSince == DateTime.MaxValue) zoneSince = DateTime.Now; else if ((DateTime.Now - zoneSince).TotalMilliseconds > 100) Expand(false); }
            else zoneSince = DateTime.MaxValue;
            return;
        }
        var shape = state == State.Peek ? PeekShape : PanelShape;
        var screenShape = new Rectangle(b.Left + (int)shape.X, b.Top, (int)shape.Width, (int)shape.Height);
        if (state == State.Peek)
        {
            if (screenShape.Contains(p)) { Expand(false); return; }
            if (DateTime.Now > peekUntil) Collapse();
            return;
        }
        if (state != State.Open) return;
        var r = screenShape; r.Inflate((int)(20 * S), (int)(20 * S));
        bool keep = pinned || (typing && ContainsFocus) || Control.MouseButtons != MouseButtons.None;
        if (r.Contains(p) || keep) { leftSince = DateTime.MaxValue; return; }
        if (leftSince == DateTime.MaxValue) leftSince = DateTime.Now;
        else if ((DateTime.Now - leftSince).TotalMilliseconds > 250) Collapse();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (state == State.Open && (pinned || typing)) { pinned = false; typing = false; Collapse(); }
    }

    public void Expand(bool pinned)
    {
        if (!ready || (fullscreen && !pinned)) return;
        this.pinned |= pinned;
        if (state is State.Open or State.Opening) { if (pinned) Activate(); return; }
        var fg = GetForegroundWindow();
        if (fg != Handle) prevForeground = fg;
        state = State.Opening; leftSince = DateTime.MaxValue; Log("expand");
        StartAnim(PanelShape, 260);              // first frame right away
        if (pinned) Activate();
        Send(new { type = "open", tab });
        BeginInvoke(PushLive);
    }

    public void PeekNotif(object n)
    {
        Send(new { type = "notif", n });
        if (state == State.Closed) { Peek(); peekUntil = DateTime.Now.AddSeconds(5); }
    }

    public void Peek()
    {
        if (state != State.Closed || fullscreen || !ready) return;
        state = State.Peek; peekUntil = DateTime.Now.AddSeconds(3);
        animFrom = cur; animTo = PeekShape; animStart = DateTime.Now; animMs = 220; anim.Start();
        Send(new { type = "peek" }); BeginInvoke(PushLive);
    }

    public void Collapse()
    {
        if (state is State.Closed or State.Closing) return;
        state = State.Closing; pinned = false; typing = false;
        Send(new { type = "close" });
        StartAnim(PillShape, 170);
    }

    // games / videos in fullscreen: stay out of the way
    static bool IsFullscreenApp()
    {
        if (SHQueryUserNotificationState(out int s) != 0) return false;
        return s == 2 || s == 3 || s == 4; // BUSY, D3D full screen, presentation mode
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312) { if (state == State.Open) Collapse(); else Expand(true); } // WM_HOTKEY
        if (m.Msg == 0x0201 && state == State.Closed) { Expand(true); return; }          // click on the pill
        base.WndProc(ref m);
    }

    // ---------------------------------------------------------------- bridge
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public void Send(object o)
    {
        if (!ready || IsDisposed) return;
        try { web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(o, Json)); } catch { }
    }

    // live data is gathered off the UI thread so animations never wait for it
    bool liveBusy;
    async void PushLive()
    {
        if (liveBusy || !ready) return;
        liveBusy = true;
        try { string t = tab; var st = await Task.Run(() => Svc.LiveState(t)); Send(st); }
        catch { }
        finally { liveBusy = false; }
    }

    void OnMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode m;
        try { m = JsonNode.Parse(e.WebMessageAsJson); } catch { return; }
        string cmd = (string)m?["cmd"] ?? "";
        switch (cmd)
        {
            case "ready": ready = true; Send(Svc.InitState()); break;
            case "tab": tab = (string)m["name"] ?? "overview"; PushLive(); break;
            case "typing": typing = (bool?)m["on"] ?? false; break;
            case "pin": pinned = (bool?)m["on"] ?? false; if (pinned) Activate(); break;
            case "close": Collapse(); break;
            case "quit": Quit(); break;
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
                Svc.Clips.Prepare((string)m["id"]);
                Collapse();
                PasteInto(prevForeground);
                break;
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
        UnregisterHotKey(Handle, 1);
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Collapse(); return; }
        base.OnFormClosing(e);
    }

    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, int mods, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
}
