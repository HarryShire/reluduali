using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>
    /// The timeline's loop bracket and lanes (Mycelium's 10-18 test take, 26:06-28:14, and his words after it).
    ///
    /// Over the bar, a strip with the LOOP BRACKET, the global one, as Ableton's: drag on the strip to draw it, drag an end
    /// to move that end, drag its middle to move it whole; double-click it (or the Loop button) to switch the loop on and
    /// off. While on, the game keeps playing that section (LoopBracket).
    ///
    /// Under the bar, the LANES: the POV lane first - every POV take of this film as its in and out markers, and the POV
    /// Queue's in and out not yet a take, dashed (Mycelium: "make the POV one more like markers on its own timeline layer";
    /// takes that overlap get a row each) - then one lane per cinematic of this film, its markers on it ("I want to see this
    /// cinematic where it is on the timeline. I then want to see this POV where it is"). Drag a POV marker to move it, its
    /// middle to move the take; drag a cinematic to move it in the film ("make number three start five minutes later or ...
    /// a few frames later"), a marker of it to retime that one. Left / Right nudge the picked one a frame (Shift: a second).
    /// A click picks the row on its page.
    ///
    /// SNAPPING (Mycelium: "drag the global and it snaps to the markers on a layer in the timeline"): any drag here lands on
    /// the nearest marker within 8 pixels - a POV in or out, a cinematic's start, end or markers, the game's own dolly
    /// markers, the playhead, the loop's ends. Hold Alt to drag freely.
    ///
    /// KEYFRAMES (Mycelium, 35:28-37:03: "per cam path ... I need to see a box open up that says rotation, FOV"; "have actual
    /// keyframes per these cameras on the timeline, I click keyframe and somewhere in an effects controls panel, the keyframes
    /// come up"; "every single parameter can be keyframed"): under the take picked, a lane per parameter it has keys for
    /// (TakeKeys: a slider's right-click puts them there), each key a dot to drag along the film. A click on a key, on a
    /// cinematic's marker or on a cinematic opens the EFFECTS strip under the lanes: the key's values; the cinematic's
    /// cameras with their rotation (pitch, yaw, roll) and FOV. "Keys" plays the picked take's keys as the film plays.
    ///
    /// ZOOM: the wheel over the strip or the bar zooms around the pointer; Shift+wheel moves along; "Whole film" goes back.
    /// Over the lanes the wheel scrolls them when there are more than fit (Ctrl+wheel zooms there too).
    /// </summary>
    public sealed partial class TheaterTimeline
    {
        readonly Canvas _strip = new Canvas { Height = 18, ClipToBounds = true, Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 2) };
        readonly Canvas _lanes = new Canvas { ClipToBounds = true, Background = Brushes.Transparent, Focusable = true, FocusVisualStyle = null, Margin = new Thickness(0, 0, 0, 4) };
        Button _loopButton, _wholeButton;
        const int LaneH = 15, MaxLanesShown = 7, SnapPx = 8;
        int _laneTop;                                    // the first lane shown (they scroll when there are more)

        // the view: the film ticks at the left and right edges (zoom)
        int _v0, _v1;
        bool _zoomed;
        int V0 { get { return _zoomed ? _v0 : _start; } }
        int V1 { get { return _zoomed ? _v1 : _end; } }
        bool Viewable { get { return _tick != int.MinValue && _end > _start && _start != int.MaxValue; } }
        double X(double t, double w) { return w * (t - V0) / Math.Max(1.0, V1 - V0); }
        int T(double x, double w) { return V0 + (int)Math.Round((V1 - V0) * x / Math.Max(1.0, w)); }

        // the film's rows, as lane items
        sealed class LaneItem
        {
            public int Row = -1;                        // the queue's row; -1 = the POV Queue's in / out, not a take yet
            public bool Cine, Pending, Picked;
            public int In, Out;                         // film ticks (Out -1: only the in is marked)
            public int[] Marks = new int[0];            // a cinematic's markers; a parameter lane's keys
            public bool IsKey;                          // a parameter lane of the take picked (its keys in Marks)
            public string KeyLabel = "";                // which parameter (KeyParam.Label)
            public string Label = "";
            public int Lane;
            public LaneItem Copy() { var c = (LaneItem)MemberwiseClone(); c.Marks = (int[])Marks.Clone(); return c; }
        }
        readonly List<LaneItem> _items = new List<LaneItem>();
        readonly List<string> _laneNames = new List<string>();
        string _film;                                    // the demo file the game plays (null: not known yet)
        long _filmFor;                                   // the theater it was found for
        int _filmTries;
        readonly Dictionary<string, Tuple<DateTime, int[]>> _markCache = new Dictionary<string, Tuple<DateTime, int[]>>(StringComparer.OrdinalIgnoreCase);

        // a drag on the strip or the lanes
        enum Drag { None, LoopIn, LoopOut, LoopMove, LoopDraw, ItemIn, ItemOut, ItemMove, ItemMark, ItemKey }
        Drag _drag;
        LaneItem _dragItem, _dragWas;                    // the item as it is being dragged, and as it was
        int _dragMark, _dragGrab, _dragA, _dragB;        // which marker; where in the item / loop it was grabbed; the loop as it was
        double _dragFromX;
        bool _dragMoved;
        LaneItem _picked;                                // the lane item the arrows nudge
        double _snapX = double.NaN;                      // the snap guide, while a drag sits on a marker
        Drag _committing;                                // the drag that just ended (tests read it)
        int _pickedMark = -1;                            // the cinematic's marker picked (the effects strip shows it first)
        int _pickedKey = int.MinValue;                   // the key picked on a parameter lane (its tick)
        readonly WrapPanel _fx = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4) };
        Button _keysButton;

        /// <summary>The take picked on the timeline (-1: none): slider keyframes go to it.</summary>
        public int PickedRow { get { return _picked != null && !_picked.Pending ? _picked.Row : -1; } }

        UIElement LoopButton()
        {
            _loopButton = Small("Loop", "The loop bracket on or off (the strip over the bar: drag there to set it). While on, the game keeps playing that section.", () =>
            {
                if (!LoopBracket.Has) { _time.Text = "drag on the strip over the bar to set the loop first"; return; }
                LoopBracket.Switch(!LoopBracket.On);
            });
            _loopButton.FontFamily = new FontFamily("Segoe UI"); _loopButton.FontSize = 11;
            _wholeButton = Small("Whole film", "Zoomed in: back to the whole film", () => { _zoomed = false; Draw(); });
            _wholeButton.FontFamily = new FontFamily("Segoe UI"); _wholeButton.FontSize = 11;
            _wholeButton.Visibility = Visibility.Collapsed;
            _keysButton = Small("Keys", "Play the picked take's keyframes as the film plays (the sliders follow them)", () => TakeKeys.Play(!TakeKeys.Playing));
            _keysButton.FontFamily = new FontFamily("Segoe UI"); _keysButton.FontSize = 11;
            var both = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
            both.Children.Add(_loopButton); both.Children.Add(_keysButton); both.Children.Add(_wholeButton);
            return both;
        }

        void InitLanes()
        {
            ToolTipService.SetInitialShowDelay(_strip, 1500);
            _strip.ToolTip = "The loop bracket: drag here to set it, its ends to change it, its middle to move it; double-click it to switch the loop on or off. Snaps to the markers below (Alt: no snapping). Wheel: zoom.";
            _strip.MouseLeftButtonDown += (s, e) => { try { StripDown(e); } catch (Exception ex) { App.Note("the loop bracket", ex); } };
            _strip.MouseMove += (s, e) => { try { StripMove(e); } catch (Exception ex) { App.Note("the loop bracket", ex); } };
            _strip.MouseLeftButtonUp += (s, e) => EndDrag(_strip);
            _strip.LostMouseCapture += (s, e) => { if (_drag != Drag.None && _drag < Drag.ItemIn) EndDrag(_strip); };
            _strip.MouseRightButtonUp += (s, e) => StripMenu();
            _strip.SizeChanged += (s, e) => DrawLanes();
            _lanes.MouseLeftButtonDown += (s, e) => { try { LanesDown(e); } catch (Exception ex) { App.Note("the lanes", ex); } };
            _lanes.MouseMove += (s, e) => { try { LanesMove(e); } catch (Exception ex) { App.Note("the lanes", ex); } };
            _lanes.MouseLeftButtonUp += (s, e) => EndDrag(_lanes);
            _lanes.LostMouseCapture += (s, e) => { if (_drag >= Drag.ItemIn) EndDrag(_lanes); };
            _lanes.MouseRightButtonUp += (s, e) => LanesMenu(e.GetPosition(_lanes));
            _lanes.KeyDown += (s, e) => LanesKey(e);
            _lanes.SizeChanged += (s, e) => DrawLanes();
            foreach (UIElement u in new UIElement[] { _strip, _bar }) u.PreviewMouseWheel += (s, e) => { Wheel(e, (FrameworkElement)s); };
            _lanes.PreviewMouseWheel += (s, e) =>
            {
                if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0 || _laneNames.Count <= MaxLanesShown) { Wheel(e, _lanes); return; }
                _laneTop = Math.Max(0, Math.Min(_laneNames.Count - MaxLanesShown, _laneTop + (e.Delta > 0 ? -1 : 1)));
                e.Handled = true; DrawLanes();
            };
            LoopBracket.Changed += () => Dispatcher.BeginInvoke(new Action(DrawLanes));
            TakeKeys.Changed += () => Dispatcher.BeginInvoke(new Action(() => { DrawLanes(); ShowFx(); }));
            var holder = (Panel)_lanes.Parent;                                  // the effects strip right under the lanes
            holder.Children.Insert(holder.Children.IndexOf(_lanes) + 1, _fx);
            CamQueueControl.QueueChanged += () => DrawLanes();
            CamQueueControl.PovMarksChanged += () => DrawLanes();
        }

        /// <summary>A new film in the game: its demo file found again (the lanes show its rows), its loop gone.</summary>
        void NewFilm(long theater)
        {
            LoopBracket.Film(theater);
            _film = null; _filmFor = 0; _filmTries = 0; _zoomed = false; _laneTop = 0;
        }

        void FindFilm()
        {
            if (_theater == 0 || (_film != null && _filmFor == _theater)) return;
            if (_filmTries++ % 20 != 0) return;                         // every 5 s at most (the poll is 4 a second)
            string f = CamQueueControl.FilmQuiet();
            if (f != null) { _film = f; _filmFor = _theater; }
        }

        static bool SameFile(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>A cinematic row's markers file (beside its camera track), or null.</summary>
        static string MarkersFile(CamQueue q, QueueRow r)
        {
            if (q == null || string.IsNullOrEmpty(r.CamPath)) return null;
            string ct = System.IO.Path.IsPathRooted(r.CamPath) ? r.CamPath : System.IO.Path.Combine(q.Dir, r.CamPath);
            return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ct), System.IO.Path.GetFileName(ct).Split('.')[0] + ".markers.json");
        }

        static string TrackFile(CamQueue q, QueueRow r) { return System.IO.Path.IsPathRooted(r.CamPath) ? r.CamPath : System.IO.Path.Combine(q.Dir, r.CamPath); }

        int[] MarkTicks(CamQueue q, QueueRow r)
        {
            string mj = MarkersFile(q, r);
            if (mj == null || !File.Exists(mj)) return new int[0];
            DateTime t = File.GetLastWriteTimeUtc(mj);
            Tuple<DateTime, int[]> c;
            if (_markCache.TryGetValue(mj, out c) && c.Item1 == t) return c.Item2;
            int[] ticks;
            try { ticks = CamPaths.ReadMarkersJson(mj).Select(m => m.Tick).OrderBy(x => x).ToArray(); }
            catch { ticks = new int[0]; }
            _markCache[mj] = Tuple.Create(t, ticks);
            return ticks;
        }

        /// <summary>The lanes again from the queue: the POV lane(s), then a lane per cinematic of this film.</summary>
        void BuildItems()
        {
            _items.Clear(); _laneNames.Clear();
            FindFilm();
            CamQueue q = CamQueueControl.Queue;
            int pickedPov = CamQueueControl.Picked(false), pickedCine = CamQueueControl.Picked(true);
            var povs = new List<LaneItem>();
            int pin = CamQueueControl.PendingIn, pout = CamQueueControl.PendingOut;
            if (pin >= 0) povs.Add(new LaneItem { Pending = true, In = pin, Out = pout > pin ? pout : -1, Label = "POV in / out, not a take yet" });
            var cines = new List<LaneItem>();
            if (q != null && _film != null)
            {
                for (int i = 0; i < q.Rows.Count; i++)
                {
                    QueueRow r = q.Rows[i];
                    if (!SameFile(r.Demo, _film) || r.TickInValue < 0 || r.TickOutValue <= r.TickInValue) continue;
                    if (r.IsCine) cines.Add(new LaneItem { Row = i, Cine = true, In = r.TickInValue, Out = r.TickOutValue, Marks = MarkTicks(q, r), Picked = i == pickedCine, Label = (i + 1) + "  " + r.Name });
                    else povs.Add(new LaneItem { Row = i, In = r.TickInValue, Out = r.TickOutValue, Picked = i == pickedPov, Label = (i + 1) + "  " + r.Name + (r.Pov.Length > 0 ? "  (" + r.Pov + ")" : "") });
                }
            }
            // the POV lane: takes that overlap get a row each
            var ends = new List<int>();
            foreach (LaneItem it in povs.OrderBy(p => p.In))
            {
                int end = it.Out > it.In ? it.Out : it.In + 1;
                int lane = ends.FindIndex(e => e + 40 < it.In);
                if (lane < 0) { lane = ends.Count; ends.Add(end); } else ends[lane] = end;
                it.Lane = lane;
            }
            int povLanes = Math.Max(1, ends.Count);
            for (int i = 0; i < povLanes; i++) _laneNames.Add(i == 0 ? (_film == null && q != null && q.Rows.Count > 0 ? "POV  (this film's takes show once it is known: load it from Demos)" : "POV") : "");
            int l = povLanes;
            // the take picked: its keyframed parameters, a lane each, right under it
            int focus = PickedRow >= 0 ? PickedRow : (pickedCine >= 0 ? pickedCine : pickedPov);
            var keyItems = new List<LaneItem>();
            Action<int, string> keyLanes = (row, name) =>
            {
                foreach (KeyParam p in TakeKeys.Of(row))
                {
                    if (p.Keys.Count == 0) continue;
                    var it = new LaneItem { Row = row, IsKey = true, KeyLabel = p.Label, Marks = p.Keys.Select(k => k.Tick).ToArray(), Lane = l++, Label = "      " + p.Label };
                    it.In = it.Marks.Min(); it.Out = it.Marks.Max();
                    keyItems.Add(it); _laneNames.Add(it.Label);
                }
            };
            if (focus >= 0 && povs.Any(p => p.Row == focus)) keyLanes(focus, "");
            foreach (LaneItem c in cines)
            {
                c.Lane = l++; _laneNames.Add(c.Label);
                if (c.Row == focus) keyLanes(focus, c.Label);
            }
            _items.AddRange(povs); _items.AddRange(cines); _items.AddRange(keyItems);
            if (_laneTop > Math.Max(0, _laneNames.Count - MaxLanesShown)) _laneTop = Math.Max(0, _laneNames.Count - MaxLanesShown);
        }

        // ---------------------------------------------------------------- drawing

        static Rectangle Box(double x, double y, double w, double h, Brush fill, double opacity = 1)
        {
            var r = new Rectangle { Width = Math.Max(0, w), Height = Math.Max(0, h), Fill = fill, Opacity = opacity, IsHitTestVisible = false };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
            return r;
        }

        static TextBlock Words(string s, double x, double y, Brush b, double size = 10)
        {
            var t = new TextBlock { Text = s, Foreground = b, FontSize = size, IsHitTestVisible = false, FontFamily = new FontFamily("Segoe UI") };
            Canvas.SetLeft(t, x); Canvas.SetTop(t, y);
            return t;
        }

        void DrawLanes()
        {
            if (_drag < Drag.ItemIn) BuildItems();
            else
            {
                // mid-drag: the dragged item as it is now, the rest as built
                for (int i = 0; i < _items.Count; i++) if (_dragWas != null && Same(_items[i], _dragWas)) _items[i] = _dragItem;
            }
            DrawStrip();
            _lanes.Children.Clear();
            int shown = Math.Min(Math.Max(1, _laneNames.Count), MaxLanesShown);
            _lanes.Height = shown * LaneH;
            double w = _lanes.ActualWidth;
            if (_wholeButton != null) _wholeButton.Visibility = _zoomed ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < shown; i++)
            {
                int lane = _laneTop + i;
                _lanes.Children.Add(Box(0, i * LaneH, w, LaneH - 1, Ui.Brush("Inset"), lane % 2 == 0 ? 0.55 : 0.35));
                if (lane < _laneNames.Count && _laneNames[lane].Length > 0) _lanes.Children.Add(Words(_laneNames[lane], 4, i * LaneH, Ui.Brush("Dim"), 9.5));
            }
            if (_laneNames.Count > MaxLanesShown)
            {
                double h = shown * LaneH, part = h * MaxLanesShown / _laneNames.Count;
                _lanes.Children.Add(Box(w - 3, h * _laneTop / _laneNames.Count, 3, part, Ui.Brush("Dim"), 0.8));
            }
            if (w <= 0 || !Viewable) return;
            foreach (LaneItem it in _items)
            {
                int row = it.Lane - _laneTop;
                if (row < 0 || row >= shown) continue;
                double y = row * LaneH, x0 = X(it.In, w), x1 = it.Out > it.In ? X(it.Out, w) : x0;
                Brush c = it.Cine ? Ui.Brush("TlMark") : it.Pending ? Ui.Brush("TlTarget") : Ui.Brush("Good");
                bool hot = it.Picked || (_picked != null && Same(_picked, it));
                if (it.IsKey)
                {
                    Brush kc = Ui.Brush("Accent");
                    if (x1 > x0) _lanes.Children.Add(Box(x0, y + LaneH / 2.0 - 0.5, x1 - x0, 1, kc, 0.5));
                    foreach (int m in it.Marks)
                    {
                        double mx = X(m, w);
                        bool on = _picked != null && Same(_picked, it) && m == _pickedKey;
                        var dot = new Ellipse { Width = on ? 8 : 6, Height = on ? 8 : 6, Fill = on ? Ui.Brush("TlTarget") : kc, IsHitTestVisible = false };
                        Canvas.SetLeft(dot, mx - dot.Width / 2); Canvas.SetTop(dot, y + LaneH / 2.0 - dot.Height / 2 - 0.5);
                        _lanes.Children.Add(dot);
                    }
                    continue;
                }
                if (x1 > x0) _lanes.Children.Add(Box(x0, y + 2, x1 - x0, LaneH - 5, c, hot ? 0.55 : 0.28));
                if (it.Cine)
                {
                    foreach (int m in it.Marks)
                    {
                        double mx = X(m, w);
                        var d = new Rectangle { Width = 5, Height = 5, Fill = c, IsHitTestVisible = false, RenderTransform = new RotateTransform(45, 2.5, 2.5) };
                        Canvas.SetLeft(d, mx - 2.5); Canvas.SetTop(d, y + LaneH / 2.0 - 3);
                        _lanes.Children.Add(d);
                    }
                }
                else
                {
                    // the POV markers: an in flag and an out flag (dashed, the in / out not yet a take)
                    _lanes.Children.Add(Box(x0 - 1, y + 1, 2, LaneH - 3, c, it.Pending ? 0.9 : 1));
                    _lanes.Children.Add(Box(x0 - 1, y + 1, 5, 3, c));
                    if (it.Out > it.In) { _lanes.Children.Add(Box(x1 - 1, y + 1, 2, LaneH - 3, c, it.Pending ? 0.9 : 1)); _lanes.Children.Add(Box(x1 - 4, y + 1, 5, 3, c)); }
                }
                if (x1 - x0 > 46)
                {
                    var t = Words(it.Pending ? "POV in / out" : it.Label, Math.Max(x0 + 5, 2), y, Ui.Brush("Text"), 9.5);
                    t.MaxWidth = Math.Max(0, Math.Min(x1, w) - Math.Max(x0 + 5, 2) - 4); t.TextTrimming = TextTrimming.CharacterEllipsis;
                    _lanes.Children.Add(t);
                }
            }
            // the playhead and the snap guide across the lanes too
            double hx = X(_tick, w);
            if (hx >= 0 && hx <= w) _lanes.Children.Add(Box(hx - 0.5, 0, 1, shown * LaneH, Ui.Brush("TlHead"), 0.7));
            if (!double.IsNaN(_snapX)) _lanes.Children.Add(Box(_snapX - 0.5, 0, 1, shown * LaneH, Ui.Brush("TlTarget")));
        }

        void DrawStrip()
        {
            _strip.Children.Clear();
            double w = _strip.ActualWidth;
            if (_keysButton != null) _keysButton.Background = TakeKeys.Playing ? Ui.Brush("Accent") : null;
            if (_loopButton != null)
            {
                _loopButton.Background = LoopBracket.On ? Ui.Brush("Accent") : null;
                _loopButton.Content = LoopBracket.On ? "Loop on" : "Loop";
            }
            if (w <= 0 || !Viewable) return;
            _strip.Children.Add(Box(0, 17, w, 1, Ui.Brush("Line"), 0.6));
            if (LoopBracket.Has)
            {
                double x0 = X(LoopBracket.In, w), x1 = X(LoopBracket.Out, w);
                Brush c = LoopBracket.On ? Ui.Brush("Accent") : Ui.Brush("Dim");
                _strip.Children.Add(Box(x0, 2, Math.Max(1, x1 - x0), 13, c, LoopBracket.On ? 0.85 : 0.5));
                // Ableton's brace: the ends drop to the bar
                _strip.Children.Add(Box(x0, 2, 2, 16, c));
                _strip.Children.Add(Box(x1 - 2, 2, 2, 16, c));
                double secs = (LoopBracket.Out - LoopBracket.In) / 1000.0;
                string label = Clock(LoopBracket.In - _start) + " - " + Clock(LoopBracket.Out - _start) + "  (" + secs.ToString("0.0#", CultureInfo.InvariantCulture) + " s)" + (LoopBracket.On && LoopBracket.Loops > 0 ? "  loop " + LoopBracket.Loops : "");
                if (x1 - x0 > 150) _strip.Children.Add(Words(label, x0 + 6, 1, LoopBracket.On ? Ui.Brush("Back") : Ui.Brush("Text"), 9.5));
                else if (x1 < w - 170) _strip.Children.Add(Words(label, x1 + 5, 1, Ui.Brush("Dim"), 9.5));
            }
            double hx = X(_tick, w);
            if (hx >= 0 && hx <= w) _strip.Children.Add(Box(hx - 0.5, 0, 1, 18, Ui.Brush("TlHead"), 0.7));
            if (!double.IsNaN(_snapX)) _strip.Children.Add(Box(_snapX - 0.5, 0, 1, 18, Ui.Brush("TlTarget")));
        }

        static bool Same(LaneItem a, LaneItem b) { return a != null && b != null && a.Row == b.Row && a.Pending == b.Pending && a.Cine == b.Cine && a.IsKey == b.IsKey && a.KeyLabel == b.KeyLabel; }

        // ---------------------------------------------------------------- snapping

        /// <summary>Every place a drag may land on: the markers on the lanes, the game's dolly markers, the playhead, the
        /// loop's ends (not those of what is being dragged).</summary>
        List<int> SnapPoints(LaneItem except, bool loopToo)
        {
            var p = new List<int>();
            if (_tick != int.MinValue) p.Add(_tick);
            p.AddRange(_markTicks);
            if (loopToo && LoopBracket.Has) { p.Add(LoopBracket.In); p.Add(LoopBracket.Out); }
            foreach (LaneItem it in _items)
            {
                if (except != null && Same(it, except)) continue;
                p.Add(it.In);
                if (it.Out > it.In) p.Add(it.Out);
                p.AddRange(it.Marks);
            }
            return p;
        }

        /// <summary>A tick moved onto the nearest snap point within SnapPx, unless Alt is held. Sets the guide.</summary>
        int Snap(int t, List<int> points, double w)
        {
            _snapX = double.NaN;
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 || w <= 0) return t;
            double best = double.MaxValue, xt = X(t, w); int at = t;
            foreach (int p in points) { double d = Math.Abs(X(p, w) - xt); if (d < best) { best = d; at = p; } }
            if (best > SnapPx) return t;
            _snapX = X(at, w);
            return at;
        }

        /// <summary>A span moved by its grab point: snapped by whichever of its two ends is nearer a snap point.</summary>
        int SnapSpan(int a, int len, List<int> points, double w)
        {
            int sa = Snap(a, points, w); double ga = _snapX;
            int sb = Snap(a + len, points, w); double gb = _snapX;
            double da = double.IsNaN(ga) ? double.MaxValue : Math.Abs(X(sa, w) - X(a, w));
            double db = double.IsNaN(gb) ? double.MaxValue : Math.Abs(X(sb, w) - X(a + len, w));
            if (da == double.MaxValue && db == double.MaxValue) { _snapX = double.NaN; return a; }
            if (da <= db) { _snapX = ga; return sa; }
            _snapX = gb; return sb - len;
        }

        // ---------------------------------------------------------------- the loop strip

        void StripDown(MouseButtonEventArgs e)
        {
            if (StripPress(e.GetPosition(_strip).X, e.ClickCount)) { _strip.CaptureMouse(); e.Handled = true; }
        }

        /// <summary>A press on the strip at x: true when a drag starts.</summary>
        bool StripPress(double x, int clicks)
        {
            double w = _strip.ActualWidth;
            if (!Viewable || w <= 0) return false;
            bool has = LoopBracket.Has;
            double x0 = has ? X(LoopBracket.In, w) : 0, x1 = has ? X(LoopBracket.Out, w) : 0;
            if (has && clicks == 2 && x >= x0 - 4 && x <= x1 + 4) { LoopBracket.Switch(!LoopBracket.On); return false; }
            _dragA = LoopBracket.In; _dragB = LoopBracket.Out;
            _dragFromX = x; _dragMoved = false;
            if (has && Math.Abs(x - x0) <= 6) _drag = Drag.LoopIn;
            else if (has && Math.Abs(x - x1) <= 6) _drag = Drag.LoopOut;
            else if (has && x > x0 && x < x1) { _drag = Drag.LoopMove; _dragGrab = T(x, w) - LoopBracket.In; }
            else { _drag = Drag.LoopDraw; _dragGrab = Snap(T(x, w), SnapPoints(null, false), w); }
            return true;
        }

        void StripMove(MouseEventArgs e) { StripTo(e.GetPosition(_strip).X); }

        void StripTo(double x)
        {
            double w = _strip.ActualWidth;
            if (_drag == Drag.None || _drag >= Drag.ItemIn)
            {
                // the pointer says what a press would do
                if (!LoopBracket.Has || w <= 0) { _strip.Cursor = Cursors.IBeam; return; }
                double x0 = X(LoopBracket.In, w), x1 = X(LoopBracket.Out, w);
                _strip.Cursor = Math.Abs(x - x0) <= 6 || Math.Abs(x - x1) <= 6 ? Cursors.SizeWE : x > x0 && x < x1 ? Cursors.SizeAll : Cursors.IBeam;
                return;
            }
            if (Math.Abs(x - _dragFromX) >= 3) _dragMoved = true;
            if (!_dragMoved) return;
            int t = Math.Max(_start, Math.Min(_end, T(Math.Max(0, Math.Min(w, x)), w)));
            List<int> pts = SnapPoints(null, false);
            switch (_drag)
            {
                case Drag.LoopIn: { int a = Snap(t, pts, w); LoopBracket.Set(Math.Min(a, _dragB - 100), _dragB); break; }
                case Drag.LoopOut: { int b = Snap(t, pts, w); LoopBracket.Set(_dragA, Math.Max(b, _dragA + 100)); break; }
                case Drag.LoopMove: { int len = _dragB - _dragA, a = SnapSpan(t - _dragGrab, len, pts, w); LoopBracket.Set(a, a + len); break; }
                case Drag.LoopDraw: { int b = Snap(t, pts, w); LoopBracket.Set(_dragGrab, b); break; }
            }
            ShowLoopWords();
            DrawLanes();
        }

        void ShowLoopWords()
        {
            if (!LoopBracket.Has) return;
            _time.Text = "loop " + Clock(LoopBracket.In - _start) + " - " + Clock(LoopBracket.Out - _start) + (double.IsNaN(_snapX) ? "" : "  (snapped)");
        }

        void StripMenu()
        {
            var m = new ContextMenu();
            Action<string, bool, Action> add = (text, on, act) => { var i = new MenuItem { Header = text, IsEnabled = on }; i.Click += (s, e) => { try { act(); } catch (Exception ex) { App.Note("the loop", ex); } }; m.Items.Add(i); };
            CamQueue q = CamQueueControl.Queue;
            int pp = CamQueueControl.Picked(false), pc = CamQueueControl.Picked(true);
            add(LoopBracket.On ? "Loop off" : "Loop on", LoopBracket.Has, () => LoopBracket.Switch(!LoopBracket.On));
            add("Loop the picked POV take", q != null && pp >= 0, () => LoopRow(pp));
            add("Loop the picked cinematic", q != null && pc >= 0, () => LoopRow(pc));
            add("Loop the POV in / out not yet a take", CamQueueControl.PendingIn >= 0 && CamQueueControl.PendingOut > CamQueueControl.PendingIn, () => { LoopBracket.Set(CamQueueControl.PendingIn, CamQueueControl.PendingOut); LoopBracket.Switch(true); });
            add("Clear the loop", LoopBracket.Has, LoopBracket.Clear);
            m.IsOpen = true;
        }

        void LoopRow(int row)
        {
            CamQueue q = CamQueueControl.Queue;
            if (q == null || row < 0 || row >= q.Rows.Count) return;
            QueueRow r = q.Rows[row];
            if (r.TickInValue < 0 || r.TickOutValue <= r.TickInValue) return;
            LoopBracket.Set(r.TickInValue, r.TickOutValue);
            LoopBracket.Switch(true);
            _time.Text = "the loop is " + r.Name + "'s: the row does not change when the loop moves";
        }

        // ---------------------------------------------------------------- the lanes

        /// <summary>The item under a point, and which part: 1 its in, 2 its out, 3 its middle, 4 a marker (its index out).</summary>
        LaneItem HitItem(Point p, out int part, out int mark)
        {
            part = 0; mark = -1;
            double w = _lanes.ActualWidth;
            int lane = (int)(p.Y / LaneH) + _laneTop;
            LaneItem best = null;
            foreach (LaneItem it in _items)
            {
                if (it.Lane != lane) continue;
                if (it.IsKey)
                {
                    int near = -1; double nd = 5.01;
                    for (int k = 0; k < it.Marks.Length; k++) { double d = Math.Abs(X(it.Marks[k], w) - p.X); if (d < nd) { nd = d; near = k; } }
                    if (near >= 0) { part = 5; mark = near; return it; }
                    continue;
                }
                double x0 = X(it.In, w), x1 = it.Out > it.In ? X(it.Out, w) : x0;
                // a narrow item (zoomed out): its middle third still moves it; its markers wait for a zoom in (0731: a 3 s take
                // 9 px wide was all ends, and a cinematic's three markers 3 px apart)
                double edge = it.Out > it.In ? Math.Min(5, (x1 - x0) / 3) : 5;
                if (it.Cine && x1 - x0 >= 6 * it.Marks.Length)
                {
                    int near = -1; double nd = 4.01;
                    for (int k = 0; k < it.Marks.Length; k++) { double d = Math.Abs(X(it.Marks[k], w) - p.X); if (d < nd) { nd = d; near = k; } }
                    if (near >= 0) { part = 4; mark = near; return it; }
                }
                if (!it.Cine && Math.Abs(p.X - x0) <= edge) { part = 1; return it; }
                if (!it.Cine && it.Out > it.In && Math.Abs(p.X - x1) <= edge) { part = 2; return it; }
                if (p.X > x0 - 2 && p.X < x1 + 2) { part = 3; best = it; }
            }
            return best;
        }

        void LanesDown(MouseButtonEventArgs e)
        {
            _lanes.Focus();
            if (LanesPress(e.GetPosition(_lanes))) { _lanes.CaptureMouse(); e.Handled = true; }
        }

        /// <summary>A press on the lanes: true when a drag starts.</summary>
        bool LanesPress(Point p)
        {
            if (!Viewable) return false;
            int part, mark;
            LaneItem it = HitItem(p, out part, out mark);
            if (it == null) { _picked = null; DrawLanes(); return false; }
            _dragWas = it; _dragItem = it.Copy(); _dragFromX = p.X; _dragMoved = false;
            _drag = part == 1 ? Drag.ItemIn : part == 2 ? Drag.ItemOut : part == 4 ? Drag.ItemMark : part == 5 ? Drag.ItemKey : Drag.ItemMove;
            _dragMark = mark;
            _dragGrab = T(p.X, _lanes.ActualWidth) - it.In;
            return true;
        }

        void LanesMove(MouseEventArgs e) { LanesTo(e.GetPosition(_lanes)); }

        void LanesTo(Point p)
        {
            double w = _lanes.ActualWidth;
            if (_drag < Drag.ItemIn)
            {
                int part = 0, mark;
                LaneItem it = Viewable ? HitItem(p, out part, out mark) : null;
                _lanes.Cursor = it == null ? Cursors.Arrow : part == 3 ? Cursors.SizeAll : Cursors.SizeWE;
                return;
            }
            if (Math.Abs(p.X - _dragFromX) >= 3) _dragMoved = true;
            if (!_dragMoved) return;
            int t = Math.Max(_start, Math.Min(_end, T(Math.Max(0, Math.Min(w, p.X)), w)));
            List<int> pts = SnapPoints(_dragWas, true);
            LaneItem d = _dragItem, was = _dragWas;
            switch (_drag)
            {
                case Drag.ItemIn: d.In = Math.Min(Snap(t, pts, w), (d.Out > 0 ? d.Out : int.MaxValue) - 50); break;
                case Drag.ItemOut: d.Out = Math.Max(Snap(t, pts, w), d.In + 50); break;
                case Drag.ItemMove:
                    {
                        int len = (was.Out > was.In ? was.Out : was.In) - was.In;
                        int a = SnapSpan(t - _dragGrab, len, pts, w), shift = a - was.In;
                        d.In = was.In + shift; if (was.Out > was.In) d.Out = was.Out + shift;
                        for (int k = 0; k < d.Marks.Length; k++) d.Marks[k] = was.Marks[k] + shift;
                        break;
                    }
                case Drag.ItemKey: d.Marks[_dragMark] = Snap(t, pts, w); break;
                case Drag.ItemMark:
                    {
                        int k = _dragMark;
                        int lo = k > 0 ? was.Marks[k - 1] + 20 : int.MinValue, hi = k < was.Marks.Length - 1 ? was.Marks[k + 1] - 20 : int.MaxValue;
                        d.Marks[k] = Math.Max(lo, Math.Min(hi, Snap(t, pts, w)));
                        d.In = d.Marks.Min(); d.Out = d.Marks.Max();
                        break;
                    }
            }
            _time.Text = DragWords(d);
            DrawLanes();
        }

        string DragWords(LaneItem d)
        {
            if (d.IsKey) return d.KeyLabel + " key at " + Clock(d.Marks[_dragMark] - _start) + (double.IsNaN(_snapX) ? "" : "  (snapped)");
            string s = d.Cine ? (_drag == Drag.ItemMark ? "marker " + (_dragMark + 1) + " at " + Clock(d.Marks[_dragMark] - _start) : "cinematic " + Clock(d.In - _start) + " - " + Clock(d.Out - _start))
                              : "POV " + Clock(d.In - _start) + (d.Out > d.In ? " - " + Clock(d.Out - _start) : "");
            int moved = d.In - _dragWas.In;
            if (_drag == Drag.ItemMove && moved != 0) s += "  (" + (moved > 0 ? "+" : "") + (moved / 1000.0).ToString("0.000", CultureInfo.InvariantCulture) + " s)";
            return s + (double.IsNaN(_snapX) ? "" : "  (snapped)");
        }

        void EndDrag(UIElement from)
        {
            Drag d = _drag;
            if (d == Drag.None) return;
            _drag = Drag.None;
            _committing = d;
            if (from.IsMouseCaptured) from.ReleaseMouseCapture();
            _snapX = double.NaN;
            if (d >= Drag.ItemIn)
            {
                LaneItem it = _dragItem, was = _dragWas;
                _dragItem = _dragWas = null;
                _pickedMark = d == Drag.ItemMark ? _dragMark : -1;
                _pickedKey = d == Drag.ItemKey ? it.Marks[_dragMark] : int.MinValue;
                if (!_dragMoved)
                {
                    // a click: pick it (here, for the arrows, and on its page)
                    _picked = it;
                    if (it.Row >= 0) CamQueueControl.Pick(it.Row);
                    _time.Text = it.Pending ? "the POV in / out not yet a take: Add POV take on the POV Queue" : it.Label.Trim() + "  (Left / Right: a frame; Shift: a second)";
                }
                else if (d == Drag.ItemKey) _time.Text = TakeKeys.Edit(it.Row, it.KeyLabel, was.Marks[_dragMark], it.Marks[_dragMark], null);
                else _time.Text = Commit(it, was, d == Drag.ItemMark);
                _picked = it;
                ShowFx();
            }
            else if (_dragMoved) ShowLoopWords();
            DrawLanes();
        }

        void LanesKey(KeyEventArgs e)
        {
            if (_picked == null || (e.Key != Key.Left && e.Key != Key.Right)) return;
            int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 1000 : (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? 1 : 17;
            int by = e.Key == Key.Left ? -step : step;
            if (_picked.IsKey)
            {
                if (_pickedKey == int.MinValue) return;
                _time.Text = TakeKeys.Edit(_picked.Row, _picked.KeyLabel, _pickedKey, _pickedKey + by, null);
                _pickedKey += by;
                e.Handled = true;
                DrawLanes(); ShowFx();
                return;
            }
            LaneItem was = _picked, it = was.Copy();
            it.In += by; if (it.Out > 0) it.Out += by;
            for (int k = 0; k < it.Marks.Length; k++) it.Marks[k] += by;
            _time.Text = Commit(it, was, false);
            _picked = it;
            e.Handled = true;
            DrawLanes();
        }

        void LanesMenu(Point p)
        {
            if (!Viewable) return;
            int part, mark;
            LaneItem it = HitItem(p, out part, out mark);
            var m = new ContextMenu();
            Action<string, bool, Action> add = (text, on, act) => { var i = new MenuItem { Header = text, IsEnabled = on }; i.Click += (s, e) => { try { act(); } catch (Exception ex) { App.Note("the lanes", ex); } }; m.Items.Add(i); };
            CamQueue q = CamQueueControl.Queue;
            if (it == null)
            {
                int lane = (int)(p.Y / LaneH) + _laneTop;
                if (lane >= 0 && lane < _laneNames.Count && !_items.Any(x => x.Cine && x.Lane == lane))
                    add("POV in / out from the loop", LoopBracket.Has, () => CamQueueControl.SetPending(LoopBracket.In, LoopBracket.Out));
                else return;
            }
            else if (it.IsKey)
            {
                int kt = it.Marks[Math.Max(0, mark)];
                add("Delete this key", true, () => _time.Text = TakeKeys.Edit(it.Row, it.KeyLabel, kt, -1, null));
                add("Go to it", true, () => Seek(kt));
            }
            else if (it.Pending)
            {
                add("Add it as a POV take", it.Out > it.In && CamQueueControl.PovInstance != null, () => _time.Text = CamQueueControl.PovInstance.AddPovLive(null));
                add("Loop it", it.Out > it.In, () => { LoopBracket.Set(it.In, it.Out); LoopBracket.Switch(true); });
                add("Set it from the loop", LoopBracket.Has, () => CamQueueControl.SetPending(LoopBracket.In, LoopBracket.Out));
                add("Clear it", true, () => CamQueueControl.SetPending(-1, -1));
            }
            else if (!it.Cine)
            {
                add("Loop this take", true, () => LoopRow(it.Row));
                add("Set its in and out from the loop", LoopBracket.Has, () => { var c = it.Copy(); c.In = LoopBracket.In; c.Out = LoopBracket.Out; _time.Text = Commit(c, it, false); DrawLanes(); });
                add("Go to its start", true, () => Seek(it.In));
            }
            else
            {
                add("Loop this cinematic", true, () => LoopRow(it.Row));
                add("Preview it in the game's Dolly Camera, looping", CamQueueControl.Instance != null, () => _time.Text = CamQueueControl.Instance.PreviewInDolly(it.Row));
                add("Go to its start", true, () => Seek(it.In));
            }
            m.IsOpen = true;
        }

        // ---------------------------------------------------------------- the edits, into the queue's files

        /// <summary>An item dragged or nudged: its row changed in the queue (and its files, for a cinematic). The words say
        /// what happened.</summary>
        string Commit(LaneItem it, LaneItem was, bool retime)
        {
            if (it.Pending) { CamQueueControl.SetPending(it.In, it.Out); return "POV in / out moved (not a take yet)"; }
            CamQueue q = CamQueueControl.Queue;
            if (q == null || it.Row < 0 || it.Row >= q.Rows.Count) return "the row is gone";
            QueueRow r = q.Rows[it.Row];
            try
            {
                if (!it.Cine)
                {
                    if (it.Out <= it.In) return "a POV take needs its out after its in";
                    r.TickIn = it.In.ToString(CultureInfo.InvariantCulture); r.TickOut = it.Out.ToString(CultureInfo.InvariantCulture);
                    r.ClockIn = CamPaths.ClockLabel(it.In); r.ClockOut = CamPaths.ClockLabel(it.Out + 999);
                    if (it.In - was.In == it.Out - was.Out) TakeKeys.Shift(it.Row, it.In - was.In);     // moved whole: its keys go with it
                    CamQueueControl.Edited(it.Row);
                    return r.Name + ": " + Clock(it.In - _start) + " - " + Clock(it.Out - _start) + " (" + ((it.Out - it.In) / 1000.0).ToString("0.0##", CultureInfo.InvariantCulture) + " s)";
                }
                string why = LaneEdit.Cine(q, r, was.Marks, it.Marks, it.In - was.In, retime);
                if (!retime) TakeKeys.Shift(it.Row, it.In - was.In);
                _markCache.Clear();
                CamQueueControl.Edited(it.Row);
                return why;
            }
            catch (Exception ex) { App.Note("the lanes: an edit", ex); return "could not change " + r.Name + ": " + ex.Message; }
        }

        // ---------------------------------------------------------------- the effects strip (2026-10-18)

        TextBox Num(double v, double width = 58)
        {
            return new TextBox { Text = v.ToString("0.###", CultureInfo.InvariantCulture), Width = width, Margin = new Thickness(2, 0, 8, 2), VerticalContentAlignment = VerticalAlignment.Center, FontSize = 11 };
        }

        static Button TextBtn(string text, string tip, Action click)
        {
            Button b = Small(text, tip, click);
            b.FontFamily = new FontFamily("Segoe UI"); b.FontSize = 11; b.Margin = new Thickness(0, 0, 6, 2);
            return b;
        }

        static TextBlock Say(string s, Brush b = null) { return new TextBlock { Text = s, Foreground = b ?? Ui.Brush("Dim"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 2) }; }

        /// <summary>The picked key's values, or the picked cinematic's cameras (rotation and FOV), under the lanes.</summary>
        void ShowFx()
        {
            _fx.Children.Clear();
            _fx.Visibility = Visibility.Collapsed;
            LaneItem it = _picked;
            CamQueue q = CamQueueControl.Queue;
            if (it == null || it.Pending || q == null || it.Row < 0 || it.Row >= q.Rows.Count) return;
            QueueRow r = q.Rows[it.Row];
            int row = it.Row;
            if (it.IsKey)
            {
                KeyParam p = TakeKeys.Of(row).FirstOrDefault(x => x.Label == it.KeyLabel);
                KnobPanel.MoveKey k = p == null ? null : p.Keys.FirstOrDefault(x => x.Tick == _pickedKey);
                if (k == null) return;
                _fx.Children.Add(Say(p.Label + " on \"" + r.Name + "\", the key at " + Clock(k.Tick - _start) + ":", Ui.Brush("Text")));
                var boxes = new List<TextBox>();
                for (int j = 0; j < k.V.Length; j++)
                {
                    string part = p.Parts != null && j < p.Parts.Length && p.Parts[j].Length > 0 ? p.Parts[j] : (k.V.Length > 1 ? "value " + (j + 1) : "value");
                    _fx.Children.Add(Say(part));
                    TextBox b = Num(k.V[j]); boxes.Add(b); _fx.Children.Add(b);
                }
                int at = k.Tick; string label = p.Label;
                Action set = () =>
                {
                    var v = new double[boxes.Count];
                    for (int j = 0; j < v.Length; j++)
                        if (!double.TryParse(boxes[j].Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[j])) { _time.Text = "not a number: " + boxes[j].Text; return; }
                    _time.Text = TakeKeys.Edit(row, label, at, -1, v);
                };
                foreach (TextBox b in boxes) b.KeyDown += (s, e) => { if (e.Key == Key.Enter) set(); };
                _fx.Children.Add(TextBtn("Set", "These values for the key (Enter in a box does it too)", set));
                _fx.Children.Add(TextBtn("Delete key", null, () => { _time.Text = TakeKeys.Edit(row, label, at, -1, null); _pickedKey = int.MinValue; }));
                _fx.Children.Add(TextBtn("Go to it", "The film to this key", () => Seek(at)));
            }
            else if (it.Cine)
            {
                string mj = MarkersFile(q, r);
                if (mj == null || !File.Exists(mj)) { _fx.Children.Add(Say(r.Name + ": a generated path, no cameras to edit here")); _fx.Visibility = Visibility.Visible; return; }
                List<DollyMarker> ms;
                try { ms = CamPaths.ReadMarkersJson(mj).OrderBy(m => m.Tick).ToList(); } catch (Exception ex) { _fx.Children.Add(Say("could not read its cameras: " + ex.Message)); _fx.Visibility = Visibility.Visible; return; }
                _fx.Children.Add(Say("\"" + r.Name + "\", its cameras: rotation (pitch, yaw, roll) and FOV", Ui.Brush("Text")));
                var all = new List<TextBox[]>();
                for (int i = 0; i < ms.Count; i++)
                {
                    DollyMarker m = ms[i];
                    var group = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 2) };
                    if (i == _pickedMark) group.Background = Ui.Brush("Inset");
                    group.Children.Add(Say((i + 1) + "  " + Clock(m.Tick - _start), i == _pickedMark ? Ui.Brush("Accent") : null));
                    var bx = new[] { Num(m.Pitch, 50), Num(m.Yaw, 60), Num(m.Roll, 44), Num(m.Fov, 44) };
                    string[] names = { "pitch", "yaw", "roll", "FOV" };
                    for (int j = 0; j < 4; j++) { group.Children.Add(Say(names[j])); bx[j].Margin = new Thickness(0, 0, 4, 2); group.Children.Add(bx[j]); }
                    all.Add(bx); _fx.Children.Add(group);
                }
                Action setAll = () =>
                {
                    var vals = new double[all.Count][];
                    for (int i = 0; i < all.Count; i++)
                    {
                        vals[i] = new double[4];
                        for (int j = 0; j < 4; j++)
                            if (!double.TryParse(all[i][j].Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out vals[i][j])) { _time.Text = "camera " + (i + 1) + ": not a number: " + all[i][j].Text; return; }
                    }
                    try { _time.Text = LaneEdit.Cameras(q, r, vals); _markCache.Clear(); CamQueueControl.Edited(row); }
                    catch (Exception ex) { App.Note("the effects strip", ex); _time.Text = "could not change the cameras: " + ex.Message; }
                };
                foreach (TextBox[] bx in all) foreach (TextBox b in bx) b.KeyDown += (s, e) => { if (e.Key == Key.Enter) setAll(); };
                _fx.Children.Add(TextBtn("Set", "These rotations and FOVs for the path (Enter in a box does it too); its track is made again. Apply to game puts them in the game.", setAll));
            }
            else
            {
                int n = TakeKeys.Of(row).Count(p => p.Keys.Count > 0);
                _fx.Children.Add(Say("\"" + r.Name + "\": " + (n == 0 ? "no keyframes yet. Right-click any slider > Keyframe, with the film where the key goes." : n + " parameter(s) keyed, in the lanes under it."), Ui.Brush("Text")));
            }
            _fx.Visibility = Visibility.Visible;
        }

        // ---------------------------------------------------------------- for tests (the pipe's "console loop")

        /// <summary>What the strip and the lanes hold now, one line each.</summary>
        public string LanesText()
        {
            BuildItems();
            var sb = new System.Text.StringBuilder();
            sb.Append("film ").Append(_film ?? "(not known)").Append(", view ").Append(V0).Append('-').Append(V1).Append(_zoomed ? " zoomed" : "").Append(", playhead ").Append(_tick).Append('\n');
            sb.Append("loop ").Append(LoopBracket.Has ? LoopBracket.In + "-" + LoopBracket.Out : "none").Append(LoopBracket.On ? " ON" : " off").Append(", loops ").Append(LoopBracket.Loops).Append(", ").Append(LoopBracket.Said ?? "").Append('\n');
            for (int i = 0; i < _laneNames.Count; i++) sb.Append("lane ").Append(i).Append(": ").Append(_laneNames[i]).Append('\n');
            foreach (LaneItem it in _items)
                sb.Append(it.Cine ? "cine" : it.Pending ? "pending" : "pov").Append(" row ").Append(it.Row).Append(" lane ").Append(it.Lane).Append(' ').Append(it.In).Append('-').Append(it.Out)
                  .Append(it.Marks.Length > 0 ? " marks " + string.Join(",", it.Marks) : "").Append(it.Picked ? " picked" : "").Append("  ").Append(it.Label).Append('\n');
            return sb.ToString().TrimEnd();
        }

        /// <summary>The view from one film tick to another (tests; the wheel does it by hand). 0 0: the whole film.</summary>
        public string TestZoom(int a, int b)
        {
            if (a <= 0 && b <= 0) _zoomed = false;
            else if (b > a + 100) { _v0 = Math.Max(_start, a); _v1 = Math.Min(_end, b); _zoomed = true; }
            Draw();
            return "view " + V0 + "-" + V1 + (_zoomed ? " (zoomed)" : " (the whole film)");
        }

        /// <summary>A drag without a mouse, through the same code as the mouse's (tests): on the strip, or on a lane, from
        /// one film tick to another in steps. Snapping as the mouse's (no Alt).</summary>
        public string TestDrag(bool strip, int lane, int fromTick, int toTick)
        {
            if (!Viewable) return "no film on the timeline";
            BuildItems();
            FrameworkElement on = strip ? (FrameworkElement)_strip : _lanes;
            double w = on.ActualWidth;
            if (w <= 0) return "the timeline is not shown (the Workspace layout shows it)";
            double y = strip ? 8 : (lane - _laneTop) * LaneH + LaneH / 2.0;
            bool started = strip ? StripPress(X(fromTick, w), 1) : LanesPress(new Point(X(fromTick, w), y));
            if (!started) return "the press at " + fromTick + " started no drag";
            for (int i = 1; i <= 8; i++)
            {
                double x = X(fromTick + (toTick - fromTick) * i / 8.0, w);
                if (strip) StripTo(x); else LanesTo(new Point(x, y));
            }
            bool snapped = !double.IsNaN(_snapX);
            string during = _time.Text;
            EndDrag(on);
            return (snapped ? "snapped; " : "") + during + "\n" + _time.Text;
        }

        // ---------------------------------------------------------------- zoom

        void Wheel(MouseWheelEventArgs e, FrameworkElement over)
        {
            if (!Viewable) return;
            double w = over.ActualWidth;
            if (w <= 0) return;
            e.Handled = true;
            int a = V0, b = V1, span = b - a;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                if (!_zoomed) return;
                int by = (int)(span * 0.15) * (e.Delta > 0 ? -1 : 1);
                by = Math.Max(_start - a, Math.Min(_end - b, by));
                _v0 = a + by; _v1 = b + by;
            }
            else
            {
                double x = e.GetPosition(over).X;
                int at = T(x, w);
                double f = e.Delta > 0 ? 0.75 : 1 / 0.75;
                int ns = (int)Math.Max(400, Math.Min(_end - _start, span * f));
                int na = at - (int)(ns * x / w);
                na = Math.Max(_start, Math.Min(_end - ns, na));
                _v0 = na; _v1 = na + ns;
                _zoomed = ns < _end - _start;
            }
            Draw();
        }
    }

    /// <summary>A cinematic moved or retimed on the timeline, in its files: its markers (markers.json, each marker's time),
    /// its camera track (made again from the markers, as SAVE MY MARKERS makes it; a track with no markers is moved as it
    /// is), and its row's ticks.</summary>
    static class LaneEdit
    {
        public static string Cine(CamQueue q, QueueRow r, int[] marksWere, int[] marksNow, int shift, bool retime)
        {
            string ct = System.IO.Path.IsPathRooted(r.CamPath) ? r.CamPath : System.IO.Path.Combine(q.Dir, r.CamPath);
            string mj = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ct), System.IO.Path.GetFileName(ct).Split('.')[0] + ".markers.json");
            CamTrack track;
            string what;
            if (File.Exists(mj))
            {
                List<DollyMarker> ms = CamPaths.ReadMarkersJson(mj).OrderBy(m => m.Tick).ToList();
                if (retime)
                {
                    if (marksNow.Length != ms.Count) return "the markers changed under the drag: nothing moved";
                    for (int k = 0; k < ms.Count; k++) ms[k].Tick = marksNow[k];
                }
                else foreach (DollyMarker m in ms) m.Tick += shift;
                foreach (DollyMarker m in ms) if (m.Raw != null && m.Raw.Length >= 4) Array.Copy(BitConverter.GetBytes(m.Tick), 0, m.Raw, 0, 4);
                string demo, map;
                DemoAndMap(mj, out demo, out map);
                string warn;
                track = CamPaths.TrackFromMarkers(ms, 1, out warn);
                if (track == null) return "not moved: " + warn;
                CamPaths.WriteMarkersJson(mj, ms, demo, map);
                track.Write(ct, "from " + ms.Count + " theater markers (Catmull-Rom, 1 ms steps), " + System.IO.Path.GetFileName(demo ?? r.Demo) + ", " + (retime ? "retimed" : "moved") + " on the timeline");
                what = retime ? "retimed" : "moved";
            }
            else
            {
                if (retime) return "this path has no markers to retime (a generated one): move it whole";
                track = CamTrack.Read(ct);
                for (int i = 0; i < track.Ticks.Length; i++) track.Ticks[i] += shift;
                track.Write(ct, "moved " + shift + " ms on the timeline");
                what = "moved";
            }
            r.TickIn = track.First.ToString(CultureInfo.InvariantCulture); r.TickOut = track.Last.ToString(CultureInfo.InvariantCulture);
            r.ClockIn = CamPaths.ClockLabel(track.First); r.ClockOut = CamPaths.ClockLabel(track.Last + 999);
            return r.Name + " " + what + ": " + r.ClockIn + ", " + track.Seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s"
                   + (what == "moved" && shift != 0 ? " (" + (shift > 0 ? "+" : "") + (shift / 1000.0).ToString("0.000", CultureInfo.InvariantCulture) + " s)" : "");
        }

        /// <summary>A cinematic's cameras given their rotation and FOV (the effects strip): per marker pitch, yaw, roll, FOV.
        /// The look-at follows the new pitch and yaw; the track is made again from the markers.</summary>
        public static string Cameras(CamQueue q, QueueRow r, double[][] vals)
        {
            string ct = System.IO.Path.IsPathRooted(r.CamPath) ? r.CamPath : System.IO.Path.Combine(q.Dir, r.CamPath);
            string mj = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ct), System.IO.Path.GetFileName(ct).Split('.')[0] + ".markers.json");
            List<DollyMarker> ms = CamPaths.ReadMarkersJson(mj).OrderBy(m => m.Tick).ToList();
            if (vals.Length != ms.Count) return "the cameras changed meanwhile: nothing set";
            int changed = 0;
            for (int i = 0; i < ms.Count; i++)
            {
                DollyMarker m = ms[i]; double[] v = vals[i];
                if (Math.Abs(m.Pitch - v[0]) < 1e-4 && Math.Abs(m.Yaw - v[1]) < 1e-4 && Math.Abs(m.Roll - v[2]) < 1e-4 && Math.Abs(m.Fov - v[3]) < 1e-4) continue;
                m.Pitch = v[0]; m.Yaw = v[1]; m.Roll = v[2]; m.Fov = Math.Max(1, Math.Min(179, v[3]));
                double p = m.Pitch * Math.PI / 180, y = m.Yaw * Math.PI / 180;
                m.LX = m.X + 10 * Math.Cos(p) * Math.Cos(y); m.LY = m.Y + 10 * Math.Cos(p) * Math.Sin(y); m.LZ = m.Z - 10 * Math.Sin(p);
                changed++;
            }
            if (changed == 0) return r.Name + ": nothing changed";
            string demo, map, warn;
            DemoAndMap(mj, out demo, out map);
            CamTrack track = CamPaths.TrackFromMarkers(ms, 1, out warn);
            if (track == null) return "not set: " + warn;
            CamPaths.WriteMarkersJson(mj, ms, demo, map);
            track.Write(ct, "from " + ms.Count + " theater markers (Catmull-Rom, 1 ms steps), " + System.IO.Path.GetFileName(demo ?? r.Demo) + ", rotation / FOV set on the timeline");
            return r.Name + ": " + changed + " camera(s) set (rotation / FOV); Apply to game puts them in the game";
        }

        static void DemoAndMap(string mj, out string demo, out string map)
        {
            demo = map = "";
            try
            {
                var doc = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(mj));
                object v;
                if (doc.TryGetValue("demo", out v) && v != null) demo = v.ToString();
                if (doc.TryGetValue("map", out v) && v != null) map = v.ToString();
            }
            catch { }
        }
    }
}
