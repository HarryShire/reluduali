using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace ReludualiConsole
{
    /// <summary>Where a view looks from.</summary>
    public enum ViewAngle { Camera, Free, Top, Front, Right, Back, Left, Bottom }

    /// <summary>How a view draws the map: its colours, see-through, its edges only, or not at all.</summary>
    public enum MapLook { Solid, Ghost, Wire, Off }

    /// <summary>How a view draws the players.</summary>
    public enum PlayersLook { On, Ghost, Off }

    /// <summary>
    /// One of the CAM QUEUE's 3D views (2026-10-14). Mycelium: "see the campaths from Bo2 in a rough blender viewport ... its
    /// just looping as soon as two or more markers have been placed in BO2 at a timescale the user sets", then: "allow the
    /// user to make as many as they like, and to pin ones they frequently use. Id personally have the Looping one on Always,
    /// and a Static view of the Overhead Camera Paths, and a Static Side View of Campaths with Wireframe, or Models Turned
    /// off, or rendered low visibility so the cameras are Easily Viewable."
    ///
    /// Each view has its own angle (through the path's camera at 1920x808, free orbit, or a fixed side - top, front, right,
    /// back, left, bottom), its own look for the map and the players, and its own pin. The path, its markers (as small
    /// cameras you can grab) and the camera moving along it are drawn over the 3D, so the map never hides them. All views
    /// share one CamScene: one path, one loop clock, one set of posed players.
    ///
    /// Mouse: the free view orbits (left), pans (right) and zooms (wheel); the fixed sides pan with either button and zoom,
    /// and never turn. A camera marker is grabbed within 14 px of its eye: drag moves it in the plane facing the view,
    /// Alt+drag re-aims it, Ctrl+Z undoes. Keys (click the view first): Blender's numpad - 1 front, 3 right, 7 top (Ctrl:
    /// back, left, bottom), 9 the opposite side, 5 orthographic / perspective, 0 through the camera, 2 4 6 8 orbit, + - zoom,
    /// period or Home frame the path. The top-row digits work too.
    /// </summary>
    public sealed class CamView : UserControl
    {
        public const double LensAspect = 1920.0 / 808.0;

        public CamScene Scene { get; private set; }
        public ViewAngle Angle { get; private set; }
        public MapLook MapMode { get; private set; }
        public PlayersLook PlayersMode { get; private set; }
        public bool Pinned { get { return _pin.IsChecked == true; } set { _pin.IsChecked = value; } }

        /// <summary>The host's buttons: close this view, pop it out (or back), make it the big one, it changed (pin, look).</summary>
        public event Action<CamView> CloseRequested, PopRequested, MainRequested, SettingsChanged;

        private readonly Viewport3D _vp = new Viewport3D { ClipToBounds = true, IsHitTestVisible = false };
        private readonly PerspectiveCamera _persp = new PerspectiveCamera { FieldOfView = 60, NearPlaneDistance = 2, FarPlaneDistance = 80000, UpDirection = new Vector3D(0, 0, 1) };
        private readonly OrthographicCamera _ortho = new OrthographicCamera { Width = 900, NearPlaneDistance = 1, FarPlaneDistance = 200000, UpDirection = new Vector3D(0, 0, 1) };
        private readonly ModelVisual3D _model = new ModelVisual3D();
        private readonly Image _wire = new Image { IsHitTestVisible = false, Stretch = Stretch.Fill };
        private readonly Overlay _overlay = new Overlay();
        private readonly Grid _box = new Grid { ClipToBounds = true };
        private readonly Grid _body = new Grid { ClipToBounds = true, Background = Ui.Brush("ViewBack") };
        private readonly TextBlock _info = new TextBlock { Foreground = new SolidColorBrush(Color.FromArgb(200, 0xDD, 0xD3, 0xEC)), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11,
                                                           Margin = new Thickness(8, 6, 8, 0), IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _empty = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x9D, 0x96, 0xA8)), Margin = new Thickness(14), TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false,
                                                            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, MaxWidth = 420 };
        private readonly ComboBox _angleBox = new ComboBox { Width = 86, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(8, 2, 22, 2), MinHeight = 24, FontSize = 12 };
        private readonly ComboBox _mapBox = new ComboBox { Width = 102, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(8, 2, 22, 2), MinHeight = 24, FontSize = 12 };
        private readonly Button _playersButton;
        private readonly ToggleButton _pin = Ui.IconToggle(Glyph.Pin, "Pin: this view comes back every time the Designer opens");
        private readonly Button _popButton;
        private readonly Border _frame = new Border { CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), SnapsToDevicePixels = true };
        private bool _quiet;

        // the free view's orbit, and the fixed sides' framing
        private Point3D _target;
        private double _yaw = 225, _pitch = 35, _dist = 900, _width = 1200;
        private bool _freeOrtho;

        // the mouse
        private Point _drag;
        private MouseButton _dragButton;
        private bool _dragging, _grabbing, _aiming;
        private readonly List<Point> _markerScreen = new List<Point>();
        private const double PickRadius = 14;

        private static readonly string[] AngleNames = { "Camera", "Free", "Top", "Front", "Right", "Back", "Left", "Bottom" };
        private static readonly string[] MapNames = { "Colour", "See-through", "Wireframe", "No map" };
        private static readonly string[] PlayerNames = { "Players", "Players faint", "No players" };

        public CamView(CamScene scene, ViewAngle angle, MapLook map, PlayersLook players, bool pinned)
        {
            Scene = scene;
            Angle = angle; MapMode = map; PlayersMode = players;
            Focusable = true;
            FocusVisualStyle = null;
            PreviewKeyDown += OnKey;
            _vp.Children.Add(_model);
            _vp.Camera = _persp;

            foreach (string s in AngleNames) _angleBox.Items.Add(s);
            foreach (string s in MapNames) _mapBox.Items.Add(s);
            _quiet = true;
            _angleBox.SelectedIndex = (int)angle; _mapBox.SelectedIndex = (int)map;
            _pin.IsChecked = pinned;
            _quiet = false;
            _angleBox.ToolTip = "Where this view looks from: through the path's camera (the loop), free orbit, or a fixed side (Blender's numpad works too)";
            _mapBox.ToolTip = "How this view draws the map: in colour, see-through, as a wireframe, or not at all - so the cameras stand out";
            _angleBox.SelectionChanged += (s, e) => { if (!_quiet) SetAngle((ViewAngle)_angleBox.SelectedIndex); };
            _mapBox.SelectionChanged += (s, e) => { if (!_quiet) SetMapLook((MapLook)_mapBox.SelectedIndex); };
            _pin.Checked += (s, e) => Changed(); _pin.Unchecked += (s, e) => Changed();
            _playersButton = Ui.IconButton(Glyph.Person, "", () => SetPlayersLook((PlayersLook)(((int)PlayersMode + 1) % 3)));
            PaintPlayersButton();

            // the header: its own buttons first (so a narrow view clips the drop-downs, never the buttons), then what it shows
            var head = new DockPanel { LastChildFill = false, Margin = new Thickness(6, 5, 4, 5) };
            var close = Ui.IconButton(Glyph.Close, "Close this view", () => { var h = CloseRequested; if (h != null) h(this); });
            _popButton = Ui.IconButton(Glyph.PopOut, "Pop out: this view in a window of its own (close that window to put it back)", () => { var h = PopRequested; if (h != null) h(this); });
            foreach (var b in new UIElement[] { close, _popButton, _pin, _playersButton }) { DockPanel.SetDock(b, Dock.Right); head.Children.Add(b); }
            head.Children.Add(_angleBox); head.Children.Add(_mapBox);
            var headBar = new Border { Child = head, Background = Ui.Brush("CardBack"), BorderBrush = Ui.Brush("Line"), BorderThickness = new Thickness(0, 0, 0, 1),
                                       CornerRadius = new CornerRadius(3, 3, 0, 0), ToolTip = null };
            headBar.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) { var h = MainRequested; if (h != null) h(this); e.Handled = true; } };

            _box.Children.Add(_vp);
            _box.Children.Add(_wire);
            _box.Children.Add(_overlay);
            _body.Children.Add(_box);
            _body.Children.Add(_info);
            _body.Children.Add(_empty);
            _body.MouseDown += OnMouseDown;
            _body.MouseMove += OnMouseMove;
            _body.MouseUp += OnMouseUp;
            _body.MouseWheel += (s, e) => { Zoom(e.Delta / 120.0); };
            _body.SizeChanged += (s, e) => Layout();

            var dock = new DockPanel();
            DockPanel.SetDock(headBar, Dock.Top);
            dock.Children.Add(headBar);
            dock.Children.Add(new Border { Child = _body, CornerRadius = new CornerRadius(0, 0, 3, 3), ClipToBounds = true });
            _frame.Child = dock;
            _frame.Background = Ui.Brush("ViewBack");
            Content = _frame;
            GotKeyboardFocus += (s, e) => PaintFrame();
            LostKeyboardFocus += (s, e) => PaintFrame();
            PaintFrame();

            _overlay.Draw = DrawOverlay;
            IsVisibleChanged += (s, e) => { Scene.UpdateHook(); if ((bool)e.NewValue) { _wireDirty = true; Redraw(); } };
            Scene.Attach(this);
            Scene.Changed += OnSceneChanged;
            Scene.Ticked += OnTick;
            Scene.PathMoved += OnPathMoved;
            Compose();
            Frame();
            ShowEmpty();
        }

        /// <summary>The view is gone for good: it no longer follows the scene or poses players.</summary>
        public void Dispose()
        {
            Scene.Changed -= OnSceneChanged;
            Scene.Ticked -= OnTick;
            Scene.PathMoved -= OnPathMoved;
            Scene.Detach(this);
            ReleasePlayers();
        }

        private void PaintPlayersButton()
        {
            var t = (TextBlock)_playersButton.Content;
            t.Opacity = PlayersMode == PlayersLook.On ? 1 : PlayersMode == PlayersLook.Ghost ? 0.55 : 0.22;
            _playersButton.ToolTip = "The players and their guns: " + (PlayersMode == PlayersLook.On ? "shown" : PlayersMode == PlayersLook.Ghost ? "faint (see-through)" : "hidden")
                                     + ". Click: " + (PlayersMode == PlayersLook.On ? "faint" : PlayersMode == PlayersLook.Ghost ? "hidden" : "shown") + ".";
        }

        public void SetPopped(bool popped) { _popButton.ToolTip = popped ? "Put this view back in the Designer" : "Pop out: this view in a window of its own (close that window to put it back)"; }

        private void PaintFrame()
        {
            _frame.BorderBrush = IsKeyboardFocusWithin ? Ui.Brush("Focus") : Ui.Brush("RimSoft");
        }

        private void Changed() { var h = SettingsChanged; if (h != null) h(this); }

        /// <summary>A name for the view's look, for the pipe and the pictures: "Top, Wireframe, Players".</summary>
        public string Describe() { return AngleNames[(int)Angle] + ", " + MapNames[(int)MapMode] + ", " + PlayerNames[(int)PlayersMode] + (Pinned ? ", pinned" : ""); }

        /// <summary>The view's look as words for the settings file: "top wire ghost".</summary>
        public string Save() { return Angle.ToString().ToLowerInvariant() + " " + MapMode.ToString().ToLowerInvariant() + " " + PlayersMode.ToString().ToLowerInvariant(); }

        // ---------------------------------------------------------------- what it shows

        public void SetAngle(ViewAngle a) { SetAngle(a, true); }

        private void SetAngle(ViewAngle a, bool frame)
        {
            Angle = a;
            _quiet = true; _angleBox.SelectedIndex = (int)a; _quiet = false;
            Layout();
            if (frame) Frame();
            Changed();
        }

        public void SetMapLook(MapLook m)
        {
            MapMode = m;
            _quiet = true; _mapBox.SelectedIndex = (int)m; _quiet = false;
            Compose();
            _wireDirty = true;
            Redraw();
            Changed();
        }

        public void SetPlayersLook(PlayersLook p)
        {
            PlayersMode = p;
            PaintPlayersButton();
            Compose();
            Changed();
        }

        /// <summary>A marker is being dragged: only the path over the 3D is drawn again (the map, its wireframe and the players stay).</summary>
        private void OnPathMoved()
        {
            if (!IsVisible) return;
            if (Angle == ViewAngle.Camera) UpdateCamera();
            _overlay.InvalidateVisual();
        }

        private void OnSceneChanged(bool reframe)
        {
            Compose();
            if (reframe) Frame();
            _wireDirty = true;
            ShowEmpty();
            Redraw();
        }

        private void ShowEmpty()
        {
            bool none = Scene.Track == null;
            _empty.Text = none ? (Scene.Why.Length > 0 ? Scene.Why : "Place two or more markers in the theater's Edit Camera: the path loops here.") : Scene.Why;
            _empty.Visibility = _empty.Text.Length > 0 && (none || Scene.MapData == null) ? Visibility.Visible : Visibility.Collapsed;
        }

        private static readonly Model3DGroup Lights = MakeLights();

        private static Model3DGroup MakeLights()
        {
            var g = new Model3DGroup();
            g.Children.Add(new AmbientLight(Color.FromRgb(78, 80, 88)));
            g.Children.Add(new DirectionalLight(Color.FromRgb(205, 200, 190), new Vector3D(-0.45, -0.3, -0.84)));
            g.Children.Add(new DirectionalLight(Color.FromRgb(70, 76, 92), new Vector3D(0.5, 0.45, -0.25)));
            g.Freeze();
            return g;
        }

        private Model3DGroup _players;
        private CamPlayers _playersOf;
        private bool _playersGhost;

        private void ReleasePlayers()
        {
            if (_players != null && _playersOf != null) _playersOf.Release(_players);
            _players = null; _playersOf = null;
        }

        /// <summary>The view's 3D: the lights, what is solid, then what is see-through (so it shows what is behind it).</summary>
        private void Compose()
        {
            var g = new Model3DGroup();
            g.Children.Add(Lights);
            bool wantPlayers = Scene.Players != null && PlayersMode != PlayersLook.Off;
            bool ghostPlayers = PlayersMode == PlayersLook.Ghost;
            if (!wantPlayers || _playersOf != Scene.Players || _playersGhost != ghostPlayers) ReleasePlayers();
            if (wantPlayers && _players == null) { _players = Scene.Players.NewGroup(ghostPlayers); _playersOf = Scene.Players; _playersGhost = ghostPlayers; }
            MapData md = Scene.MapData;
            if (md != null && MapMode == MapLook.Solid) g.Children.Add(md.Solid);
            if (_players != null && !ghostPlayers) g.Children.Add(_players);
            if (_players != null && ghostPlayers) g.Children.Add(_players);
            if (md != null && MapMode == MapLook.Ghost) g.Children.Add(md.Ghost);
            _model.Content = g;
        }

        // ---------------------------------------------------------------- cameras

        private bool IsSide { get { return Angle >= ViewAngle.Top; } }

        /// <summary>The fixed sides' directions (from the target toward the camera), as Blender's numpad.</summary>
        private static void SideDir(ViewAngle a, out double yaw, out double pitch)
        {
            pitch = 0;
            switch (a)
            {
                case ViewAngle.Front: yaw = -90; break;              // from -Y, looking +Y (Blender's front)
                case ViewAngle.Back: yaw = 90; break;
                case ViewAngle.Right: yaw = 0; break;                // from +X, looking -X
                case ViewAngle.Left: yaw = 180; break;
                case ViewAngle.Top: yaw = -90; pitch = 90; break;    // screen up = +Y
                case ViewAngle.Bottom: yaw = -90; pitch = -90; break;
                default: yaw = 225; pitch = 35; break;
            }
        }

        private static Vector3D Dir(double yaw, double pitch)
        {
            double y = yaw * Math.PI / 180, p = pitch * Math.PI / 180;
            return new Vector3D(Math.Cos(p) * Math.Cos(y), Math.Cos(p) * Math.Sin(y), Math.Sin(p));
        }

        /// <summary>Straight down (top) or up (bottom), Z can't be up: the screen's up is the way the view faces, as in Blender.</summary>
        private static Vector3D UpFor(double yaw, double pitch)
        {
            double y = yaw * Math.PI / 180;
            return Math.Abs(pitch) > 89.5 ? new Vector3D(-Math.Cos(y) * Math.Sign(pitch), -Math.Sin(y) * Math.Sign(pitch), 0) : new Vector3D(0, 0, 1);
        }

        private double BoxAspect { get { return Math.Max(0.2, _box.ActualWidth / Math.Max(1, _box.ActualHeight)); } }

        private void UpdateCamera()
        {
            if (Angle == ViewAngle.Camera)
            {
                double[] p = Scene.Pose;
                Vector3D f = Forward(p[3], p[4]);
                _persp.Position = new Point3D(p[0], p[1], p[2]);
                _persp.LookDirection = f;
                _persp.UpDirection = Up(f, p[5]);
                _persp.FieldOfView = HFov(p[6] > 0 ? p[6] : 65, LensAspect);
                if (_vp.Camera != _persp) _vp.Camera = _persp;
            }
            else if (Angle == ViewAngle.Free && !_freeOrtho)
            {
                Vector3D d = Dir(_yaw, _pitch);
                _persp.FieldOfView = 60;
                _persp.Position = _target + d * _dist;
                _persp.LookDirection = -d;
                _persp.UpDirection = UpFor(_yaw, _pitch);
                if (_vp.Camera != _persp) _vp.Camera = _persp;
            }
            else
            {
                double yaw = _yaw, pitch = _pitch;
                if (IsSide) SideDir(Angle, out yaw, out pitch);
                Vector3D d = Dir(yaw, pitch);
                _ortho.Position = _target + d * 40000;
                _ortho.LookDirection = -d;
                _ortho.UpDirection = UpFor(yaw, pitch);
                _ortho.Width = IsSide ? _width : _dist * 1.15;      // the free view's ortho: the width a 60-degree view sees there
                if (_vp.Camera != _ortho) _vp.Camera = _ortho;
            }
            _wireDirty = true;
            Redraw();
        }

        /// <summary>Looks at the whole path: from behind its first camera and above (free), or all of it in frame (a side).</summary>
        public void Frame()
        {
            double r;
            Point3D c = Scene.Centre(out r);
            _target = c;
            if (Scene.Track != null)
            {
                _dist = Math.Max(350, r * 3.2 + 250);
                _yaw = Scene.Track.Rows[0][4] + 180 + 35;
                _pitch = 32;
            }
            if (IsSide)
            {
                double yaw, pitch;
                SideDir(Angle, out yaw, out pitch);
                Vector3D d = Dir(yaw, pitch), u = UpFor(yaw, pitch), right = Vector3D.CrossProduct(-d, u);
                right.Normalize();
                double minR = 0, maxR = 0, minU = 0, maxU = 0;
                bool any = false;
                Action<Point3D> take = p =>
                {
                    double a = Vector3D.DotProduct(p - c, right), b = Vector3D.DotProduct(p - c, u);
                    if (!any) { minR = maxR = a; minU = maxU = b; any = true; }
                    minR = Math.Min(minR, a); maxR = Math.Max(maxR, a); minU = Math.Min(minU, b); maxU = Math.Max(maxU, b);
                };
                foreach (Point3D p in Scene.PathPts) take(p);
                if (Scene.Markers != null) foreach (DollyMarker m in Scene.Markers) take(new Point3D(m.X, m.Y, m.Z));
                if (any)
                {
                    _target = c + right * ((minR + maxR) / 2) + u * ((minU + maxU) / 2);
                    _width = Math.Max(400, Math.Max(maxR - minR, (maxU - minU) * BoxAspect) * 1.35 + 260);
                }
            }
            UpdateCamera();
        }

        public static Vector3D Forward(double pitch, double yaw)
        {
            double p = pitch * Math.PI / 180, y = yaw * Math.PI / 180;
            return new Vector3D(Math.Cos(p) * Math.Cos(y), Math.Cos(p) * Math.Sin(y), -Math.Sin(p));
        }

        /// <summary>The camera's up: the world's up made square to the view, then turned by the roll (+ lifts the view's left side).</summary>
        public static Vector3D Up(Vector3D f, double roll)
        {
            Vector3D right = Vector3D.CrossProduct(f, new Vector3D(0, 0, 1));
            if (right.LengthSquared < 1e-9) right = new Vector3D(0, -1, 0);
            right.Normalize();
            Vector3D up0 = Vector3D.CrossProduct(right, f);
            up0.Normalize();
            double r = roll * Math.PI / 180;
            return up0 * Math.Cos(r) + right * Math.Sin(r);
        }

        /// <summary>The game's FOV (horizontal at 4:3) as the horizontal FOV at this aspect (hor+), as campath.game_to_hfov.</summary>
        public static double HFov(double gameFov, double aspect)
        {
            return 2 * Math.Atan(Math.Tan(gameFov * Math.PI / 360) * aspect / (4.0 / 3.0)) * 180 / Math.PI;
        }

        /// <summary>The 3D box: the whole view, or - through the camera - the 1920x808 frame in its middle.</summary>
        private void Layout()
        {
            double w = _body.ActualWidth, h = _body.ActualHeight;
            if (w < 2 || h < 2) return;
            if (Angle == ViewAngle.Camera)
            {
                double bw = Math.Min(w, h * LensAspect), bh = bw / LensAspect;
                _box.Width = bw; _box.Height = bh;
                _box.HorizontalAlignment = HorizontalAlignment.Center; _box.VerticalAlignment = VerticalAlignment.Center;
            }
            else
            {
                _box.Width = double.NaN; _box.Height = double.NaN;
                _box.HorizontalAlignment = HorizontalAlignment.Stretch; _box.VerticalAlignment = VerticalAlignment.Stretch;
            }
            _wireDirty = true;
            UpdateCamera();
        }

        // ---------------------------------------------------------------- the loop

        private void OnTick()
        {
            if (!IsVisible) return;
            if (Angle == ViewAngle.Camera) { UpdateCamera(); return; }
            Redraw();
        }

        private void Redraw()
        {
            if (_wireDirty && MapMode == MapLook.Wire) DrawWire();
            else if (MapMode != MapLook.Wire && _wire.Source != null) _wire.Source = null;
            _wireDirty = false;
            _overlay.InvalidateVisual();
            double film = Scene.Track != null ? Scene.Track.Seconds : 0;
            _info.Text = Scene.Track == null ? AngleNames[(int)Angle] :
                AngleNames[(int)Angle] + "   " + Scene.FilmTime.ToString("0.00", CultureInfo.InvariantCulture) + " / " + film.ToString("0.00", CultureInfo.InvariantCulture) + " s"
                + (Angle == ViewAngle.Camera ? "   FOV " + Scene.Pose[6].ToString("0.#", CultureInfo.InvariantCulture) + "   roll " + Scene.Pose[5].ToString("0.#", CultureInfo.InvariantCulture) : "")
                + (Scene.Sel >= 0 && Scene.Markers != null && Scene.Sel < Scene.Markers.Count && Angle != ViewAngle.Camera ? "   marker " + (Scene.Sel + 1) + " (drag: move, Alt+drag: aim, Ctrl+Z: undo)" : "")
                + (Scene.BlockedRuns > 0 ? "   ! the camera passes through the map at " + Scene.BlockedRuns + (Scene.BlockedRuns == 1 ? " place (red)" : " places (red)") : "");
        }

        // ---------------------------------------------------------------- projecting

        /// <summary>The view's camera as plain numbers, for projecting thousands of points quickly.</summary>
        private struct Proj
        {
            public Point3D Pos;
            public Vector3D F, R, U;
            public bool Ortho;
            public double Half, Tx, Near, W, H, Aspect;

            public bool Map(double x, double y, double z, out double sx, out double sy, out double depth)
            {
                double dx = x - Pos.X, dy = y - Pos.Y, dz = z - Pos.Z;
                depth = dx * F.X + dy * F.Y + dz * F.Z;
                double a = dx * R.X + dy * R.Y + dz * R.Z, b = dx * U.X + dy * U.Y + dz * U.Z;
                double nx, ny;
                if (Ortho) { nx = a / Half; ny = b / (Half / Aspect); }
                else
                {
                    if (depth < Near) { sx = sy = 0; return false; }
                    nx = a / (depth * Tx); ny = b / (depth * Tx / Aspect);
                }
                sx = (nx + 1) / 2 * W; sy = (1 - ny) / 2 * H;
                return true;
            }
        }

        private Proj MakeProj(double w, double h)
        {
            var cam = (ProjectionCamera)_vp.Camera;
            var p = new Proj { Pos = cam.Position, W = w, H = h, Aspect = w / Math.Max(1, h) };
            p.F = cam.LookDirection; p.F.Normalize();
            p.R = Vector3D.CrossProduct(p.F, cam.UpDirection); p.R.Normalize();
            p.U = Vector3D.CrossProduct(p.R, p.F);
            var o = cam as OrthographicCamera;
            if (o != null) { p.Ortho = true; p.Half = o.Width / 2; p.Near = o.NearPlaneDistance; }
            else { var pc = (PerspectiveCamera)cam; p.Tx = Math.Tan(pc.FieldOfView * Math.PI / 360); p.Near = pc.NearPlaneDistance; }
            return p;
        }

        private bool Project(Point3D w, Size s, out Point screen)
        {
            Proj p = MakeProj(s.Width, s.Height);
            double x, y, d;
            bool ok = p.Map(w.X, w.Y, w.Z, out x, out y, out d);
            screen = new Point(x, y);
            return ok;
        }

        /// <summary>World units per screen pixel at a point (how far a marker moves for a mouse move).</summary>
        private double UnitsPerPixel(Point3D w)
        {
            double width = Math.Max(1, _box.ActualWidth);
            var o = _vp.Camera as OrthographicCamera;
            if (o != null) return o.Width / width;
            var pc = (PerspectiveCamera)_vp.Camera;
            Vector3D f = pc.LookDirection; f.Normalize();
            double z = Math.Max(1, Vector3D.DotProduct(w - pc.Position, f));
            return 2 * z * Math.Tan(pc.FieldOfView * Math.PI / 360) / width;
        }

        private void ViewAxes(out Vector3D right, out Vector3D up)
        {
            var cam = (ProjectionCamera)_vp.Camera;
            Vector3D f = cam.LookDirection; f.Normalize();
            right = Vector3D.CrossProduct(f, cam.UpDirection); right.Normalize();
            up = Vector3D.CrossProduct(right, f);
        }

        // ---------------------------------------------------------------- the wireframe

        private WriteableBitmap _wireBmp;
        private int[] _px;
        private ushort[] _acc;
        private bool _wireDirty = true;
        private static readonly int[] WireLut = MakeWireLut();

        /// <summary>Coverage to colour: a lavender line, brighter where lines pile up, never past the chrome.</summary>
        private static int[] MakeWireLut()
        {
            var lut = new int[2048];
            for (int i = 0; i < lut.Length; i++)
            {
                double a = 0.92 * (1 - Math.Exp(-i / 260.0));
                double r = 0xD8 * a, g = 0xB0 * a, b = 0xFF * a;
                lut[i] = ((int)(a * 255) << 24) | ((int)r << 16) | ((int)g << 8) | (int)b;     // premultiplied BGRA
            }
            return lut;
        }

        /// <summary>
        /// The map's creases and outlines (MapData.Wire), drawn into a bitmap of the view's own pixels. A fixed side shows a
        /// slab of the map around the path - a section cut, fading with the distance from the path - because from the side
        /// every wall of the whole map would pile up into one block; a perspective view fades with the distance from its eye.
        /// </summary>
        private void DrawWire()
        {
            MapData md = Scene.MapData;
            double dpi = 1;
            var src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null) dpi = src.CompositionTarget.TransformToDevice.M11;
            int W = (int)Math.Round(Math.Max(1, _box.ActualWidth) * dpi), H = (int)Math.Round(Math.Max(1, _box.ActualHeight) * dpi);
            if (W < 4 || H < 4) return;
            if (_wireBmp == null || _wireBmp.PixelWidth != W || _wireBmp.PixelHeight != H)
            {
                _wireBmp = new WriteableBitmap(W, H, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32, null);
                _px = new int[W * H];
                _acc = new ushort[W * H];
            }
            if (_wire.Source != _wireBmp) _wire.Source = _wireBmp;
            Array.Clear(_acc, 0, _acc.Length);
            if (md != null && md.Wire != null)
            {
                Proj p = MakeProj(W, H);
                float[] e = md.Wire;
                double r;
                Point3D c = Scene.Centre(out r);
                // the slab's half-depth (fixed sides) or the fade's reach (perspective), in game units
                double slab = Math.Max(700, r * 2.5 + 400), reach = 7000;
                double cDepth = (c.X - p.Pos.X) * p.F.X + (c.Y - p.Pos.Y) * p.F.Y + (c.Z - p.Pos.Z) * p.F.Z;
                for (int i = 0; i + 5 < e.Length; i += 6)
                {
                    double ax, ay, ad, bx, by, bd;
                    bool okA = p.Map(e[i], e[i + 1], e[i + 2], out ax, out ay, out ad);
                    bool okB = p.Map(e[i + 3], e[i + 4], e[i + 5], out bx, out by, out bd);
                    double fade;
                    if (p.Ortho)
                    {
                        double off = Math.Abs((ad + bd) / 2 - cDepth);
                        if (Scene.Track != null && off > slab) continue;
                        fade = Scene.Track != null ? 1 - off / slab : 0.6;
                    }
                    else fade = Math.Min(1, 1 - Math.Max(0, Math.Min(ad, bd)) / reach);
                    if (fade <= 0.04) continue;
                    _strength = (int)(26 + 50 * fade * fade);
                    if (!okA || !okB)
                    {
                        if (p.Ortho || (!okA && !okB)) continue;
                        // one end behind the camera: cut the edge at the near plane
                        double t = (p.Near - ad) / (bd - ad);
                        double cx = e[i] + (e[i + 3] - e[i]) * t, cy = e[i + 1] + (e[i + 4] - e[i + 1]) * t, cz = e[i + 2] + (e[i + 5] - e[i + 2]) * t;
                        double sx, sy, sd;
                        p.Map(cx, cy, cz, out sx, out sy, out sd);
                        if (!okA) { ax = sx; ay = sy; } else { bx = sx; by = sy; }
                    }
                    Line(ax, ay, bx, by, W, H);
                }
            }
            for (int k = 0; k < _acc.Length; k++) _px[k] = WireLut[Math.Min(2047, (int)_acc[k])];
            _wireBmp.WritePixels(new Int32Rect(0, 0, W, H), _px, W * 4, 0);
        }

        /// <summary>A line of soft coverage (two pixels across, split by distance), added to what is there.</summary>
        private void Line(double x0, double y0, double x1, double y1, int W, int H)
        {
            // clip to the bitmap (with a margin) before stepping
            if ((x0 < -2 && x1 < -2) || (y0 < -2 && y1 < -2) || (x0 > W + 2 && x1 > W + 2) || (y0 > H + 2 && y1 > H + 2)) return;
            double dx = x1 - x0, dy = y1 - y0;
            double len = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (len < 0.5) { Plot((int)x0, (int)y0, 60, W, H); return; }
            if (len > 6000) return;                                  // nearly edge-on to a perspective camera: not worth drawing
            int strength = _strength;
            if (Math.Abs(dx) >= Math.Abs(dy))
            {
                if (x0 > x1) { double t = x0; x0 = x1; x1 = t; t = y0; y0 = y1; y1 = t; }
                double g = (y1 - y0) / Math.Max(1e-9, x1 - x0);
                int xs = Math.Max(0, (int)Math.Ceiling(x0)), xe = Math.Min(W - 1, (int)Math.Floor(x1));
                double y = y0 + g * (xs - x0);
                for (int x = xs; x <= xe; x++, y += g)
                {
                    int yi = (int)Math.Floor(y);
                    double f = y - yi;
                    Plot(x, yi, (int)(strength * (1 - f)), W, H);
                    Plot(x, yi + 1, (int)(strength * f), W, H);
                }
            }
            else
            {
                if (y0 > y1) { double t = x0; x0 = x1; x1 = t; t = y0; y0 = y1; y1 = t; }
                double g = (x1 - x0) / Math.Max(1e-9, y1 - y0);
                int ys = Math.Max(0, (int)Math.Ceiling(y0)), ye = Math.Min(H - 1, (int)Math.Floor(y1));
                double x = x0 + g * (ys - y0);
                for (int y = ys; y <= ye; y++, x += g)
                {
                    int xi = (int)Math.Floor(x);
                    double f = x - xi;
                    Plot(xi, y, (int)(strength * (1 - f)), W, H);
                    Plot(xi + 1, y, (int)(strength * f), W, H);
                }
            }
        }

        private int _strength = 60;

        private void Plot(int x, int y, int v, int W, int H)
        {
            if (x < 0 || y < 0 || x >= W || y >= H || v <= 0) return;
            int k = y * W + x;
            int s = _acc[k] + v;
            _acc[k] = (ushort)(s > 2047 ? 2047 : s);
        }

        // ---------------------------------------------------------------- the drawing over the 3D

        private static readonly Pen PathPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(240, 138, 36)), 2));
        private static readonly Pen PathGlow = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(70, 240, 138, 36)), 6));
        // where the path passes through the map (2026-10-16): red, thicker, over the orange
        private static readonly Pen BlockedPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(255, 64, 88)), 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        private static readonly Pen CamPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(120, 220, 255)), 1.5));
        private static readonly Pen MarkerPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(255, 214, 90)), 1.5));
        private static readonly Pen SelPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(255, 120, 40)), 2.5));
        private static readonly Pen RingPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1));
        private static readonly Pen ThirdsPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1));
        private static readonly Brush MarkerBrush = FrozenBrush(Color.FromRgb(255, 214, 90));
        private static readonly Brush SelBrush = FrozenBrush(Color.FromRgb(255, 120, 40));
        private static readonly Brush CamBrush = FrozenBrush(Color.FromRgb(120, 220, 255));
        private static readonly Brush LabelBack = FrozenBrush(Color.FromArgb(150, 16, 12, 18));
        private static readonly Typeface Face = new Typeface("Segoe UI");

        private static Pen Frozen(Pen p) { p.Freeze(); return p; }
        private static Brush FrozenBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        /// <summary>A camera as a small pyramid from its eye along its view, `pixels` long on screen whatever the zoom.</summary>
        private bool DrawCamera(DrawingContext dc, Size s, Point3D c, double pitch, double yaw, double roll, double fov, double pixels, Pen pen, Brush dot, out Point eye)
        {
            if (!Project(c, s, out eye)) return false;
            Vector3D f = Forward(pitch, yaw);
            Vector3D up = Up(f, roll);
            Vector3D right = Vector3D.CrossProduct(f, up); right.Normalize();
            double hx = Math.Tan(HFov(fov > 0 ? fov : 65, LensAspect) * Math.PI / 360), hy = hx / LensAspect;
            double L = pixels * UnitsPerPixel(c);
            var corners = new[] { c + (f + right * hx + up * hy) * L, c + (f - right * hx + up * hy) * L, c + (f - right * hx - up * hy) * L, c + (f + right * hx - up * hy) * L };
            var pts = new Point[4];
            bool all = true;
            for (int k = 0; k < 4; k++) all &= Project(corners[k], s, out pts[k]);
            if (all)
            {
                for (int k = 0; k < 4; k++) { dc.DrawLine(pen, eye, pts[k]); dc.DrawLine(pen, pts[k], pts[(k + 1) % 4]); }
                // a short tick off the top edge, as Blender marks a camera's up
                Point mid = new Point((pts[0].X + pts[1].X) / 2, (pts[0].Y + pts[1].Y) / 2);
                Vector upv = mid - new Point((pts[2].X + pts[3].X) / 2, (pts[2].Y + pts[3].Y) / 2);
                if (upv.Length > 1) { upv.Normalize(); dc.DrawLine(pen, mid, mid + upv * 5); }
            }
            dc.DrawEllipse(dot, null, eye, 3.5, 3.5);
            return true;
        }

        private void DrawOverlay(DrawingContext dc, Size s)
        {
            if (s.Width < 4) return;
            if (Angle == ViewAngle.Camera)
            {
                // the frame's thirds
                for (int k = 1; k <= 2; k++)
                {
                    dc.DrawLine(ThirdsPen, new Point(s.Width * k / 3, 0), new Point(s.Width * k / 3, s.Height));
                    dc.DrawLine(ThirdsPen, new Point(0, s.Height * k / 3), new Point(s.Width, s.Height * k / 3));
                }
                _markerScreen.Clear();
                return;
            }
            if (Scene.Track == null) { _markerScreen.Clear(); return; }
            var geo = new StreamGeometry();
            Proj p = MakeProj(s.Width, s.Height);
            using (StreamGeometryContext g = geo.Open())
            {
                bool open = false;
                foreach (Point3D w in Scene.PathPts)
                {
                    double x, y, d;
                    if (!p.Map(w.X, w.Y, w.Z, out x, out y, out d)) { open = false; continue; }
                    if (!open) { g.BeginFigure(new Point(x, y), false, false); open = true; }
                    else g.LineTo(new Point(x, y), true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, PathGlow, geo);
            dc.DrawGeometry(null, PathPen, geo);
            bool[] bl = Scene.Blocked;
            if (bl != null && Scene.BlockedRuns > 0)
                for (int i = 0; i < bl.Length && i + 1 < Scene.PathPts.Count; i++)
                {
                    if (!bl[i]) continue;
                    double x0, y0, d0, x1, y1, d1;
                    Point3D a = Scene.PathPts[i], b = Scene.PathPts[i + 1];
                    if (p.Map(a.X, a.Y, a.Z, out x0, out y0, out d0) && p.Map(b.X, b.Y, b.Z, out x1, out y1, out d1))
                        dc.DrawLine(BlockedPen, new Point(x0, y0), new Point(x1, y1));
                }
            _markerScreen.Clear();
            List<DollyMarker> ms = Scene.Markers;
            if (ms != null)
                for (int i = 0; i < ms.Count; i++)
                {
                    DollyMarker m = ms[i];
                    bool sel = i == Scene.Sel;
                    Point eye;
                    if (!DrawCamera(dc, s, new Point3D(m.X, m.Y, m.Z), m.Pitch, m.Yaw, m.Roll, m.Fov, sel ? 30 : 22, sel ? SelPen : MarkerPen, sel ? SelBrush : MarkerBrush, out eye))
                    { _markerScreen.Add(new Point(double.NaN, double.NaN)); continue; }
                    _markerScreen.Add(eye);
                    if (sel) dc.DrawEllipse(null, RingPen, eye, PickRadius, PickRadius);
                    var ft = new FormattedText((i + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11, sel ? SelBrush : MarkerBrush, 1.0);
                    dc.DrawRoundedRectangle(LabelBack, null, new Rect(eye.X + 5, eye.Y - 17, ft.Width + 6, ft.Height + 1), 3, 3);
                    dc.DrawText(ft, new Point(eye.X + 8, eye.Y - 17));
                }
            // the camera now, along the path
            double[] q = Scene.Pose;
            Point pc;
            DrawCamera(dc, s, new Point3D(q[0], q[1], q[2]), q[3], q[4], q[5], q[6], 34, CamPen, CamBrush, out pc);
        }

        /// <summary>A layer that draws whatever its callback draws, over the 3D, letting the mouse through.</summary>
        private sealed class Overlay : FrameworkElement
        {
            public Action<DrawingContext, Size> Draw;
            public Overlay() { IsHitTestVisible = false; }
            protected override void OnRender(DrawingContext dc) { var d = Draw; if (d != null) d(dc, RenderSize); }
        }

        // ---------------------------------------------------------------- the mouse: orbit, pan, zoom, grab a camera

        private int PickMarker(Point at)
        {
            int best = -1;
            double bestD = PickRadius;
            for (int i = 0; i < _markerScreen.Count; i++)
            {
                if (double.IsNaN(_markerScreen[i].X)) continue;
                double d = (_markerScreen[i] - at).Length;
                if (d <= bestD) { bestD = d; best = i; }
            }
            return best;
        }

        private Point _downAt;

        /// <summary>
        /// Mycelium's round 1 (2026-10-15, 11:02): right-click "add camera" between two markers. A right click that does not pan
        /// opens a menu: a camera where the click is on the path, or halfway between any two markers.
        /// </summary>
        private void ShowAddMenu(Point at)
        {
            List<DollyMarker> ms = Scene.Markers;
            if (ms == null || ms.Count < 2 || Scene.Track == null) return;
            var menu = new ContextMenu { PlacementTarget = this };
            Size s = _overlay.RenderSize;
            Proj p = MakeProj(s.Width, s.Height);
            double best = 18, here = double.NaN;
            List<double> ticks = Scene.PathTicks;
            for (int i = 0; i < Scene.PathPts.Count && ticks != null && i < ticks.Count; i++)
            {
                double x, y, d;
                Point3D w = Scene.PathPts[i];
                if (!p.Map(w.X, w.Y, w.Z, out x, out y, out d)) continue;
                double dist = (new Point(x, y) - at).Length;
                if (dist < best) { best = dist; here = ticks[i]; }
            }
            if (!double.IsNaN(here))
            {
                double t = here;
                var item = new MenuItem { Header = "Add a camera here" };
                item.Click += (s2, e2) => Scene.InsertAt(t);
                menu.Items.Add(item);
                menu.Items.Add(new Separator());
            }
            for (int i = 0; i + 1 < ms.Count; i++)
            {
                double mid = (ms[i].Tick + ms[i + 1].Tick) / 2.0;
                var item = new MenuItem { Header = "Add a camera between " + (i + 1) + " and " + (i + 2) };
                item.Click += (s2, e2) => Scene.InsertAt(mid);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            Focus();
            Point at = e.GetPosition(_box);
            _downAt = at;
            if (e.ChangedButton == MouseButton.Left && Scene.Markers != null && Angle != ViewAngle.Camera)
            {
                int i = PickMarker(at);
                if (i >= 0)
                {
                    Scene.BeginEdit(i);
                    _grabbing = true;
                    _aiming = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
                    _drag = at;
                    _body.CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }
            if (e.ClickCount == 2) { Frame(); return; }
            _drag = at;
            _dragButton = e.ChangedButton;
            _dragging = true;
            _body.CaptureMouse();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            Point p = e.GetPosition(_box);
            if (_grabbing)
            {
                Vector dm = p - _drag;
                _drag = p;
                if (dm.Length > 0) Grab(dm);
                return;
            }
            if (!_dragging || Angle == ViewAngle.Camera) return;
            Vector d = p - _drag;
            _drag = p;
            if (_dragButton == MouseButton.Left && Angle == ViewAngle.Free)
            {
                _yaw -= d.X * 0.4;
                _pitch = Math.Max(-90, Math.Min(90, _pitch + d.Y * 0.4));
            }
            else
            {
                // pan: the free view with the right button, a fixed side with either (it never turns)
                Vector3D right, up;
                ViewAxes(out right, out up);
                double k = _vp.Camera is OrthographicCamera ? ((OrthographicCamera)_vp.Camera).Width / Math.Max(1, _box.ActualWidth) : _dist * 0.0015;
                _target = _target - right * d.X * k + up * d.Y * k;
            }
            UpdateCamera();
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_grabbing)
            {
                _grabbing = false;
                _body.ReleaseMouseCapture();
                Scene.EndEdit();
                _aiming = false;
                return;
            }
            _dragging = false;
            _body.ReleaseMouseCapture();
            if (e.ChangedButton == MouseButton.Right && Angle != ViewAngle.Camera && (e.GetPosition(_box) - _downAt).Length < 4) ShowAddMenu(e.GetPosition(_box));
        }

        /// <summary>A grabbed camera follows the mouse: in the plane facing the view, or (Alt) turning.</summary>
        private void Grab(Vector d)
        {
            if (Scene.Markers == null || Scene.Sel < 0 || Scene.Sel >= Scene.Markers.Count) return;
            if (_aiming) { Scene.AimBy(-d.X * 0.3, d.Y * 0.3); return; }
            DollyMarker m = Scene.Markers[Scene.Sel];
            Vector3D right, up;
            ViewAxes(out right, out up);
            double k = UnitsPerPixel(new Point3D(m.X, m.Y, m.Z));
            Scene.MoveBy(right * d.X * k - up * d.Y * k);
        }

        private void Zoom(double notches)
        {
            if (Angle == ViewAngle.Camera) return;
            double f = Math.Pow(0.88, notches);
            if (IsSide) _width = Math.Max(60, Math.Min(120000, _width * f));
            else _dist = Math.Max(40, Math.Min(40000, _dist * f));
            UpdateCamera();
        }

        private void Orbit(double dYaw, double dPitch)
        {
            if (Angle != ViewAngle.Free)
            {
                // Blender: turning from a numpad view goes to a free one, from where that view was
                double y, p;
                SideDir(Angle, out y, out p);
                if (IsSide) { _yaw = y; _pitch = p; _dist = _width / 1.15; _freeOrtho = true; }
                SetAngle(ViewAngle.Free, false);
            }
            _yaw += dYaw;
            _pitch = Math.Max(-90, Math.Min(90, _pitch + dPitch));
            UpdateCamera();
        }

        /// <summary>Blender's numpad, on this view.</summary>
        private void OnKey(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            switch (k)
            {
                case Key.NumPad1: case Key.D1: SetView(ctrl ? "back" : "front"); break;
                case Key.NumPad3: case Key.D3: SetView(ctrl ? "left" : "right"); break;
                case Key.NumPad7: case Key.D7: SetView(ctrl ? "bottom" : "top"); break;
                case Key.NumPad9: case Key.D9: SetView("opposite"); break;
                case Key.NumPad5: case Key.D5: SetView("toggle"); break;
                case Key.NumPad0: case Key.D0: SetView("camera"); break;
                case Key.NumPad4: case Key.D4: Orbit(15, 0); break;
                case Key.NumPad6: case Key.D6: Orbit(-15, 0); break;
                case Key.NumPad8: case Key.D8: Orbit(0, 15); break;
                case Key.NumPad2: case Key.D2: Orbit(0, -15); break;
                case Key.Add: case Key.OemPlus: Zoom(1); break;
                case Key.Subtract: case Key.OemMinus: Zoom(-1); break;
                case Key.Decimal: case Key.OemPeriod: case Key.Home: Frame(); break;
                case Key.Z: if (ctrl) Scene.Undo(); else return; break;
                case Key.Escape: Scene.Select(-1); break;
                default: return;
            }
            e.Handled = true;
        }

        /// <summary>A named view: front, back, right, left, top, bottom, opposite, ortho, persp, toggle, camera, free.</summary>
        public void SetView(string v)
        {
            switch ((v ?? "").ToLowerInvariant())
            {
                case "front": SetAngle(ViewAngle.Front); break;
                case "back": SetAngle(ViewAngle.Back); break;
                case "right": SetAngle(ViewAngle.Right); break;
                case "left": SetAngle(ViewAngle.Left); break;
                case "top": SetAngle(ViewAngle.Top); break;
                case "bottom": SetAngle(ViewAngle.Bottom); break;
                case "camera": SetAngle(Angle == ViewAngle.Camera ? ViewAngle.Free : ViewAngle.Camera); break;
                case "free": SetAngle(ViewAngle.Free); break;
                case "opposite":
                    {
                        var opp = new Dictionary<ViewAngle, ViewAngle> { { ViewAngle.Front, ViewAngle.Back }, { ViewAngle.Back, ViewAngle.Front }, { ViewAngle.Right, ViewAngle.Left },
                                                                         { ViewAngle.Left, ViewAngle.Right }, { ViewAngle.Top, ViewAngle.Bottom }, { ViewAngle.Bottom, ViewAngle.Top } };
                        if (opp.ContainsKey(Angle)) SetAngle(opp[Angle]);
                        else if (Angle == ViewAngle.Free) { _yaw += 180; UpdateCamera(); }
                        break;
                    }
                case "ortho": case "persp": case "toggle":
                    {
                        if (Angle != ViewAngle.Free) Orbit(0, 0);
                        _freeOrtho = v == "ortho" || (v == "toggle" && !_freeOrtho);
                        UpdateCamera();
                        break;
                    }
            }
        }

        // ---------------------------------------------------------------- for the pipe's tests

        /// <summary>Drag marker i by (dx, dy) pixels as the mouse would (aim: Alt+drag), then release.</summary>
        public string DragForTest(int i, double dx, double dy, bool aim)
        {
            if (Scene.Markers == null || i < 0 || i >= Scene.Markers.Count) return "no marker " + (i + 1);
            if (Angle == ViewAngle.Camera) return "this view looks through the camera: pick a free view or a side to drag markers";
            Scene.BeginEdit(i);
            _aiming = aim;
            Grab(new Vector(dx, dy));
            _aiming = false;
            Scene.EndEdit();
            DollyMarker m = Scene.Markers[i];
            return "marker " + (i + 1) + " now " + m.X.ToString("0.0", CultureInfo.InvariantCulture) + " " + m.Y.ToString("0.0", CultureInfo.InvariantCulture) + " "
                   + m.Z.ToString("0.0", CultureInfo.InvariantCulture) + ", pitch " + m.Pitch.ToString("0.0", CultureInfo.InvariantCulture) + " yaw " + m.Yaw.ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>What is under a point of the view (a marker or nothing).</summary>
        public string PickForTest(double x, double y)
        {
            int i = PickMarker(new Point(x, y));
            return i < 0 ? "nothing within " + PickRadius + " px" : "marker " + (i + 1) + " (drawn at " + _markerScreen[i].X.ToString("0") + "," + _markerScreen[i].Y.ToString("0") + ")";
        }

        /// <summary>Where each marker is drawn now, in the view's pixels.</summary>
        public string MarkerScreens()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _markerScreen.Count; i++)
                sb.Append(i + 1).Append(": ").Append(double.IsNaN(_markerScreen[i].X) ? "behind" : _markerScreen[i].X.ToString("0") + "," + _markerScreen[i].Y.ToString("0")).Append("  ");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- the maps on disk (shared with the self-tests)

        public static IEnumerable<string> MeshFolders()
        {
            yield return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "maps");
            yield return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Reluduali", "maps");
            string env = Environment.GetEnvironmentVariable("RELUDUALI_MAPS");
            if (!string.IsNullOrEmpty(env)) yield return env;
        }

        public static string MeshFile(string map)
        {
            if (string.IsNullOrEmpty(map)) return null;
            foreach (string d in MeshFolders())
            {
                string p = System.IO.Path.Combine(d, map + ".rvmesh.gz");
                if (System.IO.File.Exists(p)) return p;
            }
            return null;
        }

        /// <summary>A map in its colours (the self-test's RenderTest).</summary>
        public static Model3D LoadMesh(string path) { return MapData.Load(path).Solid; }

        public static string LastMapLoad { get { return CamScene.LastMapLoad; } }
    }
}
