using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>
    /// The Batch page (2026-10-18). Mycelium, testing the Designer: "This recording tab should not even be in the batch tab ...
    /// instead of recording, this can be a global recording"; "the batch tab should have these sort of global commands. It
    /// should have global commands for POVs separately and then global commands for cinematics separately ... and also be able
    /// to save the global recording parameters for the queue into a preset"; "I want to see a detailed list inside of the
    /// batch queue. What cinematic it has, what FPS it has, where it's going to be recording to"; "this here can be expanded
    /// to the right, and then they see columns of their settings, like, is this POV recording green screen? Is this POV
    /// recording depth?"; "when you click one of these things ... you see different settings pop up and populate"; "the looks
    /// tab should be in the batch". Around the Batch tab (BatchCore): the POV / CINE global recording above its Output card,
    /// the looks shelf and the take list with a take's own settings where its list was, the night (CamQueueControl) beside Run.
    /// </summary>
    public static class BatchPage
    {
        public static UIElement Around(BatchControl batch)
        {
            var global = new GlobalRecording(batch);
            var takes = new TakesView(batch);
            var night = new Border();
            batch.Host(global, takes, night);
            // the Cam Queue is built after this page (it follows the Batch's list): its night card comes once it is there
            batch.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                try { if (CamQueueControl.Instance != null) night.Child = CamQueueControl.Instance.NightCard(); }
                catch (Exception ex) { App.Note("the Batch page's night card", ex); }
            }));
            return new ScrollViewer { Content = batch, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }

        // ---------------------------------------------------------------- shared bits

        /// <summary>The open queue when the Batch's list is its queue.csv (edits go into it); null otherwise.</summary>
        internal static CamQueue QueueOf(BatchControl batch)
        {
            CamQueue q = CamQueueControl.Queue;
            if (q == null || batch == null) return null;
            try { return string.Equals(Path.GetFullPath(q.Csv), Path.GetFullPath(batch.CsvPath), StringComparison.OrdinalIgnoreCase) ? q : null; }
            catch { return null; }
        }

        /// <summary>Grey words in an empty text box (Mycelium: a box "never touched" shows what it will record with).</summary>
        internal static void Ghost(TextBox box, Func<string> ghost)
        {
            var text = new TextBlock { Foreground = Ui.Brush("Dim"), Opacity = 0.75, Margin = new Thickness(5, 0, 0, 0), FontStyle = FontStyles.Italic };
            var brush = new VisualBrush(text) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Center };
            Brush normal = box.Background;
            Action paint = () => { text.Text = ghost(); box.Background = box.Text.Length == 0 ? (Brush)brush : normal; };
            box.TextChanged += (s, e) => paint();
            box.Loaded += (s, e) => { normal = normal ?? box.Background; paint(); };
            box.Tag = paint;
        }

        internal static void Repaint(TextBox box) { var a = box.Tag as Action; if (a != null) a(); }

        internal static string OnOff(int v) { return v < 0 ? "" : v == 1 ? "on" : "off"; }

        internal static Button Btn(string text, Action click, string tip = null, string icon = null)
        {
            var b = Ui.Button(text, click, tip, icon);
            b.Margin = new Thickness(0, 0, 6, 6);
            return b;
        }
    }

    /// <summary>The global recording: one card for the POV takes, one for the cinematics (queue.txt's pov_ and cine_ keys),
    /// each with its batch preset.</summary>
    internal sealed class GlobalRecording : UserControl
    {
        readonly BatchControl _batch;
        readonly KindCard _pov, _cine;
        readonly TextBlock _note = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("Dim"), Margin = new Thickness(2, 0, 0, 8) };

        public GlobalRecording(BatchControl batch)
        {
            _batch = batch;
            Foreground = Ui.Brush("Text");
            _pov = new KindCard(this, "pov", "POV takes", "How every POV take records, unless the take says otherwise (its row below)");
            _cine = new KindCard(this, "cine", "Cinematics", "How every cinematic records, unless the take says otherwise (its row below)");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(_cine, 2);
            grid.Children.Add(_pov); grid.Children.Add(_cine);
            var p = new StackPanel();
            p.Children.Add(_note);
            p.Children.Add(grid);
            Content = p;
            CamQueueControl.QueueChanged += () => Dispatcher.BeginInvoke(new Action(Show));
            batch.TakesLoaded += () => Dispatcher.BeginInvoke(new Action(Show));
            IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) Show(); };
            Loaded += (s, e) => Show();
        }

        internal CamQueue Queue { get { return BatchPage.QueueOf(_batch); } }
        internal BatchControl Batch { get { return _batch; } }

        internal void Show()
        {
            CamQueue q = Queue;
            _note.Text = q != null
                ? "The queue " + q.Name + ". Each take's own settings, its batch preset and its Global CFG's recording part come first; then these. Bold in the list below = the take's own."
                : CamQueueControl.Queue == null ? "Open or make a queue on the POV Queue or the Cam Queue: its takes, and how they record, show here."
                : "The list below is not the open queue (" + CamQueueControl.Queue.Name + "): Load its queue.csv above to set how its takes record.";
            _pov.Show(q); _cine.Show(q);
        }

        /// <summary>A kind's setting changed: written into the queue, the list read again.</summary>
        internal void Changed() { try { _batch.Reload(); } catch (Exception ex) { App.Note("the Batch list after a setting", ex); } }
    }

    /// <summary>One kind's global recording.</summary>
    internal sealed class KindCard : PrintCard
    {
        readonly GlobalRecording _owner;
        readonly string _kind;
        readonly ComboBox _preset = new ComboBox { MinWidth = 150, Margin = new Thickness(0, 0, 6, 6), ToolTip = "A batch preset: a saved set of these settings (Presets\\BATCH). Loaded, its settings win over the Global CFG's." };
        readonly TextBox _fps = Box(56), _before = Box(46), _after = Box(46), _dist = Box(56);
        readonly ComboBox _hud = Pick("shown", "hidden"), _film = Pick("CODCAST", "regular theater"), _codec = new ComboBox { MinWidth = 200, Margin = new Thickness(0, 0, 10, 6) };
        readonly CheckBox _depth = Check("Depth"), _plate = Check("Clean plate"), _noPlayers = Check("No players"), _playersOnly = Check("Players only");
        readonly TextBlock _look = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("Dim"), Margin = new Thickness(6, 0, 0, 0) };
        readonly TextBlock _said = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("AccentPale"), Margin = new Thickness(0, 2, 0, 0) };
        bool _filling;

        static TextBox Box(double w) { return new TextBox { Width = w, Margin = new Thickness(0, 0, 10, 6), VerticalContentAlignment = VerticalAlignment.Center }; }
        static ComboBox Pick(params string[] items) { var c = new ComboBox { MinWidth = 110, Margin = new Thickness(0, 0, 10, 6) }; foreach (string i in items) c.Items.Add(i); return c; }
        static CheckBox Check(string t) { return new CheckBox { Content = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 6) }; }
        static TextBlock L(string t) { return new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6), Foreground = Ui.Brush("Dim") }; }

        public KindCard(GlobalRecording owner, string kind, string title, string blurb)
        {
            _owner = owner; _kind = kind;
            Padding = new Thickness(14, 10, 14, 6);
            Margin = new Thickness(0, 0, 0, 10);
            BatchPage.Ghost(_fps, () => "1000");
            BatchPage.Ghost(_before, () => kind == "cine" ? "0.5" : "3");
            BatchPage.Ghost(_after, () => kind == "cine" ? "0.5" : "3");
            BatchPage.Ghost(_dist, () => "2000");
            _fps.ToolTip = "Frames per second of film. Left empty: 1000 (1000, 500, 250, 200, 125 and 100 step whole milliseconds)";
            _before.ToolTip = _after.ToolTip = "Seconds recorded before the take's start and after its end (handles for the edit)";
            _dist.ToolTip = "The depth pass's distance in game units: there and past it is black";
            _hud.ToolTip = "The kill feed, score and name tags";
            _film.ToolTip = "CODCAST (its HUD panel; POV takes), or the regular theater (FREE CAMERA and the dolly; cinematics)";
            _codec.ToolTip = "What these takes are written as; \"the Output card's\" = the codec on the Output card below";

            var head = new TextBlock { Text = title, FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Chrome"), Margin = new Thickness(0, 0, 0, 2) };
            Ui.Title(head);
            var p = new StackPanel();
            p.Children.Add(head);
            p.Children.Add(Ui.Note(blurb, 0, 8));
            p.Children.Add(Ui.Row(L("Batch preset"), _preset, BatchPage.Btn("Load", LoadPreset, "Its settings into this card (they then win over the Global CFG's)"),
                                  BatchPage.Btn("Save", SavePreset, "This card's settings over the picked preset (Save as... when none is picked)", Glyph.Save),
                                  BatchPage.Btn("Save as...", SavePresetAs, "This card's settings as a new batch preset")));
            p.Children.Add(Ui.Row(L("fps"), _fps, L("before s"), _before, L("after s"), _after));
            p.Children.Add(Ui.Row(L("HUD"), _hud, L("film"), _film));
            p.Children.Add(Ui.Row(L("Passes"), _depth, _dist, L("units"), _plate, _noPlayers, _playersOnly));
            p.Children.Add(Ui.Row(L("Codec"), _codec));
            var zone = LooksShelf.KindZone(kind, _look, file => SetLook(file), () => SetLook(null));
            p.Children.Add(Ui.Row(L("CFG"), BatchPage.Btn("Edit CFG...", EditCfg, "The CFG sent before every take of this kind (a take's own CFG replaces it)", Glyph.Command), L("Global CFG"), zone));
            p.Children.Add(_said);
            Child = p;

            foreach (TextBox b in new[] { _fps, _before, _after, _dist }) b.LostKeyboardFocus += (s, e) => Write();
            foreach (TextBox b in new[] { _fps, _before, _after, _dist }) b.KeyDown += (s, e) => { if (e.Key == Key.Enter) Write(); };
            foreach (ComboBox c in new[] { _hud, _film, _codec }) c.SelectionChanged += (s, e) => Write();
            foreach (CheckBox c in new[] { _depth, _plate, _noPlayers, _playersOnly }) { c.Checked += (s, e) => Write(); c.Unchecked += (s, e) => Write(); }
            _preset.DropDownOpened += (s, e) => FillPresets();
        }

        string D(CamQueue q, string key) { return q == null ? "" : q.Default(_kind + "_" + key); }

        static bool IsOn(string v, bool dflt) { v = (v ?? "").Trim().ToLowerInvariant(); return v.Length == 0 || v == "tab" ? dflt : v == "on" || v == "1" || v == "show" || v == "yes"; }

        void FillPresets()
        {
            string keep = _preset.SelectedItem as string;
            _preset.Items.Clear();
            _preset.Items.Add("(none)");
            foreach (string n in RecordingSettings.Presets()) _preset.Items.Add(n);
            _preset.SelectedItem = keep != null && _preset.Items.Contains(keep) ? keep : "(none)";
        }

        internal void Show(CamQueue q)
        {
            _filling = true;
            try
            {
                IsEnabled = q != null;
                FillPresets();
                string pr = D(q, "preset");
                _preset.SelectedItem = pr.Length > 0 && _preset.Items.Contains(pr) ? pr : "(none)";
                _fps.Text = D(q, "fps"); _before.Text = D(q, "before"); _after.Text = D(q, "after"); _dist.Text = D(q, "depthdist");
                _hud.SelectedIndex = IsOn(D(q, "hud"), _kind != "cine") ? 0 : 1;
                _film.SelectedIndex = IsOn(D(q, "codcast"), _kind != "cine") ? 0 : 1;
                _depth.IsChecked = IsOn(D(q, "depth"), false); _plate.IsChecked = IsOn(D(q, "plate"), false);
                _noPlayers.IsChecked = IsOn(D(q, "noplayers"), false); _playersOnly.IsChecked = IsOn(D(q, "playersonly"), false);
                _codec.Items.Clear();
                _codec.Items.Add("(the Output card's)");
                foreach (string n in _owner.Batch.CodecNames) _codec.Items.Add(n);
                string c = D(q, "codec");
                _codec.SelectedItem = c.Length > 0 && _codec.Items.Contains(c) ? c : "(the Output card's)";
                string look = D(q, "look");
                _look.Text = look.Length == 0 ? "none: drop one here" : Path.GetFileNameWithoutExtension(look);
                foreach (TextBox b in new[] { _fps, _before, _after, _dist }) BatchPage.Repaint(b);
            }
            finally { _filling = false; }
        }

        Dictionary<string, string> Values()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "fps", _fps.Text.Trim() }, { "before", _before.Text.Trim() }, { "after", _after.Text.Trim() },
                { "hud", _hud.SelectedIndex == 1 ? "hide" : "show" }, { "codcast", _film.SelectedIndex == 1 ? "off" : "on" },
                { "depth", _depth.IsChecked == true ? "on" : "off" }, { "depthdist", _dist.Text.Trim() }, { "plate", _plate.IsChecked == true ? "on" : "off" },
                { "noplayers", _noPlayers.IsChecked == true ? "on" : "off" }, { "playersonly", _playersOnly.IsChecked == true ? "on" : "off" },
                { "codec", _codec.SelectedIndex > 0 ? _codec.SelectedItem as string : "" },
            };
        }

        /// <summary>The card into queue.txt. A field changed by hand: the batch preset no longer describes the card, so it is let go.</summary>
        void Write()
        {
            if (_filling) return;
            CamQueue q = _owner.Queue;
            if (q == null) return;
            var v = Values();
            int bad;
            if (_fps.Text.Trim().Length > 0 && (!int.TryParse(_fps.Text.Trim(), out bad) || bad <= 0)) { _said.Text = "fps is a whole number (1000, 500, 250 ...)."; return; }
            bool changed = false;
            foreach (var kv in v) if (D(q, kv.Key) != kv.Value) { q.SetDefault(_kind + "_" + kv.Key, kv.Value); changed = true; }
            if (!changed) return;
            string pr = D(q, "preset");
            if (pr.Length > 0)
            {
                var preset = RecordingSettings.ReadPreset(pr);
                if (preset.Any(kv => { string mine; return v.TryGetValue(kv.Key, out mine) && !string.Equals(mine, kv.Value, StringComparison.OrdinalIgnoreCase); }))
                { q.SetDefault(_kind + "_preset", ""); _filling = true; _preset.SelectedItem = "(none)"; _filling = false; _said.Text = "Changed by hand: the batch preset " + pr + " is let go (Save as... keeps this as a preset)."; }
            }
            else _said.Text = "";
            _owner.Changed();
        }

        void LoadPreset()
        {
            CamQueue q = _owner.Queue;
            string name = _preset.SelectedItem as string;
            if (q == null || name == null || name == "(none)") { if (q != null && name == "(none)") { q.SetDefault(_kind + "_preset", ""); _owner.Changed(); _said.Text = "No batch preset."; } return; }
            var p = RecordingSettings.ReadPreset(name);
            foreach (var kv in p) q.SetDefault(_kind + "_" + kv.Key, kv.Value);
            q.SetDefault(_kind + "_preset", name);
            Show(q);
            _owner.Changed();
            _said.Text = "Loaded " + name + " (" + p.Count + " settings): it wins over a Global CFG's recording settings.";
        }

        void SavePreset()
        {
            string name = _preset.SelectedItem as string;
            if (name == null || name == "(none)") { SavePresetAs(); return; }
            try { RecordingSettings.SavePreset(name, Values()); _said.Text = "Saved over the batch preset " + name + "."; }
            catch (Exception ex) { _said.Text = "Not saved: " + ex.Message; }
        }

        void SavePresetAs()
        {
            string name = Ask.Text(Window.GetWindow(this), "Save batch preset", "A name for these " + (_kind == "cine" ? "cinematic" : "POV") + " settings:", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = CamQueue.Clean(name);
            try
            {
                RecordingSettings.SavePreset(name, Values());
                CamQueue q = _owner.Queue;
                if (q != null) q.SetDefault(_kind + "_preset", name);
                FillPresets(); _filling = true; _preset.SelectedItem = name; _filling = false;
                _said.Text = "Saved as the batch preset " + name + ".";
            }
            catch (Exception ex) { _said.Text = "Not saved: " + ex.Message; }
        }

        void SetLook(string file)
        {
            CamQueue q = _owner.Queue;
            if (q == null) { _said.Text = "Open a queue first."; return; }
            try { q.SetKindLook(_kind, file); Show(q); _owner.Changed(); _said.Text = file == null ? "No Global CFG for the " + (_kind == "cine" ? "cinematics" : "POV takes") + " (a take's own stays)." : "Every " + (_kind == "cine" ? "cinematic" : "POV take") + " without its own records with " + Path.GetFileNameWithoutExtension(file) + "."; }
            catch (Exception ex) { _said.Text = "Could not: " + ex.Message; }
        }

        void EditCfg()
        {
            CamQueue q = _owner.Queue;
            if (q == null) { _said.Text = "Open a queue first."; return; }
            string file = q.Default(_kind + "_cfg");
            if (file.Length == 0) { file = _kind + ".cfg"; q.SetDefault(_kind + "_cfg", file); }
            string path = Path.IsPathRooted(file) ? file : Path.Combine(q.Dir, file);
            if (!File.Exists(path)) File.WriteAllText(path, "", new System.Text.UTF8Encoding(false));
            TextFileWindow.Open(Window.GetWindow(this), path, "The CFG sent before every " + (_kind == "cine" ? "cinematic" : "POV take") + " (" + path + ")");
        }
    }

    /// <summary>A plain editor for a text file: saved as it is typed and when closed, never only on a button.</summary>
    internal static class TextFileWindow
    {
        public static void Open(Window owner, string path, string title)
        {
            var w = new Window { Title = title, Width = 640, Height = 460, Owner = owner, Background = Ui.Brush("Back") };
            var box = new TextBox { AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(8) };
            try { box.Text = File.ReadAllText(path); } catch { }
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            Action flush = () => { t.Stop(); try { File.WriteAllText(path, box.Text, new System.Text.UTF8Encoding(false)); } catch (Exception ex) { App.Note("saving " + path, ex); } };
            t.Tick += (s, e) => flush();
            box.TextChanged += (s, e) => { t.Stop(); t.Start(); };
            w.Closing += (s, e) => flush();
            box.LostKeyboardFocus += (s, e) => flush();
            w.Content = box;
            w.Show();
        }
    }

    /// <summary>The looks shelf (moved from the Cam Queue, 2026-10-18): every Global CFG and CFG as a chip to drag onto a take or a
    /// kind. A .cfg from Explorer works the same.</summary>
    internal static class LooksShelf
    {
        public const string Format = "ReludualiLookFile";

        public static FrameworkElement Make(out Action refill)
        {
            var chips = new WrapPanel();
            Action fill = () =>
            {
                chips.Children.Clear();
                string presets = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Presets");
                int n = 0;
                foreach (var group in new[] { new[] { "GLOBAL", "Global CFG" }, new[] { "CFG", "CFG" } })
                {
                    string dir = Path.Combine(presets, group[0]);
                    string[] files;
                    try { files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.cfg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray() : new string[0]; }
                    catch { files = new string[0]; }
                    foreach (string f in files) { chips.Children.Add(Chip(f, group[1])); n++; }
                }
                if (n == 0) chips.Children.Add(new TextBlock { Text = "None yet: make a Global CFG on any look page (New Global CFG...), or save a CFG on the CFG page.", Foreground = Ui.Brush("Dim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            };
            var head = new WrapPanel();
            head.Children.Add(new TextBlock { Text = "Looks", FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Chrome"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 6) });
            head.Children.Add(new TextBlock { Text = "drag one onto a take below, or onto a kind's Global CFG above", Foreground = Ui.Brush("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 6) });
            var again = Ui.IconButton(Glyph.Refresh, "Look again (a Global CFG or CFG saved since)", fill);
            again.Margin = new Thickness(0, 0, 0, 6);
            head.Children.Add(again);
            var panel = new StackPanel();
            panel.Children.Add(head);
            panel.Children.Add(chips);
            fill();
            refill = fill;
            return new PrintCard { Padding = new Thickness(12, 8, 12, 4), Margin = new Thickness(0, 0, 0, 8), Child = panel };
        }

        static FrameworkElement Chip(string file, string what)
        {
            var chip = new Border
            {
                Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1),
                BorderBrush = Ui.Brush("RimSoft"), Background = Ui.Brush("CardBack"), Cursor = Cursors.SizeAll,
                ToolTip = "The " + what + " " + Path.GetFileNameWithoutExtension(file) + ": drag it onto a take, or onto a kind's Global CFG",
                Child = Ui.IconText(what == "CFG" ? Glyph.Command : Glyph.Palette, Path.GetFileNameWithoutExtension(file)),
            };
            Point down = new Point(double.NaN, 0);
            chip.PreviewMouseLeftButtonDown += (s, e) => down = e.GetPosition(chip);
            chip.PreviewMouseLeftButtonUp += (s, e) => down = new Point(double.NaN, 0);
            chip.MouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || double.IsNaN(down.X)) return;
                Vector moved = e.GetPosition(chip) - down;
                if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                down = new Point(double.NaN, 0);
                DragDrop.DoDragDrop(chip, new DataObject(Format, file), DragDropEffects.Copy);
            };
            return chip;
        }

        /// <summary>The dragged look's file: a chip, or one .cfg from Explorer. Null when it is neither.</summary>
        public static string FileOf(IDataObject data)
        {
            if (data.GetDataPresent(Format)) return data.GetData(Format) as string;
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length == 1 && string.Equals(Path.GetExtension(files[0]), ".cfg", StringComparison.OrdinalIgnoreCase) && File.Exists(files[0])) return files[0];
            }
            return null;
        }

        /// <summary>A kind's drop zone: its Global CFG's name, a drop sets it, the bin clears it.</summary>
        public static FrameworkElement KindZone(string kind, TextBlock text, Action<string> dropped, Action clear)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Ui.Icon(Glyph.Palette, 13));
            row.Children.Add(text);
            var bin = new Button { Content = Ui.Icon(Glyph.Delete, 12), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0), ToolTip = "No Global CFG for every " + (kind == "cine" ? "cinematic" : "POV take") };
            bin.Click += (s, e) => clear();
            row.Children.Add(bin);
            var zone = new Border
            {
                Padding = new Thickness(8, 3, 6, 3), Margin = new Thickness(0, 0, 8, 6), CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1),
                BorderBrush = Ui.Brush("Accent"), Background = Brushes.Transparent, AllowDrop = true, Child = row,
                ToolTip = "Drop a Global CFG here: every " + (kind == "cine" ? "cinematic" : "POV take") + " records with it, unless the take has its own",
            };
            zone.DragOver += (s, e) => { e.Effects = FileOf(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            zone.DragEnter += (s, e) => zone.Background = Ui.Brush("RailOver");
            zone.DragLeave += (s, e) => zone.Background = Brushes.Transparent;
            zone.Drop += (s, e) => { zone.Background = Brushes.Transparent; string f = FileOf(e.Data); if (f != null) dropped(f); };
            return zone;
        }
    }

    /// <summary>The take list (the Batch's takes, one column per setting, bold where the take has its own), the looks shelf
    /// over it, and the picked take's own settings under it.</summary>
    internal sealed class TakesView : UserControl
    {
        readonly BatchControl _batch;
        readonly DataGrid _grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
            CanUserAddRows = false, CanUserDeleteRows = false, CanUserReorderColumns = true, CanUserResizeColumns = true, CanUserSortColumns = false,
            GridLinesVisibility = DataGridGridLinesVisibility.None, BorderThickness = new Thickness(0), AllowDrop = true, MinHeight = 160,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        readonly StackPanel _detail = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        readonly TextBlock _said = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("AccentPale"), Margin = new Thickness(0, 4, 0, 0) };
        Action _refillShelf;
        int _picked = -1;

        public TakesView(BatchControl batch)
        {
            _batch = batch;
            Foreground = Ui.Brush("Text");
            _grid.Background = Brushes.Transparent;
            _grid.RowBackground = Brushes.Transparent;
            _grid.AlternatingRowBackground = Brushes.Transparent;
            _grid.Foreground = Ui.Brush("Text");
            _grid.ColumnHeaderStyle = HeaderStyle();
            _grid.CellStyle = CellStyle();
            Column("#", "No", 34); Column("Kind", "Kind", 46); Column("Name", "Name", 150); Column("Frag", "Frag", 140); Column("Film time", "Time", 78);
            Column("Len", "Len", 50); Column("fps", "Fps", 50, "FpsW"); Column("Before / after", "Around", 92, "AroundW"); Column("HUD", "Hud", 60, "HudW");
            Column("Film", "Film", 74, "FilmW"); Column("Passes", "Passes", 150, "PassesW"); Column("Codec", "Codec", 130, "CodecW");
            Column("Global CFG", "Look", 120, "LookW"); Column("Batch preset", "Preset", 100, "PresetW"); Column("Records to", "Out", 260);
            _grid.SelectionChanged += (s, e) => { _picked = _grid.SelectedIndex; ShowDetail(); };
            _grid.DragOver += (s, e) => { e.Effects = LooksShelf.FileOf(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            _grid.Drop += (s, e) => DropLook(e);
            batch.PickedTake = () => _picked;

            var shelf = LooksShelf.Make(out _refillShelf);
            var dock = new DockPanel();
            DockPanel.SetDock(shelf, Dock.Top);
            dock.Children.Add(shelf);
            var detailCard = new PrintCard { Padding = new Thickness(12, 8, 12, 6), Margin = new Thickness(0, 8, 0, 0), Child = new StackPanel { Children = { _detail, _said } } };
            DockPanel.SetDock(detailCard, Dock.Bottom);
            dock.Children.Add(detailCard);
            dock.Children.Add(_grid);
            Content = dock;
            batch.TakesLoaded += () => Dispatcher.BeginInvoke(new Action(Fill));
            CamQueueControl.QueueChanged += () => Dispatcher.BeginInvoke(new Action(() => { _batch.Reload(); }));
            IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) { _refillShelf(); Fill(); } };
            ShowDetail();
        }

        void Column(string head, string path, double width, string weight = null)
        {
            var c = new DataGridTextColumn { Header = head, Binding = new System.Windows.Data.Binding(path), Width = new DataGridLength(width) };
            var st = new Style(typeof(TextBlock));
            st.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            st.Setters.Add(new Setter(TextBlock.ToolTipProperty, new System.Windows.Data.Binding(path)));
            if (weight != null) st.Setters.Add(new Setter(TextBlock.FontWeightProperty, new System.Windows.Data.Binding(weight)));
            c.ElementStyle = st;
            _grid.Columns.Add(c);
        }

        static Style HeaderStyle()
        {
            var st = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
            st.Setters.Add(new Setter(Control.BackgroundProperty, Ui.Brush("Inset")));
            st.Setters.Add(new Setter(Control.ForegroundProperty, Ui.Brush("Dim")));
            st.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)));
            st.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            st.Setters.Add(new Setter(Control.BorderBrushProperty, Ui.Brush("RimSoft")));
            return st;
        }

        static Style CellStyle()
        {
            var st = new Style(typeof(DataGridCell));
            st.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            st.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 2, 4, 2)));
            st.Setters.Add(new Setter(Control.ForegroundProperty, Ui.Brush("Text")));
            var sel = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            sel.Setters.Add(new Setter(Control.BackgroundProperty, Ui.Brush("Accent")));
            sel.Setters.Add(new Setter(Control.ForegroundProperty, Ui.Brush("Chrome")));
            st.Triggers.Add(sel);
            return st;
        }

        /// <summary>One take as the list shows it (and why each value is what it is).</summary>
        public sealed class Row
        {
            public int No { get; set; }
            public string Kind { get; set; }
            public string Name { get; set; }
            public string Frag { get; set; }
            public string Time { get; set; }
            public string Len { get; set; }
            public string Fps { get; set; }
            public string Around { get; set; }
            public string Hud { get; set; }
            public string Film { get; set; }
            public string Passes { get; set; }
            public string Codec { get; set; }
            public string Look { get; set; }
            public string Preset { get; set; }
            public string Out { get; set; }
            public FontWeight FpsW { get; set; }
            public FontWeight AroundW { get; set; }
            public FontWeight HudW { get; set; }
            public FontWeight FilmW { get; set; }
            public FontWeight PassesW { get; set; }
            public FontWeight CodecW { get; set; }
            public FontWeight LookW { get; set; }
            public FontWeight PresetW { get; set; }
        }

        static FontWeight W(Take t, params string[] keys) { return keys.Any(k => { string f; return t.From.TryGetValue(k, out f) && f == "own"; }) ? FontWeights.Bold : FontWeights.Normal; }

        static string Sec(int ms, string dflt) { return ms < 0 ? dflt : (ms / 1000.0).ToString("0.0#", CultureInfo.InvariantCulture); }

        void Fill()
        {
            int keep = _picked;
            var rows = new List<Row>();
            IList<Take> takes = _batch.Takes ?? new List<Take>();
            CamQueue q = BatchPage.QueueOf(_batch);
            string outDir = _batch.OutputFolder;
            foreach (Take t in takes)
            {
                var passes = new List<string>();
                if (t.Depth == 1) passes.Add("depth " + (t.DepthDist > 0 ? t.DepthDist : 2000));
                if (t.Plate == 1) passes.Add("plate");
                if (t.NoPlayers == 1) passes.Add("no players");
                if (t.PlayersOnly == 1) passes.Add("players only");
                bool ownLook = q != null && t.Number - 1 < q.Rows.Count && t.Number >= 1 && q.Rows[t.Number - 1].Look.Length > 0;
                rows.Add(new Row
                {
                    No = t.Number, Kind = t.IsCine ? "CINE" : "POV", Name = t.Name.Length > 0 ? t.Name : t.Folder, Frag = t.Folder,
                    Time = t.HasTicks ? CamPaths.ClockLabel(t.TickIn) + "-" + CamPaths.ClockLabel(t.TickOut + 999) : Take.Clock(t.ClockIn) + "-" + Take.Clock(t.ClockOut),
                    Len = t.Seconds + " s",
                    Fps = (t.Fps > 0 ? t.Fps : 1000).ToString(CultureInfo.InvariantCulture),
                    Around = Sec(t.BeforeMs, "3") + " / " + Sec(t.AfterMs, "3"),
                    Hud = t.Hud == 0 ? "hidden" : "shown",
                    Film = t.Codcast == 0 ? "regular" : "CODCAST",
                    Passes = passes.Count == 0 ? "-" : string.Join(", ", passes),
                    Codec = t.Codec.Length > 0 ? t.Codec : "(" + Short(_batch.CodecName) + ")",
                    Look = string.IsNullOrEmpty(t.LookPath) ? "-" : Path.GetFileNameWithoutExtension(t.LookPath),
                    Preset = t.Preset.Length > 0 ? t.Preset : "-",
                    Out = Path.Combine(outDir, t.Folder) + "\\",
                    FpsW = W(t, "fps"), AroundW = W(t, "before", "after"), HudW = W(t, "hud"), FilmW = W(t, "codcast"),
                    PassesW = W(t, "depth", "depthdist", "plate", "noplayers", "playersonly"), CodecW = W(t, "codec"),
                    LookW = ownLook ? FontWeights.Bold : FontWeights.Normal, PresetW = q != null && t.Number >= 1 && t.Number - 1 < q.Rows.Count && q.Rows[t.Number - 1].Preset.Length > 0 ? FontWeights.Bold : FontWeights.Normal,
                });
            }
            _grid.ItemsSource = rows;
            if (keep >= 0 && keep < rows.Count) _grid.SelectedIndex = keep;
            else { _picked = -1; ShowDetail(); }
        }

        static string Short(string codec) { int p = codec.IndexOf(" ("); return p > 0 ? codec.Substring(0, p) : codec; }

        void DropLook(DragEventArgs e)
        {
            string file = LooksShelf.FileOf(e.Data);
            if (file == null) return;
            var hit = _grid.InputHitTest(e.GetPosition(_grid)) as DependencyObject;
            while (hit != null && !(hit is DataGridRow)) hit = VisualTreeHelper.GetParent(hit);
            var row = hit as DataGridRow;
            if (row == null) { _said.Text = "Drop it on a take (or on a kind's Global CFG above)."; return; }
            int i = row.GetIndex();
            CamQueue q = BatchPage.QueueOf(_batch);
            if (q == null || i < 0 || i >= q.Rows.Count) { _said.Text = "This list is not the open queue: only a queue's takes take a look."; return; }
            try { q.SetLook(i, file); _batch.Reload(); _said.Text = "Take " + (i + 1) + " (" + q.Rows[i].Name + ") records with " + Path.GetFileNameWithoutExtension(file) + ", sent after its CFG."; }
            catch (Exception ex) { App.Note("giving a take a look", ex); _said.Text = "Could not: " + ex.Message; }
        }

        // ---------------------------------------------------------------- the picked take's own settings

        void ShowDetail()
        {
            _detail.Children.Clear();
            IList<Take> takes = _batch.Takes;
            if (takes == null || _picked < 0 || _picked >= takes.Count)
            {
                _detail.Children.Add(Ui.Note("Pick a take in the list: its settings show here, where each comes from, and any can be its own. Bold in the list = the take's own."));
                return;
            }
            Take t = takes[_picked];
            CamQueue q = BatchPage.QueueOf(_batch);
            QueueRow r = q != null && _picked < q.Rows.Count ? q.Rows[_picked] : null;
            var title = new TextBlock { Text = "Take " + t.Number + ": " + (t.IsCine ? "CINE " : "POV ") + (t.Name.Length > 0 ? t.Name : t.Folder), FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Chrome"), Margin = new Thickness(0, 0, 0, 6) };
            Ui.Title(title);
            _detail.Children.Add(title);
            if (r == null) { _detail.Children.Add(Ui.Note("This list is not the open queue: its takes record as their CSV says. Open its folder on the POV Queue or the Cam Queue to change them here.")); return; }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            int line = 0;
            Action<string, FrameworkElement, string[]> add = (label, editor, keys) =>
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4), Foreground = Ui.Brush("Dim") };
                string from = string.Join(", ", keys.Select(k => { string f; return t.From.TryGetValue(k, out f) ? f : "the Batch's own"; }).Distinct());
                bool own = keys.Any(k => { string f; return t.From.TryGetValue(k, out f) && f == "own"; });
                var origin = new TextBlock { Text = own ? "this take's own" : "from " + from, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 4),
                                             Foreground = own ? Ui.Brush("Accent") : Ui.Brush("Dim"), FontWeight = own ? FontWeights.SemiBold : FontWeights.Normal };
                editor.HorizontalAlignment = HorizontalAlignment.Left;
                Grid.SetRow(l, line); Grid.SetRow(editor, line); Grid.SetColumn(editor, 1); Grid.SetRow(origin, line); Grid.SetColumn(origin, 2);
                grid.Children.Add(l); grid.Children.Add(editor); grid.Children.Add(origin);
                if (own)
                {
                    var back = new Button { Content = "Use its kind's", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 0, 4), ToolTip = "Forget this take's own value: it takes its preset's, its Global CFG's or its kind's again" };
                    back.Click += (s, e) => { foreach (string k in keys) r.SetOwn(k, ""); SaveRow(q, "Take " + t.Number + ": " + label + " back to its kind's."); };
                    Grid.SetRow(back, line); Grid.SetColumn(back, 3); grid.Children.Add(back);
                }
                line++;
            };
            // fps
            var fps = new TextBox { Width = 70, Text = t.Fps > 0 ? t.Fps.ToString(CultureInfo.InvariantCulture) : "1000", Margin = new Thickness(0, 0, 0, 4), VerticalContentAlignment = VerticalAlignment.Center };
            fps.LostKeyboardFocus += (s, e) => Own(q, r, t, "fps", fps.Text.Trim(), (t.Fps > 0 ? t.Fps : 1000).ToString(CultureInfo.InvariantCulture));
            fps.KeyDown += (s, e) => { if (e.Key == Key.Enter) Own(q, r, t, "fps", fps.Text.Trim(), (t.Fps > 0 ? t.Fps : 1000).ToString(CultureInfo.InvariantCulture)); };
            add("fps", fps, new[] { "fps" });
            // before / after
            var before = new TextBox { Width = 46, Text = Sec(t.BeforeMs, "3"), Margin = new Thickness(0, 0, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
            var after = new TextBox { Width = 46, Text = Sec(t.AfterMs, "3"), Margin = new Thickness(0, 0, 0, 4), VerticalContentAlignment = VerticalAlignment.Center };
            before.LostKeyboardFocus += (s, e) => Own(q, r, t, "before", before.Text.Trim(), Sec(t.BeforeMs, "3"));
            after.LostKeyboardFocus += (s, e) => Own(q, r, t, "after", after.Text.Trim(), Sec(t.AfterMs, "3"));
            add("before / after s", Ui.Row(before, after), new[] { "before", "after" });
            // HUD, film
            var hud = new ComboBox { MinWidth = 100, Margin = new Thickness(0, 0, 0, 4) }; hud.Items.Add("shown"); hud.Items.Add("hidden"); hud.SelectedIndex = t.Hud == 0 ? 1 : 0;
            hud.SelectionChanged += (s, e) => Own(q, r, t, "hud", hud.SelectedIndex == 1 ? "hide" : "show", t.Hud == 0 ? "hide" : "show");
            add("HUD", hud, new[] { "hud" });
            var film = new ComboBox { MinWidth = 140, Margin = new Thickness(0, 0, 0, 4) }; film.Items.Add("CODCAST"); film.Items.Add("regular theater"); film.SelectedIndex = t.Codcast == 0 ? 1 : 0;
            film.SelectionChanged += (s, e) => Own(q, r, t, "codcast", film.SelectedIndex == 1 ? "off" : "on", t.Codcast == 0 ? "off" : "on");
            add("film", film, new[] { "codcast" });
            // passes
            var passes = new WrapPanel();
            foreach (var p in new[] { new[] { "depth", "Depth" }, new[] { "plate", "Clean plate" }, new[] { "noplayers", "No players" }, new[] { "playersonly", "Players only" } })
            {
                string key = p[0];
                int now = key == "depth" ? t.Depth : key == "plate" ? t.Plate : key == "noplayers" ? t.NoPlayers : t.PlayersOnly;
                var cb = new CheckBox { Content = p[1], IsChecked = now == 1, Margin = new Thickness(0, 0, 12, 4), VerticalAlignment = VerticalAlignment.Center };
                cb.Checked += (s, e) => Own(q, r, t, key, "on", BatchPage.OnOff(now));
                cb.Unchecked += (s, e) => Own(q, r, t, key, "off", BatchPage.OnOff(now));
                passes.Children.Add(cb);
            }
            add("passes", passes, new[] { "depth", "plate", "noplayers", "playersonly" });
            // codec
            var codec = new ComboBox { MinWidth = 230, Margin = new Thickness(0, 0, 0, 4) };
            codec.Items.Add("(the Output card's)");
            foreach (string n in _batch.CodecNames) codec.Items.Add(n);
            codec.SelectedItem = t.Codec.Length > 0 && codec.Items.Contains(t.Codec) ? t.Codec : "(the Output card's)";
            codec.SelectionChanged += (s, e) => Own(q, r, t, "codec", codec.SelectedIndex > 0 ? codec.SelectedItem as string : "", t.Codec);
            add("codec", codec, new[] { "codec" });
            _detail.Children.Add(grid);

            // the batch preset and the Global CFG: whose is it, and change it
            var preset = new ComboBox { MinWidth = 150, Margin = new Thickness(0, 0, 8, 6) };
            preset.Items.Add("(the kind's)");
            foreach (string n in RecordingSettings.Presets()) preset.Items.Add(n);
            preset.SelectedItem = r.Preset.Length > 0 && preset.Items.Contains(r.Preset) ? r.Preset : "(the kind's)";
            preset.SelectionChanged += (s, e) => { r.Preset = preset.SelectedIndex > 0 ? preset.SelectedItem as string : ""; SaveRow(q, "Take " + t.Number + ": batch preset " + (r.Preset.Length > 0 ? r.Preset : "its kind's") + "."); };
            bool ownLook;
            string look = q.LookOf(r, out ownLook);
            var noLook = new Button { Content = "No look of its own", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 8, 6), IsEnabled = ownLook };
            noLook.Click += (s, e) => { q.SetLook(_picked, null); _batch.Reload(); _said.Text = "Take " + t.Number + " takes its kind's Global CFG again."; };
            _detail.Children.Add(Ui.Row(Ui.Label("Batch preset"), preset, Ui.Label("Global CFG"),
                                        new TextBlock { Text = look.Length == 0 ? "none (drop one on its row)" : look + (ownLook ? "" : " (its kind's)"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6), FontWeight = ownLook ? FontWeights.SemiBold : FontWeights.Normal },
                                        noLook));
        }

        /// <summary>A setting of the take's own, written into the queue when it differs from what the take records with now.</summary>
        void Own(CamQueue q, QueueRow r, Take t, string key, string value, string now)
        {
            if (q == null || r == null) return;
            if (string.Equals(value ?? "", now ?? "", StringComparison.OrdinalIgnoreCase)) return;
            if (key == "fps") { int v; if (!int.TryParse(value, out v) || v <= 0) { _said.Text = "fps is a whole number (1000, 500 ...)."; return; } }
            if (key == "before" || key == "after") { double v; if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v < 0) { _said.Text = "Seconds, for example 3 or 0.5."; return; } }
            r.SetOwn(key, value);
            SaveRow(q, "Take " + t.Number + ": its own " + key + " = " + (value.Length > 0 ? value : "the Output card's") + ".");
        }

        void SaveRow(CamQueue q, string said)
        {
            try { q.Save(); _batch.Reload(); _said.Text = said; }
            catch (Exception ex) { App.Note("saving a take's setting", ex); _said.Text = "Not saved: " + ex.Message; }
        }
    }
}
