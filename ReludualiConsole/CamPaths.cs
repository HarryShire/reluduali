using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>One of the theater's dolly markers, as the game keeps it (docs\dolly-markers.md, found 2026-10-12).</summary>
    public sealed class DollyMarker
    {
        public int Tick;                 // film time, ms of the demo's own clock
        public double X, Y, Z;           // position, game units
        public double LX, LY, LZ;        // look-at = position + 10 x forward
        public double Pitch, Yaw;        // degrees: pitch positive looks down; yaw 0 = +X, toward +Y
        public double Roll;              // degrees, + lifts the view's left side
        public double Fov;               // the game's FOV (cg_fov's convention: horizontal at 4:3)
        public byte[] Raw;               // all 96 bytes as read, so the words not decoded yet survive
    }

    /// <summary>A camera track: RipperBridge's "cam path" file, one pose per line, "tick x y z pitch yaw roll fov".</summary>
    public sealed class CamTrack
    {
        public int[] Ticks;
        public double[][] Rows;          // x y z pitch yaw roll fov

        public int First { get { return Ticks[0]; } }
        public int Last { get { return Ticks[Ticks.Length - 1]; } }
        public double Seconds { get { return (Last - First) / 1000.0; } }

        /// <summary>The pose at a tick: linear between samples, the first and last held outside them (as RipperBridge plays it).</summary>
        public void Sample(double tick, double[] pose)
        {
            int n = Ticks.Length;
            if (tick <= Ticks[0]) { Array.Copy(Rows[0], pose, 7); return; }
            if (tick >= Ticks[n - 1]) { Array.Copy(Rows[n - 1], pose, 7); return; }
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (Ticks[mid] <= tick) lo = mid; else hi = mid; }
            double u = (tick - Ticks[lo]) / (double)(Ticks[hi] - Ticks[lo]);
            for (int k = 0; k < 7; k++) pose[k] = Rows[lo][k] + (Rows[hi][k] - Rows[lo][k]) * u;
        }

        public static CamTrack Read(string path)
        {
            var ticks = new List<int>();
            var rows = new List<double[]>();
            foreach (string line in File.ReadAllLines(path))
            {
                string s = line.Trim();
                if (s.Length == 0 || s[0] == '#') continue;
                string[] f = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 8) continue;
                ticks.Add(int.Parse(f[0], CultureInfo.InvariantCulture));
                var r = new double[7];
                for (int k = 0; k < 7; k++) r[k] = double.Parse(f[k + 1], CultureInfo.InvariantCulture);
                rows.Add(r);
            }
            if (ticks.Count == 0) throw new InvalidDataException("no samples in " + path);
            return new CamTrack { Ticks = ticks.ToArray(), Rows = rows.ToArray() };
        }

        public void Write(string path, string header)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("# reluduali-camtrack 1: ").Append(header).Append('\n');
            for (int i = 0; i < Ticks.Length; i++)
            {
                double[] r = Rows[i];
                sb.Append(Ticks[i].ToString(inv)).Append(' ')
                  .Append(r[0].ToString("F4", inv)).Append(' ').Append(r[1].ToString("F4", inv)).Append(' ').Append(r[2].ToString("F4", inv)).Append(' ')
                  .Append(r[3].ToString("F4", inv)).Append(' ').Append(r[4].ToString("F4", inv)).Append(' ').Append(r[5].ToString("F4", inv)).Append(' ')
                  .Append(r[6].ToString("F3", inv)).Append('\n');
            }
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }

    /// <summary>
    /// The CAM QUEUE's reads of the game and its path maths (2026-10-14). Everything here READS the game; nothing writes
    /// to it (Mycelium: "I do clear my markers" - the Console never touches them).
    /// </summary>
    public static class CamPaths
    {
        // The markers: an array inside the theater's own allocation, 0x60 bytes each, and their count (docs\dolly-markers.md).
        public const int MarkerArray = 0xED440, MarkerStride = 0x60, MarkerCount = 0xEE708, MaxMarkers = 64;
        // The film the game is playing, as {data pointer, size}: what RipperBridge's loaddemo fills in.
        public const int FilmRecord = 0x2E046D0;

        /// <summary>The markers placed in the theater now (empty list when there are none), or null with why.</summary>
        public static List<DollyMarker> ReadMarkers(GameClock clock, out string why, out string signature)
        {
            why = ""; signature = "";
            if (clock == null || !clock.Attached) { why = "the game is not running"; return null; }
            int theater = clock.TheaterBase;
            if (theater == 0 || theater == int.MinValue) { why = "no film in the theater"; return null; }
            byte[] cnt = clock.ReadBytes((uint)theater + (long)MarkerCount, 4);
            if (cnt == null) { why = "could not read the marker count"; return null; }
            int n = BitConverter.ToInt32(cnt, 0);
            if (n < 0 || n > MaxMarkers) { why = "the marker count reads " + n + " (not a film's?)"; return null; }
            var list = new List<DollyMarker>();
            if (n == 0) { signature = theater.ToString("X") + ":0"; return list; }
            byte[] block = clock.ReadBytes((uint)theater + (long)MarkerArray, n * MarkerStride);
            if (block == null) { why = "could not read the markers"; return null; }
            // A marker placed without zooming keeps 0 in its FOV word (sandbox, 0396b): it plays at the theater's FOV,
            // theater + 0x8 - which holds the FOV (90), or the zoom value (FOV - 360) while a dolly tool drives it.
            double theaterFov = 90;
            byte[] tf = clock.ReadBytes((uint)theater + 8L, 4);
            if (tf != null) theaterFov = FovOf(BitConverter.ToSingle(tf, 0), 90);
            var sig = new StringBuilder(theater.ToString("X")).Append(':').Append(n);
            for (int i = 0; i < n; i++)
            {
                DollyMarker m = Decode(block, i * MarkerStride, theaterFov);
                list.Add(m);
                // only the decoded words: word 1 flips while a marker is being placed and would rebuild for nothing
                sig.Append('|').Append(m.Tick).Append(',').Append(BitConverter.ToString(block, i * MarkerStride + 8, 24))
                   .Append(',').Append(BitConverter.ToString(block, i * MarkerStride + 60, 4)).Append(',').Append(BitConverter.ToString(block, i * MarkerStride + 72, 4));
            }
            signature = sig.ToString();
            return list;
        }

        /// <summary>A FOV word as the theater keeps it: FOV - 360 (the zoom, -359..-1), a plain FOV (1..179), or anything
        /// else (0: never zoomed) -> the fallback.</summary>
        public static double FovOf(double v, double fallback)
        {
            if (v <= -181 && v >= -359) return v + 360.0;
            if (v >= 1 && v <= 179) return v;
            return fallback;
        }

        /// <summary>Word 0 tick; 2-4 position; 5-7 look-at (position + 10 x forward); 15 roll; 18 FOV - 360 (0 when the
        /// marker was never zoomed: then the theater's FOV).</summary>
        public static DollyMarker Decode(byte[] b, int o, double theaterFov = 90)
        {
            var m = new DollyMarker
            {
                Tick = BitConverter.ToInt32(b, o),
                X = BitConverter.ToSingle(b, o + 8), Y = BitConverter.ToSingle(b, o + 12), Z = BitConverter.ToSingle(b, o + 16),
                LX = BitConverter.ToSingle(b, o + 20), LY = BitConverter.ToSingle(b, o + 24), LZ = BitConverter.ToSingle(b, o + 28),
                Roll = BitConverter.ToSingle(b, o + 60),
                Fov = FovOf(BitConverter.ToSingle(b, o + 72), theaterFov),
                Raw = new byte[MarkerStride]
            };
            Array.Copy(b, o, m.Raw, 0, MarkerStride);
            AnglesFromLook(m);
            return m;
        }

        private static void AnglesFromLook(DollyMarker m)
        {
            double fx = m.LX - m.X, fy = m.LY - m.Y, fz = m.LZ - m.Z;
            double len = Math.Sqrt(fx * fx + fy * fy + fz * fz);
            if (len < 1e-9) { m.Pitch = 0; m.Yaw = 0; return; }
            m.Pitch = -Math.Asin(Math.Max(-1, Math.Min(1, fz / len))) * 180 / Math.PI;
            m.Yaw = Math.Atan2(fy, fx) * 180 / Math.PI;
        }

        private static double CatmullRom(double p0, double p1, double p2, double p3, double t)
        {
            double t2 = t * t, t3 = t2 * t;
            return 0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
        }

        /// <summary>
        /// The markers as a track, exactly as tools\memwatch\markers_to_track.py makes it: a Catmull-Rom curve through
        /// the positions and the (unwrapped) angles, roll and FOV linear between markers. Measured 2026-10-14 against the
        /// game's own playback of 9 markers on Mycelium's game: 2.1 units median off over a 1,884-unit path, FOV 0.13 deg,
        /// roll 0.05 deg - so a queued path records the move he saw. Markers are taken in film order; two at the same
        /// tick keep the first. Null with why when fewer than 2 remain.
        /// </summary>
        public static CamTrack TrackFromMarkers(IList<DollyMarker> markers, int step, out string why)
        {
            why = "";
            var ms = markers.OrderBy(m => m.Tick).ToList();
            bool sorted = true;
            for (int i = 1; i < markers.Count; i++) if (markers[i].Tick < markers[i - 1].Tick) sorted = false;
            var keep = new List<DollyMarker>();
            int dupes = 0;
            foreach (DollyMarker m in ms) { if (keep.Count > 0 && keep[keep.Count - 1].Tick == m.Tick) { dupes++; continue; } keep.Add(m); }
            if (!sorted) why += "the markers were not in film order (taken in film order). ";
            if (dupes > 0) why += dupes + " marker(s) at the same film time as another (the first kept). ";
            if (keep.Count < 2) { why += "a path needs 2 markers at different film times"; return null; }

            int n = keep.Count;
            var rows = new double[n][];
            double prevYaw = 0;
            for (int i = 0; i < n; i++)
            {
                DollyMarker m = keep[i];
                double yaw = m.Yaw;
                if (i > 0)
                {
                    while (yaw - prevYaw > 180) yaw -= 360;
                    while (yaw - prevYaw < -180) yaw += 360;
                }
                prevYaw = yaw;
                rows[i] = new[] { m.Tick, m.X, m.Y, m.Z, m.Pitch, yaw, m.Roll, m.Fov };
            }
            var ticks = new List<int>();
            var poses = new List<double[]>();
            for (int i = 0; i < n - 1; i++)
            {
                double[] r0 = rows[Math.Max(0, i - 1)], r1 = rows[i], r2 = rows[i + 1], r3 = rows[Math.Min(n - 1, i + 2)];
                int t0 = (int)r1[0], t1 = (int)r2[0];
                int tick = i == 0 ? t0 : ((t0 + step - 1) / step) * step;
                while (tick < t1 || (i == n - 2 && tick <= t1))
                {
                    double u = (tick - t0) / (double)(t1 - t0);
                    var v = new double[7];
                    for (int j = 1; j <= 5; j++) v[j - 1] = CatmullRom(r0[j], r1[j], r2[j], r3[j], u);
                    v[5] = r1[6] + (r2[6] - r1[6]) * u;
                    v[6] = r1[7] + (r2[7] - r1[7]) * u;
                    ticks.Add(tick);
                    poses.Add(v);
                    tick += step;
                }
            }
            return new CamTrack { Ticks = ticks.ToArray(), Rows = poses.ToArray() };
        }

        /// <summary>Distance and film time between consecutive markers, and how uneven the speed is (Mycelium's rule: "the
        /// time and distance travelled needs to be relatively uniform across the placed camera keyframes").</summary>
        public static string SpeedReport(IList<DollyMarker> markers, out double spread)
        {
            spread = 0;
            var ms = markers.OrderBy(m => m.Tick).ToList();
            var speeds = new List<double>();
            for (int i = 1; i < ms.Count; i++)
            {
                double dt = (ms[i].Tick - ms[i - 1].Tick) / 1000.0;
                if (dt <= 0) continue;
                double dx = ms[i].X - ms[i - 1].X, dy = ms[i].Y - ms[i - 1].Y, dz = ms[i].Z - ms[i - 1].Z;
                speeds.Add(Math.Sqrt(dx * dx + dy * dy + dz * dz) / dt);
            }
            if (speeds.Count == 0) return "";
            double lo = speeds.Min(), hi = speeds.Max();
            spread = lo > 0 ? hi / lo - 1 : 0;
            return "speed between markers " + lo.ToString("0") + "-" + hi.ToString("0") + " u/s"
                   + (speeds.Count < 2 ? "" : spread <= 0.2 ? " (even)" : " (" + (spread * 100).ToString("0") + "% uneven)");
        }

        // ---------------------------------------------------------------- markers.json

        /// <summary>The markers as JSON, in the format tools\memwatch reads (write_markers.py, markers_to_track.py),
        /// with every marker's raw 96 bytes beside it.</summary>
        public static void WriteMarkersJson(string path, IList<DollyMarker> markers, string demo, string map)
        {
            var list = new List<object>();
            foreach (DollyMarker m in markers)
                list.Add(new Dictionary<string, object>
                {
                    { "tick", m.Tick }, { "pos", new[] { m.X, m.Y, m.Z } }, { "look", new[] { m.LX, m.LY, m.LZ } },
                    { "pitch", m.Pitch }, { "yaw", m.Yaw }, { "roll", m.Roll }, { "fov", m.Fov },
                    { "raw", m.Raw == null ? "" : BitConverter.ToString(m.Raw).Replace("-", "").ToLowerInvariant() }
                });
            var doc = new Dictionary<string, object>
            {
                { "format", "reluduali-markers" }, { "version", 1 }, { "read", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                { "demo", demo ?? "" }, { "map", map ?? "" }, { "markers", list }
            };
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(doc), new UTF8Encoding(false));
        }

        public static List<DollyMarker> ReadMarkersJson(string path)
        {
            var doc = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            var list = new List<DollyMarker>();
            foreach (object o in (System.Collections.ArrayList)doc["markers"])
            {
                var d = (Dictionary<string, object>)o;
                Func<string, int, double> at = (k, i) => Convert.ToDouble(((System.Collections.ArrayList)d[k])[i], CultureInfo.InvariantCulture);
                var m = new DollyMarker
                {
                    Tick = Convert.ToInt32(d["tick"], CultureInfo.InvariantCulture),
                    X = at("pos", 0), Y = at("pos", 1), Z = at("pos", 2),
                    Roll = d.ContainsKey("roll") ? Convert.ToDouble(d["roll"], CultureInfo.InvariantCulture) : 0,
                    Fov = d.ContainsKey("fov") ? Convert.ToDouble(d["fov"], CultureInfo.InvariantCulture) : 65
                };
                if (d.ContainsKey("look")) { m.LX = at("look", 0); m.LY = at("look", 1); m.LZ = at("look", 2); }
                if (d.ContainsKey("pitch") && d.ContainsKey("yaw"))
                {
                    m.Pitch = Convert.ToDouble(d["pitch"], CultureInfo.InvariantCulture);
                    m.Yaw = Convert.ToDouble(d["yaw"], CultureInfo.InvariantCulture);
                }
                else AnglesFromLook(m);
                list.Add(m);
            }
            return list;
        }

        // ---------------------------------------------------------------- which film

        /// <summary>
        /// The demo file the game is playing, found by matching the film in the game's memory (its size, then its first
        /// 64 KB) against the candidate files. Null with why when none matches.
        /// </summary>
        public static string FilmInGame(GameClock clock, IEnumerable<string> candidates, out string why)
        {
            why = "";
            if (clock == null || !clock.Attached) { why = "the game is not running"; return null; }
            byte[] rec = clock.ReadBytes(clock.ModuleBaseAddress + FilmRecord, 8);
            if (rec == null) { why = "could not read the film record"; return null; }
            uint data = BitConverter.ToUInt32(rec, 0);
            int size = BitConverter.ToInt32(rec, 4);
            if (data == 0 || size <= 0) { why = "the game holds no film record (loaded some other way?)"; return null; }
            int n = Math.Min(size, 65536);
            byte[] head = clock.ReadBytes(data, n);
            if (head == null) { why = "could not read the film in memory"; return null; }
            int sameSize = 0;
            foreach (string path in candidates)
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists || fi.Length != size) continue;
                    sameSize++;
                    var buf = new byte[n];
                    using (FileStream f = fi.OpenRead())
                    {
                        int got = 0;
                        while (got < n) { int r = f.Read(buf, got, n - got); if (r <= 0) break; got += r; }
                        if (got != n) continue;
                    }
                    bool same = true;
                    for (int i = 0; i < n && same; i++) same = buf[i] == head[i];
                    if (same) return path;
                }
                catch { }
            }
            why = "no demo file matches the film in the game (" + size + " bytes; " + sameSize + " file(s) of that size)";
            return null;
        }

        /// <summary>"tdm_mp_raid_8_30_2026_15_16.demo" -> "raid".</summary>
        public static string ShortMap(string demoPath)
        {
            string map = Looks.MapOf(demoPath) ?? "";
            if (map.StartsWith("mp_", StringComparison.OrdinalIgnoreCase)) map = map.Substring(3);
            return map.Replace("_", " ");
        }

        /// <summary>The theater clock for a tick, with the zero most films measured (2300): only a label.</summary>
        public static string ClockLabel(int tick)
        {
            int s = Math.Max(0, (tick - 2300) / 1000);
            return (s / 60) + ":" + (s % 60).ToString("00");
        }
    }
}
