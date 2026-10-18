using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ReludualiConsole
{
    /// <summary>
    /// ReludualiConsole.exe --theater [film.demo] (2026-10-17; Mycelium: "a Theater Only Launch of Redacted with Custom UIs", and
    /// "When Launching Our Redacted a user Should be Greeter with text that fades in "Reluduali Black Ops 2" and plays this
    /// Sample then goes into the Theater Screen"). The splash, then the game docked in the Workspace layout with the DEMOS
    /// tab open. The game is started with "+set reluduali_theater 1", which the theater's own menus read (docs\theater-only.md).
    /// It never loads a film itself: loaddemo from the main menu leaves the game's own jump ticks broken (the theater HUD's
    /// "Downloading: 0.00 %", Mycelium 2026-10-17). A film named on the command line goes to the game as reluduali_theater_film,
    /// for the theater's menus to open the game's own way.
    /// A second launch while a Console runs shows the splash itself and asks the running one, over its pipe (bo2batch,
    /// "console ..."), for the same layout and tab: one Console at a time is kept (0051).
    /// </summary>
    public static class TheaterLaunch
    {
        public const string GameFlag = "+set reluduali_theater 1";
        public static bool On { get; private set; }
        public static string Film { get; private set; }

        /// <summary>--theater, and the film after it if one is named (a path, or a file name the theater's menus know).</summary>
        public static bool Parse(string[] args)
        {
            int i = Array.FindIndex(args, a => string.Equals(a, "--theater", StringComparison.OrdinalIgnoreCase));
            if (i < 0) return false;
            On = true;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) Film = args[i + 1];
            return true;
        }

        /// <summary>The first Console: called once its main window is up.</summary>
        public static void Begin(MainWindow win)
        {
            // after the window's own Loaded work (its layout, the tether), not in the middle of it
            win.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                // the early splash came first, so WPF took it for the application's main window: the Designer's is that
                if (Application.Current != null) Application.Current.MainWindow = win;
                var splash = _early;
                if (splash == null) { splash = new TheaterSplash(win); splash.Show(); Start(); }
                Say(Workspace(win.TourConsole));
                Say(win.TourConsole("tool demos"));
                IntPtr console = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                WaitForTheater(splash, h => h == console, null);
            }));
        }

        static TheaterSplash _early;

        /// <summary>
        /// The first Console, at its very start (before its main window, which takes a few seconds to build: sandbox 0563 had
        /// the desktop still showing at 2 s): the splash and the sample at once, the theater layer in and the game started,
        /// so the game loads while the Designer builds itself. Begin takes it from there.
        /// </summary>
        public static void Early()
        {
            try
            {
                _early = new TheaterSplash(null);
                _early.Show();
                Start();
            }
            catch (Exception ex) { App.Note("--theater: the early splash", ex); _early = null; }
        }

        /// <summary>The theater layer in, the game started, and the watch that takes the layer out when the game exits.</summary>
        static void Start()
        {
            App.Note("--theater: " + StartGame() + (Film != null ? " Film: " + Film : ""), null);
            if (_placedIn != null) TheaterLayer.Watch(_placedIn, null);       // out again when this game exits
        }

        /// <summary>A Console is already running: this launch greets, starts the game, asks the running one, then ends.</summary>
        public static void ForRunningConsole(Application app)
        {
            // no second Console window: App.OnStartup returns before it makes one
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;     // the sample rings out after the splash has gone
            var splash = new TheaterSplash(null);
            splash.Show();
            App.Note("--theater beside a running Console: " + StartGame() + (Film != null ? " Film: " + Film : ""), null);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Say(Workspace(c => AskConsole("console " + c)));
                Say(AskConsole("console tool demos"));
            });
            // this process put the theater layer in: it stays (with no window) until the game exits and takes it out again
            bool layerOut = _placedIn == null;
            if (_placedIn != null) TheaterLayer.Watch(_placedIn, () => layerOut = true);
            WaitForTheater(splash, IsConsoleWindow, () =>
            {
                // this process plays the sample: it ends when the sample has rung out (at most 30 s more) and the layer is out
                var quit = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                var limit = DateTime.UtcNow.AddSeconds(30);
                quit.Tick += (s, e) => { if ((DateTime.UtcNow >= TheaterSplash.SampleEnds || DateTime.UtcNow >= limit) && layerOut) { quit.Stop(); app.Shutdown(0); } };
                quit.Start();
            });
        }

        /// <summary>0-1 as a whole percent, for the game's command line (its dvars read as whole numbers there).</summary>
        static string Percent(double v) { return ((int)Math.Round(v * 100)).ToString(System.Globalization.CultureInfo.InvariantCulture); }

        static void Say(string reply) { if (!string.IsNullOrEmpty(reply)) App.Note("--theater: " + reply.Replace("\n", " | "), null); }

        /// <summary>
        /// The Workspace layout for this run only ("layout workspace once", 2026-10-17): the layout the person saved stays saved.
        /// A Console without "once" answers with its usage line; then plain "layout workspace", which it remembers.
        /// </summary>
        static string Workspace(Func<string, string> console)
        {
            string a = console("layout workspace once");
            if (a != null && a.StartsWith("usage", StringComparison.Ordinal)) a = console("layout workspace") + " (this Console has no 'once': the Workspace is now its saved layout)";
            return a;
        }

        static bool _menus;            // the theater's menus are in the game for this launch: the splash waits for their ready
        static string _placedIn;       // the game folder this launch put the theater layer into (it comes out when the game exits)

        /// <summary>Starts Redacted as Where.StartGame does, with the theater's flag after the arguments saved on SETUP, and
        /// the theater layer put in first (TheaterLayer). Never while the game runs: then nothing of ours goes in.</summary>
        static string StartGame()
        {
            _menus = false; _placedIn = null;
            try
            {
                string g = Where.Game;
                if (Process.GetProcessesByName("t6mpv43").Length > 0)
                {
                    _menus = TheaterLayer.Installed(g);
                    return "Redacted is already running: taken in as it is (its menus are the ones it started with; nothing put in).";
                }
                if (g == null) return "The game folder is not known: choose t6mpv43.exe on SETUP.";
                string exe = Path.Combine(g, "t6mpv43.exe");
                if (!File.Exists(exe)) return "There is no t6mpv43.exe in " + g + ".";
                string layer = TheaterLayer.Place(g, out _menus);
                if (File.Exists(Path.Combine(g, TheaterLayer.RecordName))) _placedIn = g;
                string args = Where.Saved("gameargs") ?? "";
                // the theater's flag, and the splash's palette, so the game's own cover takes the same colours (no flash between)
                if (args.IndexOf("reluduali_theater", StringComparison.OrdinalIgnoreCase) < 0)
                    args = GameFlag + " +set reluduali_theater_night " + Percent(TheaterSplash.Sky.Night) + " +set reluduali_theater_warm " + Percent(TheaterSplash.Sky.Warm) + " " + args;
                args = GameCommandLine.Join(args);      // a +wait between every two +commands: Redacted loses every second one
                try { Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = g, UseShellExecute = false }); }
                catch { if (_placedIn != null) { App.Note("--theater: " + TheaterLayer.Remove(g), null); _placedIn = null; _menus = false; } throw; }
                return "Starting Redacted for the theater (" + args + "); " + layer + ".";
            }
            catch (Exception ex) { App.Note("--theater: starting Redacted", ex); return "Could not start Redacted: " + ex.Message; }
        }

        /// <summary>
        /// Twice a second: the game's window, and whose it is. The splash goes once the game is docked (and, with the
        /// theater's menus in the game, once they say reluduali_theater_ready), never before it has been up 4 s, and at the
        /// latest after 90 s. Then the film, if one was named, goes to the game.
        /// </summary>
        static void WaitForTheater(TheaterSplash splash, Func<IntPtr, bool> dockedTo, Action after)
        {
            var clock = Stopwatch.StartNew();
            bool menus = _menus, asking = false, ready = !menus, closing = false;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            t.Tick += (s, e) =>
            {
                if (closing) return;
                int pid; IntPtr game = GameDock.FindGame(out pid);
                bool docked = game != IntPtr.Zero && dockedTo(GetWindow(game, 4));     // GW_OWNER: the Console it is docked in
                if (docked && !ready && !asking)
                {
                    splash.Status = "Opening the theater…";
                    asking = true;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        string a = null;
                        try { a = BatchRecorder.BridgeClient.Ask("dvar reluduali_theater_ready", 600, 1); } catch { }
                        splash.Dispatcher.BeginInvoke(new Action(() => { asking = false; if (DvarIsOne(a)) ready = true; }));
                    });
                }
                bool timeUp = clock.Elapsed.TotalSeconds > 90;
                if (clock.Elapsed.TotalSeconds < 4 || !((docked && ready) || timeUp || splash.Skipped)) return;
                closing = true;
                t.Stop();
                App.Note("--theater: the splash goes after " + clock.Elapsed.TotalSeconds.ToString("0.0") + " s" + (docked ? ", the game docked" : ", the game NOT docked")
                         + (menus ? (ready ? ", the theater's menus ready" : ", the theater's menus NOT ready") : ", no theater menus in the game") + (splash.Skipped ? " (skipped)" : ""), null);
                splash.FadeOut(() => { HandFilm(); if (after != null) after(); });
            };
            t.Start();
        }

        /// <summary>RipperBridge's "dvar" answer says 1: "&lt;name&gt; int 1", "&lt;name&gt; float 1", "&lt;name&gt; string "1"" (as the game made it).</summary>
        public static bool DvarIsOne(string answer)
        {
            if (answer == null || answer.StartsWith("error", StringComparison.Ordinal)) return false;
            string v = answer.Trim();
            int sp = v.LastIndexOf(' ');
            return sp >= 0 && v.Substring(sp + 1).Trim('"') == "1";
        }

        /// <summary>The film named on the command line, to the game for the theater's menus (they open it the game's own way).</summary>
        static void HandFilm()
        {
            if (Film == null) return;
            string name = Path.GetFileName(Film.Trim('"'));
            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (int i = 0; i < 30; i++)
                {
                    string a = null;
                    try { a = BatchRecorder.BridgeClient.Ask("cmd set reluduali_theater_film \"" + name.Replace("\"", "") + "\"", 1000, 1); } catch { }
                    if (a != null && a.StartsWith("ok", StringComparison.Ordinal)) { App.Note("--theater: the film handed to the game: " + name, null); return; }
                    Thread.Sleep(2000);
                }
                App.Note("--theater: the bridge never answered; the film was not handed over: " + name, null);
            });
        }

        /// <summary>One line to the running Console's pipe (\\.\pipe\bo2batch), its whole reply back.</summary>
        static string AskConsole(string line)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", BatchRecorder.BatchPipe.PipeName, PipeDirection.InOut))
                    {
                        pipe.Connect(1500);
                        byte[] b = Encoding.UTF8.GetBytes(line + "\n");
                        pipe.Write(b, 0, b.Length);
                        pipe.Flush();
                        using (var r = new StreamReader(pipe, Encoding.UTF8)) return r.ReadToEnd().Trim();
                    }
                }
                catch { Thread.Sleep(500); }
            }
            return "the running Console's pipe did not answer: " + line;
        }

        /// <summary>
        /// ReludualiConsole.exe --theater-test OUTDIR: what can be checked with no game, no window shown and no sound - the
        /// command line read, the sample as the splash plays it (OUTDIR\splash-pcm16.wav), and the splash drawn at three moments
        /// of its fade (splash-*.png, measured: unseen at 0 s, the words up by 2 s). One PASS/FAIL line each in
        /// OUTDIR\theater-test.txt; exit 0 when all pass.
        /// </summary>
        public static int SelfTest(string outDir)
        {
            Directory.CreateDirectory(outDir);
            var lines = new System.Collections.Generic.List<string>();
            int fails = 0;
            Action<string, bool> check = (what, ok) => { lines.Add((ok ? "PASS  " : "FAIL  ") + what); if (!ok) fails++; };

            check("--theater alone: on, no film", ParseFresh(new[] { "--theater" }) == "on, no film");
            check("--theater with a film: the film", ParseFresh(new[] { "--theater", @"C:\films\a b.demo" }) == @"on, film C:\films\a b.demo");
            check("--theater before another switch: no film", ParseFresh(new[] { "--theater", "--x" }) == "on, no film");
            check("no --theater: off", ParseFresh(new[] { "--gui-tour", "x" }) == "off");
            check("the bridge's answers read: int 1, float 1, string \"1\" yes; 0, 10, an error no",
                  DvarIsOne("reluduali_theater_ready int 1") && DvarIsOne("reluduali_theater_ready float 1") && DvarIsOne("reluduali_theater_ready string \"1\"")
                  && !DvarIsOne("reluduali_theater_ready int 0") && !DvarIsOne("reluduali_theater_ready float 10") && !DvarIsOne("error no such dvar") && !DvarIsOne(null));

            string f = SamplePath();
            check("the sample found: " + (f != null ? Path.GetFileName(f) : "none"), f != null);
            if (f != null)
            {
                byte[] pcm = TheaterSplash.Pcm16(File.ReadAllBytes(f));
                File.WriteAllBytes(Path.Combine(outDir, "splash-pcm16.wav"), pcm);
                int format = BitConverter.ToInt16(pcm, 20), ch = BitConverter.ToInt16(pcm, 22), rate = BitConverter.ToInt32(pcm, 24), bits = BitConverter.ToInt16(pcm, 34);
                double secs = (pcm.Length - 44) / (double)(rate * ch * bits / 8);
                int peak = 0;
                for (int i = 44; i + 1 < pcm.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(pcm, i)));
                check(string.Format(System.Globalization.CultureInfo.InvariantCulture, "as 16-bit PCM: {0} ch, {1} Hz, {2:0.00} s, peak {3:0.000} of full scale",
                      ch, rate, secs, peak / 32767.0), format == 1 && bits == 16 && secs > 1 && peak > 0 && peak < 32767);
            }

            // the game's command line: a +wait between every two +commands (Redacted loses every second one)
            check("the command line: nothing stays nothing", GameCommandLine.Join("") == "");
            check("the command line: one +set as it is", GameCommandLine.Join("+set a 1") == "+set a 1");
            check("the command line: a +wait between two", GameCommandLine.Join("+set a 1 +set b 2") == "+set a 1 +wait +set b 2");
            check("the command line: -switches first, quotes whole, the person's own bare +wait out",
                  GameCommandLine.Join("-windowed +set a \"x y\"  +wait +set b 2 +exec c.cfg") == "-windowed +set a \"x y\" +wait +set b 2 +wait +exec c.cfg");
            check("the command line: the theater's flags each on its own",
                  GameCommandLine.Join(GameFlag + " +set reluduali_theater_night 1 +set com_maxfps 250") == "+set reluduali_theater 1 +wait +set reluduali_theater_night 1 +wait +set com_maxfps 250");

            // the theater layer in and out of a fake game folder (as the theater launch does it, and a crash undone)
            LayerChecks(Path.Combine(outDir, "fake-game"), check);

            // the splash, painted as a 1920x1080 screen gets it (a third: 640x360), by day and by night; each frame also saved
            // three times larger with hard pixels, as it is shown
            check("the lettering found: " + (TheaterFile("reluduali.svg") != null ? "reluduali.svg" : "none (plain capitals instead)"), TheaterFile("reluduali.svg") != null);
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            // the sky by the Sun (the site's formulae): 17 Oct 2026 at 40 N 75 W, 13:00 EDT high, 18:20 the horizon, 00:00 night
            double noon = Sky.SunAltitude(new DateTime(2026, 10, 17, 17, 0, 0, DateTimeKind.Utc), 40, -75);
            double dusk = Sky.SunAltitude(new DateTime(2026, 10, 17, 22, 20, 0, DateTimeKind.Utc), 40, -75);
            double midnight = Sky.SunAltitude(new DateTime(2026, 10, 18, 4, 0, 0, DateTimeKind.Utc), 40, -75);
            Sky sn = Sky.FromAltitude(noon), sd = Sky.FromAltitude(dusk), sm = Sky.FromAltitude(midnight);
            check(string.Format(inv, "the Sun: {0:0.0} deg at 13:00 (day {1:0.00}), {2:0.0} at 18:20 (night {3:0.00}, warm {4:0.00}), {5:0.0} at 00:00 (night {6:0.00})",
                  noon, sn.Night, dusk, sd.Night, sd.Warm, midnight, sm.Night),
                  noon > 38 && noon < 43 && sn.Night == 0 && sn.Warm == 0 && dusk < 0 && dusk > -3 && sd.Warm > 0.8 && sd.Night > 0 && sd.Night < 1 && sm.Night == 1 && sm.Warm == 0);
            double plat, plon; Sky.Place(out plat, out plon);
            check(string.Format(inv, "this PC's place from its clock: {0:0} {1:0}", plat, plon), plat >= -90 && plat <= 90 && plon >= -180 && plon <= 180);

            foreach (var when in new[] { "day", "dusk", "night" })
            {
                var p = new SplashPainter(640, 360, when == "day" ? new Sky(0, 0) : when == "dusk" ? Sky.FromAltitude(-0.8) : new Sky(1, 0));
                p.SetStatus("Starting the theater…");
                var px = new byte[640 * 360 * 4];
                Func<double, Int32Rect, int> dark = (t, box) =>
                {
                    p.Paint(t, px);
                    int n = 0;
                    for (int y = box.Y; y < box.Y + box.Height && y < 360; y++)
                        for (int x = box.X; x < box.X + box.Width && x < 640; x++)
                        { int o = (y * 640 + x) * 4; if (0.2126 * px[o + 2] + 0.7152 * px[o + 1] + 0.0722 * px[o] < 0.3 * 255) n++; }
                    return n;
                };
                Func<double, Int32Rect, int> vivid = (t, box) =>
                {
                    p.Paint(t, px);
                    int n = 0;
                    for (int y = box.Y; y < box.Y + box.Height; y++)
                        for (int x = box.X; x < box.X + box.Width; x++)
                        { int o = (y * 640 + x) * 4; int hi = Math.Max(px[o], Math.Max(px[o + 1], px[o + 2])), lo = Math.Min(px[o], Math.Min(px[o + 1], px[o + 2])); if (hi - lo > 115) n++; }
                    return n;
                };
                var whole = new Int32Rect(0, 0, 640, 360);
                var mark = new Int32Rect(320 - 60, (int)(360 * 0.33) - 60, 120, 120);
                var low = new Int32Rect(0, (int)(360 * 0.84), 640, 360 - (int)(360 * 0.84));

                // no black frame first, and every channel on the old Mac's 8 levels
                p.Paint(0, px);
                double lum = 0; bool grid = true;
                for (int o = 0; o < px.Length; o += 4)
                {
                    lum += (0.2126 * px[o + 2] + 0.7152 * px[o + 1] + 0.0722 * px[o]) / 255;
                    for (int j = 0; j < 3; j++) if (Array.IndexOf(SplashPainter.Levels, px[o + j]) < 0) grid = false;
                }
                lum /= 640 * 360;
                check(string.Format(inv, "{0}: the first frame is the ground (mean brightness {1:0.00}), every channel on 8 levels", when, lum), lum > 0.45 && grid);
                int m0 = MarkArea(p, 0.05), m1 = MarkArea(p, 0.5);
                check(string.Format(inv, "{0}: the mark bursts on the hit: {1} px of chrome before, {2} after", when, m0, m1), m0 > 0 && m1 > m0);
                int n0 = dark(0.1, p.NameBox), n1 = dark(1.2, p.NameBox);
                check(string.Format(inv, "{0}: RELUDUALI grows in: {1} dark px at 0.1 s, {2} at 1.2 s", when, n0, n1), n0 < 10 && n1 > 300);
                int s0 = vivid(1.6, p.NameBox), s1 = vivid(SplashPainter.Settled, p.NameBox);
                check(string.Format(inv, "{0}: one sheen: {1} rainbow px at 1.6 s, {2} when settled (the seeds)", when, s0, s1), s0 > s1 + 30);
                int o0 = dark(0.5, p.OpsBox), o1 = dark(SplashPainter.Settled, p.OpsBox);
                check(string.Format(inv, "{0}: BLACK OPS 2 fades in: {1} dark px at 0.5 s, {2} when settled", when, o0, o1), o0 < 10 && o1 > 40);
                int l0 = dark(1.0, low), l1 = dark(SplashPainter.Settled, low);
                check(string.Format(inv, "{0}: the status line: {1} dark px at 1.0 s, {2} when settled", when, l0, l1), l0 < 5 && l1 > 20);

                foreach (double t in new[] { 0.0, 0.1, 0.4, 0.8, 1.2, 1.6, SplashPainter.Settled })
                {
                    p.Paint(t, px);
                    var big = new byte[1920 * 1080 * 4];
                    for (int y = 0; y < 1080; y++) for (int x = 0; x < 1920; x++) Buffer.BlockCopy(px, ((y / 3) * 640 + x / 3) * 4, big, (y * 1920 + x) * 4, 4);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(1920, 1080, 96, 96, PixelFormats.Bgra32, null, big, 1920 * 4)));
                    using (var s = File.Create(Path.Combine(outDir, "splash-" + when + "-" + t.ToString("0.0", inv) + "s.png"))) enc.Save(s);
                }
                var clock = Stopwatch.StartNew();
                for (int k = 0; k < 30; k++) p.Paint(0.5 + k / 60.0, px);
                double ms = clock.Elapsed.TotalMilliseconds / 30;
                check(string.Format(inv, "{0}: a frame paints in {1:0.0} ms (under 16 for 60 a second)", when, ms), ms < 16);
            }
            // the warm tint: the dusk ground's top is warmer (redder than blue) than noon's, and night's foot is not day's
            {
                float[] dt, db, di, ds, wt, wb, wi, ws, nt, nb, ni, ns;
                SplashPainter.Palette(new Sky(0, 0), out dt, out db, out di, out ds);
                SplashPainter.Palette(Sky.FromAltitude(-0.8), out wt, out wb, out wi, out ws);
                SplashPainter.Palette(new Sky(1, 0), out nt, out nb, out ni, out ns);
                check(string.Format(inv, "the sky's colours: noon's top {0:0.00}/{1:0.00} red/blue, dusk's {2:0.00}/{3:0.00}, night's foot not day's", dt[0], dt[2], wt[0], wt[2]),
                      dt[0] < dt[2] && wt[0] > wt[2] && Math.Abs(nb[2] - db[2]) > 0.1);
            }
            File.WriteAllLines(Path.Combine(outDir, "theater-test.txt"), lines);
            return fails == 0 ? 0 : 1;
        }

        /// <summary>The self-test's run of TheaterLayer against a fake game folder: never a real one.</summary>
        static void LayerChecks(string game, Action<string, bool> check)
        {
            if (Directory.Exists(game)) Directory.Delete(game, true);
            Directory.CreateDirectory(Path.Combine(game, "data", "images"));                // as in the game: data\ is there, data\ui_mp is not
            string stub = Path.Combine(game, "..", "stub.lua"), text = Path.Combine(game, "..", "text.lua");
            File.WriteAllBytes(stub, new byte[] { 0x1B, (byte)'L', (byte)'u', (byte)'a', (byte)'Q', 0x0D, 1, 2, 3 });
            File.WriteAllText(text, "-- plain text: it would hang the game");
            string target = Path.Combine(game, TheaterLayer.Rel), record = Path.Combine(game, TheaterLayer.RecordName);
            bool menus;

            TheaterLayer.Place(game, out menus, text);
            check("the layer: text is refused (it would hang the game)", !menus && !File.Exists(target) && !File.Exists(record));

            TheaterLayer.Place(game, out menus, stub);
            check("the layer: put in, with its record", menus && File.Exists(target) && File.Exists(record) && TheaterLayer.IsStub(target));
            TheaterLayer.Remove(game);
            check("the layer: out again, its folders gone, data\\ kept", !File.Exists(target) && !Directory.Exists(Path.Combine(game, "data", "ui_mp"))
                  && Directory.Exists(Path.Combine(game, "data", "images")) && !File.Exists(record));

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.WriteAllText(target, "the person's own");
            TheaterLayer.Place(game, out menus, stub);
            bool aside = Directory.Exists(Path.Combine(game, "reluduali-backup")) && Directory.GetFiles(Path.Combine(game, "reluduali-backup")).Length == 1;
            TheaterLayer.Place(game, out menus, stub);                                       // a crash left it in: undone, then in again
            bool once = Directory.GetFiles(Path.Combine(game, "reluduali-backup")).Length == 1;
            TheaterLayer.Remove(game);
            check("the layer: the person's own file set aside (once, after a crash) and put back",
                  aside && once && File.ReadAllText(target) == "the person's own" && !Directory.Exists(Path.Combine(game, "reluduali-backup")));

            File.Delete(target);
            TheaterLayer.Place(game, out menus, stub);
            File.WriteAllText(target, "changed since");
            TheaterLayer.Remove(game);
            check("the layer: a file that is not ours any more is left alone", File.Exists(target) && File.ReadAllText(target) == "changed since");

            File.Delete(target);
            File.WriteAllText(Path.Combine(game, TheaterLayer.InstalledNote), "the installer keeps them in");
            TheaterLayer.Place(game, out menus, stub);
            check("the layer: nothing put in where the installer keeps the menus", menus && !File.Exists(target) && !File.Exists(record));

            Directory.Delete(game, true);
            File.Delete(stub); File.Delete(text);
        }

        /// <summary>How many canvas pixels the mark's chrome covers at t (the field above 1), for the self-test.</summary>
        static int MarkArea(SplashPainter p, double t)
        {
            var px = new byte[p.W * p.H * 4];
            p.Paint(t, px);
            var bare = new SplashPainter(p.W, p.H, p.Sky);      // the same frame without the mark: the ground and the words only
            var gx = new byte[px.Length];
            bare.PaintWithoutMark(t, gx);
            int n = 0;
            for (int o = 0; o < px.Length; o += 4) if (px[o] != gx[o] || px[o + 1] != gx[o + 1] || px[o + 2] != gx[o + 2]) n++;
            return n;
        }

        static string ParseFresh(string[] a)
        {
            On = false; Film = null;
            string r = Parse(a) ? "on, " + (Film != null ? "film " + Film : "no film") : "off";
            On = false; Film = null;
            return r;
        }

        static bool IsConsoleWindow(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            try { uint pid; GetWindowThreadProcessId(h, out pid); return Process.GetProcessById((int)pid).ProcessName.Equals("ReludualiConsole", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>The sample (Mycelium's, 2026-10-17) beside the Console, or in the repository when run from a build folder.</summary>
        public static string SamplePath() { return TheaterFile("splash.wav"); }

        /// <summary>A file of the theater launch: theater\NAME beside the Console (as installed), or tools\theater\NAME
        /// in the repository when it runs from a build folder; null when there is none.</summary>
        public static string TheaterFile(string name)
        {
            string here = AppDomain.CurrentDomain.BaseDirectory;
            string f = Path.Combine(here, "theater", name);
            if (File.Exists(f)) return f;
            DirectoryInfo d = new DirectoryInfo(here);
            for (int i = 0; i < 5 && d != null; i++, d = d.Parent)
            {
                f = Path.Combine(d.FullName, "tools", "theater", name);         // the repository's own layout
                if (File.Exists(f)) return f;
            }
            return null;
        }

        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    }

    /// <summary>
    /// The theater layer is in the game only while a theater launch runs (2026-10-17, his design: "If the USER starts
    /// Redacted on its own, There is no overwrite, but if the user starts Redacted from the Console, it starts with the
    /// Reluduali stuff", and "backup the original"). Place puts our stub (build_theater.ps1: the game takes only bytecode)
    /// at the one path the game takes it from, data\ui_mp\t6\ffotdmp.lua (sandbox 0531); a file already there is set aside
    /// in reluduali-backup, never deleted. Every step goes into reluduali-theater-placed.txt in the game folder as it is
    /// done, so a crash can always be undone: Remove takes ours out (only while it is still ours, by its SHA-256), puts the
    /// set-aside file back and takes out the folders it made; Tidy runs that at every Console start once the game is not
    /// running, and Watch runs it when the theater's game exits. Nothing is placed while the game runs, nor when the
    /// installer keeps the menus in the game (reluduali-theater.txt). The file is inert without the theater's flags
    /// anyway (sandbox 0534/0536/0550: a normal start is exactly as always).
    /// </summary>
    public static class TheaterLayer
    {
        public const string Rel = @"data\ui_mp\t6\ffotdmp.lua";
        public const string RecordName = "reluduali-theater-placed.txt";
        public const string InstalledNote = "reluduali-theater.txt";
        static DispatcherTimer _watch;

        /// <summary>Our stub: theater\ffotdmp.lua beside the Console (as installed), or release\test\theater-layer's build in
        /// the repository when the Console runs from a build folder; null without one, or when the file is not our bytecode.</summary>
        public static string Source()
        {
            string f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "theater", "ffotdmp.lua");
            if (!File.Exists(f))
            {
                f = null;
                DirectoryInfo d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                for (int i = 0; i < 5 && d != null && f == null; i++, d = d.Parent)
                {
                    string c = Path.Combine(d.FullName, "release", "test", "theater-layer", Rel);
                    if (File.Exists(c)) f = c;
                }
            }
            return f != null && IsStub(f) ? f : null;
        }

        /// <summary>Our bytecode, never text: the game takes a rawfile as bytecode, and text there hangs it at start (probe 2,
        /// sandbox 0529).</summary>
        public static bool IsStub(string f)
        {
            byte[] head = new byte[5];
            try { using (var s = File.OpenRead(f)) if (s.Read(head, 0, 5) < 5) return false; } catch { return false; }
            return head[0] == 0x1B && head[1] == (byte)'L' && head[2] == (byte)'u' && head[3] == (byte)'a' && head[4] == (byte)'Q';
        }

        /// <summary>The installer keeps the theater's menus in this game itself (its Theater-only launch, kept in).</summary>
        public static bool Installed(string game) { return game != null && File.Exists(Path.Combine(game, InstalledNote)); }

        /// <summary>Puts the layer in for a theater launch; a sentence for the log, and whether the theater's menus are in.</summary>
        public static string Place(string game, out bool menus) { return Place(game, out menus, Source()); }

        /// <summary>Place with a given stub (the self-test's).</summary>
        public static string Place(string game, out bool menus, string src)
        {
            menus = false;
            try
            {
                if (Installed(game)) { menus = true; return "the installer keeps the theater's menus in this game"; }
                if (File.Exists(Path.Combine(game, RecordName))) Remove(game);      // left by a crash: undone first
                if (src == null || !IsStub(src)) return "no theater layer beside the Designer (theater\\ffotdmp.lua): the game opens with its own menus";
                string target = Path.Combine(game, Rel), record = Path.Combine(game, RecordName);
                byte[] ours = File.ReadAllBytes(src);
                File.WriteAllText(record, "# the Reluduali theater layer, placed " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                                  + " by the Console for a theater launch: what it takes out again, last first\r\n");
                Action<string> note = l => File.AppendAllText(record, l + "\r\n");
                var missing = new Stack<string>();
                for (string d = Path.GetDirectoryName(target); !string.IsNullOrEmpty(d) && !Directory.Exists(d); d = Path.GetDirectoryName(d)) missing.Push(d);
                foreach (string d in missing) { Directory.CreateDirectory(d); note("dir\t" + d); }
                if (File.Exists(target))
                {
                    if (Sha(File.ReadAllBytes(target)) == Sha(ours)) { note("file\t" + target + "\t" + Sha(ours)); menus = true; return "the theater layer was in already (ours, from before): it comes out after this game"; }
                    string keep = Path.Combine(game, "reluduali-backup");
                    if (!Directory.Exists(keep)) { Directory.CreateDirectory(keep); note("dir\t" + keep); }
                    string aside = Path.Combine(keep, Rel.Replace('\\', '_') + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    note("moved\t" + target + "\t" + aside);
                    File.Move(target, aside);
                }
                // recorded first, then written whole or not at all (a temporary name, renamed): a write cut short never leaves
                // a partial file that Remove would not know as ours (the code review's C-23)
                note("file\t" + target + "\t" + Sha(ours));
                string part = target + ".part";
                try { File.WriteAllBytes(part, ours); File.Move(part, target); }
                catch { try { File.Delete(part); } catch { } throw; }
                menus = true;
                return "the theater layer in for this game (" + Rel + ")";
            }
            catch (Exception ex)
            {
                App.Note("--theater: putting the theater layer in", ex);
                try { Remove(game); } catch { }
                return "could not put the theater layer in (" + ex.Message + "): the game opens with its own menus";
            }
        }

        /// <summary>Takes back out what the record lists, last first; puts back what was set aside. A sentence for the log.</summary>
        public static string Remove(string game)
        {
            string record = game == null ? null : Path.Combine(game, RecordName);
            if (record == null || !File.Exists(record)) return "nothing of the theater's in the game";
            var lines = File.ReadAllLines(record).Where(l => l.Length > 0 && !l.StartsWith("#")).Reverse().ToList();
            int removed = 0, back = 0;
            foreach (string l in lines)
            {
                string[] f = l.Split('\t');
                try
                {
                    if (f[0] == "file" && File.Exists(f[1]))
                    {
                        if (f.Length < 3 || Sha(File.ReadAllBytes(f[1])) == f[2]) { File.Delete(f[1]); removed++; }
                        else App.Note("--theater: " + f[1] + " is not ours any more: left as it is", null);
                    }
                    else if (f[0] == "moved" && !File.Exists(f[1]) && File.Exists(f[2])) { File.Move(f[2], f[1]); back++; }
                    else if (f[0] == "dir" && Directory.Exists(f[1]) && !Directory.EnumerateFileSystemEntries(f[1]).Any()) Directory.Delete(f[1]);
                }
                catch (Exception ex) { App.Note("--theater: taking back " + l.Replace("\t", " "), ex); }
            }
            File.Delete(record);
            return "the theater layer taken out (" + removed + " file" + (back > 0 ? ", the original put back" : "") + ")";
        }

        /// <summary>At a Console start: what a crash left in is taken out once the game is not running (watched until then).</summary>
        public static void Tidy()
        {
            try
            {
                string game = Where.Game;
                if (game == null || !File.Exists(Path.Combine(game, RecordName))) return;
                if (Process.GetProcessesByName("t6mpv43").Length == 0) App.Note("--theater, at start: " + Remove(game), null);
                else Watch(game, null);
            }
            catch (Exception ex) { App.Note("--theater: tidying the theater layer", ex); }
        }

        /// <summary>Every 2 s: once the theater's game has gone (or never came within 60 s), the layer comes out.</summary>
        public static void Watch(string game, Action done)
        {
            if (_watch != null) _watch.Stop();
            var started = DateTime.UtcNow;
            bool seen = false;
            _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _watch.Tick += (s, e) =>
            {
                bool running = Process.GetProcessesByName("t6mpv43").Length > 0;
                if (running) { seen = true; return; }
                if (!seen && (DateTime.UtcNow - started).TotalSeconds < 60) return;
                _watch.Stop(); _watch = null;
                App.Note("--theater: the game has gone: " + Remove(game), null);
                if (done != null) done();
            };
            _watch.Start();
        }

        static string Sha(byte[] b) { using (var h = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(h.ComputeHash(b)).Replace("-", ""); }
    }

    /// <summary>
    /// The greeting: "Reluduali Black Ops 2" with the sample (2026-10-17), in the website's look since the same afternoon
    /// (reluduali.com's Animated Launch Screen Brief: the site's sky palettes, an old Mac's 8x8 ordered dither, the
    /// three-body chrome mark of board 47, RELUDUALI in his grown lettering, one holo sheen, timed to the sample's hit
    /// at 0.1 s). SplashPainter paints each frame at a third of the window; it is shown scaled up with hard pixels. Esc,
    /// Space, Enter or a click skips it. With Windows' animations off, the last frame stands and only the fade runs.
    /// The sample is 32-bit float (his file, unchanged): it is played as 16-bit PCM made in memory, through Windows' own
    /// PlaySound (SoundPlayer), which every edition of Windows has.
    /// </summary>
    public sealed class TheaterSplash : Window
    {
        public bool Skipped { get; private set; }
        /// <summary>When the sample has played out (UTC); MinValue when nothing plays.</summary>
        public static DateTime SampleEnds { get; private set; }
        static System.Media.SoundPlayer _player;    // kept: the sample rings out after the splash
        /// <summary>The site's night palette from 19:00 to 07:00 by this PC's clock (the brief's simple rule), the day's between.</summary>
        /// <summary>The sky the splash is painted under (the site's, by the Sun over this PC: Sky), taken once a launch.</summary>
        public static Sky Sky { get { if (_launchSky == null) _launchSky = ReludualiConsole.Sky.Now(); return _launchSky.Value; } }
        static Sky? _launchSky;

        readonly Image _image = new Image { Stretch = Stretch.Fill };
        readonly ScaleTransform _zoom = new ScaleTransform(1, 1);
        readonly Sky _sky = Sky;
        readonly bool _animate = SystemParameters.ClientAreaAnimation;
        SplashPainter _paint;
        System.Windows.Media.Imaging.WriteableBitmap _bmp;
        byte[] _px;
        Stopwatch _clock;
        string _status = "Starting the theater…";

        public TheaterSplash(Window over)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            ShowInTaskbar = over == null;
            Topmost = true;
            Title = "Reluduali Black Ops 2";
            Background = new SolidColorBrush(SplashPainter.Ground(_sky, 1));     // the ground's own colour, never a black frame first
            if (over != null) { Owner = over; Left = over.Left + 20; Top = over.Top + 20; }   // maximized on the Console's screen
            WindowStartupLocation = over != null ? WindowStartupLocation.Manual : WindowStartupLocation.CenterScreen;
            WindowState = WindowState.Maximized;
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
            _image.RenderTransformOrigin = new Point(0.5, 0.5);
            _image.RenderTransform = _zoom;
            Content = new Grid { Children = { _image } };

            KeyDown += (s, e) => { if (e.Key == Key.Escape || e.Key == Key.Space || e.Key == Key.Enter) Skipped = true; };
            MouseLeftButtonDown += (s, e) => Skipped = true;
            Loaded += (s, e) =>
            {
                int w = Math.Max(320, (int)(ActualWidth / 3)), h = Math.Max(180, (int)(ActualHeight / 3));
                _paint = new SplashPainter(w, h, _sky);
                _paint.SetStatus(_status);
                _bmp = new System.Windows.Media.Imaging.WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                _px = new byte[w * h * 4];
                _image.Source = _bmp;
                Play();
                _clock = Stopwatch.StartNew();
                if (_animate) CompositionTarget.Rendering += Frame;
                else Draw(SplashPainter.Settled);
            };
            Closed += (s, e) => CompositionTarget.Rendering -= Frame;
        }

        /// <summary>The one line under the greeting ("Starting the theater..."), from the launch.</summary>
        public string Status
        {
            set { _status = value; if (_paint != null) { _paint.SetStatus(value); if (!_animate) Draw(SplashPainter.Settled); } }
        }

        void Frame(object sender, EventArgs e) { Draw(_clock.Elapsed.TotalSeconds); }

        void Draw(double t)
        {
            _paint.Paint(t, _px);
            _bmp.WritePixels(new Int32Rect(0, 0, _paint.W, _paint.H), _px, _paint.W * 4, 0);
        }

        /// <summary>Out like a card closing on the site: a little smaller toward the centre as it fades (only the fade without animations).</summary>
        public void FadeOut(Action done)
        {
            var d = TimeSpan.FromSeconds(0.38);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            var a = new DoubleAnimation(1, 0, d) { EasingFunction = ease };
            a.Completed += (s, e) => { Close(); if (done != null) done(); };
            if (_animate)
            {
                _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.94, d) { EasingFunction = ease });
                _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.94, d) { EasingFunction = ease });
            }
            BeginAnimation(OpacityProperty, a);
        }

        static void Play()
        {
            string f = TheaterLaunch.SamplePath();
            if (f == null) { App.Note("--theater: no splash.wav beside the Designer: a silent splash", null); return; }
            try
            {
                byte[] pcm = Pcm16(File.ReadAllBytes(f));
                _player = new System.Media.SoundPlayer(new MemoryStream(pcm));
                _player.Play();
                int bytesPerSecond = BitConverter.ToInt32(pcm, 28);     // the header Pcm16 writes (or the file's own, when it was PCM already)
                SampleEnds = DateTime.UtcNow.AddSeconds(bytesPerSecond > 0 ? (pcm.Length - 44) / (double)bytesPerSecond + 0.5 : 0);
            }
            catch (Exception ex) { App.Note("--theater: the sample", ex); }
        }

        /// <summary>A RIFF WAVE as 16-bit PCM: 32-bit float (format 3, or 0xFFFE with a float subformat) converted, PCM as it is.</summary>
        public static byte[] Pcm16(byte[] wav)
        {
            int channels = 0, rate = 0, bits = 0, format = 0, dataAt = -1, dataLen = 0;
            for (int i = 12; i + 8 <= wav.Length;)
            {
                string id = Encoding.ASCII.GetString(wav, i, 4);
                int size = BitConverter.ToInt32(wav, i + 4);
                if (id == "fmt ")
                {
                    format = BitConverter.ToUInt16(wav, i + 8); channels = BitConverter.ToUInt16(wav, i + 10);
                    rate = BitConverter.ToInt32(wav, i + 12); bits = BitConverter.ToUInt16(wav, i + 22);
                    if (format == 0xFFFE && size >= 40) format = BitConverter.ToUInt16(wav, i + 32);     // WAVE_FORMAT_EXTENSIBLE: the subformat's tag
                }
                else if (id == "data") { dataAt = i + 8; dataLen = Math.Min(size, wav.Length - dataAt); break; }
                i += 8 + size + (size & 1);
            }
            if (dataAt < 0 || channels == 0) throw new InvalidDataException("not a WAVE file the splash can play");
            if (format == 1 && bits == 16) return wav;
            if (format != 3 || bits != 32) throw new InvalidDataException("the splash plays 16-bit PCM or 32-bit float WAVE (this one: format " + format + ", " + bits + " bits)");
            int samples = dataLen / 4;
            var o = new MemoryStream(44 + samples * 2);
            var w = new BinaryWriter(o);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + samples * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(samples * 2);
            for (int k = 0; k < samples; k++)
            {
                double v = BitConverter.ToSingle(wav, dataAt + k * 4) * 32767.0;
                w.Write((short)Math.Max(-32768, Math.Min(32767, Math.Round(v))));
            }
            w.Flush();
            return o.ToArray();
        }
    }

    /// <summary>
    /// The splash's frames, painted in software at a third of the window (reluduali.com's Animated Launch Screen Brief,
    /// 2026-10-17). Every frame: the ground (the site's sky gradient, day or night), the mark (board 47: three chrome bodies
    /// on three tilted planes fusing and parting, ported as it is from the site's logo.mjs), RELUDUALI in his grown lettering
    /// (reluduali.svg: letters and seeds), BLACK OPS 2 in spaced monospace capitals, one rainbow sheen across the name, and
    /// a status line; then every channel goes to 8 levels through an 8x8 ordered (Bayer) dither, as an old Mac's screen.
    /// The storyboard (seconds from the sample's start; its hit lands at 0.1): the fused drop bursts into three on a
    /// spring at 0.1, the name grows left to right 0.15-1.10 and its seeds pop in last, BLACK OPS 2 fades in 0.7-1.6, the
    /// sheen crosses 1.3-1.9, then it holds with the mark breathing (an 8 s cycle) and the status line.
    /// </summary>
    public sealed class SplashPainter
    {
        public readonly int W, H;
        public readonly Sky Sky;
        /// <summary>A moment after everything has come in: the still frame when animations are off.</summary>
        public const double Settled = 2.4;

        readonly float[] _top, _bottom, _ink, _seed;
        static readonly float[] Maroon = Rgb(0x5C, 0x0F, 0x2E);
        static readonly float[][] Ramp = { Rgb(0x0B, 0x2A, 0x4A), Rgb(0x46, 0x6E, 0x96), Rgb(0x9F, 0xC2, 0xE0), Rgb(0xE2, 0xF0, 0xFC), Rgb(0xFF, 0xFF, 0xFF) };
        static readonly float[][] Rainbow = { Rgb(0xFF, 0x4F, 0xAE), Rgb(0xFF, 0xD2, 0x4F), Rgb(0x4F, 0xFF, 0xC0), Rgb(0x4F, 0xB4, 0xFF), Rgb(0xB4, 0x4F, 0xFF) };
        static readonly int[] B = { 0, 32, 8, 40, 2, 34, 10, 42, 48, 16, 56, 24, 50, 18, 58, 26, 12, 44, 4, 36, 14, 46, 6, 38, 60, 28, 52, 20, 62, 30, 54, 22,
                                    3, 35, 11, 43, 1, 33, 9, 41, 51, 19, 59, 27, 49, 17, 57, 25, 15, 47, 7, 39, 13, 45, 5, 37, 63, 31, 55, 23, 61, 29, 53, 21 };

        readonly byte[] _name, _seeds, _ops;      // coverage, 0-255, per canvas pixel
        byte[] _status;
        readonly double _markX, _markY, _markSize;
        public readonly Int32Rect NameBox, OpsBox;

        static float[] Rgb(int r, int g, int b) { return new[] { r / 255f, g / 255f, b / 255f }; }

        static float[] Mix(float[] a, float[] b, double t) { float u = (float)t; return new[] { a[0] + (b[0] - a[0]) * u, a[1] + (b[1] - a[1]) * u, a[2] + (b[2] - a[2]) * u }; }

        /// <summary>
        /// The colours for a sky: the site's day (#7fb0dd down to #eaf6ff, navy ink, pink seeds) and night (#e2873c down to
        /// #e7d3b8, dark maroon ink, orange seeds), blended by how much it is night; then, near sunrise and sunset, warmed
        /// toward a coral top (#f08a5d) and a gold foot (#ffd9a0).
        /// </summary>
        public static void Palette(Sky sky, out float[] top, out float[] bottom, out float[] ink, out float[] seed)
        {
            top = Mix(Mix(Rgb(0x7F, 0xB0, 0xDD), Rgb(0xE2, 0x87, 0x3C), sky.Night), Rgb(0xF0, 0x8A, 0x5D), 0.55 * sky.Warm);
            bottom = Mix(Mix(Rgb(0xEA, 0xF6, 0xFF), Rgb(0xE7, 0xD3, 0xB8), sky.Night), Rgb(0xFF, 0xD9, 0xA0), 0.45 * sky.Warm);
            ink = Mix(Rgb(0x0B, 0x2A, 0x4A), Rgb(0x3A, 0x07, 0x16), sky.Night);
            seed = Mix(Rgb(0xFF, 0x4F, 0xAE), Rgb(0xE2, 0x87, 0x3C), sky.Night);
        }

        /// <summary>The ground's colour at a height (0 top, 1 bottom) for a sky.</summary>
        public static Color Ground(Sky sky, double f)
        {
            float[] a, b, ink, seed;
            Palette(sky, out a, out b, out ink, out seed);
            return Color.FromRgb((byte)(255 * (a[0] + (b[0] - a[0]) * f)), (byte)(255 * (a[1] + (b[1] - a[1]) * f)), (byte)(255 * (a[2] + (b[2] - a[2]) * f)));
        }

        public SplashPainter(int w, int h, Sky sky)
        {
            W = w; H = h; Sky = sky;
            Palette(sky, out _top, out _bottom, out _ink, out _seed);

            // the layout: the mark above, the name across the middle, BLACK OPS 2 under it, the status line low
            _markSize = h * 0.30; _markX = w / 2.0; _markY = h * 0.33;
            double nw = Math.Min(w * 0.56, h * 0.17 * 122.33 / 22.83), nh = nw * 22.83 / 122.33;
            double nx = (w - nw) / 2, ny = h * 0.55;
            NameBox = new Int32Rect((int)nx, (int)ny, (int)Math.Ceiling(nw), (int)Math.Ceiling(nh));
            Geometry letters, seeds;
            Lettering(out letters, out seeds);
            var place = new TransformGroup();
            place.Children.Add(new ScaleTransform(nw / 122.33, nw / 122.33));
            place.Children.Add(new TranslateTransform(nx + 0.60 * nw / 122.33, ny));
            if (letters != null)
            {
                letters.Transform = place; seeds.Transform = place;
                _name = Mask(dc => dc.DrawGeometry(Brushes.White, null, letters));
                _seeds = Mask(dc => dc.DrawGeometry(Brushes.White, null, seeds));
            }
            else
            {
                // no lettering file: the name in plain capitals, so the splash still says it
                _name = Mask(dc => Words(dc, "RELUDUALI", "Segoe UI Light", FontWeights.Light, nh * 0.95, 0.05, ny - nh * 0.1));
                _seeds = new byte[w * h];
            }
            double ops = Math.Max(9, h * 0.042), oy = ny + nh + h * 0.045;
            _ops = Mask(dc => Words(dc, "BLACK OPS 2", "Consolas", FontWeights.SemiBold, ops, 0.16, oy));
            OpsBox = new Int32Rect(0, (int)oy, w, (int)Math.Ceiling(ops * 1.4));
            _status = new byte[w * h];
        }

        /// <summary>The status line, in small monospace near the bottom.</summary>
        public void SetStatus(string text)
        {
            // solid and bold (sandbox 0563: at 80% through the dither the small type read "Opeaing the theatar")
            double size = Math.Max(9, H * 0.034);
            _status = string.IsNullOrEmpty(text) ? new byte[W * H] : Mask(dc => Words(dc, text, "Consolas", FontWeights.Bold, size, 0.06, H * 0.86));
        }

        /// <summary>RELUDUALI from theater\reluduali.svg: its paths are the letters, its circles the seeds (null without the file).</summary>
        internal static void Lettering(out Geometry letters, out Geometry seeds)
        {
            letters = seeds = null;
            try
            {
                string f = TheaterLaunch.TheaterFile("reluduali.svg");
                if (f == null) return;
                string svg = File.ReadAllText(f);
                var l = new GeometryGroup { FillRule = FillRule.Nonzero };
                var s = new GeometryGroup { FillRule = FillRule.Nonzero };
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(svg, "<path[^>]*\\sd=\"([^\"]+)\""))
                    l.Children.Add(Geometry.Parse(m.Groups[1].Value));
                var num = System.Globalization.CultureInfo.InvariantCulture;
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(svg, "<circle[^>]*cx=\"([-0-9.]+)\"[^>]*cy=\"([-0-9.]+)\"[^>]*r=\"([0-9.]+)\""))
                {
                    double r = double.Parse(m.Groups[3].Value, num);
                    s.Children.Add(new EllipseGeometry(new Point(double.Parse(m.Groups[1].Value, num), double.Parse(m.Groups[2].Value, num)), r, r));
                }
                if (l.Children.Count == 0) return;
                letters = l; seeds = s;
            }
            catch (Exception ex) { App.Note("--theater: the lettering", ex); letters = seeds = null; }
        }

        /// <summary>Capitals spaced by a share of their size, centred across the canvas at a height.</summary>
        void Words(DrawingContext dc, string text, string font, FontWeight weight, double size, double spacing, double top)
        {
            var face = new Typeface(new FontFamily(font), FontStyles.Normal, weight, FontStretches.Normal);
            var parts = new List<FormattedText>();
            double width = 0;
            foreach (char c in text)
            {
#pragma warning disable 618
                var ft = new FormattedText(c.ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, Brushes.White);
#pragma warning restore 618
                parts.Add(ft);
                width += ft.WidthIncludingTrailingWhitespace + size * spacing;
            }
            width -= size * spacing;
            double x = (W - width) / 2;
            foreach (FormattedText ft in parts) { dc.DrawText(ft, new Point(x, top)); x += ft.WidthIncludingTrailingWhitespace + size * spacing; }
        }

        /// <summary>What a drawing covers, 0-255 per canvas pixel (drawn white on nothing, at one canvas pixel per unit).</summary>
        byte[] Mask(Action<DrawingContext> draw)
        {
            var v = new DrawingVisual();
            using (DrawingContext dc = v.RenderOpen()) draw(dc);
            var rt = new System.Windows.Media.Imaging.RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            rt.Render(v);
            var px = new byte[W * H * 4];
            rt.CopyPixels(px, W * 4, 0);
            var a = new byte[W * H];
            for (int i = 0; i < a.Length; i++) a[i] = px[i * 4 + 3];
            return a;
        }

        /// <summary>One frame at t seconds from the sample's start, into BGRA (W x H x 4).</summary>
        public void Paint(double t, byte[] bgra) { Frame(t, bgra, true); }

        /// <summary>The same frame without the mark (the self-test measures the mark by the difference).</summary>
        public void PaintWithoutMark(double t, byte[] bgra) { Frame(t, bgra, false); }

        void Frame(double t, byte[] bgra, bool withMark)
        {
            // the mark: a fused drop at 0.6 until the hit, then a spring to full size (a slight overshoot, settled by about
            // 0.4 s) while the three bodies part; then it breathes, apart to fused and back every 8 s
            double s = t - 0.1, scale, breath;
            if (s < 0) { scale = 0.6; breath = 0; }
            else
            {
                scale = 1 - 0.4 * Math.Exp(-9 * s) * Math.Cos(14 * s);
                breath = s < 0.35 ? Math.Sin(s / 0.35 * Math.PI / 2) : 0.5 + 0.5 * Math.Cos((s - 0.35) / 8 * Math.PI * 2);
            }
            double spread = 0.08 + 0.22 * breath, r2 = 0.034 + 0.02 * (1 - breath), spin = t * 0.9;
            var bx = new double[3]; var by = new double[3];
            for (int k = 0; k < 3; k++)
            {
                double a = spin + k * Math.PI * 2 / 3, tilt = 0.25 + k * 0.12;
                bx[k] = Math.Cos(a) * spread; by[k] = Math.Sin(a) * spread * Math.Cos(tilt);
            }
            double size = _markSize * scale;
            int mx0 = Math.Max(0, (int)(_markX - size / 2)), mx1 = Math.Min(W, (int)(_markX + size / 2) + 1);
            int my0 = Math.Max(0, (int)(_markY - size / 2)), my1 = Math.Min(H, (int)(_markY + size / 2) + 1);

            double reveal = Clamp((t - 0.15) / 0.95), seedsIn = Clamp((t - 1.0) / 0.15), ops = Clamp((t - 0.7) / 0.9), status = Clamp((t - 1.9) / 0.3);
            double sheen = t >= 1.3 && t <= 1.9 ? -0.25 + 1.5 * (t - 1.3) / 0.6 : double.NaN;
            float opsK = (float)ops, statusK = (float)status;
            // the name's edge: nothing before the hit, all of it by 1.1 s, dithered where it is
            double edge = reveal * 1.1 - 0.05;
            double nameX = NameBox.X, nameW = Math.Max(1, NameBox.Width), nameY = NameBox.Y, nameH = Math.Max(1, NameBox.Height);
            for (int y = 0; y < H; y++)
            {
                float f = H > 1 ? y / (float)(H - 1) : 0;
                float gr = _top[0] + (_bottom[0] - _top[0]) * f, gg = _top[1] + (_bottom[1] - _top[1]) * f, gb = _top[2] + (_bottom[2] - _top[2]) * f;
                bool markRow = withMark && y >= my0 && y < my1;
                int row = y * W;
                for (int x = 0; x < W; x++)
                {
                    int i = row + x;
                    float d = Dither[((y & 7) << 3) | (x & 7)];
                    float r = gr, g = gg, b = gb;

                    if (markRow && x >= mx0 && x < mx1)
                    {
                        double px = (x + 0.5 - _markX) / size, py = (y + 0.5 - _markY) / size, fl = 0, gx = 0, gy = 0;
                        for (int k = 0; k < 3; k++)
                        {
                            double dx = px - bx[k], dy = py - by[k], d2 = dx * dx + dy * dy + 1e-4, v = r2 / d2;
                            fl += v; gx += -2 * v * dx / d2; gy += -2 * v * dy / d2;
                        }
                        if (fl > 1)
                        {
                            // chrome: the sky above, the ground below, a dark horizon band where the surface turns
                            double n = Math.Sqrt(gx * gx + gy * gy); if (n == 0) n = 1;
                            double shade = Clamp(0.55 + 0.45 * (-gy / n) - 0.18 * Math.Abs(gx / n) - 0.25 / fl);
                            float[] m = Ramp[Math.Min(Ramp.Length - 1, (int)(shade * (Ramp.Length - 1) + d))];
                            r = m[0]; g = m[1]; b = m[2];
                        }
                        else if (breath > 0 && px * px + py * py < 0.0036 * breath * breath && fl < 0.6) { r = Maroon[0]; g = Maroon[1]; b = Maroon[2]; }
                    }

                    byte nm = _name[i];
                    if (nm != 0)
                    {
                        double u = (x - nameX) / nameW;
                        if (u < edge + (d - 0.5) * 0.08 && nm > d * 255)
                        {
                            r = _ink[0]; g = _ink[1]; b = _ink[2];
                            if (!double.IsNaN(sheen))
                            {
                                // the sheen: a rainbow band leaning across the name, its edges dithered
                                double across = (u + (y - nameY) / nameH * 0.3 - sheen) / 0.16, a = Math.Abs(across);
                                if (a < 1 && 1 - a > d * 0.9)
                                {
                                    double q = (across + 1) / 2 * (Rainbow.Length - 1);
                                    int lo = Math.Min(Rainbow.Length - 2, (int)q); float w = (float)(q - lo);
                                    float[] c0 = Rainbow[lo], c1 = Rainbow[lo + 1];
                                    r = c0[0] + (c1[0] - c0[0]) * w; g = c0[1] + (c1[1] - c0[1]) * w; b = c0[2] + (c1[2] - c0[2]) * w;
                                }
                            }
                        }
                    }
                    if (seedsIn > 0 && _seeds[i] > d * 255 && (x - nameX) / nameW < seedsIn * 1.05) { r = _seed[0]; g = _seed[1]; b = _seed[2]; }   // they pop in left to right, last
                    if (opsK > 0 && _ops[i] * opsK > d * 255) { r = _ink[0]; g = _ink[1]; b = _ink[2]; }
                    if (statusK > 0 && _status[i] * statusK > d * 255) { r = _ink[0]; g = _ink[1]; b = _ink[2]; }

                    // the old Mac: 8 levels a channel, the threshold from the 8x8 table
                    int o = i * 4;
                    bgra[o] = Levels[Level8(b, d)]; bgra[o + 1] = Levels[Level8(g, d)]; bgra[o + 2] = Levels[Level8(r, d)]; bgra[o + 3] = 255;
                }
            }
        }

        static readonly float[] Dither = MakeDither();
        static float[] MakeDither() { var a = new float[64]; for (int i = 0; i < 64; i++) a[i] = (B[i] + 0.5f) / 64f; return a; }
        /// <summary>The eight levels a channel can take: 0, 36, 72, 109, 145, 182, 218, 255.</summary>
        public static readonly byte[] Levels = { 0, 36, 72, 109, 145, 182, 218, 255 };
        static int Level8(float v, float d) { int q = (int)(v * 7 + d); return q < 0 ? 0 : q > 7 ? 7 : q; }
        static double Clamp(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
    }

    /// <summary>
    /// Redacted loses every second "+command" on t6mpv43's command line, whatever its name: the 2nd, the 4th, the 6th...
    /// (sandbox 0561/0562, measured by the main session: of ten "+set", the even ones never became dvars, cg_drawFPS among
    /// them; with "+wait" between them all of them did, and the game started as usual). So every start the Console makes
    /// - the theater's and the ordinary Start Redacted - goes through Join: the arguments again, with a "+wait" between
    /// every two +commands (and none of the person's own bare "+wait", which would put the count out of step again).
    /// </summary>
    public static class GameCommandLine
    {
        public static string Join(string args)
        {
            var before = new List<string>();                  // what comes before the first +command (e.g. -windowed)
            var commands = new List<List<string>>();
            foreach (string t in Tokens(args ?? ""))
            {
                if (t.Length > 1 && t[0] == '+')
                {
                    commands.Add(new List<string> { t });
                    continue;
                }
                if (commands.Count == 0) before.Add(t); else commands[commands.Count - 1].Add(t);
            }
            commands.RemoveAll(c => c.Count == 1 && string.Equals(c[0], "+wait", StringComparison.OrdinalIgnoreCase));
            var parts = new List<string>(before);
            for (int i = 0; i < commands.Count; i++)
            {
                if (i > 0) parts.Add("+wait");
                parts.AddRange(commands[i]);
            }
            return string.Join(" ", parts);
        }

        /// <summary>Words split on spaces, a "quoted part" kept whole with its quotes.</summary>
        static IEnumerable<string> Tokens(string s)
        {
            var b = new StringBuilder();
            bool quoted = false;
            foreach (char c in s)
            {
                if (c == '"') quoted = !quoted;
                if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (b.Length > 0) { yield return b.ToString(); b.Clear(); }
                    continue;
                }
                b.Append(c);
            }
            if (b.Length > 0) yield return b.ToString();
        }
    }

    /// <summary>
    /// The sky over this PC, as the website's is (reluduali.com's sky.mjs, 2026-10-17; asked for the launch screen the same
    /// day: "Could we make it so that the time of day changes the launchscreen?" and the warm tint around sunrise and
    /// sunset: "Want it"). Where the PC is comes from its clock, never by asking: the time zone's standard offset gives a
    /// longitude, its band a rough latitude. The Sun's height comes from the almanac's formulae (the site's almanac.mjs).
    /// Night: 0 with the Sun above 0.5 degrees, 1 once it is 6 degrees below, a blend between (civil twilight), as the site.
    /// Warm: the tint near the horizon, strongest just as the Sun crosses it and gone 4 degrees either side - about a
    /// half hour around sunrise and around sunset where the Console runs.
    /// </summary>
    public struct Sky
    {
        public readonly double Night, Warm;
        public Sky(double night, double warm) { Night = Clamp(night); Warm = Clamp(warm); }

        public static Sky Now()
        {
            double lat, lon;
            Place(out lat, out lon);
            return At(DateTime.UtcNow, lat, lon);
        }

        public static Sky At(DateTime utc, double lat, double lon) { return FromAltitude(SunAltitude(utc, lat, lon)); }

        public static Sky FromAltitude(double alt)
        {
            double d = (alt + 0.8) / 4;                                // the horizon, refraction and all, at -0.8
            return new Sky((0.5 - alt) / 6.5, Math.Abs(d) < 1 ? 0.5 + 0.5 * Math.Cos(d * Math.PI) : 0);
        }

        /// <summary>Roughly where this PC is, from its clock: the standard offset's longitude, a latitude by its band.</summary>
        public static void Place(out double lat, out double lon)
        {
            double off = TimeZoneInfo.Local.BaseUtcOffset.TotalHours;
            lon = Math.Max(-180, Math.Min(180, off * 15));
            lat = off <= -2.5 ? 40 : off <= 3 ? 50 : off <= 9 ? 30 : off <= 11 ? -32 : -40;    // the Americas, Europe/Africa, Asia, Australia, New Zealand
        }

        /// <summary>The Sun's height above the horizon, degrees, at a moment (UTC) and a place.</summary>
        public static double SunAltitude(DateTime utc, double lat, double lon)
        {
            const double R = Math.PI / 180;
            double n = (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;
            double g = 357.528 + 0.9856003 * n;
            double l = 280.460 + 0.9856474 * n + 1.915 * Math.Sin(g * R) + 0.020 * Math.Sin(2 * g * R);   // ecliptic longitude
            double eps = 23.439 - 0.0000004 * n;
            double ra = Math.Atan2(Math.Cos(eps * R) * Math.Sin(l * R), Math.Cos(l * R)) / R;
            double dec = Math.Asin(Math.Sin(eps * R) * Math.Sin(l * R)) / R;
            double gmst = (280.46061837 + 360.98564736629 * n) % 360;
            double h = (gmst + lon - ra) * R;
            return Math.Asin(Math.Sin(lat * R) * Math.Sin(dec * R) + Math.Cos(lat * R) * Math.Cos(dec * R) * Math.Cos(h)) / R;
        }

        static double Clamp(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
    }
}
