using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BatchRecorder
{
    /// <summary>
    /// How a take records, from four places (Mycelium, 2026-10-18: "I want to be able to specify passes, green screen, depth ...
    /// POV"; "the batch tab should have these sort of global commands ... for POVs separately and then ... for cinematics
    /// separately ... and also be able to save the global recording parameters for the queue into a preset"; "do you want to
    /// set the passes globally or ... per cinematic"; "A global CFG should be able to have those settings, but you can still
    /// be able to override those settings from inside of the batch tab with a batch preset"). Each setting of a take comes
    /// from the first of these that has it:
    ///   1. the row's own value (the Batch's take list, a cell changed by hand);
    ///   2. the row's batch preset (Presets\BATCH\&lt;name&gt;.txt);
    ///   3. its Global CFG's RECORDING part (the look the row records with: "// == RECORDING ==", then "depth 1" lines);
    ///   4. its kind's global recording (POV or CINE: queue.txt beside the list, pov_fps, cine_depth ...);
    ///   5. the Batch tab's own boxes.
    /// The keys, the same in all of them: fps, before, after (seconds), hud (show/hide), codcast (on/off), depth (on/off),
    /// depthdist (game units), plate, noplayers, playersonly (on/off), codec (the codec list's name).
    /// </summary>
    public static class RecordingSettings
    {
        public static readonly string[] Keys = { "fps", "before", "after", "hud", "codcast", "depth", "depthdist", "plate", "noplayers", "playersonly", "codec" };

        public const string Section = "RECORDING";
        const string Mark = "// == ";

        /// <summary>Presets\BATCH beside the Designer.</summary>
        public static string PresetFolder { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Presets", "BATCH"); } }

        public static string PresetPath(string name) { return Path.Combine(PresetFolder, name + ".txt"); }

        public static List<string> Presets()
        {
            try { return Directory.Exists(PresetFolder) ? Directory.GetFiles(PresetFolder, "*.txt").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>(); }
            catch { return new List<string>(); }
        }

        /// <summary>A batch preset's values (key=value lines), empty when there is none by that name.</summary>
        public static Dictionary<string, string> ReadPreset(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return Only(TakeList.ReadDefaults(PresetPath(name.Trim())));
        }

        public static void SavePreset(string name, IDictionary<string, string> values)
        {
            Directory.CreateDirectory(PresetFolder);
            var sb = new StringBuilder("# Reluduali batch preset: how a take records (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ")\r\n");
            foreach (string k in Keys) { string v; if (values.TryGetValue(k, out v) && !string.IsNullOrWhiteSpace(v)) sb.Append(k).Append('=').Append(v.Trim()).Append("\r\n"); }
            File.WriteAllText(PresetPath(name), sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>The recording keys only, out of any key=value set.</summary>
        static Dictionary<string, string> Only(Dictionary<string, string> d)
        {
            var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in Keys) { string v; if (d.TryGetValue(k, out v) && v.Trim().Length > 0) o[k] = v.Trim(); }
            return o;
        }

        /// <summary>A Global CFG's RECORDING part ("key value" or "key=value" lines), empty when it has none.</summary>
        public static Dictionary<string, string> FromLook(string lookPath)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(lookPath) || !File.Exists(lookPath)) return d;
            bool inside = false;
            try
            {
                foreach (string raw in File.ReadAllLines(lookPath, Encoding.UTF8))
                {
                    string l = raw.Trim();
                    if (l.StartsWith(Mark, StringComparison.Ordinal) && l.EndsWith("==", StringComparison.Ordinal))
                    {
                        inside = string.Equals(l.Substring(Mark.Length).TrimEnd('=').Trim(), Section, StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inside || l.Length == 0 || l.StartsWith("//", StringComparison.Ordinal)) continue;
                    int cut = l.IndexOf('=');
                    if (cut < 0) cut = l.IndexOf(' ');
                    if (cut <= 0) continue;
                    d[l.Substring(0, cut).Trim()] = l.Substring(cut + 1).Trim();
                }
            }
            catch { }
            return Only(d);
        }

        /// <summary>A Global CFG's text without its RECORDING part: what is sent to the game.</summary>
        public static string StripSection(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf(Mark + Section, StringComparison.OrdinalIgnoreCase) < 0) return text;
            var sb = new StringBuilder();
            bool inside = false;
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string l = raw.Trim();
                if (l.StartsWith(Mark, StringComparison.Ordinal) && l.EndsWith("==", StringComparison.Ordinal))
                    inside = string.Equals(l.Substring(Mark.Length).TrimEnd('=').Trim(), Section, StringComparison.OrdinalIgnoreCase);
                if (!inside) sb.Append(raw).Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>Writes a Global CFG's RECORDING part (replaced; the other parts untouched). Empty values are left out; no
        /// values at all takes the part away.</summary>
        public static void WriteToLook(string lookPath, IDictionary<string, string> values)
        {
            string text = File.Exists(lookPath) ? File.ReadAllText(lookPath, Encoding.UTF8) : "";
            var sb = new StringBuilder(StripSection(text).TrimEnd());
            var lines = Keys.Where(k => values.ContainsKey(k) && !string.IsNullOrWhiteSpace(values[k])).Select(k => k + " " + values[k].Trim()).ToList();
            if (lines.Count > 0)
            {
                if (sb.Length > 0) sb.Append("\r\n");
                sb.Append(Mark).Append(Section).Append(" ==\r\n");
                foreach (string l in lines) sb.Append(l).Append("\r\n");
            }
            File.WriteAllText(lookPath, sb.ToString().TrimEnd() + "\r\n", new UTF8Encoding(false));
        }
    }
}
