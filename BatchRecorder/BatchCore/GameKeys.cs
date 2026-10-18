using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BatchRecorder
{
    /// <summary>
    /// Posting the key to the game's own window is how DemoRipper already drives
    /// BO2's theater: no activation, no focus theft, and it works while another
    /// app is in front. Nothing is written to the game's memory.
    /// </summary>
    public static class GameKeys
    {
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
        private const int VK_RETURN = 0x0D, ScanReturn = 0x1C;

        public static IntPtr GameWindow()
        {
            foreach (Process p in Process.GetProcessesByName("t6mpv43"))
            {
                // the game's own window by its class: docked in the Designer it is owned, and MainWindowHandle passes owned
                // windows by (the code review's B-4: the Batch's F4, F2 and Enter went nowhere while the game was docked)
                IntPtr found = IntPtr.Zero; uint want = (uint)p.Id;
                EnumWindows((h, l) =>
                {
                    uint wp; GetWindowThreadProcessId(h, out wp);
                    if (wp != want) return true;
                    var cls = new System.Text.StringBuilder(64); GetClassName(h, cls, 64);
                    if (cls.ToString() == "CoDBlackOps") { found = h; return false; }
                    return true;
                }, IntPtr.Zero);
                if (found != IntPtr.Zero) return found;
                if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            }
            return IntPtr.Zero;
        }

        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>The game's drawing size in pixels, which is what a recording takes; false when there is no window.</summary>
        public static bool ClientSize(out int width, out int height)
        {
            width = height = 0;
            IntPtr h = GameWindow();
            RECT r;
            if (h == IntPtr.Zero || !GetClientRect(h, out r)) return false;
            width = r.Right - r.Left;
            height = r.Bottom - r.Top;
            return width > 0 && height > 0;
        }

        private const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;

        /// <summary>
        /// A mouse click at the middle of the game window. This is the FALLBACK for
        /// changing the spectated player; `demo_client n` is the direct route and
        /// needs none of this. Kept because theater ignored demo_switchplayer and
        /// every +/- bind command we tried, so the mouse was the only thing known
        /// to work by hand -- and a posted click may still be ignored if the game
        /// reads the mouse through raw input rather than the message queue.
        /// </summary>
        public static bool Click(bool rightButton)
        {
            IntPtr h = GameWindow();
            if (h == IntPtr.Zero) return false;

            RECT r;
            int x = 400, y = 300;
            if (GetClientRect(h, out r)) { x = (r.Right - r.Left) / 2; y = (r.Bottom - r.Top) / 2; }
            IntPtr pos = new IntPtr((y << 16) | (x & 0xFFFF));

            PostMessage(h, rightButton ? WM_RBUTTONDOWN : WM_LBUTTONDOWN,
                        new IntPtr(rightButton ? 2 : 1), pos);
            System.Threading.Thread.Sleep(40);
            PostMessage(h, rightButton ? WM_RBUTTONUP : WM_LBUTTONUP, IntPtr.Zero, pos);
            return true;
        }

        /// <summary>
        /// One F4: toggles the theater's timeline bar (Mycelium, 2026-09-29). Posted F4
        /// works. Every demo load brings the bar back, so after a load one press hides it.
        /// </summary>
        public static bool PressF4()
        {
            IntPtr h = GameWindow();
            if (h == IntPtr.Zero) return false;
            const int VK_F4 = 0x73, ScanF4 = 0x3E;
            PostMessage(h, WM_KEYDOWN, new IntPtr(VK_F4), new IntPtr((ScanF4 << 16) | 1));
            System.Threading.Thread.Sleep(40);
            PostMessage(h, WM_KEYUP, new IntPtr(VK_F4), new IntPtr((ScanF4 << 16) | 1 | (3 << 30)));
            return true;
        }

        /// <summary>
        /// One F2: toggles the theater's HUD - kill feed, score, name tags (Mycelium, 2026-10-13: "press F2 to toggle
        /// hud"). cg_draw2d 0 and hud_enable 0 do NOT hide the kill feed in a film (sandbox 0376/0377: both read 0
        /// while it was drawn); F2 does. Every demo load brings the HUD back (0378), so after a load one press hides it.
        /// </summary>
        public static bool PressF2()
        {
            IntPtr h = GameWindow();
            if (h == IntPtr.Zero) return false;
            const int VK_F2 = 0x71, ScanF2 = 0x3C;
            PostMessage(h, WM_KEYDOWN, new IntPtr(VK_F2), new IntPtr((ScanF2 << 16) | 1));
            System.Threading.Thread.Sleep(40);
            PostMessage(h, WM_KEYUP, new IntPtr(VK_F2), new IntPtr((ScanF2 << 16) | 1 | (3 << 30)));
            return true;
        }

        /// <summary>One ENTER, with the scan code and the transition bits the game expects.</summary>
        public static bool PressEnter()
        {
            IntPtr h = GameWindow();
            if (h == IntPtr.Zero) return false;

            IntPtr down = new IntPtr((ScanReturn << 16) | 1);
            IntPtr up = new IntPtr((ScanReturn << 16) | 1 | (3 << 30));   // key was down, now released

            PostMessage(h, WM_KEYDOWN, new IntPtr(VK_RETURN), down);
            System.Threading.Thread.Sleep(40);
            PostMessage(h, WM_KEYUP, new IntPtr(VK_RETURN), up);
            return true;
        }
    }
}
