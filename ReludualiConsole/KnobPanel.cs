using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>A row of sliders driving one CFG line: "<name> {0} {1} {2}".</summary>
    public sealed class Knob
    {
        public string Label, Line, Tip;
        public string[] Parts;
        public double[] Values, Min, Max;
        public string ReadDvar;          // read back by "dvar <name>" (the numbers after name and type) ...
        public int FogField = -1;        // ... or from the game's fog record, Values.Length floats from here
        public bool Colour;              // three values with a swatch and the colour wheel
        public bool Whole;               // an int dvar: whole numbers only
        public Func<double[]> ReadWith;  // ... or read by this (2026-10-17: FOG's blend-mode rows, from the bridge's "fog status")
        // > 1: the slider's travel is curved (value = Min + (Max - Min) * position^Curve), so the low end, where a dvar
        // like r_zfar does all its work, gets most of the travel (Mycelium's round 1, 2026-10-15, Far clip: "There's a lot of
        // values but before nothing happens and then as soon as it gets to the very edge ... make this slider ... more fine")
        public double Curve = 1;
        internal Slider[] Sliders;
        internal TextBox[] Boxes;
        internal Border Swatch;
    }

    /// <summary>An on/off line: a CFG text for each state, read back from one dvar.</summary>
    public sealed class Switch
    {
        public string Label, On, Off, ReadDvar, Tip;
        public Func<double[]> ReadWith;  // read by this instead of a dvar: non-zero is on
        internal CheckBox Box;
    }

    /// <summary>
    /// The live panel under FOG, SUN, SSAO, DOF and LOOK (overgrow night, 2026-10-10). Mycelium, 2026-10-02: "sliders that
    /// have immediate effects in game, not only when they are released." Every move is sent as soon as the game has
    /// answered the last one - never a wait for the slider to rest; whatever moved meanwhile goes in the next send, so
    /// the newest value always wins and nothing queues up. The routes are UserCfg.Send's, measured on t6mpv43: plain
    /// dvars as console commands, cheat-protected ones by clearing the flag first, fog lines into the fog record.
    /// </summary>
    public class KnobPanel : UserControl
    {
        private readonly string _name, _bo2Key;
        private readonly List<object> _rows = new List<object>();     // Knob, Switch, or string (a heading)
        private readonly HashSet<Knob> _dirty = new HashSet<Knob>();
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(2, 2, 0, 4), TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("AccentPale") };
        private readonly ComboBox _presets = new ComboBox { MinWidth = 210, Margin = new Thickness(0, 0, 8, 6), ToolTip = "Your saved presets for this page (Presets folder beside the Designer)" };
        private readonly WrapPanel _extra = new WrapPanel();
        private string _intro;
        private bool _sending, _quiet, _built;
        private int _sends;
        private long _lastMs, _worstMs;
        private static readonly Dictionary<string, string> OwnFog = new Dictionary<string, string>();   // map -> fog record hex, as loaded

        /// <param name="name">The tab's name: its presets live in Presets\&lt;name&gt; beside the console.</param>
        /// <param name="bo2Key">The section of a settings file this panel takes ("fog", "sun", "ssao", "dof"), or null.</param>
        public KnobPanel(string name, string intro, string bo2Key)
        {
            _name = name; _bo2Key = bo2Key;
            lock (All) All.Add(this);
            Foreground = (Brush)Application.Current.FindResource("Text");
            if (!string.IsNullOrEmpty(intro)) _intro = intro;
            IsVisibleChanged += (s, e) =>
            {
                if (!(bool)e.NewValue) return;
                if (!_built) Build();
                ReadFromGame();
                ThreadPool.QueueUserWorkItem(_ => { try { DvarTable.Find("r_dof_tweak"); } catch { } });   // warm the table: the first protected move is then quick
            };
        }

        public KnobPanel Heading(string text) { _rows.Add(text); return this; }

        public KnobPanel Add(string label, string line, string read, double[] values, double[] min, double[] max, string[] parts = null, string tip = null, double curve = 1)
        {
            _rows.Add(new Knob { Label = label, Line = line, ReadDvar = read, Values = (double[])values.Clone(), Min = min, Max = max,
                                 Parts = parts ?? new string[values.Length].Select(_ => "").ToArray(), Tip = tip, Curve = curve });
            return this;
        }

        // a curved knob's slider runs 0..1 (its position); a straight one's runs Min..Max (the value itself)
        private static bool Curved(Knob k) { return k.Curve != 1 && !k.Whole; }

        private static double ValueAt(Knob k, int i, double pos)
        {
            if (!Curved(k)) return pos;
            return k.Min[i] + (k.Max[i] - k.Min[i]) * Math.Pow(Math.Max(0, Math.Min(1, pos)), k.Curve);
        }

        /// <summary>A value onto its slider (straight or curved); a value past the ends moves that end, as before.</summary>
        private static void PutValue(Knob k, int i, double v)
        {
            var s = k.Sliders[i];
            if (v < k.Min[i]) k.Min[i] = v;
            if (v > k.Max[i]) k.Max[i] = v;
            if (!Curved(k))
            {
                if (v < s.Minimum) s.Minimum = v;
                if (v > s.Maximum) s.Maximum = v;
                s.Value = v;
                return;
            }
            double span = k.Max[i] - k.Min[i];
            s.Value = span <= 0 ? 0 : Math.Pow((v - k.Min[i]) / span, 1 / k.Curve);
        }

        public KnobPanel Whole(string label, string line, string read, double value, double min, double max, string tip = null)
        {
            _rows.Add(new Knob { Label = label, Line = line, ReadDvar = read, Values = new[] { value }, Min = new[] { min }, Max = new[] { max },
                                 Parts = new[] { "" }, Whole = true, Tip = tip });
            return this;
        }

        /// <summary>Moves a knob's sliders from code (a button, a sync): sent live like a hand on the slider.</summary>
        public void Set(Knob k, double[] v)
        {
            if (!_built) Build();
            for (int i = 0; i < k.Sliders.Length && i < v.Length; i++) PutValue(k, i, v[i]);
        }

        public Knob Find(string label) { return _rows.OfType<Knob>().FirstOrDefault(k => k.Label == label); }

        /// <summary>A page's slider by the page's name and the slider's label, or null.</summary>
        public static Knob FindKnob(string page, string label)
        {
            KnobPanel p;
            lock (All) p = All.FirstOrDefault(x => string.Equals(x._name, page, StringComparison.OrdinalIgnoreCase));
            return p == null ? null : p.Find(label);
        }

        /// <summary>A slider of a page set from code and sent live, not recorded as a move (the take keys' live play). False
        /// when no page or slider has that name.</summary>
        public static bool SetLive(string page, string label, double[] v)
        {
            KnobPanel p;
            lock (All) p = All.FirstOrDefault(x => x._name == page);
            Knob k = p == null ? null : p.Find(label);
            if (k == null) return false;
            p._applying = true;
            try { p.Set(k, v); } finally { p._applying = false; }
            return true;
        }

        public KnobPanel Colour(string label, string line, string read, double[] values, double max, string tip = null)
        {
            _rows.Add(new Knob { Label = label, Line = line, ReadDvar = read, Values = (double[])values.Clone(), Min = new double[] { 0, 0, 0 },
                                 Max = new double[] { max, max, max }, Parts = new[] { "R", "G", "B" }, Colour = true, Tip = tip });
            return this;
        }

        public KnobPanel Fog(string label, string fogLine, int field, double[] values, double[] min, double[] max, bool colour = false, string tip = null)
        {
            string line = fogLine + " " + string.Join(" ", Enumerable.Range(0, values.Length).Select(i => "{" + i + "}"));
            _rows.Add(new Knob { Label = label, Line = line, FogField = field, Values = (double[])values.Clone(), Min = min, Max = max, Colour = colour,
                                 Parts = colour ? new[] { "R", "G", "B" } : new string[values.Length].Select(_ => "").ToArray(), Tip = tip });
            return this;
        }

        public KnobPanel Toggle(string label, string on, string off, string read, string tip = null)
        {
            _rows.Add(new Switch { Label = label, On = on, Off = off, ReadDvar = read, Tip = tip });
            return this;
        }

        /// <summary>The switch added last turns on by itself when any slider of the page moves by hand (DOF: the game ignores
        /// the values until its tweak is on, 2026-10-18).</summary>
        public KnobPanel SwitchedOnByMoves()
        {
            _onByMoves = _rows.LastOrDefault() as Switch;
            return this;
        }
        private Switch _onByMoves;

        /// <summary>Anything else the tab wants under the sliders (buttons of its own).</summary>
        public KnobPanel Below(UIElement e) { _extra.Children.Add(e); return this; }

        /// <summary>The row added last is read from the game by this (a worker thread; null = not known), not by a dvar.</summary>
        public KnobPanel ReadBy(Func<double[]> read)
        {
            object last = _rows.LastOrDefault();
            var k = last as Knob; if (k != null) k.ReadWith = read;
            var s = last as Switch; if (s != null) s.ReadWith = read;
            return this;
        }

        public IEnumerable<Knob> Knobs { get { return _rows.OfType<Knob>(); } }

        /// <summary>
        /// The page (2026-10-14 layout): a toolbar - presets first (pick, load, save), then files (open, copy as CFG), then the
        /// game (read what it holds, reset it to its own); the page's own quick buttons; a card per heading of sliders; the
        /// moves card at the bottom; the live status under it all.
        /// </summary>
        private void Build()
        {
            _built = true;
            var root = new StackPanel { Margin = new Thickness(12, 2, 12, 12) };
            if (_intro != null) root.Children.Add(Ui.Note(_intro, 0, 10));

            var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            top.Children.Add(new TextBlock { Text = "Preset", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6), Foreground = Ui.Brush("Dim") });
            top.Children.Add(_presets);
            top.Children.Add(Ui.Primary(Btn("Load", (s, e) => LoadPreset(), "Send the picked preset to the game")));
            // Save beside Save as (2026-10-18, Mycelium: "There should be a save as, but also a save as well")
            top.Children.Add(Btn("Save", (s, e) => SaveOver(), "These sliders over the picked preset (Save as... when none is picked)"));
            top.Children.Add(Btn("Save as...", (s, e) => SavePreset(), "These sliders as a preset of your own"));
            top.Children.Add(Ui.Gap());
            top.Children.Add(Btn("Open file...", (s, e) => Import(), "A .cfg file"));
            top.Children.Add(Btn("Copy as CFG", (s, e) => { Clipboard.SetText(AsCfg()); _status.Text = "Copied as CFG."; }, "These sliders as CFG lines, on the clipboard"));
            top.Children.Add(Ui.Gap());
            top.Children.Add(Btn("Read from game", (s, e) => ReadFromGame(), "Fill the sliders with what the game holds now (nothing is sent)"));
            top.Children.Add(Btn("Reset", (s, e) => ResetAll(), "Back to the game's own values (the fog record: back to the map's own)"));
            root.Children.Add(top);
            root.Children.Add(GlobalBar());
            if (_extra.Children.Count > 0) { _extra.Margin = new Thickness(0, 0, 0, 6); root.Children.Add(_extra); }
            ListPresets();

            // a card per heading; rows before the first heading get a card of their own
            Grid grid = null;
            int r = 0;
            Func<string, Grid> card = title =>
            {
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var c = Ui.Card(title, null, g);
                c.Padding = new Thickness(16, 11, 16, 9);
                root.Children.Add(c);
                r = 0;
                return g;
            };
            foreach (object row in _rows)
            {
                var h = row as string;
                if (h != null) { grid = card(h); continue; }
                if (grid == null) grid = card(null);
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var sw = row as Switch;
                if (sw != null)
                {
                    sw.Box = new CheckBox { Content = sw.Label, Margin = new Thickness(0, 5, 0, 5), ToolTip = sw.Tip };
                    Switch me = sw;
                    sw.Box.Checked += (s, e) => { if (!_quiet) SendNow(me.On); };
                    sw.Box.Unchecked += (s, e) => { if (!_quiet) SendNow(me.Off); };
                    Grid.SetRow(sw.Box, r); Grid.SetColumnSpan(sw.Box, 2); grid.Children.Add(sw.Box);
                }
                var k = row as Knob;
                if (k != null)
                {
                    var label = new TextBlock { Text = k.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 10, 4), ToolTip = k.Tip, TextWrapping = TextWrapping.Wrap };
                    Grid.SetRow(label, r); grid.Children.Add(label);
                    // one value: the slider stretches with the page (beside the game in the Workspace it is narrow); three: a row
                    Panel line;
                    if (k.Values.Length == 1)
                    {
                        var lg = new Grid { MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
                        lg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
                        lg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        line = lg;
                    }
                    else line = new WrapPanel();
                    BuildKnob(k, line);
                    Grid.SetRow(line, r); Grid.SetColumn(line, 1); grid.Children.Add(line);
                }
                r++;
            }

            // moves: slider moves recorded against the film's clock
            var moves = new WrapPanel();
            _recordBox.Checked += (s, e) => MovesOn(); _recordBox.Unchecked += (s, e) => MovesOn();
            _playBox.Checked += (s, e) => MovesOn(); _playBox.Unchecked += (s, e) => MovesOn();
            System.Windows.Automation.AutomationProperties.SetAutomationId(_recordBox, "RecordMoves");
            System.Windows.Automation.AutomationProperties.SetAutomationId(_playBox, "PlayMoves");
            System.Windows.Automation.AutomationProperties.SetAutomationId(_movesInfo, "MovesInfo");
            _recordBox.Margin = _playBox.Margin = new Thickness(0, 0, 8, 6);
            moves.Children.Add(_recordBox);
            moves.Children.Add(_playBox);
            moves.Children.Add(Btn("Timeline...", (s, e) => new TimelineWindow(this) { Owner = Window.GetWindow(this) }.Show(), "The moves as curves over the film's ticks: add, drag and remove keys"));
            moves.Children.Add(Ui.Gap());
            moves.Children.Add(Btn("Save moves...", (s, e) => SaveMoves()));
            moves.Children.Add(Btn("Load moves...", (s, e) => LoadMoves()));
            moves.Children.Add(Btn("Clear moves", (s, e) => { _tracks.Clear(); ShowMoves(); }));
            // 2026-10-18: the moves as the picked take's own keyframes, which the batch records with it (TakeKeys)
            moves.Children.Add(Btn("Give them to the take", (s, e) => { _status.Text = TakeKeys.FromMoves(TakeKeys.Target, _name, _tracks.ToList()); },
                "These moves become keyframes of the take picked (on the timeline, or on the Cam Queue / POV Queue): they show under its lane, and the batch records them with that take"));
            _movesInfo.Margin = new Thickness(4, 0, 0, 6);
            moves.Children.Add(_movesInfo);
            var movesCard = Ui.Card("Moves", "Record your slider moves against the film's own clock; Play moves replays them on the same ticks - while you watch, and while the batch records.", moves);
            movesCard.Padding = new Thickness(16, 11, 16, 7);
            root.Children.Add(movesCard);
            _movesTimer.Tick += (s, e) => PlayMoves();

            root.Children.Add(_status);
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private void BuildKnob(Knob k, Panel line)
        {
            int n = k.Values.Length;
            k.Sliders = new Slider[n]; k.Boxes = new TextBox[n];
            for (int i = 0; i < n; i++)
            {
                int at = i;
                if (k.Parts[i].Length > 0) line.Children.Add(new TextBlock { Text = k.Parts[i], Width = 24, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("Dim") });
                var s = new Slider { Minimum = k.Min[i], Maximum = k.Max[i], Value = Clamp(k, i, k.Values[i]), Width = n == 1 ? double.NaN : 150,
                                     VerticalAlignment = VerticalAlignment.Center, SmallChange = (k.Max[i] - k.Min[i]) / 200, LargeChange = (k.Max[i] - k.Min[i]) / 20 };
                if (k.Whole) { s.IsSnapToTickEnabled = true; s.TickFrequency = 1; s.SmallChange = 1; s.LargeChange = 1; }
                if (Curved(k))
                {
                    s.Minimum = 0; s.Maximum = 1; s.SmallChange = 0.005; s.LargeChange = 0.05;
                    double span = k.Max[i] - k.Min[i];
                    s.Value = span <= 0 ? 0 : Math.Pow((Clamp(k, i, k.Values[i]) - k.Min[i]) / span, 1 / k.Curve);
                }
                string id = k.Label + (n > 1 ? "." + (k.Parts[i].Length > 0 ? k.Parts[i] : i.ToString()) : "");
                System.Windows.Automation.AutomationProperties.SetAutomationId(s, id);
                System.Windows.Automation.AutomationProperties.SetName(s, id);
                var box = new TextBox { Width = 72, Margin = new Thickness(8, 2, 12, 2), Text = Fmt(k.Values[i]), VerticalContentAlignment = VerticalAlignment.Center };
                s.ValueChanged += (o, e) =>
                {
                    k.Values[at] = ValueAt(k, at, e.NewValue);
                    if (!box.IsKeyboardFocused) box.Text = Fmt(k.Values[at]);
                    Paint(k);
                    if (_quiet) return;
                    if (!_applying) Touched(k);
                    // a hand on a slider of a page that needs its switch on (DOF): on it goes, sent before the value
                    if (!_applying && _onByMoves != null && _onByMoves.Box != null && _onByMoves.Box.IsChecked != true) _onByMoves.Box.IsChecked = true;
                    _dirty.Add(k);
                    Kick();
                };
                // a typed value: Enter or leaving the box sends it (beyond the slider's range too, the game clamps to its own)
                Action typed = () =>
                {
                    double v;
                    if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { box.Text = Fmt(k.Values[at]); return; }
                    PutValue(k, at, v);
                };
                box.KeyDown += (o, e) => { if (e.Key == Key.Enter) typed(); };
                box.LostKeyboardFocus += (o, e) => typed();
                k.Sliders[i] = s; k.Boxes[i] = box;
                s.ContextMenu = box.ContextMenu = SliderMenu(k, at);              // Custom too: its sliders take keyframes (not "add as custom")
                if (line is Grid) { Grid.SetColumn(box, 1); s.MinWidth = 120; ((Grid)line).Width = double.NaN; }
                line.Children.Add(s); line.Children.Add(box);
            }
            if (k.Colour)
            {
                k.Swatch = new Border { Width = 30, Height = 22, CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1), BorderBrush = Ui.Brush("RimHi"), Margin = new Thickness(4, 0, 6, 0),
                                        Cursor = Cursors.Hand, ToolTip = "Pick on the colour wheel (live)" };
                k.Swatch.MouseLeftButtonUp += (o, e) => ColourWheel.Show(Window.GetWindow(this), k.Label, k.Values, k.Max[0], v =>
                {
                    for (int i = 0; i < 3; i++) { if (v[i] > k.Sliders[i].Maximum) k.Sliders[i].Maximum = v[i]; k.Sliders[i].Value = v[i]; }
                });
                line.Children.Add(k.Swatch);
                Paint(k);
            }
        }

        /// <summary>A slider's right-click (2026-10-18, Mycelium: "every time I see a slider, I should just be able to right-click ...
        /// add custom slider"): the same setting on the Custom page, where it takes keys and a MIDI fader.</summary>
        private ContextMenu SliderMenu(Knob k, int at)
        {
            var menu = new ContextMenu();
            // 2026-10-18 (Mycelium: "every single parameter can be keyframed"): a key on the picked take, at the film's time now
            var key = new MenuItem { Header = "Keyframe on the picked take" };
            menu.Opened += (s, e) =>
            {
                string name = TakeKeys.TargetName;
                key.Header = name == null ? "Keyframe: pick a take first (on the timeline or a queue page)" : "Keyframe on \"" + name + "\" at the film's time now";
                key.IsEnabled = name != null;
            };
            key.Click += (s, e) =>
            {
                int tick = -1;
                try { string st = BridgeClient.Ask("stats", 500, 1); if (st != null && !st.Contains("theater 0x00000000")) tick = (int)BridgeClient.Field(st, "tick"); } catch { }
                _status.Text = TakeKeys.Add(TakeKeys.Target, _name, k, tick);
            };
            menu.Items.Add(key);
            if (_name == "CUSTOM") return menu;
            menu.Items.Add(new Separator());
            var add = new MenuItem { Header = "Add as custom slider" };
            string dvar = null, why = null;
            string[] words = (k.Line ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (k.FogField >= 0 || k.Line.StartsWith("bridge", StringComparison.OrdinalIgnoreCase)) why = "this one is the Designer's own (the fog record), not a game setting";
            else if (k.Line.Contains(";")) why = "this slider sets more than one setting at once";
            else if (words.Length == 2 && words[1] == "{" + at + "}") dvar = words[0];
            else if (words.Length > 2 && k.Values.Length > 1) why = "a custom slider drives one number; this setting takes " + k.Values.Length;
            else why = "this slider is not one game setting";
            if (dvar == null) { add.IsEnabled = false; add.ToolTip = why; menu.Items.Add(new MenuItem { Header = "Add as custom slider: " + why, IsEnabled = false }); return menu; }
            add.Header = "Add as custom slider (" + dvar + ")";
            add.Click += (s, e) =>
            {
                if (CustomControl.Instance == null) { _status.Text = "The Custom page did not build."; return; }
                _status.Text = CustomControl.Instance.AddSetting(dvar, k.Label, k.Min[at], k.Max[at], k.Values[at]);
            };
            menu.Items.Add(add);
            return menu;
        }

        private static double Clamp(Knob k, int i, double v) { return Math.Max(k.Min[i], Math.Min(k.Max[i], v)); }

        private static void Paint(Knob k)
        {
            if (k.Swatch == null) return;
            double top = Math.Max(1e-6, Math.Max(k.Values[0], Math.Max(k.Values[1], k.Values[2])));
            Func<double, byte> c = x => (byte)Math.Max(0, Math.Min(255, Math.Round(255 * Math.Pow(Math.Max(0, x) / top, 1 / 2.2))));
            k.Swatch.Background = new SolidColorBrush(Color.FromRgb(c(k.Values[0]), c(k.Values[1]), c(k.Values[2])));
        }

        private static string Fmt(double v) { return v.ToString(Math.Abs(v) >= 100 ? "0.#" : "0.###", CultureInfo.InvariantCulture); }

        private static string LineOf(Knob k)
        {
            return string.Format(CultureInfo.InvariantCulture, k.Line, k.Values.Select(v => (object)(k.Whole ? Math.Round(v) : Math.Round(v, 4))).ToArray());
        }

        // ---------------------------------------------------------------- live sending

        /// <summary>Sends what moved, unless a send is out: then its completion sends again. The newest value always wins.</summary>
        private void Kick()
        {
            if (_sending || _dirty.Count == 0) return;
            _sending = true;
            string text = string.Join("\n", _dirty.Select(LineOf));
            _dirty.Clear();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var sw = Stopwatch.StartNew();
                string result;
                try { result = UserCfg.Send(text, n => true, Log); }
                catch (Exception ex) { App.Note(_name + " live send", ex); result = "failed: " + ex.Message; }
                long ms = sw.ElapsedMilliseconds;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _sending = false; _sends++; _lastMs = ms; _worstMs = Math.Max(_worstMs, ms);
                    _status.Text = "Live: " + text.Replace("\n", "  |  ") + "   (" + result + "; " + ms + " ms; " + _sends + " sends, slowest " + _worstMs + " ms)";
                    Kick();
                }));
            });
        }

        private void SendNow(string text, Action after = null)
        {
            _status.Text = "sending...";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string result;
                try { result = UserCfg.Send(text, n => true, Log); }
                catch (Exception ex) { App.Note(_name + " send", ex); result = "failed: " + ex.Message; }
                Dispatcher.BeginInvoke(new Action(() => { _status.Text = text.Replace("\n", "  |  ") + "  ->  " + result; if (after != null) after(); }));
            });
        }

        // ---------------------------------------------------------------- moves (overgrow night, 2026-10-10)
        // Mycelium, 2026-10-01: "Live Recording of the Slider Values into the recording itself so a user could do multiple passes
        // live." Record: every move by hand is stamped with the film's tick (RipperBridge's stats, read every 30 ms on a
        // worker). Play: as the film's clock passes the stamps, the sliders follow (straight lines between keys; before the
        // first key the first value, after the last the last) and send as usual - while watching, and while a batch records,
        // since the batch steps the same film clock. Both on = a second pass: knobs touched now are recorded over (keys
        // between this pass's touches are replaced), untouched ones play. Knobs only; switches are not recorded.

        public sealed class MoveKey { public int Tick; public double[] V; }
        private readonly Dictionary<Knob, List<MoveKey>> _tracks = new Dictionary<Knob, List<MoveKey>>();
        internal Dictionary<Knob, List<MoveKey>> Tracks { get { return _tracks; } }
        internal string PanelName { get { return _name; } }
        internal void TracksChanged() { ShowMoves(); }
        private readonly Dictionary<Knob, int> _lastRec = new Dictionary<Knob, int>();
        private readonly Dictionary<Knob, DateTime> _lastTouch = new Dictionary<Knob, DateTime>();
        private readonly System.Windows.Controls.Primitives.ToggleButton _recordBox = new System.Windows.Controls.Primitives.ToggleButton { Content = Ui.IconText(Glyph.Record, "Record moves"), Margin = new Thickness(0, 0, 8, 0) };
        private readonly System.Windows.Controls.Primitives.ToggleButton _playBox = new System.Windows.Controls.Primitives.ToggleButton { Content = Ui.IconText(Glyph.Play, "Play moves"), Margin = new Thickness(0, 0, 8, 0) };
        private readonly TextBlock _movesInfo = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("Dim") };
        private readonly DispatcherTimer _movesTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        private volatile int _tick = -1;
        private volatile bool _polling;
        private bool _applying;

        private bool Recording { get { return _recordBox.IsChecked == true; } }
        private bool Playing { get { return _playBox.IsChecked == true; } }

        private void MovesOn()
        {
            if (Recording || Playing)
            {
                _lastRec.Clear();
                if (!_polling)
                {
                    _polling = true;
                    var t = new Thread(() =>
                    {
                        while (_polling)
                        {
                            string st = null;
                            try { st = BridgeClient.Ask("stats", 300, 1); } catch { }
                            int tick = st != null && !st.Contains("theater 0x00000000") ? (int)BridgeClient.Field(st, "tick") : -1;
                            _tick = tick;
                            Thread.Sleep(30);
                        }
                    }) { IsBackground = true, Name = _name + " moves clock" };
                    t.Start();
                }
                _movesTimer.Start();
            }
            else { _polling = false; _movesTimer.Stop(); }
            ShowMoves();
        }

        /// <summary>A move by hand: recorded when recording, and it holds playback off this knob for a moment.</summary>
        private void Touched(Knob k)
        {
            _lastTouch[k] = DateTime.Now;
            int t = _tick;
            if (!Recording || t < 0) return;
            List<MoveKey> track;
            if (!_tracks.TryGetValue(k, out track)) _tracks[k] = track = new List<MoveKey>();
            int from;
            if (!_lastRec.TryGetValue(k, out from)) from = t - 1;
            track.RemoveAll(x => x.Tick > Math.Min(from, t) && x.Tick <= Math.Max(from, t));     // a second pass replaces what it covers
            track.Add(new MoveKey { Tick = t, V = (double[])k.Values.Clone() });
            track.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            _lastRec[k] = t;
            ShowMoves();
        }

        internal static double[] ValueAt(List<MoveKey> track, int tick)
        {
            if (track.Count == 0) return null;
            if (tick <= track[0].Tick) return track[0].V;
            if (tick >= track[track.Count - 1].Tick) return track[track.Count - 1].V;
            int i = track.FindIndex(x => x.Tick > tick);
            MoveKey a = track[i - 1], b = track[i];
            double f = (tick - a.Tick) / (double)Math.Max(1, b.Tick - a.Tick);
            return a.V.Select((v, j) => v + (b.V[j] - v) * f).ToArray();
        }

        /// <summary>True while the batch records with exact moves: RipperBridge applies them per frame, so the replay
        /// here stays quiet (it would land late and fight them). Cleared when the batch is no longer recording.</summary>
        internal static volatile bool ExactRecording;

        /// <summary>
        /// The playback engine, laid in (2026-10-17). Mycelium (10-17): "Keyframing Parameters in the Game itself ... seeing how
        /// the keyframes effect BO2 when played back". Every page's moves that play, written as RipperBridge events (dev40)
        /// for every frame of a recording, start to end by step: each frame's tick, then each moving slider's line at that
        /// tick, resolved the way a live move is sent (a command, a fog-record write, a bridge command). RipperBridge runs a
        /// tick's events when the recorder makes that frame, so a move lands on its own frame. Null when nothing plays.
        /// </summary>
        public static string BakeMoves(string takeKeys, int start, int end, int step, string file)
        {
            var work = new List<Tuple<string, bool, List<MoveKey>>>();       // a slider's line, whole numbers, its keys
            // the take's own keyframes (2026-10-18, TakeKeys): they win over a page's moves for the same slider
            if (!string.IsNullOrEmpty(takeKeys) && File.Exists(takeKeys))
                foreach (KeyParam p in TakeKeys.Read(takeKeys))
                    if (p.Keys.Count > 0) work.Add(Tuple.Create(p.Line, p.Whole, p.Keys));
            var taken = new HashSet<string>(work.Select(x => x.Item1));
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                List<KnobPanel> pages;
                lock (All) pages = All.ToList();
                foreach (KnobPanel p in pages)
                    if (p.Playing)
                        foreach (var kv in p._tracks)
                            if (kv.Value.Count > 0 && !taken.Contains(kv.Key.Line))
                                work.Add(Tuple.Create(kv.Key.Line, kv.Key.Whole, kv.Value.Select(x => new MoveKey { Tick = x.Tick, V = (double[])x.V.Clone() }).ToList()));
            }));
            if (work.Count == 0 || step <= 0 || end < start) return null;
            var sb = new StringBuilder();
            sb.Append("# reluduali-params 2: the Console's exact moves, every frame of ticks ").Append(start).Append('-').Append(end).Append(" by ").Append(step).Append(" ms\n");
            var resolved = new Dictionary<string, List<string>>();
            for (int t = start; t <= end + step; t += step)
                foreach (var w in work)
                {
                    double[] v = ValueAt(w.Item3, t);
                    if (v == null) continue;
                    string line = string.Format(CultureInfo.InvariantCulture, w.Item1, v.Select(x => (object)(w.Item2 ? Math.Round(x) : Math.Round(x, 4))).ToArray());
                    List<string> ev;
                    if (!resolved.TryGetValue(line, out ev)) resolved[line] = ev = UserCfg.Resolve(line, n => true);
                    foreach (string e in ev) sb.Append(t).Append(" !").Append(e).Append('\n');
                }
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
            return file;
        }

        private void PlayMoves()
        {
            if (ExactRecording && !BatchEngine.Recording) ExactRecording = false;
            if (!Playing || ExactRecording) return;
            int t = _tick;
            if (t < 0) return;
            foreach (var kv in _tracks)
            {
                DateTime last;
                if (_lastTouch.TryGetValue(kv.Key, out last) && (DateTime.Now - last).TotalMilliseconds < 400) continue;   // a hand on it now
                double[] v = ValueAt(kv.Value, t);
                if (v == null || v.Select((x, j) => Math.Abs(x - kv.Key.Values[j])).Max() < 1e-6) continue;
                _applying = true;
                try { Set(kv.Key, v); } finally { _applying = false; }
            }
        }

        private void ShowMoves()
        {
            int keys = _tracks.Values.Sum(x => x.Count);
            if (keys == 0) { _movesInfo.Text = Recording ? "recording: move a slider while the film plays" : "no moves"; return; }
            _movesInfo.Text = _tracks.Count(x => x.Value.Count > 0) + " knob(s), " + keys + " key(s), ticks " +
                              _tracks.Values.Where(x => x.Count > 0).Min(x => x[0].Tick) + "-" + _tracks.Values.Where(x => x.Count > 0).Max(x => x[x.Count - 1].Tick) +
                              (Recording ? "   (recording)" : "") + (Playing ? "   (playing)" : "");
        }

        private string MovesFolder { get { return Path.Combine(PresetFolder, "moves"); } }

        private void SaveMoves()
        {
            if (_tracks.Count == 0) { _status.Text = "No moves to save."; return; }
            string name = Ask.Text(Window.GetWindow(this), "Save moves", "Name:", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var data = _tracks.ToDictionary(kv => kv.Key.Label, kv => kv.Value.Select(x => new object[] { x.Tick, x.V }).ToList());
            try
            {
                Directory.CreateDirectory(MovesFolder);
                File.WriteAllText(Path.Combine(MovesFolder, name + ".moves.json"), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(data), new UTF8Encoding(false));
                _status.Text = "Saved moves as " + name + " in " + MovesFolder;
            }
            catch (Exception ex) { _status.Text = "Could not save the moves: " + ex.Message; }
        }

        private void LoadMoves()
        {
            Directory.CreateDirectory(MovesFolder);
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Moves (*.moves.json)|*.moves.json", InitialDirectory = MovesFolder };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                var data = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, System.Collections.ArrayList>>(File.ReadAllText(dlg.FileName));
                _tracks.Clear();
                foreach (var kv in data)
                {
                    Knob k = Find(kv.Key);
                    if (k == null) continue;
                    var list = new List<MoveKey>();
                    foreach (System.Collections.ArrayList pair in kv.Value)
                        list.Add(new MoveKey { Tick = Convert.ToInt32(pair[0], CultureInfo.InvariantCulture),
                                           V = ((System.Collections.ArrayList)pair[1]).Cast<object>().Select(o => Convert.ToDouble(o, CultureInfo.InvariantCulture)).ToArray() });
                    _tracks[k] = list.OrderBy(x => x.Tick).ToList();
                }
                ShowMoves();
                _status.Text = "Loaded " + Path.GetFileName(dlg.FileName) + ". Play moves to use them.";
            }
            catch (Exception ex) { _status.Text = "Could not read the moves: " + ex.Message; }
        }

        /// <summary>The pipe's "console moves PAGE ..." (tests in the sandbox, 2026-10-17): key LABEL TICK V[,V,V] (a key put
        /// straight on a slider's track) | play on|off | clear | text. PAGE is the tab's name without spaces (fog, look).</summary>
        public static string PipeMoves(string args)
        {
            const string usage = "usage: console moves PAGE key LABEL TICK V[,V,V] | play on|off | clear | text";
            string[] w = (args ?? "").Trim().Split(new[] { ' ' }, 3);
            if (w.Length < 2) return usage;
            KnobPanel p;
            lock (All) p = All.FirstOrDefault(x => string.Equals(x._name.Replace(" ", ""), w[0].Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
            if (p == null) return "no page " + w[0];
            if (!p._built) p.Build();
            string rest = w.Length > 2 ? w[2].Trim() : "";
            switch (w[1].ToLowerInvariant())
            {
                case "key":
                    {
                        string[] q = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        int tick;
                        if (q.Length < 3 || !int.TryParse(q[q.Length - 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out tick)) return usage;
                        Knob k = p.Find(string.Join(" ", q.Take(q.Length - 2)));
                        if (k == null) return "no slider " + string.Join(" ", q.Take(q.Length - 2)) + " on " + p._name;
                        double[] v;
                        try { v = q[q.Length - 1].Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray(); }
                        catch { return usage; }
                        if (v.Length != k.Values.Length) return k.Label + " takes " + k.Values.Length + " value(s)";
                        List<MoveKey> track;
                        if (!p._tracks.TryGetValue(k, out track)) p._tracks[k] = track = new List<MoveKey>();
                        track.RemoveAll(x => x.Tick == tick);
                        track.Add(new MoveKey { Tick = tick, V = v });
                        track.Sort((a, b) => a.Tick.CompareTo(b.Tick));
                        p.ShowMoves();
                        return "ok " + p.MovesText();
                    }
                case "play": p._playBox.IsChecked = rest.Equals("on", StringComparison.OrdinalIgnoreCase); return "play " + (p.Playing ? "on" : "off");
                case "clear": p._tracks.Clear(); p.ShowMoves(); return "cleared";
                case "text": return p.MovesText();
                default: return usage;
            }
        }

        /// <summary>For scripts and tests: the recorded moves as text.</summary>
        public string MovesText()
        {
            return string.Join("\n", _tracks.Select(kv => kv.Key.Label + ": " + string.Join(" ", kv.Value.Select(x => x.Tick + "=" + string.Join("/", x.V.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)))))));
        }

        // ---------------------------------------------------------------- reading the game

        private static double[] Numbers(string reply)
        {
            // "r_exposureValue float 3" / "r_lightTweakSunColor linearRGB 1 1 0": the numbers after name and type
            if (reply == null || reply.StartsWith("error", StringComparison.Ordinal) || reply.StartsWith("(", StringComparison.Ordinal)) return null;
            string[] p = reply.Split(' ');
            var v = new List<double>();
            for (int i = 2; i < p.Length; i++) { double d; if (double.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) v.Add(d); }
            return v.Count > 0 ? v.ToArray() : null;
        }

        private static float[] ReadFog(out string hex)
        {
            hex = BridgeClient.Ask("read " + BatchRecorder.Fog.Address.ToString("X") + " 64", 600, 1);
            if (hex == null || hex.Length != 128) return null;
            var f = new float[16];
            for (int i = 0; i < 16; i++)
            {
                var b = new byte[4];
                for (int j = 0; j < 4; j++) b[j] = Convert.ToByte(hex.Substring(i * 8 + j * 2, 2), 16);
                f[i] = BitConverter.ToSingle(b, 0);
            }
            return f;
        }

        /// <summary>Fills every row with what the game holds now, sending nothing back.</summary>
        public void ReadFromGame()
        {
            if (!_built) return;
            var knobs = _rows.OfType<Knob>().ToList();
            var switches = _rows.OfType<Switch>().ToList();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var got = new Dictionary<object, double[]>();
                bool any = false;
                try
                {
                    float[] fog = null; string fogHex = null;
                    if (knobs.Any(k => k.FogField >= 0))
                    {
                        fog = ReadFog(out fogHex);
                        string map = Looks.CurrentMap();
                        if (fog != null && map != null) lock (OwnFog) if (!OwnFog.ContainsKey(map)) OwnFog[map] = fogHex;
                    }
                    foreach (Knob k in knobs)
                    {
                        double[] v = null;
                        if (k.FogField >= 0 && fog != null) v = Enumerable.Range(0, k.Values.Length).Select(i => (double)fog[k.FogField + i]).ToArray();
                        else if (k.ReadWith != null) v = k.ReadWith();
                        else if (k.ReadDvar != null) v = Numbers(BridgeClient.Ask("dvar " + k.ReadDvar, 600, 1));
                        if (v != null) { got[k] = v; any = true; }
                    }
                    foreach (Switch s in switches)
                    {
                        if (s.ReadWith != null) { double[] r = s.ReadWith(); if (r != null && r.Length > 0) { got[s] = r; any = true; } continue; }
                        if (s.ReadDvar == null) continue;
                        double[] v = Numbers(BridgeClient.Ask("dvar " + s.ReadDvar, 600, 1));
                        if (v != null) { got[s] = v; any = true; }
                    }
                }
                catch (Exception ex) { App.Note(_name + " read from game", ex); }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!any) { _status.Text = "Not connected to the game: the sliders show their last values."; return; }
                    _quiet = true;
                    try
                    {
                        foreach (var kv in got)
                        {
                            var k = kv.Key as Knob;
                            if (k != null)
                                for (int i = 0; i < k.Sliders.Length && i < kv.Value.Length; i++)
                                {
                                    double v = kv.Value[i];
                                    PutValue(k, i, v); k.Values[i] = v; k.Boxes[i].Text = Fmt(v);
                                }
                            var s = kv.Key as Switch;
                            if (s != null)
                            {
                                // on = the game holds the value the ON line sets ("No gun": on when cg_drawGun is 0)
                                double? want = ValueIn(s.On, s.ReadDvar);
                                s.Box.IsChecked = want.HasValue ? Math.Abs(kv.Value[0] - want.Value) < 1e-6 : kv.Value[0] != 0;
                            }
                        }
                    }
                    finally { _quiet = false; }
                    _status.Text = "Read from the game (nothing sent).";
                }));
            });
        }

        /// <summary>The number a CFG line sets for one dvar ("cg_drawGun 0; x 1", "cg_drawGun" -> 0), or null.</summary>
        private static double? ValueIn(string line, string dvar)
        {
            if (line == null || dvar == null) return null;
            foreach (string part in line.Split(';'))
            {
                string[] w = part.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                double d;
                if (w.Length >= 2 && w[0].Equals(dvar, StringComparison.OrdinalIgnoreCase) && double.TryParse(w[1], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            }
            return null;
        }

        // ---------------------------------------------------------------- reset, presets, files

        private static IEnumerable<string> DvarsIn(string line)
        {
            foreach (string part in line.Split(';'))
            {
                string c = part.Trim();
                int sp = c.IndexOf(' ');
                string name = sp > 0 ? c.Substring(0, sp) : c;
                if (name.Equals("bridge", StringComparison.OrdinalIgnoreCase)) continue;   // RipperBridge's own (FOG's blend modes): not a dvar
                if (name.Length > 0 && BatchRecorder.Fog.FieldOf(name) < 0) yield return name;
            }
        }

        private void ResetAll()
        {
            var dvars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object row in _rows)
            {
                var k = row as Knob; if (k != null) foreach (string d in DvarsIn(k.Line)) dvars.Add(d);
                var s = row as Switch; if (s != null) { foreach (string d in DvarsIn(s.On)) dvars.Add(d); foreach (string d in DvarsIn(s.Off)) dvars.Add(d); }
            }
            bool fog = _rows.OfType<Knob>().Any(k => k.FogField >= 0);
            // FOG's blend-mode rows are RipperBridge's own fog: its reset is that fog taken away (and unlocked)
            bool ours = _rows.OfType<Knob>().Any(k => k.Line.StartsWith("bridge fog", StringComparison.OrdinalIgnoreCase));
            _status.Text = "resetting...";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string note = "";
                try
                {
                    foreach (string d in dvars)
                    {
                        if (ProtectedDvars.Contains(d)) DvarTable.Unprotect(d);
                        BridgeClient.Ask("cmd reset " + d, 1000, 2);
                    }
                    if (fog)
                    {
                        string map = Looks.CurrentMap(), own = null;
                        if (map != null) lock (OwnFog) OwnFog.TryGetValue(map, out own);
                        if (own != null) note = BridgeClient.Ask("write " + BatchRecorder.Fog.Address.ToString("X") + " " + own, 1000, 2) == "ok" ? "; the map's own fog back" : "; the fog could not be written";
                        else note = "; the map's own fog is not known (read it before changing it, or reload the film)";
                    }
                    if (ours)
                    {
                        string a = BridgeClient.Ask("fog off", 1000, 2), b = BridgeClient.Ask("fog lock off", 1000, 2);
                        note += a != null && a.StartsWith("fog on", StringComparison.Ordinal) && b != null ? "; the blend-mode fog off and unlocked" : "; the blend-mode fog could not be reached (" + (a ?? BridgeClient.LastError) + ")";
                    }
                }
                catch (Exception ex) { App.Note(_name + " reset", ex); note = "; failed: " + ex.Message; }
                Dispatcher.BeginInvoke(new Action(() => { _status.Text = "Reset " + dvars.Count + " dvars to the game's defaults" + note + "."; ReadFromGame(); }));
            });
        }

        public string AsCfg()
        {
            var sb = new StringBuilder("// Reluduali " + _name + ", " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\r\n");
            foreach (object row in _rows)
            {
                var k = row as Knob;
                if (k != null) foreach (string part in LineOf(k).Split(';')) sb.Append(part.Trim()).Append("\r\n");
                var s = row as Switch;
                if (s != null) foreach (string part in ((s.Box != null && s.Box.IsChecked == true) ? s.On : s.Off).Split(';')) sb.Append(part.Trim()).Append("\r\n");
            }
            return sb.ToString();
        }

        private string PresetFolder { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Presets", _name); } }

        private void ListPresets()
        {
            _presets.Items.Clear();
            try
            {
                if (Directory.Exists(PresetFolder))
                    foreach (string f in Directory.GetFiles(PresetFolder).Where(f => f.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f))
                        _presets.Items.Add(Path.GetFileName(f));
            }
            catch (Exception ex) { App.Note("listing " + _name + " presets", ex); }
        }

        private void LoadPreset()
        {
            string f = _presets.SelectedItem as string;
            if (f == null) { _status.Text = "Pick a preset first."; return; }
            SendFile(Path.Combine(PresetFolder, f));
        }

        private void SaveOver()
        {
            string f = _presets.SelectedItem as string;
            if (f == null) { SavePreset(); return; }
            try
            {
                string path = Path.Combine(PresetFolder, f);
                File.WriteAllText(path, AsCfg(), new UTF8Encoding(false));
                _status.Text = "Saved over " + f + ".";
            }
            catch (Exception ex) { App.Note("saving a preset", ex); _status.Text = "Could not save: " + ex.Message; }
        }

        private void SavePreset()
        {
            string name = Ask.Text(Window.GetWindow(this), "Save " + _name + " preset", "Name:", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            try
            {
                Directory.CreateDirectory(PresetFolder);
                File.WriteAllText(Path.Combine(PresetFolder, name + ".cfg"), AsCfg(), new UTF8Encoding(false));
                ListPresets();
                _presets.SelectedItem = name + ".cfg";
                _status.Text = "Saved " + name + ".cfg in " + PresetFolder;
            }
            catch (Exception ex) { App.Note("saving a preset", ex); _status.Text = "Could not save: " + ex.Message; }
        }

        // ---------------------------------------------------------------- global presets (2026-10-16)
        // Mycelium, 2026-10-15: "when I save slider settings they're sent to a global reluduali preset because having all the
        // panels with their own preset saving can sort of be overwhelming. Global Presets were something I deeply missed about
        // the V3 console because it did them very well." And 10-16: "Global Presets is a Must." One file per look,
        // Presets\GLOBAL\<name>.cfg beside the Console, with a section per page ("// == FOG ==" and that page's lines). Every
        // page shows the same picked look: Save this page puts its section in (replacing the old one), Load sends every section
        // to its page, one after the other, and each page then reads the game back into its sliders.

        private static readonly List<KnobPanel> All = new List<KnobPanel>();
        private static string _globalPicked;
        private static event Action GlobalsChanged;

        // ---- for the CFG page's Global CFG view (2026-10-18) ----
        public static string GlobalsFolder { get { return GlobalFolder; } }
        /// <summary>The Global CFG every page shows (null: none picked); set, every page follows.</summary>
        public static string PickedGlobal
        {
            get { return _globalPicked; }
            set { _globalPicked = value; if (GlobalsChanged != null) GlobalsChanged(); }
        }
        public static event Action PickedGlobalChanged { add { GlobalsChanged += value; } remove { GlobalsChanged -= value; } }
        public static Dictionary<string, string> ReadGlobalFile(string file) { return ReadGlobal(file); }
        public static void WriteGlobalFile(string file, Dictionary<string, string> sections) { WriteGlobal(file, sections); if (GlobalsChanged != null) GlobalsChanged(); }
        /// <summary>Sends the picked Global CFG to every page it holds (Load everywhere), from any page that is there.</summary>
        public static string LoadPickedEverywhere()
        {
            KnobPanel first;
            lock (All) first = All.FirstOrDefault();
            if (first == null) return "no look pages";
            first.LoadGlobal();
            return first._status.Text;
        }
        private Action _onGlobals;
        private ComboBox _globals;

        /// <summary>A panel put away for good (the Custom page builds a new one at every change): out of the pages the global
        /// looks reach, and off their change list (the code review's P-2: the old ones piled up, and New global look then
        /// met two CUSTOM pages and failed).</summary>
        public void Retire()
        {
            lock (All) All.Remove(this);
            if (_onGlobals != null) { GlobalsChanged -= _onGlobals; _onGlobals = null; }
        }
        private static string GlobalFolder { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Presets", "GLOBAL"); } }
        private const string SectionMark = "// == ";

        private UIElement GlobalBar()
        {
            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            // the words (2026-10-18, Mycelium: "Send SSAO to global CFG. Because that just makes more sense ... less vague than send
            // this page to it")
            bar.Children.Add(new TextBlock { Text = "Global CFG", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6), Foreground = Ui.Brush("Dim"),
                                             ToolTip = "One look for every page: Look, Fog, Sun, SSAO, DOF, Sky, Theater and Custom in one preset" });
            _globals = new ComboBox { MinWidth = 210, Margin = new Thickness(0, 0, 8, 6), ToolTip = "Your global looks (Presets\\GLOBAL beside the Designer)" };
            _globals.SelectionChanged += (s, e) =>
            {
                var picked = _globals.SelectedItem as string;
                if (picked == null || picked == _globalPicked) return;
                _globalPicked = picked;
                if (GlobalsChanged != null) GlobalsChanged();
            };
            bar.Children.Add(_globals);
            bar.Children.Add(Ui.Primary(Btn("Load everywhere", (s, e) => LoadGlobal(), "Send the picked global look to every page it holds")));
            bar.Children.Add(Btn("Send " + _name + " to Global CFG", (s, e) => SaveToGlobal(_globalPicked), "Put this page's sliders into the picked Global CFG (its old " + _name + " part is replaced)"));
            bar.Children.Add(Btn("New Global CFG...", (s, e) => NewGlobal(), "A new Global CFG from every page you have opened"));
            _onGlobals = () => Dispatcher.BeginInvoke(new Action(ListGlobals));
            GlobalsChanged += _onGlobals;
            ListGlobals();
            return bar;
        }

        private void ListGlobals()
        {
            if (_globals == null) return;
            _globals.Items.Clear();
            try
            {
                if (Directory.Exists(GlobalFolder))
                    foreach (string f in Directory.GetFiles(GlobalFolder, "*.cfg").OrderBy(f => f)) _globals.Items.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch (Exception ex) { App.Note("listing the global looks", ex); }
            if (_globalPicked != null && _globals.Items.Contains(_globalPicked)) _globals.SelectedItem = _globalPicked;
        }

        /// <summary>A global look's sections: page name -> its lines.</summary>
        private static Dictionary<string, string> ReadGlobal(string file)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(file)) return d;
            string page = null;
            var sb = new StringBuilder();
            foreach (string l in File.ReadAllLines(file))
            {
                if (l.StartsWith(SectionMark, StringComparison.Ordinal) && l.TrimEnd().EndsWith("==", StringComparison.Ordinal))
                {
                    if (page != null) d[page] = sb.ToString();
                    page = l.Substring(SectionMark.Length).TrimEnd().TrimEnd('=').Trim();
                    sb.Clear();
                }
                else if (page != null) sb.Append(l).Append("\r\n");
            }
            if (page != null) d[page] = sb.ToString();
            return d;
        }

        private static void WriteGlobal(string file, Dictionary<string, string> sections)
        {
            var sb = new StringBuilder("// Reluduali global look, saved " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\r\n");
            foreach (var kv in sections.OrderBy(kv => kv.Key)) sb.Append(SectionMark).Append(kv.Key).Append(" ==\r\n").Append(kv.Value.TrimEnd()).Append("\r\n");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        }

        private void SaveToGlobal(string name)
        {
            if (name == null) { _status.Text = "Pick a Global CFG first, or make one with New Global CFG."; return; }
            try
            {
                string file = Path.Combine(GlobalFolder, name + ".cfg");
                var sections = ReadGlobal(file);
                sections[_name] = AsCfg();
                WriteGlobal(file, sections);
                _status.Text = "Saved " + _name + " into the global look \"" + name + "\" (it holds " + string.Join(", ", sections.Keys.OrderBy(k => k)) + ").";
            }
            catch (Exception ex) { App.Note("saving to a global look", ex); _status.Text = "Could not save: " + ex.Message; }
        }

        private void NewGlobal()
        {
            string name = Ask.Text(Window.GetWindow(this), "New global look", "Name:", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            NewGlobalNamed(name);
        }

        /// <summary>The pipe's "console global ..." (tests in the sandbox): read PAGE | save NAME PAGE | new NAME | load NAME |
        /// value PAGE LABEL | show NAME.</summary>
        public static string PipeGlobal(string args)
        {
            string[] w = (args ?? "").Trim().Split(new[] { ' ' }, 2);
            string cmd = w[0].ToLowerInvariant(), rest = w.Length > 1 ? w[1].Trim() : "";
            List<KnobPanel> pages;
            lock (All) pages = All.ToList();
            Func<string, KnobPanel> page = n => pages.FirstOrDefault(p => string.Equals(p._name, n, StringComparison.OrdinalIgnoreCase));
            KnobPanel first = pages.FirstOrDefault();
            if (first == null) return "no pages";
            switch (cmd)
            {
                case "read": { var p = page(rest); if (p == null) return "no page " + rest; if (!p._built) p.Build(); p.ReadFromGame(); return "reading " + p._name; }
                case "save":
                    {
                        int sp = rest.LastIndexOf(' ');
                        var p = sp > 0 ? page(rest.Substring(sp + 1)) : null;
                        if (p == null) return "usage: console global save NAME PAGE";
                        if (!p._built) p.Build();
                        p.SaveToGlobal(rest.Substring(0, sp).Trim());
                        return p._status.Text;
                    }
                case "new": first.NewGlobalNamed(rest); return first._status.Text;
                case "load": _globalPicked = rest; first.LoadGlobal(); return "loading the global look \"" + rest + "\" (" + first._status.Text + ")";
                case "status": return first._status.Text;
                case "value":
                    {
                        int sp = rest.IndexOf(' ');
                        var p = sp > 0 ? page(rest.Substring(0, sp)) : null;
                        Knob k = p == null ? null : p.Find(rest.Substring(sp + 1).Trim());
                        if (k == null) return "usage: console global value PAGE LABEL";
                        if (!p._built) p.Build();
                        return string.Join(" ", Enumerable.Range(0, k.Sliders.Length).Select(i => ValueAt(k, i, k.Sliders[i].Value).ToString("0.###", CultureInfo.InvariantCulture)));
                    }
                case "show": { string f = Path.Combine(GlobalFolder, rest + ".cfg"); return File.Exists(f) ? File.ReadAllText(f) : "no such global look"; }
                default: return "console global read PAGE | save NAME PAGE | new NAME | load NAME | status | value PAGE LABEL | show NAME";
            }
        }

        private void NewGlobalNamed(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            List<KnobPanel> pages;
            lock (All) pages = All.ToList();
            var opened = pages.Where(p => p._built).ToList();
            var notYet = pages.Where(p => !p._built).Select(p => p._name).ToList();
            try
            {
                WriteGlobal(Path.Combine(GlobalFolder, name + ".cfg"), opened.ToDictionary(p => p._name, p => p.AsCfg(), StringComparer.OrdinalIgnoreCase));
                _globalPicked = name;
                if (GlobalsChanged != null) GlobalsChanged();
                _status.Text = "Made the global look \"" + name + "\" from " + string.Join(", ", opened.Select(p => p._name)) + "."
                             + (notYet.Count > 0 ? " Not in it (not opened yet): " + string.Join(", ", notYet) + ". Open one and press Send ... to Global CFG." : "");
            }
            catch (Exception ex) { App.Note("making a global look", ex); _status.Text = "Could not save: " + ex.Message; }
        }

        private void LoadGlobal()
        {
            if (_globalPicked == null) { _status.Text = "Pick a global look first."; return; }
            var sections = ReadGlobal(Path.Combine(GlobalFolder, _globalPicked + ".cfg"));
            List<KnobPanel> pages;
            lock (All) pages = All.ToList();
            var plan = sections.Select(kv => new { Page = pages.FirstOrDefault(p => string.Equals(p._name, kv.Key, StringComparison.OrdinalIgnoreCase)), Name = kv.Key, Text = kv.Value }).ToList();
            // its RECORDING part is the batch's (how its takes record), not a page's
            string missing = string.Join(", ", plan.Where(x => x.Page == null && !x.Name.Equals(RecordingSettings.Section, StringComparison.OrdinalIgnoreCase)).Select(x => x.Name));
            _status.Text = "Loading the global look \"" + _globalPicked + "\"...";
            string look = _globalPicked;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var said = new List<string>();
                foreach (var x in plan.Where(x => x.Page != null))      // one page after another: the bridge's pipe takes one at a time
                {
                    string r;
                    try { r = UserCfg.Send(x.Text, n => true, Log); }
                    catch (Exception ex) { App.Note("global look " + x.Name, ex); r = "failed: " + ex.Message; }
                    said.Add(x.Name + " " + r);
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var x in plan.Where(x => x.Page != null)) { if (!x.Page._built) x.Page.Build(); x.Page.ReadFromGame(); }
                    _status.Text = "Global look \"" + look + "\": " + string.Join("; ", said) + (missing.Length > 0 ? ". No page here for: " + missing : "") + ".";
                }));
            });
        }

        private void Import()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Settings (*.cfg)|*.cfg|All files|*.*" };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) SendFile(dlg.FileName);
        }

        /// <summary>A .cfg, sent as it is. Then the sliders follow the game.</summary>
        public void SendFile(string path)
        {
            string text;
            try { text = SettingsFiles.ToCfg(path, _bo2Key); }
            catch (Exception ex) { _status.Text = "Could not read " + Path.GetFileName(path) + ": " + ex.Message; return; }
            if (string.IsNullOrWhiteSpace(text)) { _status.Text = Path.GetFileName(path) + " has nothing for " + _name + "."; return; }
            SendNow(text, ReadFromGame);
        }

        private void Log(string line) { App.Note(_name + ": " + line.Trim(), null); }

        private static Button Btn(string text, RoutedEventHandler click, string tip = null)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6), ToolTip = tip };
            b.Click += click;
            return b;
        }
    }
}
