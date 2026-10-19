using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>
    /// Apply to game (2026-10-18). Mycelium, testing the Designer: he added a camera between markers 1 and 2 in the Cam Queue's
    /// 3D view, and "the third camera did not get created inside of the game"; "There needs to be ... Apply camera button.
    /// It should either delete the current markers in-game and just add what it has inside of the console. Or ... keep
    /// whatever is added in here real-time with whatever is in the game"; later, "it adds in the game either automatically or
    /// after you click apply". (Until now the Designer never wrote his markers: "I do clear my markers", 2026-10-14. His later
    /// words replace that.)
    ///
    /// The game's own way, so its bookkeeping is its own (docs\dolly-markers.md): every marker removed with
    /// demo_removedollycammarker -1; then for each of ours, in film order, the film taken to its time (the timeline's seek,
    /// then a slow last step) and demo_adddollycammarker - the game places it with its own snapshot, so F and the Jump land on
    /// it; then its decoded words written as ours: time, position, look-at, roll, FOV (as tools\memwatch\write_markers.py
    /// did on 10-13: the game played those back, roll and zoom included). Every other word stays the game's.
    /// The game must be in FREE CAMERA's Edit Camera (where markers are placed by hand too); nothing is changed otherwise.
    /// </summary>
    public static class MarkerApply
    {
        const int ModeByte = 0xD4C00, SubMode = 0xD4C04;

        static long Theater()
        {
            string s = BridgeClient.Ask("stats", 800, 2);
            if (s == null) return 0;
            int i = s.IndexOf("theater 0x", StringComparison.Ordinal);
            if (i < 0) return 0;
            long v; return long.TryParse(s.Substring(i + 10).Split(' ')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        static int Tick() { string s = BridgeClient.Ask("stats", 400, 1); return s == null ? int.MinValue : (int)BridgeClient.Field(s, "tick"); }

        static byte[] Read(long addr, int n)
        {
            string h = BridgeClient.Ask("read " + addr.ToString("X") + " " + n.ToString(CultureInfo.InvariantCulture), 800, 2);
            if (h == null || h.Length != n * 2) return null;
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
            return b;
        }

        static bool Write(long addr, byte[] b)
        {
            var sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("X2"));
            string r = BridgeClient.Ask("write " + addr.ToString("X") + " " + sb, 800, 2);
            return r == "ok";
        }

        static int Count(long th) { byte[] c = Read(th + CamPaths.MarkerCount, 4); return c == null ? -1 : BitConverter.ToInt32(c, 0); }

        static bool WaitCount(long th, int want, int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until) { if (Count(th) == want) return true; Thread.Sleep(100); }
            return Count(th) == want;
        }

        /// <summary>True when the game is in FREE CAMERA's Edit Camera (mode 2, its own mode 1).</summary>
        public static bool InEditCamera(long th)
        {
            byte[] m = Read(th + ModeByte, 1), s = Read(th + SubMode, 4);
            return m != null && s != null && m[0] == 2 && BitConverter.ToInt32(s, 0) == 1;
        }

        /// <summary>The film to a tick and paused, whatever layout the Designer is in (the timeline under the game is only there
        /// in the Workspace): back, or far ahead, through the keyframe RipperBridge kept at or before it; then played forward,
        /// fast and then slow, so the marker's snapshot is the moment it names. int.MinValue with <paramref name="why"/> when
        /// the film cannot get there (no keyframe before it in this load).</summary>
        /// <summary>The film played up to a tick from a keyframe just jumped to (the Cam Queue's marker Jump lets the film go).</summary>
        public static int GoToForJump(int tick, out string why) { return GoTo(tick, out why); }

        static int GoTo(int tick, out string why)
        {
            why = null;
            BridgeClient.Ask("cmd demo_pause 1", 800, 1);
            Thread.Sleep(150);
            int t = Tick();
            if (t == int.MinValue) { why = "the film did not answer"; return t; }
            if (t > tick + 60 || tick - t > 4000)
            {
                string r = BridgeClient.Ask("kf jump " + tick.ToString(CultureInfo.InvariantCulture), 10000, 1);
                if (r != null && r.StartsWith("ok", StringComparison.Ordinal)) { Thread.Sleep(300); t = Tick(); }
                else if (t > tick + 60)
                {
                    why = "the film is past " + CamPaths.ClockLabel(tick) + " and has no keyframe before it to go back to (play the film once from before the path: keyframes are kept as it plays)";
                    return int.MinValue;
                }
            }
            Func<double, int, int> play = (speed, stopAt) =>
            {
                BridgeClient.Ask("setdvar timescale " + speed.ToString(CultureInfo.InvariantCulture), 800, 1);
                BridgeClient.Ask("cmd demo_pause 0", 800, 1);
                var until = DateTime.UtcNow.AddSeconds(90);
                int now = Tick();
                while (DateTime.UtcNow < until) { now = Tick(); if (now == int.MinValue || now >= stopAt) break; Thread.Sleep(5); }
                BridgeClient.Ask("cmd demo_pause 1", 800, 1);
                return now;
            };
            if (t < tick - 1500) t = play(Math.Min(6, Math.Max(1, (tick - t) / 1500.0)), tick - 800);
            if (t != int.MinValue && t < tick - 5) t = play(0.25, tick - 5);
            BridgeClient.Ask("setdvar timescale 1", 800, 1);
            Thread.Sleep(150);
            return Tick();
        }

        /// <summary>The markers in the game replaced by these (film order). The result in words; <paramref name="progress"/> gets
        /// a line per marker.</summary>
        public static string Apply(IList<DollyMarker> markers, Action<string> progress)
        {
            // the film is played to each marker: the loop bracket must not take it back meanwhile
            using (LoopBracket.Hold()) return ApplyHeld(markers, progress);
        }

        static string ApplyHeld(IList<DollyMarker> markers, Action<string> progress)
        {
            var ms = markers.OrderBy(m => m.Tick).ToList();
            var keep = new List<DollyMarker>();
            foreach (DollyMarker m in ms) if (keep.Count == 0 || keep[keep.Count - 1].Tick != m.Tick) keep.Add(m);
            if (keep.Count < 2) return "Not applied: a path needs 2 markers at different film times.";
            if (keep.Count > 50) return "Not applied: the game takes at most 50 markers (" + keep.Count + " here).";
            long th = Theater();
            if (th == 0) return "Not applied: no film is up in the game.";
            if (!InEditCamera(th))
            {
                // the game's own keys to Edit Camera from wherever it was (CameraView, 2026-10-18 - until now he had to)
                string to = CameraView.To(CameraView.View.EditCamera);
                if (!to.StartsWith("ok", StringComparison.Ordinal) || !InEditCamera(th))
                    return "Not applied: the game could not be put in Edit Camera (" + to + "). Nothing in the game was changed.";
                if (progress != null) progress("the game in Edit Camera: " + to.Substring(3));
            }
            BridgeClient.Ask("cam off", 2000, 1);            // a preview's camera would fight the markers

            int was = Count(th);
            BridgeClient.Ask("cmd demo_removedollycammarker -1", 1000, 2);
            if (!WaitCount(th, 0, 3000)) return "Not applied: the game did not clear its " + was + " marker(s) (it has " + Count(th) + ").";
            int done = 0;
            foreach (DollyMarker m in keep)
            {
                string why;
                int at = GoTo(m.Tick, out why);
                if (at == int.MinValue) return "Stopped after " + done + " of " + keep.Count + ": " + why + ".";
                if (Theater() != th) return "Stopped after " + done + " of " + keep.Count + ": the film was loaded again on the way (its markers went with it).";
                BridgeClient.Ask("cmd demo_adddollycammarker", 1000, 2);
                if (!WaitCount(th, done + 1, 3000))
                    return "Stopped after " + done + " of " + keep.Count + ": the game did not add a marker at " + CamPaths.ClockLabel(m.Tick) + " (is it still in Edit Camera?).";
                long slot = th + CamPaths.MarkerArray + (long)CamPaths.MarkerStride * done;
                byte[] rec = Read(slot, CamPaths.MarkerStride);
                if (rec == null) return "Stopped after " + done + " of " + keep.Count + ": could not read the marker the game made.";
                Fill(rec, m);
                if (!Write(slot, rec)) return "Stopped after " + done + " of " + keep.Count + ": could not write marker " + (done + 1) + ".";
                done++;
                if (progress != null) progress("marker " + done + " of " + keep.Count + " at " + CamPaths.ClockLabel(m.Tick) + " (the film placed it at tick " + at + ")");
            }
            string back;
            GoTo(keep[0].Tick, out back);
            return "Applied: the game has your " + done + " markers now (" + CamPaths.ClockLabel(keep[0].Tick) + " to " + CamPaths.ClockLabel(keep[keep.Count - 1].Tick)
                   + "), the film at the first. Its own " + was + " were replaced.";
        }

        /// <summary>A marker's decoded words written over the game's record: 0 the time, 2-4 position, 5-7 look-at
        /// (position + 10 x forward, from pitch and yaw), 15 roll, 18 FOV - 360. The rest stays the game's.</summary>
        public static void Fill(byte[] rec, DollyMarker m)
        {
            double p = m.Pitch * Math.PI / 180, y = m.Yaw * Math.PI / 180;
            double fx = Math.Cos(p) * Math.Cos(y), fy = Math.Cos(p) * Math.Sin(y), fz = -Math.Sin(p);
            Put(rec, 0, BitConverter.GetBytes(m.Tick));
            Put(rec, 8, F(m.X)); Put(rec, 12, F(m.Y)); Put(rec, 16, F(m.Z));
            Put(rec, 20, F(m.X + 10 * fx)); Put(rec, 24, F(m.Y + 10 * fy)); Put(rec, 28, F(m.Z + 10 * fz));
            Put(rec, 60, F(m.Roll));
            double fov = m.Fov >= 1 && m.Fov <= 179 ? m.Fov : 90;
            Put(rec, 72, F(fov - 360.0));
        }

        static byte[] F(double v) { return BitConverter.GetBytes((float)v); }
        static void Put(byte[] rec, int at, byte[] v) { Array.Copy(v, 0, rec, at, 4); }
    }
}
