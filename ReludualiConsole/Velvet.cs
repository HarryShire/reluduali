using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ReludualiConsole
{
    /// <summary>
    /// The Designer's surfaces and edges (Mycelium, 2026-10-17): "Ableton Color Scheme, Reluduali Website Theme Edges and
    /// Texture Dithering, Make sure visibility is prioritized and doesn't interfere on the Overlay Setting. Velvet Dither
    /// Textures over the Ableton Color Scheme. Add a Contrast Feature for the UI Appearance in the Settings." Earlier the
    /// same day: "make the background way less foggy looking. Use a dither texture based on the Reluduali existing
    /// textures."
    ///
    /// - The colours are the Ableton theme's ("Eye-pleasing" by dede.zak97: plum #342836, panel #372938, device #161511,
    ///   lilac #BF90C6, soft lilac #99869C, raspberry #BF245D, orange #DB490E, teal #2E95BE).
    /// - The texture is reluduali.com's velvet, made the website's way (its velvet.mjs): a crushed pile of warped noise,
    ///   longer across than down, worked out a cell to every two pixels and put on a few tones through the 8x8 ordered
    ///   (Bayer) pattern of its cards and of the greeting's sky. Here the pile is fine and the tones are close (a few
    ///   steps of the theme's plum), so it reads as cloth under the text and never as fog; a gentle shade runs down a page.
    /// - The edges are the website's holographic foil (#ff4fae #ffd24f #4fffc0 #4fb4ff #b44fff at 115 degrees over
    ///   pearl), moved by the pointer (FollowPointer): Theme.xaml binds its FoilShift to Foil.
    /// - Contrast (Setup > Appearance; "contrast" in the Designer's places.txt: soft, normal or high) sets the letters, the
    ///   lines and how strong the texture is. The Designer's Skin.cs asks for a contrast's colours and surfaces by name
    ///   (TextFor, DeepFor ...), so a change shows at once; the properties without a name are the contrast saved when the
    ///   program started (the installer's).
    ///
    /// The Designer's Theme.xaml takes these through Skin.cs (the dark half of its day and night). The installer links this
    /// file for the same surfaces.
    /// </summary>
    public static class Velvet
    {
        // ------------------------------------------------------------------ the contrast setting
        /// <summary>"soft", "normal" or "high", from %LOCALAPPDATA%\Reluduali\Console\places.txt (its "contrast" line), as it
        /// was when the program started.</summary>
        public static readonly string Contrast = ReadContrast();
        static string ReadContrast()
        {
            try
            {
                string f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reluduali", "Console", "places.txt");
                if (File.Exists(f))
                    foreach (string l in File.ReadAllLines(f))
                        if (l.StartsWith("contrast=", StringComparison.OrdinalIgnoreCase))
                        {
                            string v = l.Substring(9).Trim().ToLowerInvariant();
                            if (v == "soft" || v == "high") return v;
                        }
            }
            catch { }
            return "normal";
        }

        // ------------------------------------------------------------------ colours that follow the contrast
        public static Color TextColor { get { return TextFor(Contrast); } }
        public static Color DimColor { get { return DimFor(Contrast); } }
        public static Color ChromeColor { get { return ChromeFor(Contrast); } }
        public static Color BorderColor { get { return BorderFor(Contrast); } }
        public static Color BackColor { get { return BackFor(Contrast); } }
        public static Color InsetColor { get { return InsetFor(Contrast); } }

        public static Color TextFor(string c) { return c == "high" ? C(0xFFFFFFFF) : c == "soft" ? C(0xFFD6CDD8) : C(0xFFE6DEE8); }
        public static Color DimFor(string c) { return c == "high" ? C(0xFFCBBECD) : c == "soft" ? C(0xFF968799) : C(0xFFA797AA); }
        public static Color ChromeFor(string c) { return c == "high" ? C(0xFFFFFFFF) : C(0xFFF6F0F7); }
        public static Color BorderFor(string c) { return c == "high" ? C(0xFF7C6B7F) : c == "soft" ? C(0xFF4B3C4E) : C(0xFF544457); }
        public static Color BackFor(string c) { return c == "high" ? C(0xFF221925) : C(0xFF2B2030); }
        public static Color InsetFor(string c) { return c == "high" ? C(0xFF19121B) : C(0xFF221924); }
        internal static Color C(uint argb) { return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb); }

        // ------------------------------------------------------------------ the foil's light
        /// <summary>Where the foil's light is: the pointer, as a fraction of its window (bound by Theme.xaml's FoilShift;
        /// the binding also keeps the foil brushes from being frozen, so they can move).</summary>
        public sealed class Light : System.ComponentModel.INotifyPropertyChanged
        {
            double _x, _y;
            public double X { get { return _x; } set { _x = value; Changed("X"); } }
            public double Y { get { return _y; } set { _y = value; Changed("Y"); } }
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            void Changed(string n) { var h = PropertyChanged; if (h != null) h(this, new System.ComponentModel.PropertyChangedEventArgs(n)); }
        }
        public static readonly Light Foil = new Light();

        /// <summary>Every window's pointer moves the one foil light, so the edges catch it together (a pixel's move redraws nothing).</summary>
        public static void FollowPointer()
        {
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseMoveEvent, new MouseEventHandler((s, e) =>
            {
                var w = s as Window;
                if (w == null || w.ActualWidth < 1 || w.ActualHeight < 1) return;
                Point p = e.GetPosition(w);
                double x = (p.X / w.ActualWidth - 0.5) * 0.7, y = (p.Y / w.ActualHeight - 0.5) * 0.45;
                if (Math.Abs(x - Foil.X) + Math.Abs(y - Foil.Y) < 0.006) return;
                Foil.X = x; Foil.Y = y;
            }), true);
        }

        // ------------------------------------------------------------------ the surfaces
        // the page: the theme's plum (a shade runs down it)
        static readonly uint[] Page = { 0xFF2C2131, 0xFF2F2434, 0xFF322637, 0xFF35293A, 0xFF382B3C };
        // the window's frame, the header and the rail: a step darker
        static readonly uint[] Frame = { 0xFF241A27, 0xFF271D2A, 0xFF2A202D, 0xFF2D2230, 0xFF302533 };
        // a card: the theme's panel
        static readonly uint[] Card = { 0xFF33263A, 0xFF36293C, 0xFF392C3F, 0xFF3C2F42 };
        // a 3D view or the game's slot: the theme's device black
        static readonly uint[] View = { 0xFF141012, 0xFF181316, 0xFF1C161A };

        /// <summary>The 8x8 ordered (Bayer) pattern of reluduali.com's cards, world and velvet, row by row.</summary>
        internal static readonly int[] B = { 0, 32, 8, 40, 2, 34, 10, 42, 48, 16, 56, 24, 50, 18, 58, 26, 12, 44, 4, 36, 14, 46, 6, 38, 60, 28, 52, 20, 62, 30, 54, 22,
                                    3, 35, 11, 43, 1, 33, 9, 41, 51, 19, 59, 27, 49, 17, 57, 25, 15, 47, 7, 39, 13, 45, 5, 37, 63, 31, 55, 23, 61, 29, 53, 21 };

        /// <summary>A tile's size: 256 x 256 pixels, a cell two of them; the pile's lattice repeats inside it, so the tile
        /// meets itself without a seam. A page's shade does not tile: it is a separate picture 2160 pixels tall.</summary>
        public const int Tile = 256;

        // how much of a surface's look is the pile (the rest is the shade): high contrast keeps the texture quiet
        static double NapFor(string c) { return c == "high" ? 0.35 : c == "soft" ? 0.85 : 0.65; }

        /// <summary>The page: velvet over a shade, lighter at the top.</summary>
        public static BitmapSource DeepImage { get { return DeepFor(Contrast); } }
        /// <summary>A card: velvet, even from top to foot.</summary>
        public static BitmapSource CardImage { get { return CardFor(Contrast); } }
        /// <summary>The window's frame, the header and the rail.</summary>
        public static BitmapSource RailImage { get { return RailFor(Contrast); } }
        /// <summary>A 3D view's ground and the game's slot: the device black, barely textured.</summary>
        public static BitmapSource ViewImage { get { return ViewFor(Contrast); } }

        // each surface made once for each contrast asked for
        static readonly System.Collections.Generic.Dictionary<string, BitmapSource> _made = new System.Collections.Generic.Dictionary<string, BitmapSource>();
        static BitmapSource Made(string key, Func<BitmapSource> make)
        {
            lock (_made)
            {
                BitmapSource b;
                if (!_made.TryGetValue(key, out b)) _made[key] = b = make();
                return b;
            }
        }
        public static BitmapSource DeepFor(string c) { return Made("deep " + c, () => Surface(Page, 3, 0.0, 1.0, 2160, NapFor(c))); }
        public static BitmapSource CardFor(string c) { return Made("card " + c, () => Surface(Card, 11, 0.45, 0.45, Tile, NapFor(c))); }
        public static BitmapSource RailFor(string c) { return Made("rail " + c, () => Surface(Frame, 7, 0.2, 0.8, 2160, NapFor(c))); }
        public static BitmapSource ViewFor(string c) { return Made("view " + c, () => Surface(View, 5, 0.5, 0.5, Tile, NapFor(c))); }

        /// <summary>A surface at the contrast the program started with.</summary>
        public static BitmapSource Surface(uint[] tones, int seed, double from, double to, int height)
        {
            return Surface(tones, seed, from, to, height, NapFor(Contrast));
        }

        /// <summary>A surface: Tile pixels wide, <paramref name="height"/> tall (Tile, or 2160 for a page's shade); each
        /// two-pixel cell put on one of the tones by the pile (<paramref name="nap"/> of it) and the shade from
        /// <paramref name="from"/> at the top to <paramref name="to"/> at the foot (0 = the lightest tone), through the 8x8
        /// ordered pattern.</summary>
        public static BitmapSource Surface(uint[] tones, int seed, double from, double to, int height, double nap)
        {
            int cw = Tile / 2, ch = height / 2, levels = tones.Length - 1;
            // the pile, on a lattice of 8 x 12 noise cells across the tile (longer across than down), repeating
            const int px = 8, py = 12;
            double su = cw / (double)px, sv = (Tile / 2) / (double)py;
            var pixels = new uint[Tile * height];
            for (int y = 0; y < ch; y++)
            {
                double shade = 1 - (from + (to - from) * y / (double)Math.Max(1, ch - 1));
                for (int x = 0; x < cw; x++)
                {
                    double u = x / su, v = (y % (Tile / 2)) / sv;
                    double wx = Fbm(u, v, seed + 3, px, py), wy = Fbm(u + 5.2, v + 1.3, seed + 7, px, py);
                    double pile = Fbm(u + 1.4 * wx, v + 1.4 * wy, seed + 11, px, py);
                    double t = shade * (1 - nap) + pile * nap;
                    double d = (B[(y & 7) * 8 + (x & 7)] + 0.5) / 64.0;
                    int q = (int)(t * levels + d);
                    if (q < 0) q = 0; else if (q > levels) q = levels;
                    uint c = tones[q];
                    int o = (y * 2) * Tile + x * 2;
                    pixels[o] = c; pixels[o + 1] = c; pixels[o + Tile] = c; pixels[o + Tile + 1] = c;
                }
            }
            var bmp = BitmapSource.Create(Tile, height, 96, 96, PixelFormats.Bgra32, null, pixels, Tile * 4);
            bmp.Freeze();
            return bmp;
        }

        // value noise on a lattice that wraps every (px, py) cells at the base octave, doubling with each octave
        internal static double Hash(int ix, int iy, int seed)
        {
            unchecked
            {
                int h = ix * 374761393 + iy * 668265263 + seed * 1442695041;
                h = (h ^ (int)((uint)h >> 13)) * 1274126177;
                return ((uint)(h ^ (int)((uint)h >> 16))) / 4294967296.0;
            }
        }

        static double Noise(double x, double y, int seed, int px, int py)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            double fx = x - ix, fy = y - iy;
            double sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
            int x0 = Mod(ix, px), x1 = Mod(ix + 1, px), y0 = Mod(iy, py), y1 = Mod(iy + 1, py);
            double a = Hash(x0, y0, seed), b = Hash(x1, y0, seed), c = Hash(x0, y1, seed), d = Hash(x1, y1, seed);
            return a + (b - a) * sx + (c - a) * sy + (a - b - c + d) * sx * sy;
        }

        internal static double Fbm(double x, double y, int seed, int px, int py)
        {
            double v = 0, amp = 0.5;
            for (int o = 0, f = 1; o < 3; o++, f *= 2) { v += amp * Noise(x * f, y * f, seed + o, px * f, py * f); amp *= 0.5; }
            return v / 0.875;
        }

        static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }
    }
}
