using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>
    /// The game's camera view, got to from wherever it was left (Mycelium 2026-10-18: "how does the Designer get to the "Dolly
    /// Camera" view? Is this solved reliably? So no matter what settings the user left BO2 on, it can get back to that view
    /// effectively?").
    ///
    /// Measured in the sandbox (0730, 2026-10-18), with a film up:
    ///   theater + 0xD4C00  the camera mode, F3 cycles it: 0 1ST PERSON -> 1 3RD PERSON -> 2 FREE CAMERA -> 0
    ///   theater + 0xD4C04  FREE CAMERA's own mode, right-click cycles it: 0 Free Roam -> 2 Dolly Camera -> 1 Edit Camera -> 0
    ///                      (the theater bar's label says "Dolly Camera" at 2; it keeps its value through F3)
    /// CODCAST has no FREE CAMERA modes (Mycelium 10-13), so Free Roam, Edit Camera and Dolly Camera need the regular theater.
    ///
    /// Both words are only READ from here (never written from outside the game: 0497). The game's own keys change them,
    /// posted to its window as the timeline's F4 is - no real input, no focus needed - and each press is checked by
    /// reading the word again. A key that changes nothing (a menu open in the game eats it) stops the walk with why.
    /// </summary>
    public static class CameraView
    {
        public enum View { FirstPerson = 0, ThirdPerson = 1, FreeRoam = 2, EditCamera = 3, Dolly = 4 }

        const int CamMode = 0xD4C00, FreeMode = 0xD4C04;

        public static string Name(View v)
        {
            switch (v)
            {
                case View.FirstPerson: return "1st person";
                case View.ThirdPerson: return "3rd person";
                case View.FreeRoam: return "Free Roam";
                case View.EditCamera: return "Edit Camera";
                default: return "Dolly Camera";
            }
        }

        static long Theater()
        {
            string s = BridgeClient.Ask("stats", 800, 2);
            if (s == null) return 0;
            int i = s.IndexOf("theater 0x", StringComparison.Ordinal);
            if (i < 0) return 0;
            long v; return long.TryParse(s.Substring(i + 10).Split(' ')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        static int ReadInt(long addr)
        {
            string h = BridgeClient.Ask("read " + addr.ToString("X") + " 4", 800, 2);
            if (h == null || h.Length != 8) return int.MinValue;
            var b = new byte[4];
            for (int i = 0; i < 4; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
            return BitConverter.ToInt32(b, 0);
        }

        /// <summary>The camera mode (0-2) and FREE CAMERA's own mode (0 Free Roam, 1 Edit Camera, 2 Dolly), or false.</summary>
        public static bool Read(out int cam, out int free)
        {
            cam = free = -1;
            long th = Theater();
            if (th == 0) return false;
            int c = ReadInt(th + CamMode), f = ReadInt(th + FreeMode);
            if (c == int.MinValue || f == int.MinValue) return false;
            cam = c & 0xFF; free = f;
            return true;
        }

        /// <summary>The view now, or null (no film, or not readable).</summary>
        public static View? Now()
        {
            int cam, free;
            if (!Read(out cam, out free)) return null;
            return Of(cam, free);
        }

        static View? Of(int cam, int free)
        {
            if (cam == 0) return View.FirstPerson;
            if (cam == 1) return View.ThirdPerson;
            if (cam != 2) return null;
            return free == 0 ? View.FreeRoam : free == 1 ? View.EditCamera : free == 2 ? View.Dolly : (View?)null;
        }

        static int FreeOf(View v) { return v == View.FreeRoam ? 0 : v == View.EditCamera ? 1 : 2; }

        /// <summary>
        /// The game to a view, from any other: CODCAST off when it needs FREE CAMERA's modes, F3 until the camera mode is
        /// right, then right-click until FREE CAMERA's own mode is. "ok ..." when it is there (read back), else why not.
        /// Off the window's thread: each press waits for the game to take it.
        /// </summary>
        public static string To(View want)
        {
            long th = Theater();
            if (th == 0) return "no film is up in the game";
            IntPtr w = FindWindow("CoDBlackOps", IntPtr.Zero);
            if (w == IntPtr.Zero) return "the game's window was not found";
            int cam, free;
            if (!Read(out cam, out free)) return "the camera words could not be read";
            View? was = Of(cam, free);
            bool freeKind = want == View.FreeRoam || want == View.EditCamera || want == View.Dolly;
            string steps = "";

            if (freeKind)
            {
                // CODCAST has no FREE CAMERA modes: the regular theater first (the bit the batch flips per take)
                using (var clock = new GameClock())
                {
                    if (clock.Attach() && clock.InTheater && clock.IsCodcast)
                    {
                        if (!clock.SetCodcast(false)) return "the film is in CODCAST and could not be switched to the regular theater: " + clock.Error;
                        Thread.Sleep(400);
                        if (clock.IsCodcast) return "the film is in CODCAST and the switch to the regular theater did not take";
                        steps += "CODCAST off; ";
                    }
                }
            }

            int wantCam = want == View.FirstPerson ? 0 : want == View.ThirdPerson ? 1 : 2;
            for (int i = 0; i < 4 && cam != wantCam; i++)
            {
                int before = cam;
                Key(w, 0x72);                                                    // F3
                if (!WaitFor(() => { int c, f; return Read(out c, out f) && c != before; }, 1500))
                    return "F3 changed nothing (is a menu open in the game? Esc closes it)" + Was(was);
                Read(out cam, out free);
                steps += "F3; ";
            }
            if (cam != wantCam) return "the camera mode did not reach " + Name(want) + Was(was);

            if (freeKind)
            {
                int wantFree = FreeOf(want);
                for (int i = 0; i < 4 && free != wantFree; i++)
                {
                    int before = free;
                    RightClick(w);
                    if (!WaitFor(() => { int c, f; return Read(out c, out f) && f != before; }, 1500))
                        return "a right-click changed nothing (is a menu open in the game? Esc closes it)" + Was(was);
                    Read(out cam, out free);
                    steps += "right-click; ";
                }
                if (free != wantFree) return "FREE CAMERA did not reach " + Name(want) + Was(was);
            }
            View? now = Of(cam, free);
            if (now != want) return "the game reads " + (now.HasValue ? Name(now.Value) : "something else") + ", not " + Name(want);
            return "ok " + Name(want) + (steps.Length == 0 ? " (it was already)" : " (" + steps.TrimEnd(' ', ';') + ")");
        }

        static string Was(View? v) { return v.HasValue ? " (it was in " + Name(v.Value) + ")" : ""; }

        static bool WaitFor(Func<bool> done, int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until) { if (done()) return true; Thread.Sleep(60); }
            return done();
        }

        static void Key(IntPtr w, int vk)
        {
            uint sc = MapVirtualKey((uint)vk, 0);
            PostMessage(w, 0x0100, (IntPtr)vk, (IntPtr)(1 | (int)(sc << 16)));
            Thread.Sleep(40);
            PostMessage(w, 0x0101, (IntPtr)vk, new IntPtr(unchecked((int)(1 | (sc << 16) | (3u << 30)))));
        }

        static void RightClick(IntPtr w)
        {
            RECT r;
            GetClientRect(w, out r);
            var lp = (IntPtr)(((r.B / 2) << 16) | (r.R / 2 & 0xFFFF));
            PostMessage(w, 0x0204, (IntPtr)2, lp);                              // WM_RBUTTONDOWN, MK_RBUTTON
            Thread.Sleep(60);
            PostMessage(w, 0x0205, IntPtr.Zero, lp);                            // WM_RBUTTONUP
        }

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, IntPtr title);
    }
}
