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
    /// <summary>
    /// The CAM QUEUE tab (2026-10-14). Mycelium: "Que up 12+ Campaths per demo and then just have them record one after the
    /// other all at once at night", "As many as I am able to que during the day accross multple demos alongside their
    /// POV's with specific CFG specifications for POV and for CINEs", "I do clear my markers" (so one path per save).
    ///
    /// SAVE MY MARKERS reads the dolly markers he placed in Edit Camera out of the running game (read only), turns them
    /// into the track the batch plays (the same curve the game's own dolly flies: measured to 2 units), and adds a CINE
    /// row. POV in / out / Add POV take does the same for a player's own view. The queue is a folder of ordinary files
    /// (CamQueue); RUN QUEUE hands it to the BATCH tab's engine with the BATCH tab's settings. The 3D view loops the path
    /// he is placing as soon as two markers exist, at the speed he sets.
    /// </summary>
    public sealed class CamQueueControl : UserControl
    {
        /// <summary>The Cam Queue (the cinematics' tab): the night's runs, the pipe's "queue ...".</summary>
        public static CamQueueControl Instance { get; private set; }
        /// <summary>The POV Queue (2026-10-18): the same queue, its POV rows.</summary>
        public static CamQueueControl PovInstance { get; private set; }
        private static readonly List<CamQueueControl> Pages = new List<CamQueueControl>();

        // one queue for both tabs: opened on either, shown on both
        private static CamQueue _sharedQueue;
        private CamQueue _q { get { return _sharedQueue; } set { _sharedQueue = value; } }
        /// <summary>The open queue (the Batch page edits its rows and its kinds' settings), or null.</summary>
        public static CamQueue Queue { get { return _sharedQueue; } }
        /// <summary>Raised on the window's thread when the queue changed (a row added, moved, removed; another queue opened).</summary>
        public static event Action QueueChanged;
        private readonly string _kind;
        private CamQueue _shownQueue;
        // the queue rows this tab's list shows, in order (its list index -> the queue's row index)
        private readonly List<int> _shown = new List<int>();
        private readonly TextBlock _povRecording = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("Dim") };
        private readonly GameClock _game = new GameClock();
        private readonly CamScene _scene = new CamScene();
        private readonly CamViewsPanel _views;
        private readonly ListView _list = new ListView { BorderThickness = new Thickness(0) };
        private readonly TextBox _queueBox = new TextBox { Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Tonight's queue: a folder of ordinary files (queue.csv, the paths, the CFGs)" };
        private readonly TextBlock _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Foreground = Ui.Brush("Dim"), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _live = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 2, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("Dim") };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 0, 0), Foreground = Ui.Brush("AccentPale") };
        private readonly TextBox _note = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "A note on the picked row: saved as you type" };
        private readonly TextBlock _defaultsText = new TextBlock { Foreground = Ui.Brush("Dim"), FontWeight = FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        private readonly TextBox _povFps = Small(), _povBefore = Small(), _povAfter = Small(), _cineFps = Small(), _cineBefore = Small(), _cineAfter = Small();
        private readonly TextBox _jumpN = new TextBox { Width = 40, Text = "1", VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Which of your markers (1 = the first)" };
        private bool _jumping;
        private readonly ComboBox _povHud = HudBox(), _cineHud = HudBox(), _povMode = ModeBox(), _cineMode = ModeBox();
        private readonly CheckBox _quitAfter = new CheckBox { Content = "Then quit the game cleanly", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 18, 0) };
        private readonly CheckBox _shutdownAfter = new CheckBox { Content = "Then shut down the PC", IsChecked = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        private readonly DispatcherTimer _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly DispatcherTimer _noteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        // Apply to game, and keeping the game in step with every edit and every row picked (2026-10-18)
        private readonly CheckBox _inStep = new CheckBox { Content = "Keep the game in step", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 10, 6),
                                                           ToolTip = "Every edit in the 3D view, and every CINE row you pick, goes into the game's Edit Camera by itself (the film moves to place each marker)" };
        private readonly DispatcherTimer _stepTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        private int _applying;
        private bool _loadingDefaults;

        // what the game shows now
        private string _liveSig = "", _liveMap, _liveWhy = "";
        private int _liveTheater;
        private List<DollyMarker> _liveMarkers;
        private CamTrack _liveTrack;
        private bool _viewLive = true;
        private DateTime _attachTry = DateTime.MinValue;

        // POV in / out, marked live
        private int _povIn = -1, _povOut = -1;
        private string _povName = "";

        // the note box: saved as typed and on every way of leaving it (65 of Mycelium's notes were lost once, 2026-10-13)
        private QueueRow _noteRow;

        private static string SettingsFile { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Presets", "CAMQUEUE", "camqueue.txt"); } }

        /// <summary>
        /// One queue, two tabs (2026-10-18; Mycelium: "in the shoot section, we should also have something called, say, POV Queue
        /// ... the name of the cam queue isn't necessarily so explicit"; "In the cam queue, you do your cinematics. In the POV
        /// view ... you do your POVs ... And then in the batch queue, you could then record them all at once"). "cine": the Cam
        /// Queue (markers, paths, the 3D views); "pov": the POV Queue (POV in / out, the POV takes). Both show the same queue
        /// folder, each its own kind of row; the night (check, rehearse, record all) is on the Batch page (NightCard).
        /// </summary>
        public CamQueueControl(string kind = "cine")
        {
            _kind = kind == "pov" ? "pov" : "cine";
            if (_kind == "cine") Instance = this; else PovInstance = this;
            lock (Pages) Pages.Add(this);
            Foreground = (Brush)Application.Current.FindResource("Text");
            BuildList();
            _views = new CamViewsPanel(_scene);

            var root = new Grid { Margin = new Thickness(12, 0, 12, 2) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // row 0: tonight's queue - the folder, open / new / show it, and what is in it (the same queue on both tabs)
            var qbar = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            qbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            qbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
            qbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            qbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 440 });
            var qLabel = new TextBlock { Text = "Queue", FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Chrome"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
                                         ToolTip = "The POV Queue and the Cam Queue share one queue: POV takes here and there, cinematics in the Cam Queue, all recorded from the Batch page" };
            var qButtons = new StackPanel { Orientation = Orientation.Horizontal };
            qButtons.Children.Add(Btn("New for tonight", (s, e) => NewQueue(), "A new queue folder for tonight", Glyph.Add));
            qButtons.Children.Add(Btn("Open", (s, e) => OpenQueue(_queueBox.Text.Trim()), "Open the folder typed here as the queue (made if it is new)", Glyph.Folder));
            qButtons.Children.Add(Btn("Show files", (s, e) => { if (_q != null) Process.Start("explorer.exe", "\"" + _q.Dir + "\""); }, "The queue's folder in Explorer"));
            Grid.SetColumn(_queueBox, 1); Grid.SetColumn(qButtons, 2); Grid.SetColumn(_summary, 3);
            qbar.Children.Add(qLabel); qbar.Children.Add(_queueBox); qbar.Children.Add(qButtons); qbar.Children.Add(_summary);
            Grid.SetRow(qbar, 0); root.Children.Add(qbar);

            // row 1: the list and what acts on it | (the Cam Queue) the 3D views
            var main = new Grid();
            var left = new DockPanel();

            // capture: what is on the theater's screen now goes into the queue
            var capture = new WrapPanel();
            if (_kind == "cine")
            {
                var save = Btn("SAVE MY MARKERS", (s, e) => Report(SaveMarkers(null)), "Read the markers you placed in Edit Camera and add them to the queue as a cinematic. Nothing in the game changes.", Glyph.Camera);
                save.Style = Ui.Style("PrimaryButton");
                save.Padding = new Thickness(16, 7, 16, 7); save.FontSize = 13.5;
                capture.Children.Add(save);
                capture.Children.Add(Ui.Gap());
                // 2026-10-17: the film to one of his markers, the game's own jump (F on it does the same, then opens its menu)
                capture.Children.Add(new TextBlock { Text = "Marker", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6), Foreground = Ui.Brush("Dim") });
                _jumpN.Margin = new Thickness(0, 0, 6, 6);
                capture.Children.Add(_jumpN);
                capture.Children.Add(Btn("Jump", (s, e) => JumpToMarker(), "The film to that marker of yours (1 = the first), as F on it does, without its menu. The camera goes onto the marker; Shift+click keeps the camera where it is."));
            }
            else
            {
                var pin = Btn("POV in", (s, e) => Report(MarkPov(true)), "The film's time now is where a POV take starts (and who is being watched is its player)");
                pin.Style = Ui.Style("PrimaryButton"); pin.Padding = new Thickness(16, 7, 16, 7);
                var pout = Btn("POV out", (s, e) => Report(MarkPov(false)), "The film's time now is where it ends");
                pout.Style = Ui.Style("PrimaryButton"); pout.Padding = new Thickness(16, 7, 16, 7);
                capture.Children.Add(pin);
                capture.Children.Add(pout);
                capture.Children.Add(Btn("Add POV take", (s, e) => Report(AddPovLive(null)), "Add the POV take between the in and out you marked", Glyph.Add));
                capture.Children.Add(Ui.Gap());
                // the loop and the POV markers, separate, meeting only here (Mycelium 2026-10-18: "two separate ones that work together")
                capture.Children.Add(Btn("From the loop", (s, e) => Report(PovFromLoop()), "POV in and out from the timeline's loop bracket. The loop stays as it is."));
                capture.Children.Add(Btn("Loop it", (s, e) => Report(LoopPov()), "The timeline's loop bracket to the picked take's in and out (or the in and out marked here), and the loop on. The take does not change when the loop moves.", Glyph.Play));
                capture.Children.Add(Ui.Gap());
                capture.Children.Add(Btn("Go to its start", (s, e) => Report(GoToStart(Sel())), "The film to the picked take's start (POV in), on its player", Glyph.Play));
            }
            var captureCard = new PrintCard { Padding = new Thickness(12, 10, 12, 8), Margin = new Thickness(0, 0, 0, 8) };
            var capturePanel = new StackPanel();
            capturePanel.Children.Add(capture);
            capturePanel.Children.Add(_live);
            if (_kind == "pov")
            {
                // where the POV's fps is (Mycelium: "it was also kind of annoying ... looking for where the pov was going to have its fps")
                _povRecording.Margin = new Thickness(2, 6, 0, 0);
                capturePanel.Children.Add(_povRecording);
            }
            captureCard.Child = capturePanel;
            DockPanel.SetDock(captureCard, Dock.Top); left.Children.Add(captureCard);

            // the picked row: its note, then what can be done to it, grouped
            var edit = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            edit.Children.Add(Btn("", (s, e) => MoveSel(-1), "Up: earlier in the night", Glyph.Up));
            edit.Children.Add(Btn("", (s, e) => MoveSel(1), "Down: later in the night", Glyph.Down));
            edit.Children.Add(Ui.Gap());
            edit.Children.Add(Btn("Rename", (s, e) => RenameSel(), "The row's own name in this list", Glyph.Edit));
            edit.Children.Add(Btn("Frag name", (s, e) => FragSel(), "The frag this row belongs to: its recordings folder and its files' names. It starts as the name of the folder that holds the demo; changed here, it changes for every row of that frag"));
            edit.Children.Add(Btn("Duplicate", (s, e) => { int i = Sel(); if (i >= 0) { FlushNote(); _q.Duplicate(i); Refresh(i + 1); } }, "A second row with the same " + (_kind == "cine" ? "path" : "take") + " (its own name and folder)", Glyph.Copy));
            edit.Children.Add(Btn("Own CFG", (s, e) => OwnCfg(), "This row gets a CFG of its own (a copy of its kind's, to change)"));
            if (_kind == "cine")
            {
                edit.Children.Add(Ui.Gap());
                edit.Children.Add(Btn("Preview in game", (s, e) => Report(Preview(Sel())), "Play this path through the game's free camera now (its film loaded in the theater)", Glyph.Play));
                edit.Children.Add(Btn("Stop preview", (s, e) => Report(StopPreview()), null));
                // 2026-10-18 (Mycelium: "you could see the camera path looping inside of the game on the dolly camera mode")
                edit.Children.Add(Btn("Loop in the game's dolly", (s, e) => Report(PreviewInDolly(Sel())), "The game's own Dolly Camera flies this path over and over: its markers go into the game, the game goes to Dolly Camera from whatever view it is in, and the timeline's loop bracket goes over the path. Loop (under the game) stops it.", Glyph.Play));
                // 2026-10-18: the markers shown here put into the game (Mycelium: "Apply ... Camera ... Button")
                edit.Children.Add(Btn("Apply to game", (s, e) => Report(ApplyToGame()), "Put the markers shown here into the game in place of its own: the game's Edit Camera then has exactly these (positions, roll, FOV). The game must be in Edit Camera.", Glyph.Camera));
                edit.Children.Add(_inStep);
            }
            edit.Children.Add(Ui.Gap());
            edit.Children.Add(Btn("Remove", (s, e) => RemoveSel(), "Out of the queue; its files go to the queue's removed folder, never deleted", Glyph.Delete));
            DockPanel.SetDock(edit, Dock.Bottom);

            var noteRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var noteLabel = new TextBlock { Text = "Note", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Foreground = Ui.Brush("Dim") };
            DockPanel.SetDock(noteLabel, Dock.Left);
            noteRow.Children.Add(noteLabel);
            noteRow.Children.Add(_note);
            DockPanel.SetDock(noteRow, Dock.Bottom);

            // the Batch page records them all (Mycelium: "in the batch queue, you could then record them all at once")
            var toBatch = new TextBlock { Margin = new Thickness(2, 10, 0, 0), Foreground = Ui.Brush("Dim"), TextWrapping = TextWrapping.Wrap };
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("the Batch page")) { Foreground = Ui.Brush("Accent") };
            link.Click += (s, e) => { var w = Application.Current.MainWindow as MainWindow; if (w != null) w.ShowTool("batch"); };
            toBatch.Inlines.Add(new System.Windows.Documents.Run((_kind == "cine" ? "Cinematics" : "POV takes") + " are recorded from "));
            toBatch.Inlines.Add(link);
            toBatch.Inlines.Add(new System.Windows.Documents.Run(": each take's fps, passes, look and codec are there, with Record all."));
            DockPanel.SetDock(toBatch, Dock.Bottom);

            left.Children.Add(toBatch);
            left.Children.Add(edit);
            left.Children.Add(noteRow);
            left.Children.Add(_list);
            if (_kind == "cine")
            {
                main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(43, GridUnitType.Star), MinWidth = 380 });
                main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(57, GridUnitType.Star), MinWidth = 320 });
                Grid.SetColumn(left, 0); main.Children.Add(left);
                var split = new GridSplitter { Width = 10, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns, Cursor = Cursors.SizeWE };
                Grid.SetColumn(split, 1); main.Children.Add(split);
                Grid.SetColumn(_views, 2); main.Children.Add(_views);
            }
            else main.Children.Add(left);
            Grid.SetRow(main, 1); root.Children.Add(main);

            Grid.SetRow(_status, 2); root.Children.Add(_status);
            Content = root;

            _list.SelectionChanged += (s, e) => { FlushNote(); ShowSelected(); };
            _note.TextChanged += (s, e) => { if (_noteRow != null) { _noteTimer.Stop(); _noteTimer.Start(); } };
            _note.LostKeyboardFocus += (s, e) => FlushNote();
            _noteTimer.Tick += (s, e) => FlushNote();
            Unloaded += (s, e) => FlushNote();
            Dispatcher.ShutdownStarted += (s, e) => { FlushNote(); if (_kind == "cine") SaveSettings(); _views.CloseWindows(); };
            _scene.MarkersEdited += OnMarkersEdited;
            _scene.MarkersEdited += ms => { if (_inStep.IsChecked == true) { _stepTimer.Stop(); _stepTimer.Start(); } };
            _stepTimer.Tick += (s, e) => { _stepTimer.Stop(); Report(ApplyToGame()); };
            _scene.SpeedChanged += SaveSettings;
            _views.LiveRequested += () => { _viewLive = true; _list.SelectedIndex = -1; ShowLiveInView(true); };
            IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) { PaintPovRecording(); if (_q != null && _q != _shownQueue) { _queueBox.Text = _q.Dir; Refresh(); } } };

            _poll.Tick += (s, e) => PollGame();
            if (!App.Tour) _poll.Start();
            if (_kind == "cine")
            {
                BatchEngine.TakeFinished += OnTakeFinished;
                BatchPipe.Extra["queue"] = rest => OnUi(() => Pipe(rest));
                LoadSettings();
            }
            else if (_q != null) { _queueBox.Text = _q.Dir; Refresh(); }
            _scene.Clear("Place two or more markers in the theater's Edit Camera: the path loops here.");
        }

        // ---------------------------------------------------------------- the night, on the Batch page (2026-10-18)

        private FrameworkElement _nightCard;

        /// <summary>
        /// Check, Rehearse, Record all, Stop, what happens after, and how the files are named: the night's card, shown on the
        /// Batch page (Mycelium: "in the batch queue, you could then record them all at once"). The Cam Queue keeps the night's
        /// logic (every row once, the failed ones once more, the status file, quit and shut down); only its card moved.
        /// </summary>
        public FrameworkElement NightCard()
        {
            if (_nightCard != null) return _nightCard;
            var run = new WrapPanel();
            run.Children.Add(Btn("Check", (s, e) => Report(Check()), "Every demo, track and CFG found, now rather than at 3 am", Glyph.Check));
            run.Children.Add(Btn("Rehearse", (s, e) => Report(Run(true)), "Goes through the whole queue without recording (a dry run)"));
            var go = Btn("Record all", (s, e) => Report(Run(false)), "Record every take in the queue, one after the other, each as its row says; the ones that fail once more at the end", Glyph.Record);
            go.Style = Ui.Style("PrimaryButton");
            go.Padding = new Thickness(18, 7, 18, 7);
            run.Children.Add(go);
            var stop = Btn("Stop", (s, e) => Report(StopRun()), "Stops after the current step", Glyph.Stop);
            stop.Style = Ui.Style("DangerButton");
            run.Children.Add(stop);
            var after = new WrapPanel();
            after.Children.Add(_quitAfter);
            after.Children.Add(_shutdownAfter);
            // the files' names (Mycelium, 2026-10-16: "C and D, as Options"; D his pick): a frag's POV and cinematics in one folder
            var names = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            names.Children.Add(new TextBlock { Text = "How files are named", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Foreground = Ui.Brush("Dim") });
            var style = new ComboBox { MinWidth = 330, ToolTip = "Every take goes into Recordings\\<the frag>\\, with the POV and its cinematics side by side. Short names: edit short-names.txt in %LOCALAPPDATA%\\Reluduali\\Console (Standoff=Stnd)" };
            style.Items.Add("Short   (for example: POV_Stnd-Ballst-360nswb_t0001)");
            style.Items.Add("Full   (for example: Standoff ballista 360 ns wb - 440 - POV - take0001)");
            style.Items.Add("Old   (a folder per row, as before 0.6)");
            style.SelectedIndex = BatchRecorder.Naming.Style == "C" ? 1 : BatchRecorder.Naming.Style == "D" ? 0 : 2;
            style.SelectionChanged += (s, e) =>
            {
                BatchRecorder.Naming.Style = style.SelectedIndex == 1 ? "C" : style.SelectedIndex == 0 ? "D" : "";
                try { Where.Save("names", BatchRecorder.Naming.Style.Length > 0 ? BatchRecorder.Naming.Style : "old"); } catch { }
                Refresh(Sel());
            };
            names.Children.Add(style);
            var said = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0), Foreground = Ui.Brush("AccentPale") };
            _nightSaid = said;
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "The night", FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Application.Current.TryFindResource("TitleFont") ?? new FontFamily("Segoe UI"), FontSize = 14.5, Margin = new Thickness(0, 0, 0, 8) });
            panel.Children.Add(run);
            panel.Children.Add(names);
            panel.Children.Add(after);
            panel.Children.Add(said);
            _nightCard = new PrintCard { Padding = new Thickness(14, 10, 14, 8), Margin = new Thickness(0, 0, 0, 10), Child = panel };
            return _nightCard;
        }

        private TextBlock _nightSaid;

        // ---------------------------------------------------------------- small UI helpers

        private static TextBox Small() { return new TextBox { Width = 54, Margin = new Thickness(0, 0, 14, 6), VerticalContentAlignment = VerticalAlignment.Center }; }

        private static ComboBox HudBox()
        {
            var c = new ComboBox { Width = 104, Margin = new Thickness(0, 0, 14, 6) };
            c.Items.Add("BATCH's"); c.Items.Add("hidden"); c.Items.Add("shown");
            c.SelectedIndex = 0;
            return c;
        }

        /// <summary>The film mode per kind: the BATCH tab's Require CODCAST, CODCAST, or the regular theater.</summary>
        private static ComboBox ModeBox()
        {
            var c = new ComboBox { Width = 112, Margin = new Thickness(0, 0, 14, 6), ToolTip = "Cinematics need the regular theater (CODCAST has no dolly camera)" };
            c.Items.Add("BATCH's"); c.Items.Add("regular"); c.Items.Add("CODCAST");
            c.SelectedIndex = 0;
            return c;
        }

        private static TextBlock Label(string s) { return new TextBlock { Text = s, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6), Foreground = Ui.Brush("Dim") }; }

        private static Button Btn(string text, RoutedEventHandler click, string tip, string icon = null)
        {
            var b = new Button { Content = icon == null ? (object)text : Ui.IconText(icon, text), Margin = new Thickness(0, 0, 6, 6), ToolTip = tip };
            if (text.Length == 0) b.Padding = new Thickness(9, 5, 9, 5);
            b.Click += click;
            return b;
        }

        private FrameworkElement Profile(string title, string kind, TextBox fps, TextBox before, TextBox after, ComboBox hud, ComboBox mode)
        {
            var p = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            p.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Width = 84, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6), Foreground = Ui.Brush("Chrome") });
            p.Children.Add(Btn("CFG...", (s, e) => EditKindCfg(kind), "The CFG sent before every " + kind.ToUpperInvariant() + " row (a row's own CFG replaces it)"));
            p.Children.Add(Label("fps")); p.Children.Add(fps);
            p.Children.Add(Label("before s")); p.Children.Add(before);
            p.Children.Add(Label("after s")); p.Children.Add(after);
            p.Children.Add(Label("HUD")); p.Children.Add(hud);
            p.Children.Add(Label("film")); p.Children.Add(mode);
            fps.ToolTip = before.ToolTip = after.ToolTip = "Empty = the BATCH page's";
            return p;
        }

        private void Report(string s) { if (s == null) return; _status.Text = s; if (_nightSaid != null) _nightSaid.Text = s; }

        // ---------------------------------------------------------------- looks (2026-10-18)
        // The looks shelf (drag a Global CFG onto a row or a kind) is on the Batch page since Mycelium's test of 10-18 ("the looks
        // tab should be in the batch"); the list here shows each row's look.

        private string LookLabel(QueueRow r)
        {
            if (_q == null) return "";
            bool own;
            string name = _q.LookOf(r, out own);
            return name.Length == 0 ? "" : own ? name : "(" + name + ")";
        }
        // ---------------------------------------------------------------- the list

        private sealed class Item
        {
            public int No { get; set; }
            public string Kind { get; set; }
            public string Name { get; set; }
            public string Demo { get; set; }
            public string File { get; set; }
            public string Time { get; set; }
            public string Len { get; set; }
            public string From { get; set; }
            public string Note { get; set; }
            public string Look { get; set; }
        }

        private void BuildList()
        {
            var gv = new GridView();
            foreach (var c in new[] { new[] { "#", "No", "30" }, new[] { "Kind", "Kind", "46" }, new[] { "Name", "Name", "136" }, new[] { "Film time", "Time", "82" },
                                      new[] { "Len", "Len", "52" }, new[] { "Look", "Look", "120" }, new[] { "Frag (its folder)", "Demo", "170" }, new[] { "File", "File", "210" },
                                      new[] { "From", "From", "72" }, new[] { "Note", "Note", "200" } })
                gv.Columns.Add(new GridViewColumn { Header = c[0], DisplayMemberBinding = new System.Windows.Data.Binding(c[1]), Width = double.Parse(c[2], CultureInfo.InvariantCulture) });
            _list.View = gv;
            // the console's colours for a row picked or under the mouse (Theme.xaml's GridRow): Windows' own light highlight
            // left grey text on near-white (sandbox picture 0396b-2)
            _list.ItemContainerStyle = Ui.Style("GridRow");
            if (_list.ItemContainerStyle == null) _list.ItemContainerStyle = (Style)System.Windows.Markup.XamlReader.Parse(
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ListViewItem'>" +
                "<Setter Property='Foreground' Value='#FFE0E0E0'/>" +
                "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListViewItem'>" +
                "<Border x:Name='Bd' Background='Transparent' Padding='0,2,0,2'>" +
                "<GridViewRowPresenter Content='{TemplateBinding Content}' Columns='{TemplateBinding GridView.ColumnCollection}'/></Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='#FF2C2C2C'/></Trigger>" +
                "<Trigger Property='IsSelected' Value='True'><Setter TargetName='Bd' Property='Background' Value='#FF4B1D78'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
        }

        private bool Mine(QueueRow r) { return _kind == "cine" ? r.IsCine : !r.IsCine; }

        /// <summary>The list again, this tab's kind of rows; <paramref name="select"/> is a row of the queue (-1: none). The other
        /// tab follows (the same queue), and so does anything listening (the Batch page).</summary>
        private void Refresh(int select = -1) { RefreshOwn(select); foreach (CamQueueControl p in Pages.ToArray()) if (p != this) p.RefreshOwn(p.Sel()); if (QueueChanged != null) QueueChanged(); }

        private void RefreshOwn(int select)
        {
            _list.Items.Clear();
            _shown.Clear();
            _shownQueue = _q;
            if (_q == null) { _summary.Text = "no queue open"; PaintPovRecording(); return; }
            if (_queueBox.Text != _q.Dir) _queueBox.Text = _q.Dir;
            int i = 0;
            var kinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (QueueRow r in _q.Rows)
            {
                i++;
                // the file its take will be (Naming, 2026-10-16): the take number is the first free one when it records
                string key = r.Folder + "|" + (r.IsCine ? "cine" : "pov"), file = "";
                int n; kinds.TryGetValue(key, out n); kinds[key] = ++n;
                if (!Mine(r)) continue;
                if (BatchRecorder.Naming.Style == "C" || BatchRecorder.Naming.Style == "D")
                    file = BatchRecorder.Naming.Base(BatchRecorder.Naming.Style, r.Folder, !r.IsCine, n, 1) + "...";
                _shown.Add(i - 1);
                _list.Items.Add(new Item
                {
                    No = i, Kind = r.IsCine ? "CINE" : "POV", Name = r.Name, Demo = r.Folder, File = file,
                    Time = r.ClockIn + (r.ClockOut.Length > 0 ? "-" + r.ClockOut : ""),
                    Len = r.Seconds > 0 ? r.Seconds.ToString("0.0#", CultureInfo.InvariantCulture) + " s" : "",
                    From = r.IsCine ? r.Source : (r.Pov.Length > 0 ? r.Pov : "pov"), Note = r.Note,
                    // its own look as it is; its kind's in brackets (it changes when the kind's does)
                    Look = LookLabel(r)
                });
            }
            int at = _shown.IndexOf(select);
            if (at >= 0) _list.SelectedIndex = at;
            int mine = _shown.Count, others = _q.Rows.Count - mine;
            double sec = _q.Rows.Where(Mine).Sum(r => r.Seconds);
            int demos = _q.Rows.Where(Mine).Select(r => r.Demo.Length > 0 ? r.Demo : r.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            _summary.Text = mine + (_kind == "cine" ? " cinematic(s)" : " POV take(s)") + " on " + demos + " demo(s), " + sec.ToString("0.#", CultureInfo.InvariantCulture) + " s of film"
                            + (others > 0 ? "; and " + others + (_kind == "cine" ? " POV take(s) on the POV Queue" : " cinematic(s) on the Cam Queue") : "");
            PaintPovRecording();
        }

        /// <summary>The queue row picked in this tab's list, or -1.</summary>
        private int Sel() { int li = _list.SelectedIndex; return li >= 0 && li < _shown.Count ? _shown[li] : -1; }

        /// <summary>Picks a queue row in this tab's list (-1, or a row of the other kind: nothing picked).</summary>
        private void SelectRow(int queueRow) { _list.SelectedIndex = _shown.IndexOf(queueRow); }

        /// <summary>The POV Queue's line on how its takes record: the Batch's POV global (Mycelium: "kind of looking for where the pov
        /// was going to have its fps").</summary>
        private void PaintPovRecording()
        {
            if (_kind != "pov") return;
            if (_q == null) { _povRecording.Text = ""; return; }
            Func<string, string, string> d = (k, dflt) => { string v = _q.Default("pov_" + k); return v.Length == 0 || v == "tab" ? dflt : v; };
            string hud = d("hud", "shown"), mode = d("codcast", "on");
            _povRecording.Text = "POV takes record at " + d("fps", "1000") + " fps, " + d("before", "3") + " s before and " + d("after", "3") + " s after, HUD " + (hud == "hide" || hud == "off" ? "hidden" : "shown")
                                 + ", " + (mode == "off" ? "in the regular theater" : "in CODCAST") + (d("depth", "off") == "on" ? ", with a depth pass" : "")
                                 + ". Change it on the Batch page (POV takes), or per take in its list.";
        }

        /// <summary>The film to a POV take's start, on its player (the timeline's seek, then the batch's own way to the player).</summary>
        private string GoToStart(int i)
        {
            if (_q == null || i < 0 || i >= _q.Rows.Count) return "Pick a POV take first.";
            QueueRow r = _q.Rows[i];
            int tin = r.TickInValue;
            if (tin < 0) return "This take has no start tick.";
            TheaterTimeline tl = TheaterTimeline.Current;
            string who = r.Pov;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string said;
                try
                {
                    bool ok = tl != null && tl.SeekNow(tin);
                    if (!ok) { BridgeClient.Ask("kf jump " + tin.ToString(CultureInfo.InvariantCulture), 10000, 1); BridgeClient.Ask("cmd demo_pause 1", 800, 1); }
                    string slot = "";
                    if (who.Length > 0 && Attached()) { int sl = _game.SlotForName(who); if (sl >= 0) { BridgeClient.Ask("cmd demo_client " + sl.ToString(CultureInfo.InvariantCulture), 800, 1); slot = ", on " + who; } }
                    said = "The film is at " + r.ClockIn + slot + ".";
                }
                catch (Exception ex) { said = "Could not go there: " + ex.Message; }
                Dispatcher.BeginInvoke(new Action(() => Report(said)));
            });
            return "Going to " + r.Name + "...";
        }

        private void ShowSelected()
        {
            int i = Sel();
            _noteRow = null;
            if (_q == null || i < 0 || i >= _q.Rows.Count) { _note.Text = ""; return; }
            QueueRow r = _q.Rows[i];
            _note.Text = r.Note;
            _noteRow = r;
            if (r.IsCine)
            {
                try
                {
                    string ct = Path.IsPathRooted(r.CamPath) ? r.CamPath : Path.Combine(_q.Dir, r.CamPath);
                    CamTrack t = CamTrack.Read(ct);
                    string mj = Path.Combine(Path.GetDirectoryName(ct), Path.GetFileName(ct).Split('.')[0] + ".markers.json");
                    List<DollyMarker> ms = File.Exists(mj) ? CamPaths.ReadMarkersJson(mj) : null;
                    _viewLive = false;
                    _viewRow = i;
                    _viewRowRef = r;
                    _scene.ShowPath(t, ms, Looks.MapOf(r.Demo), "row " + (i + 1) + ": " + r.Name, true);
                    // picking a cinematic puts it in the game, when the game is kept in step (Mycelium: "As a user clicks on different
                    // kinds of cinematics")
                    if (_inStep.IsChecked == true && ms != null) { _stepTimer.Stop(); _stepTimer.Start(); }
                    LoadPlayers(Path.Combine(Path.GetDirectoryName(ct), Path.GetFileName(ct).Split('.')[0] + ".players.rvp.gz"), i);
                }
                catch (Exception ex) { Report("could not show row " + (i + 1) + ": " + ex.Message); }
            }
        }

        /// <summary>The row's players (tools\mapimport\viewport_players.py beside its track), read off the UI thread.</summary>
        private void LoadPlayers(string file, int row)
        {
            _scene.SetPlayers(null);
            if (!File.Exists(file)) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                CamPlayers p = null;
                string err = null;
                try { p = CamPlayers.Load(file); }
                catch (Exception ex) { err = ex.Message; App.Note("loading players " + file, ex); }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_viewLive || _viewRow != row) return;          // he moved on
                    if (p != null) _scene.SetPlayers(p);
                    else Report("could not load the players: " + err);
                }));
            });
        }

        private void FlushNote()
        {
            _noteTimer.Stop();
            if (_noteRow == null || _q == null || !_q.Rows.Contains(_noteRow)) return;
            string v = _note.Text.Replace("\r", " ").Replace("\n", " ");
            if (_noteRow.Note == v) return;
            _noteRow.Note = v;
            try { _q.Save(); } catch (Exception ex) { Report("could not save the note: " + ex.Message); return; }
            int i = _q.Rows.IndexOf(_noteRow);
            if (i >= 0 && i < _list.Items.Count) ((Item)_list.Items[i]).Note = v;
            _list.Items.Refresh();
        }

        private void MoveSel(int d)
        {
            int i = Sel();
            if (_q == null || i < 0) return;
            FlushNote();
            int j = Math.Max(0, Math.Min(_q.Rows.Count - 1, i + d));
            _q.Move(i, j);
            Refresh(j);
        }

        private void RemoveSel()
        {
            int i = Sel();
            if (_q == null || i < 0) return;
            FlushNote();
            string name = _q.Rows[i].Name;
            if (MessageBox.Show("Take row " + (i + 1) + " (" + name + ") out of the queue? Its files go to the removed folder.", "CAM QUEUE",
                                MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            _q.Remove(i);
            Refresh(Math.Min(i, _q.Rows.Count - 1));
            Report("removed " + name + " (its files are in " + _q.RemovedDir + ")");
        }

        private void RenameSel()
        {
            int i = Sel();
            if (_q == null || i < 0) return;
            FlushNote();
            string name = Ask("Rename", "A name for row " + (i + 1) + " (it is also its clip's folder):", _q.Rows[i].Name);
            if (name == null) return;
            _q.Rename(i, name);
            Refresh(i);
        }

        /// <summary>Mycelium, 2026-10-16: the frag's name "should just auto populate based on what the folder name is holding the
        /// demo file itself, with an option to change it if needed". Changed for every row of that frag, so its POV and its
        /// cinematics stay in one folder.</summary>
        private void FragSel()
        {
            int i = Sel();
            if (_q == null || i < 0) { Report("pick a row first"); return; }
            FlushNote();
            string was = _q.Rows[i].Folder;
            string frag = Ask("Frag name", "The frag's name: its folder in Recordings and its files' names (every row of this frag changes):", was);
            if (frag == null) return;
            frag = CamQueue.Clean(frag);
            if (frag.Length == 0) return;
            int n = 0;
            foreach (QueueRow r in _q.Rows) if (string.Equals(r.Folder, was, StringComparison.OrdinalIgnoreCase)) { r.Folder = frag; n++; }
            _q.Save();
            Refresh(i);
            Report("frag name now \"" + frag + "\" for " + n + " row(s)");
        }

        private void OwnCfg()
        {
            int i = Sel();
            if (_q == null || i < 0) { Report("pick a row first"); return; }
            string path = _q.OwnCfg(i, true);
            EditFile(path, "CFG for row " + (i + 1) + " (" + _q.Rows[i].Name + ") - sent instead of its kind's CFG");
            Refresh(i);
        }

        private void EditKindCfg(string kind)
        {
            if (_q == null) { Report("open a queue first"); return; }
            string file = _q.Default(kind + "_cfg");
            if (file.Length == 0) { file = kind + ".cfg"; _q.SetDefault(kind + "_cfg", file); }
            string path = Path.IsPathRooted(file) ? file : Path.Combine(_q.Dir, file);
            if (!File.Exists(path)) File.WriteAllText(path, "", new UTF8Encoding(false));
            EditFile(path, "The CFG sent before every " + kind.ToUpperInvariant() + " row (" + path + ")");
        }

        /// <summary>A plain text editor for a CFG file: it saves as it is typed (and when closed), never only on a button.</summary>
        private void EditFile(string path, string title)
        {
            var w = new Window { Title = title, Width = 640, Height = 460, Owner = Window.GetWindow(this), Background = (Brush)Application.Current.FindResource("Back") };
            var box = new TextBox { AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(8) };
            try { box.Text = File.ReadAllText(path); } catch { }
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            Action flush = () => { t.Stop(); try { File.WriteAllText(path, box.Text, new UTF8Encoding(false)); } catch (Exception ex) { Report("could not save " + path + ": " + ex.Message); } };
            t.Tick += (s, e) => flush();
            box.TextChanged += (s, e) => { t.Stop(); t.Start(); };
            w.Closing += (s, e) => flush();
            box.LostKeyboardFocus += (s, e) => flush();
            w.Content = box;
            w.Show();
        }

        private string Ask(string title, string prompt, string value)
        {
            var w = new Window { Title = title, Width = 420, SizeToContent = SizeToContent.Height, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)Application.Current.FindResource("Back") };
            var p = new StackPanel { Margin = new Thickness(12) };
            p.Children.Add(new TextBlock { Text = prompt, Foreground = Foreground, TextWrapping = TextWrapping.Wrap });
            var box = new TextBox { Text = value ?? "", Margin = new Thickness(0, 8, 0, 8) };
            p.Children.Add(box);
            var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(14, 2, 14, 2), HorizontalAlignment = HorizontalAlignment.Right };
            string result = null;
            ok.Click += (s, e) => { result = box.Text; w.Close(); };
            p.Children.Add(ok);
            w.Content = p;
            box.Focus(); box.SelectAll();
            w.ShowDialog();
            return result;
        }

        // ---------------------------------------------------------------- the queue

        private void NewQueue()
        {
            string dir = Path.Combine(CamQueue.DefaultRoot, DateTime.Now.ToString("yyyy-MM-dd"));
            for (int n = 2; Directory.Exists(dir) && File.Exists(Path.Combine(dir, "queue.csv")) && File.ReadAllLines(Path.Combine(dir, "queue.csv")).Length > 1; n++)
                dir = Path.Combine(CamQueue.DefaultRoot, DateTime.Now.ToString("yyyy-MM-dd") + " " + n);
            OpenQueue(dir);
        }

        public string OpenQueue(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return "no folder";
            // the running night writes its status, its failed rows and its second pass into its own queue (Q-8)
            if (_running) { Report("A queue is recording: open another one when it ends, or Stop it first."); return "busy: a queue is running"; }
            FlushNote();
            try
            {
                string seed = BatchControl.Instance != null ? BatchControl.Instance.CfgText : "";
                _q = CamQueue.Open(dir, seed);
            }
            catch (Exception ex) { App.Note("opening a queue", ex); Report("could not open " + dir + ": " + ex.Message); return "error: " + ex.Message; }
            _queueBox.Text = _q.Dir;
            ShowDefaults();
            Refresh();
            SaveSettings();
            // the BATCH page shows this queue's list and follows it as rows are added (2026-10-18: "not 1:1")
            try { if (BatchControl.Instance != null) BatchControl.Instance.Follow(_q.Csv); } catch (Exception ex) { App.Note("the BATCH page following the queue", ex); }
            string s = "queue " + _q.Dir + ": " + _q.Rows.Count + " rows";
            Report(s);
            return s;
        }

        private void ShowDefaults()
        {
            _loadingDefaults = true;
            try
            {
                _povFps.Text = _q.Default("pov_fps"); _povBefore.Text = _q.Default("pov_before"); _povAfter.Text = _q.Default("pov_after");
                _cineFps.Text = _q.Default("cine_fps"); _cineBefore.Text = _q.Default("cine_before"); _cineAfter.Text = _q.Default("cine_after");
                _povHud.SelectedIndex = HudIndex(_q.Default("pov_hud"));
                _cineHud.SelectedIndex = HudIndex(_q.Default("cine_hud"));
                _povMode.SelectedIndex = HudIndex(_q.Default("pov_codcast"));
                _cineMode.SelectedIndex = HudIndex(_q.Default("cine_codcast"));
            }
            finally { _loadingDefaults = false; }
        }

        private static int HudIndex(string v)
        {
            switch ((v ?? "").Trim().ToLowerInvariant()) { case "hide": case "hidden": case "off": return 1; case "show": case "shown": case "on": return 2; default: return 0; }
        }

        private static string HudValue(ComboBox c) { return c.SelectedIndex == 1 ? "hide" : c.SelectedIndex == 2 ? "show" : "tab"; }
        private static string ModeValue(ComboBox c) { return c.SelectedIndex == 1 ? "off" : c.SelectedIndex == 2 ? "on" : "tab"; }

        private void DefaultsChanged()
        {
            if (_loadingDefaults || _q == null) return;
            try
            {
                _q.SetDefault("pov_fps", _povFps.Text.Trim()); _q.SetDefault("pov_before", _povBefore.Text.Trim()); _q.SetDefault("pov_after", _povAfter.Text.Trim());
                _q.SetDefault("cine_fps", _cineFps.Text.Trim()); _q.SetDefault("cine_before", _cineBefore.Text.Trim()); _q.SetDefault("cine_after", _cineAfter.Text.Trim());
                _q.SetDefault("pov_hud", HudValue(_povHud)); _q.SetDefault("cine_hud", HudValue(_cineHud));
                _q.SetDefault("pov_codcast", ModeValue(_povMode)); _q.SetDefault("cine_codcast", ModeValue(_cineMode));
            }
            catch (Exception ex) { Report("could not save the defaults: " + ex.Message); }
        }

        private void LoadSettings()
        {
            Dictionary<string, string> s = TakeList.ReadDefaults(SettingsFile);
            string v;
            if (s.TryGetValue("speed", out v)) { double d; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) _scene.Speed = d; }
            if (s.TryGetValue("quit", out v)) _quitAfter.IsChecked = v == "on";
            if (s.TryGetValue("queue", out v) && v.Length > 0 && Directory.Exists(v)) OpenQueue(v);
            else _summary.Text = "no queue open: New makes one for tonight";
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                File.WriteAllText(SettingsFile, "queue=" + (_q != null ? _q.Dir : "") + "\nspeed=" + _scene.Speed.ToString("0.###", CultureInfo.InvariantCulture)
                                                + "\nquit=" + (_quitAfter.IsChecked == true ? "on" : "off") + "\n", new UTF8Encoding(false));
            }
            catch { }
        }

        // ---------------------------------------------------------------- the GUI tour (pictures without a window)

        /// <summary>For --gui-tour: open a queue folder and show one of its rows.</summary>
        public string TourOpen(string dir, int row)
        {
            string s = OpenQueue(dir);
            if (_q != null && row >= 0 && row < _q.Rows.Count) SelectRow(row);
            return s;
        }

        /// <summary>For --gui-tour: the loop held at a third of the path, so the pictures show the camera on its way.</summary>
        public void TourSettle() { if (_scene.Track != null) _scene.Seek(_scene.Track.Seconds * 0.35); }

        public bool TourMapSettled { get { return _scene.MapSettled; } }

        /// <summary>For --gui-tour: a "queue ..." pipe command, as a script would send it.</summary>
        public string TourPipe(string command) { return Pipe(command); }

        public string TourStatus() { return _scene.Status + "\n" + _views.List(); }

        // ---------------------------------------------------------------- the game, read live

        private bool Attached()
        {
            if (_game.Attached && _game.Alive) return true;
            if ((DateTime.Now - _attachTry).TotalSeconds < 3) return false;
            _attachTry = DateTime.Now;
            try { return _game.Attach(); } catch { return false; }
        }

        private void PollGame()
        {
            if (!IsVisible && _liveMarkers != null) return;          // nobody is looking; the save button reads afresh anyway
            if (!Attached()) { SetLive(null, "the game is not running", ""); return; }
            string why, sig;
            List<DollyMarker> ms;
            try { ms = CamPaths.ReadMarkers(_game, out why, out sig); }
            catch (Exception ex) { ms = null; why = ex.Message; sig = ""; }
            int theater = _game.TheaterBase;
            if (theater != _liveTheater)
            {
                _liveTheater = theater;
                _liveMap = null;
                if (theater != 0 && theater != int.MinValue)
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        string map = null;
                        try { map = Looks.CurrentMap(); } catch { }
                        Dispatcher.BeginInvoke(new Action(() => { _liveMap = map; if (_viewLive) ShowLiveInView(false); }));
                    });
            }
            SetLive(ms, why, sig);
            if (_kind == "pov" && IsVisible) UpdateLiveText();          // who is being watched changes without the markers changing
        }

        private void SetLive(List<DollyMarker> ms, string why, string sig)
        {
            bool changed = sig != _liveSig || why != _liveWhy;
            if (sig != _liveSig) _liveEdited = null;
            _liveSig = sig; _liveWhy = why;
            if (!changed) return;
            _liveMarkers = ms;
            _liveTrack = null;
            _liveSpeed = "";
            if (ms != null && ms.Count >= 2)
            {
                string w;
                _liveTrack = CamPaths.TrackFromMarkers(ms, 1, out w);
                double spread;
                _liveSpeed = CamPaths.SpeedReport(ms, out spread);
            }
            UpdateLiveText();
            // he is placing markers: the view follows them (that is what he is looking at)
            if (_liveTrack != null && !_viewLive && sig.Length > 0) { _viewLive = true; _list.SelectedIndex = -1; }
            if (_viewLive) ShowLiveInView(false);
        }

        private string _liveSpeed = "";

        private void UpdateLiveText()
        {
            List<DollyMarker> ms = _liveMarkers;
            string pov = _povIn >= 0 ? "   POV in " + CamPaths.ClockLabel(_povIn) + (_povOut > _povIn ? " out " + CamPaths.ClockLabel(_povOut) : "") : "";
            if (_kind == "pov")
            {
                // the POV Queue: who is being watched now, and the marks so far (no dolly markers here)
                string who = "";
                try { if (_game.Attached && _game.InTheater) { int slot = _game.PlayerIndex; string n = _game.PlayerName(slot); who = n.Length > 0 ? "watching " + n : "slot " + slot; } } catch { }
                _live.Text = (who.Length > 0 ? "Live: " + who : ms == null ? "Live: " + _liveWhy : "Live: the film is up") + pov
                             + (_povIn < 0 ? ".  POV in marks the start on the player you are watching." : _povOut <= _povIn ? ".  Now POV out at its end." : ".  Add POV take.");
                return;
            }
            _live.Text = ms == null ? "Live: " + _liveWhy + pov
                       : ms.Count == 0 ? "Live: no markers placed" + pov
                       : "Live: " + ms.Count + " marker" + (ms.Count == 1 ? "" : "s")
                         + (_liveTrack != null ? ", " + _liveTrack.Seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s at film " + CamPaths.ClockLabel(_liveTrack.First) + "; " + _liveSpeed : "") + pov;
        }

        private void ShowLiveInView(bool reframe)
        {
            _viewRow = -1;
            _viewRowRef = null;
            _scene.SetPlayers(null);
            if (_liveEdited != null && _liveMarkers != null)
            {
                string w;
                CamTrack t = CamPaths.TrackFromMarkers(_liveEdited, 1, out w);
                if (t != null) { _scene.ShowPath(t, _liveEdited, _liveMap, "live: " + _liveEdited.Count + " markers (edited here)", reframe); return; }
            }
            if (_liveTrack != null) _scene.ShowPath(_liveTrack, _liveMarkers, _liveMap, "live: " + _liveMarkers.Count + " markers", reframe);
            else _scene.Clear(_liveMarkers == null ? "Live: " + _liveWhy : "Place two or more markers in the theater's Edit Camera: the path loops here.");
        }

        // ---------------------------------------------------------------- which demo

        private List<string> _demoFiles;
        private DateTime _demoScan = DateTime.MinValue;

        private IEnumerable<string> DemoCandidates()
        {
            if (_demoFiles == null || (DateTime.Now - _demoScan).TotalSeconds > 60)
            {
                var files = new List<string>();
                var roots = new List<string>();
                if (BatchControl.Instance != null && BatchControl.Instance.DemoRoot.Length > 0) roots.Add(BatchControl.Instance.DemoRoot);
                try { string g = Where.InGame(@"Plugins\OpenNetStorage\Cloud\FILM_MP"); if (!string.IsNullOrEmpty(g)) roots.Add(g); } catch { }
                foreach (string r in roots)
                {
                    try { if (Directory.Exists(r)) files.AddRange(Directory.GetFiles(r, "*.demo", SearchOption.AllDirectories)); }
                    catch { }
                }
                if (!string.IsNullOrEmpty(DemosControl.LastLoaded)) files.Insert(0, DemosControl.LastLoaded);
                _demoFiles = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                _demoScan = DateTime.Now;
            }
            return _demoFiles;
        }

        /// <summary>The demo file the game is playing: matched in memory; else the film the DEMOS tab loaded; else he picks it.</summary>
        private string FilmNow(bool mayAsk, out string how)
        {
            string why;
            string path = CamPaths.FilmInGame(_game, DemoCandidates(), out why);
            if (path != null) { how = "found in the game"; return path; }
            if (!string.IsNullOrEmpty(DemosControl.LastLoaded) && File.Exists(DemosControl.LastLoaded)) { how = "the DEMOS tab's last load (" + why + ")"; return DemosControl.LastLoaded; }
            how = why;
            if (!mayAsk) return null;
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Which demo is in the theater? (" + why + ")", Filter = "BO2 films (*.demo)|*.demo" };
            if (BatchControl.Instance != null && Directory.Exists(BatchControl.Instance.DemoRoot)) dlg.InitialDirectory = BatchControl.Instance.DemoRoot;
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) { how = "picked"; return dlg.FileName; }
            return null;
        }

        // ---------------------------------------------------------------- the film to marker N (2026-10-17)

        /// <summary>The Jump button: off the window's thread (a jump the game does not take waits up to 8 s for it).</summary>
        private void JumpToMarker()
        {
            int n;
            if (!int.TryParse(_jumpN.Text.Trim(), out n) || n < 1) { Report("Marker: type which one (1 = the first)."); return; }
            if (_jumping) return;
            bool keep = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            _jumping = true; Report("Jumping to marker " + n + "...");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string r = MarkerJump(n, keep);
                Dispatcher.BeginInvoke(new Action(() => { _jumping = false; Report(r); }));
            });
        }

        /// <summary>RipperBridge dev22's "kf marker N": the game's own jump to its marker N's snapshot, Edit Camera for the
        /// call only, no menu. The reply in plain words.</summary>
        public static string MarkerJump(int n, bool keepCamera)
        {
            string r = BridgeClient.Ask("kf marker " + n.ToString(CultureInfo.InvariantCulture) + (keepCamera ? " keep" : ""), 10000, 1);
            if (r == null) return "Not jumped: RipperBridge did not answer (" + BridgeClient.LastError + ").";
            if (r.StartsWith("error usage", StringComparison.Ordinal)) return "Not jumped: this RipperBridge has no marker jump (Setup installs the RipperBridge that comes with the Designer).";
            if (r.StartsWith("error ", StringComparison.Ordinal)) return "Not jumped: " + r.Substring(6) + ".";
            int at = r.IndexOf(" at ", StringComparison.Ordinal), sp = at < 0 ? -1 : r.IndexOf(' ', at + 4);
            string tick = at < 0 ? "" : r.Substring(at + 4, (sp < 0 ? r.Length : sp) - at - 4);
            // Sandbox 0723-0724 (2026-10-18): a jump that puts the camera on the marker leaves the film held there - it does not
            // play again (41050 -> 41141, then still, at any speed, in any camera mode) until another jump. "keep" does not
            // hold it. So the film is let go at once: a keyframe jump to the marker's moment (the camera stays on the marker),
            // then played up to it - where the batch, the timeline and Play can move it again.
            string freed = "";
            if (!keepCamera)
            {
                string landed = BridgeClient.Ask("stats", 800, 1);
                int t = landed == null ? -1 : (int)BridgeClient.Field(landed, "tick");
                string k = t > 0 ? BridgeClient.Ask("kf jump " + t.ToString(CultureInfo.InvariantCulture), 10000, 1) : null;
                if (k != null && k.StartsWith("ok", StringComparison.Ordinal)) { string why; MarkerApply.GoToForJump(t, out why); }
                else freed = " Note: the film stays held on the marker until it is moved (no keyframe before it yet: play the film once from before it).";
            }
            return "The film is at your marker " + n + (tick.Length > 0 ? " (tick " + tick + ")" : "") + (keepCamera ? ", the camera where it was." : ", the camera on the marker.") + freed;
        }

        // ---------------------------------------------------------------- SAVE MY MARKERS, POV in / out

        /// <summary>The button: the markers now, as a CINE row. The reply says what was saved, or why nothing was.</summary>
        public string SaveMarkers(string name, bool mayAsk = true)
        {
            if (_q == null) OpenQueue(Path.Combine(CamQueue.DefaultRoot, DateTime.Now.ToString("yyyy-MM-dd")));
            if (_q == null) return "no queue";
            if (!Attached()) return "The game is not running.";
            string why, sig;
            List<DollyMarker> ms = CamPaths.ReadMarkers(_game, out why, out sig);
            if (ms == null) return "Nothing saved: " + why + ".";
            if (ms.Count < 2) return "Nothing saved: place at least 2 markers in Edit Camera first (" + ms.Count + " now).";
            bool edited = _liveEdited != null && _liveEdited.Count >= 2 && sig == _liveSig;      // an added or removed camera counts too
            if (edited) ms = _liveEdited;
            string warn;
            CamTrack track = CamPaths.TrackFromMarkers(ms, 1, out warn);
            if (track == null) return "Nothing saved: " + warn + ".";
            string how;
            string demo = FilmNow(mayAsk, out how);
            if (demo == null) return "Nothing saved: which demo is this? " + how + ". Load it from the DEMOS tab, or pick it when asked.";
            string map = _liveMap ?? Looks.MapOf(demo);
            if (_q.Rows.Any(r => r.IsCine && string.Equals(r.Demo, demo, StringComparison.OrdinalIgnoreCase) && r.TickInValue == track.First && r.TickOutValue == track.Last && r.Source == "markers"))
                return "These markers are already in the queue (the same film time on the same demo). Clear them in the game and place the next path.";
            QueueRow row;
            try { row = _q.AddMarkers(ms, track, demo, map, name); }
            catch (Exception ex) { App.Note("saving markers", ex); return "Could not save: " + ex.Message; }
            if (edited) { row.Note = ms.Count + " markers, edited in the 3D view"; _q.Save(); }
            Refresh(_q.Rows.Count - 1);
            _viewLive = true;
            ShowLiveInView(false);
            double spread;
            string speed = CamPaths.SpeedReport(ms, out spread);
            return "Saved row " + _q.Rows.Count + " \"" + row.Name + "\": " + ms.Count + " markers" + (edited ? " (as edited in the 3D view)" : "") + ", " + track.Seconds.ToString("0.00", CultureInfo.InvariantCulture)
                   + " s at film " + row.ClockIn + " on " + row.Folder + " (" + how + "). " + speed + (warn.Length > 0 ? ". Note: " + warn : "")
                   + ". Clear the markers in the game for the next path.";
        }

        private string MarkPov(bool start)
        {
            if (!Attached()) return "The game is not running.";
            if (!_game.InTheater) return "No film in the theater.";
            int tick = _game.Tick;
            if (start) { _povIn = tick; _povOut = -1; _povName = _game.PlayerName(_game.PlayerIndex); }
            else _povOut = tick;
            UpdateLiveText();
            RaisePovMarks();
            return start ? "POV in at film " + CamPaths.ClockLabel(tick) + " (tick " + tick + "), watching " + (_povName.Length > 0 ? _povName : "slot " + _game.PlayerIndex) + ". Now POV out."
                         : "POV out at film " + CamPaths.ClockLabel(tick) + " (tick " + tick + ")" + (_povIn >= 0 && tick > _povIn ? ": " + ((tick - _povIn) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s. Add POV take." : ". Mark POV in first.");
        }

        public string AddPovLive(string name, bool mayAsk = true)
        {
            if (_q == null) OpenQueue(Path.Combine(CamQueue.DefaultRoot, DateTime.Now.ToString("yyyy-MM-dd")));
            if (_povIn < 0 || _povOut <= _povIn) return "Mark POV in, then POV out (later in the film), first.";
            if (string.IsNullOrEmpty(_povName)) return "Nobody's view was being watched at POV in: switch to the player, then mark POV in again.";
            if (!Attached()) return "The game is not running.";
            string how;
            string demo = FilmNow(mayAsk, out how);
            if (demo == null) return "Nothing added: which demo is this? " + how + ".";
            QueueRow r = _q.AddPov(demo, _povName, _povIn, _povOut, name);
            Refresh(_q.Rows.Count - 1);
            _povIn = _povOut = -1;
            RaisePovMarks();
            return "Added row " + _q.Rows.Count + " \"" + r.Name + "\": " + _povName + "'s view, " + r.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s at film " + r.ClockIn + " on " + r.Folder + ".";
        }

        // ---------------------------------------------------------------- the timeline's lanes and its loop (2026-10-18)
        // Mycelium: "Make two separate ones that work together, because one may want to change the global bracket without
        // changing the POV one. So make the POV one more like markers on its own timeline layer". The POV in and out marked
        // here, and every POV take's, are markers on the timeline's POV lane; the loop bracket is the timeline's own. They
        // meet only when asked: "From the loop" and "Loop it".

        /// <summary>Raised on the window's thread when the POV in / out marked here (not a take yet) changed.</summary>
        public static event Action PovMarksChanged;
        private static void RaisePovMarks() { if (PovMarksChanged != null) PovMarksChanged(); }

        /// <summary>The POV Queue's in and out not yet a take (-1: none).</summary>
        public static int PendingIn { get { CamQueueControl p = PovInstance; return p == null ? -1 : p._povIn; } }
        public static int PendingOut { get { CamQueueControl p = PovInstance; return p == null ? -1 : p._povOut; } }

        /// <summary>The POV Queue's in and out, set from the timeline (dragged on the POV lane, or from the loop). Its player is
        /// the one being watched now when none was marked.</summary>
        public static void SetPending(int tin, int tout)
        {
            CamQueueControl p = PovInstance;
            if (p == null) return;
            p._povIn = tin; p._povOut = tout;
            if (string.IsNullOrEmpty(p._povName) && p.Attached()) p._povName = p._game.PlayerName(p._game.PlayerIndex);
            p.UpdateLiveText();
            RaisePovMarks();
        }

        /// <summary>The film the game is playing, as a demo path, never asking (the timeline's lanes show its rows); null when
        /// not known.</summary>
        public static string FilmQuiet()
        {
            CamQueueControl p = Instance ?? PovInstance;
            if (p == null || !p.Attached() || !p._game.InTheater) return null;
            try { string how; return p.FilmNow(false, out how); } catch { return null; }
        }

        /// <summary>The row picked on its kind's page (-1: none).</summary>
        public static int Picked(bool cine) { CamQueueControl p = cine ? Instance : PovInstance; return p == null ? -1 : p.Sel(); }

        /// <summary>Picks a row on its kind's page (a click on its lane).</summary>
        public static void Pick(int row)
        {
            CamQueue q = _sharedQueue;
            if (q == null || row < 0 || row >= q.Rows.Count) return;
            CamQueueControl p = q.Rows[row].IsCine ? Instance : PovInstance;
            if (p != null) p.SelectRow(row);
        }

        /// <summary>Rows changed on the timeline: the queue saved, every page and the Batch shown again.</summary>
        public static void Edited(int row)
        {
            CamQueue q = _sharedQueue;
            if (q == null) return;
            q.Save();
            CamQueueControl p = Instance ?? PovInstance;
            if (p != null) p.Refresh(row);
        }

        private string PovFromLoop()
        {
            if (!LoopBracket.Has) return "There is no loop bracket: drag one on the strip above the timeline (in the Workspace layout).";
            SetPending(LoopBracket.In, LoopBracket.Out);
            return "POV in " + CamPaths.ClockLabel(_povIn) + ", out " + CamPaths.ClockLabel(_povOut) + " from the loop (" + ((_povOut - _povIn) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)
                   + " s), watching " + (_povName.Length > 0 ? _povName : "nobody yet") + ". Add POV take, or drag them on the POV lane. The loop stays as it is.";
        }

        private string LoopPov()
        {
            int i = Sel();
            int a, b; string what;
            if (i >= 0 && _q != null && i < _q.Rows.Count && _q.Rows[i].TickInValue >= 0 && _q.Rows[i].TickOutValue > _q.Rows[i].TickInValue)
            { a = _q.Rows[i].TickInValue; b = _q.Rows[i].TickOutValue; what = "\"" + _q.Rows[i].Name + "\""; }
            else if (_povIn >= 0 && _povOut > _povIn) { a = _povIn; b = _povOut; what = "the POV in and out marked here"; }
            else return "Pick a POV take, or mark POV in and out, first.";
            LoopBracket.Set(a, b);
            LoopBracket.Switch(true);
            return "The loop is " + what + ": " + CamPaths.ClockLabel(a) + " to " + CamPaths.ClockLabel(b) + ", on. The take does not change when you move the loop.";
        }


        // ---------------------------------------------------------------- preview in the game

        private string Preview(int i)
        {
            if (_q == null || i < 0 || i >= _q.Rows.Count) return "pick a CINE row first";
            QueueRow r = _q.Rows[i];
            if (!r.IsCine) return "row " + (i + 1) + " is a POV take: there is no path to preview";
            if (!Attached() || !_game.InTheater) return "load the row's film in the theater first";
            string how;
            string demo = FilmNow(false, out how);
            if (demo != null && r.Demo.Length > 0 && !string.Equals(Path.GetFullPath(demo), Path.GetFullPath(r.Demo), StringComparison.OrdinalIgnoreCase))
                return "the theater has " + Path.GetFileName(demo) + "; this row is on " + Path.GetFileName(r.Demo) + " (load it from the DEMOS tab)";
            string ct = Path.IsPathRooted(r.CamPath) ? r.CamPath : Path.Combine(_q.Dir, r.CamPath);
            CamTrack t = CamTrack.Read(ct);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string reply = BridgeClient.CamPath(ct);        // asked again while the free camera is not ready yet
                string result;
                if (!reply.StartsWith("ok", StringComparison.Ordinal)) result = "the game would not take the path: " + reply + " (Preview needs the RipperBridge that comes with the Designer)";
                else
                {
                    BridgeClient.Ask("cmd demo_pause 1", 1000, 2);
                    BridgeClient.Ask("cmd demo_desiredTime " + (t.First - 1000).ToString(CultureInfo.InvariantCulture), 1000, 2);
                    Thread.Sleep(1500);
                    BridgeClient.Ask("cmd demo_pause 0", 1000, 2);
                    result = "previewing " + r.Name + " in the game from 1 s before it (Stop preview gives the camera back)";
                }
                Dispatcher.BeginInvoke(new Action(() => Report(result)));
            });
            return "sending " + r.Name + " to the game...";
        }

        private string StopPreview()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string a = BridgeClient.Ask("cam off", 2000, 1) ?? BridgeClient.LastError;
                string b = BridgeClient.Ask("cam mode 0", 2000, 1) ?? BridgeClient.LastError;
                BridgeClient.Ask("cmd demo_pause 1", 1000, 2);
                Dispatcher.BeginInvoke(new Action(() => Report("preview stopped: " + a + "; " + b)));
            });
            return "stopping the preview...";
        }

        // ---------------------------------------------------------------- Apply to game (2026-10-18)

        /// <summary>The markers the views show: the picked row's (its markers.json), or the live ones as edited here.</summary>
        private List<DollyMarker> ShownMarkers(out string what, out string demo)
        {
            what = ""; demo = null;
            if (_viewLive || _viewRowRef == null)
            {
                what = "the live markers" + (_liveEdited != null ? " as edited here" : "");
                return _liveEdited ?? _liveMarkers;
            }
            return RowMarkers(_viewRowRef, out what, out demo);
        }

        /// <summary>A queue row's markers (its markers.json), or null with what it is.</summary>
        private List<DollyMarker> RowMarkers(QueueRow r, out string what, out string demo)
        {
            what = ""; demo = null;
            if (_q == null || !_q.Rows.Contains(r) || !r.IsCine) return null;
            string ct = Path.IsPathRooted(r.CamPath) ? r.CamPath : Path.Combine(_q.Dir, r.CamPath);
            string mj = Path.Combine(Path.GetDirectoryName(ct), Path.GetFileName(ct).Split('.')[0] + ".markers.json");
            if (!File.Exists(mj)) { what = "row " + (_q.Rows.IndexOf(r) + 1) + " (a generated path, no markers)"; return null; }
            what = "row " + (_q.Rows.IndexOf(r) + 1) + " \"" + r.Name + "\"";
            demo = r.Demo;
            return CamPaths.ReadMarkersJson(mj);
        }

        /// <summary>Apply to game: off the window's thread (each marker is a seek). The reply comes when it is done. With a row
        /// (the pipe's "queue apply I"): that row's markers; without: what the views show.</summary>
        private string ApplyToGame(QueueRow row = null, Action<string> then = null)
        {
            string what, demo;
            List<DollyMarker> ms;
            try { ms = row != null ? RowMarkers(row, out what, out demo) : ShownMarkers(out what, out demo); }
            catch (Exception ex) { return "Not applied: could not read the markers (" + ex.Message + ")."; }
            if (ms == null || ms.Count < 2) return "Not applied: pick a CINE row with markers, or place markers in the game" + (what.Length > 0 ? " (" + what + " has none)" : "") + ".";
            if (!Attached() || !_game.InTheater) return "Not applied: load the film in the theater first.";
            if (BatchControl.Instance != null && BatchControl.Instance.IsBusy) return "Not applied: the batch has the film.";
            if (demo != null)
            {
                string how, film = FilmNow(false, out how);
                if (film != null && demo.Length > 0 && !string.Equals(Path.GetFullPath(film), Path.GetFullPath(demo), StringComparison.OrdinalIgnoreCase))
                    return "Not applied: the theater has " + Path.GetFileName(film) + "; " + what + " is on " + Path.GetFileName(demo) + ".";
            }
            if (Interlocked.CompareExchange(ref _applying, 1, 0) != 0) return "Already applying markers to the game.";
            var copy = ms.ToList();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string r;
                try { r = MarkerApply.Apply(copy, line => Dispatcher.BeginInvoke(new Action(() => Report("Applying " + what + ": " + line)))); }
                catch (Exception ex) { App.Note("applying markers to the game", ex); r = "Not applied: " + ex.Message; }
                Dispatcher.BeginInvoke(new Action(() => { Interlocked.Exchange(ref _applying, 0); Report(r); }));
                if (then != null && r.StartsWith("Applied", StringComparison.Ordinal))
                {
                    try { then(r); } catch (Exception ex) { App.Note("after applying markers", ex); }
                }
            });
            return "Applying " + what + " (" + ms.Count + " markers) to the game: the film moves to place each one...";
        }

        /// <summary>
        /// A cinematic flown by the game's own dolly, looping (Mycelium 10-18, 25:34: "I also want there to be a possibility for
        /// the in-game ... to be doing the exact motion"; 35:12: "you could see the camera path looping inside of the game on
        /// the dolly camera mode. ... then you wouldn't even need this sort of fake test kind of path thing"). Its markers
        /// applied to the game (Edit Camera reached by itself), then Dolly Camera (CameraView: from whatever view the game was
        /// in), the loop bracket over the path, on, and the film played. Off the window's thread; the page says how it went.
        /// </summary>
        public string PreviewInDolly(int i)
        {
            if (_q == null || i < 0 || i >= _q.Rows.Count || !_q.Rows[i].IsCine) return "Pick a cinematic first.";
            QueueRow r = _q.Rows[i];
            int a = r.TickInValue, b = r.TickOutValue;
            if (a < 0 || b <= a) return "This cinematic has no film times.";
            StopPreview();
            LoopBracket.Switch(false);
            string name = r.Name;
            return ApplyToGame(r, applied =>
            {
                string v = CameraView.To(CameraView.View.Dolly);
                string said;
                if (!v.StartsWith("ok", StringComparison.Ordinal)) said = "The markers are in the game, but the Dolly Camera did not come up: " + v + ".";
                else
                {
                    LoopBracket.Set(a, b);
                    LoopBracket.Switch(true);
                    BridgeClient.Ask("cmd demo_pause 0", 800, 1);
                    said = "\"" + name + "\" loops in the game's Dolly Camera (" + v.Substring(3) + "): " + CamPaths.ClockLabel(a) + " to " + CamPaths.ClockLabel(b)
                           + ". The loop bracket is over it on the timeline; Loop switches it off.";
                }
                Dispatcher.BeginInvoke(new Action(() => Report(said)));
            });
        }

        // ---------------------------------------------------------------- check and run

        private string Check()
        {
            if (_q == null) return "open a queue first";
            FlushNote();
            List<string> p = _q.Check(BatchControl.Instance != null ? BatchControl.Instance.DemoRoot : "");
            return p.Count == 0 ? "Check: all " + _q.Rows.Count + " rows are ready (demos, tracks and CFGs found)." : "Check: " + p.Count + " problem(s): " + string.Join("  |  ", p.ToArray());
        }

        private readonly List<string> _night = new List<string>();
        private bool _running;

        private string Run(bool dry, bool ask = true)
        {
            if (_q == null) return "open a queue first";
            if (BatchControl.Instance == null) return "the BATCH tab did not build, so nothing can record";
            FlushNote();
            if (_q.Rows.Count == 0) return "the queue is empty";
            List<string> problems = _q.Check(BatchControl.Instance.DemoRoot);
            if (problems.Count > 0) return "Not started. " + problems.Count + " problem(s): " + string.Join("  |  ", problems.ToArray());
            if (!dry && ask && MessageBox.Show("Record all " + _q.Rows.Count + " rows now, with the BATCH tab's settings?" + (_shutdownAfter.IsChecked == true ? "\n\nThe PC shuts down when it ends." : ""),
                                        "RUN QUEUE", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return "not started";
            _night.Clear();
            _night.Add("CAM QUEUE " + _q.Name + " - " + (dry ? "rehearsal (dry run)" : "recording") + " started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ", " + _q.Rows.Count + " rows");
            WriteStatus();
            bool quit = _quitAfter.IsChecked == true && !dry, shutdown = _shutdownAfter.IsChecked == true && !dry;
            _running = true;
            _stopAsked = false;
            _dryRun = dry;
            string why = BatchControl.Instance.StartQueue(_q.Csv, dry, results => Finished(results, quit, shutdown, false));
            if (why != null) { _running = false; _night.Add("not started: " + why); WriteStatus(); return "Not started: " + why; }
            return (dry ? "Rehearsing " : "Recording ") + _q.Rows.Count + " rows (the BATCH tab's log follows it; " + _q.StatusPath + " is rewritten after each).";
        }

        private string StopRun()
        {
            _stopAsked = true;
            if (BatchControl.Instance == null || !BatchControl.Instance.IsBusy) return "nothing is running";
            return BatchControl.Instance.PipeStop();
        }

        private void OnTakeFinished(BatchEngine.Result r, int done, int total)
        {
            if (!_running) return;
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + done + " of " + total + "  " + (r.Ok ? "ok    " : "FAILED") + "  "
                          + (r.Take.Name.Length > 0 ? r.Take.Name : r.Take.Folder) + (r.Ok ? (r.Frames > 0 ? " (" + r.Frames + " frames)" : "") : ": " + r.Message);
            Dispatcher.BeginInvoke(new Action(() => { _night.Add(line); WriteStatus(); }));
        }

        private bool _stopAsked, _dryRun;
        private List<int> _lastFailed = new List<int>();

        /// <summary>The rows (by number) that failed in the last pass, as a take list of their own beside the queue.</summary>
        private string RetryList(List<int> numbers)
        {
            string csv = Path.Combine(_q.Dir, "queue-retry.csv");
            var lines = File.ReadAllLines(_q.Csv);
            var keep = new List<string> { lines[0] };
            for (int i = 1; i < lines.Length; i++)
            {
                string[] f = TakeList.SplitCsv(lines[i]);
                int n;
                if (f.Length > 0 && int.TryParse(f[0], out n) && numbers.Contains(n)) keep.Add(lines[i]);
            }
            File.WriteAllLines(csv, keep, new UTF8Encoding(false));
            return csv;
        }

        private void Finished(List<BatchEngine.Result> results, bool quit, bool shutdown, bool retried)
        {
            _running = false;
            // A night should not lose a path to one bad take: every row that failed is recorded once more, after the rest
            // (the recorder's own words for a lost tick: "record it again"). Not after Stop, not in a dry run.
            List<int> failed = results == null ? new List<int>() : results.Where(r => !r.Ok && !r.Message.StartsWith("not started", StringComparison.Ordinal)).Select(r => r.Take.Number).ToList();
            _lastFailed = failed;
            try { if (_q != null) File.WriteAllText(Path.Combine(_q.Dir, "last-failed.txt"), string.Join(" ", failed.Select(x => x.ToString()).ToArray()), new UTF8Encoding(false)); }
            catch { }
            if (!retried && failed.Count > 0 && !_stopAsked && !_dryRun && results != null)
            {
                _night.Add(DateTime.Now.ToString("HH:mm:ss") + "  first pass: " + results.Count(r => r.Ok) + " of " + results.Count + " ok; recording the " + failed.Count + " that failed once more (rows " + string.Join(", ", failed.Select(x => x.ToString()).ToArray()) + ")");
                WriteStatus();
                _running = true;
                string why = BatchControl.Instance.StartQueue(RetryList(failed), false, res2 => Finished(res2, quit, shutdown, true));
                if (why == null) return;
                _running = false;
                _night.Add("the second pass did not start: " + why);
            }
            int ok = results == null ? 0 : results.Count(r => r.Ok);
            int n = results == null ? 0 : results.Count;
            _night.Add(DateTime.Now.ToString("HH:mm:ss") + "  finished" + (retried ? " (second pass)" : "") + ": " + ok + " of " + n + " ok" + (!retried && n < _q.Rows.Count ? " (" + (_q.Rows.Count - n) + " not reached)" : ""));
            WriteStatus();
            Report("Queue finished: " + ok + " of " + n + " ok. " + _q.StatusPath);
            if (_stopAsked && (quit || shutdown))
            {
                // Stop means stop: the game stays open and the PC stays on (the code review's Q-3)
                _night.Add(DateTime.Now.ToString("HH:mm:ss") + "  stopped by hand: the game stays open" + (shutdown ? ", the PC stays on" : ""));
                WriteStatus();
                return;
            }
            if (!quit && !shutdown) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string q = "";
                if (quit || shutdown)
                {
                    q = BridgeClient.Ask("cmd quit", 2000, 1) ?? BridgeClient.LastError;
                    Thread.Sleep(10000);
                }
                bool still = Process.GetProcessesByName("t6mpv43").Length > 0;
                string line = DateTime.Now.ToString("HH:mm:ss") + "  game quit: " + q + (still ? " (still running after 10 s)" : " (closed)");
                if (shutdown && !still)
                {
                    try { Process.Start(new ProcessStartInfo("shutdown", "/s /t 120 /c \"CAM QUEUE finished: shutting down in 2 minutes (shutdown /a cancels)\"") { CreateNoWindow = true, UseShellExecute = false }); line += "; PC shutting down in 2 minutes"; }
                    catch (Exception ex) { line += "; could not shut down: " + ex.Message; }
                }
                else if (shutdown) line += "; NOT shutting down, because the game did not close";
                Dispatcher.BeginInvoke(new Action(() => { _night.Add(line); WriteStatus(); }));
            });
        }

        private void WriteStatus()
        {
            if (_q == null) return;
            try { File.WriteAllText(_q.StatusPath, string.Join("\r\n", _night.ToArray()) + "\r\n", new UTF8Encoding(false)); }
            catch (Exception ex) { App.Note("writing the queue status", ex); }
        }

        // ---------------------------------------------------------------- the 3D view: edits and the pop-out

        private List<DollyMarker> _liveEdited;
        private int _viewRow = -1;
        // the row the views show, by itself: rows move (Up, Down, Remove, Duplicate) and queues change (Open, New), so an
        // edit is saved into this row wherever it is now, or nowhere (the code review's Q-1)
        private QueueRow _viewRowRef;

        /// <summary>
        /// A drag in the 3D view (Mycelium: "select and drag around in the space"). Live markers: the edit is kept here and is
        /// what SAVE MY MARKERS saves (the game's own markers are never written). A row's markers: saved over its files at
        /// once, the first original kept beside them (.orig), its track rebuilt.
        /// </summary>
        private void OnMarkersEdited(List<DollyMarker> ms)
        {
            if (_viewLive || _viewRow < 0)
            {
                _liveEdited = ms;
                Report("Edited in the 3D view: SAVE MY MARKERS saves this version. Your markers in the game are unchanged (Ctrl+Z in the view undoes).");
                return;
            }
            int at = _q == null || _viewRowRef == null ? -1 : _q.Rows.IndexOf(_viewRowRef);
            if (at < 0) { Report("Not saved: the row this view shows is no longer in the open queue. Pick the row again to edit it."); return; }
            QueueRow r = _q.Rows[at];
            try
            {
                string ct = Path.IsPathRooted(r.CamPath) ? r.CamPath : Path.Combine(_q.Dir, r.CamPath);
                string id = Path.GetFileName(ct).Split('.')[0];
                string dir = Path.GetDirectoryName(ct);
                string mj = Path.Combine(dir, id + ".markers.json");
                foreach (string f in new[] { ct, mj })
                {
                    string orig = Path.Combine(dir, Path.GetFileName(f).Replace(id + ".", id + ".orig."));
                    if (File.Exists(f) && !File.Exists(orig)) File.Copy(f, orig);
                }
                string w;
                CamTrack t = CamPaths.TrackFromMarkers(ms, 1, out w);
                if (t == null) { Report("not saved: " + w); return; }
                CamPaths.WriteMarkersJson(mj, ms, r.Demo, Looks.MapOf(r.Demo));
                t.Write(ct, "from " + ms.Count + " markers, edited in the Designer's 3D view, " + Path.GetFileName(r.Demo));
                r.TickIn = t.First.ToString(CultureInfo.InvariantCulture);
                r.TickOut = t.Last.ToString(CultureInfo.InvariantCulture);
                if (!r.Note.Contains("edited")) r.Note = (r.Note.Length > 0 ? r.Note + "; " : "") + "edited in the 3D view";
                // the note box shows this row: it holds the note too, or its next save would put the old one back
                if (_noteRow == r && _note.Text != r.Note) _note.Text = r.Note;
                _q.Save();
                if (at < _list.Items.Count) { ((Item)_list.Items[at]).Note = r.Note; _list.Items.Refresh(); }
                Report("Row " + (at + 1) + " \"" + r.Name + "\" saved as edited (the original is kept as " + id + ".orig.*; Ctrl+Z in the view undoes).");
            }
            catch (Exception ex) { App.Note("saving an edited path", ex); Report("could not save the edit: " + ex.Message); }
        }

        // ---------------------------------------------------------------- the pipe: "queue ..." (bo2batch)

        private string OnUi(Func<string> work)
        {
            string result = null;
            DispatcherOperation op = Dispatcher.BeginInvoke(new Action(() =>
            {
                try { result = work(); }
                catch (Exception ex) { result = "error: " + ex.GetBaseException().Message; }
            }));
            if (op.Wait(TimeSpan.FromSeconds(20)) != DispatcherOperationStatus.Completed) return "busy: the window did not answer within 20 s";
            return result;
        }

        private const string PipeHelp =
            "queue commands:\n" +
            "  queue open DIR | new           a queue folder (made if new) | tonight's\n" +
            "  queue show                     the CAM QUEUE tab to the front (for a picture)\n" +
            "  queue snap FILE.png            the Console's own picture of its window, whatever covers it\n" +
            "  queue list                     the rows\n" +
            "  queue markers                  the markers in the game now (read only)\n" +
            "  queue film                     which demo the game is playing\n" +
            "  queue save [NAME]              SAVE MY MARKERS\n" +
            "  queue jump N [keep]            the film to his marker N (the game's own jump; keep = the camera stays)\n" +
            "  queue pov-in | pov-out | pov-add [NAME]   a POV take marked live\n" +
            "  queue add-track TRACK DEMO [NAME]         a generated path (copied in)\n" +
            "  queue add-pov DEMO POV TICK_IN TICK_OUT [NAME]\n" +
            "  queue move I J | remove I | rename I NAME | dup I | note I TEXT   (rows from 1)\n" +
            "  queue default KEY VALUE        queue.txt (pov_fps, cine_before, cine_hud, ...)\n" +
            "  queue cfg KIND|I TEXT          a kind's CFG or row I's own (| between lines)\n" +
            "  queue check | run [dry] | stop | status | then quit|stay\n" +
            "  queue view | view row I | view live | view speed X | view preset front|back|left|right|top|bottom|opposite|toggle|camera\n" +
            "  queue view markers | view pick X Y | view drag I DX DY [aim] | view undo   (the first non-camera view's markers, for tests)\n" +
            "  queue view list | view add ANGLE MAP PLAYERS [pin] | view close I | view main I | view look I MAP | view pop I | view default\n" +
            "  queue run failed               the rows that failed in the last run, once more\n" +
            "  queue apply [I]                Apply to game: row I's markers (or what the views show) in place of the game's\n" +
            "  queue track-from MARKERS.json OUT [STEP]  the C# track maths on a file (to compare with markers_to_track.py)";

        private string Pipe(string rest)
        {
            string[] a = (rest ?? "").Trim().Split(new[] { ' ' }, 2);
            string cmd = a[0].ToLowerInvariant(), arg = a.Length > 1 ? a[1].Trim() : "";
            Func<string, int> row = s => { int i; return int.TryParse(s, out i) ? i - 1 : -1; };
            switch (cmd)
            {
                case "": case "help": return PipeHelp;
                case "open": return OpenQueue(arg);
                case "said": return _status.Text;                              // the page's last words (tests: what Apply said when done)
                case "apply":
                    {
                        if (arg.Length == 0) return ApplyToGame();
                        int i = row(arg);
                        if (_q == null || i < 0 || i >= _q.Rows.Count) return "no row " + arg;
                        return ApplyToGame(_q.Rows[i]);
                    }
                case "snap":
                    {
                        // the Console's own picture of itself, whatever covers it (Windows won't raise it over the game)
                        Window w = Window.GetWindow(this);
                        if (w == null || arg.Length == 0) return "usage: queue snap FILE.png";
                        var root = (FrameworkElement)w.Content;
                        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight),
                                                                                      96, 96, PixelFormats.Pbgra32);
                        var dv = new DrawingVisual();
                        using (DrawingContext dc = dv.RenderOpen())
                        {
                            dc.DrawRectangle(((Control)w).Background ?? Brushes.Black, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                        }
                        bmp.Render(dv);
                        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(arg)));
                        using (FileStream f = File.Create(arg)) enc.Save(f);
                        return "picture " + arg + " (" + bmp.PixelWidth + "x" + bmp.PixelHeight + ")";
                    }
                case "show":
                    {
                        var win = Application.Current.MainWindow as MainWindow;
                        if (win == null) return "the main window is not up";
                        win.ShowCamQueue();
                        return "CAM QUEUE shown";
                    }
                case "new": NewQueue(); return _q != null ? "queue " + _q.Dir : "could not make one";
                case "list":
                    {
                        if (_q == null) return "no queue open";
                        var sb = new StringBuilder("queue " + _q.Dir + "\n");
                        for (int i = 0; i < _q.Rows.Count; i++)
                        {
                            QueueRow r = _q.Rows[i];
                            sb.Append(i + 1).Append("  ").Append(r.IsCine ? "CINE" : "POV ").Append("  ").Append(r.Name).Append("  ").Append(r.Folder)
                              .Append("  ticks ").Append(r.TickIn).Append('-').Append(r.TickOut).Append("  ").Append(r.Source)
                              .Append(r.Cfg.Length > 0 ? "  cfg " + r.Cfg : "").Append(r.Note.Length > 0 ? "  note: " + r.Note : "").Append('\n');
                        }
                        return sb.Append(_summary.Text).ToString();
                    }
                case "markers":
                    {
                        if (!Attached()) return "the game is not running";
                        string why, sig;
                        List<DollyMarker> ms = CamPaths.ReadMarkers(_game, out why, out sig);
                        if (ms == null) return "no markers: " + why;
                        var sb = new StringBuilder(ms.Count + " markers (theater 0x" + _game.TheaterBase.ToString("X8") + ")\n");
                        foreach (DollyMarker m in ms)
                            sb.AppendFormat(CultureInfo.InvariantCulture, "tick {0}  pos {1:0.0} {2:0.0} {3:0.0}  pitch {4:0.00} yaw {5:0.00} roll {6:0.0} fov {7:0.0}\n", m.Tick, m.X, m.Y, m.Z, m.Pitch, m.Yaw, m.Roll, m.Fov);
                        return sb.Append(_live.Text).ToString();
                    }
                case "film":
                    {
                        if (!Attached()) return "the game is not running";
                        string how;
                        string f = FilmNow(false, out how);
                        return (f ?? "unknown") + " (" + how + ")";
                    }
                case "save": return SaveMarkers(arg.Length > 0 ? arg : null, false);
                case "jump":
                    {
                        string[] p = SplitArgs(arg); int n;
                        if (p.Length < 1 || !int.TryParse(p[0], out n)) return "usage: queue jump N [keep]";
                        return MarkerJump(n, p.Length > 1 && p[1] == "keep");
                    }
                case "dolly": return PreviewInDolly(row(arg));                 // 2026-10-18: the path in the game's own Dolly Camera, looping
                case "pov-in": return MarkPov(true);
                case "pov-out": return MarkPov(false);
                case "pov-add": return AddPovLive(arg.Length > 0 ? arg : null, false);
                case "add-track":
                    {
                        if (_q == null) return "no queue open";
                        string[] p = SplitArgs(arg);
                        if (p.Length < 2) return "usage: queue add-track TRACK DEMO [NAME]";
                        QueueRow r = _q.AddTrack(p[0], p[1], p.Length > 2 ? p[2] : null, "generator", p.Length > 3 ? p[3] : "");
                        Refresh(_q.Rows.Count - 1);
                        return "row " + _q.Rows.Count + ": " + r.Name + " (" + r.TickIn + "-" + r.TickOut + ")";
                    }
                case "add-pov":
                    {
                        if (_q == null) return "no queue open";
                        string[] p = SplitArgs(arg);
                        int tin, tout;
                        if (p.Length < 4 || !int.TryParse(p[2], out tin) || !int.TryParse(p[3], out tout)) return "usage: queue add-pov DEMO POV TICK_IN TICK_OUT [NAME]";
                        QueueRow r = _q.AddPov(p[0], p[1], tin, tout, p.Length > 4 ? p[4] : null);
                        Refresh(_q.Rows.Count - 1);
                        return "row " + _q.Rows.Count + ": " + r.Name;
                    }
                case "move": { string[] p = SplitArgs(arg); if (_q == null || p.Length < 2) return "usage: queue move I J"; _q.Move(row(p[0]), row(p[1])); Refresh(row(p[1])); return "moved"; }
                case "remove": { if (_q == null) return "no queue"; int i = row(arg); if (i < 0 || i >= _q.Rows.Count) return "no row " + arg; string n = _q.Rows[i].Name; _q.Remove(i); Refresh(); return "removed " + n; }
                case "rename": { string[] p = arg.Split(new[] { ' ' }, 2); if (_q == null || p.Length < 2) return "usage: queue rename I NAME"; _q.Rename(row(p[0]), p[1]); Refresh(row(p[0])); return "now " + _q.Rows[row(p[0])].Name; }
                case "dup": { if (_q == null) return "no queue"; QueueRow r = _q.Duplicate(row(arg)); Refresh(); return r == null ? "no row " + arg : "row " + (_q.Rows.IndexOf(r) + 1) + ": " + r.Name; }
                case "note": { string[] p = arg.Split(new[] { ' ' }, 2); if (_q == null || p.Length < 1 || row(p[0]) < 0 || row(p[0]) >= _q.Rows.Count) return "usage: queue note I TEXT"; _q.Rows[row(p[0])].Note = p.Length > 1 ? p[1] : ""; _q.Save(); Refresh(row(p[0])); return "noted"; }
                case "default": { string[] p = arg.Split(new[] { ' ' }, 2); if (_q == null || p[0].Length == 0) return "usage: queue default KEY VALUE"; _q.SetDefault(p[0], p.Length > 1 ? p[1] : ""); ShowDefaults(); return p[0] + "=" + _q.Default(p[0]); }
                case "cfg":
                    {
                        string[] p = arg.Split(new[] { ' ' }, 2);
                        if (_q == null || p[0].Length == 0) return "usage: queue cfg pov|cine|I TEXT";
                        string text = p.Length > 1 ? p[1].Replace("|", "\r\n") : "";
                        string file;
                        if (p[0] == "pov" || p[0] == "cine") { file = Path.Combine(_q.Dir, _q.Default(p[0] + "_cfg").Length > 0 ? _q.Default(p[0] + "_cfg") : p[0] + ".cfg"); }
                        else { int i = row(p[0]); if (i < 0 || i >= _q.Rows.Count) return "no row " + p[0]; file = _q.OwnCfg(i, true); Refresh(i); }
                        File.WriteAllText(file, text, new UTF8Encoding(false));
                        return "wrote " + file;
                    }
                case "then":
                    if (arg == "quit") _quitAfter.IsChecked = true;
                    else if (arg == "stay") _quitAfter.IsChecked = false;
                    else return "usage: queue then quit | stay   (after RUN QUEUE: quit the game cleanly, or leave it up)";
                    return "after the run: " + (_quitAfter.IsChecked == true ? "quit the game cleanly" : "leave the game up");
                case "check": return Check();
                case "run":
                    if (arg.Equals("failed", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_q != null && _lastFailed.Count == 0 && File.Exists(Path.Combine(_q.Dir, "last-failed.txt")))
                            foreach (string t in File.ReadAllText(Path.Combine(_q.Dir, "last-failed.txt")).Split(new[] { ' ', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                            { int n; if (int.TryParse(t, out n)) _lastFailed.Add(n); }
                        if (_q == null || _lastFailed.Count == 0) return "no failed rows from the last run";
                        _running = true; _stopAsked = false; _dryRun = false;
                        _night.Add(DateTime.Now.ToString("HH:mm:ss") + "  recording the rows that failed again: " + string.Join(", ", _lastFailed.Select(x => x.ToString()).ToArray()));
                        WriteStatus();
                        string why = BatchControl.Instance.StartQueue(RetryList(_lastFailed), false, res => Finished(res, false, false, true));
                        if (why != null) { _running = false; return "not started: " + why; }
                        return "recording rows " + string.Join(", ", _lastFailed.Select(x => x.ToString()).ToArray()) + " again";
                    }
                    return Run(arg.Equals("dry", StringComparison.OrdinalIgnoreCase), false);      // the pipe is a person at a script, not a click
                case "stop": return StopRun();
                case "status": return File.Exists(_q != null ? _q.StatusPath : "") ? File.ReadAllText(_q.StatusPath) : "no status yet";
                case "view":
                    {
                        // preset / markers / pick / drag act on the first view that is not looking through the camera
                        string[] p = SplitArgs(arg);
                        CamView ev = _views.EditView;
                        if (p.Length >= 2 && p[0] == "preset") { if (ev != null) ev.SetView(p[1]); }
                        else if (p.Length >= 1 && p[0] == "markers") { return ev != null ? ev.MarkerScreens() : "no view"; }
                        else if (p.Length >= 3 && p[0] == "pick") { return ev != null ? ev.PickForTest(double.Parse(p[1], CultureInfo.InvariantCulture), double.Parse(p[2], CultureInfo.InvariantCulture)) : "no view"; }
                        else if (p.Length >= 4 && p[0] == "drag") { return ev != null ? ev.DragForTest(row(p[1]), double.Parse(p[2], CultureInfo.InvariantCulture), double.Parse(p[3], CultureInfo.InvariantCulture), p.Length > 4 && p[4] == "aim") : "no view"; }
                        else if (p.Length >= 1 && p[0] == "undo") { _scene.Undo(); }
                        else if (p.Length >= 2 && p[0] == "pop") { CamView v = _views.View(row(p[1])); if (v == null) return "no view " + p[1]; _views.TogglePop(v); return _views.List(); }
                        else if (p.Length >= 2 && p[0] == "row") { SelectRow(row(p[1])); }
                        else if (p.Length >= 1 && p[0] == "live") { _viewLive = true; _list.SelectedIndex = -1; ShowLiveInView(true); }
                        else if (p.Length >= 2 && p[0] == "speed") { double d; if (double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) _scene.Speed = d; }
                        else if (p.Length >= 1 && p[0] == "list") { return _views.List(); }
                        else if (p.Length >= 4 && p[0] == "add")
                        {
                            ViewAngle va; MapLook m; PlayersLook pl;
                            if (!Enum.TryParse(p[1], true, out va) || !Enum.TryParse(p[2], true, out m) || !Enum.TryParse(p[3], true, out pl))
                                return "usage: queue view add camera|free|top|front|right|back|left|bottom solid|ghost|wire|off on|ghost|off [pin]";
                            _views.AddView(va, m, pl, p.Length > 4 && p[4] == "pin");
                            return _views.List();
                        }
                        else if (p.Length >= 2 && p[0] == "close") { _views.Close(row(p[1])); return _views.List(); }
                        else if (p.Length >= 2 && p[0] == "main") { _views.Main(row(p[1])); return _views.List(); }
                        else if (p.Length >= 3 && p[0] == "look")
                        {
                            CamView v = _views.View(row(p[1]));
                            MapLook m;
                            if (v == null || !Enum.TryParse(p[2], true, out m)) return "usage: queue view look I solid|ghost|wire|off";
                            v.SetMapLook(m);
                            return _views.List();
                        }
                        else if (p.Length >= 1 && p[0] == "default") { _views.DefaultLayout(); return _views.List(); }
                        return _scene.Status + "\n" + _views.List() + "\nlast map load: " + CamView.LastMapLoad + "\nmeshes looked for in: " + string.Join("; ", CamView.MeshFolders().ToArray());
                    }
                case "track-from":
                    {
                        string[] p = SplitArgs(arg);
                        if (p.Length < 2) return "usage: queue track-from MARKERS.json OUT [STEP]";
                        int step = p.Length > 2 ? int.Parse(p[2], CultureInfo.InvariantCulture) : 5;
                        List<DollyMarker> ms = CamPaths.ReadMarkersJson(p[0]);
                        string w;
                        CamTrack t = CamPaths.TrackFromMarkers(ms, step, out w);
                        if (t == null) return "no track: " + w;
                        t.Write(p[1], "from " + p[0] + " (Catmull-Rom through " + ms.Count + " markers, " + step + " ms steps)");
                        return t.Ticks.Length + " samples, ticks " + t.First + ".." + t.Last + " -> " + p[1] + (w.Length > 0 ? " (" + w + ")" : "");
                    }
                default: return "unknown: queue " + cmd + "\n" + PipeHelp;
            }
        }

        /// <summary>Arguments split at spaces, with "double quotes" around any that hold spaces (demo paths do).</summary>
        private static string[] SplitArgs(string s)
        {
            var list = new List<string>();
            var cur = new StringBuilder();
            bool q = false;
            foreach (char c in s ?? "")
            {
                if (c == '"') { q = !q; continue; }
                if (c == ' ' && !q) { if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); } continue; }
                cur.Append(c);
            }
            if (cur.Length > 0) list.Add(cur.ToString());
            return list.ToArray();
        }
    }
}
