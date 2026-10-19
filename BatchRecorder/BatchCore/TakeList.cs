using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BatchRecorder
{
    /// <summary>One row of the take list: a demo, and the part of it to record.</summary>
    public sealed class Take
    {
        public int Number;
        public string Folder;          // clip folder name, and the take's name on disk
        public string Pov;             // the player whose view was recorded, read off the preview
        public int ClockIn;            // seconds, as the theater panel showed them
        public int ClockOut;
        public string Note;            // "single take, clocks read by eye" / "... fitted ... (+-2 s)"
        public string CamPath;         // optional (2026-10-12): a camera track (RipperBridge "cam path") - a cinematic
                                       // recorded through the theater's free camera instead of a player's view
        public string DemoPath;        // resolved when the list is loaded

        // The CAM QUEUE's columns (2026-10-14; Mycelium: "Que up 12+ Campaths per demo ... alongside their POV's with
        // specific CFG specifications for POV and for CINEs"). All optional, and all AFTER campath, so a writer that
        // knows only the first seven columns (the Blender add-on's "Add to batch queue") still lines up.
        public string Kind = "";       // "pov" or "cine"; empty = cine when there is a camera track, else pov
        public string OutDir = "";     // this row's own output folder
        public string Name = "";       // the path's own name: its clip is <out>\<folder>\<name>\<name>.<ext>
        public string Source = "";     // where the row came from: markers, blender, generator, pov
        public string CfgPath = "";    // a CFG file sent INSTEAD of the CFG box before this take
        public string LookPath = "";   // a look (a global look or any CFG) sent AFTER the CFG, so it has the last word (2026-10-18)
        public int Fps;                // 0 = the tab's
        public int TickIn = -1;        // exact demo ticks: when both are set they replace clock_in/clock_out, and the
        public int TickOut = -1;       // take needs no zero (a camera track's own ticks, or POV in/out marked live)
        public int BeforeMs = -1;      // -1 = the tab's before/after sliders
        public int AfterMs = -1;
        public int Hud = -1;           // -1 = the tab's No HUD box; 0 = hidden; 1 = shown
        public int Codcast = -1;       // -1 = the tab's Require CODCAST; 0 = the regular theater (cinematics); 1 = CODCAST
        // how it records, per take (2026-10-18: RecordingSettings): -1 = the tab's box, 0 off, 1 on
        public int Depth = -1, Plate = -1, NoPlayers = -1, PlayersOnly = -1;
        public int DepthDist = -1;     // game units; -1 = the tab's
        public string Codec = "";      // a name in the codec list; "" = the tab's
        public string Preset = "";     // the row's batch preset (Presets\BATCH), if it names one
        public string Keys = "";       // its keyframes, every parameter (2026-10-18): <list>\keys\<name>.keys.json, when it has one
        /// <summary>Where each recording setting came from: "own", "preset NAME", "look NAME", "POV global" / "CINE global";
        /// a key not in here is the Batch tab's own box.</summary>
        public readonly Dictionary<string, string> From = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // the file names of a queued take (Naming, 2026-10-16): set when the run starts and when the take first names its files
        public int KindIndex = 1;      // its place among the same frag's takes of its kind (CINE03 = the third cinematic)
        public string FileBase;        // the name its files take, decided once (the take number is the first one free)
        public bool IsPov { get { return Kind == "pov" || (Kind.Length == 0 && string.IsNullOrEmpty(CamPath)); } }

        public bool IsCine { get { return Kind == "cine" || (Kind.Length == 0 && !string.IsNullOrEmpty(CamPath)); } }
        public bool HasTicks { get { return TickIn >= 0 && TickOut > TickIn; } }
        public bool Fitted { get { return Note != null && Note.IndexOf("fitted", StringComparison.OrdinalIgnoreCase) >= 0; } }
        public int Seconds { get { return HasTicks ? (TickOut - TickIn + 999) / 1000 : Math.Max(0, ClockOut - ClockIn); } }

        public override string ToString()
        {
            string span = HasTicks
                ? "ticks " + TickIn + "-" + TickOut + " (" + ((TickOut - TickIn) / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " s)"
                : Clock(ClockIn) + "-" + Clock(ClockOut);
            return "take " + Number + "  " + (IsCine ? "CINE" : "POV") + (Name.Length > 0 ? "  " + Name : "") + "  " + Folder + "  " + span + LookLabel;
        }

        private string LookLabel { get { return string.IsNullOrEmpty(LookPath) ? "" : "   look: " + Path.GetFileNameWithoutExtension(LookPath); } }

        public static string Clock(int s) { return (s / 60) + ":" + (s % 60).ToString("00"); }
    }

    /// <summary>
    /// Reads the take list and matches each row to a demo on disk.
    ///
    /// The list's own start_tick / end_tick columns are deliberately ignored:
    /// they were computed with a fixed zero of 3000, and the zero is per load
    /// (see <see cref="GameClock.ZeroFromLanding"/>). The clock times are what
    /// was read off the theater panel, so those are what gets converted. The CAM
    /// QUEUE's tick_in / tick_out are a different thing: exact demo ticks read from
    /// the game or a camera track, which need no zero at all.
    /// </summary>
    public static class TakeList
    {
        /// <summary>The CAM QUEUE's header: the original seven, then its own columns.</summary>
        public const string Header = "take,folder,pov,clock_in,clock_out,note,campath,kind,name,source,cfg,fps,tick_in,tick_out,demo,before,after,hud,codcast,look,depth,depthdist,plate,noplayers,playersonly,codec,preset";

        /// <summary>A queue folder's defaults per kind, beside its CSV (see <see cref="Load"/>).</summary>
        public const string DefaultsFile = "queue.txt";

        public static List<Take> Load(string csvPath, string demoRoot, out string error)
        {
            error = "";
            var takes = new List<Take>();
            if (!File.Exists(csvPath)) { error = "No take list at " + csvPath; return takes; }

            string[] lines = File.ReadAllLines(csvPath);
            if (lines.Length < 2) { error = "The take list is empty."; return takes; }

            string[] head = SplitCsv(lines[0]);
            int cTake = IndexOf(head, "take"), cFolder = IndexOf(head, "folder"), cPov = IndexOf(head, "pov");
            int cIn = IndexOf(head, "clock_in"), cOut = IndexOf(head, "clock_out"), cNote = IndexOf(head, "note");
            int cCam = IndexOf(head, "campath");
            int cKind = IndexOf(head, "kind"), cName = IndexOf(head, "name"), cSource = IndexOf(head, "source");
            int cCfg = IndexOf(head, "cfg"), cFps = IndexOf(head, "fps"), cTin = IndexOf(head, "tick_in"), cTout = IndexOf(head, "tick_out");
            int cDemo = IndexOf(head, "demo"), cBefore = IndexOf(head, "before"), cAfter = IndexOf(head, "after"), cHud = IndexOf(head, "hud");
            int cCodcast = IndexOf(head, "codcast"), cOutDir = IndexOf(head, "outdir"), cLook = IndexOf(head, "look");
            int cPreset = IndexOf(head, "preset");
            var cRec = RecordingSettings.Keys.ToDictionary(k => k, k => IndexOf(head, k), StringComparer.OrdinalIgnoreCase);
            if (cFolder < 0 || cIn < 0 || cOut < 0) { error = "The take list needs folder, clock_in and clock_out columns."; return takes; }

            string listDir = Path.GetDirectoryName(Path.GetFullPath(csvPath));
            Dictionary<string, string> defaults = ReadDefaults(Path.Combine(listDir, DefaultsFile));

            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(demoRoot))
            {
                foreach (string dir in Directory.GetDirectories(demoRoot, "*", SearchOption.AllDirectories))
                {
                    string[] demos = Directory.GetFiles(dir, "*.demo");
                    if (demos.Length == 1) byName[Path.GetFileName(dir)] = demos[0];
                }
            }

            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim().Length == 0) continue;
                string[] f = SplitCsv(lines[i]);
                var t = new Take
                {
                    Number = cTake >= 0 ? ParseInt(Get(f, cTake)) : i,
                    Folder = Get(f, cFolder),
                    Pov = cPov >= 0 ? Get(f, cPov) : "",
                    ClockIn = ParseClock(Get(f, cIn)),
                    ClockOut = ParseClock(Get(f, cOut)),
                    Note = cNote >= 0 ? Get(f, cNote) : "",
                    CamPath = cCam >= 0 ? Get(f, cCam) : "",
                    Kind = Get(f, cKind).ToLowerInvariant(),
                    Name = Get(f, cName),
                    Source = Get(f, cSource),
                    CfgPath = Get(f, cCfg),
                    Fps = Math.Max(0, ParseInt(Get(f, cFps))),
                    TickIn = ParseInt(Get(f, cTin)),
                    TickOut = ParseInt(Get(f, cTout)),
                    BeforeMs = ParseMs(Get(f, cBefore)),
                    AfterMs = ParseMs(Get(f, cAfter)),
                    Hud = ParseHud(Get(f, cHud)),
                    Codcast = ParseHud(Get(f, cCodcast)),
                    OutDir = Get(f, cOutDir),
                    LookPath = Get(f, cLook),
                    Preset = Get(f, cPreset)
                };
                // the row's own recording settings, as written (fps, before ... the columns above read them too)
                var own = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in cRec) { string v = Get(f, kv.Value); if (v.Length > 0) own[kv.Key] = v; }
                if (t.Kind != "pov" && t.Kind != "cine") t.Kind = "";
                if (t.CamPath.Length > 0 && !Path.IsPathRooted(t.CamPath))      // relative to the list
                    t.CamPath = Path.GetFullPath(Path.Combine(listDir, t.CamPath));
                if (t.CfgPath.Length > 0 && !Path.IsPathRooted(t.CfgPath))
                    t.CfgPath = Path.GetFullPath(Path.Combine(listDir, t.CfgPath));
                if (t.LookPath.Length > 0 && !Path.IsPathRooted(t.LookPath))
                    t.LookPath = Path.GetFullPath(Path.Combine(listDir, t.LookPath));
                ApplyDefaults(t, defaults, listDir, own);
                // its keyframes travel with it (Mycelium 10-18: "all that keyframe data is saved and ready for the batch")
                string keys = KeysFile(listDir, t.Name);
                if (keys != null && File.Exists(keys)) t.Keys = keys;

                // An explicit demo file wins; else the folder whose only .demo it is (the original rule).
                string demo = Get(f, cDemo);
                if (demo.Length > 0 && !Path.IsPathRooted(demo)) demo = Path.GetFullPath(Path.Combine(listDir, demo));
                string path;
                if (demo.Length > 0 && File.Exists(demo)) t.DemoPath = demo;
                else if (byName.TryGetValue(t.Folder, out path)) t.DemoPath = path;
                takes.Add(t);
            }
            return takes;
        }

        /// <summary>
        /// queue.txt, key=value: what a row of each kind gets when it has no value of its own. Mycelium: "Usually the POV
        /// will be one CFG, and the CINES will be another ... I want the option to have slightly different [ones] per
        /// campath if the user decides". Keys: pov_cfg / cine_cfg (a CFG file, relative to the list), pov_fps /
        /// cine_fps, pov_before / pov_after / cine_before / cine_after (seconds), pov_hud / cine_hud (show, hide, tab),
        /// pov_codcast / cine_codcast (on, off, tab: the film mode).
        /// </summary>
        /// <summary>A take's keyframes file (the Designer's timeline writes it): keys\&lt;its name&gt;.keys.json beside the list,
        /// the name made safe for a file name. Null without a name.</summary>
        public static string KeysFile(string listDir, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(listDir)) return null;
            var sb = new System.Text.StringBuilder();
            foreach (char c in name.Trim()) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return Path.Combine(listDir, "keys", sb + ".keys.json");
        }

        public static Dictionary<string, string> ReadDefaults(string path)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(path)) return d;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                    d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return d;
        }

        /// <summary>A take's CFG and look from its kind when it has none of its own, then every recording setting from the first
        /// place that has it (RecordingSettings: own, the row's batch preset, its look's RECORDING part, its kind's global).
        /// Where each came from is kept in Take.From.</summary>
        private static void ApplyDefaults(Take t, Dictionary<string, string> d, string listDir, Dictionary<string, string> own = null)
        {
            string k = t.IsCine ? "cine_" : "pov_";
            string v;
            if (t.CfgPath.Length == 0 && d.TryGetValue(k + "cfg", out v) && v.Length > 0)
                t.CfgPath = Path.IsPathRooted(v) ? v : Path.GetFullPath(Path.Combine(listDir, v));
            if (t.LookPath.Length == 0 && d.TryGetValue(k + "look", out v) && v.Length > 0)
                t.LookPath = Path.IsPathRooted(v) ? v : Path.GetFullPath(Path.Combine(listDir, v));
            if (t.Preset.Length == 0 && d.TryGetValue(k + "preset", out v) && v.Length > 0) t.Preset = v;     // the kind's batch preset, if it names one

            own = own ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            t.From.Clear();
            foreach (string key in RecordingSettings.Keys) if (own.ContainsKey(key) && Set(t, key, own[key])) t.From[key] = "own";
            var kind = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in d) if (kv.Key.StartsWith(k, StringComparison.OrdinalIgnoreCase)) kind[kv.Key.Substring(k.Length)] = kv.Value;
            var layers = new List<KeyValuePair<string, Dictionary<string, string>>>
            {
                new KeyValuePair<string, Dictionary<string, string>>("preset " + t.Preset, RecordingSettings.ReadPreset(t.Preset)),
                new KeyValuePair<string, Dictionary<string, string>>("look " + Path.GetFileNameWithoutExtension(t.LookPath ?? ""), RecordingSettings.FromLook(t.LookPath)),
                new KeyValuePair<string, Dictionary<string, string>>(t.IsCine ? "CINE global" : "POV global", kind),
            };
            foreach (string key in RecordingSettings.Keys)
            {
                if (t.From.ContainsKey(key)) continue;
                foreach (var layer in layers)
                    if (layer.Value.TryGetValue(key, out v) && Set(t, key, v)) { t.From[key] = layer.Key; break; }
            }
        }

        /// <summary>One recording setting onto the take; false when the value says "the tab's" (empty, "tab") or does not read.</summary>
        private static bool Set(Take t, string key, string v)
        {
            v = (v ?? "").Trim();
            if (v.Length == 0 || v.Equals("tab", StringComparison.OrdinalIgnoreCase)) return false;
            switch (key.ToLowerInvariant())
            {
                case "fps": { int x = ParseInt(v); if (x <= 0) return false; t.Fps = x; return true; }
                case "before": { int x = ParseMs(v); if (x < 0) return false; t.BeforeMs = x; return true; }
                case "after": { int x = ParseMs(v); if (x < 0) return false; t.AfterMs = x; return true; }
                case "hud": { int x = ParseHud(v); if (x < 0) return false; t.Hud = x; return true; }
                case "codcast": { int x = ParseHud(v); if (x < 0) return false; t.Codcast = x; return true; }
                case "depth": { int x = ParseHud(v); if (x < 0) return false; t.Depth = x; return true; }
                case "plate": { int x = ParseHud(v); if (x < 0) return false; t.Plate = x; return true; }
                case "noplayers": { int x = ParseHud(v); if (x < 0) return false; t.NoPlayers = x; return true; }
                case "playersonly": { int x = ParseHud(v); if (x < 0) return false; t.PlayersOnly = x; return true; }
                case "depthdist": { int x = ParseInt(v); if (x <= 0) return false; t.DepthDist = x; return true; }
                case "codec": t.Codec = v; return true;
                default: return false;
            }
        }

        /// <summary>"2:07" -> 127. Also accepts plain seconds.</summary>
        public static int ParseClock(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            s = s.Trim();
            int colon = s.IndexOf(':');
            if (colon < 0) return ParseInt(s);
            int m = ParseInt(s.Substring(0, colon)), sec = ParseInt(s.Substring(colon + 1));
            if (m < 0 || sec < 0) return -1;
            return m * 60 + sec;
        }

        private static int ParseInt(string s)
        {
            int v;
            return int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1;
        }

        /// <summary>Seconds ("0.5", or "0,5" as a comma-decimal Windows writes it: the code review's Q-9) -> ms; empty or
        /// unreadable -> -1.</summary>
        private static int ParseMs(string s)
        {
            double v;
            if (!double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v < 0) return -1;
            return (int)Math.Round(v * 1000);
        }

        /// <summary>"hide" / "off" / "0" -> 0, "show" / "on" / "1" -> 1, anything else ("tab", empty) -> -1 (HUD and CODCAST).</summary>
        private static int ParseHud(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "hide": case "hidden": case "off": case "0": case "no": return 0;
                case "show": case "shown": case "on": case "1": case "yes": return 1;
                default: return -1;
            }
        }

        private static string Get(string[] f, int i) { return i >= 0 && i < f.Length ? f[i].Trim() : ""; }

        private static int IndexOf(string[] head, string name)
        {
            for (int i = 0; i < head.Length; i++)
                if (string.Equals(head[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>Minimal CSV: commas, with "quoted, fields" allowed -- these folder names contain commas and plus signs.</summary>
        public static string[] SplitCsv(string line)
        {
            var fields = new List<string>();
            var cur = new System.Text.StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else cur.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { fields.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            fields.Add(cur.ToString());
            return fields.ToArray();
        }

        /// <summary>One CSV field, quoted when it has to be (SplitCsv reads it back).</summary>
        public static string Quote(string s)
        {
            s = s ?? "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 && s.Trim() == s) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
