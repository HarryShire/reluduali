using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace ReludualiConsole
{
    /// <summary>
    /// The players and their guns for the CAM QUEUE's 3D view (2026-10-14; Mycelium: "Make sure the playermodels and
    /// weapons are loaded in the Consoles 3D Viewport"): tools\mapimport\viewport_players.py's RVP1 file - the game's own
    /// models and every bone of every player through a path's window.
    /// Each frame the models are skinned here on the CPU (4 weights a vertex) and drawn in both views; a gun rides its
    /// player's right hand. Poses between two stored frames are blended.
    /// </summary>
    public sealed class CamPlayers
    {
        private sealed class Mesh
        {
            public byte Kind;                  // 0 body, 1 gun
            public float[] V;                  // bind pose, xyz
            public ushort[] B;                 // 4 bones a vertex
            public float[] W;                  // 4 weights a vertex
            public Int32Collection Tris;
            public int Bones;
        }

        private sealed class Instance
        {
            public Mesh Mesh;
            public int Entity;
            public byte Flag, Colour;
            public float[][] Frames;           // per frame: bones x 12 (3x4 row-major), or null when not shown
            public MeshGeometry3D Geometry;    // one per instance, shared by every view's model of it
            public Material Solid, Ghost;
        }

        /// <summary>A view's group of the players: its own model of each instance (solid or see-through) over the shared meshes.</summary>
        private sealed class ViewGroup
        {
            public Model3DGroup Group;
            public GeometryModel3D[] Models;
        }

        private readonly List<ViewGroup> _groups = new List<ViewGroup>();

        public int FirstTick { get; private set; }
        public int StepMs { get; private set; }
        public int FrameCount { get; private set; }
        public int Bodies { get; private set; }
        public int Guns { get; private set; }
        public string Source { get; private set; }

        private readonly List<Instance> _inst = new List<Instance>();
        // made in Build and NewGroup, on the UI thread: Load runs on a worker, and a group made there belongs to that thread
        // (sandbox 0399: Model3DGroup.Children threw from the view's frame loop)
        private bool _built;

        /// <summary>One solid group (the self-test's RenderTest draws it).</summary>
        public Model3DGroup LensGroup { get; private set; }

        private static readonly Color[] Tints =
        {
            Color.FromRgb(214, 120, 110), Color.FromRgb(110, 170, 220), Color.FromRgb(130, 200, 130), Color.FromRgb(220, 190, 100),
            Color.FromRgb(180, 130, 210), Color.FromRgb(100, 200, 200), Color.FromRgb(230, 150, 80), Color.FromRgb(160, 160, 220),
        };

        /// <summary>Reads an RVP1 file (any thread). The WPF models are made by <see cref="Build"/> on the UI thread.</summary>
        public static CamPlayers Load(string path)
        {
            var p = new CamPlayers { Source = path };
            using (var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
            using (var br = new BinaryReader(gz))
            {
                if (new string(br.ReadChars(4)) != "RVP1") throw new InvalidDataException("not an RVP1 players file");
                p.FirstTick = br.ReadInt32();
                p.StepMs = br.ReadInt32();
                p.FrameCount = (int)br.ReadUInt32();
                int nm = (int)br.ReadUInt32(), ni = (int)br.ReadUInt32();
                var meshes = new List<Mesh>();
                for (int m = 0; m < nm; m++)
                {
                    var me = new Mesh { Kind = br.ReadByte() };
                    int nv = (int)br.ReadUInt32(), nt = (int)br.ReadUInt32();
                    me.Bones = br.ReadUInt16();
                    me.V = new float[nv * 3];
                    Buffer.BlockCopy(br.ReadBytes(nv * 12), 0, me.V, 0, nv * 12);
                    me.B = new ushort[nv * 4];
                    me.W = new float[nv * 4];
                    byte[] w = br.ReadBytes(nv * 12);
                    for (int v = 0; v < nv; v++)
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            me.B[v * 4 + k] = BitConverter.ToUInt16(w, v * 12 + k * 2);
                            me.W[v * 4 + k] = w[v * 12 + 8 + k] / 255f;
                        }
                    }
                    var t = new int[nt * 3];
                    Buffer.BlockCopy(br.ReadBytes(nt * 12), 0, t, 0, nt * 12);
                    me.Tris = new Int32Collection(t);
                    me.Tris.Freeze();
                    meshes.Add(me);
                }
                for (int i = 0; i < ni; i++)
                {
                    var ins = new Instance { Mesh = meshes[br.ReadUInt16()], Entity = br.ReadInt32(), Flag = br.ReadByte(), Colour = br.ReadByte() };
                    ins.Frames = new float[p.FrameCount][];
                    int floats = ins.Mesh.Bones * 12;
                    for (int f = 0; f < p.FrameCount; f++)
                    {
                        if (br.ReadByte() == 0) continue;
                        var a = new float[floats];
                        Buffer.BlockCopy(br.ReadBytes(floats * 4), 0, a, 0, floats * 4);
                        ins.Frames[f] = a;
                    }
                    if (ins.Mesh.Kind == 1) p.Guns++; else p.Bodies++;
                    p._inst.Add(ins);
                }
            }
            return p;
        }

        /// <summary>The WPF side (UI thread): one mesh per instance, shared by every view's model of it.</summary>
        public void Build()
        {
            if (_built) return;
            _built = true;
            foreach (Instance ins in _inst)
            {
                Color c = ins.Mesh.Kind == 1 ? Color.FromRgb(70, 72, 78) : Tints[(ins.Entity * 5 + 3) % Tints.Length];
                if (ins.Flag == 1 && ins.Mesh.Kind == 0) c = Color.FromRgb((byte)(c.R * 0.6), (byte)(c.G * 0.6), (byte)(c.B * 0.6));   // a corpse, darker
                var mat = new DiffuseMaterial(new SolidColorBrush(c));
                mat.Freeze();
                var ghost = new MaterialGroup();
                ghost.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(70, c.R, c.G, c.B))));
                ghost.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(30, c.R, c.G, c.B))));
                ghost.Freeze();
                ins.Solid = mat; ins.Ghost = ghost;
                ins.Geometry = new MeshGeometry3D { TriangleIndices = ins.Mesh.Tris, Positions = new Point3DCollection() };
            }
            LensGroup = NewGroup(false);
        }

        /// <summary>A group of the players for one view, posed with the rest (UI thread). Ghost: see-through.</summary>
        public Model3DGroup NewGroup(bool ghost)
        {
            Build();
            var g = new ViewGroup { Group = new Model3DGroup(), Models = new GeometryModel3D[_inst.Count] };
            for (int i = 0; i < _inst.Count; i++)
            {
                Material m = ghost ? _inst[i].Ghost : _inst[i].Solid;
                g.Models[i] = new GeometryModel3D(_inst[i].Geometry, m) { BackMaterial = m };
            }
            _groups.Add(g);
            Fill(g);
            return g.Group;
        }

        /// <summary>A view closed or changed its look: its group is no longer posed.</summary>
        public void Release(Model3DGroup group) { _groups.RemoveAll(x => x.Group == group); }

        public void ReleaseAll() { _groups.Clear(); }

        private bool[] _shown = new bool[0];

        private void Fill(ViewGroup g)
        {
            g.Group.Children.Clear();
            for (int i = 0; i < _inst.Count && i < _shown.Length; i++) if (_shown[i]) g.Group.Children.Add(g.Models[i]);
        }

        private int _shownOver = -1;

        /// <summary>Poses everyone for a film tick (UI thread): blended between the two stored frames around it.</summary>
        public void Pose(double tick)
        {
            if (FrameCount == 0 || !_built) return;
            double fi = (tick - FirstTick) / StepMs;
            int f0 = (int)Math.Floor(fi);
            double u = fi - f0;
            if (f0 < 0) { f0 = 0; u = 0; }
            if (f0 >= FrameCount - 1) { f0 = FrameCount - 1; u = 0; }
            int f1 = Math.Min(FrameCount - 1, f0 + 1);
            if (_shown.Length != _inst.Count) _shown = new bool[_inst.Count];
            bool changed = false;
            int shown = 0;
            for (int i = 0; i < _inst.Count; i++)
            {
                Instance ins = _inst[i];
                float[] a = ins.Frames[f0], b = ins.Frames[f1];
                bool on = a != null;
                if (on != _shown[i]) { _shown[i] = on; changed = true; }
                if (!on) continue;
                if (b == null) { b = a; }
                Skin(ins, a, b, (float)u);
                shown++;
            }
            if (changed) foreach (ViewGroup g in _groups) Fill(g);       // who is shown changed: every view's group follows
            _shownOver = shown;
        }

        public int Shown { get { return _shownOver; } }

        private static void Skin(Instance ins, float[] a, float[] b, float u)
        {
            Mesh me = ins.Mesh;
            int nv = me.V.Length / 3;
            var pts = new Point3DCollection(nv);
            float[] m = new float[12];
            for (int v = 0; v < nv; v++)
            {
                float x = me.V[v * 3], y = me.V[v * 3 + 1], z = me.V[v * 3 + 2];
                double ox = 0, oy = 0, oz = 0, wsum = 0;
                for (int k = 0; k < 4; k++)
                {
                    float w = me.W[v * 4 + k];
                    if (w <= 0) continue;
                    int bo = me.B[v * 4 + k] * 12;
                    if (bo + 12 > a.Length) continue;
                    for (int j = 0; j < 12; j++) m[j] = a[bo + j] + (b[bo + j] - a[bo + j]) * u;
                    ox += w * (m[0] * x + m[1] * y + m[2] * z + m[3]);
                    oy += w * (m[4] * x + m[5] * y + m[6] * z + m[7]);
                    oz += w * (m[8] * x + m[9] * y + m[10] * z + m[11]);
                    wsum += w;
                }
                if (wsum > 0) { ox /= wsum; oy /= wsum; oz /= wsum; }
                pts.Add(new Point3D(ox, oy, oz));
            }
            ins.Geometry.Positions = pts;
        }
    }
}
