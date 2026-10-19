using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>
    /// The Global CFG, all in one place (2026-10-18). Mycelium: "There needs to be a place that says global and it literally has all
    /// the sliders from look, fog, sun, ssao, doff, sky ... so someone has peace of mind ... I have all my colors and my fov, my
    /// sun, my ssao, my doff ... and just peace of mind that things aren't being added twice when they shouldn't be added
    /// twice"; then: "we should probably redesign the CFG feature to just include the global CFG ... there's certain features
    /// that will be designer only ... and a set of features that are just Dvars in particular so we can probably add a split
    /// right through this view. First tab Dvars second tab parameters that are kind of designer specific."
    /// Tab 1, Dvars: every setting the picked Global CFG holds, page by page, one line each, a setting held by two pages marked.
    /// Tab 2, Designer parameters: how its takes record (the RECORDING part the Batch reads) and the Designer's own lines (the
    /// FOG page's blend modes).
    /// </summary>
    public sealed class GlobalCfgView : UserControl
    {
        readonly ComboBox _pick = new ComboBox { MinWidth = 220, Margin = new Thickness(0, 0, 8, 6), ToolTip = "Your Global CFGs (Presets\\GLOBAL): every look page shows the same one" };
        readonly ListView _dvars = new ListView { MinHeight = 260, BorderThickness = new Thickness(0) };
        readonly TextBox _value = new TextBox { Width = 260, Margin = new Thickness(0, 0, 8, 6), VerticalContentAlignment = VerticalAlignment.Center };
        readonly TextBlock _picked = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6), FontFamily = new FontFamily("Consolas") };
        readonly TextBlock _twice = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("AccentPale"), Margin = new Thickness(0, 4, 0, 6) };
        readonly TextBlock _said = Ui.Status();
        readonly StackPanel _designerLines = new StackPanel();
        readonly TextBox _fps = Box(56), _before = Box(46), _after = Box(46), _dist = Box(56);
        readonly ComboBox _hud = Pick("(not set)", "shown", "hidden"), _film = Pick("(not set)", "CODCAST", "regular theater"), _codec = new ComboBox { MinWidth = 220, Margin = new Thickness(0, 0, 10, 6) };
        readonly ComboBox _depth = Pick("(not set)", "on", "off"), _plate = Pick("(not set)", "on", "off"), _noPlayers = Pick("(not set)", "on", "off"), _playersOnly = Pick("(not set)", "on", "off");
        bool _filling;

        static TextBox Box(double w) { return new TextBox { Width = w, Margin = new Thickness(0, 0, 10, 6), VerticalContentAlignment = VerticalAlignment.Center }; }
        static ComboBox Pick(params string[] items) { var c = new ComboBox { MinWidth = 100, Margin = new Thickness(0, 0, 10, 6) }; foreach (string i in items) c.Items.Add(i); c.SelectedIndex = 0; return c; }
        static TextBlock L(string t) { return new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6), Foreground = Ui.Brush("Dim") }; }

        sealed class Line
        {
            public string Page { get; set; }
            public string Setting { get; set; }
            public string Value { get; set; }
            public string Twice { get; set; }
            public int At;                      // its line in its page's part
        }

        string File_ { get { string n = _pick.SelectedItem as string; return n == null ? null : Path.Combine(KnobPanel.GlobalsFolder, n + ".cfg"); } }

        public GlobalCfgView()
        {
            Foreground = Ui.Brush("Text");
            var gv = new GridView();
            foreach (var c in new[] { new[] { "Page", "Page", "90" }, new[] { "Setting", "Setting", "260" }, new[] { "Value", "Value", "220" }, new[] { "Also on", "Twice", "160" } })
                gv.Columns.Add(new GridViewColumn { Header = c[0], DisplayMemberBinding = new System.Windows.Data.Binding(c[1]), Width = double.Parse(c[2], CultureInfo.InvariantCulture) });
            _dvars.View = gv;
            _dvars.ItemContainerStyle = Ui.Style("GridRow");
            _dvars.SelectionChanged += (s, e) => { var l = _dvars.SelectedItem as Line; _picked.Text = l == null ? "" : l.Page + ": " + l.Setting; _value.Text = l == null ? "" : l.Value; };

            var top = Ui.Row(L("Global CFG"), _pick,
                             Ui.Primary(BatchPage.Btn("Load everywhere", () => _said.Text = KnobPanel.LoadPickedEverywhere(), "Send it to the game: every page it holds")),
                             BatchPage.Btn("New...", NewOne, "A new Global CFG from every look page you have opened", Glyph.Add),
                             BatchPage.Btn("Show the file", () => { string f = File_; if (f != null && File.Exists(f)) System.Diagnostics.Process.Start("notepad.exe", "\"" + f + "\""); }, "The file itself, in Notepad"));
            _pick.SelectionChanged += (s, e) => { if (_filling) return; string n = _pick.SelectedItem as string; if (n != null && n != KnobPanel.PickedGlobal) KnobPanel.PickedGlobal = n; Fill(); };

            // tab 1: the dvars
            var edit = Ui.Row(_picked, _value, BatchPage.Btn("Set", SetValue, "The picked line takes this value (saved at once)"), BatchPage.Btn("Remove", RemoveLine, "The picked line out of this Global CFG", Glyph.Delete));
            _value.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) SetValue(); };
            var dvarTab = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var note = Ui.Note("Every setting this Global CFG sends, page by page. A setting held by two pages is sent twice; the later page wins (pages go in the order listed). Change a page's own part on its page (Send ... to Global CFG), or a line here.", 0, 6);
            DockPanel.SetDock(note, Dock.Top); dvarTab.Children.Add(note);
            DockPanel.SetDock(_twice, Dock.Top); dvarTab.Children.Add(_twice);
            DockPanel.SetDock(edit, Dock.Bottom); dvarTab.Children.Add(edit);
            dvarTab.Children.Add(_dvars);

            // tab 2: the Designer's own: how its takes record, and the Designer's own lines
            BatchPage.Ghost(_fps, () => "not set"); BatchPage.Ghost(_before, () => "not set"); BatchPage.Ghost(_after, () => "not set"); BatchPage.Ghost(_dist, () => "2000");
            var rec = new StackPanel();
            rec.Children.Add(Ui.Note("How a take recorded with this Global CFG records, when the take and its batch preset do not say otherwise (a batch preset wins over these; these win over the Batch page's POV and cinematic settings). \"Not set\" leaves it to the Batch page.", 0, 8));
            rec.Children.Add(Ui.Row(L("fps"), _fps, L("before s"), _before, L("after s"), _after));
            rec.Children.Add(Ui.Row(L("HUD"), _hud, L("film"), _film));
            rec.Children.Add(Ui.Row(L("Depth"), _depth, _dist, L("units"), L("Clean plate"), _plate, L("No players"), _noPlayers, L("Players only"), _playersOnly));
            rec.Children.Add(Ui.Row(L("Codec"), _codec));
            foreach (TextBox b in new[] { _fps, _before, _after, _dist }) { b.LostKeyboardFocus += (s, e) => WriteRecording(); b.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) WriteRecording(); }; }
            foreach (ComboBox c in new[] { _hud, _film, _codec, _depth, _plate, _noPlayers, _playersOnly }) c.SelectionChanged += (s, e) => WriteRecording();
            var designerTab = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            designerTab.Children.Add(Ui.Card("Recording", null, rec));
            designerTab.Children.Add(Ui.Card("The Designer's own", "Lines only the Designer understands (sent through RipperBridge, not to the game's console): the FOG page's blend modes. Change them on their page.", _designerLines));

            var tabs = Ui.Tabs(Tuple.Create("1  Dvars", (UIElement)dvarTab), Tuple.Create("2  Designer parameters", (UIElement)new ScrollViewer { Content = designerTab, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }));
            var root = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
            DockPanel.SetDock(_said, Dock.Bottom); root.Children.Add(_said);
            root.Children.Add(tabs);
            Content = root;
            KnobPanel.PickedGlobalChanged += () => Dispatcher.BeginInvoke(new Action(List));
            IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) List(); };
            List();
        }

        void List()
        {
            _filling = true;
            try
            {
                _pick.Items.Clear();
                try { if (Directory.Exists(KnobPanel.GlobalsFolder)) foreach (string f in Directory.GetFiles(KnobPanel.GlobalsFolder, "*.cfg").OrderBy(f => f)) _pick.Items.Add(Path.GetFileNameWithoutExtension(f)); }
                catch (Exception ex) { App.Note("listing the Global CFGs", ex); }
                string want = KnobPanel.PickedGlobal;
                if (want != null && _pick.Items.Contains(want)) _pick.SelectedItem = want;
                else if (_pick.Items.Count > 0) _pick.SelectedIndex = 0;
            }
            finally { _filling = false; }
            Fill();
        }

        void Fill()
        {
            string file = File_;
            var lines = new List<Line>();
            _designerLines.Children.Clear();
            _twice.Text = "";
            if (file == null || !File.Exists(file))
            {
                _dvars.ItemsSource = lines;
                _said.Text = _pick.Items.Count == 0 ? "No Global CFG yet: New... makes one from the look pages you have opened." : "";
                FillRecording(new Dictionary<string, string>());
                return;
            }
            Dictionary<string, string> sections = KnobPanel.ReadGlobalFile(file);
            foreach (var kv in sections)
            {
                if (kv.Key.Equals(RecordingSettings.Section, StringComparison.OrdinalIgnoreCase)) continue;
                string[] ls = kv.Value.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < ls.Length; i++)
                {
                    string l = ls[i].Trim();
                    if (l.Length == 0 || l.StartsWith("//", StringComparison.Ordinal)) continue;
                    if (l.StartsWith("bridge ", StringComparison.OrdinalIgnoreCase))
                    {
                        _designerLines.Children.Add(new TextBlock { Text = kv.Key + ":  " + l.Substring(7), FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 0, 0, 3) });
                        continue;
                    }
                    int sp = l.IndexOf(' ');
                    lines.Add(new Line { Page = kv.Key, Setting = sp > 0 ? l.Substring(0, sp) : l, Value = sp > 0 ? l.Substring(sp + 1).Trim() : "", At = i });
                }
            }
            if (_designerLines.Children.Count == 0) _designerLines.Children.Add(Ui.Note("None in this Global CFG."));
            // a setting on two pages: each says where else
            var byName = lines.GroupBy(l => l.Setting, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(x => x.Page).Distinct().Count() > 1).ToList();
            foreach (var g in byName) foreach (Line l in g) l.Twice = string.Join(", ", g.Where(x => x.Page != l.Page).Select(x => x.Page).Distinct());
            _twice.Text = byName.Count == 0 ? "No setting is on two pages." : byName.Count + " setting(s) on two pages: " + string.Join("; ", byName.Select(g => g.Key + " (" + string.Join(", ", g.Select(x => x.Page).Distinct()) + ")"));
            _dvars.ItemsSource = lines;
            FillRecording(RecordingSettings.FromLook(file));
            _said.Text = Path.GetFileName(file) + ": " + lines.Count + " settings on " + sections.Keys.Count(k => !k.Equals(RecordingSettings.Section, StringComparison.OrdinalIgnoreCase)) + " page(s).";
        }

        void SetValue()
        {
            var l = _dvars.SelectedItem as Line;
            string file = File_;
            if (l == null || file == null) { _said.Text = "Pick a line first."; return; }
            ChangeLine(file, l, l.Setting + (_value.Text.Trim().Length > 0 ? " " + _value.Text.Trim() : ""));
        }

        void RemoveLine()
        {
            var l = _dvars.SelectedItem as Line;
            string file = File_;
            if (l == null || file == null) { _said.Text = "Pick a line first."; return; }
            ChangeLine(file, l, null);
        }

        void ChangeLine(string file, Line l, string now)
        {
            try
            {
                var sections = KnobPanel.ReadGlobalFile(file);
                string part;
                if (!sections.TryGetValue(l.Page, out part)) return;
                var ls = part.Replace("\r\n", "\n").Split('\n').ToList();
                if (l.At < 0 || l.At >= ls.Count) return;
                if (now == null) ls.RemoveAt(l.At); else ls[l.At] = now;
                sections[l.Page] = string.Join("\r\n", ls);
                KnobPanel.WriteGlobalFile(file, sections);
                _said.Text = now == null ? "Removed " + l.Setting + " from " + l.Page + "." : l.Page + ": " + now + " (saved; Load everywhere sends it).";
                Fill();
            }
            catch (Exception ex) { App.Note("changing a Global CFG line", ex); _said.Text = "Not saved: " + ex.Message; }
        }

        void NewOne()
        {
            string name = Ask.Text(Window.GetWindow(this), "New Global CFG", "Name:", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            _said.Text = KnobPanel.PipeGlobal("new " + name.Trim());
            List();
        }

        // ---- the RECORDING part

        static int Index(string v, string on, string off) { v = (v ?? "").Trim().ToLowerInvariant(); return v.Length == 0 ? 0 : (v == on || v == "on" || v == "1" || v == "show" || v == "yes") ? 1 : 2; }

        void FillRecording(Dictionary<string, string> d)
        {
            _filling = true;
            try
            {
                Func<string, string> g = k => { string v; return d.TryGetValue(k, out v) ? v : ""; };
                _fps.Text = g("fps"); _before.Text = g("before"); _after.Text = g("after"); _dist.Text = g("depthdist");
                _hud.SelectedIndex = Index(g("hud"), "show", "hide");
                _film.SelectedIndex = Index(g("codcast"), "on", "off");
                _depth.SelectedIndex = Index(g("depth"), "on", "off"); _plate.SelectedIndex = Index(g("plate"), "on", "off");
                _noPlayers.SelectedIndex = Index(g("noplayers"), "on", "off"); _playersOnly.SelectedIndex = Index(g("playersonly"), "on", "off");
                _codec.Items.Clear();
                _codec.Items.Add("(not set)");
                if (BatchControl.Instance != null) foreach (string n in BatchControl.Instance.CodecNames) _codec.Items.Add(n);
                string c = g("codec");
                _codec.SelectedItem = c.Length > 0 && _codec.Items.Contains(c) ? c : "(not set)";
                foreach (TextBox b in new[] { _fps, _before, _after, _dist }) BatchPage.Repaint(b);
            }
            finally { _filling = false; }
        }

        void WriteRecording()
        {
            if (_filling) return;
            string file = File_;
            if (file == null) return;
            Func<ComboBox, string, string, string> tri = (c, on, off) => c.SelectedIndex == 1 ? on : c.SelectedIndex == 2 ? off : "";
            var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "fps", _fps.Text.Trim() }, { "before", _before.Text.Trim() }, { "after", _after.Text.Trim() },
                { "hud", tri(_hud, "show", "hide") }, { "codcast", tri(_film, "on", "off") },
                { "depth", tri(_depth, "on", "off") }, { "depthdist", _dist.Text.Trim() }, { "plate", tri(_plate, "on", "off") },
                { "noplayers", tri(_noPlayers, "on", "off") }, { "playersonly", tri(_playersOnly, "on", "off") },
                { "codec", _codec.SelectedIndex > 0 ? _codec.SelectedItem as string : "" },
            };
            if (RecordingSettings.FromLook(file).Count == v.Count(kv => kv.Value.Length > 0) && RecordingSettings.FromLook(file).All(kv => { string x; return v.TryGetValue(kv.Key, out x) && x == kv.Value; })) return;
            try
            {
                RecordingSettings.WriteToLook(file, v);
                int n = v.Count(kv => kv.Value.Length > 0);
                _said.Text = n == 0 ? "This Global CFG says nothing about recording now." : "Saved: takes recorded with this Global CFG take its " + n + " recording setting(s) (copied into a queue when it is dropped on a take).";
            }
            catch (Exception ex) { App.Note("writing a Global CFG's recording part", ex); _said.Text = "Not saved: " + ex.Message; }
        }
    }
}
