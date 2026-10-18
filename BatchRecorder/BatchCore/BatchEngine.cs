using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace BatchRecorder
{
    /// <summary>
    /// The recording loop, one take at a time.
    ///
    /// Rules it encodes, each of which was paid for:
    ///  - End playback and wait for the theater to drop before loading anything.
    ///    Loading a demo while one is playing kills the client (2026-09-27).
    ///  - Jump back once after loading, before believing any tick. Ticks run
    ///    through the load screen and the menu and are wrong until a jump back;
    ///    the zero is then read from where the jump lands.
    ///  - Every step is verified by reading the game, not assumed from the fact
    ///    that a command was sent.
    /// </summary>
    public sealed class BatchEngine
    {
        public sealed class Options
        {
            public string OutputDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Reluduali", "Recordings");     // XF-0095: no drive of Mycelium's written in
            // The film steps 1000/fps whole ms a frame: 600 and 1000 record the same frames (1 ms each),
            // so 1000 is the honest name for what "600" always gave (Mycelium, 2026-09-29).
            public int Fps = 1000;
            public bool Depth;                  // 2026-10-17 (Mycelium: "It can be off by default but it should be there")
            public bool Plate;                  // 2026-10-17: the clean plate beside a take (off by default)
            // 2026-10-17 (Mycelium: "a Console button for the world and players passes in the BATCH tab"): the take recorded
            // again with RipperBridge's rule on (dev26 "only=world|players"): "<take> - world", "<take> - players"
            public bool NoPlayers, PlayersOnly;
            public int DepthDistance = 2000;    // game units: black there and past it
            public int PreRollMs = 3000;        // start recording this far before the take (the "before" slider)
            public int PostRollMs = 3000;       // and keep going this far after it (the "after" slider)
            public bool DryRun = true;          // everything except the recording
            public bool LoadEachDemo;           // off for a single take on the demo already up
            // Recording 40 demos in the wrong film mode is the expensive mistake,
            // and it is invisible until you look at the footage. Checked per take.
            public bool RequireCodcast = true;
            // cg_draw2d 0 hides every 2D element, the kill feed included - for
            // cinematics and no-HUD clips only (Mycelium, 2026-09-29). Off by default:
            // the CODCAST panel's own settings (saved in the profile) decide the HUD.
            public bool HideHud;
            public string UserCfg = "";         // the CFG box: sent right before every recording (UserCfg.cs)
            public int Zero = -1;               // -1 = measure it; otherwise the zero read off the HUD
            public int FallbackZero = 2300;     // when the landings disagree; -1 = fail the take instead
            public bool SendThroughBridge = true; // console commands via RipperBridge's pipe, the host's route as the fallback
            // BO2 itself only goes up to 6.0x (Mycelium, 2026-09-28), so 10 was being
            // asked for something the game does not do - a likely reason the
            // snapshots it laid down came out 10.3 to 11.5 s apart instead of on a grid.
            public int Timescale = 6;           // speed used to watch past the take
            public int WatchTimeoutMs = 4 * 60 * 1000;
            public int MaxJumps = 120;          // demo_back presses before giving up
            public int JumpSettleMs = 250;
            // A floor, not the whole answer. The recorder does not capture in real time: it steps the film one
            // frame at a time, so a 15 s take at 600 fps is 9,000 frames and takes far longer than 15 s of wall
            // clock, and a flat timeout would abandon a recording that was working perfectly well. The real budget
            // is computed per take.
            public int RecordTimeoutMs = 10 * 60 * 1000;
            // A ceiling per frame; a stall is what ends a recording early.
            public int MsAllowedPerFrame = 3000;
            // No new frame for this long while recording = stalled.
            public int StallMs = 120 * 1000;
            public string FfmpegPath = "";
            public string VideoCodecArgs = "-c:v dnxhd -profile:v dnxhr_hq -pix_fmt yuv422p";
            public string VideoExtension = "mov";
        }

        public sealed class Result
        {
            public Take Take;
            public bool Ok;
            public string Message = "";
            public int Zero = -1;
            public int StartTick = -1;
            public int LandedTick = -1;
            public int Frames = -1;
        }

        private readonly GameClock _clock;
        private readonly Options _opt;
        // Whether the theater bar is known to be showing: true after each of our own
        // loads, false once F4 has hidden it. Unknown (false) for a demo loaded by hand.
        private bool _barShowing;
        private bool _hudShowing;     // the theater's F2 HUD (kill feed, score, name tags): every load brings it back
        private bool _hudKnown;       // whether _hudShowing is known (after one of our loads), so F2 can also bring it back
        // The demo this batch last loaded, so takes from the same demo don't reload it.
        private string _loadedDemo;
        private readonly Action<string> _log;
        private volatile bool _cancel;

        public BatchEngine(GameClock clock, Options options, Action<string> log)
        {
            _clock = clock;
            _opt = options;
            _log = log ?? (s => { });
        }

        public void Cancel() { _cancel = true; }

        // ---- per take (the CAM QUEUE, 2026-10-14): a row's own value, else the tab's ----
        // Mycelium: "Usually the POV will be one CFG, and the CINES will be another ... I want the option to have slightly
        // different [ones] per campath if the user decides". TakeList fills a row's empty fields from its queue's
        // defaults per kind (queue.txt), so what reaches here is already the row's own or its kind's.
        private int FpsFor(Take t) { return t.Fps > 0 ? t.Fps : _opt.Fps; }
        private int BeforeFor(Take t) { return t.BeforeMs >= 0 ? t.BeforeMs : _opt.PreRollMs; }
        private int AfterFor(Take t) { return t.AfterMs >= 0 ? t.AfterMs : _opt.PostRollMs; }
        private bool HideHudFor(Take t) { return t.Hud >= 0 ? t.Hud == 0 : _opt.HideHud; }

        /// <summary>The CFG sent right before this take: its own file when it names one, else the CFG box. Null when
        /// the row names a file that cannot be read - a take must never go out with a CFG it did not ask for.</summary>
        private string CfgFor(Take t, out string why)
        {
            why = "";
            if (string.IsNullOrEmpty(t.CfgPath)) return _opt.UserCfg ?? "";
            try { return File.ReadAllText(t.CfgPath, Encoding.UTF8); }
            catch (Exception ex) { why = "cannot read this take's CFG " + t.CfgPath + ": " + ex.Message; return null; }
        }

        /// <summary>Where a take's files go: a named row (a queued path) gets a folder of its own inside its demo's, so
        /// twelve paths on one demo never land on each other in either recorder; a plain row keeps the old layout.</summary>
        private void OutputFor(Take t, out string dir, out string name)
        {
            string folder = Sanitise(t.Folder);
            if (!string.IsNullOrEmpty(t.OutDir)) { name = Sanitise(t.Name.Length > 0 ? t.Name : t.Folder); dir = t.OutDir; }
            else if (string.IsNullOrEmpty(t.Name)) { name = folder; dir = Path.Combine(_opt.OutputDirectory, folder); }
            else if (Naming.Style == "C" || Naming.Style == "D")
            {
                // Mycelium, 2026-10-16: a frag's POV and its cinematics all in one folder, Recordings\<the frag>\, named C or D
                dir = Path.Combine(_opt.OutputDirectory, folder);
                if (t.FileBase == null) t.FileBase = Naming.NextFree(dir, Naming.Style, t.Folder, t.IsPov, t.KindIndex);
                name = t.FileBase;
                return;
            }
            else { name = Sanitise(t.Name); dir = Path.Combine(_opt.OutputDirectory, folder, name); }
            // never over an earlier recording (a take run again, a list run twice): the first free name, decided once a
            // run so the passes, the Lumit copy and the log all name the same files
            if (t.FileBase == null) t.FileBase = FreeBase(dir, string.IsNullOrEmpty(t.Name) && string.IsNullOrEmpty(t.OutDir) ? name + " - take " + t.Number : name);
        }

        /// <summary>The base name itself when nothing in the folder starts with it, else "base (2)", "base (3)" ...</summary>
        private static string FreeBase(string dir, string b)
        {
            Func<string, bool> taken = n =>
            {
                try { return Directory.Exists(dir) && (Directory.GetFiles(dir, n + ".*").Length > 0 || Directory.GetFileSystemEntries(dir, n + " - *").Length > 0); }
                catch { return false; }
            };
            if (!taken(b)) return b;
            for (int i = 2; i < 1000; i++) if (!taken(b + " (" + i + ")")) return b + " (" + i + ")";
            return b + " (" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ")";
        }

        private static string VideoFile(Take t, string dir, string name, string ext)
        {
            if (t.FileBase != null) return Path.Combine(dir, t.FileBase + "." + ext);
            return Path.Combine(dir, string.IsNullOrEmpty(t.Name) ? name + " - take " + t.Number + "." + ext : name + "." + ext);
        }

        /// <summary>Each queued take's place among its frag's takes of the same kind, in the queue's order (CINE01, CINE02 ...).</summary>
        private static void NumberKinds(IList<Take> all)
        {
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Take t in all) t.FileBase = null;             // every run decides its names afresh (OutputFor)
            foreach (Take t in all)
            {
                if (string.IsNullOrEmpty(t.Name)) continue;
                string key = (t.Folder ?? "") + "|" + (t.IsPov ? "pov" : "cine");
                int n;
                seen.TryGetValue(key, out n);
                t.KindIndex = n + 1;
                seen[key] = n + 1;
                t.FileBase = null;
            }
        }

        private void Log(string s) { _log(DateTime.Now.ToString("HH:mm:ss") + "  " + s); }

        private bool _bridgeSendFailed;

        private bool Send(string command)
        {
            // Through RipperBridge when it is there: it hands the text to the game's own
            // Cbuf_AddText from inside the game. The host's route stays the fallback.
            if (_opt.SendThroughBridge && !_bridgeSendFailed)
            {
                string reply = BridgeClient.Ask("cmd " + command, 1000, 2);
                if (reply == "ok") return true;
                // with no other route (the Designer hosts this on its own), one failure must not turn every later command
                // away: the next one tries the bridge again (the code review's B-10)
                if (ConsoleHost.Current is StandaloneHost)
                {
                    Log("    RipperBridge did not take '" + command + "' (" + (reply ?? BridgeClient.LastError) + ")");
                    return false;
                }
                _bridgeSendFailed = true;
                Log("    RipperBridge did not take a command (" + (reply ?? BridgeClient.LastError) + "); sending through " + ConsoleHost.Current.Name + " from now on");
            }
            if (!ConsoleHost.Current.SendCommand(command))
            {
                Log("could not send '" + command + "': " + ConsoleHost.Current.LastError);
                return false;
            }
            return true;
        }

        private bool Wait(Func<bool> until, int timeoutMs, string what)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (_cancel) return false;
                if (until()) return true;
                Thread.Sleep(100);
                waited += 100;
            }
            if (what != null) Log("timed out after " + (timeoutMs / 1000) + " s waiting for " + what);
            return false;
        }

        // A command can be lost, and two sent back to back more often: takes 32 and 33
        // of the first multi-clip run sat still for 2-4 minutes because a "demo_pause 0"
        // never arrived. Commands that move playback are therefore checked by their
        // effect and sent again. Anything sent with them goes in the same string, so it
        // arrives or is lost together with the pause change.
        private bool Resume(string with)
        {
            for (int attempt = 1; attempt <= 4 && !_cancel; attempt++)
            {
                int t0 = _clock.Tick;
                Send(string.IsNullOrEmpty(with) ? "demo_pause 0" : with + ";demo_pause 0");
                if (Wait(() => { int t = _clock.Tick; return t != int.MinValue && t != t0; }, 1500, null)) return true;
                Log("    playback did not start (attempt " + attempt + "); sending again");
            }
            return false;
        }

        /// <summary>Sends until the effect shows, a few times, for the same reason as Resume.</summary>
        private bool SendUntil(string command, Func<bool> done, int waitMs, int attempts, string what)
        {
            for (int attempt = 1; attempt <= attempts && !_cancel; attempt++)
            {
                Send(command);
                if (Wait(done, waitMs, null)) return true;
                if (attempt < attempts) Log("    " + what + " did not happen (attempt " + attempt + "); sending '" + command + "' again");
            }
            Log("    " + what + " did not happen after " + attempts + " sends of '" + command + "'");
            return false;
        }


        /// <summary>
        /// The replay's speed. "timescale" is cheat-protected: sent as a console command it is
        /// ignored without a word (measured in Mycelium's game 2026-10-01: "timescale 20" and
        /// "timescale 6" left the replay at 1x), so every "timescale 4/6/0.25" this engine sent
        /// before ran at normal speed. RipperBridge's setdvar writes the dvar table directly
        /// (6x measured, 12,150 ticks in 2 s). Mycelium: 10 is the most it takes; 6 is what he uses.
        /// </summary>
        private bool SetTimescale(double scale)
        {
            if (scale > 10) scale = 10;
            string v = scale.ToString(CultureInfo.InvariantCulture);
            string reply = BridgeClient.Ask("setdvar timescale " + v, 1000, 2);
            if (reply != null && reply.StartsWith("timescale", StringComparison.Ordinal)) return true;
            Log("    could not set timescale " + v + " through RipperBridge (" + (reply ?? "no reply") + "); playback stays at its speed");
            return false;
        }

        private bool Pause(string with)
        {
            for (int attempt = 1; attempt <= 4 && !_cancel; attempt++)
            {
                Send(string.IsNullOrEmpty(with) ? "demo_pause 1" : "demo_pause 1;" + with);
                Thread.Sleep(250);
                int a = _clock.Tick; Thread.Sleep(300); int b = _clock.Tick;
                if (a == b) return true;
                Log("    playback did not pause (attempt " + attempt + "); sending again");
            }
            return false;
        }

        /// <summary>
        /// Null when the output drive can hold the take with a quarter to spare, else why not.
        /// A full drive mid-take fails that take and every one after it, so an unattended run
        /// stops BEFORE starting one it cannot finish (2026-10-01: 37 overnight takes are
        /// 3.8-5.4 TB in lossless Ut Video, against a 4 TB drive).
        ///
        /// Size per frame, as a share of raw RGB at the game's size, from measured 3840x1608
        /// game footage: Ut Video 4.95-7 MB (38% at the busy end), ProRes 4444
        /// 4.12 MB (22%), ProRes 422 HQ 2.76 MB (15%), DNxHR HQ 2.60 MB, H.264 CRF 12 0.36 MB.
        /// The depth pass's PNGs are counted at half of raw.
        /// </summary>
        private string DiskWontHold(Take take)
        {
            if (_opt.DryRun) return null;
            try
            {
                int w, h;
                if (!GameKeys.ClientSize(out w, out h)) { w = 3840; h = 1608; }
                double raw = w * (double)h * 3;
                string codec = (_opt.VideoCodecArgs ?? "").ToLowerInvariant();
                double share = codec.Contains("utvideo") ? 0.38
                             : codec.Contains("prores_ks") && codec.Contains("-profile:v 4") ? 0.24
                             : codec.Contains("prores") || codec.Contains("dnx") ? 0.16
                             : 0.04;
                double perFrame = raw * share + (_opt.Depth ? raw * 0.5 : 0);
                double ms = (take.HasTicks ? take.TickOut - take.TickIn : take.Seconds * 1000.0 + 2000)   // +-1 s for clocks read by eye
                            + BeforeFor(take) + AfterFor(take);
                double framesPerMs = 1.0 / Math.Max(1, 1000 / Math.Max(1, FpsFor(take)));   // the film steps 1000/fps whole ms a frame
                double need = ms * framesPerMs * perFrame * 1.25;
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_opt.OutputDirectory)));
                if (drive.AvailableFreeSpace >= need) return null;
                return drive.Name + " has " + (drive.AvailableFreeSpace / 1e9).ToString("0", CultureInfo.InvariantCulture)
                       + " GB free; this take needs about " + (need / 1.25 / 1e9).ToString("0", CultureInfo.InvariantCulture)
                       + " GB, plus a quarter to spare";
            }
            catch (Exception ex)
            {
                Log("    could not check the free space (" + ex.Message + "); going ahead");
                return null;
            }
        }

        /// <summary>True while a batch that really records runs (not a dry run): the contest timer's "queued recording".</summary>
        public static bool Recording { get; private set; }

        /// <summary>A take finished and recorded: its folder and when it started (for the Lumit folder, overgrow night 2026-10-10).</summary>
        public static event Action<string, DateTime> TakeRecorded;

        /// <summary>Every take as it ends, recorded or not, with how many of the run are done and how many there are
        /// (the CAM QUEUE's night status file, 2026-10-14). Raised on the batch's own thread.</summary>
        public static event Action<Result, int, int> TakeFinished;

        /// <summary>The Console's exact moves (2026-10-17, the playback engine): given a Reluduali recording's start and end
        /// ticks, its step and a file to write, the slider moves that play as RipperBridge events for every frame (the file),
        /// or null when none play. Set by the Console; BatchCore alone has none.</summary>
        public static Func<int, int, int, string, string> MovesTrack;

        /// <summary>True while a recording with exact moves runs (the Console's own replay of them waits), false after.</summary>
        public static Action<bool> MovesRecording;

        public List<Result> Run(IEnumerable<Take> takes)
        {
            Recording = !_opt.DryRun;
            try { return RunTakes(takes); }
            finally { Recording = false; }
        }

        private List<Result> RunTakes(IEnumerable<Take> takes)
        {
            var results = new List<Result>();
            // The film steps 1000 / fps WHOLE milliseconds per frame (an integer divide:
            // ~1 ms of replay per frame at "600"). Say what the chosen rate really records.
            if (_opt.Fps > 0 && 1000 % _opt.Fps != 0)
            {
                int step = Math.Max(1, 1000 / _opt.Fps);
                Log("NOTE: " + _opt.Fps + " fps steps " + step + " ms of replay per frame, so the footage is "
                    + (1000.0 / step).ToString("0.#", CultureInfo.InvariantCulture) + " frames per second of replay, not "
                    + _opt.Fps + ". Interpret it at that rate, or pick a rate that divides 1000 (1000, 500, 250, 200, 125, 100).");
            }
            var all = new List<Take>(takes);
            NumberKinds(all);
                foreach (Take take in all)
                {
                    if (_cancel) { Log("cancelled."); break; }
                    string full = DiskWontHold(take);
                    if (full != null)
                    {
                        Log("STOPPING before take " + take.Number + ": " + full);
                        results.Add(new Result { Take = take, Message = "not started: " + full });
                        break;
                    }
                    DateTime began = DateTime.Now;
                    Result r = RunOne(take);
                    results.Add(r);
                    if (r.Ok && !_opt.DryRun && TakeRecorded != null)
                    {
                        string takeDir, takeName;
                        OutputFor(take, out takeDir, out takeName);     // a queued path's own folder, so the Lumit copy finds its clip
                        try { TakeRecorded(takeDir, began); }
                        catch (Exception ex) { Log("    after the take: " + ex.Message); }
                    }
                    // A take can succeed and still carry something worth reading. Logging
                    // the message only on failure is how a warning gets lost -- the same
                    // way DemoRipper dropped OBS's remux warning whenever a file arrived.
                    if (!r.Ok) Log("take " + take.Number + " FAILED: " + r.Message);
                    else if (r.Message.Length > 0 && r.Message != "dry run") Log("take " + take.Number + " done, but: " + r.Message);
                    else Log("take " + take.Number + " done");
                    if (TakeFinished != null)
                    {
                        try { TakeFinished(r, results.Count, all.Count); }
                        catch (Exception ex) { Log("    after the take: " + ex.Message); }
                    }
                }
            return results;
        }

        /// <summary>
        /// Mycelium's order, which is the only one that works (2026-09-28):
        ///
        ///   demo loaded -> timescale 10 past the part to be recorded, so the
        ///   jump points exist -> demo_back repeatedly until before the start
        ///   tick -> the recording, from the start tick to the end tick.
        ///
        /// Note what it does NOT do: it never ends playback to begin with. A demo
        /// stays loaded for its whole take, the theater screen stays up, and
        /// playback is only ended when moving to a different demo.
        /// </summary>
        public Result RunOne(Take take)
        {
            var r = new Result { Take = take };
            Log("--- " + take);
            if (take.Fitted && !take.HasTicks) Log("    note: this row's clocks are fitted, +-2 s. Check the result.");
            int fps = FpsFor(take);
            if (fps != _opt.Fps || !string.IsNullOrEmpty(take.CfgPath) || take.BeforeMs >= 0 || take.AfterMs >= 0 || take.Hud >= 0)
                Log("    this row's own: " + fps + " fps, before " + (BeforeFor(take) / 1000.0).ToString("0.0#", CultureInfo.InvariantCulture)
                    + " s, after " + (AfterFor(take) / 1000.0).ToString("0.0#", CultureInfo.InvariantCulture) + " s, HUD "
                    + (HideHudFor(take) ? "hidden" : "shown") + ", CFG " + (string.IsNullOrEmpty(take.CfgPath) ? "the CFG box" : take.CfgPath));
            if (fps > 0 && 1000 % fps != 0)
                Log("    NOTE: " + fps + " fps steps " + Math.Max(1, 1000 / fps) + " ms of replay a frame (pick 1000, 500, 250, 200, 125 or 100)");

            bool sameDemo = _loadedDemo != null && _clock.InTheater
                            && string.Equals(_loadedDemo, take.DemoPath, StringComparison.OrdinalIgnoreCase);
            // a film jumped by a keyframe (the timeline's) is loaded again first, even with "load each demo" off
            bool jumped = _clock.InTheater && !string.IsNullOrEmpty(take.DemoPath) && KeyframeJumpedSinceLoad();
            if (jumped)
            {
                Log("    the film was jumped by a keyframe and this RipperBridge cannot pair frames: loading it again (a recording right after a jump would lose ticks)");
                sameDemo = false;
            }
            if (_opt.LoadEachDemo && sameDemo)
                Log("    same demo as the last take, already up - not reloading it");
            else if ((_opt.LoadEachDemo || jumped) && !string.IsNullOrEmpty(take.DemoPath))
            {
                _loadedDemo = null;
                if (!LoadTheDemo(take)) { r.Message = "could not load this take's demo"; return r; }
                _loadedDemo = take.DemoPath;
                _barShowing = true;   // a load always brings the theater bar back
                _hudShowing = true;   // and the HUD (sandbox 0378)
                _hudKnown = true;

                // Every load clears the CODCAST bit (sandbox, 2026-09-29: 0x04 at the menu,
                // 0x00 once the next demo was up), and setting it again works - the
                // theater's own mode label follows it. So a batch that wants CODCAST
                // sets it after each load rather than refusing every take after the first.
                if (take.Codcast < 0 && _opt.RequireCodcast && !_clock.IsCodcast)
                {
                    Log("    the load cleared CODCAST (flags " + _clock.DemoModeFlags + ") - setting it again");
                    if (!_clock.SetCodcast(true)) Log("    " + _clock.Error);
                    Thread.Sleep(500);
                }
            }

            if (!_clock.InTheater)
            {
                r.Message = "no demo is loaded - load one on the theater screen first";
                Log("    " + r.Message);
                return r;
            }

            // A queued row says which film mode it wants (2026-10-14): POV takes in CODCAST (the F panel's HUD), and
            // cinematics in the regular theater (Mycelium, 2026-10-13: "for cinematics, you probably should go into regular
            // theater mode because codcast doesnt have the dolly camera or edit camera modes"). One queue mixes both on
            // the same demo, so the bit is set or cleared per row and then checked.
            // A row on "the tab's" mode wants CODCAST when the tab requires it. In a queue the row before it may have been a
            // cinematic on the same demo, which turned CODCAST off (sandbox 0394: both POV rows failed "not in CODCAST"),
            // so the bit is set here too, not only after a load - and then checked, as before.
            int wantCodcast = take.Codcast >= 0 ? take.Codcast : (_opt.RequireCodcast ? 1 : -1);
            if (wantCodcast >= 0 && _clock.IsCodcast != (wantCodcast == 1))
            {
                Log("    " + (wantCodcast == 1 ? "CODCAST on for this take" : "regular theater for this cinematic (CODCAST off)"));
                if (!_clock.SetCodcast(wantCodcast == 1)) Log("    " + _clock.Error);
                Thread.Sleep(500);
                if (_clock.IsCodcast != (wantCodcast == 1))
                {
                    r.Message = wantCodcast == 1
                        ? "not in CODCAST film mode (demo mode flags = " + _clock.DemoModeFlags + ") and setting it did not take. Load the demo through FILM CODCAST, or turn the requirement off."
                        : "could not switch to the regular theater (demo mode flags = " + _clock.DemoModeFlags + ")";
                    Log("    " + r.Message);
                    return r;
                }
            }

            // The POV goes first: it costs nothing to change and everything to get
            // wrong. A take recorded from the wrong player is 9,000 useless frames.
            // Not fatal here: a player who joined mid-match is not in the roster at the
            // start (a test run, standoff demo: 12 names, take at 4:06).
            // The POV is set again at the take's own time (EnsurePov, inside GoTo), and
            // THAT one fails the take if the player still is not there.
            if (!GoToPov(take.Pov))
                Log("    not on POV '" + take.Pov + "' yet - trying again at the take's time");

            // 1. Play past the end of the take, fast. BO2 only creates jump points
            //    as a replay plays, so they do not exist ahead of where it has been.
            //    This is needed whatever the zero turns out to be.
            if (!PlayPast(take)) { r.Message = "could not play past the take"; return r; }

            // 2. Establish the zero. Measuring it from the landings is the fallback,
            //    not the requirement: on this library the landings do NOT share a
            //    remainder, so Calibrate rightly refuses. A zero set from the
            //    on-screen clock must therefore not be thrown away because the
            //    measurement it replaces failed -- which is what the first version
            //    did, by calling Calibrate before looking at Options.Zero.
            //    A row with exact ticks (a queued camera path, or POV in/out marked in the game) needs no zero at all:
            //    its ticks are the demo's own clock, which is what the recorders and the camera track run on.
            int zero = _opt.Zero;
            int startTick, endTick;
            if (take.HasTicks)
            {
                startTick = take.TickIn - BeforeFor(take);
                endTick = take.TickOut + AfterFor(take);
                r.StartTick = startTick;
                Log("    exact ticks, no zero needed: " + take.TickIn + "-" + take.TickOut + ", recording " + startTick + " to " + endTick
                    + " (" + (BeforeFor(take) / 1000.0).ToString("0.0#", CultureInfo.InvariantCulture) + " s before, "
                    + (AfterFor(take) / 1000.0).ToString("0.0#", CultureInfo.InvariantCulture) + " s after)");
            }
            else
            {
            if (zero >= 0)
                Log("    using the zero you set: " + zero + " (not measuring)");
            else if (!Calibrate(out zero))
            {
                // a test run, 2026-09-29: on paintball and studio the landings were ragged
                // (10,100 / 10,400 / 10,700 jumps), and 0132 had already shown the grid is a
                // per-load guide, not the HUD's zero. The HUD-verified zeros so far are 2300
                // (Nuketown) and 2250 (Studio); retail's was 2000. So assume the fallback and
                // say so, rather than throw the take away; the buffers carry the difference.
                if (_opt.FallbackZero < 0)
                {
                    r.Message = "could not measure this load's zero - read the paused clock and press Set zero";
                    return r;
                }
                zero = _opt.FallbackZero;
                Log("    ZERO ASSUMED: " + zero + " (the landings disagree; HUD-verified zeros so far are 2300 and 2250). Check the preview.");
                r.Message = "zero assumed " + zero;
            }
            r.Zero = zero;
            Log("    zero = " + zero + " ms  (tick = clock x 1000 + " + zero + ")");

            startTick = GameClock.TickForClock(take.ClockIn, zero) - BeforeFor(take);
            endTick = GameClock.TickForClock(take.ClockOut, zero) + AfterFor(take);
            r.StartTick = startTick;
            Log("    recording " + Take.Clock(take.ClockIn) + " - " + (BeforeFor(take) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)
                + " s to " + Take.Clock(take.ClockOut) + " + " + (AfterFor(take) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)
                + " s: ticks " + startTick + " to " + endTick);
            }
            // a take near the film's end: the "after" would run past it, and a recording waits for a tick past its end that
            // never comes (the code review's B-11; the last kill is often the trickshot)
            int filmEnd = _clock.EndTick;
            if (filmEnd > 0 && endTick > filmEnd - 100)
            {
                Log("    the film ends at tick " + filmEnd + ": recording to " + (filmEnd - 100) + " instead of " + endTick);
                endTick = filmEnd - 100;
            }

            // 3. Get back to before the start tick: a keyframe jump, or steps.
            // The POV is set again inside GoTo, between the jump back and the approach.
            // After a fresh load the game puts the view back on the demo's own player a
            // few seconds in (sandbox, 2026-09-29: demo_client 10 held for ~4.5 s, then
            // the index went back to 8 at tick ~12,700). The first batch recorded the
            bool reached = GoTo(startTick, take.Pov);
            if (!reached && !string.IsNullOrEmpty(take.DemoPath) && !_cancel)
            {
                // 2026-10-17 (Mycelium's laptop, and the sandbox 0454-0456): demo_back moved nothing, so a take could not get back
                // from past its end, and every row failed. The film loaded again starts at its beginning: its mode, the POV,
                // then forward to the start tick, as the approach does - what the Console's timeline does to go back.
                Log("    the jump back did nothing: loading the film again and playing forward to the start");
                _loadedDemo = null;
                if (LoadTheDemo(take))
                {
                    _loadedDemo = take.DemoPath;
                    if (wantCodcast >= 0 && _clock.IsCodcast != (wantCodcast == 1)) { _clock.SetCodcast(wantCodcast == 1); Thread.Sleep(500); }
                    int now = SettledTick();
                    if (now != int.MinValue && now > startTick) Log("    the film starts at " + now + ", after this take's start " + startTick + ": recording from there");
                    // a fresh load hands the view back to the film's own player some way in (sandbox 2026-09-29: at tick ~12,700;
                    // the laptop 2026-10-17 00:45 and 00:51: "the view left POV during the approach", even with the POV set 6 s
                    // in), so the film is played to just before the take first, and the POV set there, with 1.5 s to spare
                    if (now != int.MinValue && startTick - now > 2500)
                    {
                        Log("    to just before the take first (a fresh load hands the view back to the film's own player on the way)");
                        ApproachStart(startTick - 1500);
                    }
                    reached = EnsurePov(take.Pov, startTick) && (now > startTick || ApproachStart(startTick));
                }
            }
            if (!reached) { r.Message = "could not reach the start tick on POV '" + take.Pov + "'"; return r; }
            r.LandedTick = _clock.Tick;
            Log("    sitting at " + r.LandedTick + ", which is " + (startTick - r.LandedTick) + " ms before the start tick");
            int povSlot = string.IsNullOrEmpty(take.Pov) ? -1 : _clock.SlotForName(take.Pov);
            if (povSlot >= 0 && _clock.PlayerIndex != povSlot)
            {
                r.Message = "the view left POV '" + take.Pov + "' during the approach (slot " + _clock.PlayerIndex + ")";
                return r;
            }

            // The theater bar would be in every frame. Each load brings it back, so one
            // F4 after a load hides it; further takes on the same demo need none.
            if (_barShowing)
            {
                GameKeys.PressF4();
                Thread.Sleep(400);
                _barShowing = false;
                Log("    F4: theater bar hidden");
            }
            // The HUD (Mycelium, 2026-10-13: "press F2 to toggle hud"): in a film the kill feed and score stay drawn with
            // cg_draw2d 0 and hud_enable 0 (sandbox 0376/0377); the theater's own F2 hides them. A toggle, so one press
            // per load, like F4. A queue mixes rows that hide it (cinematics) with rows that may show it (POV), so once
            // the state is known (after one of our loads) F2 also brings it back for a row that wants it.
            bool hide = HideHudFor(take);
            if (hide && _hudShowing)
            {
                GameKeys.PressF2();
                Thread.Sleep(400);
                _hudShowing = false;
                Log("    F2: HUD hidden (kill feed, score, name tags)");
            }
            else if (!hide && !_hudShowing && _hudKnown)
            {
                GameKeys.PressF2();
                Thread.Sleep(400);
                _hudShowing = true;
                Log("    F2: HUD shown again (this row keeps it)");
            }

            // 4. A cinematic (2026-10-12): the take's camera track drives the theater's free camera at the film's
            //    tick (RipperBridge dev9 "cam path": free camera on, the angle accumulator found, the pose written every
            //    frame). The player's view is not used, so the row's pov is ignored. Mycelium: "It probably be easiest to
            //    Que in game Cinematics from Blender, even if the user still wanted to record using the in-game look."
            bool cam = !string.IsNullOrEmpty(take.CamPath);
            if (cam)
            {
                if (!File.Exists(take.CamPath)) { r.Message = "no camera track at " + take.CamPath; return r; }
                string reply = BridgeClient.Ask("cam path " + take.CamPath) ?? ("no answer: " + BridgeClient.LastError);
                Log("    cam path: " + reply);
                if (!reply.StartsWith("ok", StringComparison.Ordinal)) { r.Message = "camera track refused: " + reply; return r; }
            }

            // 5. Settings, the ticks, then start.
            bool recorded;
            try { recorded = Record(take, startTick, endTick, ref r); }
            finally
            {
                if (cam)
                {
                    Log("    cam off: " + (BridgeClient.Ask("cam off") ?? BridgeClient.LastError)
                        + "; first person: " + (BridgeClient.Ask("cam mode 0") ?? BridgeClient.LastError));
                }
            }
            if (!recorded) return r;

            // 6. The extra passes (Reluduali recorder): the same ticks again, aligned frame for frame by the tick
            foreach (string pass in ExtraPasses(take))
            {
                if (_cancel) break;
                string why = RecordPass(take, startTick, endTick, pass, wantCodcast);
                if (why != null) { r.Message = (r.Message.Length > 0 ? r.Message + "; " : "") + "the " + pass + " pass: " + why; Log("    " + r.Message); }
            }

            r.Ok = true;
            return r;
        }

        private IEnumerable<string> ExtraPasses(Take take)
        {
            if (_opt.NoPlayers) yield return "world";
            if (_opt.PlayersOnly) yield return "players";
        }

        // the pass being recorded now ("" the take itself): its file name's suffix and RipperBridge's rule
        private string _pass = "";

        /// <summary>One extra pass: back to the take's start (a keyframe jump, or the film loaded again), the camera track
        /// again if the row has one, and the same ticks recorded with the rule on. Null when it worked, else why not.</summary>
        private string RecordPass(Take take, int startTick, int endTick, string pass, int wantCodcast)
        {
            Log("  the " + pass + " pass: back to " + startTick + ", the same ticks with " + (pass == "world" ? "the players hidden" : "only the players"));
            bool reached = GoTo(startTick, take.Pov);
            if (!reached && !string.IsNullOrEmpty(take.DemoPath) && !_cancel)
            {
                Log("    the jump back did nothing: loading the film again");
                _loadedDemo = null;
                if (LoadTheDemo(take))
                {
                    _loadedDemo = take.DemoPath;
                    if (wantCodcast >= 0 && _clock.IsCodcast != (wantCodcast == 1)) { _clock.SetCodcast(wantCodcast == 1); Thread.Sleep(500); }
                    int now = SettledTick();
                    if (now != int.MinValue && startTick - now > 2500) ApproachStart(startTick - 1500);
                    reached = EnsurePov(take.Pov, startTick) && (_clock.Tick > startTick || ApproachStart(startTick));
                }
            }
            if (!reached) return "could not get back to the start tick";
            bool cam = !string.IsNullOrEmpty(take.CamPath);
            if (cam)
            {
                string reply = BridgeClient.Ask("cam path " + take.CamPath) ?? ("no answer: " + BridgeClient.LastError);
                if (!reply.StartsWith("ok", StringComparison.Ordinal)) return "camera track refused: " + reply;
            }
            var pr = new Result { Take = take };
            _pass = pass;
            try { if (!Record(take, startTick, endTick, ref pr)) return pr.Message.Length > 0 ? pr.Message : "the recording failed"; }
            finally
            {
                _pass = "";
                if (cam) { BridgeClient.Ask("cam off"); BridgeClient.Ask("cam mode 0"); }
            }
            Log("    the " + pass + " pass: " + pr.Frames + " frames");
            return null;
        }

        /// <summary>
        /// Ends playback, waits for the theater to actually close, then loads the
        /// take's demo through RipperBridge (the host's own load as fallback). Loading over a
        /// running demo is what killed the client on 2026-09-27, so the wait is
        /// the point of this, not a formality.
        /// </summary>
        private bool LoadTheDemo(Take take)
        {
            if (_clock.InTheater)
            {
                Pause(null);
                Log("    ending playback before loading");
                // demo_stop closes the theater in about 0.3 s, paused or not. The host's
                // own end of playback is only the fallback.
                if (!SendUntil("demo_stop", () => !_clock.InTheater, 3000, 3, "the theater closing"))
                {
                    if (!ConsoleHost.Current.EndDemoPlayback()) Log("    EndDemoPlayback: " + ConsoleHost.Current.LastError);
                    if (!Wait(() => !_clock.InTheater, 20000, "the theater to close")) return false;
                }
                // The menu reloads its zones after a stop. A load sent 1 s later never
                // came up; sent 8 s later it came up in 8 s (sandbox, 2026-09-29). So let
                // the menu settle first.
                Thread.Sleep(8000);
            }

            Log("    loading " + Path.GetFileName(take.DemoPath));
            _kfAt = int.MinValue;                   // RipperBridge clears its keyframes on a load: they belong to the film
            // What was up before the load. "The theater is up" alone is not "our demo is up": in the
            // sandbox (2026-10-01, 0187) a film started from the theater menu a moment earlier came up
            // during the load, the take took it for its own demo (end tick 286400, the old film's), and
            // then failed when the game swapped to the new one. So the load counts only once the theater
            // is up with a DIFFERENT end tick, or after the theater has been seen closed and up again.
            int endBefore = _clock.EndTick;
            bool sawClosed = !_clock.InTheater;
            // RipperBridge loads the film from inside the game (the film copied into memory,
            // its record and thumbnail written, demo_play), and refuses while a film is
            // still up. The host's own load is the fallback.
            string reply = BridgeClient.Ask("loaddemo " + take.DemoPath, 5000, 2);
            if (reply != null && reply.StartsWith("ok", StringComparison.Ordinal))
                Log("    RipperBridge: " + reply);
            else
            {
                Log("    RipperBridge did not load it (" + (reply ?? BridgeClient.LastError) + "); trying " + ConsoleHost.Current.Name);
                if (!ConsoleHost.Current.LoadDemoFile(take.DemoPath)) { Log("    load failed: " + ConsoleHost.Current.LastError); return false; }
            }

            // Loading puts the demo up; it does not always start it, and the
            // theater reads as closed until playback is actually running.
            Func<bool> ourDemoUp = () =>
            {
                bool up = _clock.InTheater;
                if (!up) { sawClosed = true; return false; }
                int end = _clock.EndTick;
                return end > 0 && (end != endBefore || sawClosed);
            };
            if (!Wait(ourDemoUp, 20000, "the demo to come up"))
            {
                Log("    loaded but not playing - nudging it");
                Resume(null);
                if (!Wait(ourDemoUp, 60000, "playback to start")) return false;
            }
            Thread.Sleep(1500);   // let the first frames settle before touching the clock
            Log("    loaded, end tick " + _clock.EndTick);
            return true;
        }

        /// <summary>
        /// Plays past the end of the take at speed, then pauses. This is what makes
        /// the jump points exist; without it demo_back has nowhere to land, and the
        /// tick readings are wrong.
        ///
        /// It deliberately does NOT measure the zero. Playing past is required;
        /// measuring is one way of getting a number that can also be typed in.
        /// </summary>
        private bool PlayPast(Take take)
        {
            // The clock's zero is not known yet, so aim using the widest plausible
            // one and overshoot deliberately: being too far forward is free.
            // The "after" buffer extends the take, so it has to be played past too.
            // A row with exact ticks knows its end exactly; the grid spacing still lets the last jump point land past it.
            int target = take.HasTicks ? take.TickOut + AfterFor(take) + 500 + GameClock.GridSpacing
                                       : (take.ClockOut + 3) * 1000 + AfterFor(take) + GameClock.GridSpacing;
            int filmEnd = _clock.EndTick;                                  // never aim past the film's own end (B-11)
            if (filmEnd > 0 && target > filmEnd - 300) target = filmEnd - 300;
            Log("    playing past " + (take.HasTicks ? "tick " + take.TickOut : Take.Clock(take.ClockOut)) + " at timescale " + _opt.Timescale);

            SetTimescale(_opt.Timescale);
            if (!Resume(null))
            {
                SetTimescale(1);
                Log("    playback would not start");
                return false;
            }

            // the pass through the film: keyframes all the way, so any later take behind it jumps back in one step
            bool reached = Wait(() => { int t = _clock.Tick; KeyframeOnTheWay(t); return t != int.MinValue && t >= target; },
                                _opt.WatchTimeoutMs, "playback to pass the take");

            Pause(null);
            SetTimescale(1);
            Thread.Sleep(400);

            if (!reached) { Log("    never reached " + target + " (now " + _clock.Tick + ")"); return false; }

            Log("    paused past the take at " + SettledTick());
            return true;
        }

        /// <summary>
        /// Measures this load's zero by jumping back several times and reading
        /// each landing ONCE IT HAS SETTLED.
        ///
        /// Reading too early is what made the first version wrong by four
        /// seconds: landings came back 10,200 apart with different remainders
        /// because the clock was still moving when it was sampled. A landing only
        /// counts when two reads agree.
        ///
        /// If the settled landings share a remainder against the 10 s grid, that
        /// remainder is the zero. If they do not, the jumps are relative rather
        /// than to fixed points, and this says so instead of inventing a number --
        /// in which case the zero has to come from the on-screen clock.
        /// </summary>
        public bool Calibrate(out int zero)
        {
            zero = -1;
            var landings = new List<int>();

            for (int i = 0; i < 4 && !_cancel; i++)
            {
                int before = SettledTick();
                if (before == int.MinValue) return false;
                if (!SendUntil("demo_back", () => { int t = _clock.Tick; return t != int.MinValue && Math.Abs(t - before) > 1000; }, 2500, 3, "a jump back")) return false;

                int landed = SettledTick();
                if (landed == int.MinValue) return false;
                landings.Add(landed);
                Log("    jump " + (i + 1) + ": " + before + " -> " + landed
                    + "   moved " + (before - landed) + "   remainder " + GameClock.ZeroFromLanding(landed));
            }

            if (landings.Count < 2) return false;

            // The remainder most landings agree on (within 60 ms), if at least two do and
            // they are a majority. A test run's first take (paintball, 2026-09-29) had
            // three landings on remainder 3300 and a fourth jump of 10,400 onto 2900, and
            // demanding all four agree threw the take away. 0132 also showed the grid is a
            // per-load guide to the zero rather than the HUD's own (the tick is what stays
            // put), so an outlier is logged and the 3 s buffers carry the rest.
            int best = -1, bestCount = 0;
            foreach (int a in landings)
            {
                int ra = GameClock.ZeroFromLanding(a), n = 0;
                foreach (int b in landings) if (Math.Abs(GameClock.ZeroFromLanding(b) - ra) <= 60) n++;
                if (n > bestCount) { bestCount = n; best = ra; }
            }

            if (bestCount == landings.Count)
            {
                zero = best;
                Log("    landings share a remainder, so the jump points are fixed: zero = " + zero);
                return true;
            }
            if (bestCount >= 2 && bestCount * 2 > landings.Count)
            {
                zero = best;
                Log("    " + bestCount + " of " + landings.Count + " landings share remainder " + best + "; using it as the zero (the others are logged above)");
                return true;
            }

            Log("    landings do NOT share a remainder, so demo_back moves relative to where you are.");
            Log("    the zero cannot be read from a landing on this demo - set it from the on-screen clock.");
            return false;
        }

        /// <summary>A tick only counts once two reads agree; otherwise it is being sampled mid-move.</summary>
        private int SettledTick(int tries = 40)
        {
            for (int i = 0; i < tries && !_cancel; i++)
            {
                int a = _clock.Tick;
                Thread.Sleep(120);
                int b = _clock.Tick;
                if (a == b && a != int.MinValue) return a;
            }
            Log("    the clock never settled");
            return int.MinValue;
        }

        /// <summary>
        /// Gets to the start tick the only ways this game actually supports
        /// (Mycelium, 2026-09-28):
        ///
        ///   - demo_back, which lands on the 10 s grid at or before a point;
        ///   - playing forward at a timescale, which reaches anything;
        ///   - jumping FORWARD, but only where the demo has already been played
        ///     and the jump points exist.
        ///
        /// So: step back past the start, then close the gap by playing. Stepping
        /// alone can leave us 9 s early, and at 600 fps every early second is 600
        /// wasted frames in every pass folder.
        /// </summary>
        private bool GoTo(int startTick, string pov)
        {

            // A keyframe first (RipperBridge dev21): back, or far forward, in one step.
            if (KeyframeTo(startTick)) return EnsurePov(pov, startTick) && ApproachStart(startTick);

            // The POV between the jump back and the approach: its nudge needs replay
            // to spare, and the approach then ends ~100 ms short with the camera on
            // the right player.
            return StepBackTo(startTick) && EnsurePov(pov, startTick) && ApproachStart(startTick);
        }

        // ---- keyframes (RipperBridge dev21, 2026-10-17) -------------------------------------------------------------
        // The snapshot the game itself keeps for a theater camera marker, taken by the bridge outside the marker list
        // ("kf save") and jumped to ("kf jump <tick>": the last one at or before it). demo_back moved nothing (2026-10-17),
        // so a take behind the playhead used to load the film again. Now the approach keeps one every 3 s of film it plays
        // through, and a later take on the same film jumps back to just before its start.
        private const int KfEvery = 3000;
        private int _kfAt = int.MinValue;          // the film tick of the last keyframe taken (int.MinValue: none since the load)
        private bool _kfNone;                      // an older RipperBridge: no "kf"

        /// <summary>On the way through the film: a keyframe every KfEvery ms of film (from inside Wait's checks).</summary>
        private void KeyframeOnTheWay(int t)
        {
            if (_kfNone || t == int.MinValue) return;
            if (_kfAt != int.MinValue && t >= _kfAt && t - _kfAt < KfEvery) return;
            string r = BridgeClient.Ask("kf save", 3000, 1);
            if (r != null && r.StartsWith("error unknown command", StringComparison.Ordinal)) { _kfNone = true; return; }
            _kfAt = t;
        }

        /// <summary>Jumps to the last keyframe KfLeadMs before the start, when that beats playing.</summary>
        // 2 s: room for the POV's nudge (a jump puts the view back on slot 0 - 0492 - and EnsurePov plays 250 ms to switch
        // it). 0477 tried 5.5 s against the lost ticks; those were the lockstep's, fixed in RipperBridge dev21 (0491).
        private const int KfLeadMs = 2000;
        // Sandbox 0476-0477: both takes after a jump lost ticks - the recorder's lockstep falling a frame out of step, a race
        // the jump made likely. Fixed in RipperBridge dev21 by pairing each step with its own frame (0491: four batch takes
        // after jumps, 0 bad steps; 1000 fps too). So the batch jumps when the bridge can pair ("pairable" in kf status), and
        // only a bridge that cannot gets a jumped film loaded again first. A "kf-no-jump-before-takes.flag" file beside the
        // Console turns the jump off.
        private static readonly bool KfJumpBeforeTakes = !File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kf-no-jump-before-takes.flag"));

        /// <summary>RipperBridge's "kf status": this load's keyframe jumps, and whether its lockstep can pair frames.</summary>
        private bool KfStatus(out long loadJumps, out bool pairable)
        {
            loadJumps = 0; pairable = false;
            if (_kfNone) return false;
            string s = BridgeClient.Ask("kf status", 2000, 1);
            if (s == null) return false;
            if (s.StartsWith("error unknown command", StringComparison.Ordinal)) { _kfNone = true; return false; }
            loadJumps = BridgeClient.Field(s, "load_jumps");
            pairable = BridgeClient.Field(s, "pairable") == 1;
            return true;
        }

        /// <summary>Whether this film load was jumped by a keyframe AND the bridge cannot pair frames (then it is loaded again).</summary>
        private bool KeyframeJumpedSinceLoad()
        {
            long jumps; bool pairable;
            return KfStatus(out jumps, out pairable) && jumps > 0 && !pairable;
        }

        private bool KeyframeTo(int startTick)
        {
            if (!KfJumpBeforeTakes || _kfNone) return false;
            long jumps0; bool pairable;
            if (!KfStatus(out jumps0, out pairable) || !pairable) return false;   // an unpaired lockstep: no jump before a recording
            int now = SettledTick();
            if (now == int.MinValue) return false;
            int want = startTick - KfLeadMs;
            if (now <= want && want - now < 20000) return false;       // a little ahead: playing forward is as quick
            string list = BridgeClient.Ask("kf list", 2000, 1);
            if (list == null) return false;
            if (list.StartsWith("error unknown command", StringComparison.Ordinal)) { _kfNone = true; return false; }
            int best = int.MinValue, colon = list.IndexOf(':');
            if (colon >= 0)
                foreach (string p in list.Substring(colon + 1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                { int v; if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v <= want && v > best) best = v; }
            if (best == int.MinValue || (now <= want && best <= now)) return false;     // none, or only behind us on the way forward
            string r = BridgeClient.Ask("kf jump " + want.ToString(CultureInfo.InvariantCulture), 10000, 1);
            if (r == null || !r.StartsWith("ok", StringComparison.Ordinal)) { Log("    the keyframe jump did not take: " + (r ?? BridgeClient.LastError)); return false; }
            Thread.Sleep(300);
            int t = SettledTick();
            Log("    keyframe: " + r + "; at " + t);
            return t != int.MinValue && t <= startTick;
        }

        /// <summary>
        /// Puts the camera on the take's POV.
        ///
        /// `demo_client n` sets the slot directly. Clicking is the fallback: by hand a click steps the slot
        /// by exactly one and wraps through the occupied slots, so this can walk to
        /// the target -- but a posted click may be ignored where a real one is not.
        ///
        /// Either way it VERIFIES against selectedPlayerIndex rather than trusting
        /// the command, because demo_switchplayer and four +/- bind commands were
        /// all accepted in silence and did nothing at all.
        /// </summary>
        /// <summary>
        /// Puts the view back on the POV if something moved it (a fresh load hands it
        /// back to the demo's own player a few seconds in), verified by the index.
        ///
        /// The CAMERA follows a POV change only once playback runs. At 10 fps that
        /// was a frame; at 1000 frames a second the first ~100 frames of the 0120
        /// take were still the previous player (2026-09-29). So this runs BEFORE the
        /// approach, with GoToPov's nudge, and with at least a second of replay to
        /// spare before the start.
        /// </summary>
        private bool EnsurePov(string pov, int startTick)
        {
            if (string.IsNullOrEmpty(pov)) return true;
            int want = _clock.SlotForName(pov);
            if (want < 0) return GoToPov(pov);
            if (_clock.PlayerIndex == want) { Log("    still on " + pov + " (slot " + want + ")"); return true; }
            Log("    the view is on slot " + _clock.PlayerIndex + " now; back to " + pov + " (slot " + want + ")");
            int now = SettledTick();
            if (now != int.MinValue && startTick - now < 1000)
                SendUntil("demo_back", () => _clock.Tick < now - 1000, 2500, 3, "a jump back to make room for the POV change");
            return GoToPov(pov);
        }

        /// <summary>The POV move on its own, so it can be tested without running a take.</summary>
        public bool GoToPovPublic(string pov) { return GoToPov(pov); }

        private bool GoToPov(string pov)
        {
            if (string.IsNullOrEmpty(pov)) return true;

            int want = _clock.SlotForName(pov);
            // Right after a load the roster can still be empty (a test run, Yemen demo,
            // 2026-09-29: "no slot" with nothing listed). Play a couple of seconds and look again.
            for (int tries = 0; want < 0 && tries < 3 && !_cancel; tries++)
            {
                Log("    no slot for POV '" + pov + "' yet; playing 2 s and reading the roster again");
                Resume(null); Thread.Sleep(2000); Pause(null);
                want = _clock.SlotForName(pov);
            }
            if (want < 0)
            {
                Log("    no slot for POV '" + pov + "'. The roster reads:");
                string[] names = _clock.Roster();
                for (int i = 0; i < names.Length; i++)
                    if (names[i].Length > 0) Log("      " + i + "  " + names[i]);
                return false;
            }

            if (_clock.PlayerIndex == want) { Log("    already on " + pov + " (slot " + want + ")"); return true; }

            Log("    POV '" + pov + "' is slot " + want + "; currently " + _clock.PlayerIndex);
            if (SendUntil("demo_client " + want.ToString(CultureInfo.InvariantCulture), () => _clock.PlayerIndex == want, 1500, 3, "the POV change"))
            {
                // The index changes at once but the CAMERA does not: the view only
                // switches when playback resumes (Mycelium, 2026-09-29). Nudge it, so
                // the take cannot open on a frame from the previous player. Safe
                // wherever there is replay to spare before the start: EnsurePov only
                // calls this before the approach (see GoTo).
                Resume(null);
                Thread.Sleep(250);
                Pause(null);

                Log("    on " + pov + " (slot " + want + ")");
                return true;
            }

            Log("    demo_client did not take; clicking through instead");
            for (int i = 0; i < 2 * GameClock.RosterSlots && !_cancel; i++)
            {
                if (!GameKeys.Click(true)) { Log("    no game window to click"); return false; }
                Thread.Sleep(220);
                if (_clock.PlayerIndex == want) { Log("    on " + pov + " after " + (i + 1) + " clicks"); return true; }
            }

            Log("    could not reach " + pov + "; left on slot " + _clock.PlayerIndex);
            return false;
        }

        /// <summary>
        /// Closes the last few seconds by playing forward, because demo_back lands
        /// on the 10 s grid and can leave us most of a jump early. It stops SHORT
        /// of the start tick deliberately: arriving early only adds pre-roll, while
        /// arriving late clips the beginning off the take.
        /// </summary>
        private bool ApproachStart(int startTick)
        {
            int now = SettledTick();
            if (now == int.MinValue) return false;
            // Fast, then normal speed, then the fine finish below. Each stage stops with room for
            // the pause's own delay at that speed (~100-300 ms of wall time): 4 s of replay at 6x,
            // 0.6 s at 1x. Before 2026-10-01 the "fast" stage silently ran at 1x (see SetTimescale).
            if (now < startTick - 6000)
            {
                Log("    " + (startTick - now) + " ms early; fast forward at timescale " + _opt.Timescale);
                SetTimescale(_opt.Timescale);
                if (!Resume(null)) { SetTimescale(1); Log("    playback would not start"); return false; }
                bool fast = Wait(() => { int t = _clock.Tick; KeyframeOnTheWay(t); return t != int.MinValue && t >= startTick - 4000; },
                                 120000, "4 s before the start tick");
                Pause(null);
                SetTimescale(1);
                Thread.Sleep(400);
                if (!fast) { Log("    never got near the start tick"); return false; }
                now = SettledTick();
            }
            if (now < startTick - 1200)
            {
                Log("    " + (startTick - now) + " ms early; playing forward to the start");
                if (!Resume(null)) { Log("    playback would not start"); return false; }

                bool reached = Wait(() => { int t = _clock.Tick; KeyframeOnTheWay(t); return t != int.MinValue && t >= startTick - 600; },
                                    120000, "the start tick");

                Pause(null);
                Thread.Sleep(400);

                if (!reached) { Log("    never reached the start tick"); return false; }
                Log("    settled at " + SettledTick() + " for a start tick of " + startTick);
            }

            // The fine approach. The coarse stage lands 300-600 ms short; a quarter
            // speed finish lands within about 100 ms, still short of the start.
            int gap = startTick - SettledTick();
            if (gap > 150)
            {
                SetTimescale(0.25);
                if (Resume(null))
                {
                    Wait(() => { int t = _clock.Tick; return t != int.MinValue && t >= startTick - 100; }, 15000, "the start tick (fine)");
                    Pause(null);
                    Thread.Sleep(300);
                }
                SetTimescale(1);
                Log("    fine approach: " + gap + " ms short -> " + (startTick - SettledTick()) + " ms short");
            }
            return true;
        }

        /// <summary>demo_back moves one jump point per press, so this presses until it is before the start.</summary>
        private bool StepBackTo(int startTick)
        {
            for (int i = 0; i < _opt.MaxJumps && !_cancel; i++)
            {
                int now = _clock.Tick;
                if (now == int.MinValue) return false;
                if (now <= startTick) return true;

                if (!SendUntil("demo_back", () => _clock.Tick < now - 1000, 2500, 3, "a jump back")) return false;
                Thread.Sleep(_opt.JumpSettleMs);
            }
            Log("    gave up stepping back at " + _clock.Tick + " (wanted at or before " + startTick + ")");
            return false;
        }


        private bool SeekTo(int targetTick)
        {
            int before = _clock.Tick;
            Send("demo_desiredTime " + targetTick.ToString(CultureInfo.InvariantCulture));
            bool moved = Wait(() => Math.Abs(_clock.Tick - targetTick) < 1500, 6000, "demo_desiredTime to land");
            if (moved) return true;

            // Fall back to the keys the theater definitely has: jump back or
            // forward a grid step at a time, then play the remainder.
            Log("    demo_desiredTime did not move it (" + before + " -> " + _clock.Tick + "); stepping instead");
            for (int i = 0; i < 80 && !_cancel; i++)
            {
                int now = _clock.Tick;
                if (now > targetTick + GameClock.GridSpacing) { Send("demo_back"); }
                else if (now < targetTick - GameClock.GridSpacing) { Send("demo_forward"); }
                else break;
                Thread.Sleep(350);
            }
            return Math.Abs(_clock.Tick - targetTick) <= GameClock.GridSpacing;
        }

        private bool Record(Take take, int startTick, int endTick, ref Result r)
        {
            string name, dir;
            OutputFor(take, out dir, out name);
            string why;
            string userCfg = CfgFor(take, out why);
            if (userCfg == null) { r.Message = why; Log("    " + why); return false; }
            return RecordReluduali(take, startTick, endTick, dir, name, userCfg, ref r);
        }

        // ---- the Reluduali recorder (2026-10-03) -------------------------------------------------
        // RipperBridge 0.11's "rec": the game's own fixedtime steps the film 1000/fps whole ms a frame, in lockstep
        // with the frames drawn (its main thread waits for each one), and every frame goes to ffmpeg with its tick
        // in <video>.frames.csv. Start and end are ticks of the demo's own clock. Measured in the sandbox: 0 lost
        // ticks over 1,001-5,000 frames.
        private bool RecordReluduali(Take take, int startTick, int endTick, string dir, string name, string userCfg, ref Result r)
        {
            if (!_opt.DryRun)
            {
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { r.Message = "could not create " + dir + ": " + ex.Message; Log("    " + r.Message); return false; }
            }
            // cg_draw2d is cheat-protected: a plain console "cg_draw2d 0" answers ok and changes nothing, so the 103
            // rating cinematics of 2026-10-12 (0355) all kept the kill feed. UserCfg.Send clears the flag first.
            string hud = "cg_draw2d " + (HideHudFor(take) ? 0 : 1) + ";cg_drawFPS 0";
            Log("    " + (_opt.DryRun ? "[dry run] would send" : "HUD") + " (" + hud + ")" + (_opt.DryRun ? "" : ": " + UserCfg.Send(hud, _clock.HasDvar, Log)));
            if (!_opt.DryRun)
            {
                string look = Looks.Send(Looks.MapOf(take.DemoPath) ?? Looks.CurrentMap(), _clock.HasDvar, Log);
                Log("    " + look);
            }
            if (!string.IsNullOrWhiteSpace(userCfg))
                Log("    " + (string.IsNullOrEmpty(take.CfgPath) ? "" : Path.GetFileName(take.CfgPath) + ": ") + UserCfg.Send(userCfg, _clock.HasDvar, Log));

            int step = Math.Max(1, 1000 / Math.Max(1, FpsFor(take)));
            string video = VideoFile(take, dir, name, _opt.VideoExtension);
            if (_pass.Length > 0) video = Path.Combine(Path.GetDirectoryName(video), Path.GetFileNameWithoutExtension(video) + " - " + _pass + Path.GetExtension(video));
            if (_opt.DryRun)
            {
                Log("    [dry run] would record ticks " + startTick + "-" + endTick + " at " + step + " ms a frame (Reluduali recorder) to " + video);
                r.Message = "dry run";
                return true;
            }
            if (string.IsNullOrEmpty(_opt.FfmpegPath) || !File.Exists(_opt.FfmpegPath))
            { r.Message = "no ffmpeg at '" + _opt.FfmpegPath + "'"; Log("    " + r.Message); return false; }

            // the Console's slider moves that play, written for every frame of this recording and applied by RipperBridge on
            // their own frames (dev40) instead of sent by the Console as the film passes them
            string moves = null;
            if (MovesTrack != null)
            {
                try { moves = MovesTrack(startTick, endTick, step, video + ".params.txt"); }
                catch (Exception ex) { Log("    exact moves not written (" + ex.Message + "); the Designer sends them as the film plays"); }
                if (moves != null) Log("    exact moves: " + Path.GetFileName(moves));
            }
            Log("    recording from tick " + _clock.Tick + " (" + (startTick - _clock.Tick) + " ms before the start tick), Reluduali recorder");
            if (moves != null && MovesRecording != null) MovesRecording(true);
            string reply = BridgeClient.Ask("rec start " + _opt.FfmpegPath + "|" + video + "|" + _opt.VideoCodecArgs
                                            + "|start=" + startTick.ToString(CultureInfo.InvariantCulture)
                                            + "|end=" + endTick.ToString(CultureInfo.InvariantCulture)
                                            + "|step=" + step.ToString(CultureInfo.InvariantCulture)
                                            + (_opt.Depth ? "|depth=" + _opt.DepthDistance.ToString(CultureInfo.InvariantCulture) : "")
                                            + (_opt.Plate ? "|plate=1" : "")
                                            + (_pass.Length > 0 ? "|only=" + _pass : "")
                                            + (moves != null ? "|params=" + moves : ""), 15000, 1);
            if (reply == null || !reply.StartsWith("ok", StringComparison.Ordinal))
            {
                if (moves != null && MovesRecording != null) MovesRecording(false);
                r.Message = "RipperBridge would not record: " + (reply ?? BridgeClient.LastError) + " (the Reluduali recorder needs the RipperBridge that comes with the Designer)";
                Log("    " + r.Message);
                return false;
            }
            Log("    " + reply);

            int expectedFrames = (endTick - startTick) / step;
            long ceiling = Math.Max((long)_opt.RecordTimeoutMs, (long)expectedFrames * _opt.MsAllowedPerFrame);
            int budget = (int)Math.Min(ceiling, int.MaxValue);
            int wantSlot = string.IsNullOrEmpty(take.Pov) ? -1 : _clock.SlotForName(take.Pov);
            int povLostAt = -1, povLostTo = -1, lastFrames = 0;
            bool done = false, stalled = false;
            string status = "";
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long lastChangeMs = 0, nextReportMs = 60000;
            Wait(() =>
            {
                if (wantSlot >= 0 && povLostAt < 0)
                {
                    int pi = _clock.PlayerIndex;
                    if (pi != wantSlot) { povLostAt = _clock.Tick; povLostTo = pi; }
                }
                status = BridgeClient.Ask("rec status", 3000, 1) ?? "";
                if (status.Contains("done 1")) { done = true; return true; }
                int f = (int)Math.Max(0, BridgeClient.Field(status, "frames"));
                long now = watch.ElapsedMilliseconds;
                if (f != lastFrames) { lastFrames = f; lastChangeMs = now; }
                else if (now - lastChangeMs > _opt.StallMs) { stalled = true; return true; }
                if (now >= nextReportMs)
                {
                    nextReportMs = now + 60000;
                    double frac = Math.Max(0.0, Math.Min(1.0, f / (double)Math.Max(1, expectedFrames)));
                    string left = frac > 0.01 ? ", about " + Math.Ceiling(now * (1 - frac) / frac / 60000.0) + " min to go" : "";
                    Log("    " + (now / 60000) + " min: " + f + " frames, " + (frac * 100).ToString("0") + " % of the window" + left);
                }
                return false;
            }, budget, "the recording to finish");
            Log("    recording took " + (watch.ElapsedMilliseconds / 1000) + " s; " + status);
            string stop = BridgeClient.Ask("rec stop", 330000, 1);      // waits for ffmpeg to write the file
            if (moves != null && MovesRecording != null) MovesRecording(false);
            Log("    video: " + (stop ?? "no answer from RipperBridge: " + BridgeClient.LastError));

            var m = System.Text.RegularExpressions.Regex.Match(stop ?? "",
                @"ok stopped: (\d+) frames captured, (\d+) written to ffmpeg.*ffmpeg exit (-?\d+)");
            if (!m.Success) { r.Message = "the video did not finish: " + (stop ?? BridgeClient.LastError); return false; }
            int captured = int.Parse(m.Groups[1].Value), written = int.Parse(m.Groups[2].Value), exit = int.Parse(m.Groups[3].Value);
            r.Frames = captured;
            long skips = BridgeClient.Field(status, "skips");
            if (captured <= 0 || written != captured || exit != 0 || stop.Contains("ERROR"))
            { r.Message = "the video is not complete: " + stop; return false; }
            if (stalled) { r.Message = "the recording stalled: no new frame for " + (_opt.StallMs / 1000) + " s"; return false; }
            if (!done) { r.Message = "recording did not finish in time"; return false; }
            // A lost tick shifts every later frame against the game's time (1 s of game must be 1 s of video).
            if (skips > 0) { r.Message = skips + " tick(s) were not drawn (first after tick " + BridgeClient.Field(status, "first_skip") + ") - record it again"; return false; }
            if (povLostAt >= 0)
            {
                r.Message = "the view left " + take.Pov + " at tick " + povLostAt + " (to slot " + povLostTo + ") - the frames from there are someone else's";
                Log("    " + r.Message);
                return false;
            }
            // the depth pass (RipperBridge dev23+): one PNG per picture frame, none black for want of a copy
            if (_opt.Depth)
            {
                var dm = System.Text.RegularExpressions.Regex.Match(stop, @"depth \(\d+ units\): (\d+) frames, (\d+) written, (\d+) missing");
                if (!dm.Success) { r.Message = "no depth pass: this RipperBridge has none (Setup installs the RipperBridge that comes with the Designer); the video is fine"; Log("    " + r.Message); return false; }
                int dFrames = int.Parse(dm.Groups[1].Value), dWritten = int.Parse(dm.Groups[2].Value), dMissing = int.Parse(dm.Groups[3].Value);
                if (dWritten != captured || dMissing > 0)
                { r.Message = "the depth pass is not complete: " + dWritten + " of " + captured + " frames written, " + dMissing + " black (no copy)"; Log("    " + r.Message); return false; }
                Log("    depth: " + dWritten + " frames (" + _opt.DepthDistance + " units)");
            }
            if (_opt.Plate)
            {
                var pm = System.Text.RegularExpressions.Regex.Match(stop, @"plate: (\d+) frames, (\d+) written, (\d+) missing \(black\), ffmpeg exit (-?\d+)");
                if (!pm.Success) { r.Message = "no clean plate: this RipperBridge has none (Setup installs the RipperBridge that comes with the Designer); the video is fine"; Log("    " + r.Message); return false; }
                int pWritten = int.Parse(pm.Groups[2].Value), pMissing = int.Parse(pm.Groups[3].Value), pExit = int.Parse(pm.Groups[4].Value);
                if (pWritten != captured || pMissing > 0 || pExit != 0)
                { r.Message = "the clean plate is not complete: " + pWritten + " of " + captured + " frames written, " + pMissing + " black (no copy), ffmpeg exit " + pExit; Log("    " + r.Message); return false; }
                Log("    plate: " + pWritten + " frames");
            }
            Log("    video: " + captured + " frames in " + video);
            return true;
        }


        private static string Sanitise(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString().Trim();
        }
    }
}
