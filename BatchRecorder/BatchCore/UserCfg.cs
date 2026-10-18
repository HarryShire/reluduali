using System;
using System.IO;
using System.Text;

namespace BatchRecorder
{
    /// <summary>
    /// The user's CFG: lines sent to the game right before every recording (Mycelium, 2026-10-01:
    /// "build a CFG Window in our Console ... One typically sends their CFG right before they
    /// hit record", every new demo).
    ///
    /// One command or "dvar value" per line, several per line with ';', "//" for comments. A
    /// cheat-protected dvar (ProtectedDvars: cg_draw2d, r_fog, r_dof_tweak...) is WRITTEN into
    /// by RipperBridge, where a console "set" is refused; every other line goes in as a console
    /// command (0.3.2: before, every dvar was written, and the sun tweaks need the game's own
    /// swapped in whole on save like the settings.
    /// </summary>
    public static class UserCfg
    {
        public const string FileName = "BatchRecorder.cfg";

        public const string Default =
            "// Sent right before every recording. One command or \"dvar value\" per line.\r\n" +
            "// No names over players' heads (not on the CODCAST F panel):\r\n" +
            "cg_drawFriendlyNames 0\r\n" +
            "cg_drawCrosshairNames 0\r\n";

        public static string PathOnDisk { get { return Path.Combine(Path.GetDirectoryName(BatchSettings.PathOnDisk), FileName); } }

        public static string Load()
        {
            try { return File.Exists(PathOnDisk) ? File.ReadAllText(PathOnDisk, Encoding.UTF8) : Default; }
            catch { return Default; }
        }

