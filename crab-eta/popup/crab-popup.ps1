# Crab ETA popup: watches the state file the crab-eta mod writes. While a long
# task runs, a small crab button sits at the bottom-right of the screen; a
# click opens it into a window with the minutes left, another click closes it.
param([Parameter(Mandatory = $true)][string]$StateFile)

$created = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\CrabEtaPopup', [ref]$created)
if (-not $created) { exit 0 }

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

public class CrabForm : Form
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint GetPriorityClass(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder name, ref int size);

    static readonly string[] Body = {
        "...#######...",
        "..#########..",
        "..##o###o##..",
        "#############",
        "#############",
        "..#########..",
    };
    static readonly string[] LegsA = { "..#.#...#.#..", "..#.#...#.#.." };
    static readonly string[] LegsB = { ".#.#.....#.#.", "..#.#...#.#.." };

    // The flag in the right claw, in grid cells relative to the body's top-left:
    // the pole stands in column 13 beside the claw, the cloth flies from its top.
    const int Cols = 17;          // body (13) + pole + cloth (3)
    const int FlagRows = 3;       // rows the flag rises above the body
    static readonly string[] ClothA = { "###", "###" };
    static readonly string[] ClothB = { "##.", "###" };

    // Crab color by 5-hour session usage, flag color by weekly usage, both:
    // 0-30 green, 30-50 yellow, 50-80 orange, 80-100 red.
    static readonly Color Green = Color.FromArgb(95, 184, 101);
    static readonly Color Yellow = Color.FromArgb(232, 197, 71);
    static readonly Color Orange = Color.FromArgb(217, 119, 87);
    static readonly Color Red = Color.FromArgb(224, 80, 74);
    static readonly Color Card = Color.FromArgb(38, 36, 33);
    static readonly Color Ink = Color.FromArgb(240, 238, 230);
    static readonly Color Dim = Color.FromArgb(160, 156, 148);

    readonly string stateFile;
    readonly Timer poll = new Timer { Interval = 500 };
    readonly Timer anim = new Timer { Interval = 15 };
    readonly ToolTip tip = new ToolTip();
    readonly float s;
    DateTime lastWrite = DateTime.MinValue;
    bool active, done, expanded, wantVisible, gameMode;
    int ticks;
    double minutes, usage = -1, weekly = -1;
    long setAt;
    string task = "";
    DateTime doneAt;
    int frame;

    int S(float v) { return (int)Math.Round(v * s); }

    public CrabForm(string stateFile)
    {
        this.stateFile = stateFile;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Card;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        using (var g = CreateGraphics()) s = g.DpiX / 96f;
        SetOpen(false);
        tip.SetToolTip(this, "Click to open or close. Right-click to hide.");
        MouseUp += (o, e) =>
        {
            if (e.Button == MouseButtons.Right) { wantVisible = false; active = false; done = false; }
            else SetOpen(!expanded);
        };
        poll.Tick += (o, e) => Poll();
        anim.Tick += (o, e) => Animate();
        poll.Start();
        anim.Start();
    }

    // Collapsed: a round crab button. Expanded: the window with the minutes.
    void SetOpen(bool open)
    {
        expanded = open;
        int bottom = Top + Height;
        Size = open ? new Size(S(350), S(96)) : new Size(S(76), S(76));
        Region = open ? Rounded(new Rectangle(0, 0, Width, Height), S(16)) : Circle(Width);
        if (Visible) Top = bottom - Height;
        Invalidate();
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; // no-activate, tool window, topmost
            return cp;
        }
    }

    static Region Rounded(Rectangle r, int d)
    {
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return new Region(p);
    }

    static Region Circle(int d)
    {
        var p = new GraphicsPath();
        p.AddEllipse(0, 0, d, d);
        return new Region(p);
    }

    Rectangle Area { get { return Screen.PrimaryScreen.WorkingArea; } }

    void Poll()
    {
        try
        {
            var info = new FileInfo(stateFile);
            if (info.Exists && info.LastWriteTimeUtc != lastWrite)
            {
                lastWrite = info.LastWriteTimeUtc;
                var json = File.ReadAllText(stateFile);
                bool wasActive = active;
                active = Regex.IsMatch(json, "\"active\"\\s*:\\s*true");
                minutes = Num(json, "minutes");
                setAt = (long)Num(json, "setAt");
                usage = Regex.IsMatch(json, "\"usage\"") ? Num(json, "usage") : -1;
                weekly = Regex.IsMatch(json, "\"weekly\"") ? Num(json, "weekly") : -1;
                var m = Regex.Match(json, "\"task\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                task = m.Success ? Regex.Unescape(m.Groups[1].Value) : "";
                if (active) { done = false; wantVisible = true; }
                else if (wasActive) { done = true; doneAt = DateTime.Now; wantVisible = true; }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (ticks++ % 4 == 0) gameMode = GameModeOn();
        if (done && (DateTime.Now - doneAt).TotalSeconds > 6) { done = false; wantVisible = false; }
        if (active && Remaining() < -15) { active = false; wantVisible = false; } // stale estimate
        Invalidate();
    }

    // Claude Game Mode puts the Claude desktop app at Below Normal priority.
    static bool GameModeOn()
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("claude"))
        {
            IntPtr h = OpenProcess(0x1000, false, p.Id); // query limited information
            if (h == IntPtr.Zero) continue;
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size)
                    && sb.ToString().IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0
                    && GetPriorityClass(h) == 0x4000) return true; // BELOW_NORMAL_PRIORITY_CLASS
            }
            finally { CloseHandle(h); }
        }
        return false;
    }

    static double Num(string json, string key)
    {
        var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?[0-9.]+)");
        double v;
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out v) ? v : 0;
    }

    double Remaining()
    {
        var started = DateTimeOffset.FromUnixTimeMilliseconds(setAt).LocalDateTime;
        return minutes - (DateTime.Now - started).TotalMinutes;
    }

    string Line()
    {
        if (done) return "Done!";
        double left = Remaining();
        return left < 1 ? "Almost done" : "~" + Math.Ceiling(left) + " min left";
    }

    string Short()
    {
        if (done) return "done";
        double left = Remaining();
        return left < 1 ? "<1m" : Math.Ceiling(left) + "m";
    }

    void Animate()
    {
        var a = Area;
        Left = a.Right - Width - S(16);
        int shown = a.Bottom - Height - S(16);
        int hidden = a.Bottom + 4;
        bool show = wantVisible && !gameMode;
        int target = show ? shown : hidden;
        if (show && !Visible) { Top = hidden; Show(); }
        if (Visible)
        {
            int step = Math.Max(1, Math.Abs(target - Top) / 5);
            if (Top < target) Top = Math.Min(target, Top + step);
            else if (Top > target) Top = Math.Max(target, Top - step);
            if (!show && Top >= hidden) { Hide(); SetOpen(false); }
        }
        if (++frame % 20 == 0) Invalidate();
    }

    static Color UsageColor(double percent)
    {
        if (percent < 0) return Orange; // no reading yet
        if (percent < 30) return Green;
        if (percent < 50) return Yellow;
        if (percent < 80) return Orange;
        return Red;
    }

    // oy is the body's top; the flag rises FlagRows cells above it.
    void DrawCrab(Graphics g, int ox, int oy, int px)
    {
        bool step = (frame / 20) % 2 == 0 && active;
        int bob = step ? Math.Max(1, px / 5) : 0;
        using (var orange = new SolidBrush(UsageColor(usage)))
        using (var eye = new SolidBrush(Card))
        using (var pole = new SolidBrush(Dim))
        using (var cloth = new SolidBrush(UsageColor(weekly)))
        {
            for (int y = -FlagRows; y <= 3; y++)
                g.FillRectangle(pole, ox + 13 * px, oy + y * px - bob, px, px);
            var flag = step ? ClothB : ClothA;
            for (int y = 0; y < flag.Length; y++)
                for (int x = 0; x < flag[y].Length; x++)
                    if (flag[y][x] == '#')
                        g.FillRectangle(cloth, ox + (14 + x) * px, oy + (y - FlagRows) * px - bob, px, px);

            for (int y = 0; y < Body.Length; y++)
                for (int x = 0; x < Body[y].Length; x++)
                {
                    char c = Body[y][x];
                    if (c == '.') continue;
                    g.FillRectangle(c == 'o' ? eye : orange, ox + x * px, oy + y * px - bob, px, px);
                }
            var legs = step ? LegsB : LegsA;
            for (int y = 0; y < legs.Length; y++)
                for (int x = 0; x < legs[y].Length; x++)
                    if (legs[y][x] == '#')
                        g.FillRectangle(orange, ox + x * px, oy + (Body.Length + y) * px, px, px);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        if (!expanded)
        {
            int px = S(3);
            // Nudged left and down so the flag's tip clears the circle's edge.
            DrawCrab(g, (Width - Cols * px) / 2 - px, S(15) + FlagRows * px, px);
            using (var f = new Font("Segoe UI Semibold", 8f))
            using (var ink = new SolidBrush(Ink))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center };
                g.DrawString(Short(), f, ink, new RectangleF(0, S(51), Width, S(20)), sf);
            }
            return;
        }

        DrawCrab(g, S(16), S(30), S(5));
        int tx = S(112);
        using (var small = new Font("Segoe UI", 9f))
        using (var big = new Font("Segoe UI Semibold", 15f))
        using (var ink = new SolidBrush(Ink))
        using (var dim = new SolidBrush(Dim))
        {
            string head = done ? "Claude" : "Claude is working";
            if (usage >= 0) head += " \u00B7 5h " + Math.Round(usage) + "%";
            if (weekly >= 0) head += " \u00B7 week " + Math.Round(weekly) + "%";
            g.DrawString(head, small, dim, tx, S(14));
            g.DrawString(Line(), big, ink, tx - S(2), S(30));
            if (!done && task.Length > 0)
            {
                var r = new RectangleF(tx, S(64), Width - tx - S(12), S(20));
                g.DrawString(task, small, dim, r, new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            }
        }
    }

    public static void Run(string stateFile)
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.Run(new CrabContext(new CrabForm(stateFile)));
    }
}

// Keeps the message loop alive while the form is hidden.
public class CrabContext : ApplicationContext
{
    public CrabContext(CrabForm form) { form.CreateControl(); var h = form.Handle; }
}
'@

[CrabForm]::Run($StateFile)
