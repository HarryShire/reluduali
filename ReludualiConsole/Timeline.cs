using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    /// The timeline of one panel's moves (overgrow night, 2026-10-10; Mycelium, 2026-10-01: "a whole Keyframe and timeline view
    /// that locks to ticks in game. With Graph Editors."). One lane per slider part that has moves: its curve over the film's
    /// ticks, the keys as dots, the game's own tick as a playhead. Click a lane: a key there (that value, that tick). Drag a
    /// key: change its value and tick. Right-click a key: remove it. Wheel: zoom; Shift+wheel: scroll. Double-click: the game
    /// goes to that tick (demo_desiredTime; needs jump points). Edits go straight into the moves Play uses.
    /// </summary>
    public class TimelineWindow : Window
    {
        const double LaneHeight = 90, Left = 170, Gap = 10;
        readonly KnobPanel _panel;
        readonly Canvas _canvas = new Canvas { Background = Ui.Brush("Inset"), ClipToBounds = true };
        readonly TextBlock _info = new TextBlock { Margin = new Thickness(8, 4, 8, 4), Foreground = Brushes.Gainsboro };
        readonly DispatcherTimer _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        double _from, _to;          // the ticks across the view
        int _gameTick = -1;
        volatile bool _asking;
        // what is being dragged: lane (knob, part), key
        Knob _dragKnob; int _dragPart; KnobPanel.MoveKey _dragKey;

        sealed class Lane { public Knob Knob; public int Part; public double Top, Lo, Hi; }
        readonly List<Lane> _lanes = new List<Lane>();

        public TimelineWindow(KnobPanel panel)
        {
            _panel = panel;
            Title = panel.PanelName + " - timeline";
            Width = 1000; Height = 520;
            Background = (Brush)Application.Current.FindResource("Back");
            var dock = new DockPanel();
            DockPanel.SetDock(_info, Dock.Bottom);
            dock.Children.Add(_info);
            dock.Children.Add(_canvas);
            Content = dock;
            Fit();
            _canvas.SizeChanged += (s, e) => Draw();
            _canvas.MouseLeftButtonDown += Down;
            _canvas.MouseMove += Move;
            _canvas.MouseLeftButtonUp += (s, e) => { _dragKey = null; _canvas.ReleaseMouseCapture(); _panel.TracksChanged(); };
            _canvas.MouseRightButtonDown += RightDown;
            _canvas.MouseWheel += Wheel;
            _clock.Tick += (s, e) => AskTick();
            _clock.Start();
            Closed += (s, e) => _clock.Stop();
        }

        /// <summary>The view spans every key, with a margin; one second if there are none.</summary>
        void Fit()
        {
            var keys = _panel.Tracks.Values.SelectMany(t => t).Select(k => k.Tick).ToList();
            if (keys.Count == 0) { _from = Math.Max(0, _gameTick) ; _to = _from + 10000; return; }
            double a = keys.Min(), b = keys.Max(), m = Math.Max(500, (b - a) * 0.1);
            _from = a - m; _to = b + m;
        }

        double X(double tick) { return Left + (tick - _from) / (_to - _from) * Math.Max(1, _canvas.ActualWidth - Left - Gap); }
        double TickAt(double x) { return _from + (x - Left) / Math.Max(1, _canvas.ActualWidth - Left - Gap) * (_to - _from); }
        double Y(Lane l, double v) { double lo = l.Lo, hi = l.Hi; return l.Top + LaneHeight - 8 - (v - lo) / Math.Max(1e-9, hi - lo) * (LaneHeight - 16); }
        double ValueAtY(Lane l, double y) { double lo = l.Lo, hi = l.Hi; return lo + (l.Top + LaneHeight - 8 - y) / (LaneHeight - 16) * (hi - lo); }

        void Draw()
        {
            _canvas.Children.Clear();
            _lanes.Clear();
            double top = 6;
            foreach (var kv in _panel.Tracks.Where(t => t.Value.Count > 0))
                for (int part = 0; part < kv.Key.Values.Length; part++)
                {
                    // the lane's height fits its own keys (a 10% margin; at least 5% of the slider's range), so small moves read
                    double kmin = kv.Value.Min(k => k.V[part]), kmax = kv.Value.Max(k => k.V[part]);
                    double full = kv.Key.Max[part] - kv.Key.Min[part], span = Math.Max(kmax - kmin, full * 0.05), mid = (kmin + kmax) / 2;
                    var lane = new Lane { Knob = kv.Key, Part = part, Top = top, Lo = mid - span * 0.6, Hi = mid + span * 0.6 };
                    _lanes.Add(lane);
                    _canvas.Children.Add(new Rectangle { Width = Math.Max(0, _canvas.ActualWidth - Gap), Height = LaneHeight - 4, Fill = Ui.Brush("Inset") });
                    Canvas.SetLeft(_canvas.Children[_canvas.Children.Count - 1], 0); Canvas.SetTop(_canvas.Children[_canvas.Children.Count - 1], top);
                    string name = kv.Key.Label + (kv.Key.Values.Length > 1 ? " " + (kv.Key.Parts[part].Length > 0 ? kv.Key.Parts[part] : part.ToString()) : "");
                    var label = new TextBlock { Text = name + "\n" + Fmt(lane.Lo) + " .. " + Fmt(lane.Hi), Foreground = Brushes.Gainsboro, Width = Left - 10, TextWrapping = TextWrapping.Wrap };
                    Canvas.SetLeft(label, 6); Canvas.SetTop(label, top + 6); _canvas.Children.Add(label);
                    // the curve: sampled across the view, as Play would set it
                    var line = new Polyline { Stroke = (Brush)Application.Current.FindResource("Accent"), StrokeThickness = 2 };
                    for (double x = Left; x <= _canvas.ActualWidth - Gap; x += 3)
                    {
                        double[] v = KnobPanel.ValueAt(kv.Value, (int)TickAt(x));
                        if (v != null) line.Points.Add(new Point(x, Y(lane, v[part])));
                    }
                    _canvas.Children.Add(line);
                    foreach (var k in kv.Value)
                    {
                        double x = X(k.Tick);
                        if (x < Left - 4 || x > _canvas.ActualWidth) continue;
                        var dot = new Ellipse { Width = 8, Height = 8, Fill = Brushes.White };
                        Canvas.SetLeft(dot, x - 4); Canvas.SetTop(dot, Y(lane, k.V[part]) - 4);
                        _canvas.Children.Add(dot);
                    }
                    top += LaneHeight;
                }
            if (_lanes.Count == 0)
            {
                var t = new TextBlock { Text = "No moves yet: Record moves, play the film and move a slider.", Foreground = Brushes.Gainsboro };
                Canvas.SetLeft(t, 12); Canvas.SetTop(t, 12); _canvas.Children.Add(t);
            }
            if (_gameTick >= 0 && _gameTick >= _from && _gameTick <= _to)
            {
                double x = X(_gameTick);
                _canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = Math.Max(top, 40), Stroke = Brushes.OrangeRed, StrokeThickness = 1.5 });
            }
            _info.Text = "ticks " + (int)_from + " - " + (int)_to + "   (" + ((_to - _from) / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " s)" +
                         (_gameTick >= 0 ? "   game at " + _gameTick : "   (no film)") + "   click: a key; drag: move it; right-click: remove; wheel: zoom; Shift+wheel: scroll; double-click: go there";
        }

        static string Fmt(double v) { return v.ToString(Math.Abs(v) >= 100 ? "0" : "0.##", CultureInfo.InvariantCulture); }

        Lane LaneAt(double y) { return _lanes.FirstOrDefault(l => y >= l.Top && y < l.Top + LaneHeight); }

        KnobPanel.MoveKey KeyNear(Lane l, Point p)
        {
            List<KnobPanel.MoveKey> track = _panel.Tracks[l.Knob];
            return track.Where(k => Math.Abs(X(k.Tick) - p.X) < 7 && Math.Abs(Y(l, k.V[l.Part]) - p.Y) < 7).FirstOrDefault();
        }

        void Down(object sender, MouseButtonEventArgs e)
        {
            Point p = e.GetPosition(_canvas);
            if (e.ClickCount == 2 && p.X > Left) { GoTo((int)TickAt(p.X)); return; }
            Lane l = LaneAt(p.Y);
            if (l == null || p.X < Left) return;
            KnobPanel.MoveKey k = KeyNear(l, p);
            List<KnobPanel.MoveKey> track = _panel.Tracks[l.Knob];
            if (k == null)
            {
                int tick = (int)TickAt(p.X);
                double[] v = (double[])(KnobPanel.ValueAt(track, tick) ?? l.Knob.Values).Clone();
                v[l.Part] = Clamp(l, ValueAtY(l, p.Y));
                k = new KnobPanel.MoveKey { Tick = tick, V = v };
                track.Add(k);
                track.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            }
            _dragKnob = l.Knob; _dragPart = l.Part; _dragKey = k;
            _canvas.CaptureMouse();
            Draw();
        }

        double Clamp(Lane l, double v) { return Math.Max(l.Knob.Min[l.Part], Math.Min(l.Knob.Max[l.Part], v)); }

        void Move(object sender, MouseEventArgs e)
        {
            if (_dragKey == null || e.LeftButton != MouseButtonState.Pressed) return;
            Lane l = _lanes.FirstOrDefault(x => x.Knob == _dragKnob && x.Part == _dragPart);
            if (l == null) return;
            Point p = e.GetPosition(_canvas);
            _dragKey.Tick = (int)TickAt(Math.Max(Left, p.X));
            _dragKey.V[_dragPart] = Clamp(l, ValueAtY(l, p.Y));
            _panel.Tracks[_dragKnob].Sort((a, b) => a.Tick.CompareTo(b.Tick));
            Draw();
        }

        void RightDown(object sender, MouseButtonEventArgs e)
        {
            Point p = e.GetPosition(_canvas);
            Lane l = LaneAt(p.Y);
            if (l == null) return;
            KnobPanel.MoveKey k = KeyNear(l, p);
            if (k == null) return;
            _panel.Tracks[l.Knob].Remove(k);
            _panel.TracksChanged();
            Draw();
        }

        void Wheel(object sender, MouseWheelEventArgs e)
        {
            double span = _to - _from;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { double d = -Math.Sign(e.Delta) * span * 0.1; _from += d; _to += d; }
            else
            {
                double at = TickAt(e.GetPosition(_canvas).X), f = e.Delta > 0 ? 0.8 : 1.25;
                _from = at - (at - _from) * f; _to = at + (_to - at) * f;
                if (_to - _from < 200) { _to = _from + 200; }
            }
            Draw();
        }

        void AskTick()
        {
            if (_asking) return;
            _asking = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string st = null;
                try { st = BridgeClient.Ask("stats", 300, 1); } catch { }
                int t = st != null && !st.Contains("theater 0x00000000") ? (int)BridgeClient.Field(st, "tick") : -1;
                Dispatcher.BeginInvoke(new Action(() => { _asking = false; if (t != _gameTick && _dragKey == null) { _gameTick = t; Draw(); } }));
            });
        }

        void GoTo(int tick)
        {
            ThreadPool.QueueUserWorkItem(_ => BridgeClient.Ask("cmd demo_desiredTime " + tick.ToString(CultureInfo.InvariantCulture), 1000, 2));
        }
    }
}
