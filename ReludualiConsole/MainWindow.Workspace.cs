using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ReludualiConsole
{
    /// <summary>
    /// The Console as a workspace (2026-10-14). Mycelium: "Could we arrange the UI in a way that essentially treats BO2 is if it
    /// were a Viewport within the Console? So One could adjust the sliders perhaps overtop of BO2, or move them aside so the
    /// BO2 is fully visible, and click into BO2 when they have to - with multiple Layout Customizations ... They could still
    /// keep the original layout, or they could have BO2 Inside the Console".
    ///
    /// Three layouts, switched in the header and remembered:
    ///  - Classic: the Console as it was; the game in its own window.
    ///  - Workspace: the game in a slot, the open page beside it (right) or under it (below); the splitter between them.
    ///  - Overlay: the game fills the work area; the page floats over it (FloatPanel), see-through, dragged anywhere.
    /// The game itself is GameDock's: its own window, borderless, owned by the Console, kept over the slot at its own shape.
    /// </summary>
    public partial class MainWindow
    {
        private enum WorkLayout { Classic, Workspace, Overlay }

        private readonly GameDock _dock = new GameDock();
        private WorkLayout _layout = WorkLayout.Classic;
        private bool _below;                         // Workspace: the page under the game instead of beside it
        private double _pageWidth = 560, _pageHeight = 380;
        private FloatPanel _float;
        private Rect _floatRect = Rect.Empty;
        private double _floatAlpha = 0.9;
        private bool _floatShown = true;
        private Border _slotHole;
        private Grid _slotBody;
        private TextBlock _slotText;
        private TheaterTimeline _timeline;
        private Button _belowButton, _panelButton;
        private Rect _placed = Rect.Empty;
        private Point _lastWindowPos;

        // ---------------------------------------------------------------- Ctrl+Alt+D: back to the Designer, from anywhere
        // A game that holds the mouse (the free camera, first person) puts the cursor back in its middle at once, so "click
        // the Designer" cannot work then, and the taskbar button brings the docked game forward (the fake-mouse test's B1).
        // The hotkey always works: Windows hands it to the Designer whoever is in front.
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        private const int HotkeyBack = 0x5244, HotkeyBackChosen = 0x5245;
        // the key shown in words ("press Alt+Q to come back"): the one chosen on Settings (2026-10-18, Mycelium: "Alt-Q is my go-to")
        public static string BackKeys { get { return Shortcuts.Back; } }

        private void InitHotkey()
        {
            if (App.Tour) return;
            var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (src == null) return;
            src.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == 0x0312 && (wp.ToInt32() == HotkeyBack || wp.ToInt32() == HotkeyBackChosen)) { BackToDesigner(); handled = true; }      // WM_HOTKEY
                return IntPtr.Zero;
            });
            if (!RegisterHotKey(src.Handle, HotkeyBack, 0x0001 | 0x0002 | 0x4000, (uint)'D'))               // Alt, Ctrl, no repeat
                App.Note(Shortcuts.SecondBack + " is taken by another program", null);
            Action chosen = () =>
            {
                try { UnregisterHotKey(src.Handle, HotkeyBackChosen); } catch { }
                uint mods, vk;
                string k = Shortcuts.Back;
                if (k.Equals(Shortcuts.SecondBack, StringComparison.OrdinalIgnoreCase) || !Shortcuts.Parse(k, out mods, out vk)) return;
                if (!RegisterHotKey(src.Handle, HotkeyBackChosen, mods | 0x4000, vk))
                    Notice(k + " is taken by another program: choose another key on Settings (" + Shortcuts.SecondBack + " still brings the Designer back).");
            };
            chosen();
            Shortcuts.Changed += () => Dispatcher.BeginInvoke(chosen);
            Closed += (s, e) => { try { UnregisterHotKey(src.Handle, HotkeyBack); UnregisterHotKey(src.Handle, HotkeyBackChosen); } catch { } };
        }

        private void BackToDesigner()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            SetForegroundWindow(new WindowInteropHelper(this).Handle);
        }

        private void InitWorkspace()
        {
            SourceInitialized += (s, e) => { try { InitHotkey(); } catch (Exception ex) { App.Note("the " + BackKeys + " hotkey", ex); } };
            // the slot: a thin bar (what the game is doing, the ways in and out), then the hole the game sits over
            _slotText = new TextBlock { Foreground = Ui.Brush("Dim"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 8, 0) };
            var into = Ui.Primary(Ui.Button("Into the game", () => { _dock.Into(); }, "The game gets the keyboard and the mouse (a click on the game does the same; the free camera does not turn as it takes them). Click the Designer, or press " + BackKeys + ", to come back.", Glyph.Play));
            into.Margin = new Thickness(0); into.Padding = new Thickness(12, 3, 12, 3); into.MinHeight = 26;
            // words, not just an icon: the expand-arrows icon it had read as "full screen" (Mycelium's round 1, 2026-10-15:
            // "The frame button in the bar over the game. I'm not sure what that one means.")
            _belowButton = Ui.Button("Page below", ToggleBelow, "Put the page under the game, or back beside it", Glyph.DockBottom);
            _belowButton.Margin = new Thickness(0, 0, 6, 0); _belowButton.MinHeight = 26; _belowButton.Padding = new Thickness(9, 3, 9, 3);
            _panelButton = Ui.Button("Panel", ToggleFloat, "Show or hide the floating page over the game", Glyph.Sliders);
            _panelButton.Margin = new Thickness(0, 0, 6, 0); _panelButton.MinHeight = 26; _panelButton.Padding = new Thickness(10, 3, 10, 3);
            var bar = new DockPanel { Margin = new Thickness(10, 6, 8, 6), LastChildFill = true };
            DockPanel.SetDock(into, Dock.Right); DockPanel.SetDock(_belowButton, Dock.Right); DockPanel.SetDock(_panelButton, Dock.Right);
            bar.Children.Add(into); bar.Children.Add(_belowButton); bar.Children.Add(_panelButton);
            bar.Children.Add(new TextBlock { Text = "\uE7F4", FontFamily = Ui.IconFont, FontSize = 13, Foreground = Ui.Brush("Accent"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            bar.Children.Add(_slotText);
            _slotHole = new Border { Background = Ui.Brush("ViewBack"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                     Child = new TextBlock { Text = "Start Redacted: the game comes in here.", Foreground = Ui.Brush("ViewText"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            _slotBody = new Grid { Margin = new Thickness(6, 0, 6, 6), ClipToBounds = true };
            _slotBody.Children.Add(_slotHole);
            _slotBody.SizeChanged += (s, e) => SizeHole();
            var dock = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            dock.Children.Add(bar);
            // our own theater timeline under the game (2026-10-16; Mycelium: "we can just have our GUI there instead")
            _timeline = new TheaterTimeline(() => _dock.Window);
            DockPanel.SetDock(_timeline, Dock.Bottom);
            dock.Children.Add(_timeline);
            dock.Children.Add(_slotBody);
            GameSlot.Child = dock;

            _lastWindowPos = new Point(double.NaN, double.NaN);
            if (!App.Tour)
            {
                var inGame = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
                inGame.Tick += (s, e) => ShowInGame();
                inGame.Start();
            }
            WorkSplit.DragCompleted += (s, e) => { RememberPageSize(); SavePlacement(); };
            LocationChanged += (s, e) => { FollowWindow(); PlaceGame(); };
            SizeChanged += (s, e) => PlaceGame();
            StateChanged += (s, e) => PlaceGame();
            LayoutUpdated += (s, e) => PlaceGame();
            Activated += (s, e) => PlaceGame();
            BatchRecorder.BatchPipe.Extra["console"] = rest => (string)Dispatcher.Invoke(new Func<string>(() => ConsolePipe(rest)));
        }

        private void Layout_Click(object sender, RoutedEventArgs e)
        {
            string tag = (string)((FrameworkElement)sender).Tag;
            SetLayout(tag == "workspace" ? WorkLayout.Workspace : tag == "overlay" ? WorkLayout.Overlay : WorkLayout.Classic);
        }

        // 2026-10-17: a layout shown for one run only ("console layout workspace once": the --theater launch) is not the
        // one saved; the layout before it is, until a layout is chosen for real
        private WorkLayout? _savedLayout;

        private void SetLayout(WorkLayout l, bool remember = true)
        {
            if (!remember && _savedLayout == null) _savedLayout = _layout;
            if (remember) _savedLayout = null;
            _layout = l;
            ApplyLayout();
            SavePlacement();
        }

        /// <summary>Puts the work area together for the layout, docks or releases the game, moves the page where it belongs.</summary>
        private void ApplyLayout()
        {
            LayoutClassic.IsChecked = _layout == WorkLayout.Classic;
            LayoutWorkspace.IsChecked = _layout == WorkLayout.Workspace;
            LayoutOverlay.IsChecked = _layout == WorkLayout.Overlay;
            WorkArea.RowDefinitions.Clear();
            WorkArea.ColumnDefinitions.Clear();
            foreach (UIElement e in new UIElement[] { GameSlot, WorkSplit, PageArea }) { Grid.SetRow(e, 0); Grid.SetColumn(e, 0); }
            _belowButton.Visibility = _layout == WorkLayout.Workspace ? Visibility.Visible : Visibility.Collapsed;
            _belowButton.Content = _below ? Ui.IconText(Glyph.DockRight, "Page beside") : Ui.IconText(Glyph.DockBottom, "Page below");
            _panelButton.Visibility = _layout == WorkLayout.Overlay ? Visibility.Visible : Visibility.Collapsed;

            if (_layout == WorkLayout.Classic)
            {
                GameSlot.Visibility = Visibility.Collapsed;
                WorkSplit.Visibility = Visibility.Collapsed;
                PageArea.Visibility = Visibility.Visible;
                PageToHost();
                SteadyOff(false);
                if (_dock.Docked) _dock.Release();
                _placed = Rect.Empty;
                return;
            }
            if (StagesPanel.Visibility == Visibility.Visible) OpenStage(_stage);      // the start screen is Classic's: open the tools
            GameSlot.Visibility = Visibility.Visible;
            if (_layout == WorkLayout.Workspace)
            {
                WorkSplit.Visibility = Visibility.Visible;
                PageArea.Visibility = Visibility.Visible;
                if (_below)
                {
                    WorkArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 160 });
                    WorkArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
                    WorkArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_pageHeight), MinHeight = 140 });
                    Grid.SetRow(WorkSplit, 1); Grid.SetRow(PageArea, 2);
                    WorkSplit.Height = 8; WorkSplit.Width = double.NaN;
                    WorkSplit.HorizontalAlignment = HorizontalAlignment.Stretch; WorkSplit.VerticalAlignment = VerticalAlignment.Stretch;
                    WorkSplit.ResizeDirection = GridResizeDirection.Rows; WorkSplit.Cursor = Cursors.SizeNS;
                    GameSlot.Margin = new Thickness(12, 12, 12, 2);
                }
                else
                {
                    WorkArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 240 });
                    WorkArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                    WorkArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_pageWidth), MinWidth = 320 });
                    Grid.SetColumn(WorkSplit, 1); Grid.SetColumn(PageArea, 2);
                    WorkSplit.Width = 8; WorkSplit.Height = double.NaN;
                    WorkSplit.HorizontalAlignment = HorizontalAlignment.Stretch; WorkSplit.VerticalAlignment = VerticalAlignment.Stretch;
                    WorkSplit.ResizeDirection = GridResizeDirection.Columns; WorkSplit.Cursor = Cursors.SizeWE;
                    GameSlot.Margin = new Thickness(12, 12, 2, 12);
                }
                PageToHost();
            }
            else
            {
                WorkSplit.Visibility = Visibility.Collapsed;
                PageArea.Visibility = Visibility.Collapsed;
                GameSlot.Margin = new Thickness(12);
                PageToFloat();
            }
            DockGame();
        }

        /// <summary>The game into the slot (when it is running); the tether's 5-second look calls this too, for a game started later.</summary>
        private void DockGame()
        {
            if (_layout == WorkLayout.Classic || App.Tour) return;
            IntPtr me = new WindowInteropHelper(this).Handle;
            if (me == IntPtr.Zero) return;
            string was = _dock.Status;
            _dock.Dock(me);
            if (_dock.Docked) { SizeHole(); _placed = Rect.Empty; PlaceGame(); if (_float != null) _float.SetOwnerWindow(_dock.Window); KeepSteady(); }
            if (!_inGameShown) _slotText.Text = _dock.Docked ? SlotLine() : _dock.Status;
            ((TextBlock)_slotHole.Child).Visibility = _dock.Docked ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>The hole keeps the game's own shape (1920x808 here: 2.376:1), as big as the slot allows.</summary>
        private void SizeHole()
        {
            double w = _slotBody.ActualWidth, h = _slotBody.ActualHeight, a = _dock.Aspect;
            if (w < 2 || h < 2) return;
            if (w / h > a) { _slotHole.Height = h; _slotHole.Width = h * a; }
            else { _slotHole.Width = w; _slotHole.Height = w / a; }
        }

        /// <summary>The game's window over the hole, in physical pixels; only when the hole moved (it is called often).</summary>
        private void PlaceGame()
        {
            if (_layout == WorkLayout.Classic || !_dock.Docked || GameSlot.Visibility != Visibility.Visible || !_slotHole.IsVisible) return;
            if (WindowState == WindowState.Minimized) return;                 // owned: it is hidden with the Console
            var src = PresentationSource.FromVisual(_slotHole);
            if (src == null || src.CompositionTarget == null) return;
            Point tl = _slotHole.PointToScreen(new Point(0, 0));                 // physical pixels already
            Matrix m = src.CompositionTarget.TransformToDevice;
            var r = new Rect(Math.Round(tl.X), Math.Round(tl.Y), Math.Round(_slotHole.ActualWidth * m.M11), Math.Round(_slotHole.ActualHeight * m.M22));
            if (r == _placed) return;
            _placed = r;
            _dock.Place((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
        }

        private void ToggleBelow()
        {
            RememberPageSize();
            _below = !_below;
            ApplyLayout();
            SavePlacement();
        }

        private void RememberPageSize()
        {
            if (_layout != WorkLayout.Workspace) return;
            if (_below && WorkArea.RowDefinitions.Count == 3) _pageHeight = Math.Max(140, WorkArea.RowDefinitions[2].ActualHeight);
            if (!_below && WorkArea.ColumnDefinitions.Count == 3) _pageWidth = Math.Max(320, WorkArea.ColumnDefinitions[2].ActualWidth);
        }

        // ---------------------------------------------------------------- the page: in the Console, or floating over the game

        private object _pageInFloat;

        private void PageToHost()
        {
            if (_float != null)
            {
                _floatRect = new Rect(_float.Left, _float.Top, _float.Width, _float.Height);
                _floatAlpha = _float.Alpha;
                if (_float.Page != null) { _float.Page = null; }
                _float.Hide();
            }
            if (_current != null && PageHost.Content != _current.Page) PageHost.Content = _current.Page;
            _pageInFloat = null;
        }

        private void PageToFloat()
        {
            if (_current == null) return;
            if (_float == null)
            {
                _float = new FloatPanel();
                _float.BesideRequested += () => SetLayout(WorkLayout.Workspace);
                _float.Changed += () => { if (_float.IsVisible) { _floatRect = new Rect(_float.Left, _float.Top, _float.Width, _float.Height); _floatAlpha = _float.Alpha; } };
                _float.IsVisibleChanged += (s, e) => { _floatShown = _float.IsVisible || _layout != WorkLayout.Overlay; };
                _float.SetOwnerWindow(_dock.Docked ? _dock.Window : new WindowInteropHelper(this).Handle);
                _float.Alpha = _floatAlpha;
            }
            if (PageHost.Content == _current.Page) PageHost.Content = null;
            _float.Page = _current.Page;
            _float.Heading = _current.Title;
            _pageInFloat = _current.Page;
            if (_floatRect.IsEmpty)
            {
                // first time: on the right of the work area
                Point tl = WorkArea.PointToScreen(new Point(0, 0));
                var src = PresentationSource.FromVisual(this);
                double k = src != null ? src.CompositionTarget.TransformFromDevice.M11 : 1;
                _floatRect = new Rect(tl.X * k + Math.Max(0, WorkArea.ActualWidth - 560), tl.Y * k + 20, 540, Math.Max(400, WorkArea.ActualHeight - 40));
            }
            _float.Left = _floatRect.X; _float.Top = _floatRect.Y; _float.Width = _floatRect.Width; _float.Height = _floatRect.Height;
            if (_floatShown && !App.Tour) _float.Show();
        }

        private void ToggleFloat()
        {
            if (_float == null) { PageToFloat(); return; }
            if (_float.IsVisible) _float.Hide(); else { _float.Show(); }
            _floatShown = _float.IsVisible;
        }

        /// <summary>The floating panel moves with the Console.</summary>
        private void FollowWindow()
        {
            var p = new Point(Left, Top);
            if (_float != null && _float.IsVisible && !double.IsNaN(_lastWindowPos.X))
            {
                _float.Left += p.X - _lastWindowPos.X;
                _float.Top += p.Y - _lastWindowPos.Y;
            }
            _lastWindowPos = p;
        }

        /// <summary>A tool came to the front: in Overlay it goes into the floating panel.</summary>
        private void PageShown()
        {
            if (_layout == WorkLayout.Overlay) PageToFloat();
        }

        /// <summary>For --gui-tour, and for --theater (TheaterLaunch.cs, 2026-10-17): the pipe's "console ..." commands. Its
        /// words "layout", "tool" and "startgame" are what --theater sends: keep them.</summary>
        public string TourConsole(string command) { return ConsolePipe(command); }

        // ---------------------------------------------------------------- no camera turn when the game takes the focus

        private int _steadyPid;              // the run of the game the bridge's "steady" is on for
        private int _steadyAsking;
        private string _steadyNote = "";

        private string SlotLine() { return "The game. Click it, or Into the game, to play; click the Designer, or press " + BackKeys + ", to come back." + _steadyNote; }

        private bool _inGameShown;

        /// <summary>
        /// The slot lights up while the game has the mouse and keys (Mycelium's round 1, 2026-10-15: "We should probably have
        /// a highlight over this entire window so then users can be ... completely sure when it's active").
        /// </summary>
        private void ShowInGame()
        {
            bool inGame = _layout != WorkLayout.Classic && _dock.InFront;
            if (inGame == _inGameShown) return;
            _inGameShown = inGame;
            GameSlot.BorderBrush = inGame ? Ui.Brush("Accent") : (Brush)FindResource("RimSoft");
            GameSlot.BorderThickness = new Thickness(inGame ? 2 : 1);
            _slotText.Foreground = Ui.Brush(inGame ? "Accent" : "Dim");
            _slotText.Text = inGame ? "You're in the game: it has the mouse and keys. " + BackKeys + " (or a click on the Designer) comes back."
                                    : (_dock.Docked ? SlotLine() : _dock.Status);
        }

        /// <summary>
        /// While the game is docked, RipperBridge's "steady on" (0.11.0-dev15): the game then turns nothing as it takes the
        /// focus - after the Console moved it, for a click anywhere on it, for Into (0416-0426; see GameDock). Asked off
        /// this thread (the bridge's pipe may be slow); the tether's 5-second look asks again until it holds, once a run.
        /// </summary>
        private void KeepSteady()
        {
            if (!_dock.Docked || _dock.Pid == _steadyPid || App.Tour) return;
            if (System.Threading.Interlocked.CompareExchange(ref _steadyAsking, 1, 0) != 0) return;
            int pid = _dock.Pid, ow = _dock.OwnWidth, oh = _dock.OwnHeight;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string a = null;
                try
                {
                    // dev17 takes the game's own size too (its menus); dev15/16 know only "steady on"
                    a = ow > 0 && oh > 0 ? BatchRecorder.BridgeClient.Ask("steady on " + ow + " " + oh, 600, 1) : null;
                    if (a == null || a.StartsWith("error usage", StringComparison.Ordinal)) a = BatchRecorder.BridgeClient.Ask("steady on", 600, 1);
                }
                catch (Exception ex) { App.Note("the bridge's steady", ex); }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    System.Threading.Interlocked.Exchange(ref _steadyAsking, 0);
                    if (a != null && a.StartsWith("steady on 1", StringComparison.Ordinal)) { _steadyPid = pid; _steadyNote = ""; }
                    else if (a != null && a.StartsWith("error unknown command", StringComparison.Ordinal))
                    { _steadyPid = pid; _steadyNote = " (This RipperBridge is too old to hold the camera still as the game takes the focus: Setup installs the RipperBridge that comes with the Designer.)"; }
                    if (_dock.Docked && !_inGameShown) _slotText.Text = SlotLine();
                }));
            });
        }

        private int _dollyPid, _dollyAsking;

        /// <summary>
        /// The Advanced Dolly Cam's roll and zoom, on whenever the bridge answers, in every layout (Mycelium, 2026-10-15:
        /// "I want it to kind of already be enabled"): RipperBridge dev19's "dolly on" - in the theater's free camera,
        /// scroll rolls and Shift+scroll zooms, 2.5 degrees a notch. Once per run of the game; an older bridge says it
        /// does not know the command, and that is the end of it.
        /// </summary>
        private void KeepDolly()
        {
            if (App.Tour) return;
            int pid; GameDock.FindGame(out pid);
            if (pid == 0 || pid == _dollyPid) return;
            if (System.Threading.Interlocked.CompareExchange(ref _dollyAsking, 1, 0) != 0) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string a = null;
                try { a = BatchRecorder.BridgeClient.Ask("dolly on", 600, 1); } catch (Exception ex) { App.Note("the bridge's dolly", ex); }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    System.Threading.Interlocked.Exchange(ref _dollyAsking, 0);
                    if (a != null && (a.StartsWith("dolly on 1", StringComparison.Ordinal) || a.StartsWith("error unknown command", StringComparison.Ordinal))) _dollyPid = pid;
                }));
            });
        }

        /// <summary>The game as it was: "steady off" when it leaves the Console (Classic, or the Console closing).</summary>
        private void SteadyOff(bool wait)
        {
            if (_steadyPid == 0) return;
            _steadyPid = 0; _steadyNote = "";
            if (wait) { try { BatchRecorder.BridgeClient.Ask("steady off", 300, 1); } catch { } }
            else System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { BatchRecorder.BridgeClient.Ask("steady off", 600, 1); } catch { } });
        }

        private void ReleaseWorkspace()
        {
            try { SteadyOff(true); } catch { }
            try { if (_dock.Docked) _dock.Release(); } catch { }
            try { if (_float != null) _float.Close(); } catch { }
        }

        private string LayoutSettings()
        {
            Func<double, string> f = v => v.ToString("0", CultureInfo.InvariantCulture);
            return "layout=" + (_savedLayout ?? _layout).ToString().ToLowerInvariant() + "\nbelow=" + (_below ? "1" : "0") + "\npage=" + f(_pageWidth) + "," + f(_pageHeight)
                   + (_floatRect.IsEmpty ? "" : "\npanel=" + f(_floatRect.X) + "," + f(_floatRect.Y) + "," + f(_floatRect.Width) + "," + f(_floatRect.Height))
                   + "\nalpha=" + _floatAlpha.ToString("0.00", CultureInfo.InvariantCulture) + "\n";
        }

        private void ReadLayoutSettings(System.Collections.Generic.Dictionary<string, string> d)
        {
            string v;
            if (d.TryGetValue("layout", out v)) _layout = v == "workspace" ? WorkLayout.Workspace : v == "overlay" ? WorkLayout.Overlay : WorkLayout.Classic;
            if (d.TryGetValue("below", out v)) _below = v == "1";
            double a, b, c, e;
            if (d.TryGetValue("page", out v))
            {
                string[] p = v.Split(',');
                if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a) && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b))
                { _pageWidth = Math.Max(320, a); _pageHeight = Math.Max(140, b); }
            }
            if (d.TryGetValue("panel", out v))
            {
                string[] p = v.Split(',');
                if (p.Length == 4 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a) && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b)
                    && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out c) && double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out e))
                {
                    var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                    if (screen.Contains(new Point(a + 40, b + 20))) _floatRect = new Rect(a, b, Math.Max(320, c), Math.Max(240, e));
                }
            }
            if (d.TryGetValue("alpha", out v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out a)) _floatAlpha = a;
        }

        /// <summary>
        /// The pipe's "console ...": layout classic | workspace | overlay | right | below; layout (status); into; tool KEY;
        /// panel (show / hide). For tests in the sandbox and scripts.
        /// </summary>
        private static volatile string _viewSaid, _camoSaid;

        private string ConsolePipe(string rest)
        {
            string[] w = (rest ?? "").Trim().Split(new[] { ' ' }, 2);
            string cmd = w[0].ToLowerInvariant(), arg = w.Length > 1 ? w[1].Trim().ToLowerInvariant() : "";
            switch (cmd)
            {
                case "layout":
                    bool once = arg.EndsWith(" once", StringComparison.Ordinal);   // shown for this run, not saved (--theater)
                    if (once) arg = arg.Substring(0, arg.Length - 5).Trim();
                    if (arg == "classic") SetLayout(WorkLayout.Classic, !once);
                    else if (arg == "workspace") SetLayout(WorkLayout.Workspace, !once);
                    else if (arg == "overlay") SetLayout(WorkLayout.Overlay, !once);
                    else if (arg == "below" || arg == "right") { RememberPageSize(); _below = arg == "below"; if (_layout == WorkLayout.Workspace) ApplyLayout(); SavePlacement(); }
                    else if (arg.Length > 0) return "usage: console layout classic | workspace | overlay [once] | right | below";
                    UpdateLayout();
                    _placed = Rect.Empty; PlaceGame();
                    return "layout " + _layout.ToString().ToLowerInvariant() + (_layout == WorkLayout.Workspace ? (_below ? ", the page below" : ", the page on the right") : "")
                           + "\ngame: " + _dock.Describe() + "\nslot: " + (_placed.IsEmpty ? "-" : _placed.ToString(CultureInfo.InvariantCulture))
                           + (_float != null ? "\npanel: " + (_float.IsVisible ? "shown" : "hidden") + " at " + _float.Left.ToString("0") + "," + _float.Top.ToString("0") + " " + _float.Width.ToString("0") + "x" + _float.Height.ToString("0") + ", alpha " + _float.Alpha.ToString("0.00", CultureInfo.InvariantCulture) : "");
                case "into": _dock.Into(); return "into the game: " + _dock.Describe();
                case "tool": ShowTool(arg); return _current != null ? "showing " + _current.Title : "no such tool";
                case "panel": ToggleFloat(); return _float != null && _float.IsVisible ? "panel shown" : "panel hidden";
                case "alpha":
                    {
                        double a;
                        if (_float == null || !double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out a)) return "usage: console alpha 0.35-1 (with the panel up)";
                        _float.Alpha = a; return "alpha " + _float.Alpha.ToString("0.00", CultureInfo.InvariantCulture) + ", see-through " + (_float.Layered ? "yes" : "no");
                    }
                case "front": Activate(); return "the Designer in front";
                case "global": return KnobPanel.PipeGlobal(w.Length > 1 ? w[1] : "");     // global looks (2026-10-16), names keep their case
                case "moves": return KnobPanel.PipeMoves(w.Length > 1 ? w[1] : "");       // a page's slider moves (2026-10-17, exact moves tests)
                case "startgame": return Where.StartGame();                                // what the Start Redacted button does
                case "seek":                                                               // the timeline's seek, for tests: a film tick
                    {
                        int t;
                        if (_timeline == null || !int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out t)) return "usage: console seek TICK";
                        _timeline.Seek(t); return "seeking to " + t;
                    }
                case "scrub":                                                              // a drag on the timeline, for tests: film ticks, "every MS"
                    {
                        if (_timeline == null) return "usage: console scrub TICK TICK ... [every MS]";
                        var ticks = new List<int>(); int every = 200, v;
                        string[] p = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < p.Length; i++)
                        {
                            if (p[i] == "every" && i + 1 < p.Length && int.TryParse(p[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) { every = v; i++; }
                            else if (int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) ticks.Add(v);
                            else return "usage: console scrub TICK TICK ... [every MS]";
                        }
                        return _timeline.TestScrub(ticks.ToArray(), every);
                    }
                case "contest":                                                            // the two-seat test (2026-10-17): link and look, no clicks
                    {
                        string[] c = (w.Length > 1 ? w[1].Trim() : "").Split(new[] { ' ' }, 2);
                        ContestTimer t = ContestTimer.Shared;
                        if (c[0] == "link" && c.Length > 1) return t.LinkWith(c[1].Trim());
                        if (c[0] == "status") return t.State + "\nnow: " + t.Now + "\nlast check-in: " + t.LastBeat + "\ntiming: " + t.Running;
                        return "usage: console contest link CODE | console contest status";
                    }
                case "loop":                                                               // the loop bracket (2026-10-18), for tests
                    {
                        string[] p = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        int a, b;
                        if (p.Length == 2 && int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out a) && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out b)) LoopBracket.Set(a, b);
                        else if (arg == "on" || arg == "off") LoopBracket.Switch(arg == "on");
                        else if (arg == "clear") LoopBracket.Clear();
                        else if (arg.StartsWith("zoom ", StringComparison.Ordinal) && _timeline != null && p.Length == 3)
                        {
                            int za, zb;
                            if (!int.TryParse(p[1], out za) || !int.TryParse(p[2], out zb)) return "usage: console loop zoom A B (0 0: the whole film)";
                            return _timeline.TestZoom(za, zb);
                        }
                        else if (arg.StartsWith("drag ", StringComparison.Ordinal) && _timeline != null)
                        {
                            // console loop drag strip FROM TO | console loop drag lane N FROM TO: a drag through the mouse's own code
                            int lane = 0, f, t;
                            bool strip = p.Length == 4 && p[1] == "strip";
                            if (!strip && !(p.Length == 5 && p[1] == "lane" && int.TryParse(p[2], out lane))) return "usage: console loop drag strip FROM TO | console loop drag lane N FROM TO";
                            if (!int.TryParse(p[p.Length - 2], out f) || !int.TryParse(p[p.Length - 1], out t)) return "usage: console loop drag strip FROM TO";
                            return _timeline.TestDrag(strip, lane, f, t);
                        }
                        else if (arg.Length > 0 && arg != "status") return "usage: console loop A B | on | off | clear | status | drag strip FROM TO | drag lane N FROM TO";
                        return _timeline != null ? _timeline.LanesText() : "loop " + LoopBracket.In + "-" + LoopBracket.Out + (LoopBracket.On ? " on" : " off");
                    }
                case "keys":                                                               // a take's keyframes (2026-10-18), for tests
                    {
                        // console keys add ROW PAGE/LABEL TICK V [V V] | list ROW | play on|off
                        string[] p = (w.Length > 1 ? w[1].Trim() : "").Split(new[] { ' ' }, 4, StringSplitOptions.RemoveEmptyEntries);
                        int row, tick;
                        if (p.Length >= 1 && p[0] == "play") { TakeKeys.Play(p.Length > 1 && p[1] == "on"); return "keys play " + (TakeKeys.Playing ? "on" : "off"); }
                        if (p.Length >= 2 && p[0] == "list" && int.TryParse(p[1], out row))
                            return string.Join("\n", TakeKeys.Of(row - 1).Select(x => x.Label + ": " + string.Join("  ", x.Keys.Select(k => k.Tick + "=" + string.Join(",", k.V.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)).ToArray())).ToArray())).ToArray());
                        if (p.Length == 4 && p[0] == "add" && int.TryParse(p[1], out row))
                        {
                            string[] pk = p[2].Split('/');
                            string[] rest2 = p[3].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (pk.Length != 2 || rest2.Length < 2 || !int.TryParse(rest2[0], out tick)) return "usage: console keys add ROW PAGE/LABEL TICK V [V V]";
                            Knob k = KnobPanel.FindKnob(pk[0], pk[1].Replace('_', ' '));
                            if (k == null) return "no slider " + pk[1] + " on " + pk[0];
                            var v = rest2.Skip(1).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                            KnobPanel.SetLive(pk[0], k.Label, v);
                            return TakeKeys.Add(row - 1, pk[0].ToUpperInvariant(), k, tick);
                        }
                        return "usage: console keys add ROW PAGE/LABEL TICK V [V V] | list ROW | play on|off";
                    }
                case "camo":                                                               // the Camo Editor's server (2026-10-18), for tests
                    {
                        string a = w.Length > 1 ? w[1].Trim() : "";
                        if (a.StartsWith("dumps", StringComparison.OrdinalIgnoreCase)) { Where.Save("camodumps", a.Length > 5 ? a.Substring(5).Trim() : ""); return "camo dumps " + CamoServer.DumpsFolder; }
                        // 2026-10-18, for tests: what the Camo Editor page needs on this PC, and its one-click fetches
                        if (a == "engine") return "webview2 " + (CamoEditorControl.EngineVersion() ?? "none") + ", c++ runtime " + OatTools.CppRuntimeVersion() + ", gun models " + OatTools.GunModels() + " in " + CamoServer.DumpsFolder + ", unlinker " + (OatTools.Unlinker() ?? "none");
                        if (a == "said") return _camoSaid ?? "-";
                        if (a == "install-webview2" || a == "install-cpp" || a == "make-models")
                        {
                            string game = Where.Game;
                            _camoSaid = a + "...";
                            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                            {
                                string r;
                                try { r = a == "make-models" ? (game == null ? "no game folder" : OatTools.MakeGunModels(game, t => _camoSaid = t)) : a == "install-cpp" ? OatTools.RunCppInstaller() : OatTools.RunWebView2Installer(); }
                                catch (Exception ex) { r = "error " + ex.GetBaseException().Message; }
                                _camoSaid = "done: " + r;
                            });
                            return _camoSaid;
                        }
                        if (a.StartsWith("open", StringComparison.OrdinalIgnoreCase)) return CamoEditorControl.Open(a.Length > 5 ? a.Substring(5).Trim() : null);
                        string why, url = CamoServer.Url(out why);
                        return url ?? ("error " + why);
                    }
                case "view":                                                               // the game's camera view (2026-10-18): CameraView
                    {
                        CameraView.View v;
                        switch (arg)
                        {
                            case "dolly": v = CameraView.View.Dolly; break;
                            case "edit": v = CameraView.View.EditCamera; break;
                            case "free": v = CameraView.View.FreeRoam; break;
                            case "first": v = CameraView.View.FirstPerson; break;
                            case "third": v = CameraView.View.ThirdPerson; break;
                            case "said": return _viewSaid ?? "-";
                            case "":
                            case "status": { CameraView.View? now = CameraView.Now(); return now.HasValue ? CameraView.Name(now.Value) : "not known (no film?)"; }
                            default: return "usage: console view dolly | edit | free | first | third | status | said";
                        }
                        _viewSaid = "switching to " + CameraView.Name(v) + "...";
                        System.Threading.ThreadPool.QueueUserWorkItem(_ => { string r; try { r = CameraView.To(v); } catch (Exception ex) { r = "error " + ex.Message; } _viewSaid = r; });
                        return _viewSaid + " (console view said: the outcome)";
                    }
                case "film": BatchRecorder.BridgeClient.LastDemo = w.Length > 1 ? w[1].Trim() : ""; return "the timeline goes back by loading " + BatchRecorder.BridgeClient.LastDemo;     // tests: a film loaded by hand
                default: return "console layout [classic|workspace|overlay|right|below] | console into | console tool KEY | console panel | console alpha A | console front";
            }
        }
    }
}