        public static bool Save(string text)
        {
            string path = PathOnDisk, tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, text ?? "", new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
                return true;
            }
            catch { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } return false; }
        }

        /// <summary>Sends every command in the text; a one-line summary back, failures logged one by one.</summary>
        public static string Send(string text, Func<string, bool> isDvar, Action<string> log)
        {
            int written = 0, commands = 0, failed = 0;
            var fog = new System.Collections.Generic.Dictionary<int, float>();
            foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw;
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0) line = line.Substring(0, comment);
                foreach (string part in line.Split(';'))
                {
                    string c = part.Trim();
                    if (c.Length == 0) continue;
                    int sp = c.IndexOfAny(new[] { ' ', '\t' });
                    string name = sp > 0 ? c.Substring(0, sp) : c;
                    string value = sp > 0 ? c.Substring(sp + 1).Trim().Trim('"') : "";
                    if (name.Equals("set", StringComparison.OrdinalIgnoreCase) || name.Equals("seta", StringComparison.OrdinalIgnoreCase))
                    {
                        // "set dvar value": the dvar is the next word
                        int sp2 = value.IndexOfAny(new[] { ' ', '\t' });
                        if (sp2 > 0) { name = value.Substring(0, sp2); value = value.Substring(sp2 + 1).Trim().Trim('"'); }
                    }

                    // "bridge <command>": straight to RipperBridge (2026-10-15: Theater's FOV slider also sets the free
                    // camera's FOV, "bridge dolly fov N", which cg_fov does not reach)
                    if (name.Equals("bridge", StringComparison.OrdinalIgnoreCase))
                    {
                        // a CFG, look or preset from someone else reaches only what the pages themselves write: the fog
                        // and the free camera, never files, memory or programs (the code review's B-3)
                        if (!value.StartsWith("fog ", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("dolly ", StringComparison.OrdinalIgnoreCase))
                        {
                            failed++; log("    bridge line refused (a CFG may send only 'bridge fog ...' and 'bridge dolly ...'): '" + value + "'");
                            continue;
                        }
                        string r = value.Length > 0 ? BridgeClient.Ask(value, 1000, 2) : null;
                        if (r != null && !r.StartsWith("error", StringComparison.Ordinal)) commands++;
                        else { failed++; log("    bridge line: '" + value + "' -> " + (r ?? "no answer")); }
                        continue;
                    }

                    // Fog lines (fog_colour 0.0286 0.0148 0.0108 ...): not dvars, but fields of the game's fog
                    // record. Collected here and written once, below, over the map's own values.
                    int fogAt = Fog.FieldOf(name);
                    if (fogAt >= 0)
                    {
                        if (Fog.Parse(fogAt, value, fog)) continue;
                        failed++; log("    CFG fog line not understood: '" + c + "'"); continue;
                    }

                    string reply;
                    // Not cheat-protected: a plain console command, so the game runs its own handling of the
                    // change (the sun tweaks only work that way). Protected ones are written by RipperBridge.
                    if (value.Length > 0 && isDvar(name) && !ProtectedDvars.Contains(name))
                    {
                        reply = BridgeClient.Ask("cmd " + c, 1000, 2);
                        if (reply == "ok") { commands++; continue; }
                    }
                    else if (value.Length > 0 && isDvar(name))
                    {
                        // Cheat-protected: clear the flag and let the game's own set run (2026-10-02, 0214:
                        // only that route changes the picture - a value written into the record reads back
                        // set while the game never applies it). Redacted only; the flag stays cleared.
                        if (DvarTable.Unprotect(name))
                        {
                            reply = BridgeClient.Ask("cmd " + c, 1000, 2);
                            if (reply == "ok") { commands++; continue; }
                        }
                        reply = BridgeClient.Ask("setdvar " + name + " " + value, 1000, 2);
                        if (reply != null && reply.StartsWith(name, StringComparison.OrdinalIgnoreCase)) { written++; continue; }
                        // A named-option dvar ("r_texFilterMipMode force trilinear") cannot be written as a
                        // number; the game's own console parses the name.
                        if (reply != null && reply.IndexOf("could not read a number", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            reply = BridgeClient.Ask("cmd " + c, 1000, 2);
                            if (reply == "ok") { commands++; continue; }
                        }
                    }
                    else
                    {
                        reply = BridgeClient.Ask("cmd " + c, 1000, 2);
                        if (reply == "ok") { commands++; continue; }
                    }
                    failed++;
                    log("    CFG line not taken: '" + c + "' (" + (reply ?? BridgeClient.LastError) + ")");
                }
            }
            string fogNote = "";
            if (fog.Count > 0)
            {
                string why;
                if (Fog.Write(fog, out why)) fogNote = ", fog " + fog.Count + " value(s) written";
                else { failed++; log("    CFG fog not written: " + why); }
            }
            return "CFG: " + written + " dvars written, " + commands + " commands sent" + fogNote + (failed > 0 ? ", " + failed + " FAILED" : "");
        }

        /// <summary>
        /// Send's routes as RipperBridge events for an exact tick (2026-10-17, the Console's exact moves; RipperBridge
        /// 0.11.0-dev40): "cmd &lt;line&gt;", "write &lt;address&gt; &lt;hex&gt;" (a field of the fog record) or "bridge &lt;command&gt;".
        /// A cheat-protected dvar is unprotected now, as Send does, so its line runs as a plain command when its tick comes.
        /// </summary>
        public static System.Collections.Generic.List<string> Resolve(string text, Func<string, bool> isDvar)
        {
            var events = new System.Collections.Generic.List<string>();
            foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw;
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0) line = line.Substring(0, comment);
                foreach (string part in line.Split(';'))
                {
                    string c = part.Trim();
                    if (c.Length == 0) continue;
                    int sp = c.IndexOfAny(new[] { ' ', '\t' });
                    string name = sp > 0 ? c.Substring(0, sp) : c;
                    string value = sp > 0 ? c.Substring(sp + 1).Trim().Trim('"') : "";
                    if (name.Equals("set", StringComparison.OrdinalIgnoreCase) || name.Equals("seta", StringComparison.OrdinalIgnoreCase))
                    {
                        int sp2 = value.IndexOfAny(new[] { ' ', '\t' });
                        if (sp2 > 0) { name = value.Substring(0, sp2); value = value.Substring(sp2 + 1).Trim().Trim('"'); }
                    }
                    if (name.Equals("bridge", StringComparison.OrdinalIgnoreCase)) { if (value.Length > 0) events.Add("bridge " + value); continue; }
                    int fogAt = Fog.FieldOf(name);
                    if (fogAt >= 0)
                    {
                        var vals = new System.Collections.Generic.Dictionary<int, float>();
                        if (Fog.Parse(fogAt, value, vals))
                            foreach (var kv in vals)
                                events.Add("write " + (Fog.Address + (uint)(kv.Key * 4)).ToString("X") + " " + BitConverter.ToString(BitConverter.GetBytes(kv.Value)).Replace("-", ""));
                        continue;
                    }
                    if (value.Length > 0 && isDvar(name) && ProtectedDvars.Contains(name)) DvarTable.Unprotect(name);
                    events.Add("cmd " + c);
                }
            }
            return events;
        }
    }

    /// <summary>
    /// The game's dvar table (t6mpv43: base 0x400000 + 0x261FC48, 0x60 bytes a record: name pointer +0x00,
    /// flags +0x0C, type +0x10, current +0x18). Clearing flag 0x80 (cheat protection) lets a console set
    /// through, and the console's set is the only route the renderer notices (measured 2026-10-02, 0214).
    /// Records are found once by reading the table through RipperBridge and cached for the game session.
    /// </summary>
    public static class DvarTable
    {
        public const uint First = 0x2A1FC48;
        public const int Stride = 0x60;
        private static readonly System.Collections.Generic.Dictionary<string, uint> _at =
            new System.Collections.Generic.Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Collections.Generic.HashSet<string> _open =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Forget what was found (a restarted game).</summary>
        public static void Reset() { lock (_at) { _at.Clear(); _open.Clear(); } }

        private static int _pid;

        /// <summary>A restarted game has its own table and its flags set again: what was found belonged to the last one
        /// (the code review's B-8: after a restart, protected lines went out as plain commands, ignored, reported sent).
        /// Called inside the lock.</summary>
        private static void SameGame()
        {
            int pid = 0;
            try
            {
                var ps = System.Diagnostics.Process.GetProcessesByName("t6mpv43");
                if (ps.Length > 0) pid = ps[0].Id;
                foreach (var x in ps) x.Dispose();
            }
            catch { }
            if (pid != _pid) { _at.Clear(); _open.Clear(); _pid = pid; }
        }

        private static byte[] Read(uint addr, int len)
        {
            string hex = BridgeClient.Ask("read " + addr.ToString("X") + " " + len, 1000, 2);
            if (hex == null || hex.Length != len * 2) return null;
            var b = new byte[len];
            for (int i = 0; i < len; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        /// <summary>The record's address, or 0. Reads the whole table once (about 2 s), caching every name.</summary>
        public static uint Find(string name)
        {
            lock (_at)
            {
                SameGame();
                uint a;
                if (_at.TryGetValue(name, out a)) return a;
                if (_at.Count > 0) return 0;              // the table was read and it is not there
                bool cut = false;
                for (int i = 0; i < 6000; i += 5)
                {
                    byte[] block = Read(First + (uint)(i * Stride), 5 * Stride);
                    if (block == null) { cut = true; break; }
                    bool any = false;
                    for (int k = 0; k < 5; k++)
                    {
                        uint p = BitConverter.ToUInt32(block, k * Stride);
                        if (p < 0x400000) continue;
                        byte[] nm = Read(p, 48);
                        if (nm == null) continue;
                        int z = Array.IndexOf(nm, (byte)0);
                        string n = System.Text.Encoding.ASCII.GetString(nm, 0, z < 0 ? nm.Length : z);
                        if (n.Length > 0 && !_at.ContainsKey(n)) _at[n] = First + (uint)((i + k) * Stride);
                        any = true;
                    }
                    if (!any) break;
                }
                uint found = _at.TryGetValue(name, out a) ? a : 0;
                if (cut) _at.Clear();                     // a scan cut short is not the whole table: read again next time
                return found;
            }
        }

        /// <summary>Clears flag 0x80 on the dvar's record (once per game session). False = not found or not writable.</summary>
        public static bool Unprotect(string name)
        {
            lock (_at) { SameGame(); if (_open.Contains(name)) return true; }
            uint rec = Find(name);
            if (rec == 0) return false;
            byte[] f = Read(rec + 0x0C, 4);
            if (f == null) return false;
            uint flags = BitConverter.ToUInt32(f, 0);
            if ((flags & 0x80) != 0)
            {
                byte[] nf = BitConverter.GetBytes(flags & ~0x80u);
                string r = BridgeClient.Ask("write " + (rec + 0x0C).ToString("X") + " " + BitConverter.ToString(nf).Replace("-", ""), 1000, 2);
                if (r == null || !r.StartsWith("ok", StringComparison.Ordinal)) return false;
            }
            lock (_at) _open.Add(name);
            return true;
        }
    }

    /// <summary>
    /// The game's fog record (Redacted t6mpv43: base 0x400000 + 0x32224B8; read live
    /// 2026-10-01 on Studio: 1052.5 13755.7 -75 611.9 -46.7 74.5 1.9 54.9 | 12.58 11.41 13.94 | 1 | 6.4 4.93 6.4 | 1),
    /// 16 floats. A write holds (read back 3 s later, 0193). Only the fields a CFG names change; the rest stay
    /// the map's own.
    /// </summary>
    public static class Fog
    {
        public const uint Address = 0x36224B8;
        // the fog lines' names (the FOG page's sliders and the shipped presets write them)
        static readonly string[] Names = {
            "fog_start", "fog_fade", "fog_height_start", "fog_height_fade", "haze_pitch", "haze_yaw",
            "haze_inner", "haze_outer", "fog_colour", "fog_opacity", "haze_colour", "haze_opacity" };
        static readonly int[] Offset = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 15 };   // float index; colours take 3

        public static int FieldOf(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            int i = Array.IndexOf(Names, n);
            return i < 0 ? -1 : Offset[i];
        }

        public static bool Parse(int at, string value, System.Collections.Generic.Dictionary<int, float> into)
        {
            string[] p = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int want = (at == 8 || at == 12) ? 3 : 1;
            if (p.Length < want) return false;
            for (int k = 0; k < want; k++)
            {
                float f;
                if (!float.TryParse(p[k], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) return false;
                into[at + k] = f;
            }
            return true;
        }

        public static bool Write(System.Collections.Generic.Dictionary<int, float> values, out string why)
        {
            why = "";
            string now = BridgeClient.Ask("read " + Address.ToString("X") + " 64", 1000, 2);
            if (now == null || now.Length != 128) { why = "could not read the fog record (" + (now ?? BridgeClient.LastError) + ")"; return false; }
            byte[] b = new byte[64];
            for (int i = 0; i < 64; i++) b[i] = Convert.ToByte(now.Substring(2 * i, 2), 16);
            foreach (var kv in values) BitConverter.GetBytes(kv.Value).CopyTo(b, kv.Key * 4);
            var hex = new StringBuilder(128);
            foreach (byte x in b) hex.Append(x.ToString("X2"));
            string r = BridgeClient.Ask("write " + Address.ToString("X") + " " + hex, 1000, 2);
            if (r != "ok") { why = r ?? BridgeClient.LastError; return false; }
            return true;
        }
    }

    /// <summary>
    /// Per-map looks (Mycelium, 2026-10-01: "CFGs that match the Theme of those skyboxes ... one per
    /// map. This will be for the edit"): Looks\&lt;map&gt;.cfg beside the DLL, e.g. Looks\mp_raid.cfg,
    /// sent before every recording on that map (after the batch's own settings, before the user's
    /// CFG so the CFG box still has the last word). The same lines as any CFG: fog and film dvars
    /// are protected, so they go in through RipperBridge like every dvar here.
    /// </summary>
    public static class Looks
    {
        public static string Folder { get { return Path.Combine(Path.GetDirectoryName(BatchSettings.PathOnDisk), "Looks"); } }

        /// <summary>"tdm_mp_nuketown_2020_8_16_2025_20_11.demo" -> "mp_nuketown_2020"; null if not a game film name.</summary>
        public static string MapOf(string demoPath)
        {
            if (string.IsNullOrEmpty(demoPath)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(demoPath),
                @"(mp_[a-z0-9_]+?)_\d+_\d+_\d+_\d+_\d+\.demo$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        public static string PathFor(string map) { return Path.Combine(Folder, map + ".cfg"); }

        /// <summary>Sends Looks\&lt;map&gt;.cfg if there is one; the summary line, or why nothing was sent.</summary>
        public static string Send(string map, Func<string, bool> isDvar, Action<string> log)
        {
            if (string.IsNullOrEmpty(map)) return "look: no map";
            string p = PathFor(map);
            if (!File.Exists(p)) return "look: none for " + map + " (no " + p + ")";
            string text;
            try { text = File.ReadAllText(p, Encoding.UTF8); }
            catch (Exception ex) { return "look: could not read " + p + ": " + ex.Message; }
            return "look " + map + ": " + UserCfg.Send(text, isDvar, log);
        }

        /// <summary>The map the game has loaded now (RipperBridge "dvar mapname"), or null.</summary>
        public static string CurrentMap()
        {
            string r = BridgeClient.Ask("dvar mapname", 1000, 2);
            if (r == null) return null;
            var m = System.Text.RegularExpressions.Regex.Match(r, "\"(mp_[a-z0-9_]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }
    }
}
