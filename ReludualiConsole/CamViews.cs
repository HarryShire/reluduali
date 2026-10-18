using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ReludualiConsole
{
    /// <summary>
    /// The CAM QUEUE's 3D views together (2026-10-14). Mycelium: "allow the user to make as many as they like, and to pin ones
    /// they frequently use. Id personally have the Looping one on Always, and a Static view of the Overhead Camera Paths,
    /// and a Static Side View of Campaths with Wireframe, or Models Turned off, or rendered low visibility".
    ///
    /// + View adds one; a view's pin keeps it every time the Console opens (Presets\CAMQUEUE\views.txt, one line a view);
    /// an unpinned view lasts until the Console closes. The first view is the big one on top, the rest sit side by side
    /// under it; double-click a view's header to make it the big one. A view can pop out into a window of its own and comes
    /// back when that window closes. The speed is the loop's, for every view.
    /// </summary>
    public sealed class CamViewsPanel : UserControl
    {
        public CamScene Scene { get; private set; }
        private readonly List<CamView> _views = new List<CamView>();
        private readonly Dictionary<CamView, Window> _popped = new Dictionary<CamView, Window>();
        private readonly Grid _area = new Grid();
        private readonly Slider _speed = new Slider { Minimum = -3.3, Maximum = 1.0, Value = 0, Width = 130, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        private readonly TextBlock _speedText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 40, Margin = new Thickness(0, 0, 10, 0) };
        private readonly Popup _menu = new Popup { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
        private readonly Button _add;
        private bool _loading;

        /// <summary>The views follow the game's markers again (the host decides what that means).</summary>
        public event Action LiveRequested;

        private static string ViewsFile { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Presets", "CAMQUEUE", "views.txt"); } }

        public CamViewsPanel(CamScene scene)
        {
            Scene = scene;
            Foreground = Ui.Brush("Text");

            // the bar: the loop's speed for every view, follow the game, add a view
            var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
            bar.Children.Add(new TextBlock { Text = "Speed", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Foreground = Ui.Brush("Dim") });
            bar.Children.Add(_speed);
            bar.Children.Add(_speedText);
            foreach (double s in new[] { 0.1, 0.25, 0.5, 1.0 })
            {
                double v = s;
                var b = new Button { Content = v.ToString("0.##", CultureInfo.InvariantCulture) + "x", Padding = new Thickness(7, 2, 7, 2), MinHeight = 24, Margin = new Thickness(0, 0, 4, 0), FontSize = 12 };
                b.Click += (o, e) => Scene.Speed = v;
                bar.Children.Add(b);
            }
            _add = Ui.Button("View", ShowMenu, "Add a 3D view: through the camera, a fixed side (top, front, right...) or free. Pin it to keep it.", Glyph.Add);
            _add.Margin = new Thickness(0);
            var live = Ui.Button("Live", () => { var h = LiveRequested; if (h != null) h(); }, "The views follow the markers in the game again (instead of the picked row)", Glyph.Eye);
            live.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(_add, Dock.Right); DockPanel.SetDock(live, Dock.Right);
            bar.Children.Add(_add); bar.Children.Add(live);
            bar.Children.Add(new TextBlock { Text = "numpad 1 3 7 9 5 0", Opacity = 0.5, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
                                             ToolTip = "Click a view first. 1 front, 3 right, 7 top (Ctrl: back, left, bottom), 9 the opposite side, 5 orthographic / perspective,\n0 through the path's camera, 2 4 6 8 orbit, + - zoom, period or Home: the whole path. The top-row digits work too.\nDrag a camera marker to move it, Alt+drag to aim it, Ctrl+Z to undo." });

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            root.Children.Add(_area);
            Content = root;

            _speed.ValueChanged += (o, e) => { if (!_loading) Scene.Speed = Math.Pow(2, _speed.Value); ShowSpeed(); };
            Scene.SpeedChanged += () => { _loading = true; _speed.Value = Math.Log(Scene.Speed, 2); _loading = false; ShowSpeed(); };
            ShowSpeed();
            BuildMenu();
            LoadViews();
            // the row under the big view: as many across as fit at ~300 px each (a view's header needs that), at most 3
            _area.SizeChanged += (s, e) => { if (_rest != null && ColsFor(e.NewSize.Width, _rest.Children.Count) != _rest.ColumnDefinitions.Count) Layout(); };
        }

        private static int ColsFor(double width, int n)
        {
            int fit = width > 0 ? Math.Max(1, (int)(width / 300)) : 2;
            return Math.Max(1, Math.Min(n, Math.Min(3, fit)));
        }

        private void ShowSpeed() { _speedText.Text = Scene.Speed.ToString(Scene.Speed < 0.1 ? "0.00" : "0.0#", CultureInfo.InvariantCulture) + "x"; }

        public IList<CamView> Views { get { return _views.AsReadOnly(); } }

        /// <summary>The view tests drag markers in: the first one not looking through the camera (else the big one).</summary>
        public CamView EditView { get { return _views.FirstOrDefault(v => v.Angle != ViewAngle.Camera) ?? _views.FirstOrDefault(); } }

        // ---------------------------------------------------------------- adding and removing

        public CamView AddView(ViewAngle a, MapLook m, PlayersLook p, bool pinned, bool save = true)
        {
            var v = new CamView(Scene, a, m, p, pinned);
            v.CloseRequested += CloseView;
            v.PopRequested += TogglePop;
            v.MainRequested += MakeMain;
            v.SettingsChanged += x => SaveViews();
            _views.Add(v);
            Layout();
            if (save) SaveViews();
            return v;
        }

        private void CloseView(CamView v)
        {
            Window w;
            if (_popped.TryGetValue(v, out w)) { _popped.Remove(v); w.Content = null; w.Close(); }
            _views.Remove(v);
            v.Dispose();
            Layout();
            SaveViews();
        }

        private void MakeMain(CamView v)
        {
            if (!_views.Remove(v)) return;
            _views.Insert(0, v);
            Layout();
            SaveViews();
        }

        /// <summary>Pop out (Mycelium: "make the 3D Viewport view draggable out of the Console"): the view in a window of its
        /// own, which can go to another screen; closing it returns the view.</summary>
        public void TogglePop(CamView v)
        {
            Window w;
            if (_popped.TryGetValue(v, out w)) { w.Close(); return; }
            Detach(v);
            w = new Window { Title = "Reluduali - 3D view (" + v.Describe() + ")", Width = 1280, Height = 760, Content = v, Owner = Application.Current.MainWindow,
                             ShowInTaskbar = true, Background = Ui.Brush("VelvetDeep") };
            w.Closed += (s, e) =>
            {
                if (!_popped.ContainsKey(v)) return;          // closed by CloseView
                _popped.Remove(v);
                w.Content = null;
                v.SetPopped(false);
                Layout();
            };
            _popped[v] = w;
            v.SetPopped(true);
            Layout();
            w.Show();
        }

        public bool AnyPopped { get { return _popped.Count > 0; } }

        public void CloseWindows() { foreach (Window w in _popped.Values.ToList()) w.Close(); }

        private static void Detach(FrameworkElement e)
        {
            var p = e.Parent as Panel;
            if (p != null) p.Children.Remove(e);
            var c = e.Parent as ContentControl;
            if (c != null) c.Content = null;
            var d = e.Parent as Decorator;
            if (d != null) d.Child = null;
        }

        // ---------------------------------------------------------------- the layout: the big one on top, the rest beside each other under it

        private Grid _rest;

        private void Layout()
        {
            foreach (CamView v in _views) if (!_popped.ContainsKey(v)) Detach(v);
            if (_rest != null) _rest.Children.Clear();
            _area.Children.Clear();
            _area.RowDefinitions.Clear();
            _area.ColumnDefinitions.Clear();
            var docked = _views.Where(v => !_popped.ContainsKey(v)).ToList();
            if (docked.Count == 0)
            {
                _area.Children.Add(new TextBlock
                {
                    Text = _views.Count == 0 ? "No 3D views. + View adds one: through the camera (the loop), a fixed side, or free." : "Every view is in a window of its own: close it to bring it back here.",
                    Foreground = Ui.Brush("Dim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
                });
                return;
            }
            _area.Children.Add(docked[0]);
            if (docked.Count == 1) return;
            _area.RowDefinitions.Add(new RowDefinition { Height = new GridLength(57, GridUnitType.Star), MinHeight = 120 });
            _area.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            _area.RowDefinitions.Add(new RowDefinition { Height = new GridLength(43, GridUnitType.Star), MinHeight = 100 });
            Grid.SetRow(docked[0], 0);
            var split = new GridSplitter { Height = 8, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows, Cursor = System.Windows.Input.Cursors.SizeNS };
            split.Template = HorizontalSplitter();
            Grid.SetRow(split, 1);
            _area.Children.Add(split);
            int n = docked.Count - 1, cols = ColsFor(_area.ActualWidth, n), rows = (n + cols - 1) / cols;
            _rest = new Grid();
            for (int c = 0; c < cols; c++) _rest.ColumnDefinitions.Add(new ColumnDefinition());
            for (int r = 0; r < rows; r++) _rest.RowDefinitions.Add(new RowDefinition());
            for (int i = 0; i < n; i++)
            {
                CamView v = docked[i + 1];
                v.Margin = new Thickness(i % cols == 0 ? 0 : 4, i / cols == 0 ? 0 : 8, (i % cols == cols - 1) ? 0 : 4, 0);
                Grid.SetColumn(v, i % cols); Grid.SetRow(v, i / cols);
                _rest.Children.Add(v);
            }
            docked[0].Margin = new Thickness(0);
            Grid.SetRow(_rest, 2);
            _area.Children.Add(_rest);
        }

        private static ControlTemplate HorizontalSplitter()
        {
            var t = new ControlTemplate(typeof(GridSplitter));
            var grid = new FrameworkElementFactory(typeof(Grid));
            grid.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
            var line = new FrameworkElementFactory(typeof(Border));
            line.SetValue(FrameworkElement.HeightProperty, 2.0);
            line.SetValue(FrameworkElement.WidthProperty, 60.0);
            line.SetValue(Border.CornerRadiusProperty, new CornerRadius(1));
            line.SetValue(Border.BackgroundProperty, Ui.Brush("Line"));
            grid.AppendChild(line);
            t.VisualTree = grid;
            return t;
        }

        // ---------------------------------------------------------------- + View

        private void BuildMenu()
        {
            var p = new StackPanel { Margin = new Thickness(6) };
            Action<string, string, Action> item = (title, blurb, go) =>
            {
                var t = new StackPanel();
                t.Children.Add(new TextBlock { Text = title, Foreground = Ui.Brush("Text") });
                t.Children.Add(new TextBlock { Text = blurb, Foreground = Ui.Brush("Dim"), FontSize = 11.5 });
                var b = new Button { Content = t, Style = Ui.Style("QuietButton"), HorizontalContentAlignment = HorizontalAlignment.Left, HorizontalAlignment = HorizontalAlignment.Stretch,
                                     Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 0, 2) };
                b.Click += (s, e) => { _menu.IsOpen = false; go(); };
                p.Children.Add(b);
            };
            item("Through the camera", "the loop: what the path's camera sees, 1920x808", () => AddView(ViewAngle.Camera, MapLook.Solid, PlayersLook.On, false));
            item("Top", "fixed, from above: the paths as a floor plan", () => AddView(ViewAngle.Top, MapLook.Ghost, PlayersLook.On, false));
            item("Side", "fixed, from the right, in wireframe", () => AddView(ViewAngle.Right, MapLook.Wire, PlayersLook.On, false));
            item("Front", "fixed, from the front, in wireframe", () => AddView(ViewAngle.Front, MapLook.Wire, PlayersLook.On, false));
            item("Back / other side", "fixed, from the back; Ctrl+3 in a view for the left", () => AddView(ViewAngle.Back, MapLook.Wire, PlayersLook.On, false));
            item("Free", "orbit with the mouse, the map in colour", () => AddView(ViewAngle.Free, MapLook.Solid, PlayersLook.On, false));
            p.Children.Add(new Border { Height = 1, Background = Ui.Brush("Line"), Margin = new Thickness(8, 6, 8, 6), Opacity = 0.7 });
            item("Back to the default layout", "the loop, the top and the side, pinned - the views open now close", DefaultLayout);
            _menu.Child = new Border { Child = p, Background = Ui.Brush("CardBack"), BorderBrush = Ui.Brush("RimSoft"), BorderThickness = new Thickness(1),
                                       CornerRadius = new CornerRadius(3), MinWidth = 280 };
            _menu.PlacementTarget = _add;
        }

        private void ShowMenu() { _menu.PlacementTarget = _add; _menu.HorizontalOffset = -200; _menu.IsOpen = true; }

        /// <summary>Mycelium's own: the loop through the camera, a fixed top view, a fixed side view in wireframe - pinned.</summary>
        public void DefaultLayout()
        {
            foreach (CamView v in _views.ToList()) CloseView(v);
            AddView(ViewAngle.Camera, MapLook.Solid, PlayersLook.On, true, false);
            AddView(ViewAngle.Top, MapLook.Ghost, PlayersLook.On, true, false);
            AddView(ViewAngle.Right, MapLook.Wire, PlayersLook.On, true, false);
            SaveViews();
        }

        // ---------------------------------------------------------------- pins

        private void LoadViews()
        {
            _loading = true;
            try
            {
                if (File.Exists(ViewsFile))
                {
                    foreach (string line in File.ReadAllLines(ViewsFile))
                    {
                        string[] w = line.Trim().Split(' ');
                        if (w.Length < 3 || w[0].StartsWith("#", StringComparison.Ordinal)) continue;
                        ViewAngle a; MapLook m; PlayersLook p;
                        if (Enum.TryParse(w[0], true, out a) && Enum.TryParse(w[1], true, out m) && Enum.TryParse(w[2], true, out p)) AddView(a, m, p, true, false);
                    }
                    if (_views.Count > 0 || File.ReadAllText(ViewsFile).Contains("# none")) return;
                }
                DefaultLayout();
            }
            catch (Exception ex) { App.Note("reading the 3D views", ex); if (_views.Count == 0) DefaultLayout(); }
            finally { _loading = false; }
        }

        private void SaveViews()
        {
            if (_loading && _views.Count == 0) return;
            try
            {
                var sb = new StringBuilder("# the CAM QUEUE's pinned 3D views, the big one first: angle map players (Reluduali Console)\r\n");
                var pinned = _views.Where(v => v.Pinned).ToList();
                foreach (CamView v in pinned) sb.Append(v.Save()).Append("\r\n");
                if (pinned.Count == 0) sb.Append("# none\r\n");
                Directory.CreateDirectory(Path.GetDirectoryName(ViewsFile));
                File.WriteAllText(ViewsFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { App.Note("saving the 3D views", ex); }
        }

        /// <summary>For the pipe: each view's look, the big one first.</summary>
        public string List()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _views.Count; i++) sb.Append(i + 1).Append(": ").Append(_views[i].Describe()).Append(_popped.ContainsKey(_views[i]) ? " (popped out)" : "").Append("\n");
            return sb.Length == 0 ? "no views" : sb.ToString().TrimEnd();
        }

        public CamView View(int i) { return i >= 0 && i < _views.Count ? _views[i] : null; }

        public void Close(int i) { if (i >= 0 && i < _views.Count) CloseView(_views[i]); }

        public void Main(int i) { if (i >= 0 && i < _views.Count) MakeMain(_views[i]); }
    }
}
