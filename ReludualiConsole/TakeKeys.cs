using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>One keyframed parameter of a take: a slider of a page, its keys (film tick, values).</summary>
    public sealed class KeyParam
    {
        public string Page = "", Knob = "", Line = "";
        public bool Whole;
        public string[] Parts = new string[0];
        public List<KnobPanel.MoveKey> Keys = new List<KnobPanel.MoveKey>();
        public string Label { get { return Title(Page) + " · " + Knob; } }
        static string Title(string page) { return page.Length == 0 ? page : page.Substring(0, 1).ToUpperInvariant() + page.Substring(1).ToLowerInvariant(); }
    }

    /// <summary>
    /// A take's keyframes, every parameter (Mycelium's 10-18 test take, 36:14: "every single parameter can be keyframed. I could
    /// keyframe every single parameter and put it on the timeline with the cam paths and then I could add those keyframes
    /// inside of the actual batch editor so then when you record your POV and your 19 million cinematics ... all that
    /// keyframe data is saved and ready for the batch").
    ///
    /// A queue row's keys live beside the queue, keys\&lt;its name&gt;.keys.json (TakeList.KeysFile): per slider its page, its
    /// label, the line it sends and its keys. A slider's right-click puts a key on the take picked (at the film's time now,
    /// its values now); the timeline shows them under that take's lane, where they move, and the effects strip edits them.
    /// The batch bakes them into the recording frame by frame (KnobPanel.BakeMoves, RipperBridge's params events), so a key
    /// lands on its own frame. "Play keys" plays the picked take's keys while the film plays, as the pages' moves do.
    /// The camera's own keys are the cinematic's markers (their time, roll and FOV); those stay in its markers file.
    /// </summary>
    public static class TakeKeys
    {
        /// <summary>Raised on the window's thread when any take's keys changed.</summary>
        public static event Action Changed;

        static readonly Dictionary<string, Tuple<DateTime, List<KeyParam>>> Cache = new Dictionary<string, Tuple<DateTime, List<KeyParam>>>(StringComparer.OrdinalIgnoreCase);

        public static string FileOf(int row)
        {
            CamQueue q = CamQueueControl.Queue;
            return q == null || row < 0 || row >= q.Rows.Count ? null : q.KeysOf(q.Rows[row]);
        }

        /// <summary>A take's keys (empty when it has none). Read again only when the file changed.</summary>
        public static List<KeyParam> Of(int row)
        {
            string f = FileOf(row);
            if (f == null || !File.Exists(f)) return new List<KeyParam>();
            DateTime t = File.GetLastWriteTimeUtc(f);
            Tuple<DateTime, List<KeyParam>> c;
            if (Cache.TryGetValue(f, out c) && c.Item1 == t) return c.Item2;
            List<KeyParam> ps;
            try { ps = Read(f); } catch { ps = new List<KeyParam>(); }
            Cache[f] = Tuple.Create(t, ps);
            return ps;
        }

        public static List<KeyParam> Read(string file)
        {
            var doc = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
            var list = new List<KeyParam>();
            object po;
            if (!doc.TryGetValue("params", out po) || !(po is System.Collections.ArrayList)) return list;
            foreach (object o in (System.Collections.ArrayList)po)
            {
                var d = (Dictionary<string, object>)o;
                Func<string, string> s = k => d.ContainsKey(k) && d[k] != null ? d[k].ToString() : "";
                var p = new KeyParam { Page = s("page"), Knob = s("knob"), Line = s("line"), Whole = s("whole") == "True" };
                if (d.ContainsKey("parts") && d["parts"] is System.Collections.ArrayList) p.Parts = ((System.Collections.ArrayList)d["parts"]).Cast<object>().Select(x => x == null ? "" : x.ToString()).ToArray();
                if (d.ContainsKey("keys") && d["keys"] is System.Collections.ArrayList)
                    foreach (object ko in (System.Collections.ArrayList)d["keys"])
                    {
                        var kd = (Dictionary<string, object>)ko;
                        var v = ((System.Collections.ArrayList)kd["v"]).Cast<object>().Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
                        p.Keys.Add(new KnobPanel.MoveKey { Tick = Convert.ToInt32(kd["tick"], CultureInfo.InvariantCulture), V = v });
                    }
                p.Keys.Sort((a, b) => a.Tick.CompareTo(b.Tick));
                if (p.Line.Length > 0) list.Add(p);
            }
            return list;
        }

        public static void Write(string file, string take, List<KeyParam> ps)
        {
            var list = ps.Where(p => p.Keys.Count > 0).Select(p => (object)new Dictionary<string, object>
            {
                { "page", p.Page }, { "knob", p.Knob }, { "line", p.Line }, { "whole", p.Whole }, { "parts", p.Parts },
                { "keys", p.Keys.OrderBy(k => k.Tick).Select(k => new Dictionary<string, object> { { "tick", k.Tick }, { "v", k.V } }).ToList() }
            }).ToList();
            var doc = new Dictionary<string, object> { { "format", "reluduali-keys" }, { "version", 1 }, { "take", take ?? "" }, { "params", list } };
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(doc), new UTF8Encoding(false));
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
            Cache.Remove(file);
        }

        static void Save(int row, List<KeyParam> ps)
        {
            CamQueue q = CamQueueControl.Queue;
            string f = FileOf(row);
            if (f == null) return;
            if (ps.All(p => p.Keys.Count == 0)) { if (File.Exists(f)) File.Delete(f); Cache.Remove(f); }
            else Write(f, q.Rows[row].Name, ps);
            Raise();
        }

        static void Raise() { Action a = Changed; if (a != null) a(); }

        static List<KeyParam> Copy(int row)
        {
            return Of(row).Select(p => new KeyParam { Page = p.Page, Knob = p.Knob, Line = p.Line, Whole = p.Whole, Parts = p.Parts,
                                                      Keys = p.Keys.Select(k => new KnobPanel.MoveKey { Tick = k.Tick, V = (double[])k.V.Clone() }).ToList() }).ToList();
        }

        /// <summary>A key on a take for a slider: its values now, at a film tick (one there already is replaced).</summary>
        public static string Add(int row, string page, Knob k, int tick)
        {
            CamQueue q = CamQueueControl.Queue;
            if (q == null || row < 0 || row >= q.Rows.Count) return "Pick a take first (a POV take or a cinematic: on the timeline, or on its queue page).";
            if (tick < 0) return "The film's time is not known: is a film up?";
            List<KeyParam> ps = Copy(row);
            KeyParam p = ps.FirstOrDefault(x => x.Page == page && x.Knob == k.Label);
            if (p == null) ps.Add(p = new KeyParam { Page = page, Knob = k.Label, Line = k.Line, Whole = k.Whole, Parts = k.Parts ?? new string[0] });
            p.Keys.RemoveAll(x => Math.Abs(x.Tick - tick) < 5);
            p.Keys.Add(new KnobPanel.MoveKey { Tick = tick, V = (double[])k.Values.Clone() });
            p.Keys.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            Save(row, ps);
            return "Key: " + p.Label + " = " + string.Join(" ", k.Values.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)).ToArray())
                   + " at " + CamPaths.ClockLabel(tick) + " on \"" + q.Rows[row].Name + "\" (" + p.Keys.Count + " key(s) for it).";
        }

        /// <summary>A key moved in time, its values changed, or (values null and tick -1) deleted.</summary>
        public static string Edit(int row, string label, int keyTick, int newTick, double[] values)
        {
            List<KeyParam> ps = Copy(row);
            KeyParam p = ps.FirstOrDefault(x => x.Label == label);
            if (p == null) return "that parameter has no keys on this take";
            KnobPanel.MoveKey k = p.Keys.FirstOrDefault(x => x.Tick == keyTick);
            if (k == null) return "no key there";
            if (newTick < 0 && values == null) { p.Keys.Remove(k); Save(row, ps); return p.Label + ": key at " + CamPaths.ClockLabel(keyTick) + " deleted"; }
            if (newTick >= 0) { p.Keys.RemoveAll(x => x != k && Math.Abs(x.Tick - newTick) < 5); k.Tick = newTick; }
            if (values != null && values.Length == k.V.Length) k.V = values;
            p.Keys.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            Save(row, ps);
            return p.Label + " at " + CamPaths.ClockLabel(k.Tick) + " = " + string.Join(" ", k.V.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)).ToArray());
        }

        /// <summary>Every key of a take moved by the same amount (the take moved on the timeline: its keys go with it).</summary>
        public static void Shift(int row, int by)
        {
            if (by == 0) return;
            List<KeyParam> ps = Copy(row);
            if (ps.Count == 0) return;
            foreach (KeyParam p in ps) foreach (KnobPanel.MoveKey k in p.Keys) k.Tick += by;
            Save(row, ps);
        }

        /// <summary>A page's recorded moves given to a take as its keys (the Moves card's "Give them to the take").</summary>
        public static string FromMoves(int row, string page, IEnumerable<KeyValuePair<Knob, List<KnobPanel.MoveKey>>> tracks)
        {
            CamQueue q = CamQueueControl.Queue;
            if (q == null || row < 0 || row >= q.Rows.Count) return "Pick a take first.";
            List<KeyParam> ps = Copy(row);
            int n = 0, keys = 0;
            foreach (var kv in tracks)
            {
                if (kv.Value.Count == 0) continue;
                ps.RemoveAll(x => x.Page == page && x.Knob == kv.Key.Label);
                ps.Add(new KeyParam { Page = page, Knob = kv.Key.Label, Line = kv.Key.Line, Whole = kv.Key.Whole, Parts = kv.Key.Parts ?? new string[0],
                                      Keys = kv.Value.Select(k => new KnobPanel.MoveKey { Tick = k.Tick, V = (double[])k.V.Clone() }).ToList() });
                n++; keys += kv.Value.Count;
            }
            if (n == 0) return "No moves recorded on this page.";
            Save(row, ps);
            return n + " slider(s), " + keys + " key(s) now on \"" + q.Rows[row].Name + "\": the batch records them with it.";
        }

        // ---------------------------------------------------------------- the take whose keys are worked on

        /// <summary>The take keys go to: the one picked on the timeline, else the Cam Queue's, else the POV Queue's (-1: none).</summary>
        public static int Target
        {
            get
            {
                TheaterTimeline tl = TheaterTimeline.Current;
                int r = tl != null ? tl.PickedRow : -1;
                if (r < 0) r = CamQueueControl.Picked(true);
                if (r < 0) r = CamQueueControl.Picked(false);
                return r;
            }
        }

        public static string TargetName
        {
            get { int r = Target; CamQueue q = CamQueueControl.Queue; return q != null && r >= 0 && r < q.Rows.Count ? q.Rows[r].Name : null; }
        }

        // ---------------------------------------------------------------- live: the picked take's keys as the film plays

        static volatile bool _playing;
        static Thread _player;

        public static bool Playing { get { return _playing; } }

        public static void Play(bool on)
        {
            _playing = on;
            if (on && (_player == null || !_player.IsAlive))
            {
                _player = new Thread(PlayLoop) { IsBackground = true, Name = "take keys" };
                _player.Start();
            }
            Raise();
        }

        static void PlayLoop()
        {
            var last = new Dictionary<string, double[]>();
            while (_playing)
            {
                int t = -1;
                try { string st = BridgeClient.Ask("stats", 300, 1); if (st != null && !st.Contains("theater 0x00000000")) t = (int)BridgeClient.Field(st, "tick"); } catch { }
                if (t >= 0 && !KnobPanel.ExactRecording && !BatchEngine.Recording)
                {
                    int tick = t;
                    try
                    {
                        Application.Current.Dispatcher.Invoke(new Action(() =>
                        {
                            int row = Target;
                            foreach (KeyParam p in Of(row))
                            {
                                double[] v = KnobPanel.ValueAt(p.Keys, tick);
                                double[] was;
                                if (v == null || (last.TryGetValue(p.Label, out was) && was.Length == v.Length && was.Select((x, j) => Math.Abs(x - v[j])).Max() < 1e-6)) continue;
                                if (KnobPanel.SetLive(p.Page, p.Knob, v)) last[p.Label] = v;
                            }
                        }));
                    }
                    catch { }
                }
                Thread.Sleep(30);
            }
        }
    }
}
