using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace ReludualiConsole
{
    /// <summary>
    /// What the CAM QUEUE's 3D views all show (2026-10-14): one path, its markers, the map, the players, and the loop's
    /// clock. Mycelium: "allow the user to make as many [views] as they like" - so the views share this: the path is sampled
    /// and the players are posed once a frame however many views there are, and a marker moved in any view moves in all.
    /// Everything here only draws; it never talks to the game.
    /// </summary>
    public sealed class CamScene
    {
        public CamTrack Track { get; private set; }
        public List<Point3D> PathPts { get; private set; }
        /// <summary>The markers as the views edit them (a copy: the caller's list is never changed).</summary>
        public List<DollyMarker> Markers { get; private set; }
        public int Sel = -1;
        public string Map { get; private set; }
        public string Label { get; private set; }
        /// <summary>Why nothing (or no map) shows, for the views' empty text.</summary>
        public string Why { get; private set; }
        public MapData MapData { get; private set; }
        public CamPlayers Players { get; private set; }
        /// <summary>The camera now: x y z pitch yaw roll fov.</summary>
        public readonly double[] Pose = new double[7];
        /// <summary>Seconds into the path now.</summary>
        public double FilmTime { get; private set; }
        public double Fps { get; private set; }

        /// <summary>The path changed (true: a new one - views frame it); the markers moved; the map or players arrived.</summary>
        public event Action<bool> Changed;
        /// <summary>The loop moved on a frame: the camera and the players are where they are now.</summary>
        public event Action Ticked;
        /// <summary>The markers after an edit in a view (on release, and on undo).</summary>
        public event Action<List<DollyMarker>> MarkersEdited;
        public event Action SpeedChanged;
        /// <summary>Only the path and its markers moved (a drag in progress): views redraw the path over their 3D, nothing else.</summary>
        public event Action PathMoved;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _loopStart, _speed = 1, _fpsAt;
        private int _frames;
        private readonly List<CamView> _views = new List<CamView>();
        private readonly Stack<List<DollyMarker>> _undo = new Stack<List<DollyMarker>>();
        private bool _moved;

        public CamScene()
        {
            PathPts = new List<Point3D>();
            PathTicks = new List<double>();
            Label = ""; Why = "";
            // a map's shape made while the views wait for it (Setup's button, or by itself): shown at once (the code review's Q-6)
            Dispatcher ui = Dispatcher.CurrentDispatcher;
            MapShapes.Made += map => ui.BeginInvoke(new Action(() =>
            {
                if (!string.Equals(map, Map, StringComparison.OrdinalIgnoreCase) || MapData != null) return;
                Map = null; SetMap(map); Fire(false);
            }));
        }

        private bool _hooked;

        /// <summary>
        /// The frame loop runs only while a view is on the screen: WPF draws every frame for as long as anything listens to
        /// CompositionTarget.Rendering, so a loop left hooked kept the whole Console drawing 60 times a second on any page
        /// (2026-10-14: the PC makes music too).
        /// </summary>
        internal void UpdateHook()
        {
            bool want = _views.Any(v => v.IsVisible);
            if (want == _hooked) return;
            _hooked = want;
            if (want) CompositionTarget.Rendering += OnFrame; else CompositionTarget.Rendering -= OnFrame;
        }

        /// <summary>Film seconds per real second.</summary>
        public double Speed
        {
            get { return _speed; }
            set
            {
                double v = Math.Max(0.05, Math.Min(2, value));
                if (Math.Abs(v - _speed) < 1e-9) return;
                // keep the camera where it is: the loop restarts from the same film time at the new speed
                double now = _clock.Elapsed.TotalSeconds;
                _loopStart = now - FilmTime / v;
                _speed = v;
                var h = SpeedChanged; if (h != null) h();
            }
        }

        internal void Attach(CamView v) { if (!_views.Contains(v)) _views.Add(v); UpdateHook(); }
        internal void Detach(CamView v) { _views.Remove(v); UpdateHook(); }

        // ---------------------------------------------------------------- what to show

        /// <summary>Shows a path, looping. Markers (may be null) become the cameras the views can grab.</summary>
        public void ShowPath(CamTrack track, IList<DollyMarker> markers, string map, string label, bool reframe)
        {
            bool first = Track == null;
            Track = track;
            Label = label ?? "";
            int keepSel = Markers != null && markers != null && Markers.Count == markers.Count ? Sel : -1;
            Markers = markers != null && markers.Count > 0 ? CloneAll(markers) : null;
            Sel = keepSel;
            if (reframe) _undo.Clear();
            BuildPath();
            Why = "";                                  // before SetMap: its "No 3D map ... Setup" hint must show (Q-7)
            SetMap(map);
            CheckBlocked();
            _loopStart = _clock.Elapsed.TotalSeconds;
            Fire(first || reframe);
        }

        public void Clear(string why)
        {
            Track = null;
            PathPts = new List<Point3D>();
            PathTicks = new List<double>();
            Blocked = null; BlockedRuns = 0;
            Markers = null;
            Sel = -1;
            Label = "";
            Why = why ?? "";
            Fire(false);
        }

        private void BuildPath()
        {
            var pts = new List<Point3D>();
            var ticks = new List<double>();
            if (Track != null)
            {
                int stride = Math.Max(1, Track.Ticks.Length / 500);
                for (int i = 0; i < Track.Ticks.Length; i += stride) { pts.Add(new Point3D(Track.Rows[i][0], Track.Rows[i][1], Track.Rows[i][2])); ticks.Add(Track.Ticks[i]); }
                double[] last = Track.Rows[Track.Rows.Length - 1];
                pts.Add(new Point3D(last[0], last[1], last[2]));
                ticks.Add(Track.Ticks[Track.Ticks.Length - 1]);
                Track.Sample(Track.First + Math.Min(FilmTime, Track.Seconds) * 1000.0, Pose);
            }
            PathPts = pts;
            PathTicks = ticks;
            Blocked = null;
        }

        /// <summary>Per path segment (PathPts[i] to [i+1]): true where it passes through the map. Null until checked.</summary>
        public bool[] Blocked { get; private set; }
        /// <summary>How many separate stretches of the path pass through the map.</summary>
        public int BlockedRuns { get; private set; }

        /// <summary>The pass-through check (not while a marker is dragged: on release, a new path, and a map that loaded).</summary>
        private void CheckBlocked()
        {
            Blocked = null; BlockedRuns = 0;
            MapData md = MapData;
            if (md == null || PathPts.Count < 2) return;
            var bl = new bool[PathPts.Count - 1];
            int runs = 0;
            try
            {
                for (int i = 0; i + 1 < PathPts.Count; i++)
                {
                    bl[i] = md.SegmentHits(PathPts[i], PathPts[i + 1]);
                    if (bl[i] && (i == 0 || !bl[i - 1])) runs++;
                }
            }
            catch (Exception ex) { App.Note("the pass-through check", ex); return; }
            Blocked = bl; BlockedRuns = runs;
        }

        private void Fire(bool reframe) { var h = Changed; if (h != null) h(reframe); }

        /// <summary>The players and their guns through the path's window (null: none). Posed from the loop's film tick.</summary>
        public void SetPlayers(CamPlayers p)
        {
            if (Players != null && Players != p) Players.ReleaseAll();
            Players = p;
            if (p != null) { p.Build(); p.Pose(Track != null ? Track.First + FilmTime * 1000.0 : p.FirstTick); }
            Fire(false);
        }

        public string PlayersStatus
        {
            get { return Players == null ? "no players" : Players.Bodies + " body parts + " + Players.Guns + " guns, " + Players.FrameCount + " poses every " + Players.StepMs + " ms, " + Players.Shown + " shown now"; }
        }

        /// <summary>The middle of the path and how far it reaches from there (for framing it).</summary>
        public Point3D Centre(out double radius)
        {
            radius = 0;
            if (PathPts.Count == 0) return new Point3D();
            double x = 0, y = 0, z = 0;
            foreach (Point3D p in PathPts) { x += p.X; y += p.Y; z += p.Z; }
            var c = new Point3D(x / PathPts.Count, y / PathPts.Count, z / PathPts.Count);
            foreach (Point3D p in PathPts) radius = Math.Max(radius, (p - c).Length);
            if (Markers != null) foreach (DollyMarker m in Markers) radius = Math.Max(radius, (new Point3D(m.X, m.Y, m.Z) - c).Length);
            return c;
        }

        // ---------------------------------------------------------------- the loop

        public string Status
        {
            get
            {
                return (Track == null ? "no path" : Label + ", " + Track.Ticks.Length + " samples, " + Track.Seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s")
                       + "; map " + (Map ?? "none") + (MapData != null ? " (loaded" + (MapData.WireEdges > 0 ? ", " + MapData.WireEdges + " wire edges" : "") + ")" : "")
                       + "; " + Fps.ToString("0.0", CultureInfo.InvariantCulture) + " fps; speed " + Speed.ToString("0.##", CultureInfo.InvariantCulture) + "x"
                       + (Sel >= 0 ? "; marker " + (Sel + 1) + " selected" : "") + "; " + PlayersStatus + "; " + _views.Count + " view(s)"
                       + (Blocked == null ? "" : BlockedRuns > 0 ? "; passes through the map at " + BlockedRuns + " place(s)" : "; clear of the map")
                       + (BatchRecorder.BatchEngine.Recording ? "; paused while the batch records" : "");
            }
        }

        private void OnFrame(object sender, EventArgs e)
        {
            if (Track == null || BatchRecorder.BatchEngine.Recording) return;      // a night's recording gets every cycle; the views wait
            if (App.Tour) return;                                                  // the pictures put the loop where they want it (Seek)
            if (!_views.Any(v => v.IsVisible)) return;
            double now = _clock.Elapsed.TotalSeconds;
            _frames++;
            if (now - _fpsAt >= 1) { Fps = _frames / (now - _fpsAt); _frames = 0; _fpsAt = now; }
            double film = Track.Seconds;
            double period = film / Math.Max(0.01, Speed) + 0.5;          // plus half a second's rest at the end
            double into = (now - _loopStart) % period;
            Advance(Math.Min(film, into * Speed));
        }

        /// <summary>Puts the loop at a film time (seconds into the path) - the frame loop, and the pictures' --gui-tour.</summary>
        public void Advance(double seconds)
        {
            if (Track == null) return;
            FilmTime = Math.Max(0, Math.Min(Track.Seconds, seconds));
            Track.Sample(Track.First + FilmTime * 1000.0, Pose);
            if (Players != null) Players.Pose(Track.First + FilmTime * 1000.0);
            var h = Ticked; if (h != null) h();
        }

        /// <summary>For pictures: the loop held at a film time from now on (the clock restarts there).</summary>
        public void Seek(double seconds)
        {
            Advance(seconds);
            _loopStart = _clock.Elapsed.TotalSeconds - FilmTime / Math.Max(0.01, Speed);
        }

        // ---------------------------------------------------------------- editing the markers (from any view)

        private static DollyMarker Clone(DollyMarker m)
        {
            return new DollyMarker { Tick = m.Tick, X = m.X, Y = m.Y, Z = m.Z, LX = m.LX, LY = m.LY, LZ = m.LZ, Pitch = m.Pitch, Yaw = m.Yaw, Roll = m.Roll, Fov = m.Fov, Raw = m.Raw };
        }

        public static List<DollyMarker> CloneAll(IList<DollyMarker> ms) { var l = new List<DollyMarker>(); foreach (DollyMarker m in ms) l.Add(Clone(m)); return l; }

        public void Select(int i) { Sel = i; Fire(false); }

        /// <summary>A grab starts: what to go back to with Ctrl+Z.</summary>
        public void BeginEdit(int i)
        {
            if (Markers == null || i < 0 || i >= Markers.Count) return;
            Sel = i;
            _undo.Push(CloneAll(Markers));
            _moved = false;
            Fire(false);
        }

        /// <summary>The selected marker moved by a world offset (its look-at with it).</summary>
        public void MoveBy(Vector3D w)
        {
            if (Markers == null || Sel < 0 || Sel >= Markers.Count) return;
            DollyMarker m = Markers[Sel];
            m.X += w.X; m.Y += w.Y; m.Z += w.Z;
            m.LX += w.X; m.LY += w.Y; m.LZ += w.Z;
            _moved = true;
            Rebuild(false);
        }

        /// <summary>The selected marker turned (Alt+drag): yaw and pitch in degrees.</summary>
        public void AimBy(double dYaw, double dPitch)
        {
            if (Markers == null || Sel < 0 || Sel >= Markers.Count) return;
            DollyMarker m = Markers[Sel];
            m.Yaw += dYaw;
            m.Pitch = Math.Max(-89, Math.Min(89, m.Pitch + dPitch));
            Vector3D f = CamView.Forward(m.Pitch, m.Yaw);
            m.LX = m.X + f.X * 10; m.LY = m.Y + f.Y * 10; m.LZ = m.Z + f.Z * 10;
            _moved = true;
            Rebuild(false);
        }

        /// <summary>A grab ends: the path is built again at full detail, and the edit is handed to the host (a click that
        /// only selected is not one).</summary>
        public void EndEdit()
        {
            if (_moved) { Rebuild(true); var h = MarkersEdited; if (h != null && Markers != null) h(CloneAll(Markers)); }
            else if (_undo.Count > 0) _undo.Pop();
            _moved = false;
        }

        public void Undo()
        {
            if (_undo.Count == 0 || Markers == null) return;
            Markers = _undo.Pop();
            Rebuild(true);
            var h = MarkersEdited;
            if (h != null) h(CloneAll(Markers));
        }

        /// <summary>
        /// Mycelium's round 1 (2026-10-15, 11:02): right-click "add camera" between two markers. A new camera goes in where the
        /// path is at that film time (its place, aim, roll and FOV as the path has them there), so the path through it is
        /// the same until it is moved; handed to the host like a drag (saved, Ctrl+Z undoes).
        /// </summary>
        public bool InsertAt(double tick)
        {
            if (Markers == null || Markers.Count < 2 || Track == null) return false;
            int after = -1;
            for (int i = 0; i + 1 < Markers.Count; i++) if (tick > Markers[i].Tick && tick < Markers[i + 1].Tick) { after = i; break; }
            if (after < 0) return false;
            _undo.Push(CloneAll(Markers));
            var q = new double[7];
            Track.Sample(tick, q);
            var m = new DollyMarker { Tick = (int)Math.Round(tick), X = q[0], Y = q[1], Z = q[2], Pitch = q[3], Yaw = q[4], Roll = q[5], Fov = q[6] };
            Vector3D f = CamView.Forward(m.Pitch, m.Yaw);
            m.LX = m.X + f.X * 10; m.LY = m.Y + f.Y * 10; m.LZ = m.Z + f.Z * 10;
            Markers.Insert(after + 1, m);
            Sel = after + 1;
            Rebuild(true);
            var h = MarkersEdited;
            if (h != null) h(CloneAll(Markers));
            return true;
        }

        /// <summary>The film tick of a path point (PathPts' own ticks), for a click on the path.</summary>
        public List<double> PathTicks { get; private set; }

        /// <summary>
        /// The path from the markers. While a marker is dragged (final false) it is built at 10 ms steps and only the path is
        /// redrawn; Mycelium's round 1 (10:35): "It's a bit slow to drag and edit these" - every move rebuilt it at 1 ms steps and
        /// had every view compose its 3D and redraw the map's wireframe, though only the path had moved.
        /// </summary>
        private void Rebuild(bool final)
        {
            string why;
            CamTrack t = CamPaths.TrackFromMarkers(Markers, final ? 1 : 10, out why);
            if (t != null) { Track = t; BuildPath(); if (final) CheckBlocked(); }
            if (final) Fire(false);
            else { var h = PathMoved; if (h != null) h(); }
        }

        // ---------------------------------------------------------------- the map

        private static readonly Dictionary<string, MapData> Maps = new Dictionary<string, MapData>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Loading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The last map load, for the pipe's "queue view" (how long it took, or why it failed).</summary>
        public static string LastMapLoad = "";

        private void SetMap(string map)
        {
            // the same map again: nothing to do once it is shown or loading; with no shape yet, look again (it may be made by now)
            if (string.Equals(map, Map, StringComparison.OrdinalIgnoreCase) && (MapData != null || string.IsNullOrEmpty(map) || IsLoading(map))) return;
            Map = map;
            MapData = null;
            if (string.IsNullOrEmpty(map)) return;
            MapData cached;
            lock (Maps) Maps.TryGetValue(map, out cached);
            if (cached != null) { MapData = cached; return; }
            string file = CamView.MeshFile(map);
            if (file == null)
            {
                Why = "No 3D map for " + map + " yet: Setup > 3D view maps makes it from your own game while a film plays on it. The path still plays.";
                // "each map by itself": made now if the game has this map loaded, then shown
                MapShapes.WhenMissing(map, () => { if (string.Equals(Map, map, StringComparison.OrdinalIgnoreCase) && MapData == null) { Map = null; SetMap(map); Fire(false); } });
                return;
            }
            lock (Maps) { if (!Loading.Add(map)) return; }
            Why = "loading " + map + "...";
            Dispatcher ui = Dispatcher.CurrentDispatcher;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                MapData m = null;
                string err = null;
                var sw = Stopwatch.StartNew();
                try { m = MapData.Load(file); }
                catch (Exception ex) { err = ex.Message; App.Note("loading the 3D map " + file, ex); }
                lock (Maps) { Loading.Remove(map); if (m != null) Maps[map] = m; }
                long ms = sw.ElapsedMilliseconds;
                ui.BeginInvoke(new Action(() =>
                {
                    LastMapLoad = map + ": " + (m == null ? "failed: " + err : ms + " ms, " + m.WireEdges + " wire edges");
                    if (!string.Equals(Map, map, StringComparison.OrdinalIgnoreCase)) return;
                    MapData = m;
                    Why = m == null ? "Could not load the 3D map " + map + ": " + err : "";
                    CheckBlocked();
                    Fire(false);
                }));
            });
        }

        private static bool IsLoading(string map) { lock (Maps) return Loading.Contains(map); }

        /// <summary>For the pictures: true once the map in hand has loaded (or there is none to load).</summary>
        public bool MapSettled { get { lock (Maps) return Map == null || MapData != null || !Loading.Contains(Map); } }
    }

    /// <summary>
    /// One map for the views, in three looks: its colours (RVM2: one per prop and world surface - Mycelium, 2026-10-14: "a
    /// Random Object Coloring so there can be some differentiation"), see-through, and its edges for the wireframe
    /// (Mycelium: "a Static Side View of Campaths with Wireframe, or Models Turned off, or rendered low visibility so the
    /// cameras are Easily Viewable"). Made on a worker thread; the models are frozen, so every view and thread can share them.
    /// </summary>
    public sealed class MapData
    {
        public Model3D Solid { get; private set; }
        public Model3D Ghost { get; private set; }
        /// <summary>The wireframe's edges: x0 y0 z0 x1 y1 z1 per edge - creases and outlines, not every triangle's.</summary>
        public float[] Wire { get; private set; }
        public int WireEdges { get { return Wire == null ? 0 : Wire.Length / 6; } }
        public Rect3D Bounds { get; private set; }

        /// <summary>24 colours round the hue circle, muted so the lighting still reads, in a shuffled (fixed) order so
        /// neighbouring groups do not look alike.</summary>
        internal static readonly Color[] Palette = MakePalette(24);

        public static MapData Load(string path)
        {
            using (var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
            using (var br = new BinaryReader(gz))
            {
                string magic = new string(br.ReadChars(4));
                if (magic != "RVM1" && magic != "RVM2") throw new InvalidDataException("not an RVM mesh");
                int nv = br.ReadInt32(), nt = br.ReadInt32();
                var b = new float[6];
                for (int i = 0; i < 6; i++) b[i] = br.ReadSingle();
                var v = new float[nv * 3];
                Buffer.BlockCopy(ReadExactly(br, nv * 12), 0, v, 0, nv * 12);
                var t = new int[nt * 3];
                Buffer.BlockCopy(ReadExactly(br, nt * 12), 0, t, 0, nt * 12);
                byte[] g = magic == "RVM2" ? ReadExactly(br, nt) : new byte[nt];
                int groups = magic == "RVM2" ? Palette.Length : 1;

                var pos = new Point3DCollection[groups];
                var nor = new Vector3DCollection[groups];
                var count = new int[groups];
                var tn = new Vector3D[nt];
                for (int k = 0; k < nt; k++) count[g[k] % groups]++;
                for (int k = 0; k < groups; k++) { pos[k] = new Point3DCollection(count[k] * 3); nor[k] = new Vector3DCollection(count[k] * 3); }
                for (int k = 0; k < nt; k++)
                {
                    int a = t[3 * k] * 3, bb = t[3 * k + 1] * 3, c = t[3 * k + 2] * 3;
                    var pa = new Point3D(v[a], v[a + 1], v[a + 2]);
                    var pb = new Point3D(v[bb], v[bb + 1], v[bb + 2]);
                    var pc = new Point3D(v[c], v[c + 1], v[c + 2]);
                    Vector3D n = Vector3D.CrossProduct(pb - pa, pc - pa);
                    if (n.LengthSquared < 1e-12) continue;
                    n.Normalize();
                    tn[k] = n;
                    int q = g[k] % groups;
                    pos[q].Add(pa); pos[q].Add(pb); pos[q].Add(pc);
                    nor[q].Add(n); nor[q].Add(n); nor[q].Add(n);
                }
                var solid = new Model3DGroup();
                var ghost = new Model3DGroup();
                for (int k = 0; k < groups; k++)
                {
                    if (pos[k].Count == 0) continue;
                    var mesh = new MeshGeometry3D { Positions = pos[k], Normals = nor[k] };
                    mesh.Freeze();
                    Color col = groups == 1 ? Color.FromRgb(168, 170, 176) : Palette[k];
                    var mat = new DiffuseMaterial(new SolidColorBrush(col));
                    mat.Freeze();
                    var gm = new GeometryModel3D(mesh, mat) { BackMaterial = mat };
                    gm.Freeze();
                    solid.Children.Add(gm);
                    // see-through: the same colour, faint, lit a little from within so it reads from every side
                    var gmat = new MaterialGroup();
                    gmat.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(24, col.R, col.G, col.B))));
                    gmat.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(16, col.R, col.G, col.B))));
                    gmat.Freeze();
                    var gg = new GeometryModel3D(mesh, gmat) { BackMaterial = gmat };
                    gg.Freeze();
                    ghost.Children.Add(gg);
                }
                solid.Freeze();
                ghost.Freeze();
                var md = new MapData { Solid = solid, Ghost = ghost, Wire = Edges(v, t, g, tn), Bounds = new Rect3D(b[0], b[1], b[2], b[3] - b[0], b[4] - b[1], b[5] - b[2]) };
                md.BuildGrid(v, t, b);
                return md;
            }
        }

        // ---------------------------------------------------------------- does a path pass through the map? (2026-10-16)
        // Mycelium's round 1 (10:42): a warning on screen when the camera passes through an object. The map's triangles in a grid
        // of 256-unit cells (each triangle in every cell its box touches), so a path segment tests only the triangles near it.

        private const double Cell = 256;
        private float[] _v;
        private int[] _t;
        private List<int>[] _cells;
        private double _gx, _gy, _gz;
        private int _nx, _ny, _nz;

        private void BuildGrid(float[] v, int[] t, float[] b)
        {
            _v = v; _t = t;
            _gx = b[0]; _gy = b[1]; _gz = b[2];
            _nx = Math.Max(1, (int)Math.Ceiling((b[3] - b[0]) / Cell) + 1);
            _ny = Math.Max(1, (int)Math.Ceiling((b[4] - b[1]) / Cell) + 1);
            _nz = Math.Max(1, (int)Math.Ceiling((b[5] - b[2]) / Cell) + 1);
            if ((long)_nx * _ny * _nz > 4000000) { _cells = null; return; }    // a map this big gets no warning rather than a slow Console
            _cells = new List<int>[_nx * _ny * _nz];
            for (int k = 0; k < t.Length / 3; k++)
            {
                double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
                for (int e = 0; e < 3; e++)
                {
                    int a = t[3 * k + e] * 3;
                    x0 = Math.Min(x0, v[a]); x1 = Math.Max(x1, v[a]);
                    y0 = Math.Min(y0, v[a + 1]); y1 = Math.Max(y1, v[a + 1]);
                    z0 = Math.Min(z0, v[a + 2]); z1 = Math.Max(z1, v[a + 2]);
                }
                int ix0 = Ix(x0, _gx, _nx), ix1 = Ix(x1, _gx, _nx), iy0 = Ix(y0, _gy, _ny), iy1 = Ix(y1, _gy, _ny), iz0 = Ix(z0, _gz, _nz), iz1 = Ix(z1, _gz, _nz);
                if ((long)(ix1 - ix0 + 1) * (iy1 - iy0 + 1) * (iz1 - iz0 + 1) > 4096) continue;     // a huge sky or ground sheet: not a wall
                for (int ix = ix0; ix <= ix1; ix++)
                    for (int iy = iy0; iy <= iy1; iy++)
                        for (int iz = iz0; iz <= iz1; iz++)
                        {
                            int c = (iz * _ny + iy) * _nx + ix;
                            (_cells[c] ?? (_cells[c] = new List<int>(8))).Add(k);
                        }
            }
        }

        private static int Ix(double p, double origin, int n) { return Math.Max(0, Math.Min(n - 1, (int)((p - origin) / Cell))); }

        /// <summary>True when the straight line from a to b crosses a triangle of the map.</summary>
        public bool SegmentHits(Point3D a, Point3D b)
        {
            if (_cells == null) return false;
            Vector3D d = b - a;
            int steps = Math.Max(1, (int)(d.Length / (Cell * 0.5)) + 1);
            var seen = new HashSet<int>();
            for (int s = 0; s <= steps; s++)
            {
                Point3D p = a + d * ((double)s / steps);
                int c = (Ix(p.Z, _gz, _nz) * _ny + Ix(p.Y, _gy, _ny)) * _nx + Ix(p.X, _gx, _nx);
                if (!seen.Add(c) || _cells[c] == null) continue;
                foreach (int k in _cells[c]) if (Crosses(a, d, k)) return true;
            }
            return false;
        }

        /// <summary>Moller-Trumbore, for the segment a .. a+d (both ends a hair inside, so a camera resting on a surface is not "through" it).</summary>
        private bool Crosses(Point3D a, Vector3D d, int k)
        {
            int i0 = _t[3 * k] * 3, i1 = _t[3 * k + 1] * 3, i2 = _t[3 * k + 2] * 3;
            var p0 = new Point3D(_v[i0], _v[i0 + 1], _v[i0 + 2]);
            Vector3D e1 = new Point3D(_v[i1], _v[i1 + 1], _v[i1 + 2]) - p0, e2 = new Point3D(_v[i2], _v[i2 + 1], _v[i2 + 2]) - p0;
            Vector3D h = Vector3D.CrossProduct(d, e2);
            double det = Vector3D.DotProduct(e1, h);
            if (Math.Abs(det) < 1e-9) return false;
            double f = 1 / det;
            Vector3D s = a - p0;
            double u = f * Vector3D.DotProduct(s, h);
            if (u < 0 || u > 1) return false;
            Vector3D q = Vector3D.CrossProduct(s, e1);
            double w = f * Vector3D.DotProduct(d, q);
            if (w < 0 || u + w > 1) return false;
            double t = f * Vector3D.DotProduct(e2, q);
            return t > 0.001 && t < 0.999;
        }

        /// <summary>
        /// The edges worth drawing: where two faces meet at more than 45 degrees (a wall's corner, a roof's ridge), where two
        /// props or surfaces meet at more than 20, and open edges. A flat floor's own triangles are left out, so the wireframe reads as
        /// the map's shapes rather than a mesh.
        /// </summary>
        private static float[] Edges(float[] v, int[] t, byte[] g, Vector3D[] tn)
        {
            int nt = t.Length / 3;
            // keyed by the pair of vertices; long's own hash (the halves XORed) gives neighbours nearly the same hash, and the
            // table turns into a list (mp_bridge took minutes) - so a mixing hash
            var first = new Dictionary<long, int>(nt * 2, Mix.Instance);
            var keep = new List<long>(nt / 2);
            var shared = new HashSet<long>(Mix.Instance);
            double crease = Math.Cos(45 * Math.PI / 180), seam = Math.Cos(20 * Math.PI / 180);
            for (int k = 0; k < nt; k++)
            {
                if (tn[k].LengthSquared < 0.5) continue;
                for (int e = 0; e < 3; e++)
                {
                    int a = t[3 * k + e], b = t[3 * k + (e + 1) % 3];
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    int other;
                    if (first.TryGetValue(key, out other))
                    {
                        shared.Add(key);
                        double dot = other >= 0 ? Vector3D.DotProduct(tn[k], tn[other]) : 1;
                        if (other >= 0 && (dot < crease || (g[k] != g[other] && dot < seam))) keep.Add(key);
                        first[key] = -1;                   // seen twice: decided
                    }
                    else first[key] = k;
                }
            }
            foreach (var kv in first) if (kv.Value >= 0 && !shared.Contains(kv.Key)) keep.Add(kv.Key);   // an open edge
            var w = new float[keep.Count * 6];
            int o = 0;
            foreach (long key in keep)
            {
                int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFF);
                w[o++] = v[a * 3]; w[o++] = v[a * 3 + 1]; w[o++] = v[a * 3 + 2];
                w[o++] = v[b * 3]; w[o++] = v[b * 3 + 1]; w[o++] = v[b * 3 + 2];
            }
            return w;
        }

        private sealed class Mix : IEqualityComparer<long>
        {
            public static readonly Mix Instance = new Mix();
            public bool Equals(long x, long y) { return x == y; }
            public int GetHashCode(long k) { unchecked { ulong h = (ulong)k * 0x9E3779B97F4A7C15UL; return (int)(h >> 32) ^ (int)h; } }
        }

        private static Color[] MakePalette(int n)
        {
            var order = new List<int>();
            for (int i = 0; i < n; i++) order.Add((i * 7) % n);               // 7 and 24 share no factor: every hue once
            var c = new Color[n];
            for (int i = 0; i < n; i++)
            {
                double h = order[i] / (double)n, s = i % 2 == 0 ? 0.42 : 0.3, l = i % 3 == 0 ? 0.66 : 0.58;
                c[i] = FromHsl(h, s, l);
            }
            return c;
        }

        private static Color FromHsl(double h, double s, double l)
        {
            Func<double, double, double, double> f = (p, q, t) =>
            {
                if (t < 0) t += 1; if (t > 1) t -= 1;
                if (t < 1.0 / 6) return p + (q - p) * 6 * t;
                if (t < 0.5) return q;
                if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
                return p;
            };
            double qq = l < 0.5 ? l * (1 + s) : l + s - l * s, pp = 2 * l - qq;
            return Color.FromRgb((byte)(255 * f(pp, qq, h + 1.0 / 3)), (byte)(255 * f(pp, qq, h)), (byte)(255 * f(pp, qq, h - 1.0 / 3)));
        }

        private static byte[] ReadExactly(BinaryReader br, int n)
        {
            byte[] b = br.ReadBytes(n);
            if (b.Length != n) throw new EndOfStreamException("the mesh file is cut short");
            return b;
        }
    }
}
