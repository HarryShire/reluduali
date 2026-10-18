using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>
    /// Times the Console's two kinds of contest time and checks in with reluduali.com (the contract in TimerLink).
    /// Decided here, once a second, because only the Console knows:
    ///   queued recording - a batch that really records is running and the game's clock moved in the last 15 s;
    ///   in-game work     - otherwise, the game is the window in front and there was input within the contest's idle minutes.
    /// Never both at once. A jump in the clock of more than 5 s (a sleeping PC) counts as nothing.
    /// </summary>
    public sealed class ContestTimer
    {
        public static readonly ContestTimer Shared = new ContestTimer();

        public string State = "Not linked.";
        public string Now = "";
        public string LastBeat = "";
        public TimerLink.Pass Pass;
        public TimerLink Link;
        public readonly Ledger Ledger = new Ledger();
        public event Action Changed = () => { };
        public bool Running { get { return _thread != null && _thread.IsAlive; } }

        TimerLink.Window _win;
        Thread _thread;
        volatile bool _stop;
        int _lastTick = -1;
        long _lastProgress;

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize, dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO i);

        static long IdleMs()
        {
            var i = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref i)) return 0;
            return unchecked((uint)Environment.TickCount - i.dwTime);
        }

        static string ForegroundExe()
        {
            try
            {
                uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                return Process.GetProcessById((int)pid).ProcessName + ".exe";
            }
            catch { return ""; }
        }

        public void Note(string s) { App.Note("CONTEST: " + s, null); }

        /// <summary>Links with the code the Lumit panel shows (the Contest page's Link..., and the pipe's "console contest link
        /// CODE" for the two-seat test, 2026-10-17), then times. A sentence for the person back.</summary>
        public string LinkWith(string code)
        {
            string said;
            try
            {
                Stop(true);
                if (Link != null) Link.Dispose();
                Link = new TimerLink(TimerLink.ConfiguredServer()) { Log = Note };
                Pass = Link.Link(code);
                said = "Linked as " + Pass.Username + " in " + Pass.Contest + ".";
                State = said;
                Start();
            }
            catch (Refused r)
            {
                said = r.Status == 404 ? "That code is mistyped, already used, or over ten minutes old. Get a new one in the panel."
                     : r.Status == 429 ? "Too many wrong codes from here: wait 15 minutes."
                     : "Not linked: " + r.Status + " " + r.Message;
                State = said;
            }
            catch (Exception ex) { said = "Not linked: reluduali.com did not answer (" + ex.GetBaseException().Message + ")."; State = said; }
            Changed();
            return said;
        }

        /// <summary>Starts timing if this PC holds a pass that has not ended.</summary>
        public void StartIfLinked()
        {
            Pass = TimerLink.LoadPass();
            if (Pass == null) { State = "Not linked. Link with the code the Lumit panel shows (Record, Link the Console)."; Changed(); return; }
            if (Pass.Expired) { State = "The pass ended (the contest's hand-in passed). Link again for the next contest."; Changed(); return; }
            Start();
        }

        public void Start()
        {
            if (Running || Pass == null) return;
            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "contest timer", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Stop(bool endWindow)
        {
            _stop = true;
            if (_thread != null) _thread.Join(3000);
            if (endWindow && _win != null && Link != null && Pass != null)
            {
                try { Link.End(Pass, _win); } catch (Exception ex) { Note("ending the window: " + ex.Message); }
                _win = null;
            }
            State = Pass != null ? "Linked as " + Pass.Username + " in " + Pass.Contest + ". Not timing." : "Not linked.";
            Changed();
        }

        void Loop()
        {
            try
            {
                if (Link == null || Link.Server.ToString() != new Uri(Pass.Server ?? TimerLink.ConfiguredServer()).GetLeftPart(UriPartial.Authority) + "/")
                {
                    if (Link != null) Link.Dispose();
                    Link = new TimerLink(Pass.Server ?? TimerLink.ConfiguredServer()) { Log = Note };
                }
                if (!OpenWindow()) return;
                var clock = Stopwatch.StartNew();
                long last = 0, nextBeat = 60000, nextStats = 0;
                while (!_stop)
                {
                    Thread.Sleep(1000);
                    long now = clock.ElapsedMilliseconds, delta = now - last;
                    last = now;
                    if (now >= nextStats) { Progress(now); nextStats = now + 2000; }
                    TimedCategory c = Decide(now);
                    Now = c == null ? "nothing to time" : c.Name + " (" + c.Multiplier.ToString("0.##", CultureInfo.InvariantCulture) + "x)";
                    if (c != null && delta > 0 && delta < 5000) Ledger.Add(c, delta);
                    if (now >= nextBeat) { nextBeat = now + 60000; if (!CheckIn()) return; }
                    Changed();
                }
            }
            catch (Exception ex) { State = "Timing stopped: " + ex.Message; Note("timer loop: " + ex); Changed(); }
        }

        bool OpenWindow()
        {
            for (int attempt = 0; !_stop; attempt++)
            {
                try
                {
                    _win = Link.Open(Pass);
                    Ledger.Categories = _win.Categories;
                    Ledger.DropUnsent();
                    State = "Timing for " + Pass.Username + " in " + Pass.Contest + ": " +
                            string.Join(", ", _win.Categories.Select(k => k.Name + " " + k.Multiplier.ToString("0.##", CultureInfo.InvariantCulture) + "x")) + ".";
                    Changed();
                    return true;
                }
                catch (Refused r)
                {
                    if (r.Status == 401) { State = "reluduali.com no longer takes this pass (unlinked, or you left the contest). Link again."; Changed(); return false; }
                    if (r.Status == 409)
                    {
                        // Sunday's server (reluduali.com, 2026-10-16) refuses a window before the contest opens ("the contest has not
                        // opened yet") and after it closes ("the deadline has passed"): its own sentence is shown as it comes
                        string said = (r.Message ?? "").Trim().TrimEnd('.');
                        if (said.Length > 0) said = char.ToUpperInvariant(said[0]) + said.Substring(1) + ".";
                        if (said.IndexOf("not opened", StringComparison.OrdinalIgnoreCase) >= 0) { State = said + " The Designer asks again in a minute."; Changed(); }
                        else { State = said.Length > 0 ? said : "This contest has nothing for the Designer to time."; Changed(); return false; }
                    }
                    else
                    State = "Could not open a window (" + r.Status + " " + r.Message + "); trying again."; Changed();
                }
                catch (Exception ex) { State = "reluduali.com did not answer (" + ex.GetBaseException().Message + "); trying again."; Changed(); }
                for (int i = 0; i < Math.Min(60, 5 << Math.Min(attempt, 4)) && !_stop; i++) Thread.Sleep(1000);
            }
            return false;
        }

        /// <summary>The game's clock moved: progress for "queued" (a frozen game or a sleeping PC stops counting after 15 s).</summary>
        void Progress(long now)
        {
            if (!BatchEngine.Recording) { _lastTick = -1; return; }
            string s = null;
            try { s = BridgeClient.Ask("stats", 400, 1); } catch { }
            if (s == null) { _lastProgress = now; return; }     // no bridge: the batch running is all we can see
            int tick = (int)BridgeClient.Field(s, "tick");
            if (tick != _lastTick) { _lastTick = tick; _lastProgress = now; }
        }

        TimedCategory Decide(long now)
        {
            if (_win == null) return null;
            if (BatchEngine.Recording)
            {
                if (now - _lastProgress > 15000) return null;
                return _win.Categories.FirstOrDefault(k => k.Kind == "queued");
            }
            var inGame = _win.Categories.FirstOrDefault(k => k.Kind == "in-game");
            if (inGame == null) return null;
            string fg = ForegroundExe();
            var procs = inGame.Processes.Length > 0 ? inGame.Processes : new[] { "t6mpv43.exe" };
            if (!procs.Any(p => p.Equals(fg, StringComparison.OrdinalIgnoreCase))) return null;
            if (IdleMs() > _win.IdleMinutes * 60000L) return null;
            return inGame;
        }

        /// <summary>One check-in and what each answer means (the contract's table). False: stop timing.</summary>
        bool CheckIn()
        {
            List<Segment> segs = Ledger.Next();
            try
            {
                bool gap;
                long charged = Link.Beat(Pass, _win, segs, out gap);
                Ledger.Done(segs);
                LastBeat = DateTime.Now.ToString("HH:mm:ss") + ": accepted" + (segs.Count > 0 ? " (" + string.Join(", ", segs.Select(s => s.Category + " " + s.InFrontMs / 1000 + " s")) + ")" : " (nothing new)") +
                           (gap ? "; the server saw a gap before it, so this one charged nothing" : "");
                return true;
            }
            catch (Refused r)
            {
                string msg = (r.Message ?? "").ToLowerInvariant();
                LastBeat = DateTime.Now.ToString("HH:mm:ss") + ": refused " + r.Status + " " + r.Message;
                switch (r.Status)
                {
                    case 400: Ledger.Done(segs); Note("BUG, refused 400: " + r.Message); return true;
                    case 401: State = "reluduali.com no longer takes this pass. Link again."; return false;
                    case 404: return OpenWindow();
                    case 409:
                        if (msg.Contains("ended")) { State = "Another Designer took over timing for this person and contest."; return false; }
                        return OpenWindow();
                    case 422: Ledger.Done(segs); return true;     // counted as sent, uncharged
                    default: return true;                         // kept: sent again, added to, next minute
                }
            }
            catch (Exception ex) { LastBeat = DateTime.Now.ToString("HH:mm:ss") + ": no answer (" + ex.GetBaseException().Message + "); sent again next minute"; return true; }
        }
    }

    /// <summary>
    /// The Lumit folder (contract section 5): Lumit's panel watches one folder, top level only, and imports a file once its
    /// size stops changing. Each finished take's videos are copied into a hidden ".reluduali-incoming" folder inside it
    /// (Lumit does not look into subfolders), then moved up in one step, so Lumit never sees half a file.
    /// </summary>
    public static class LumitFolder
    {
        static string Setting { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reluduali", "Console", "lumit-folder.txt"); } }
        static readonly string[] Video = { ".avi", ".mov", ".mp4", ".mkv" };
        public static Action<string> Said = s => { };

        public static string Path_
        {
            get { try { return File.Exists(Setting) ? File.ReadAllText(Setting).Trim() : ""; } catch { return ""; } }
            set { Directory.CreateDirectory(Path.GetDirectoryName(Setting)); File.WriteAllText(Setting, value ?? ""); }
        }

        public static void Hook() { BatchEngine.TakeRecorded += (dir, began) => ThreadPool.QueueUserWorkItem(_ => Deliver(dir, began)); }

        public static void Deliver(string takeDir, DateTime began)
        {
            string watch = Path_;
            if (string.IsNullOrEmpty(watch) || !Directory.Exists(watch) || !Directory.Exists(takeDir)) return;
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                string incoming = Path.Combine(watch, ".reluduali-incoming");
                var di = Directory.CreateDirectory(incoming);
                di.Attributes |= FileAttributes.Hidden;
                foreach (string f in Directory.GetFiles(takeDir).Where(f => Video.Contains(Path.GetExtension(f).ToLowerInvariant()) && File.GetCreationTime(f) >= began.AddSeconds(-1)))   // made by this take: the frag's earlier takes in the same folder were made before it began (the code review's B-12)
                {
                    string name = Path.GetFileName(f), final = Path.Combine(watch, name);
                    for (int n = 2; File.Exists(final); n++) final = Path.Combine(watch, Path.GetFileNameWithoutExtension(name) + " (" + n + ")" + Path.GetExtension(name));
                    string temp = Path.Combine(incoming, Path.GetFileName(final) + ".part");
                    File.Copy(f, temp, true);
                    File.Move(temp, final);
                    Said("Lumit folder: " + Path.GetFileName(final));
                }
            }
            catch (Exception ex) { App.Note("Lumit folder", ex); Said("Lumit folder: failed - " + ex.Message); }
        }
    }

    /// <summary>The CONTEST tab: the link to reluduali.com, what is being timed, the Lumit folder, and every message sent.</summary>
    public class ContestControl : UserControl
    {
        readonly TextBlock _state = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        readonly TextBlock _now = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        readonly TextBlock _cats = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontFamily = new FontFamily("Consolas") };
        readonly TextBlock _beat = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Opacity = 0.8 };
        readonly TextBlock _lumit = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        readonly TextBlock _sent = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Opacity = 0.8 };
        readonly DispatcherTimer _paint = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ContestTimer T { get { return ContestTimer.Shared; } }

        public ContestControl()
        {
            Foreground = (Brush)Application.Current.FindResource("Text");
            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(Ui.Primary(Btn("Link...", (s, e) => LinkNow(), "Type the code the Lumit panel shows (Record, Link the Console)")));
            row.Children.Add(Ui.Gap());
            row.Children.Add(Btn("Start timing", (s, e) => T.StartIfLinked()));
            row.Children.Add(Btn("Stop timing", (s, e) => ThreadPool.QueueUserWorkItem(_ => T.Stop(true))));
            row.Children.Add(Ui.Gap());
            row.Children.Add(Btn("Forget the pass here", (s, e) => { ThreadPool.QueueUserWorkItem(_ => { T.Stop(true); TimerLink.ForgetPass(); T.Pass = null; T.State = "Not linked."; }); },
                                 "Unlinking for good is done in the Lumit panel (Unlink); this only forgets the pass on this PC"));
            var timing = Ui.Card("Contest time", "Reported to " + TimerLink.ConfiguredServer() + ". In-game work and queued recording are timed here; editing and rendering are timed by the Reluduali panel in Lumit. The rates are the contest's, read when timing starts.",
                                 _state, row, _now, _cats, _beat);
            var lrow = new WrapPanel();
            lrow.Children.Add(_lumit);
            lrow.Children.Add(Btn("Choose...", (s, e) => ChooseLumit(), "The folder you also chose in the panel's Record page, Watch a folder"));
            lrow.Children.Add(Btn("Off", (s, e) => { LumitFolder.Path_ = ""; Paint(); }));
            var lumit = Ui.Card("Lumit folder", "Each finished take's video is copied there whole (through a hidden folder, then moved up in one step), so Lumit brings it into the project. " +
                                "Lumit must be open to bring a take in: a take that lands while Lumit or its Reluduali panel is closed is not brought in later. " +
                                "Pick the same folder as the panel's Record > Watch a folder.", lrow);
            var root = new StackPanel { Margin = new Thickness(12, 2, 12, 12) };
            root.Children.Add(timing);
            // 2026-10-17: the person's own sign-in (a browser code): friends, entering, handing in, as on the site and in Lumit
            root.Children.Add(Ui.Card("Account", "Friends, entering a contest and handing in: the same as on reluduali.com and in the Lumit panel, whichever you reach for first.", new AccountControl()));
            root.Children.Add(lumit);

            var ex = new Expander { Header = "What the Designer sends", Margin = new Thickness(0, 16, 0, 0), Foreground = Foreground };
            var exs = new StackPanel();
            foreach (string m in TimerLink.Messages) exs.Children.Add(new TextBlock { Text = "- " + m, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
            exs.Children.Add(new TextBlock { Text = "Only to " + TimerLink.ConfiguredServer() + ", https only, no redirects followed. The pass is kept for this Windows user only and never shown.",
                                             TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 6, 0, 6) });
            exs.Children.Add(new TextBlock { Text = "Sent so far:", Margin = new Thickness(0, 4, 0, 2) });
            exs.Children.Add(_sent);
            ex.Content = exs;
            root.Children.Add(ex);
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            _paint.Tick += (s, e) => Paint();
            IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) { Paint(); _paint.Start(); } else _paint.Stop(); };
        }

        static string Clock(double ms) { var t = TimeSpan.FromMilliseconds(ms); return ((int)t.TotalHours).ToString("0") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00"); }

        void Paint()
        {
            _state.Text = T.State;
            _now.Text = T.Running ? "Now: " + T.Now : "";
            _cats.Text = string.Join("\n", T.Ledger.Categories.Select(c => string.Format(CultureInfo.InvariantCulture, "{0,-18} {1,5:0.##}x   this session {2} in front, {3} charged",
                c.Name, c.Multiplier, Clock(T.Ledger.InFront(c.Name)), Clock(T.Ledger.Charged(c.Name)))));
            _beat.Text = T.LastBeat.Length > 0 ? "Last check-in " + T.LastBeat : "";
            string lf = LumitFolder.Path_;
            _lumit.Text = lf.Length > 0 ? lf : "(off: takes stay in the batch's output folder only)";
            if (T.Link != null) lock (T.Link.Sent) _sent.Text = string.Join("\n", T.Link.Sent.Skip(Math.Max(0, T.Link.Sent.Count - 20)));
        }

        void LinkNow()
        {
            string code = Ask.Text(Window.GetWindow(this), "Link the Console", "The code the Reluduali panel in Lumit shows (Record, Link the Console):", "");
            if (string.IsNullOrWhiteSpace(code)) return;
            _state.Text = "Linking...";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                T.LinkWith(code);
                Dispatcher.BeginInvoke(new Action(Paint));
            });
        }

        void ChooseLumit()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Pick any file in the folder Lumit watches (or type a name in it)", CheckFileExists = false, FileName = "this folder" };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            LumitFolder.Path_ = Path.GetDirectoryName(dlg.FileName);
            Paint();
        }

        static Button Btn(string text, RoutedEventHandler click, string tip = null)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6), ToolTip = tip };
            b.Click += click;
            return b;
        }
    }
}
