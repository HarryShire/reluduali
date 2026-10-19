using System;
using System.Diagnostics;

namespace BatchRecorder
{
    /// <summary>
    /// Mycelium, 2026-10-18 (his test, 01:46 and 14:21): "never at any point should the ... batch process queue Black Ops 2 ...
    /// from the game's menu screen; it always has to be from the theater". A film loaded with RipperBridge's loaddemo before the
    /// theater has been opened in that game session keeps our keyframes but loses the game's own jump ticks (the theater HUD
    /// says "Downloading: 0.00 %"; 2026-10-17). Once the theater has been opened, loads from the Designer are fine.
    ///
    /// So every loaddemo (the batch, Demos, the timeline going back) asks <see cref="Ok"/> first. The theater counts as
    /// opened in this game (its process id) when:
    ///  - a film was seen up that no loaddemo of ours put there: the person started it from the theater, or
    ///  - the Reluduali theater layer says it opened the theater lobby (reluduali_theater_ready 1).
    /// </summary>
    public static class TheaterFirst
    {
        static int _seenPid, _loadedPid;
        static int _pid; static DateTime _pidAt = DateTime.MinValue;

        public const string Why = "the theater has not been opened in this game yet: in the game, go to ONLINE, then THEATER, and start a film there once. " +
                                  "A film the Designer loads before that loses the game's own jump ticks (the theater HUD then says Downloading: 0.00 %).";

        /// <summary>The game's process id, read at most every 5 s (stats arrive 4 times a second).</summary>
        static int GamePid()
        {
            if ((DateTime.UtcNow - _pidAt).TotalSeconds < 5) return _pid;
            _pidAt = DateTime.UtcNow;
            _pid = 0;
            try { Process[] p = Process.GetProcessesByName("t6mpv43"); if (p.Length > 0) _pid = p[0].Id; foreach (Process q in p) q.Dispose(); }
            catch { }
            return _pid;
        }

        /// <summary>A line about to go to the bridge, and its answer (BridgeClient calls this for every request).</summary>
        internal static void Saw(string line, string reply)
        {
            if (line == null) return;
            if (line.StartsWith("loaddemo ", StringComparison.Ordinal))
            {
                if (reply != null && reply.StartsWith("ok", StringComparison.Ordinal)) _loadedPid = GamePid();
                return;
            }
            if (line == "stats" && reply != null && reply.Contains(" theater ") && !reply.Contains("theater 0x00000000"))
            {
                int pid = GamePid();
                if (pid != 0 && pid != _loadedPid) _seenPid = pid;
            }
        }

        /// <summary>A film was just loaded from the theater's own menus (the person said so, or the Designer watched it): the
        /// theater is open in this game.</summary>
        public static void Opened() { int pid = GamePid(); if (pid != 0) _seenPid = pid; }

        /// <summary>True when a loaddemo now keeps the game's own jump ticks: the theater was opened in this game.</summary>
        public static bool Ok()
        {
            _pidAt = DateTime.MinValue;              // a game started again since the last read has a new id
            int pid = GamePid();
            if (pid == 0) return false;
            if (_seenPid == pid) return true;
            // the theater layer opened the theater lobby itself
            string ready = BridgeClient.Ask("dvar reluduali_theater_ready", 800, 1);
            if (ready != null && !ready.StartsWith("error", StringComparison.Ordinal))
            {
                string v = ready.Trim(); int sp = v.LastIndexOf(' ');
                if (sp >= 0 && v.Substring(sp + 1).Trim('"') == "1") { _seenPid = pid; return true; }
            }
            // a film up now that we did not load (Saw marks it as the answer comes back)
            BridgeClient.Ask("stats", 800, 1);
            return _seenPid == pid;
        }
    }
}
