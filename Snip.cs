// Snip: press Print Screen, crop and mark the shot, save it to Downloads.
// Build: build.cmd (uses the C# compiler that ships with Windows, no installs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        Mutex mutex = new Mutex(true, "SnipApp_SingleInstance", out created);
        if (!created)
        {
            MessageBox.Show("Snip is already running. Look for its icon near the clock.", "Snip");
            return;
        }
        Native.SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
        {
            string log = Path.Combine(Path.GetTempPath(), "snip-error.log");
            File.AppendAllText(log, DateTime.Now + "\r\n" + e.Exception + "\r\n\r\n");
            MessageBox.Show("Snip hit a problem: " + e.Exception.Message + "\nDetails: " + log, "Snip");
        };
        Application.Run(new TrayApp());
        GC.KeepAlive(mutex);
    }
}

class TrayApp : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly Control invoker = new Control();
    readonly Native.HookProc hookProc; // kept in a field so the GC does not free it
    IntPtr hook;
    EditorForm editor;

    public TrayApp()
    {
        invoker.CreateControl();
        hookProc = OnKey;
        hook = InstallHook();

        // Windows silently removes a low-level hook if one key event takes too long
        // (LowLevelHooksTimeout), for example when the PC is busy. Print Screen then
        // stops working with no error. Put a fresh hook in place every 10 seconds.
        System.Windows.Forms.Timer rehook = new System.Windows.Forms.Timer();
        rehook.Interval = 10000;
        rehook.Tick += delegate
        {
            IntPtr fresh = InstallHook();
            if (fresh == IntPtr.Zero) return;
            IntPtr old = hook;
            hook = fresh;
            if (old != IntPtr.Zero) Native.UnhookWindowsHookEx(old);
        };
        rehook.Start();

        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("Take screenshot (Print Screen)", null, delegate { DelayedCapture(); });
        menu.Items.Add("Open Downloads folder", null, delegate { Process.Start("explorer.exe", "\"" + Native.DownloadsFolder() + "\""); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Snip", null, delegate { ExitThread(); });

        tray = new NotifyIcon();
        tray.Icon = MakeIcon();
        tray.Text = "Snip - press Print Screen";
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { DelayedCapture(); };
        tray.Visible = true;

        if (hook == IntPtr.Zero)
            tray.ShowBalloonTip(8000, "Snip", "Snip could not take over the Print Screen key. Use the tray icon instead.", ToolTipIcon.Warning);
        else if (WindowsSnipKeyOn())
            tray.ShowBalloonTip(10000, "Snip", "Windows also uses Print Screen for its Snipping Tool. Turn it off: Settings > Accessibility > Keyboard > \"Use the Print screen key to open screen capture\".", ToolTipIcon.Info);
    }

    IntPtr InstallHook()
    {
        using (Process p = Process.GetCurrentProcess())
            return Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, hookProc, Native.GetModuleHandle(p.MainModule.ModuleName), 0);
    }

    static bool WindowsSnipKeyOn()
    {
        object v = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Keyboard", "PrintScreenKeyForSnippingEnabled", null);
        return v is int && (int)v == 1;
    }

    IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Marshal.ReadInt32(lParam) == Native.VK_SNAPSHOT)
        {
            int msg = wParam.ToInt32();
            if (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP)
                invoker.BeginInvoke(new Action(Capture));
            return (IntPtr)1; // swallow the key so nothing else reacts to it
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    void DelayedCapture()
    {
        // Give the tray menu time to fade out so it is not in the shot.
        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = 400;
        t.Tick += delegate { t.Dispose(); Capture(); };
        t.Start();
    }

    void Capture()
    {
        if (editor != null) return;
        Rectangle b = Screen.FromPoint(Cursor.Position).Bounds;
        Bitmap shot = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(shot))
            g.CopyFromScreen(b.Location, Point.Empty, b.Size);

        editor = new EditorForm(shot, b);
        editor.FormClosed += delegate
        {
            string saved = editor.SavedPath;
            editor.Dispose();
            editor = null;
            if (saved != null)
                tray.ShowBalloonTip(4000, "Snip saved", Path.GetFileName(saved) + " is in Downloads (also copied, so you can paste it).", ToolTipIcon.Info);
        };
        editor.Show();
        Native.ForceForeground(editor.Handle);
    }

    static Icon MakeIcon()
    {
        Bitmap bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Brush b = new SolidBrush(Color.FromArgb(40, 120, 220))) g.FillRectangle(b, 2, 8, 28, 20);
            g.FillRectangle(Brushes.SteelBlue, 10, 4, 12, 6);
            g.FillEllipse(Brushes.White, 9, 11, 14, 14);
            using (Pen p = new Pen(Color.Red, 3)) g.DrawEllipse(p, 12, 14, 8, 8);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }
}

