// Claude game mode: shrinks the Claude app's CPU and RAM claim WITHOUT closing it,
// so Remote Control chats stay connected (End Task kills them).
//   Two kinds of Claude process:
//     chat = each chat session (claude-code\...\claude.exe) and everything it starts
//     app  = the desktop app itself (WindowsApps\...\Claude.exe: main, renderers, GPU, utility)
//   on     = chats limited all the time; app limited only while no Claude window is open
//            on screen (minimised, or closed to the tray). While a Claude window is open the
//            app runs at full speed, so switching to it is instant.
//            limited = below-normal priority + Windows efficiency mode (EcoQoS) + trim RAM
//            + hard RAM cap of CapMB per process (Windows pages the rest out)
//   off    = everything at normal priority, efficiency mode back to default, RAM cap removed
//   toggle = on if off, otherwise off. The state is the marker file gamemode.on next to the exe
//   listen = stay in the background: Ctrl+Alt+G toggles; Claude's window is watched
//            (minimise / restore / show / hide / focus events) to switch the app between
//            limited and full speed; new Claude processes are caught every 3 s
// Shows a small corner banner at once and beeps rising (on) or falling (off).
// Build: C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize /out:ClaudeGameMode.exe ClaudeGameMode.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
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

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

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
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect r);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    static BannerForm current; // banner on screen (listen mode replaces it on the next press)
    static bool busy;          // a press is still being applied
    static HotkeyWindow hotkeyWindow; // kept in a field: if collected, Windows drops the hotkey
    static System.Windows.Forms.Timer watch;    // listen mode: catches new processes, backup window check
    static System.Windows.Forms.Timer debounce; // listen mode: one sync shortly after a burst of window events
    static Dictionary<uint, bool> applied = new Dictionary<uint, bool>(); // pid -> limited?
    static WinEventProc eventProc;      // kept in a field so the hook callback isn't collected
    static IntPtr globalHook, appHook;  // focus/minimise events (all windows); show/hide (the app only)
    static uint hookedRoot;

    static readonly string Marker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gamemode.on");
    static bool IsOn() { return File.Exists(Marker); }
    static void SetOn(bool on)
    {
        try { if (on) File.WriteAllText(Marker, DateTime.Now.ToString("s")); else if (File.Exists(Marker)) File.Delete(Marker); }
        catch { }
    }

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

        // Older versions kept the state in the app's priority: carry an "on" state over.
        if (!IsOn())
        {
            uint root;
            if (FindClaude(out root).Count > 0 && Priority(root) == BelowNormal) SetOn(true);
        }

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

        debounce = new System.Windows.Forms.Timer();
        debounce.Interval = 60;
        debounce.Tick += delegate { debounce.Stop(); if (!busy) Sync(false); };

        // Out-of-context hooks: Windows posts the events to this thread's message loop.
        eventProc = delegate (IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0 || !IsOn()) return; // 0 = the window itself, not a part of it
            debounce.Stop(); debounce.Start();
        };
        // 0x0003 foreground .. 0x0017 minimise end
        globalHook = SetWinEventHook(0x0003, 0x0017, IntPtr.Zero, eventProc, 0, 0, 0x0002); // WINEVENT_SKIPOWNPROCESS

        watch = new System.Windows.Forms.Timer();
        watch.Interval = 3000;
        watch.Tick += delegate { if (!busy) Sync(false); };
        watch.Start();
        Sync(false);
        TrimSelf();
        Application.Run();
        GC.KeepAlive(single);
    }

    // Show/hide events only for the app's main process (it owns the windows); re-hooked if the app restarts.
    static void HookApp(uint root)
    {
        if (root == hookedRoot) return;
        if (appHook != IntPtr.Zero) { UnhookWinEvent(appHook); appHook = IntPtr.Zero; }
        hookedRoot = root;
        if (root != 0 && eventProc != null)
            appHook = SetWinEventHook(0x8002, 0x8003, IntPtr.Zero, eventProc, root, 0, 0x0002); // object show .. hide
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
        if (FindClaude(out root).Count == 0) return MakeBanner("Claude is not running", "Nothing to change.", false, null, mutex);
        bool on = mode == "on" || (mode == "toggle" && !IsOn());
        SetOn(on);
        busy = true;
        return MakeBanner(on ? "Claude game mode ON" : "Claude game mode OFF",
                          on ? "Chats limited \u00B7 app at full speed while its window is open" : "Back to normal speed",
                          on, delegate { return Sync(true); }, mutex);
    }

    // Brings every Claude process to the state it should have now; only changed ones are touched.
    static string Sync(bool report)
    {
        uint root;
        Dictionary<uint, bool> tree = FindClaude(out root); // pid -> is an app process
        HookApp(root);
        bool on = IsOn();
        HashSet<uint> appPids = new HashSet<uint>();
        foreach (KeyValuePair<uint, bool> kv in tree) if (kv.Value) appPids.Add(kv.Key);
        bool open = on && AppWindowOpen(appPids);

        long before = 0, after = 0;
        int capped = 0, failed = 0;
        List<uint> gone = new List<uint>();
        foreach (uint pid in applied.Keys) if (!tree.ContainsKey(pid)) gone.Add(pid);
        foreach (uint pid in gone) applied.Remove(pid);

        foreach (KeyValuePair<uint, bool> kv in tree)
        {
            bool limit = on && (!kv.Value || !open);
            bool prev;
            bool known = applied.TryGetValue(kv.Key, out prev);
            // A process never limited and not to be limited is left as Claude set it (a toggle press still resets all).
            if (!known && !limit && !report && !Capped(kv.Key)) { applied[kv.Key] = false; continue; }
            bool change = !known || prev != limit;
            if (!change && !report) continue;
            IntPtr h = OpenProcess(Access, false, kv.Key);
            if (h == IntPtr.Zero) continue;
            if (report) before += WorkingSet(h);
            if (change)
            {
                bool ok = Settle(h, limit);
                if (limit) { if (ok) capped++; else failed++; }
                applied[kv.Key] = limit;
            }
            if (report) after += WorkingSet(h);
            CloseHandle(h);
        }
        if (!report) return null;
        if (!on) return string.Format("Claude RAM {0:N0} MB \u00B7 Ctrl+Alt+G for game mode", after >> 20);
        return string.Format("RAM {0:N0} \u2192 {1:N0} MB \u00B7 {2} \u00B7 Ctrl+Alt+G undoes",
            before >> 20, after >> 20, failed > 0 && capped == 0 ? "RAM cap failed" : "max " + CapMB + " MB per chat");
    }

    // Is any real Claude window on screen (shown, not minimised, not on another virtual desktop)?
    static bool AppWindowOpen(HashSet<uint> appPids)
    {
        if (appPids.Count == 0) return false;
        bool found = false;
        EnumWindows(delegate (IntPtr w, IntPtr l)
        {
            uint pid;
            GetWindowThreadProcessId(w, out pid);
            if (!appPids.Contains(pid) || !IsWindowVisible(w) || IsIconic(w)) return true;
            if ((GetWindowLong(w, -20) & 0x80) != 0) return true; // tool window
            Rect r;
            if (!GetWindowRect(w, out r) || r.Right - r.Left < 200 || r.Bottom - r.Top < 150) return true;
            int cloaked;
            if (DwmGetWindowAttribute(w, 14, out cloaked, 4) == 0 && cloaked != 0) return true; // DWMWA_CLOAKED
            found = true;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    // Every claude.exe and everything they started, each marked app (true) or chat (false) by its
    // nearest claude.exe ancestor. root = the desktop app's main process.
    static Dictionary<uint, bool> FindClaude(out uint root)
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

        Dictionary<uint, bool> kind = new Dictionary<uint, bool>(); // claude.exe pid -> is app
        foreach (KeyValuePair<uint, string> kv in name)
            if (IsClaude(kv.Value))
                kind[kv.Key] = ImagePath(kv.Key).IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0;

        Dictionary<uint, bool> set = new Dictionary<uint, bool>(kind);
        bool added;
        do
        {
            added = false;
            foreach (KeyValuePair<uint, uint> kv in parent)
            {
                bool k;
                if (kv.Key != 0 && !set.ContainsKey(kv.Key) && set.TryGetValue(kv.Value, out k)) { set[kv.Key] = k; added = true; }
            }
        } while (added);

        root = 0;
        foreach (KeyValuePair<uint, bool> kv in kind)
        {
            uint pp; string pn;
            if (parent.TryGetValue(kv.Key, out pp) && name.TryGetValue(pp, out pn) && IsClaude(pn)) continue;
            if (root == 0) root = kv.Key;
            if (kv.Value) { root = kv.Key; break; }
        }
        return set;
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

    // Does the process carry a hard RAM cap (set by game mode, here or by an earlier run)?
    static bool Capped(uint pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return false;
        UIntPtr min, max; uint flags;
        bool capped = GetProcessWorkingSetSizeEx(h, out min, out max, out flags) && (flags & HardMaxEnable) != 0;
        CloseHandle(h);
        return capped;
    }

    static long WorkingSet(IntPtr h)
    {
        MemoryCounters c;
        return GetProcessMemoryInfo(h, out c, (uint)Marshal.SizeOf(typeof(MemoryCounters))) ? (long)c.WorkingSetSize.ToUInt64() : 0;
    }

    static void TrimSelf() { GC.Collect(); EmptyWorkingSet(GetCurrentProcess()); }

    // Puts one process into (or out of) the limited state; returns whether the RAM cap change took.
    static bool Settle(IntPtr h, bool limit)
    {
        PowerThrottling state = new PowerThrottling();
        state.Version = 1; state.ControlMask = limit ? 1u : 0u; state.StateMask = limit ? 1u : 0u;
        SetPriorityClass(h, limit ? BelowNormal : Normal);
        SetProcessInformation(h, 4, ref state, (uint)Marshal.SizeOf(typeof(PowerThrottling))); // 4 = ProcessPowerThrottling
        bool ok = Limit(h, limit);
        if (limit) EmptyWorkingSet(h);
        return ok;
    }

    // Hard working-set cap: Windows keeps the process's RAM at or below CapMB and pages the
    // rest out, rather than failing allocations, so nothing crashes. Off = cap made soft again.
    static bool Limit(IntPtr h, bool limit)
    {
        if (limit) return SetProcessWorkingSetSizeEx(h, new UIntPtr(1u << 20), new UIntPtr((ulong)CapMB << 20), HardMinDisable | HardMaxEnable);
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
        form.ClientSize = new Size((int)(440 * s), (int)(84 * s));
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
