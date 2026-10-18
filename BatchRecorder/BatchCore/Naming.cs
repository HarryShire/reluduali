using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BatchRecorder
{
    /// <summary>
    /// How a queued take's files are named (Mycelium, 2026-10-16): a frag's POV and its cinematics all in one folder,
    /// Recordings\&lt;the frag&gt;\ (the frag is the name of the folder that holds the demo, changeable on the row), and the
    /// file names in one of two styles, a Console setting:
    ///   D (the default, his pick: "the Qualitive quality is stated first and then the organization is 2nd")
    ///       POV_Stnd-Ballst-360nswb_t0001.avi      CINE03_Stnd-Ballst-360nswb_t0001.avi
    ///   C   Standoff ballista 360 ns wb - 440 - POV - take0001.avi      ... - Cine 03 - take0001.avi
    /// D's code is the frag's map and weapon shortened (Standoff = Stnd, Ballista = Ballst), then the rest as typed with the
    /// spaces taken out; the part after " - " (the clock) is left out. The short names can be changed in short-names.txt
    /// (%LOCALAPPDATA%\Reluduali\Console): one "Standoff=Stnd" per line. The take number is the first one free in the
    /// folder, so a take recorded again never lands on the last.
    /// </summary>
    public static class Naming
    {
        /// <summary>"D", "C", or "" for the old layout (a folder per path inside the demo's).</summary>
        public static string Style = "D";

        public static string OverridesFile
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reluduali", "Console", "short-names.txt"); }
        }

        static readonly string[] Maps =
        {
            "aftermath", "cargo", "carrier", "drone", "express", "hijacked", "meltdown", "overflow", "plaza", "raid", "slums", "standoff",
            "turbine", "yemen", "nuketown", "downhill", "grind", "hydro", "mirage", "encore", "magma", "vertigo", "studio", "uplink",
            "detour", "cove", "rush", "dig", "frost", "pod", "takeoff", "bridge", "castaway", "concert", "paintball", "skate", "socotra",
            "nuketown2025", "dockside", "village", "la", "pakistan", "afghanistan",
        };

        static readonly string[] Weapons =
        {
            "ballista", "dsr", "dsr50", "svu", "svu-as", "xpr", "xpr50", "an94", "an-94", "ak74u", "b23r", "kap40", "kap", "executioner",
            "tac45", "fiveseven", "five-seven", "m27", "mtar", "msmc", "mp7", "pdw", "pdw57", "vector", "chicom", "skorpion", "peacekeeper",
            "swat", "swat556", "fal", "smr", "scar", "scar-h", "type25", "m8a1", "870", "r870", "s12", "ksg", "m1216", "lsat", "hamr", "qbb",
            "mk48", "crossbow", "ballistic", "bk", "rpg", "smaw", "fhj", "knife", "combat", "riot", "galil", "ak47", "m16", "commando",
            "olympia", "remington", "ray", "raygun", "minigun", "deathmachine", "intervention", "barrett", "l96", "wa2000", "psg1",
        };

        static Dictionary<string, string> Overrides()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(OverridesFile))
                    foreach (string l in File.ReadAllLines(OverridesFile))
                    {
                        int eq = l.IndexOf('=');
                        if (eq > 0 && !l.TrimStart().StartsWith("#")) d[l.Substring(0, eq).Trim()] = l.Substring(eq + 1).Trim();
                    }
            }
            catch { }
            return d;
        }

        static bool IsVowel(char c) { return "aeiouAEIOU".IndexOf(c) >= 0; }

        static string Cap(string s) { return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant(); }

        /// <summary>A map's short name: its first letter and the consonants after it, four at most (Standoff = Stnd).</summary>
        public static string ShortMap(string word, Dictionary<string, string> over = null)
        {
            string v;
            if (over != null && over.TryGetValue(word, out v)) return v;
            string w = new string(word.Where(char.IsLetterOrDigit).ToArray());
            if (w.Length == 0) return "";
            var sb = new StringBuilder().Append(char.ToUpperInvariant(w[0]));
            foreach (char c in w.Substring(1)) if (!IsVowel(c) && sb.Length < 4) sb.Append(char.ToLowerInvariant(c));
            return sb.Length >= 3 ? sb.ToString() : Cap(w.Length > 4 ? w.Substring(0, 4) : w);
        }

        /// <summary>A weapon's short name: its first four letters and the consonants after them, six at most (Ballista = Ballst).</summary>
        public static string ShortWeapon(string word, Dictionary<string, string> over = null)
        {
            string v;
            if (over != null && over.TryGetValue(word, out v)) return v;
            string w = new string(word.Where(char.IsLetterOrDigit).ToArray());
            if (w.Length <= 4) return Cap(w);
            var sb = new StringBuilder(Cap(w.Substring(0, 4)));
            foreach (char c in w.Substring(4)) if (!IsVowel(c) && sb.Length < 6) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>D's code for a frag: "Standoff ballista 360 ns wb - 440" = "Stnd-Ballst-360nswb".</summary>
        public static string Code(string frag)
        {
            var over = Overrides();
            string main = (frag ?? "").Split(new[] { " - " }, StringSplitOptions.None)[0];
            var words = main.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.Count == 0) return "frag";
            int map = words.FindIndex(w => Maps.Contains(w.ToLowerInvariant()) || over.ContainsKey(w) && Maps.Contains(w.ToLowerInvariant()));
            int gun = words.FindIndex(w => Weapons.Contains(w.ToLowerInvariant()));
            if (map < 0)
            {
                // no map known: the first word that is not a kill count ("2k") and not the weapon
                map = Enumerable.Range(0, words.Count).Where(i => i != gun && !IsKills(words[i])).DefaultIfEmpty(-1).First();
            }
            if (gun < 0) gun = Enumerable.Range(0, words.Count).Where(i => i != map && !IsKills(words[i])).DefaultIfEmpty(-1).First();
            var parts = new List<string>();
            if (map >= 0) parts.Add(ShortMap(words[map], over));
            if (gun >= 0) parts.Add(ShortWeapon(words[gun], over));
            string tags = string.Concat(words.Where((w, i) => i != map && i != gun));
            if (tags.Length > 0) parts.Add(tags);
            return Clean(string.Join("-", parts.Where(p => p.Length > 0)));
        }

        static bool IsKills(string w) { return w.Length >= 2 && (w.EndsWith("k") || w.EndsWith("K")) && w.Substring(0, w.Length - 1).All(char.IsDigit); }

        /// <summary>A file name's base (no extension) for a take; take = its number in the folder.</summary>
        public static string Base(string style, string frag, bool pov, int kindIndex, int take)
        {
            string t4 = take.ToString("0000", CultureInfo.InvariantCulture), k2 = kindIndex.ToString("00", CultureInfo.InvariantCulture);
            if (style == "C")
                return Clean(frag + " - " + (pov ? (kindIndex > 1 ? "POV " + k2 : "POV") : "Cine " + k2) + " - take" + t4);
            return Clean((pov ? (kindIndex > 1 ? "POV" + k2 : "POV") : "CINE" + k2) + "_" + Code(frag) + "_t" + t4);
        }

        /// <summary>The first take number with no file of that name in the folder yet.</summary>
        public static string NextFree(string dir, string style, string frag, bool pov, int kindIndex)
        {
            for (int n = 1; ; n++)
            {
                string b = Base(style, frag, pov, kindIndex, n);
                bool taken = false;
                try { taken = Directory.Exists(dir) && Directory.GetFiles(dir, b + ".*").Length > 0; } catch { }
                if (!taken) return b;
            }
        }

        public static string Clean(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '-' : c);
            return sb.ToString().Trim().TrimEnd('.');
        }
    }
}