enum Kind { Pen, Arrow, Box, Circle }

class Mark
{
    public Kind Kind;
    public Color Color;
    public float Width;
    public List<PointF> Points = new List<PointF>(); // pen only
    public PointF A, B;   // arrow: A = tail, B = tip. Box and circle: opposite corners.

    public Mark Copy()
    {
        Mark m = (Mark)MemberwiseClone();
        m.Points = new List<PointF>(Points);
        return m;
    }

    public void CopyFrom(Mark o)
    {
        Kind = o.Kind; Color = o.Color; Width = o.Width; A = o.A; B = o.B;
        Points = new List<PointF>(o.Points);
    }
}

class UndoEntry
{
    public Mark Mark;           // the mark that was added, changed or deleted
    public Mark Before;         // set when the mark was changed: its old state
    public int DeletedIndex = -1; // set when the mark was deleted: where it was
    public bool IsCrop;
    public Rectangle PrevCrop;
}

class NoFocusButton : Button
{
    public NoFocusButton()
    {
        SetStyle(ControlStyles.Selectable, false);
        FlatStyle = FlatStyle.Flat;
        ForeColor = Color.White;
        BackColor = Color.FromArgb(60, 60, 60);
        FlatAppearance.BorderColor = Color.FromArgb(60, 60, 60);
        FlatAppearance.BorderSize = 3;
        AutoSize = true;
        Margin = new Padding(4);
        Padding = new Padding(6, 2, 6, 2);
    }
}

enum Tool { Crop, Pen, Arrow, Box, Circle }
enum DragMode { None, Crop, Pen, NewShape, Move, Handle }

class EditorForm : Form
{
    readonly Bitmap img;
    readonly List<Mark> marks = new List<Mark>();
    readonly List<UndoEntry> undo = new List<UndoEntry>();
    Rectangle crop = Rectangle.Empty;    // in image pixels; Empty = whole image
    Tool tool = Tool.Crop;
    Color color = Color.Red;
    float width = 6;

    DragMode drag = DragMode.None;
    Mark live;                           // mark being drawn or changed; drawn on top, not cached
    Mark before;                         // copy of live at drag start, for undo
    Mark selected;                       // shape with handles; can be moved, resized, recoloured
    int handle;
    PointF dragStart, dragNow, lastP;    // image coordinates

    readonly FlowLayoutPanel bar;
    readonly Dictionary<Tool, NoFocusButton> toolBtns = new Dictionary<Tool, NoFocusButton>();
    readonly List<NoFocusButton> colorBtns = new List<NoFocusButton>();
    readonly List<NoFocusButton> sizeBtns = new List<NoFocusButton>();

    RectangleF view;                     // where the image is shown on the form
    float scale = 1;
    Bitmap cache;                        // image + finished marks, at view size

    public string SavedPath { get; private set; }

    public EditorForm(Bitmap shot, Rectangle screenBounds)
    {
        img = shot;
        Text = "Snip editor";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screenBounds;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(28, 28, 28);
        Font = new Font("Segoe UI", 11f);
        Cursor = Cursors.Cross;

        bar = new FlowLayoutPanel();
        bar.Dock = DockStyle.Top;
        bar.AutoSize = true;
        bar.WrapContents = true;
        bar.BackColor = Color.FromArgb(45, 45, 45);
        bar.Padding = new Padding(8, 4, 8, 4);
        bar.Cursor = Cursors.Default;
        Controls.Add(bar);

        AddToolButton(Tool.Crop, "Crop (C)");
        AddToolButton(Tool.Pen, "Pen (P)");
        AddToolButton(Tool.Arrow, "Arrow (A)");
        AddToolButton(Tool.Box, "Box (R)");
        AddToolButton(Tool.Circle, "Circle (O)");
        AddGap();
        foreach (Color c in new Color[] { Color.Red, Color.Yellow, Color.Lime, Color.DodgerBlue, Color.White })
        {
            Color cc = c;
            NoFocusButton b = AddButton("", delegate { color = cc; ApplyStyle(); });
            b.AutoSize = false;
            b.Size = new Size(bar.Font.Height + 14, bar.Font.Height + 14);
            b.BackColor = c;
            b.Tag = c;
            colorBtns.Add(b);
        }
        AddGap();
        foreach (object[] s in new object[][] { new object[] { "Thin", 3f }, new object[] { "Medium", 6f }, new object[] { "Thick", 12f } })
        {
            float w = (float)s[1];
            NoFocusButton b = AddButton((string)s[0], delegate { width = w; ApplyStyle(); });
            b.Tag = w;
            sizeBtns.Add(b);
        }
        AddGap();
        AddButton("Undo (Ctrl+Z)", delegate { Undo(); });
        AddButton("Delete (Del)", delegate { DeleteSelected(); });
        AddGap();
        AddButton("Cancel (Esc)", delegate { Close(); });
        NoFocusButton done = AddButton("Done (Enter)", delegate { Finish(); });
        done.BackColor = Color.FromArgb(30, 130, 60);
        done.FlatAppearance.BorderColor = done.BackColor;

        UpdateButtons();
    }

