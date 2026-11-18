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
        using (Process p = Process.GetCurrentProcess())
            hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, hookProc, Native.GetModuleHandle(p.MainModule.ModuleName), 0);

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

class Stroke
{
    public Color Color;
    public float Width;
    public List<PointF> Points = new List<PointF>();
}

class UndoEntry
{
    public Stroke Stroke;         // set when a stroke was added
    public Rectangle PrevCrop;    // used when Stroke is null (a crop change)
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

enum Tool { Crop, Pen }

class EditorForm : Form
{
    readonly Bitmap img;
    readonly List<Stroke> strokes = new List<Stroke>();
    readonly List<UndoEntry> undo = new List<UndoEntry>();
    Rectangle crop = Rectangle.Empty;    // in image pixels; Empty = whole image
    Tool tool = Tool.Crop;
    Color color = Color.Red;
    float width = 6;

    Stroke current;                      // stroke being drawn
    bool dragging;
    PointF dragStart, dragNow;           // image coordinates, for crop

    readonly FlowLayoutPanel bar;
    NoFocusButton cropBtn, penBtn;
    readonly List<NoFocusButton> colorBtns = new List<NoFocusButton>();
    readonly List<NoFocusButton> sizeBtns = new List<NoFocusButton>();

    RectangleF view;                     // where the image is shown on the form
    float scale = 1;
    Bitmap cache;                        // image + finished strokes, at view size

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
        bar.WrapContents = false;
        bar.BackColor = Color.FromArgb(45, 45, 45);
        bar.Padding = new Padding(8, 4, 8, 4);
        bar.Cursor = Cursors.Default;
        Controls.Add(bar);

        cropBtn = AddButton("Crop (C)", delegate { SetTool(Tool.Crop); });
        penBtn = AddButton("Pen (P)", delegate { SetTool(Tool.Pen); });
        AddGap();
        foreach (Color c in new Color[] { Color.Red, Color.Yellow, Color.Lime, Color.DodgerBlue, Color.White })
        {
            Color cc = c;
            NoFocusButton b = AddButton("", delegate { color = cc; SetTool(Tool.Pen); UpdateButtons(); });
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
            NoFocusButton b = AddButton((string)s[0], delegate { width = w; SetTool(Tool.Pen); UpdateButtons(); });
            b.Tag = w;
            sizeBtns.Add(b);
        }
        AddGap();
        AddButton("Undo (Ctrl+Z)", delegate { Undo(); });
        AddGap();
        AddButton("Cancel (Esc)", delegate { Close(); });
        NoFocusButton done = AddButton("Done (Enter)", delegate { Finish(); });
        done.BackColor = Color.FromArgb(30, 130, 60);
        done.FlatAppearance.BorderColor = done.BackColor;

        UpdateButtons();
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

    void SetTool(Tool t)
    {
        tool = t;
        UpdateButtons();
    }

    void UpdateButtons()
    {
        Color on = Color.Orange;
        Color off = Color.FromArgb(60, 60, 60);
        cropBtn.FlatAppearance.BorderColor = tool == Tool.Crop ? on : off;
        penBtn.FlatAppearance.BorderColor = tool == Tool.Pen ? on : off;
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

    static void DrawStroke(Graphics g, Stroke s)
    {
        if (s.Points.Count == 1)
        {
            PointF p = s.Points[0];
            using (Brush b = new SolidBrush(s.Color))
                g.FillEllipse(b, p.X - s.Width / 2, p.Y - s.Width / 2, s.Width, s.Width);
            return;
        }
        using (Pen pen = new Pen(s.Color, s.Width))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            g.DrawLines(pen, s.Points.ToArray());
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !view.Contains(e.Location)) return;
        dragging = true;
        PointF p = ToImage(e.Location);
        if (tool == Tool.Pen)
        {
            current = new Stroke();
            current.Color = color;
            current.Width = width;
            current.Points.Add(p);
        }
        else
        {
            dragStart = dragNow = p;
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!dragging) return;
        PointF p = ToImage(e.Location);
        if (current != null) current.Points.Add(p);
        else dragNow = p;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        if (current != null)
        {
            strokes.Add(current);
            UndoEntry u = new UndoEntry();
            u.Stroke = current;
            undo.Add(u);
            current = null;
            DropCache();
            return;
        }
        Rectangle r = RectFrom(dragStart, dragNow);
        if (r.Width >= 8 && r.Height >= 8)
        {
            UndoEntry u = new UndoEntry();
            u.PrevCrop = crop;
            undo.Add(u);
            crop = r;
            SetTool(Tool.Pen); // like a phone: crop first, then mark
        }
        Invalidate();
    }

    void Undo()
    {
        if (undo.Count == 0) return;
        UndoEntry u = undo[undo.Count - 1];
        undo.RemoveAt(undo.Count - 1);
        if (u.Stroke != null) strokes.Remove(u.Stroke);
        else crop = u.PrevCrop;
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
                foreach (Stroke s in strokes) DrawStroke(cg, s);
            }
        }
        g.DrawImageUnscaled(cache, (int)view.X, (int)view.Y);

        if (current != null)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(view.X, view.Y);
            g.ScaleTransform(scale, scale);
            DrawStroke(g, current);
            g.ResetTransform();
        }

        Rectangle shown = (dragging && current == null) ? RectFrom(dragStart, dragNow) : crop;
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
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Enter: Finish(); return true;
            case Keys.Escape: Close(); return true;
            case Keys.Control | Keys.Z: Undo(); return true;
            case Keys.C: SetTool(Tool.Crop); return true;
            case Keys.P: SetTool(Tool.Pen); return true;
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
                    foreach (Stroke s in strokes) DrawStroke(g, s);
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
