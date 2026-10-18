using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>
    /// Our own theater timeline, under the docked game (Mycelium's round 1, 2026-10-15, 06:29-06:47: "always have the theater
    /// GUI muted ... toggled off like that. And then we can just have our GUI there instead"; 13:37: "get the Black Ops 2
    /// actual timeline built in this console"; 10-16: "Yes For sunday but the last of what you should get done").
    ///
    /// The film's clock from RipperBridge's stats (tick, end tick) four times a second, play / pause, the speed (timescale,
    /// set through the bridge: as a console command it is ignored), 10 s back and on, and a click anywhere on the bar to go
    /// there and pause. Forward: played fast to the point (sandbox 0454: 13-29 ms off). Back: the game's demo_back moved
    /// nothing (0456, paused or playing), so the film is loaded again and played forward to the point (0458: 175 ms off,
    /// 18 s). The dolly markers placed in Edit Camera show on the bar. The game's own bar hides with its F4 (the F4 button
    /// posts it).
    ///
    /// Keyframes (RipperBridge dev21, 2026-10-17; Mycelium: "Invisible Markers that are just for the timeline but that have
    /// no Camera connected to them. So the timeline is fully draggable."): while the film plays, one is taken every 2 s of
    /// film - the snapshot the game itself keeps for a camera marker, kept by the bridge outside the marker list. A seek
    /// back, or far forward, jumps to the last one at or before the point and plays the rest; only where none was taken
    /// yet is the film loaded again. They show as small ticks along the top of the bar.
    ///
    /// Drag to scrub (2026-10-17): while the bar is held and dragged, the film jumps from keyframe to keyframe under the
    /// pointer (each jump about a quarter of a second, one at a time, only when it brings the film nearer the pointer);
    /// let go, and it goes to the exact point, as a click does.
    /// </summary>
    public sealed class TheaterTimeline : UserControl
    {
        readonly Func<IntPtr> _gameWindow;
        readonly Canvas _bar = new Canvas { Height = 20, ClipToBounds = true, Cursor = Cursors.Hand, Background = Ui.Brush("Inset") };
        readonly Rectangle _fill = new Rectangle { Height = 20, Fill = Ui.Brush("TlFill") };   // night the lilac, day the edge's blue (Skin.cs)
        readonly Rectangle _head = new Rectangle { Width = 2, Height = 20, Fill = Ui.Brush("TlHead") };
        readonly Rectangle _target = new Rectangle { Width = 2, Height = 20, Fill = Ui.Brush("TlTarget"), Visibility = Visibility.Collapsed };
        readonly List<Rectangle> _marks = new List<Rectangle>();
        readonly List<Rectangle> _kfMarks = new List<Rectangle>();
        readonly TextBlock _time = new TextBlock { Foreground = Ui.Brush("Dim"), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0), MinWidth = 150 };
        readonly Button _play;
        readonly List<Button> _speeds = new List<Button>();
        readonly DispatcherTimer _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        int _tick = int.MinValue, _start = int.MaxValue, _end, _lastTick = int.MinValue, _still;
        long _theater;
        double _speed = 1;
        int _asking, _seeking;
        long _marksAt;
        int[] _markTicks = new int[0];
        // keyframes: the bridge's kept landings (kf list), whether this bridge has them, the last save asked for
        const int KfEvery = 2000;
        int[] _kfTicks = new int[0];
        bool _kfNone;
        int _kfAsked = int.MinValue, _kfSaving;
        // drag to scrub: held, and where the pointer is (read by the scrub's own thread)
        volatile bool _dragging;
        volatile int _dragWant;

        public TheaterTimeline(Func<IntPtr> gameWindow)
        {
            _gameWindow = gameWindow;
            _play = Small("", "Play / pause the film", TogglePlay);
            var back = Small("", "10 s back", () => Seek(_tick - 10000));
            var on = Small("", "10 s on", () => Seek(_tick + 10000));
            var row = new DockPanel { LastChildFill = true };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(back); left.Children.Add(_play); left.Children.Add(on);
            left.Children.Add(_time);
            DockPanel.SetDock(left, Dock.Left);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
            foreach (double s in new[] { 0.1, 0.25, 0.5, 1, 2 })
            {
                double v = s;
                var b = Small(v.ToString("0.##", CultureInfo.InvariantCulture) + "x", "The film's speed: timescale " + v.ToString(CultureInfo.InvariantCulture), () => SetSpeed(v));
                b.FontFamily = new FontFamily("Segoe UI"); b.FontSize = 11; b.MinWidth = 34;
                _speeds.Add(b); right.Children.Add(b);
            }
            right.Children.Add(Small("F4", "The game's own theater bar: hide it or show it (its F4)", PostF4));
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(left); row.Children.Add(right);
            row.Children.Add(new Border());                  // the space between
            _bar.Children.Add(_fill); _bar.Children.Add(_head); _bar.Children.Add(_target);
            // the bar on a row of its own, the whole width of the game: in a narrow slot the buttons left it none (0457)
            var barBox = new Border { Child = _bar, CornerRadius = new CornerRadius(2), ClipToBounds = true, Margin = new Thickness(0, 0, 0, 5),
                                      ToolTip = "The film. Click to go there and pause, or drag: the film follows through the keyframes taken while it played (the small ticks along the top), and goes to the exact point where you let go. Forward past them: played fast to the point. Back with none: the film is loaded again and played to it (the game's own jump back does nothing)." };
            _bar.MouseLeftButtonDown += (s, e) => { try { DragStart(e.GetPosition(_bar).X); } catch (Exception ex) { App.Note("the timeline: drag", ex); } };
            _bar.MouseMove += (s, e) => { if (_dragging) DragMove(e.GetPosition(_bar).X); };
            _bar.MouseLeftButtonUp += (s, e) => DragEnd();
            _bar.LostMouseCapture += (s, e) => DragEnd();
            _bar.SizeChanged += (s, e) => Draw();
            var both = new StackPanel();
            both.Children.Add(barBox);
            both.Children.Add(row);
            Content = new Border { Child = both, Padding = new Thickness(8, 5, 8, 6) };
            PaintSpeeds();
            _poll.Tick += (s, e) => Poll();
            IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) _poll.Start(); else _poll.Stop(); };
        }

        static Button Small(string text, string tip, Action click)
        {
            var b = new Button { Content = text, ToolTip = tip, MinWidth = 28, MinHeight = 24, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 0),
                                 FontFamily = Ui.IconFont, FontSize = 12 };
            b.Click += (s, e) => { try { click(); } catch (Exception ex) { App.Note("the timeline: " + text, ex); } };
            return b;
        }

        static string Clock(int ms)
        {
            if (ms < 0) ms = 0;
            return (ms / 60000).ToString(CultureInfo.InvariantCulture) + ":" + (ms / 1000 % 60).ToString("00", CultureInfo.InvariantCulture) + "." + (ms / 100 % 10).ToString(CultureInfo.InvariantCulture);
        }

        bool BatchHasIt { get { return BatchEngine.Recording || (BatchControl.Instance != null && BatchControl.Instance.IsBusy); } }

        void Poll()
        {
            if (!IsVisible || App.Tour) return;
            if (BatchHasIt) { _time.Text = "the batch has the film"; return; }
            if (Interlocked.CompareExchange(ref _asking, 1, 0) != 0) return;
            bool wantMarks = Environment.TickCount - _marksAt > 2000;
            bool none = _kfNone;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string s = null; int[] marks = null, kfs = null; bool noKf = false;
                try
                {
                    s = BridgeClient.Ask("stats", 300, 1);
                    if (s != null && wantMarks) marks = ReadMarks(s);
                    if (s != null && wantMarks && !none) kfs = ReadKeyframes(out noKf);
                }
                catch { }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Interlocked.Exchange(ref _asking, 0);
                    if (wantMarks) _marksAt = Environment.TickCount;
                    if (noKf) _kfNone = true;
                    if (kfs != null) _kfTicks = kfs;
                    Take(s, marks);
                }));
            });
        }

        /// <summary>The keyframes RipperBridge keeps for this film ("kf list": "kf N: t1 t2 ..."); none = an older bridge.</summary>
        static int[] ReadKeyframes(out bool none)
        {
            none = false;
            string r = BridgeClient.Ask("kf list", 400, 1);
            if (r == null) return null;
            if (r.StartsWith("error unknown command", StringComparison.Ordinal)) { none = true; return null; }
            int colon = r.IndexOf(':');
            if (!r.StartsWith("kf ", StringComparison.Ordinal) || colon < 0) return null;
            var t = new List<int>();
            foreach (string p in r.Substring(colon + 1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            { int v; if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) t.Add(v); }
            return t.ToArray();
        }

        /// <summary>The last keyframe at or before a tick, or int.MinValue.</summary>
        static int KeyframeAtOrBefore(int[] kfs, int tick)
        {
            int best = int.MinValue;
            foreach (int k in kfs) if (k <= tick && k > best) best = k;
            return best;
        }

        /// <summary>While the film plays: a keyframe every KfEvery ms of film not covered yet.</summary>
        void KeepKeyframes(bool playing)
        {
            if (_kfNone || !playing || _seeking != 0 || _tick == int.MinValue) return;
            int k = KeyframeAtOrBefore(_kfTicks, _tick);
            if (k != int.MinValue && _tick - k < KfEvery) return;
            if (_kfAsked != int.MinValue && _tick >= _kfAsked && _tick - _kfAsked < KfEvery) return;     // asked already, the list not read again yet
            if (Interlocked.CompareExchange(ref _kfSaving, 1, 0) != 0) return;
            _kfAsked = _tick;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string r = null;
                try { r = BridgeClient.Ask("kf save", 3000, 1); } catch { }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Interlocked.Exchange(ref _kfSaving, 0);
                    if (r != null && r.StartsWith("error unknown command", StringComparison.Ordinal)) _kfNone = true;
                    int at = r == null ? -1 : r.IndexOf("kf at ", StringComparison.Ordinal);
                    int v;
                    if (at >= 0 && int.TryParse(r.Substring(at + 6).Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                    {
                        var l = new List<int>(_kfTicks); if (!l.Contains(v)) { l.Add(v); l.Sort(); _kfTicks = l.ToArray(); }
                    }
                }));
            });
        }

        static long Theater(string s)
        {
            int i = s.IndexOf("theater 0x", StringComparison.Ordinal);
            if (i < 0) return 0;
            string hex = s.Substring(i + 10).Split(' ')[0];
            long v; return long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        /// <summary>The dolly markers' film times (theater + 0xEE708: how many; + 0xED440, 0x60 each: word 0 = its time).</summary>
        static int[] ReadMarks(string stats)
        {
            long th = Theater(stats);
            if (th == 0) return new int[0];
            string c = BridgeClient.Ask("read " + (th + 0xEE708).ToString("X") + " 4", 300, 1);
            int n = c != null && c.Length == 8 ? BitConverter.ToInt32(Hex(c), 0) : 0;
            if (n <= 0 || n > 64) return new int[0];
            // five records a read: the bridge answers at most 480 bytes (the code review's C-11: six markers or more showed none)
            var t = new int[n];
            for (int first = 0; first < n; first += 5)
            {
                int k = Math.Min(5, n - first);
                string part = BridgeClient.Ask("read " + (th + 0xED440 + 0x60 * first).ToString("X") + " " + (0x60 * k).ToString(CultureInfo.InvariantCulture), 400, 1);
                if (part == null || part.Length != 0x60 * k * 2) return new int[0];
                byte[] b = Hex(part);
                for (int i = 0; i < k; i++) t[first + i] = BitConverter.ToInt32(b, i * 0x60);
            }
            return t;
        }

        static byte[] Hex(string h) { var b = new byte[h.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16); return b; }

        void Take(string s, int[] marks)
        {
            if (s == null) { _time.Text = "no film (RipperBridge: -)"; return; }
            long th = Theater(s);
            if (th == 0) { _time.Text = "no film loaded"; _start = int.MaxValue; _tick = int.MinValue; Draw(); return; }
            int tick = (int)BridgeClient.Field(s, "tick"), end = (int)BridgeClient.Field(s, "endtick");
            if (th != _theater) { _theater = th; _start = tick; _markTicks = new int[0]; _kfTicks = new int[0]; _kfAsked = int.MinValue; }     // a new film: it loads at its start
            if (tick < _start) _start = tick;
            _still = tick == _lastTick ? _still + 1 : 0;
            _lastTick = tick;
            _tick = tick; _end = Math.Max(end, tick);
            if (marks != null) _markTicks = marks;
            bool playing = _still == 0;
            _play.Content = playing ? "" : "";
            _time.Text = _dragging ? "to " + Clock(_dragWant - _start) + " / " + Clock(_end - _start)
                       : (_seeking != 0 ? "seeking  " : "") + Clock(_tick - _start) + " / " + Clock(_end - _start);
            Draw();
            KeepKeyframes(playing);
        }

        void Draw()
        {
            double w = _bar.ActualWidth;
            if (w <= 0 || _end <= _start || _tick == int.MinValue) { _fill.Width = 0; _head.Visibility = Visibility.Collapsed; return; }
            double x = w * (_tick - _start) / (double)(_end - _start);
            _fill.Width = Math.Max(0, x);
            _head.Visibility = Visibility.Visible;
            Canvas.SetLeft(_head, Math.Max(0, x - 1));
            while (_marks.Count < _markTicks.Length) { var r = new Rectangle { Width = 3, Height = 9, Fill = Ui.Brush("TlMark") }; _marks.Add(r); _bar.Children.Add(r); }
            for (int i = 0; i < _marks.Count; i++)
            {
                bool on = i < _markTicks.Length && _markTicks[i] >= _start && _markTicks[i] <= _end;
                _marks[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (on) { Canvas.SetLeft(_marks[i], w * (_markTicks[i] - _start) / (double)(_end - _start) - 1); Canvas.SetTop(_marks[i], 11); }
            }
            while (_kfMarks.Count < _kfTicks.Length) { var r = new Rectangle { Width = 1, Height = 4, Fill = Ui.Brush("TlKey") }; _kfMarks.Add(r); _bar.Children.Add(r); }
            for (int i = 0; i < _kfMarks.Count; i++)
            {
                bool on = i < _kfTicks.Length && _kfTicks[i] >= _start && _kfTicks[i] <= _end;
                _kfMarks[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (on) { Canvas.SetLeft(_kfMarks[i], w * (_kfTicks[i] - _start) / (double)(_end - _start)); Canvas.SetTop(_kfMarks[i], 0); }
            }
        }

        void TogglePlay()
        {
            if (BatchHasIt) return;
            bool playing = _still == 0;
            ThreadPool.QueueUserWorkItem(_ => { try { BridgeClient.Ask("cmd demo_pause " + (playing ? "1" : "0"), 800, 1); } catch { } });
        }

        void SetSpeed(double v)
        {
            if (BatchHasIt) return;
            _speed = v;
            PaintSpeeds();
            ThreadPool.QueueUserWorkItem(_ => { try { BridgeClient.Ask("setdvar timescale " + v.ToString(CultureInfo.InvariantCulture), 800, 1); } catch { } });
        }

        void PaintSpeeds()
        {
            foreach (Button b in _speeds)
            {
                bool on = (string)b.Content == _speed.ToString("0.##", CultureInfo.InvariantCulture) + "x";
                b.Background = on ? Ui.Brush("Accent") : null;
            }
        }

        // not below the first tick seen only if that was the film's start (a Console started mid-film sees it later)
        int Clamp(int target) { return _end > _start ? Math.Max(0, Math.Min(_end - 50, target)) : target; }

        void ShowTarget(int target)
        {
            double w = _bar.ActualWidth;
            if (w > 0 && _end > _start) { Canvas.SetLeft(_target, w * (target - _start) / (double)(_end - _start) - 1); _target.Visibility = Visibility.Visible; }
        }

        void EndSeek() { Interlocked.Exchange(ref _seeking, 0); _target.Visibility = Visibility.Collapsed; }

        /// <summary>Goes to a film tick and pauses there (the buttons; SeekWork).</summary>
        public void Seek(int target)
        {
            if (BatchHasIt || _tick == int.MinValue) return;
            target = Clamp(target);
            if (Interlocked.CompareExchange(ref _seeking, 1, 0) != 0) return;
            ShowTarget(target);
            double speed = _speed;
            int[] kfs = _kfNone ? new int[0] : _kfTicks;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { SeekWork(target, speed, kfs); }
                catch (Exception ex) { App.Note("the timeline's seek", ex); }
                Dispatcher.BeginInvoke(new Action(EndSeek));
            });
        }

        // ---- drag to scrub (a click is a drag that never moved)
        void DragStart(double x)
        {
            if (BatchHasIt || _tick == int.MinValue || _end <= _start || _bar.ActualWidth <= 0) return;
            if (Interlocked.CompareExchange(ref _seeking, 1, 0) != 0) return;      // a seek on its way: the press waits for nothing
            _dragging = true;
            _bar.CaptureMouse();
            DragMove(x);
            double speed = _speed;
            int[] kfs = _kfNone ? new int[0] : _kfTicks;
            int from = _tick;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { ScrubWork(from, speed, kfs); }
                catch (Exception ex) { App.Note("the timeline's scrub", ex); }
                Dispatcher.BeginInvoke(new Action(() => { _dragging = false; EndSeek(); }));
            });
        }

        void DragMove(double x)
        {
            double w = _bar.ActualWidth;
            if (w <= 0 || _end <= _start) return;
            int t = Clamp(_start + (int)((_end - _start) * Math.Max(0, Math.Min(w, x)) / w));
            _dragWant = t;
            ShowTarget(t);
            _time.Text = "to " + Clock(t - _start) + " / " + Clock(_end - _start);
        }

        void DragEnd()
        {
            if (!_dragging) return;
            _dragging = false;
            if (_bar.IsMouseCaptured) _bar.ReleaseMouseCapture();
        }

        /// <summary>A drag for tests ("console scrub"): pressed at the first tick, moved through the others stepMs apart, let go
        /// after the last - the mouse's own handlers, without a mouse.</summary>
        public string TestScrub(int[] ticks, int stepMs)
        {
            if (ticks.Length == 0 || _end <= _start || _bar.ActualWidth <= 0) return "no film on the bar";
            Func<int, double> x = t => _bar.ActualWidth * (t - _start) / (double)(_end - _start);
            DragStart(x(ticks[0]));
            if (!_dragging) return "the bar is busy (a seek on its way, or the batch has the film)";
            var steps = new Queue<int>();
            for (int i = 1; i < ticks.Length; i++) steps.Enqueue(ticks[i]);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(16, stepMs)) };
            timer.Tick += (s, e) => { if (steps.Count > 0) DragMove(x(steps.Dequeue())); else { timer.Stop(); DragEnd(); } };
            timer.Start();
            return "scrubbing through " + ticks.Length + " points, " + stepMs + " ms apart (bar " + _start + "-" + _end + ")";
        }

        /// <summary>The scrub's own thread: keyframe jumps after the pointer while it is held (one at a time, so as fast as
        /// the game takes them), then the exact point where it was let go.</summary>
        void ScrubWork(int from, double speed, int[] kfs)
        {
            BridgeClient.Ask("cmd demo_pause 1", 800, 1);
            int pos = from, lastK = int.MinValue;
            while (true)
            {
                bool held = _dragging;
                int want = _dragWant;
                int k = KeyframeAtOrBefore(kfs, want);
                // only a jump that brings the film nearer: dragged ahead of the last keyframe, the film waits for the release
                if (k != int.MinValue && k != lastK && Math.Abs((long)want - k) < Math.Abs((long)want - pos))
                {
                    string r = BridgeClient.Ask("kf jump " + want.ToString(CultureInfo.InvariantCulture), 10000, 1);
                    lastK = k;
                    if (r != null && r.StartsWith("ok", StringComparison.Ordinal)) pos = k;
                    else
                    {
                        kfs = new int[0];                    // refused (one from an earlier load, past this load's reach): no more
                        Dispatcher.BeginInvoke(new Action(() => _time.Text = "the keyframe jump did not take: " + (r ?? "no answer")));
                    }
                    Thread.Sleep(60);                        // a frame or two to show where it landed
                    continue;
                }
                if (!held) break;
                Thread.Sleep(30);
            }
            SeekWork(_dragWant, speed, kfs);
        }

        /// <summary>A seek's own thread: to a film tick, paused. Back, or far forward, through the last keyframe at or
        /// before it; back with none, the film loaded again; then forward fast to the point.</summary>
        void SeekWork(int target, double speed, int[] kfs)
        {
            Func<int> now = () => { string s = BridgeClient.Ask("stats", 300, 1); return s == null ? int.MinValue : (int)BridgeClient.Field(s, "tick"); };
            int t = now();
            BridgeClient.Ask("cmd demo_pause 1", 800, 1);
            // back, or far forward: the last keyframe at or before the point (taken while the film played), then
            // played forward the rest of the way, below
            int k = KeyframeAtOrBefore(kfs, target);
            if (k != int.MinValue && t != int.MinValue && (t > target + 150 || k > t + 3000))
            {
                string r = BridgeClient.Ask("kf jump " + target.ToString(CultureInfo.InvariantCulture), 10000, 1);
                if (r != null && r.StartsWith("ok", StringComparison.Ordinal))
                {
                    Thread.Sleep(250);
                    t = now();
                }
                else Dispatcher.BeginInvoke(new Action(() => _time.Text = "the keyframe jump did not take: " + (r ?? "no answer")));
            }
            // back with no keyframe: the game's demo_back moved nothing (sandbox 0454-0456, paused or playing), so the
            // film is loaded again (it starts at its beginning) and played forward to the point. Only a film loaded
            // through the bridge is known (the Console's Demos, the Cam Queue, the batch).
            bool known = true;
            if (t != int.MinValue && t > target + 150)
            {
                string demo = BridgeClient.LastDemo;
                known = !string.IsNullOrEmpty(demo) && System.IO.File.Exists(demo);
                if (!known) Dispatcher.BeginInvoke(new Action(() => _time.Text = "going back needs the film loaded from the Designer (Demos)"));
            }
            if (known && t != int.MinValue && t > target + 150)
            {
                string demo = BridgeClient.LastDemo;
                Dispatcher.BeginInvoke(new Action(() => _time.Text = "going back: loading the film again..."));
                Func<bool> inTheater = () => { string s = BridgeClient.Ask("stats", 300, 1); return s != null && Theater(s) != 0; };
                BridgeClient.Ask("cmd demo_stop", 800, 1);
                var until = DateTime.Now.AddSeconds(20);
                while (DateTime.Now < until && inTheater()) Thread.Sleep(250);
                Thread.Sleep(3500);                 // two loads close together crash the game (2026-10-01)
                BridgeClient.Ask("loaddemo " + demo, 15000, 1);
                until = DateTime.Now.AddSeconds(60);
                while (DateTime.Now < until && !inTheater()) Thread.Sleep(250);
                Thread.Sleep(2500);
                BridgeClient.Ask("cmd demo_pause 1", 800, 1);
                Thread.Sleep(300);
                t = now();
                int loadedAt = t;
                if (loadedAt != int.MinValue) Dispatcher.BeginInvoke(new Action(() => _start = Math.Min(_start, loadedAt)));     // now the film's real start is known
            }
            if (known && t != int.MinValue && t < target - 60)
            {
                double fast = Math.Min(8, Math.Max(1, (target - t) / 1500.0));
                BridgeClient.Ask("setdvar timescale " + fast.ToString("0.##", CultureInfo.InvariantCulture), 800, 1);
                BridgeClient.Ask("cmd demo_pause 0", 800, 1);
                var until = DateTime.Now.AddSeconds(60);
                while (DateTime.Now < until)
                {
                    t = now();
                    if (t == int.MinValue || t >= target - (int)(25 * fast)) break;
                    Thread.Sleep(15);
                }
                BridgeClient.Ask("cmd demo_pause 1", 800, 1);
                BridgeClient.Ask("setdvar timescale " + speed.ToString(CultureInfo.InvariantCulture), 800, 1);
            }
        }

        void PostF4()
        {
            IntPtr g = _gameWindow != null ? _gameWindow() : IntPtr.Zero;
            if (g == IntPtr.Zero) return;
            PostMessage(g, 0x0100, (IntPtr)0x73, (IntPtr)0x003E0001);     // WM_KEYDOWN VK_F4 (scan code 0x3E)
            PostMessage(g, 0x0101, (IntPtr)0x73, new IntPtr(unchecked((int)0xC03E0001)));
        }

        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    }
}