    void AddToolButton(Tool t, string text)
    {
        toolBtns[t] = AddButton(text, delegate { SetTool(t); });
    }

    NoFocusButton AddButton(string text, EventHandler click)
    {
        NoFocusButton b = new NoFocusButton();
        b.Text = text;
        b.Click += click;
        bar.Controls.Add(b);
        return b;
    }

    void AddGap()
    {
        Panel p = new Panel();
        p.Size = new Size(16, 1);
        bar.Controls.Add(p);
    }

    static bool IsShapeTool(Tool t) { return t == Tool.Arrow || t == Tool.Box || t == Tool.Circle; }

    void SetTool(Tool t)
    {
        tool = t;
        if (!IsShapeTool(t)) selected = null;
        UpdateButtons();
        Invalidate();
    }

    // A colour or size click changes the selected shape, or else what you draw next.
    void ApplyStyle()
    {
        if (selected != null && (selected.Color != color || selected.Width != width))
        {
            UndoEntry u = new UndoEntry();
            u.Mark = selected;
            u.Before = selected.Copy();
            undo.Add(u);
            selected.Color = color;
            selected.Width = width;
            DropCache();
        }
        else if (tool == Tool.Crop) SetTool(Tool.Pen);
        UpdateButtons();
    }

    void UpdateButtons()
    {
        Color on = Color.Orange;
        Color off = Color.FromArgb(60, 60, 60);
        foreach (KeyValuePair<Tool, NoFocusButton> kv in toolBtns)
            kv.Value.FlatAppearance.BorderColor = kv.Key == tool ? on : off;
        foreach (NoFocusButton b in colorBtns)
            b.FlatAppearance.BorderColor = (Color)b.Tag == color ? on : off;
        foreach (NoFocusButton b in sizeBtns)
            b.FlatAppearance.BorderColor = (float)b.Tag == width ? on : off;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (bar == null) return; // layout runs once before the constructor builds the toolbar
        int top = bar.Bottom + 8;
        float aw = ClientSize.Width - 16, ah = ClientSize.Height - top - 8;
        if (aw <= 0 || ah <= 0) return;
        scale = Math.Min(1f, Math.Min(aw / img.Width, ah / img.Height));
        float w = img.Width * scale, h = img.Height * scale;
        view = new RectangleF((float)Math.Round((ClientSize.Width - w) / 2), (float)Math.Round(top + (ah - h) / 2), w, h);
        DropCache();
    }

    void DropCache()
    {
        if (cache != null) { cache.Dispose(); cache = null; }
        Invalidate();
    }

    PointF ToImage(Point p)
    {
        float x = (p.X - view.X) / scale, y = (p.Y - view.Y) / scale;
        return new PointF(Math.Max(0, Math.Min(img.Width, x)), Math.Max(0, Math.Min(img.Height, y)));
    }

    RectangleF ToView(Rectangle r)
    {
        return new RectangleF(view.X + r.X * scale, view.Y + r.Y * scale, r.Width * scale, r.Height * scale);
    }

    static Rectangle RectFrom(PointF a, PointF b)
    {
        int x1 = (int)Math.Round(Math.Min(a.X, b.X)), y1 = (int)Math.Round(Math.Min(a.Y, b.Y));
        int x2 = (int)Math.Round(Math.Max(a.X, b.X)), y2 = (int)Math.Round(Math.Max(a.Y, b.Y));
        return new Rectangle(x1, y1, x2 - x1, y2 - y1);
    }

