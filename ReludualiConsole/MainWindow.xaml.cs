using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>
    /// The console's window: the tether in the header, the three stages on the start screen, and the tools in a left rail
    /// (2026-10-14; it was a strip of 21 tabs). Mycelium: "do some GUI Logic in how the Tabs are ordered".
    ///
    /// The tools are in working order within each stage. Principal Vegaography follows a day in the theater: find the film
    /// (Film), make it look right (Look), dress the game (Dress), reach any setting (Control), then shoot - queue the
    /// cameras and record (Shoot). Help and Contest are for every stage, so they sit in the header.
    ///
    /// The tether is looked at every 5 seconds off the UI thread: is Redacted running, does RipperBridge answer inside it.
    /// Re-attach drops the batch's hold on the game and takes it again, which is all a restarted game needs.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _tether = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private int _looking;

        /// <summary>A tool in the rail: its page is built once, when the console starts.</summary>
        private sealed class Tool
        {
            public string Key, Title, Stage, Group, Icon, Blurb;
            public Func<object> Make;
            public object Page;
            public RadioButton Rail;
        }

        private readonly List<Tool> _tools = new List<Tool>();
        private readonly Dictionary<string, StackPanel> _stageItems = new Dictionary<string, StackPanel>();
        private readonly Dictionary<string, Border> _stageHeads = new Dictionary<string, Border>();
        private readonly Dictionary<string, string> _lastInStage = new Dictionary<string, string>();
        private string _stage = "principal";
        private Tool _current;
        private readonly DispatcherTimer _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };

        private static readonly string[][] StageNames =
        {
            new[] { "pre", "Pre-Production", "this PC" },
            new[] { "principal", "Principal Vegaography", "the game: films, looks, cameras, recording" },
            new[] { "post", "Post-Production", "3D in Blender" },
        };

        // A stage with no tools in this edition (Post-Production): its card and its place in the rail say so, and it opens
        // nothing.
        private const string LaterCard = "Post-Production (3D in Blender) comes in a later version.";
        private const string LaterRail = "3D in Blender comes in a later version.";
        private bool StageIsLater(string stage) { return !_tools.Any(t => t.Stage == stage); }

        public MainWindow()
        {
            InitializeComponent();
            TitleName.Text = Version();                      // the title is "The Reluduali Designer" (MainWindow.xaml); "Console" stays the inside name
            try { Icon = Ui.WindowIcon; } catch (Exception ex) { App.Note("the window icon", ex); }
            App.ShowError = msg => Notice("Something in the window failed and was caught (the console keeps running): " + msg + "  Details: " + App.LogPath);

            // the tools, in working order. The BATCH page is built first: the CAM QUEUE runs its nights through it.
            Add("pre", null, "setup", "Setup", Glyph.Setting, "Where the game and the stores are on this PC, and what it has", () => new SetupControl());
            Add("principal", "Film", "demos", "Demos", Glyph.Film, "Find a film, load it into the theater, play, bookmark", () => new DemosControl());
            Add("principal", "Film", "theater", "Theater", Glyph.Theater, "Live passes for the edit: no HUD, no gun, FOV, blur, a green sky", Panels.Theater);
            Add("principal", "Look", "look", "Look", Glyph.Palette, "Per-map looks, exposure and colour, live", () => new LookControl());
            Add("principal", "Look", "fog", "Fog", Glyph.Cloud, "Distance fog, height fog, the sun's haze and blend modes, live", Panels.Fog);
            Add("principal", "Look", "sun", "Sun", Glyph.Sun, "The sun's light, colour and direction, and the sky, live", Panels.Sun);
            Add("principal", "Look", "ssao", "SSAO", Glyph.Contrast, "Ambient occlusion: the soft dark in corners and under things", Panels.Ssao);
            Add("principal", "Look", "dof", "DOF", Glyph.Eye, "Depth of field, near and far, live", Panels.Dof);
            Add("principal", "Look", "sky", "Sky", Glyph.Moon, "Install a sky set, or go back to the maps' own skies", () => new SkyControl());
            Add("principal", "Control", "cfg", "CFG", Glyph.Command, "Any setting or command, with completion; CFG files", () => new CfgControl());
            Add("principal", "Control", "custom", "Custom", Glyph.Sliders, "Your own sliders on any setting, with keys and MIDI", () => new CustomControl());
            Add("principal", "Shoot", "camqueue", "Cam Queue", Glyph.Camera, "Your camera paths and POV takes, queued for the night, and the 3D views", () => new CamQueueControl());
            Add("principal", "Shoot", "batch", "Batch", Glyph.Record, "Record a list of takes by itself", () =>
            {
                // the batch page's own colours are for a host with no look of its own: here the theme's
                var batch = new BatchControl();
                batch.Resources.Clear();
                batch.Foreground = Ui.Brush("Text");
                return batch;
            });
            Add("aside", null, "help", "Help", Glyph.Help, "How each tool works, for everyone testing it", () => new HelpControl());
            Add("aside", null, "contest", "Contest", Glyph.Stopwatch, "reluduali.com's contest timer, and the Lumit folder", () => new ContestControl());
            // 2026-10-18: the Reluduali sign-in, and sharing your camos, CFGs and presets on reluduali.com
            Add("aside", null, "account", "Account", Glyph.Person, "Your Reluduali account: sign in, friends, contests, and sharing on reluduali.com", () => Ui.Page(
                Ui.Card("Reluduali sign-in", "Your Reluduali account, the same one as on reluduali.com and in the Lumit panel.", new AccountControl()),
                Ui.Card("Share to reluduali.com", "Your global looks, page presets, CFGs, map looks, custom sliders and camo designs, on your page on reluduali.com for others to use.", new ShareControl())));

            // each page on its own: one that fails to build says so in its place, and the others still build
            foreach (Tool t in _tools.Where(x => x.Key == "batch").Concat(_tools.Where(x => x.Key != "batch"))) Build(t);
            BuildRail();
            BuildHome();
            InitWorkspace();
            // the playback engine (2026-10-17): a Reluduali recording gets the sliders' moves written for its every frame,
            // and RipperBridge applies them on those frames (exact moves); the live replay waits meanwhile
            BatchEngine.MovesTrack = (s, e, st, f) => KnobPanel.BakeMoves(s, e, st, f);
            BatchEngine.MovesRecording = on => KnobPanel.ExactRecording = on;

            if (!App.Tour)
            {
                // the contest timer (reluduali.com) and the Lumit folder: timing starts by itself when this PC holds a pass
                try { LumitFolder.Hook(); ContestTimer.Shared.StartIfLinked(); } catch (Exception ex) { App.Note("starting the contest timer", ex); }
                Closing += (s, e) => { try { ContestTimer.Shared.Stop(true); } catch { } RememberPageSize(); SavePlacement(); ReleaseWorkspace(); };
                _tether.Tick += (s, e) => LookAtTether();
                _tether.Start();
                LookAtTether();
                RestorePlacement();
            }
            _noticeTimer.Tick += (s, e) => { _noticeTimer.Stop(); NoticeBar.Visibility = Visibility.Collapsed; };
            // the header's sign-in, and the update notifier (both 2026-10-18)
            PaintAccount();
            if (!App.Tour)
            {
                AccountLink.SignedInOrOut += () => Dispatcher.BeginInvoke(new Action(PaintAccount));
                UpdateNotifier.Shared = new UpdateNotifier(Dispatcher, ShowUpdate);
                UpdateNotifier.Shared.Start();
            }
            UpdateLater.Click += (s, e) => { UpdateNotifier.Dismiss(UpdateBar.Tag as string); UpdateBar.Visibility = Visibility.Collapsed; };
            UpdateGo.Click += (s, e) => { AccountLink.OpenBrowser(UpdateGo.Tag as string); UpdateNotifier.Dismiss(UpdateBar.Tag as string); UpdateBar.Visibility = Visibility.Collapsed; };
            if (Unfinished.IsTest) TitleName.Text += "  -  Test Build";
            PreviewKeyDown += OnKeys;
            Loaded += (s, e) => { Glint(); if (_layout != WorkLayout.Classic) ApplyLayout(); else LayoutClassic.IsChecked = true; };   // one layout always shows as picked (B13)
            ShowHome();
            if (_layout != WorkLayout.Classic) OpenStage(_lastTool != null ? _tools.Find(t => t.Key == _lastTool).Stage : _stage);
        }

        private void Add(string stage, string group, string key, string title, string icon, string blurb, Func<object> make)
        {
            _tools.Add(new Tool { Stage = stage, Group = group, Key = key, Title = title, Icon = icon, Blurb = blurb, Make = make });
        }

        private static void Build(Tool t)
        {
            try { t.Page = t.Make(); }
            catch (Exception ex)
            {
                App.Note("building the " + t.Title + " page", ex);
                t.Page = new TextBlock { Text = "The " + t.Title + " page failed to build: " + ex.GetBaseException().Message, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
            }
        }

        // ---------------------------------------------------------------- the rail

        private void BuildRail()
        {
            RailPanel.Children.Clear();
            int n = 0;
            foreach (string[] st in StageNames)
            {
                n++;
                string stage = st[0];
                var head = StageHead(n, st[1], stage);
                RailPanel.Children.Add(head);
                var items = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
                string group = null;
                foreach (Tool t in _tools.Where(x => x.Stage == stage))
                {
                    if (t.Group != null && t.Group != group)
                    {
                        group = t.Group;
                        var kicker = new TextBlock { Text = group.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Dim"), Opacity = 0.85,
                                                     Margin = new Thickness(21, items.Children.Count == 0 ? 4 : 10, 0, 3) };
                        PaintExtension.Bind(kicker, TextBlock.FontFamilyProperty, "KickerFont");
                        items.Children.Add(kicker);
                    }
                    var label = new StackPanel { Orientation = Orientation.Horizontal };
                    label.Children.Add(new TextBlock { Text = t.Icon, FontFamily = Ui.IconFont, FontSize = 15, Width = 28, VerticalAlignment = VerticalAlignment.Center });
                    label.Children.Add(new TextBlock { Text = t.Title, VerticalAlignment = VerticalAlignment.Center });
                    var rb = new RadioButton { Style = Ui.Style("RailItem"), Content = label, ToolTip = t.Blurb, Tag = t.Key };
                    System.Windows.Automation.AutomationProperties.SetAutomationId(rb, "Rail_" + t.Key);
                    Tool me = t;
                    rb.Checked += (s, e) => { if (_current != me) ShowTool(me.Key); };
                    string cannot = Unfinished.ToolWhy(t.Key);          // a page whose tool this install does not carry: greyed, saying why
                    if (cannot != null) Unfinished.Grey(rb, cannot);
                    t.Rail = rb;
                    items.Children.Add(rb);
                }
                if (StageIsLater(stage))
                    items.Children.Add(new TextBlock { Text = LaterRail, FontSize = 11.5, FontStyle = FontStyles.Italic, Foreground = Ui.Brush("Dim"),
                                                       TextWrapping = TextWrapping.Wrap, Margin = new Thickness(21, 0, 12, 0) });
                _stageItems[stage] = items;
                RailPanel.Children.Add(items);
            }
        }

        /// <summary>A stage's heading in the rail: its number, its name; a click opens the stage (its last tool).</summary>
        private Border StageHead(int n, string name, string stage)
        {
            var badge = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0), BorderThickness = new Thickness(1),
                                     BorderBrush = Ui.Brush("RimSoft"), Background = Ui.Brush("CardBack"), VerticalAlignment = VerticalAlignment.Center,
                                     Child = new TextBlock { Text = n.ToString(CultureInfo.InvariantCulture), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Accent"),
                                                             HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var text = new TextBlock { Text = name.ToUpperInvariant(), FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("Dim") };
            PaintExtension.Bind(text, TextBlock.FontFamilyProperty, "KickerFont");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(badge); row.Children.Add(text);
            var b = new Border { Child = row, Padding = new Thickness(14, 9, 10, 7), Margin = new Thickness(6, 4, 8, 0), CornerRadius = new CornerRadius(2), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                                 ToolTip = StageNames.First(x => x[0] == stage)[2], Tag = text };
            if (StageIsLater(stage)) { b.Cursor = null; b.ToolTip = LaterCard; _stageHeads[stage] = b; return b; }    // nothing to open yet
            b.MouseEnter += (s, e) => b.Background = Ui.Brush("RailOver");
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (s, e) => OpenStage(stage);
            _stageHeads[stage] = b;
            return b;
        }

        private void ShowRailFor(string stage)
        {
            // a stage that comes later keeps its line under its name
            foreach (var kv in _stageItems) kv.Value.Visibility = kv.Key == stage || StageIsLater(kv.Key) ? Visibility.Visible : Visibility.Collapsed;
            foreach (var kv in _stageHeads) ((TextBlock)kv.Value.Tag).Foreground = Ui.Brush(kv.Key == stage ? "Chrome" : "Dim");
        }

        /// <summary>A stage: the tool last used in it, or its first.</summary>
        public void OpenStage(string stage)
        {
            string key;
            if (StageIsLater(stage)) return;                 // nothing to open yet
            if (!_lastInStage.TryGetValue(stage, out key)) key = _tools.First(t => t.Stage == stage).Key;
            ShowTool(key);
        }

        /// <summary>Brings a tool to the front: its stage, its page, the window (the pipe's "queue show", for pictures).</summary>
        public void ShowTool(string key)
        {
            Tool t = _tools.FirstOrDefault(x => x.Key == key);
            if (t == null) return;
            _current = t;
            if (t.Stage != "aside") { _stage = t.Stage; _lastInStage[_stage] = key; _lastTool = key; }
            ShowRailFor(_stage);
            foreach (Tool o in _tools) if (o.Rail != null && o != t && o.Rail.IsChecked == true) o.Rail.IsChecked = false;
            if (t.Rail != null && t.Rail.IsChecked != true) t.Rail.IsChecked = true;
            if (_layout == WorkLayout.Overlay) PageShown();
            else
            {
                if (_float != null && _float.Page == t.Page) _float.Page = null;
                PageHost.Content = t.Page;
            }
            PageTitle.Text = t.Title;
            PageBlurb.Text = t.Blurb;
            PageIcon.Text = t.Icon;
            string stageName = t.Stage == "aside" ? "Designer" : StageNames.First(x => x[0] == t.Stage)[1];
            CrumbStage.Text = stageName;
            CrumbSep.Visibility = Visibility.Visible;
            CrumbTool.Text = t.Title;
            StagesPanel.Visibility = Visibility.Collapsed;
            Rail.Visibility = Visibility.Visible;
            WorkArea.Visibility = Visibility.Visible;
            if (_layout != WorkLayout.Overlay) PageArea.Visibility = Visibility.Visible;
            if (!App.Tour)
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            }
        }

        /// <summary>Kept for the pipe and older callers: the CAM QUEUE's "queue show".</summary>
        public void ShowCamQueue() { ShowTool("camqueue"); if (!App.Tour) Activate(); }

        private void ShowHome()
        {
            if (_layout != WorkLayout.Classic && _current != null) { OpenStage(_stage); return; }    // the game's slot stays; the start screen is Classic's
            StagesPanel.Visibility = Visibility.Visible;
            Rail.Visibility = Visibility.Collapsed;
            WorkArea.Visibility = Visibility.Collapsed;
            PageHost.Content = null;
            _current = null;
            foreach (Tool o in _tools) if (o.Rail != null) o.Rail.IsChecked = false;
            CrumbStage.Text = "Start";
            CrumbSep.Visibility = Visibility.Collapsed;
            CrumbTool.Text = "";
            UpdateContinue();
        }

        public void ShowHomeForTour() { ShowHome(); }

        public IEnumerable<string> ToolKeys { get { return _tools.Select(t => t.Key); } }

        private void Home_Click(object sender, RoutedEventArgs e) { ShowHome(); }
        private void Help_Click(object sender, RoutedEventArgs e) { ShowTool("help"); }
        private void Contest_Click(object sender, RoutedEventArgs e) { ShowTool("contest"); }
        private void Account_Click(object sender, RoutedEventArgs e) { ShowTool("account"); }

        /// <summary>The header's account button: your name when signed in, "Sign in" when not.</summary>
        private void PaintAccount()
        {
            var me = App.Tour ? null : AccountLink.Load();
            AccountText.Text = me == null ? "Sign in" : me.Username;
        }

        /// <summary>The update notifier's bar: a newer version (Download) or a notice from the Reluduali Server.</summary>
        private void ShowUpdate(UpdateNotifier.Item item)
        {
            UpdateText.Text = item.Text;
            UpdateBar.Tag = item.Key;
            UpdateGo.Content = item.Button;
            UpdateGo.Tag = item.Url;
            UpdateGo.Visibility = item.Button != null && item.Url != null ? Visibility.Visible : Visibility.Collapsed;
            UpdateBar.Visibility = Visibility.Visible;
        }
        private void Site_Click(object sender, RoutedEventArgs e) { Ui.OpenWeb(TimerLink.DefaultServer); }

        private void OnKeys(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F1) { ShowTool("help"); e.Handled = true; }
            else if (e.Key == Key.Home && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { ShowHome(); e.Handled = true; }
        }

        // ---------------------------------------------------------------- the start screen

        private TextBlock _continue;

        private void BuildHome()
        {
            HomePanel.Children.Clear();
            // the Reluduali mark, pixel for pixel (half its 256 px: every dither cell stays square)
            var logo = Ui.Mark(128);
            logo.HorizontalAlignment = HorizontalAlignment.Center;
            HomePanel.Children.Add(logo);
            var word = new TextBlock { Text = "Reluduali", FontSize = 40, FontWeight = FontWeights.Light, Foreground = Ui.Brush("Chrome"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            PaintExtension.Bind(word, VisibilityProperty, "Night");
            HomePanel.Children.Add(word);
            // by day the name as the site's mark has it: RELUDUALI in its grown lettering, ink, the seeds in the seed colour
            var name = new Lettering { Height = 40, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 8) };
            PaintExtension.Bind(name, VisibilityProperty, "Day");
            HomePanel.Children.Add(name);
            HomePanel.Children.Add(new TextBlock { Text = "The Reluduali Designer: moviemaking for Black Ops II", FontSize = 14, Foreground = Ui.Brush("Dim"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 30) });

            var cards = new Grid();
            for (int i = 0; i < 3; i++) cards.ColumnDefinitions.Add(new ColumnDefinition());
            int n = 0;
            foreach (string[] st in StageNames)
            {
                var card = StageCard(n + 1, st[0], st[1], st[2]);
                Grid.SetColumn(card, n);
                cards.Children.Add(card);
                n++;
            }
            HomePanel.Children.Add(cards);
            _continue = new TextBlock { FontSize = 13.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0), Cursor = Cursors.Hand, Foreground = Ui.Brush("AccentPale") };
            _continue.MouseLeftButtonUp += (s, e) => { if (_lastTool != null) ShowTool(_lastTool); };
            HomePanel.Children.Add(_continue);
        }

        private string _lastTool;

        private void UpdateContinue()
        {
            if (_continue == null) return;
            Tool t = _lastTool == null ? null : _tools.FirstOrDefault(x => x.Key == _lastTool);
            _continue.Text = t == null ? "" : "Continue where you left off:  " + t.Title + "  ›";
            _continue.Visibility = t == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private FrameworkElement StageCard(int n, string stage, string name, string blurb)
        {
            var p = new StackPanel();
            var number = new TextBlock { Text = n.ToString(CultureInfo.InvariantCulture), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Accent"), Margin = new Thickness(0, 0, 0, 6) };
            PaintExtension.Bind(number, TextBlock.FontFamilyProperty, "KickerFont");
            p.Children.Add(number);
            p.Children.Add(Ui.Title(new TextBlock { Text = name, FontSize = 26, FontWeight = FontWeights.Light, Foreground = Ui.Brush("Chrome"), TextWrapping = TextWrapping.Wrap }, "BigWeight"));
            p.Children.Add(new TextBlock { Text = blurb, FontSize = 13.5, Foreground = Ui.Brush("Dim"), Margin = new Thickness(0, 6, 0, 18), TextWrapping = TextWrapping.Wrap });
            if (StageIsLater(stage))
            {
                // a stage that comes in a later version: its card says so, quieter than the others, and opens nothing
                p.Children.Add(new TextBlock { Text = LaterCard, FontSize = 13.5, FontStyle = FontStyles.Italic, Foreground = Ui.Brush("AccentPale"), TextWrapping = TextWrapping.Wrap });
                return new PrintCard { Child = p, Margin = new Thickness(10, 0, 10, 0), Padding = new Thickness(22, 20, 22, 22), CornerRadius = new CornerRadius(3), Opacity = 0.7 };
            }
            var chips = new WrapPanel();
            string group = null;
            foreach (Tool t in _tools.Where(x => x.Stage == stage))
            {
                Tool me = t;
                if (t.Group != null && t.Group != group && group != null) chips.Children.Add(new Border { Width = 1, Height = 18, Background = Ui.Brush("Line"), Margin = new Thickness(2, 0, 8, 6), VerticalAlignment = VerticalAlignment.Center });
                group = t.Group;
                var chip = new Button { Content = Ui.IconText(t.Icon, t.Title), ToolTip = t.Blurb, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 4, 10, 4), FontSize = 12.5 };
                chip.Click += (s, e) => { ShowTool(me.Key); e.Handled = true; };
                string cannot = Unfinished.ToolWhy(t.Key);
                if (cannot != null) Unfinished.Grey(chip, cannot);
                chips.Children.Add(chip);
            }
            p.Children.Add(chips);
            var open = Ui.Primary(new Button { Content = "Open " + name, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
            open.Click += (s, e) => OpenStage(stage);
            p.Children.Add(open);
            var card = new PrintCard { Child = p, Margin = new Thickness(10, 0, 10, 0), Padding = new Thickness(22, 20, 22, 22), CornerRadius = new CornerRadius(3) };
            card.MouseEnter += (s, e) => card.BorderBrush = Ui.Brush("RimHi");
            card.MouseLeave += (s, e) => card.BorderBrush = Ui.Brush("RimSoft");
            return card;
        }

        /// <summary>One sweep of the chrome's glint along the header's rim, when the window opens (then still: no idle drawing).</summary>
        private void Glint()
        {
            try
            {
                var brush = ((LinearGradientBrush)Ui.Brush("RimLine")).CloneCurrentValue();
                HeaderRim.Background = brush;
                var shift = new TranslateTransform();
                brush.RelativeTransform = shift;
                var sweep = new DoubleAnimation(-0.6, 0, TimeSpan.FromMilliseconds(1400)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                // then the rim the skin keeps (it follows the pointer, and day and night)
                sweep.Completed += (s, e) => HeaderRim.Background = Ui.Brush("RimLine");
                shift.BeginAnimation(TranslateTransform.XProperty, sweep);
            }
            catch (Exception ex) { App.Note("the header's glint", ex); }
        }

        // ---------------------------------------------------------------- the window

        private static string Version()
        {
            Version v = typeof(MainWindow).Assembly.GetName().Version;
            return v.Major + "." + v.Minor + "." + v.Build;
        }

        private static string PlacementFile { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Presets", "console-window.txt"); } }

        /// <summary>The window where it was last time, and the tool last used (for the start screen's Continue).</summary>
        private void RestorePlacement()
        {
            try
            {
                if (!File.Exists(PlacementFile))
                {
                    // the first start: the whole window on the screen it opens on, centred (1480x940 ran under the taskbar on
                    // a 1080p screen, and Setup's last card could not be reached: the fake-mouse test's B7)
                    Rect wa = SystemParameters.WorkArea;
                    Width = Math.Max(MinWidth, Math.Min(Width, wa.Width - 24));
                    Height = Math.Max(MinHeight, Math.Min(Height, wa.Height - 24));
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
                    Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
                    return;
                }
                var d = File.ReadAllLines(PlacementFile).Select(line => line.Split(new[] { '=' }, 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
                string v;
                if (d.TryGetValue("tool", out v) && _tools.Any(t => t.Key == v)) _lastTool = v;
                double l, t2, w, h;
                if (d.TryGetValue("rect", out v))
                {
                    string[] r = v.Split(',');
                    if (r.Length == 4 && double.TryParse(r[0], NumberStyles.Float, CultureInfo.InvariantCulture, out l) && double.TryParse(r[1], NumberStyles.Float, CultureInfo.InvariantCulture, out t2)
                        && double.TryParse(r[2], NumberStyles.Float, CultureInfo.InvariantCulture, out w) && double.TryParse(r[3], NumberStyles.Float, CultureInfo.InvariantCulture, out h))
                    {
                        // only where a screen still is (a monitor unplugged since puts it back in the middle)
                        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                        if (screen.Contains(new Point(l + 40, t2 + 20)) && w >= MinWidth && h >= MinHeight)
                        {
                            WindowStartupLocation = WindowStartupLocation.Manual;
                            Left = l; Top = t2; Width = w; Height = h;
                        }
                    }
                }
                if (d.TryGetValue("max", out v) && v == "1") WindowState = WindowState.Maximized;
                ReadLayoutSettings(d);
                UpdateContinue();
            }
            catch (Exception ex) { App.Note("restoring the window's place", ex); }
        }

        private void SavePlacement()
        {
            try
            {
                Rect r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                string tool = _current != null ? _current.Key : _lastTool;
                Directory.CreateDirectory(Path.GetDirectoryName(PlacementFile));
                File.WriteAllText(PlacementFile, "rect=" + string.Join(",", new[] { r.Left, r.Top, r.Width, r.Height }.Select(x => x.ToString("0", CultureInfo.InvariantCulture)))
                                                 + "\nmax=" + (WindowState == WindowState.Maximized ? "1" : "0") + "\ntool=" + (tool ?? "") + "\n" + LayoutSettings());
            }
            catch (Exception ex) { App.Note("saving the window's place", ex); }
        }

        // ---------------------------------------------------------------- notices and the tether

        private void Notice(string text, bool good = false)
        {
            NoticeText.Text = text;
            NoticeBar.Background = new SolidColorBrush(good ? Color.FromArgb(0x2A, 0x5F, 0xDD, 0xA0) : Color.FromArgb(0x33, 0xFF, 0x64, 0x82));
            NoticeBar.BorderBrush = new SolidColorBrush(good ? Color.FromArgb(0x66, 0x5F, 0xDD, 0xA0) : Color.FromArgb(0x66, 0xFF, 0x64, 0x82));
            NoticeBar.Visibility = Visibility.Visible;
            _noticeTimer.Stop();
            if (good) _noticeTimer.Start();
        }

        private void NoticeClose_Click(object sender, RoutedEventArgs e) { NoticeBar.Visibility = Visibility.Collapsed; }

        private void LookAtTether()
        {
            if (Interlocked.Exchange(ref _looking, 1) == 1) return;
            bool busy = BatchControl.Instance != null && BatchControl.Instance.IsBusy;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string game, bridge;
                bool gameUp = false, bridgeUp = false;
                try
                {
                    Process[] found = Process.GetProcessesByName("t6mpv43");
                    if (found.Length == 0) game = "Redacted: not running";
                    else
                    {
                        gameUp = true;
                        game = "Redacted: pid " + found[0].Id;
                    }

                    if (!gameUp) bridge = "RipperBridge: -";
                    else if (busy) { bridge = "RipperBridge: in use by the batch"; bridgeUp = true; }
                    else
                    {
                        // One try, short: the bridge's pipe has one instance, and the batch comes first.
                        string pong = BridgeClient.Ask("ping", 600, 1);
                        bridgeUp = pong != null && pong.StartsWith("pong", StringComparison.Ordinal);
                        bridge = bridgeUp ? "RipperBridge " + pong.Substring(pong.LastIndexOf(' ') + 1) : "RipperBridge: not answering";
                    }
                }
                catch (Exception ex)
                {
                    game = "Redacted: ?"; bridge = "RipperBridge: ?";
                    App.Note("looking at the tether", ex);
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Show(GameDot, GameText, gameUp, game);
                    Show(BridgeDot, BridgeText, bridgeUp, bridge);
                    StartGameButton.Visibility = gameUp ? Visibility.Collapsed : Visibility.Visible;
                    if (_layout != WorkLayout.Classic && gameUp) DockGame();
                    if (gameUp && bridgeUp) KeepDolly();
                    try { ContestText.Text = ContestTimer.Shared.Running ? "Contest: timing" : "Contest"; } catch { }
                    Interlocked.Exchange(ref _looking, 0);
                }));
            });
        }


        private void Show(System.Windows.Shapes.Ellipse dot, TextBlock text, bool good, string what)
        {
            dot.Fill = (Brush)FindResource(good ? "Good" : "Bad");
            text.Text = what;
        }

        /// <summary>Mycelium, 2026-10-16: start Redacted from inside the Console (the installer asks for its folder).</summary>
        private void StartGame_Click(object sender, RoutedEventArgs e)
        {
            string said = Where.StartGame();
            Notice(said, said.StartsWith("Starting", StringComparison.Ordinal));
            LookAtTether();
        }

        private void Reattach_Click(object sender, RoutedEventArgs e)
        {
            BatchControl tab = BatchControl.Instance;
            string result = tab == null ? "the Batch page is not built" : tab.Reattach();
            DvarTable.Reset();      // a restarted game: its dvar records are found again
            Notice("Re-attach: " + result, result.StartsWith("Redacted pid", StringComparison.Ordinal));
            LookAtTether();
        }

    }
}
