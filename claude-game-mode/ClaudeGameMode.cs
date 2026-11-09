// Claude game mode: shrinks the Claude app's CPU and RAM claim WITHOUT closing it,
// so Remote Control chats stay connected (End Task kills them).
//   on     = below-normal priority + Windows efficiency mode (EcoQoS) + trim RAM
//            + hard RAM cap of CapMB per process (Windows pages the rest out instead of
//            letting the working set grow back; Claude keeps running, just slower)
//   off    = normal priority, efficiency mode back to Windows' default, RAM cap removed
//   toggle = on if the Claude app is at normal priority, otherwise off
//   listen = stay in the background and toggle on Ctrl+Alt+G (started at login from the
//            Startup folder; a shortcut-file hotkey made Windows add ~2 s of delay);
//            while game mode is on, also puts new Claude processes into it every 5 s
// Shows a small corner banner at once and beeps rising (on) or falling (off),
// then does the work while the banner is up.
// Build: C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize /out:ClaudeGameMode.exe ClaudeGameMode.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class ClaudeGameMode
{
    const uint SnapProcess = 0x2;
    // query info | set info | set quota | vm read | query limited
    const uint Access = 0x0400 | 0x0200 | 0x0100 | 0x0010 | 0x1000;
    const uint BelowNormal = 0x4000, Normal = 0x20;
    const int CapMB = 100;
    const uint HardMinDisable = 0x2, HardMaxEnable = 0x4, HardMaxDisable = 0x8; // QUOTA_LIMITS_HARDWS_*

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottling { public uint Version, ControlMask, StateMask; }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryCounters
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }

    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)] static extern bool ProcessFirst(IntPtr snap, ref ProcessEntry e);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] static extern bool ProcessNext(IntPtr snap, ref ProcessEntry e);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool SetPriorityClass(IntPtr h, uint priority);
    [DllImport("kernel32.dll")] static extern uint GetPriorityClass(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr h, int infoClass, ref PowerThrottling info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)] static extern bool ImageName(IntPtr h, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")] static extern bool GetProcessWorkingSetSizeEx(IntPtr h, out UIntPtr min, out UIntPtr max, out uint flags);
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSizeEx(IntPtr h, UIntPtr min, UIntPtr max, uint flags);
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);
    [DllImport("psapi.dll")] static extern bool GetProcessMemoryInfo(IntPtr h, out MemoryCounters counters, uint size);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

    static BannerForm current; // banner on screen (listen mode replaces it on the next press)
    static bool busy;          // a press is still being applied
    static HotkeyWindow hotkeyWindow; // kept in a field: if collected, Windows drops the hotkey
    static System.Windows.Forms.Timer watch; // listen mode: catches Claude processes started while on
    static HashSet<uint> enforced = new HashSet<uint>(); // processes already put into game mode

    [STAThread]
    static void Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "toggle";
        SetProcessDPIAware();
        if (mode == "listen") { Listen(); return; }

        // A second run while the first is still working waits for it, then toggles back.
        Mutex mutex = new Mutex(false, "Local\\ClaudeGameMode");
        try { mutex.WaitOne(5000); } catch (AbandonedMutexException) { }
        Application.Run(Decide(mode, mutex));
    }

    static void Listen()
    {
        bool createdNew;
        Mutex single = new Mutex(true, "Local\\ClaudeGameModeListener", out createdNew);
        if (!createdNew) return; // already listening

        hotkeyWindow = new HotkeyWindow();
        bool ok = false;
        // Ctrl+Alt+G, no auto-repeat; retry while another program may still be releasing it.
        for (int i = 0; i < 20 && !ok; i++)
        {
            ok = RegisterHotKey(hotkeyWindow.Handle, 1, 0x0002 | 0x0001 | 0x4000, 0x47);
            if (!ok) Thread.Sleep(500);
        }
        if (!ok)
        {
            Application.Run(MakeBanner("Ctrl+Alt+G is taken", "Another program uses this key; the game mode key is off.", false, null, null));
            return;
        }
        watch = new System.Windows.Forms.Timer();
        watch.Interval = 5000;
        watch.Tick += delegate { Enforce(); };
        watch.Start();
        TrimSelf();
        Application.Run();
        GC.KeepAlive(single);
    }

    // While game mode is on, a new chat or a restarted app window starts new Claude processes
    // at normal RAM; give them the same treatment. The cap itself is enforced by Windows.
    static void Enforce()
    {
        if (busy) return;
        uint root;
        List<uint> tree = FindClaude(out root);
        if (tree.Count == 0 || Priority(root) != BelowNormal) { enforced.Clear(); return; }
        enforced.IntersectWith(tree);
        foreach (uint pid in tree)
        {
            if (enforced.Contains(pid)) continue;
            IntPtr h = OpenProcess(Access, false, pid);
            if (h == IntPtr.Zero) continue;
            Settle(h, true);
            CloseHandle(h);
            enforced.Add(pid);
        }
    }

    internal static void OnHotkey()
    {
        if (busy) return;
        if (current != null && !current.IsDisposed) current.Close();
        current = Decide("toggle", null);
        current.Show();
    }

    // Decides on/off now; the returned banner does the work once it is on screen.
    static BannerForm Decide(string mode, Mutex mutex)
    {
        uint root;
        List<uint> tree = FindClaude(out root);
        if (tree.Count == 0) return MakeBanner("Claude is not running", "Nothing to change.", false, null, mutex);
        bool on = mode == "on" || (mode == "toggle" && Priority(root) != BelowNormal);
        busy = true;
        return MakeBanner(on ? "Claude game mode ON" : "Claude game mode OFF",
                          on ? "Low priority \u00B7 Remote Control stays connected" : "Back to normal speed",
                          on, delegate { return Apply(tree, on); }, mutex);
    }

    // Every claude.exe (desktop app + one per open chat) and everything they started.
    // root = the desktop app's main process, whose priority tells the current state.
    static List<uint> FindClaude(out uint root)
    {
        Dictionary<uint, uint> parent = new Dictionary<uint, uint>();
        Dictionary<uint, string> name = new Dictionary<uint, string>();
        IntPtr snap = CreateToolhelp32Snapshot(SnapProcess, 0);
        ProcessEntry e = new ProcessEntry();
        e.dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry));
        if (ProcessFirst(snap, ref e))
        {
            do { parent[e.th32ProcessID] = e.th32ParentProcessID; name[e.th32ProcessID] = e.szExeFile; }
            while (ProcessNext(snap, ref e));
        }
        CloseHandle(snap);

        HashSet<uint> set = new HashSet<uint>();
        foreach (KeyValuePair<uint, string> kv in name)
            if (IsClaude(kv.Value)) set.Add(kv.Key);
        bool added;
        do
        {
            added = false;
            foreach (KeyValuePair<uint, uint> kv in parent)
                if (kv.Key != 0 && !set.Contains(kv.Key) && set.Contains(kv.Value)) { set.Add(kv.Key); added = true; }
        } while (added);

        root = 0;
        foreach (uint pid in set)
        {
            string n, pn; uint pp;
            if (!name.TryGetValue(pid, out n) || !IsClaude(n)) continue;
            if (parent.TryGetValue(pid, out pp) && name.TryGetValue(pp, out pn) && IsClaude(pn)) continue;
            if (root == 0) root = pid;
            if (ImagePath(pid).IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0) { root = pid; break; }
        }
        return new List<uint>(set);
    }

    static bool IsClaude(string exe) { return string.Equals(exe, "claude.exe", StringComparison.OrdinalIgnoreCase); }

    static string ImagePath(uint pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return "";
        StringBuilder sb = new StringBuilder(1024); uint size = 1024;
        string path = ImageName(h, 0, sb, ref size) ? sb.ToString() : "";
        CloseHandle(h);
        return path;
    }

    static uint Priority(uint pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return Normal;
        uint p = GetPriorityClass(h);
        CloseHandle(h);
        return p;
    }

    static long WorkingSet(IntPtr h)
    {
        MemoryCounters c;
        return GetProcessMemoryInfo(h, out c, (uint)Marshal.SizeOf(typeof(MemoryCounters))) ? (long)c.WorkingSetSize.ToUInt64() : 0;
    }

    static void TrimSelf() { GC.Collect(); EmptyWorkingSet(GetCurrentProcess()); }

    static string Apply(List<uint> tree, bool on)
    {
        long before = 0, after = 0;
        int capped = 0;
        List<IntPtr> handles = new List<IntPtr>();
        foreach (uint pid in tree)
        {
            IntPtr h = OpenProcess(Access, false, pid);
            if (h == IntPtr.Zero) continue;
            before += WorkingSet(h);
            if (Settle(h, on)) capped++;
            handles.Add(h);
        }
        foreach (IntPtr h in handles) { after += WorkingSet(h); CloseHandle(h); }
        enforced = on ? new HashSet<uint>(tree) : new HashSet<uint>();
        if (!on) return string.Format("Claude RAM {0:N0} MB \u00B7 Ctrl+Alt+G for game mode", after >> 20);
        return string.Format("RAM {0:N0} \u2192 {1:N0} MB \u00B7 {2} \u00B7 Ctrl+Alt+G undoes",
            before >> 20, after >> 20, capped > 0 ? "max " + CapMB + " MB per process" : "RAM cap failed");
    }

    // Puts one process into (or out of) game mode; returns whether the RAM cap change took.
    static bool Settle(IntPtr h, bool on)
    {
        PowerThrottling state = new PowerThrottling();
        state.Version = 1; state.ControlMask = on ? 1u : 0u; state.StateMask = on ? 1u : 0u;
        SetPriorityClass(h, on ? BelowNormal : Normal);
        SetProcessInformation(h, 4, ref state, (uint)Marshal.SizeOf(typeof(PowerThrottling))); // 4 = ProcessPowerThrottling
        bool limited = Limit(h, on);
        if (on) EmptyWorkingSet(h);
        return limited;
    }

    // Hard working-set cap: Windows keeps the process's RAM at or below CapMB and pages the
    // rest out, rather than failing allocations, so nothing crashes. Off = cap made soft again.
    static bool Limit(IntPtr h, bool on)
    {
        if (on) return SetProcessWorkingSetSizeEx(h, new UIntPtr(1u << 20), new UIntPtr((ulong)CapMB << 20), HardMinDisable | HardMaxEnable);
        UIntPtr min, max; uint flags;
        return GetProcessWorkingSetSizeEx(h, out min, out max, out flags)
            && SetProcessWorkingSetSizeEx(h, min, max, HardMinDisable | HardMaxDisable);
    }

    delegate string Work();

    static BannerForm MakeBanner(string title, string body, bool on, Work work, Mutex mutex)
    {
        float s;
        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) s = g.DpiX / 96f;

        BannerForm form = new BannerForm();
        form.ClientSize = new Size((int)(420 * s), (int)(84 * s));
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        form.Location = new Point(wa.Right - form.Width - (int)(16 * s), wa.Bottom - form.Height - (int)(16 * s));

        Panel accent = new Panel();
        accent.BackColor = on ? Color.FromArgb(46, 160, 67) : Color.FromArgb(139, 148, 158);
        accent.Dock = DockStyle.Left; accent.Width = (int)(6 * s);
        form.Controls.Add(accent);
        Label t = MakeLabel(title, new Font("Segoe UI Semibold", 12f), Color.White, s, 10);
        Label b = MakeLabel(body, new Font("Segoe UI", 9f), Color.FromArgb(215, 215, 215), s, 38);
        Label r = MakeLabel("", new Font("Segoe UI", 9f), Color.FromArgb(160, 160, 160), s, 58);
        form.Controls.Add(t); form.Controls.Add(b); form.Controls.Add(r);

        form.Shown += delegate
        {
            form.Refresh(); // paint the banner before doing any work
            Thread beep = new Thread(delegate ()
            {
                try
                {
                    if (on) { Console.Beep(660, 120); Console.Beep(990, 160); }
                    else { Console.Beep(990, 120); Console.Beep(660, 160); }
                }
                catch { }
            });
            beep.IsBackground = true; beep.Start();
            try { if (work != null) { r.Text = work(); r.Refresh(); } }
            finally
            {
                busy = false;
                if (mutex != null) try { mutex.ReleaseMutex(); } catch { }
            }
            System.Windows.Forms.Timer close = new System.Windows.Forms.Timer();
            close.Interval = 2200;
            close.Tick += delegate { close.Stop(); form.Close(); };
            close.Start();
        };
        form.FormClosed += delegate { if (current == form) current = null; TrimSelf(); };
        return form;
    }

    static Label MakeLabel(string text, Font font, Color color, float s, int top)
    {
        Label l = new Label();
        l.Text = text; l.Font = font; l.ForeColor = color; l.AutoSize = true;
        l.Location = new Point((int)(20 * s), (int)(top * s));
        return l;
    }
}

// Invisible message-only window that receives the Ctrl+Alt+G press.
class HotkeyWindow : NativeWindow
{
    public HotkeyWindow()
    {
        CreateParams cp = new CreateParams();
        cp.Parent = new IntPtr(-3); // HWND_MESSAGE
        CreateHandle(cp);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312) ClaudeGameMode.OnHotkey(); // WM_HOTKEY
        base.WndProc(ref m);
    }
}

class BannerForm : Form
{
    public BannerForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(32, 32, 32);
    }

    // Never take focus from the game.
    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; // no-activate, tool window, topmost
            return cp;
        }
    }
}