    static RectangleF Box(Mark m)
    {
        return RectangleF.FromLTRB(Math.Min(m.A.X, m.B.X), Math.Min(m.A.Y, m.B.Y), Math.Max(m.A.X, m.B.X), Math.Max(m.A.Y, m.B.Y));
    }

    static float Dist(PointF a, PointF b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    // Arrow head grows with the arrow's length and thickness, within limits.
    static float HeadLength(Mark m)
    {
        float len = Dist(m.A, m.B);
        return Math.Min(len, Math.Max(m.Width * 3, Math.Min(len * 0.35f, m.Width * 4 + 24)));
    }

    static void DrawMark(Graphics g, Mark m)
    {
        if (m.Kind == Kind.Pen)
        {
            if (m.Points.Count == 1)
            {
                PointF p = m.Points[0];
                using (Brush b = new SolidBrush(m.Color))
                    g.FillEllipse(b, p.X - m.Width / 2, p.Y - m.Width / 2, m.Width, m.Width);
                return;
            }
            using (Pen pen = RoundPen(m))
                g.DrawLines(pen, m.Points.ToArray());
            return;
        }
        if (m.Kind == Kind.Box || m.Kind == Kind.Circle)
        {
            RectangleF r = Box(m);
            using (Pen pen = RoundPen(m))
            {
                if (m.Kind == Kind.Box) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                else g.DrawEllipse(pen, r);
            }
            return;
        }
        // Arrow: a shaft from A, and a filled head that ends at B.
        float len = Dist(m.A, m.B);
        if (len < 1) return;
        float ux = (m.B.X - m.A.X) / len, uy = (m.B.Y - m.A.Y) / len;
        float head = HeadLength(m), half = head * 0.55f;
        PointF baseP = new PointF(m.B.X - ux * head, m.B.Y - uy * head);
        PointF shaftEnd = new PointF(baseP.X + ux * head * 0.2f, baseP.Y + uy * head * 0.2f);
        using (Pen pen = RoundPen(m))
        {
            pen.EndCap = LineCap.Flat;
            if (Dist(m.A, shaftEnd) > 0.5f && head < len) g.DrawLine(pen, m.A, shaftEnd);
        }
        using (Brush b = new SolidBrush(m.Color))
            g.FillPolygon(b, new PointF[] {
                m.B,
                new PointF(baseP.X - uy * half, baseP.Y + ux * half),
                new PointF(baseP.X + uy * half, baseP.Y - ux * half) });
    }

    static Pen RoundPen(Mark m)
    {
        Pen pen = new Pen(m.Color, m.Width);
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        pen.LineJoin = LineJoin.Round;
        return pen;
    }

    // Handles: arrow = tail and tip; box and circle = four corners.
    static PointF[] Handles(Mark m)
    {
        if (m.Kind == Kind.Arrow) return new PointF[] { m.A, m.B };
        return new PointF[] { m.A, new PointF(m.B.X, m.A.Y), new PointF(m.A.X, m.B.Y), m.B };
    }

    static void MoveHandle(Mark m, int i, PointF p)
    {
        if (m.Kind == Kind.Arrow) { if (i == 0) m.A = p; else m.B = p; return; }
        if (i == 0) m.A = p;
        else if (i == 1) { m.B.X = p.X; m.A.Y = p.Y; }
        else if (i == 2) { m.A.X = p.X; m.B.Y = p.Y; }
        else m.B = p;
    }

    float Tolerance { get { return 10 / scale; } } // 10 screen pixels, in image pixels

    int HitHandle(PointF p)
    {
        if (selected == null) return -1;
        PointF[] hs = Handles(selected);
        for (int i = 0; i < hs.Length; i++)
            if (Dist(hs[i], p) <= Tolerance) return i;
        return -1;
    }

    bool HitBody(Mark m, PointF p)
    {
        float tol = Tolerance + m.Width / 2;
        if (m.Kind == Kind.Arrow)
        {
            if (Dist(p, m.B) <= HeadLength(m)) return true;
            float len = Dist(m.A, m.B);
            if (len < 1) return Dist(p, m.A) <= tol;
            float t = ((p.X - m.A.X) * (m.B.X - m.A.X) + (p.Y - m.A.Y) * (m.B.Y - m.A.Y)) / (len * len);
            t = Math.Max(0, Math.Min(1, t));
            return Dist(p, new PointF(m.A.X + t * (m.B.X - m.A.X), m.A.Y + t * (m.B.Y - m.A.Y))) <= tol;
        }
        RectangleF r = Box(m);
        if (m.Kind == Kind.Box)
        {
            RectangleF outer = r, inner = r;
            outer.Inflate(tol, tol);
            inner.Inflate(-tol, -tol);
            return outer.Contains(p) && !inner.Contains(p); // only the edge, so you can draw inside a box
        }
        float rx = r.Width / 2, ry = r.Height / 2;
        if (rx < 1 || ry < 1) return Dist(p, new PointF(r.X + rx, r.Y + ry)) <= tol;
        float dx = (p.X - r.X - rx) / rx, dy = (p.Y - r.Y - ry) / ry;
        return Math.Abs(Math.Sqrt(dx * dx + dy * dy) - 1) * Math.Min(rx, ry) <= tol;
    }

    Mark HitShape(PointF p)
    {
        for (int i = marks.Count - 1; i >= 0; i--)
            if (marks[i].Kind != Kind.Pen && HitBody(marks[i], p)) return marks[i];
        return null;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !view.Contains(e.Location)) return;
        PointF p = ToImage(e.Location);
        lastP = p;
        if (tool == Tool.Crop)
        {
            drag = DragMode.Crop;
            dragStart = dragNow = p;
        }
        else if (tool == Tool.Pen)
        {
            drag = DragMode.Pen;
            live = NewMark(Kind.Pen);
            live.Points.Add(p);
        }
        else
        {
            int h = HitHandle(p);
            Mark hit = h >= 0 ? selected : HitShape(p);
            if (hit != null)
            {
                drag = h >= 0 ? DragMode.Handle : DragMode.Move;
                handle = h;
                selected = live = hit;
                before = hit.Copy();
            }
            else
            {
                drag = DragMode.NewShape;
                selected = null;
                live = NewMark(tool == Tool.Arrow ? Kind.Arrow : tool == Tool.Box ? Kind.Box : Kind.Circle);
                live.A = live.B = p;
            }
        }
        DropCache();
    }

