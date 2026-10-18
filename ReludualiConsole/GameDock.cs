using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ReludualiConsole
{
    /// <summary>
    /// The game's own window in the Console's workspace (2026-10-14). Mycelium: "arrange the UI in a way that essentially treats
    /// BO2 is if it were a Viewport within the Console ... adjust the sliders perhaps overtop of BO2, or move them aside so
    /// the BO2 is fully visible, and click into BO2 when they have to".
    ///
    /// The game is not put INSIDE the Console's window (a child window of another process ties both programs' input
    /// together: the Console would freeze whenever the game is busy). It stays its own window, made borderless, owned by
    /// the Console (so it stays above it and minimises with it), and kept over the Console's game slot. Measured in the
    /// sandbox first (0408-0410): the game keeps drawing at its own size (1920x808) whatever its window's size - the
    /// picture is scaled, never cropped, so a recording's size never changes; it does not minimise when the Console takes
    /// the focus; the owner holds it above the Console; real input reaches it only while it is in front; the batch's
    /// posted keys and pictures still work. Releasing gives the window back exactly as it was.
    ///
    /// The camera jump (0416-0426): the game steers by the cursor - it puts the cursor at its window's middle every frame
    /// and turns by how far it got. On getting the focus it turns by (the cursor - the point it stored when it last set
    /// the cursor, t6mpv43 0x2B69960), 0.0442 degrees a pixel: a window moved while the game was not in front, or a click
    /// anywhere but that point, turned the camera. Nothing done from out here can win that race for a click, so the fix
    /// is in the game: RipperBridge 0.11.0-dev15's "steady on" sets that point to the cursor the moment the game is
    /// activated (the host turns it on while the game is docked). Measured: no turn for a move, a click, or Into.
    /// </summary>
    public sealed class GameDock
    {
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetLong32(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetLong32(IntPtr h, int i, int v);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint f);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        private const int SW_SHOWNOACTIVATE = 4;
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);

        /// <summary>The owner (GWL_HWNDPARENT) by the call that works for this process: the Console is 32-bit, where
        /// SetWindowLongPtrW does not exist and SetWindowLongW is the real one; a 64-bit caller needs the Ptr form (0409/0410).</summary>
        private static IntPtr GetOwnerLong(IntPtr h) { return IntPtr.Size == 8 ? GetLongPtr(h, GWL_HWNDPARENT) : (IntPtr)GetLong32(h, GWL_HWNDPARENT); }
        private static void SetOwnerLong(IntPtr h, IntPtr owner) { if (IntPtr.Size == 8) SetLongPtr(h, GWL_HWNDPARENT, owner); else SetLong32(h, GWL_HWNDPARENT, owner.ToInt32()); }

        private const int GWL_STYLE = -16, GWL_HWNDPARENT = -8;
        private const int WS_POPUP = unchecked((int)0x80000000), WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000,
                          WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_NOOWNERZORDER = 0x200;

        private IntPtr _hwnd, _owner, _origOwner;
        private int _origStyle;
        private RECT _origRect;
        private int _pid;

        /// <summary>Width over height of the game's picture (its client when it was docked): the slot keeps this shape.</summary>
        public double Aspect { get; private set; }
        public bool Docked { get { return _hwnd != IntPtr.Zero && IsWindow(_hwnd); } }
        /// <summary>The docked game has the focus: its mouse and keys.</summary>
        public bool InFront { get { return Docked && GetForegroundWindow() == _hwnd; } }
        public IntPtr Window { get { return _hwnd; } }
        /// <summary>What the dock did last, for the slot's line and the pipe.</summary>
        public string Status { get; private set; }
        /// <summary>The game's process (for the host: the jump guard is turned on once per run of the game).</summary>
        public int Pid { get { return _pid; } }
        /// <summary>The game's own size: its client when it was docked (it keeps drawing at that size). RipperBridge dev17
        /// scales the mouse to it, so BO2's menu cursor follows the pointer in the smaller slot.</summary>
        public int OwnWidth { get; private set; }
        public int OwnHeight { get; private set; }

        public GameDock() { Aspect = 1920.0 / 808.0; Status = "no game"; }

        /// <summary>The game's picture: t6mpv43's largest visible top-level window (its class is CoDBlackOps).</summary>
        public static IntPtr FindGame(out int pid)
        {
            pid = 0;
            Process p = null;
            try { Process[] ps = Process.GetProcessesByName("t6mpv43"); if (ps.Length > 0) p = ps[0]; } catch { }
            if (p == null) return IntPtr.Zero;
            uint want = (uint)p.Id;
            pid = p.Id;
            IntPtr best = IntPtr.Zero; long area = -1;
            EnumWindows((h, l) =>
            {
                uint wp; GetWindowThreadProcessId(h, out wp);
                if (wp == want && IsWindowVisible(h))
                {
                    RECT r; GetWindowRect(h, out r);
                    long a = (long)(r.R - r.L) * (r.B - r.T);
                    var cls = new StringBuilder(64); GetClassName(h, cls, 64);
                    if (cls.ToString() == "CoDBlackOps") a += 1L << 40;        // the game's own class wins
                    if (a > area) { area = a; best = h; }
                }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        /// <summary>Takes the game's window into the slot: borderless, owned by the Console. Its old self is kept for Release.</summary>
        public string Dock(IntPtr console)
        {
            int pid;
            IntPtr h = FindGame(out pid);
            if (h == IntPtr.Zero) { Status = "no game running: it goes here when Redacted starts"; return Status; }
            if (h == _hwnd && Docked && _owner == console) return Status;
            if (Docked && h != _hwnd) Release();
            _hwnd = h; _pid = pid; _owner = console;
            _origStyle = GetLong32(h, GWL_STYLE);
            GetWindowRect(h, out _origRect);
            _origOwner = GetOwnerLong(h);
            RECT c; GetClientRect(h, out c);
            if (c.R > 0 && c.B > 0) { Aspect = (double)c.R / c.B; OwnWidth = c.R; OwnHeight = c.B; }
            int style = (_origStyle & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) | WS_POPUP;
            if (IsIconic(h)) ShowWindow(h, SW_SHOWNOACTIVATE);
            SetLong32(h, GWL_STYLE, style);
            SetOwnerLong(h, console);
            SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            Status = string.Format("docked: the game's picture {0}x{1} ({2:0.###}:1) in the slot", c.R, c.B, Aspect);
            return Status;
        }

        /// <summary>Puts the docked game over a rectangle of the screen (physical pixels).</summary>
        public void Place(int x, int y, int w, int h)
        {
            if (!Docked || w < 8 || h < 8) return;
            Unminimise();
            SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        /// <summary>
        /// A game that minimised itself while it was on its own (fullscreen BO2 does when another window takes the focus)
        /// comes back, without taking the focus: moved while minimised, the slot stayed black until he clicked BO2 on
        /// the taskbar (Mycelium's round 1, 2026-10-15, Classic then Workspace).
        /// </summary>
        private void Unminimise()
        {
            if (IsIconic(_hwnd)) ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        }

        /// <summary>Gives the game its own window back, as it was before it was docked.</summary>
        public string Release()
        {
            if (!Docked) { _hwnd = IntPtr.Zero; Status = "not docked"; return Status; }
            SetOwnerLong(_hwnd, _origOwner);
            SetLong32(_hwnd, GWL_STYLE, _origStyle);
            // back where it was, but with its top-left on the screen it is on: a game window that was partly off the screen
            // came back where neither it nor what it covered could be clicked (the fake-mouse test's B6)
            int x = _origRect.L, y = _origRect.T, w = _origRect.R - _origRect.L, hh = _origRect.B - _origRect.T;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (GetMonitorInfo(MonitorFromRect(ref _origRect, 2), ref mi))           // MONITOR_DEFAULTTONEAREST
            {
                RECT wa = mi.rcWork;
                if (x + w > wa.R) x = wa.R - w;
                if (y + hh > wa.B) y = wa.B - hh;
                if (x < wa.L) x = wa.L;
                if (y < wa.T) y = wa.T;
            }
            SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, hh, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            _hwnd = IntPtr.Zero;
            Status = "released: the game has its own window back";
            return Status;
        }

        /// <summary>Into the game: the game in front (with the bridge's "steady" on, it turns nothing; see above).</summary>
        public void Into()
        {
            if (!Docked) return;
            SetForegroundWindow(_hwnd);
        }

        /// <summary>For the pipe: where the game is, and who owns it.</summary>
        public string Describe()
        {
            if (!Docked) return Status;
            RECT r, c; GetWindowRect(_hwnd, out r); GetClientRect(_hwnd, out c);
            return Status + string.Format("; window at {0},{1} {2}x{3}, owner 0x{4:X}, style 0x{5:X8}, in front {6}", r.L, r.T, c.R, c.B,
                GetWindow(_hwnd, 4).ToInt64(), GetLong32(_hwnd, GWL_STYLE), GetForegroundWindow() == _hwnd);
        }
    }
}
