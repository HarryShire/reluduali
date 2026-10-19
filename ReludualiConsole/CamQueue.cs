using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>One row of a queue, kept as the text of its CSV fields so a round trip changes nothing it does not touch.</summary>
    public sealed class QueueRow
    {
        public string Folder = "", Pov = "", ClockIn = "", ClockOut = "", Note = "", CamPath = "", Kind = "", Name = "", Source = "",
                      Cfg = "", Fps = "", TickIn = "", TickOut = "", Demo = "", Before = "", After = "", Hud = "", Codcast = "", Look = "";
        // how it records, its own (2026-10-18: the Batch's take list; empty = from its preset, its look or its kind)
        public string Depth = "", DepthDist = "", Plate = "", NoPlayers = "", PlayersOnly = "", Codec = "", Preset = "";

        /// <summary>The row's fields in TakeList.Header's order (after "take").</summary>
        public string[] Fields()
        {
            return new[] { Folder, Pov, ClockIn, ClockOut, Note, CamPath, Kind, Name, Source, Cfg, Fps, TickIn, TickOut, Demo, Before, After, Hud, Codcast, Look,
                           Depth, DepthDist, Plate, NoPlayers, PlayersOnly, Codec, Preset };
        }

        /// <summary>A recording setting of its own by its key (RecordingSettings.Keys, or "preset"); false for an unknown key.</summary>
        public bool SetOwn(string key, string value)
        {
            value = (value ?? "").Trim();
            switch ((key ?? "").ToLowerInvariant())
            {
                case "fps": Fps = value; return true;
                case "before": Before = value; return true;
                case "after": After = value; return true;
                case "hud": Hud = value; return true;
                case "codcast": Codcast = value; return true;
                case "depth": Depth = value; return true;
                case "depthdist": DepthDist = value; return true;
                case "plate": Plate = value; return true;
                case "noplayers": NoPlayers = value; return true;
                case "playersonly": PlayersOnly = value; return true;
                case "codec": Codec = value; return true;
                case "preset": Preset = value; return true;
                default: return false;
            }
        }

        public bool IsCine { get { return Kind == "cine" || (Kind.Length == 0 && CamPath.Length > 0); } }
        public int TickInValue { get { return ParseInt(TickIn); } }
        public int TickOutValue { get { return ParseInt(TickOut); } }
        public double Seconds { get { int a = TickInValue, b = TickOutValue; return a >= 0 && b > a ? (b - a) / 1000.0 : 0; } }

        public QueueRow Copy() { return (QueueRow)MemberwiseClone(); }

        private static int ParseInt(string s)
        {
            int v;
            return int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1;
        }
    }

    /// <summary>
    /// A CAM QUEUE (2026-10-14; Mycelium: "Que up 12+ Campaths per demo and then just have them record one after the other
    /// all at once at night", "accross multple demos alongside their POV's"). A queue is a folder of ordinary files:
    ///   queue.csv          the batch's own take list (TakeList.Header); its row order is the recording order
    ///   queue.txt          the defaults per kind: pov_cfg / cine_cfg, pov_fps / cine_fps, before / after, hud, codcast
    ///   pov.cfg, cine.cfg  the CFG sent before every POV / CINE row (a row may name its own instead)
    ///   paths\             each queued path: &lt;id&gt;.markers.json (as read from the game), &lt;id&gt;.camtrack.txt (what plays)
    ///   removed\           what Remove took out of the queue; nothing is ever deleted
    ///   status.txt         the night's progress, rewritten after every take (for a glance over Parsec)
    /// The Blender add-on and the generator write the same take list format, so all three sources share one queue.
    /// </summary>
    public sealed class CamQueue
    {
        public readonly string Dir;
        public readonly List<QueueRow> Rows = new List<QueueRow>();
        public Dictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Csv { get { return Path.Combine(Dir, "queue.csv"); } }
        public string DefaultsPath { get { return Path.Combine(Dir, TakeList.DefaultsFile); } }
        public string PathsDir { get { return Path.Combine(Dir, "paths"); } }
        public string RemovedDir { get { return Path.Combine(Dir, "removed"); } }
        public string StatusPath { get { return Path.Combine(Dir, "status.txt"); } }
        public string Name { get { return Path.GetFileName(Dir.TrimEnd('\\', '/')); } }

        private static readonly string[] Columns = TakeList.Header.Split(',');

        private CamQueue(string dir) { Dir = Path.GetFullPath(dir); }

        public static string DefaultRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Reluduali", "queues"); }
        }

        /// <summary>Opens a queue folder, making it (with its defaults and two CFGs) when it is new. A new queue's CFGs
        /// start as the BATCH tab's CFG box, so nothing he already sends is lost.</summary>
        public static CamQueue Open(string dir, string seedCfg)
        {
            var q = new CamQueue(dir);
            Directory.CreateDirectory(q.Dir);
            Directory.CreateDirectory(q.PathsDir);
            if (!File.Exists(q.DefaultsPath))
            {
                // Mycelium's default: "Usually the POV will be one CFG, and the CINES will be another ... so its easy to just get
                // in BO2 and start Queuing". Cinematics: 1000 fps (the money-shot study: the slowest quarter of slow-motion
                // needs 500+ fps of game time), half a second either side, HUD hidden. POV: the BATCH tab's own settings.
                File.WriteAllText(q.DefaultsPath,
                    "# CAM QUEUE defaults per kind: what a row gets when it has no value of its own (see TakeList.ReadDefaults)\n" +
                    "pov_cfg=pov.cfg\ncine_cfg=cine.cfg\n" +
                    "pov_fps=\ncine_fps=1000\n" +
                    "pov_before=\npov_after=\ncine_before=0.5\ncine_after=0.5\n" +
                    "pov_hud=tab\ncine_hud=hide\n" +
                    "# the film mode: POV takes as the BATCH tab says (CODCAST); cinematics in the regular theater\n" +
                    "pov_codcast=tab\ncine_codcast=off\n", new UTF8Encoding(false));
            }
            foreach (string kind in new[] { "pov", "cine" })
            {
                string p = Path.Combine(q.Dir, kind + ".cfg");
                if (!File.Exists(p)) File.WriteAllText(p, seedCfg ?? "", new UTF8Encoding(false));
            }
            q.Defaults = TakeList.ReadDefaults(q.DefaultsPath);
            if (File.Exists(q.Csv)) q.Load(); else q.Save();
            return q;
        }

        public void Load()
        {
            Rows.Clear();
            string[] lines = File.ReadAllLines(Csv);
            if (lines.Length == 0) return;
            string[] head = TakeList.SplitCsv(lines[0]);
            var at = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < head.Length; i++) at[head[i].Trim()] = i;
            // rows under a first line that does not name the columns would read as blank rows, and the next save would
            // write them back over every demo, path and tick (the code review's Q-2: a spreadsheet re-saving it with ';')
            if (lines.Skip(1).Any(l => l.Trim().Length > 0) && !(at.ContainsKey("folder") && at.ContainsKey("campath") && at.ContainsKey("kind")))
                throw new InvalidDataException("queue.csv's first line does not name its columns (folder, campath, kind, ...): was it saved by another program, " +
                                               "with ';' between them? Nothing was changed; open it again once its first line is the one the Designer wrote.");
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim().Length == 0) continue;
                string[] f = TakeList.SplitCsv(lines[i]);
                Func<string, string> get = c => { int k; return at.TryGetValue(c, out k) && k < f.Length ? f[k].Trim() : ""; };
                Rows.Add(new QueueRow
                {
                    Folder = get("folder"), Pov = get("pov"), ClockIn = get("clock_in"), ClockOut = get("clock_out"), Note = get("note"),
                    CamPath = get("campath"), Kind = get("kind").ToLowerInvariant(), Name = get("name"), Source = get("source"), Cfg = get("cfg"),
                    Fps = get("fps"), TickIn = get("tick_in"), TickOut = get("tick_out"), Demo = get("demo"), Before = get("before"),
                    After = get("after"), Hud = get("hud"), Codcast = get("codcast"), Look = get("look"),
                    Depth = get("depth"), DepthDist = get("depthdist"), Plate = get("plate"), NoPlayers = get("noplayers"), PlayersOnly = get("playersonly"),
                    Codec = get("codec"), Preset = get("preset")
                });
            }
            Defaults = TakeList.ReadDefaults(DefaultsPath);
        }

        /// <summary>Writes queue.csv whole (to a temporary file, then swapped in), rows numbered from 1 in order.</summary>
        public void Save()
        {
            var sb = new StringBuilder(TakeList.Header).Append('\n');
            for (int i = 0; i < Rows.Count; i++)
            {
                QueueRow r = Rows[i];
                var v = new[] { (i + 1).ToString(CultureInfo.InvariantCulture) }.Concat(r.Fields());
                sb.Append(string.Join(",", v.Select(TakeList.Quote).ToArray())).Append('\n');
            }
            string tmp = Csv + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(Csv)) File.Replace(tmp, Csv, Csv + ".bak");      // the version before this save, kept beside it
            else File.Move(tmp, Csv);
        }

        public void SetDefault(string key, string value)
        {
            Defaults[key] = value ?? "";
            var lines = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(DefaultsPath))
                foreach (string line in File.ReadAllLines(DefaultsPath))
                {
                    int eq = line.IndexOf('=');
                    string k = eq > 0 ? line.Substring(0, eq).Trim() : "";
                    if (k.Length > 0 && !line.TrimStart().StartsWith("#") && Defaults.ContainsKey(k)) { lines.Add(k + "=" + Defaults[k]); seen.Add(k); }
                    else lines.Add(line);
                }
            foreach (var kv in Defaults) if (!seen.Contains(kv.Key)) lines.Add(kv.Key + "=" + kv.Value);
            File.WriteAllLines(DefaultsPath, lines, new UTF8Encoding(false));
        }

        public string Default(string key) { string v; return Defaults.TryGetValue(key, out v) ? v : ""; }

        // ---------------------------------------------------------------- adding

        /// <summary>The take list's folder for a demo: its folder when that holds only this demo (the library's layout),
        /// else the demo's own name (a game folder full of films).</summary>
        public static string FolderFor(string demoPath)
        {
            string dir = Path.GetDirectoryName(demoPath);
            try { if (Directory.GetFiles(dir, "*.demo").Length == 1) return Path.GetFileName(dir); } catch { }
            return Path.GetFileNameWithoutExtension(demoPath);
        }

        private string NewId()
        {
            int n = 1;
            foreach (string f in Directory.Exists(PathsDir) ? Directory.GetFiles(PathsDir) : new string[0])
            {
                string b = Path.GetFileName(f);
                if (b.Length >= 4 && b[0] == 'p' && char.IsDigit(b[1]) && char.IsDigit(b[2]) && char.IsDigit(b[3]))
                    n = Math.Max(n, int.Parse(b.Substring(1, 3), CultureInfo.InvariantCulture) + 1);
            }
            foreach (QueueRow r in Rows)
            {
                string b = Path.GetFileName(r.CamPath);
                if (b.Length >= 4 && b[0] == 'p' && char.IsDigit(b[1]) && char.IsDigit(b[2]) && char.IsDigit(b[3]))
                    n = Math.Max(n, int.Parse(b.Substring(1, 3), CultureInfo.InvariantCulture) + 1);
            }
            return "p" + n.ToString("000", CultureInfo.InvariantCulture);
        }

        /// <summary>The wanted name, or it with " b", " c" ... when a row already has it (names become folders).</summary>
        public string UniqueName(string wanted, QueueRow except = null)
        {
            wanted = Clean(wanted);
            if (wanted.Length == 0) wanted = "path";
            Func<string, bool> taken = s => Rows.Any(r => r != except && string.Equals(r.Name, s, StringComparison.OrdinalIgnoreCase));
            if (!taken(wanted)) return wanted;
            for (char c = 'b'; c <= 'z'; c++) if (!taken(wanted + " " + c)) return wanted + " " + c;
            for (int i = 2; ; i++) if (!taken(wanted + " " + i)) return wanted + " " + i;
        }

        /// <summary>A name that is safe as a folder name.</summary>
        public static string Clean(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in (s ?? "").Trim()) sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '-' : c);
            return sb.ToString().Trim().TrimEnd('.');
        }

        public string DefaultName(string demoPath, int tick)
        {
            string map = CamPaths.ShortMap(demoPath);
            string clock = CamPaths.ClockLabel(tick).Replace(':', '.');
            return UniqueName((map.Length > 0 ? map + " " : "") + clock);
        }

        private static string Rel(string root, string path)
        {
            string full = Path.GetFullPath(path), r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return full.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? full.Substring(r.Length) : full;
        }

        /// <summary>A cinematic from markers read in the game: both files written, a row at the end, the queue saved.</summary>
        public QueueRow AddMarkers(IList<DollyMarker> markers, CamTrack track, string demoPath, string map, string name)
        {
            string id = NewId();
            string mj = Path.Combine(PathsDir, id + ".markers.json"), ct = Path.Combine(PathsDir, id + ".camtrack.txt");
            CamPaths.WriteMarkersJson(mj, markers, demoPath, map);
            track.Write(ct, "from " + markers.Count + " theater markers (Catmull-Rom, " + (track.Ticks.Length > 1 ? track.Ticks[1] - track.Ticks[0] : 1) + " ms steps), " + Path.GetFileName(demoPath));
            var r = CineRow(track, demoPath, name, "markers", Rel(Dir, ct));
            r.Note = markers.Count + " markers";
            Rows.Add(r);
            Save();
            return r;
        }

        /// <summary>A cinematic from a track file someone else made (the generator, Blender): copied into paths\.</summary>
        public QueueRow AddTrack(string camtrack, string demoPath, string name, string source, string note)
        {
            CamTrack track = CamTrack.Read(camtrack);
            string id = NewId();
            string ct = Path.Combine(PathsDir, id + ".camtrack.txt");
            File.Copy(camtrack, ct, false);
            string mj = Path.ChangeExtension(Path.ChangeExtension(camtrack, null), ".markers.json");
            if (File.Exists(mj)) File.Copy(mj, Path.Combine(PathsDir, id + ".markers.json"), false);
            var r = CineRow(track, demoPath, name, source, Rel(Dir, ct));
            r.Note = note ?? "";
            Rows.Add(r);
            Save();
            return r;
        }

        private QueueRow CineRow(CamTrack track, string demoPath, string name, string source, string campath)
        {
            return new QueueRow
            {
                Folder = FolderFor(demoPath), Demo = demoPath, Kind = "cine", Source = source, CamPath = campath,
                Name = string.IsNullOrWhiteSpace(name) ? DefaultName(demoPath, track.First) : UniqueName(name),
                TickIn = track.First.ToString(CultureInfo.InvariantCulture), TickOut = track.Last.ToString(CultureInfo.InvariantCulture),
                ClockIn = CamPaths.ClockLabel(track.First), ClockOut = CamPaths.ClockLabel(track.Last + 999)
            };
        }

        /// <summary>A POV take: a player's own view between two film ticks (marked in the game).</summary>
        public QueueRow AddPov(string demoPath, string pov, int tickIn, int tickOut, string name)
        {
            var r = new QueueRow
            {
                Folder = FolderFor(demoPath), Demo = demoPath, Kind = "pov", Source = "pov", Pov = pov ?? "",
                Name = string.IsNullOrWhiteSpace(name) ? UniqueName("pov " + Clean(pov) + " " + CamPaths.ClockLabel(tickIn).Replace(':', '.')) : UniqueName(name),
                TickIn = tickIn.ToString(CultureInfo.InvariantCulture), TickOut = tickOut.ToString(CultureInfo.InvariantCulture),
                ClockIn = CamPaths.ClockLabel(tickIn), ClockOut = CamPaths.ClockLabel(tickOut + 999)
            };
            Rows.Add(r);
            Save();
            return r;
        }

        // ---------------------------------------------------------------- editing

        public void Move(int from, int to)
        {
            if (from < 0 || from >= Rows.Count || to < 0 || to >= Rows.Count || from == to) return;
            QueueRow r = Rows[from];
            Rows.RemoveAt(from);
            Rows.Insert(to, r);
            Save();
        }

        /// <summary>Takes a row out of the queue. Its files go to removed\ (with the row itself, as a one-row CSV), so
        /// nothing is lost and it can be put back by hand.</summary>
        public void Remove(int i)
        {
            if (i < 0 || i >= Rows.Count) return;
            QueueRow r = Rows[i];
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Directory.CreateDirectory(RemovedDir);
            string id = Path.GetFileName(r.CamPath).Split('.')[0];
            if (r.CamPath.Length > 0 && Rows.Count(x => x.CamPath == r.CamPath) == 1)
                foreach (string f in Directory.Exists(PathsDir) ? Directory.GetFiles(PathsDir, id + ".*") : new string[0])
                    File.Move(f, Path.Combine(RemovedDir, stamp + " " + Path.GetFileName(f)));
            string keys = KeysOf(r);
            if (keys != null && File.Exists(keys)) File.Move(keys, Path.Combine(RemovedDir, stamp + " " + Path.GetFileName(keys)));
            File.WriteAllText(Path.Combine(RemovedDir, stamp + " " + Clean(r.Name) + ".csv"),
                TakeList.Header + "\n" + string.Join(",", new[] { "1" }.Concat(r.Fields()).Select(TakeList.Quote).ToArray()) + "\n", new UTF8Encoding(false));
            Rows.RemoveAt(i);
            Save();
        }

        /// <summary>A copy of a row right after it (same track; a different name, so its clip has its own folder).</summary>
        public QueueRow Duplicate(int i)
        {
            if (i < 0 || i >= Rows.Count) return null;
            QueueRow r = Rows[i].Copy();
            r.Name = UniqueName(Rows[i].Name);
            string keys = KeysOf(Rows[i]), copy = KeysOf(r);
            if (keys != null && copy != null && File.Exists(keys) && !File.Exists(copy)) File.Copy(keys, copy);      // its keyframes too
            Rows.Insert(i + 1, r);
            Save();
            return r;
        }

        /// <summary>A row's keyframes file (keys\&lt;name&gt;.keys.json): the timeline's keys for it, which the batch reads.</summary>
        public string KeysOf(QueueRow r) { return r == null ? null : TakeList.KeysFile(Dir, r.Name); }

        public void Rename(int i, string name)
        {
            if (i < 0 || i >= Rows.Count) return;
            string was = KeysOf(Rows[i]);
            Rows[i].Name = UniqueName(name, Rows[i]);
            string now = KeysOf(Rows[i]);
            if (was != null && now != null && was != now && File.Exists(was) && !File.Exists(now)) File.Move(was, now);     // its keyframes follow the name
            Save();
        }

        /// <summary>Gives a row a CFG of its own (a copy of its kind's, to edit), or takes it away again (null).</summary>
        public string OwnCfg(int i, bool own)
        {
            if (i < 0 || i >= Rows.Count) return null;
            QueueRow r = Rows[i];
            if (!own) { r.Cfg = ""; Save(); return null; }
            if (r.Cfg.Length > 0) return Path.Combine(Dir, r.Cfg);
            string kindCfg = Path.Combine(Dir, Default(r.IsCine ? "cine_cfg" : "pov_cfg"));
            string mine = Path.Combine(PathsDir, Clean(r.Name) + ".cfg");
            File.WriteAllText(mine, File.Exists(kindCfg) ? File.ReadAllText(kindCfg) : "", new UTF8Encoding(false));
            r.Cfg = Rel(Dir, mine);
            Save();
            return mine;
        }

        // ---------------------------------------------------------------- looks (2026-10-18)
        // Mycelium: "I dont know how to que a global CFG per POV, and per Cinematic. It should be drag and drop." A look (a global
        // look from Presets\GLOBAL, or any CFG) dropped on a row or on a kind is COPIED into the queue's looks\ folder, so a
        // night records what was queued even if the look is changed later; the take sends it after its CFG.

        public string LooksDir { get { return Path.Combine(Dir, "looks"); } }

        /// <summary>A copy of the look in looks\, the same file reused when it is already there unchanged; its path relative to
        /// the queue.</summary>
        private string CopyLook(string file)
        {
            Directory.CreateDirectory(LooksDir);
            string name = Clean(Path.GetFileNameWithoutExtension(file));
            string text = File.ReadAllText(file);
            string dest = Path.Combine(LooksDir, name + ".cfg");
            for (int n = 2; File.Exists(dest) && File.ReadAllText(dest) != text; n++) dest = Path.Combine(LooksDir, name + " " + n + ".cfg");
            if (!File.Exists(dest)) File.WriteAllText(dest, text, new UTF8Encoding(false));
            return Rel(Dir, dest);
        }

        /// <summary>The row's own look (null or empty: none of its own, so its kind's).</summary>
        public void SetLook(int i, string file)
        {
            if (i < 0 || i >= Rows.Count) return;
            Rows[i].Look = string.IsNullOrEmpty(file) ? "" : CopyLook(file);
            Save();
        }

        /// <summary>Every row of a kind ("pov" or "cine") without a look of its own gets this one (null: none).</summary>
        public void SetKindLook(string kind, string file)
        {
            SetDefault(kind + "_look", string.IsNullOrEmpty(file) ? "" : CopyLook(file));
        }

        /// <summary>The look a row is recorded with, and whether it is the row's own or its kind's: a name to show.</summary>
        public string LookOf(QueueRow r, out bool own)
        {
            own = r.Look.Length > 0;
            string p = own ? r.Look : Default((r.IsCine ? "cine" : "pov") + "_look");
            return p.Length == 0 ? "" : Path.GetFileNameWithoutExtension(p);
        }

        // ---------------------------------------------------------------- checking

        /// <summary>Everything that would stop a row at night, found now: the demo, the track, the CFG, the names.</summary>
        public List<string> Check(string demoRoot)
        {
            var problems = new List<string>();
            string error;
            List<Take> takes = TakeList.Load(Csv, demoRoot, out error);
            if (error.Length > 0) problems.Add(error);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < takes.Count; i++)
            {
                Take t = takes[i];
                string who = "row " + (i + 1) + " (" + (t.Name.Length > 0 ? t.Name : t.Folder) + ")";
                if (t.DemoPath == null) problems.Add(who + ": no demo file (" + (Rows.Count > i && Rows[i].Demo.Length > 0 ? Rows[i].Demo : "folder " + t.Folder) + ")");
                if (t.IsCine)
                {
                    if (string.IsNullOrEmpty(t.CamPath) || !File.Exists(t.CamPath)) problems.Add(who + ": no camera track at " + t.CamPath);
                    else
                    {
                        try
                        {
                            CamTrack tr = CamTrack.Read(t.CamPath);
                            if (t.HasTicks && (tr.First != t.TickIn || tr.Last != t.TickOut))
                                problems.Add(who + ": the track runs " + tr.First + "-" + tr.Last + " but the row says " + t.TickIn + "-" + t.TickOut);
                        }
                        catch (Exception ex) { problems.Add(who + ": the track cannot be read: " + ex.Message); }
                    }
                }
                else if (!t.HasTicks && (t.ClockIn < 0 || t.ClockOut <= t.ClockIn)) problems.Add(who + ": no in and out");
                if (!string.IsNullOrEmpty(t.CfgPath) && !File.Exists(t.CfgPath)) problems.Add(who + ": no CFG at " + t.CfgPath);
                if (!string.IsNullOrEmpty(t.LookPath) && !File.Exists(t.LookPath)) problems.Add(who + ": no look at " + t.LookPath);
                if (t.Name.Length > 0 && !names.Add(t.Folder + "\\" + t.Name)) problems.Add(who + ": the same name as another row on this demo (the clips would share a folder)");
            }
            return problems;
        }
    }
}