    Mark NewMark(Kind k)
    {
        Mark m = new Mark();
        m.Kind = k;
        m.Color = color;
        m.Width = width;
        return m;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        PointF p = ToImage(e.Location);
        switch (drag)
        {
            case DragMode.None:
                bool over = IsShapeTool(tool) && view.Contains(e.Location) && (HitHandle(p) >= 0 || HitShape(p) != null);
                Cursor = over ? Cursors.SizeAll : Cursors.Cross;
                return;
            case DragMode.Crop: dragNow = p; break;
            case DragMode.Pen: live.Points.Add(p); break;
            case DragMode.NewShape: live.B = p; break;
            case DragMode.Handle: MoveHandle(live, handle, p); break;
            case DragMode.Move:
                float dx = p.X - lastP.X, dy = p.Y - lastP.Y;
                live.A = new PointF(live.A.X + dx, live.A.Y + dy);
                live.B = new PointF(live.B.X + dx, live.B.Y + dy);
                break;
        }
        lastP = p;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        DragMode d = drag;
        drag = DragMode.None;
        if (d == DragMode.Crop)
        {
            Rectangle r = RectFrom(dragStart, dragNow);
            if (r.Width >= 8 && r.Height >= 8)
            {
                UndoEntry u = new UndoEntry();
                u.IsCrop = true;
                u.PrevCrop = crop;
                undo.Add(u);
                crop = r;
                SetTool(Tool.Pen); // like a phone: crop first, then mark
            }
        }
        else if (d == DragMode.Pen || d == DragMode.NewShape)
        {
            if (d == DragMode.Pen || Dist(live.A, live.B) * scale >= 6) // ignore a click with no drag
            {
                marks.Add(live);
                UndoEntry u = new UndoEntry();
                u.Mark = live;
                undo.Add(u);
                if (d == DragMode.NewShape) selected = live;
            }
        }
        else if (d == DragMode.Move || d == DragMode.Handle)
        {
            if (live.A != before.A || live.B != before.B)
            {
                UndoEntry u = new UndoEntry();
                u.Mark = live;
                u.Before = before;
                undo.Add(u);
            }
        }
        live = null;
        before = null;
        DropCache();
    }

    void DeleteSelected()
    {
        if (selected == null) return;
        UndoEntry u = new UndoEntry();
        u.Mark = selected;
        u.DeletedIndex = marks.IndexOf(selected);
        undo.Add(u);
        marks.Remove(selected);
        selected = null;
        DropCache();
    }

    void Undo()
    {
        if (drag != DragMode.None || undo.Count == 0) return;
        UndoEntry u = undo[undo.Count - 1];
        undo.RemoveAt(undo.Count - 1);
        if (u.IsCrop) crop = u.PrevCrop;
        else if (u.DeletedIndex >= 0) marks.Insert(Math.Min(u.DeletedIndex, marks.Count), u.Mark);
        else if (u.Before != null) u.Mark.CopyFrom(u.Before);
        else
        {
            marks.Remove(u.Mark);
            if (selected == u.Mark) selected = null;
        }
        DropCache();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (view.Width <= 0) return;
        Graphics g = e.Graphics;
        if (cache == null)
        {
            cache = new Bitmap(Math.Max(1, (int)view.Width), Math.Max(1, (int)view.Height));
            using (Graphics cg = Graphics.FromImage(cache))
            {
                cg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                cg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                cg.DrawImage(img, new Rectangle(0, 0, cache.Width, cache.Height));
                cg.SmoothingMode = SmoothingMode.AntiAlias;
                cg.ScaleTransform(scale, scale);
                foreach (Mark m in marks) if (m != live) DrawMark(cg, m);
            }
        }
        g.DrawImageUnscaled(cache, (int)view.X, (int)view.Y);

        if (live != null)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(view.X, view.Y);
            g.ScaleTransform(scale, scale);
            DrawMark(g, live);
            g.ResetTransform();
        }

        Rectangle shown = drag == DragMode.Crop ? RectFrom(dragStart, dragNow) : crop;
        if (!shown.IsEmpty)
        {
            RectangleF c = ToView(shown);
            using (Region outside = new Region(view))
            using (Brush dim = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
            {
                outside.Exclude(c);
                g.FillRegion(dim, outside);
            }
            using (Pen p = new Pen(Color.White, 2))
            {
                p.DashStyle = DashStyle.Dash;
                g.DrawRectangle(p, c.X, c.Y, c.Width, c.Height);
            }
        }

        if (selected != null && drag != DragMode.NewShape)
        {
            g.SmoothingMode = SmoothingMode.None;
            foreach (PointF h in Handles(selected))
            {
                float x = view.X + h.X * scale - 6, y = view.Y + h.Y * scale - 6;
                g.FillRectangle(Brushes.White, x, y, 12, 12);
                g.DrawRectangle(Pens.Black, x, y, 12, 12);
            }
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Enter: Finish(); return true;
            case Keys.Escape: Close(); return true;
            case Keys.Control | Keys.Z: Undo(); return true;
            case Keys.Delete: case Keys.Back: DeleteSelected(); return true;
            case Keys.C: SetTool(Tool.Crop); return true;
            case Keys.P: SetTool(Tool.Pen); return true;
            case Keys.A: SetTool(Tool.Arrow); return true;
            case Keys.R: SetTool(Tool.Box); return true;
            case Keys.O: SetTool(Tool.Circle); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void Finish()
    {
        try
        {
            using (Bitmap full = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(full))
                {
                    g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height));
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    foreach (Mark m in marks) DrawMark(g, m);
                }
                Rectangle area = crop.IsEmpty ? new Rectangle(0, 0, img.Width, img.Height) : crop;
                using (Bitmap result = full.Clone(area, PixelFormat.Format32bppArgb))
                {
                    string path = FreePath(Native.DownloadsFolder(), "Snip " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss"));
                    result.Save(path, ImageFormat.Png);
                    SavedPath = path;
                    try { Clipboard.SetImage(result); } catch (ExternalException) { } // clipboard busy: file is still saved
                }
            }
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the screenshot:\n" + ex.Message, "Snip", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static string FreePath(string folder, string name)
    {
        string path = Path.Combine(folder, name + ".png");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, name + " (" + i + ").png");
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (cache != null) cache.Dispose();
            img.Dispose();
        }
        base.Dispose(disposing);
    }
}

static class Native
{
    public const int WH_KEYBOARD_LL = 13, WM_KEYUP = 0x0101, WM_SYSKEYUP = 0x0105, VK_SNAPSHOT = 0x2C;
    const byte VK_MENU = 0x12;
    const uint KEYEVENTF_KEYUP = 0x0002;

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    // Windows only lets the app the user is using take focus. A quick Alt tap lifts that lock.
    public static void ForceForeground(IntPtr hWnd)
    {
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        SetForegroundWindow(hWnd);
    }

    public static string DownloadsFolder()
    {
        IntPtr p;
        if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) == 0)
        {
            string s = Marshal.PtrToStringUni(p);
            Marshal.FreeCoTaskMem(p);
            return s;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
