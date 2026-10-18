using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>
    /// A card, printed like reluduali.com's (its skin.css; Mycelium, 2026-10-17: "Shiny Pokemon Cards somewhat. Use a Mac
    /// 8-16bit dithering to get this sort of Canadian Money/Shiny Glossy Texture on things", and for the Designer: "I really
    /// want to urge the dither texture and the printstream reflective design ... Translucent sort of crystaline feel").
    /// A CardBorder (BatchCore's, so the BATCH page's cards are printed too) with the Card style; Printer paints it.
    /// </summary>
    public class PrintCard : CardBorder
    {
        public PrintCard() { Style = Ui.Style("Card"); }

        /// <summary>Every card draws itself again (the skin changed).</summary>
        public static void All() { Printer.All(); }
    }

    /// <summary>
    /// What prints a card (every CardBorder, once App sets CardBorder.NewPainter). By day (Skin.IsLight), the site's
    /// layers, bottom to top:
    ///  - a soft shadow under it, and its pearl face, a little clear so the sky's dither shows through;
    ///  - the double rule: the face's colour, then a hairline of the edge's blue, just inside the edge;
    ///  - banknote linework: rows of fine blue waves and, to the right, a medallion of three maroon rosettes (the site's
    ///    hypotrochoids, its sizes);
    ///  - the window stripe of a polymer note (22 px, 16 px in from the right) and the holographic foil across it all
    ///    (linear-gradient 115 degrees, its hues at 30-70 % of a box 280 % the card's size), printed over the face as the
    ///    site's multiply prints them; the foil's band goes where the pointer is when it is over the card, and moves with
    ///    the window's light as the pointer crosses the window;
    ///  - a glare where the pointer is (at rest half way across, a third down);
    ///  - the body: the face again at three quarters (the site's card-body), so the letters stay first and the print shows
    ///    in the frame round them;
    ///  - over it all an old Mac's dither, the 8x8 pattern's halves a touch lighter and darker, a pixel a cell; the edge.
    /// By night it leaves the card to be the velvet card it always was (a Border with the Card style).
    /// </summary>
    sealed class Printer : ICardPainter
    {
        static readonly List<WeakReference> _all = new List<WeakReference>();

        public static void All()
        {
            lock (_all)
                for (int i = _all.Count - 1; i >= 0; i--)
                {
                    var c = _all[i].Target as CardBorder;
                    if (c == null) _all.RemoveAt(i); else c.InvalidateVisual();
                }
        }

        readonly CardBorder _card;
        bool _over;
        Point _at;
        LinearGradientBrush _foil;
        RadialGradientBrush _glare;

        public Printer(CardBorder card)
        {
            _card = card;
            lock (_all) _all.Add(new WeakReference(card));
            card.MouseEnter += (s, e) => { _over = true; _at = e.GetPosition(card); if (Skin.Now.IsLight) card.InvalidateVisual(); };
            card.MouseLeave += (s, e) => { _over = false; if (Skin.Now.IsLight) card.InvalidateVisual(); };
            card.MouseMove += (s, e) =>
            {
                if (!Skin.Now.IsLight) return;
                Point p = e.GetPosition(card);
                if (Math.Abs(p.X - _at.X) + Math.Abs(p.Y - _at.Y) < 3) return;
                _at = p;
                Aim(card.ActualWidth, card.ActualHeight);
            };
        }

        public bool Render(DrawingContext dc)
        {
            Skin s = Skin.Now;
            double w = _card.ActualWidth, h = _card.ActualHeight;
            if (!s.IsLight || w < 8 || h < 8) return false;
            var ink = Print.For(s);
            double cr = Math.Max(0, _card.CornerRadius.TopLeft);
            var r = new Rect(0, 0, w, h);
            Thickness pad = _card.Padding;

            Print.Shadow(dc, w, h, cr, ink);
            dc.DrawRoundedRectangle(_card.Background, null, r, cr, cr);
            // the double rule: a band of the face, then the edge's hairline
            double frame = Math.Max(5, Math.Min(16, Math.Min(pad.Left, pad.Top) - 2));
            double rule = Math.Min(6, frame - 3);
            dc.DrawRoundedRectangle(null, ink.Rule, new Rect(rule + 0.5, rule + 0.5, Math.Max(0, w - 2 * rule - 1), Math.Max(0, h - 2 * rule - 1)), 1.5, 1.5);

            dc.PushClip(new RectangleGeometry(r, cr, cr));
            // banknote linework: the waves, and the medallion to the right
            dc.PushOpacity(ink.Linework);
            dc.DrawRectangle(Print.Waves, null, r);
            dc.PushTransform(new TranslateTransform(Math.Round(w - 150), Math.Round(h / 2)));
            dc.DrawDrawing(Print.Medallion);
            dc.Pop();
            dc.Pop();
            // the window stripe and the foil, printed over the face (the site's multiply: its dither mask is opaque in both
            // of its colours, so the two cover the card whole, and the dither is the grain laid over it all, below)
            var stripe = new Rect(w - 16 - 22, 0, 22, h);
            if (stripe.X > 40)
            {
                dc.PushOpacity(Math.Min(1, 0.4 * ink.Strength));
                dc.DrawRectangle(ink.Stripe, null, stripe);
                dc.Pop();
            }
            if (_foil == null) _foil = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, RelativeTransform = Print.Shift };
            _foil.GradientStops = ink.Foil;
            // the glare: where the pointer is, and at rest half way across and a third of the way down (the site's)
            if (_glare == null) _glare = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, GradientStops = Print.GlareStops };
            Aim(w, h);
            dc.PushOpacity(Math.Min(1, (_over ? 0.38 : 0.22) * ink.Strength));
            dc.DrawRectangle(_foil, null, r);
            dc.Pop();
            dc.PushOpacity(_over ? 1 : 0.6);
            dc.DrawRectangle(_glare, null, r);
            dc.Pop();
            // the body: the face again at three quarters, so the letters stay first and the print shows in the frame
            double b = frame;
            dc.DrawRoundedRectangle(ink.Body, null, new Rect(b, b, Math.Max(0, w - 2 * b), Math.Max(0, h - 2 * b)), 2, 2);
            // an old Mac's dither over the print: the 8x8 pattern's light and dark halves, a pixel a cell
            if (ink.Grain != null) dc.DrawRectangle(ink.Grain, null, r);
            dc.Pop();
            // the edge (the site's 2 px)
            var pen = new Pen(_card.BorderBrush ?? ink.Rule.Brush, 2);
            dc.DrawRoundedRectangle(null, pen, new Rect(1, 1, Math.Max(0, w - 2), Math.Max(0, h - 2)), cr, cr);
            return true;
        }

        public bool HitTest(PointHitTestParameters p, out HitTestResult result)
        {
            result = null;
            if (!Skin.Now.IsLight) return false;
            // the shadow is drawn outside the card: only the card itself is the card
            Point pt = p.HitPoint;
            if (pt.X >= 0 && pt.Y >= 0 && pt.X <= _card.ActualWidth && pt.Y <= _card.ActualHeight) result = new PointHitTestResult(_card, pt);
            return true;
        }

        /// <summary>The foil's band and the glare toward the pointer (the site's --fx/--fy and --gx/--gy).</summary>
        void Aim(double w, double h)
        {
            // at rest where the site's drift often rests, toward its far end: the blue and the violet across the card, a
            // pearl and lavender cast (the middle would put the mint in the middle: a cold one)
            double fx = 0.8, fy = 0.75;
            if (_over && w > 0 && h > 0) { fx = Math.Max(0, Math.Min(1, _at.X / w)); fy = Math.Max(0, Math.Min(1, _at.Y / h)); }
            if (_foil != null)
            {
                // the gradient's box is 2.8 times the card, placed as background-position fx% fy% places it; its line
                // runs at 115 degrees (to the right and a little down) through the box's middle
                double bw = 2.8 * w, bh = 2.8 * h;
                double cx = (w - bw) * fx + bw / 2, cy = (h - bh) * fy + bh / 2;
                double dx = Math.Sin(115 * Math.PI / 180), dy = -Math.Cos(115 * Math.PI / 180);
                double len = bw * Math.Abs(dx) + bh * Math.Abs(dy);
                _foil.StartPoint = new Point(cx - dx * len / 2, cy - dy * len / 2);
                _foil.EndPoint = new Point(cx + dx * len / 2, cy + dy * len / 2);
            }
            if (_glare != null)
            {
                double gx = _over ? _at.X : w * 0.5, gy = _over ? _at.Y : h * 0.3;
                double far = Math.Sqrt(Math.Pow(Math.Max(gx, w - gx), 2) + Math.Pow(Math.Max(gy, h - gy), 2));
                _glare.Center = _glare.GradientOrigin = new Point(gx, gy);
                _glare.RadiusX = _glare.RadiusY = Math.Max(40, Math.Min(0.46 * far, 420));
            }
        }
    }

    /// <summary>
    /// RELUDUALI in its grown lettering (theater\reluduali.svg, the splash's), as the site's mark has it: the letters in
    /// the titles' ink, the seeds in the seed colour (maroon by day, orange in the evening). Without the file, the name in
    /// the titles' Georgia.
    /// </summary>
    public sealed class Lettering : FrameworkElement
    {
        static Geometry _letters, _seeds;
        static bool _tried;

        static bool Load()
        {
            if (!_tried)
            {
                _tried = true;
                try
                {
                    Geometry l, s;
                    SplashPainter.Lettering(out l, out s);
                    if (l != null) { l.Freeze(); s.Freeze(); _letters = l; _seeds = s; }
                }
                catch (Exception ex) { App.Note("the start screen's lettering", ex); }
            }
            return _letters != null;
        }

        protected override Size MeasureOverride(Size available)
        {
            double h = double.IsNaN(Height) ? 40 : Height;
            if (!Load()) return new Size(h * 5, h);
            Rect b = Rect.Union(_letters.Bounds, _seeds.Bounds);
            return new Size(b.Height > 0 ? h * b.Width / b.Height : h * 5, h);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double h = ActualHeight, w = ActualWidth;
            if (h < 1) return;
            if (!Load())
            {
                var ft = new FormattedText("Reluduali", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                           new Typeface(new FontFamily("Georgia"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), h * 0.9, Ui.Brush("Chrome"), 1.0);
                dc.DrawText(ft, new Point((w - ft.Width) / 2, 0));
                return;
            }
            Rect b = Rect.Union(_letters.Bounds, _seeds.Bounds);
            double k = h / b.Height;
            dc.PushTransform(new MatrixTransform(k, 0, 0, k, (w - b.Width * k) / 2 - b.X * k, -b.Y * k));
            dc.DrawGeometry(Ui.Brush("Chrome"), null, _letters);
            dc.DrawGeometry(Ui.Brush("Seed"), null, _seeds);
            dc.Pop();
        }
    }

    /// <summary>The printstream's pieces: made once, or once for each of the skin's colourings.</summary>
    public static class Print
    {
        /// <summary>The 8x8 ordered dither's half, a pixel a cell (cells under 32 shown: the site's mask for its foil).</summary>
        public static readonly ImageBrush Dither = MakeDither();

        static ImageBrush MakeDither()
        {
            var px = new uint[64];
            for (int i = 0; i < 64; i++) px[i] = Velvet.B[i] < 32 ? 0xFF000000u : 0u;
            var bmp = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, px, 32);
            bmp.Freeze();
            var b = new ImageBrush(bmp) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(b, BitmapScalingMode.NearestNeighbor);
            b.Freeze();
            return b;
        }

        /// <summary>The window's light (Theme.xaml's FoilShift: the pointer across the window), shared by every foil.</summary>
        public static Transform Shift
        {
            get
            {
                try { return (Transform)Application.Current.FindResource("FoilShift"); }
                catch { return Transform.Identity; }
            }
        }

        /// <summary>Rows of fine waves, as the site's guilloche: a wave every 12 px down, 20 px long, #4fb4ff at .22.</summary>
        public static readonly DrawingBrush Waves = MakeWaves();

        static DrawingBrush MakeWaves()
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
                for (int k = 0; k <= 26; k++)
                {
                    c.BeginFigure(new Point(0, k * 12), false, false);
                    for (int i = 0; i < 22; i++) c.QuadraticBezierTo(new Point(i * 10 + 5, k * 12 + (i % 2 == 1 ? 4 : -4)), new Point(i * 10 + 10, k * 12), true, true);
                }
            g.Freeze();
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(96, 0x4F, 0xB4, 0xFF)), 0.5);
            pen.Freeze();
            var dg = new DrawingGroup();
            dg.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 220, 312))));
            dg.Children.Add(new GeometryDrawing(null, pen, g));
            dg.ClipGeometry = new RectangleGeometry(new Rect(0, 0, 220, 312));
            dg.Freeze();
            var b = new DrawingBrush(dg) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 220, 312), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
            b.Freeze();
            return b;
        }

        /// <summary>The site's three rosettes (hypotrochoids: R, r, d, turns, opacity), maroon, round (0, 0).</summary>
        public static readonly Drawing Medallion = MakeMedallion();

        static Drawing MakeMedallion()
        {
            var dg = new DrawingGroup();
            foreach (double[] p in new[] { new[] { 60, 17, 41, 9, 0.5 }, new[] { 60, 23, 30, 13, 0.35 }, new[] { 44, 13, 30, 7, 0.4 } })
            {
                double R = p[0], r = p[1], d = p[2], n = p[3];
                int steps = (int)(720 * n / 4);
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    for (int i = 0; i <= steps; i++)
                    {
                        double t = (double)i / steps * Math.PI * 2 * n;
                        var pt = new Point((R - r) * Math.Cos(t) + d * Math.Cos((R - r) / r * t), (R - r) * Math.Sin(t) - d * Math.Sin((R - r) / r * t));
                        if (i == 0) c.BeginFigure(pt, false, false); else c.LineTo(pt, true, false);
                    }
                }
                g.Freeze();
                var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * p[4]), 0x5C, 0x0F, 0x2E)), 0.55);
                pen.Freeze();
                dg.Children.Add(new GeometryDrawing(null, pen, g));
            }
            dg.Freeze();
            return dg;
        }

        /// <summary>The glare: white at .75 to nothing at 46 % of the way to the card's far corner (the site's soft light,
        /// as plain white at about a third).</summary>
        public static readonly GradientStopCollection GlareStops = MakeGlare();

        static GradientStopCollection MakeGlare()
        {
            var s = new GradientStopCollection { new GradientStop(Color.FromArgb(0x55, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0x1C, 255, 255, 255), 0.55), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) };
            s.Freeze();
            return s;
        }

        /// <summary>A card's soft shadow: rings under its edge only (nothing under the face, which the sky shows through).</summary>
        public static void Shadow(DrawingContext dc, double w, double h, double cr, Inks ink)
        {
            var card = new RectangleGeometry(new Rect(0, 0, w, h), cr, cr);
            for (int i = 1; i <= 6; i++)
            {
                double grow = i * 1.6;
                var outer = new RectangleGeometry(new Rect(-grow, -grow + 3.2, w + 2 * grow, h + 2 * grow), cr + grow, cr + grow);
                var ring = new CombinedGeometry(GeometryCombineMode.Exclude, outer, card);
                dc.DrawGeometry(ink.Shade[i - 1], null, ring);
            }
        }

        /// <summary>What a card is printed with for one colouring of the skin.</summary>
        public sealed class Inks
        {
            public Pen Rule;
            public Brush Body, Stripe, Grain;
            public Brush[] Shade = new Brush[6];
            public GradientStopCollection Foil;
            public double Strength = 1, Linework = 0.85;
        }

        static Inks _inks;
        static string _for;

        public static Inks For(Skin s)
        {
            string key = s.Evening.ToString("0.000") + " " + s.Contrast;
            if (_inks != null && _for == key) return _inks;
            Color base_ = (Color)s["Panel"], edge = (Color)s["Line"], ink = (Color)s["Text"];
            bool high = s.Contrast == "high", calm = s.Contrast == "soft";
            var k = new Inks();
            k.Strength = high ? 0.6 : calm ? 1.25 : 1;
            k.Linework = high ? 0.5 : calm ? 1 : 0.85;
            k.Rule = new Pen(new SolidColorBrush(edge), 1);
            k.Rule.Freeze();
            Color pearl = Skin.Mix(base_, Colors.White, 0.25);
            k.Body = new SolidColorBrush(Color.FromArgb((byte)(high ? 0xE0 : calm ? 0xA8 : 0xBD), pearl.R, pearl.G, pearl.B));
            k.Body.Freeze();
            // the stripe: the five hues and back to pink, a third of it in the window, printed over the face (multiply)
            var st = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 3), MappingMode = BrushMappingMode.RelativeToBoundingBox, RelativeTransform = Shift };
            Color[] hues = Skin.Hues;
            for (int i = 0; i <= 5; i++) st.GradientStops.Add(new GradientStop(Skin.Mul(hues[i % 5], base_), i / 5.0));
            k.Stripe = st;
            // the foil: transparent 18 %, the hues at 30, 40, 50, 60 and 70 %, transparent 82 % (multiplied over the face)
            var f = new GradientStopCollection();
            f.Add(new GradientStop(Color.FromArgb(0, hues[0].R, hues[0].G, hues[0].B), 0.18));
            for (int i = 0; i < 5; i++) f.Add(new GradientStop(Skin.Mul(hues[i], base_), 0.30 + 0.10 * i));
            f.Add(new GradientStop(Color.FromArgb(0, hues[4].R, hues[4].G, hues[4].B), 0.82));
            f.Freeze();
            k.Foil = f;
            for (int i = 0; i < 6; i++)
            {
                k.Shade[i] = new SolidColorBrush(Color.FromArgb((byte)(high ? 10 : 7 - i), ink.R, ink.G, ink.B));
                k.Shade[i].Freeze();
            }
            // the grain: the 8x8 pattern's lower half a touch of white, its upper half a touch of ink (high contrast: none)
            if (!high)
            {
                uint light = (uint)(calm ? 0x2A : 0x1E) << 24 | 0xFFFFFFu, dark = (uint)(calm ? 0x16 : 0x0F) << 24 | ((uint)ink.R << 16) | ((uint)ink.G << 8) | ink.B;
                var px = new uint[64];
                for (int i = 0; i < 64; i++) px[i] = Velvet.B[i] < 32 ? light : dark;
                var bmp = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, px, 32);
                bmp.Freeze();
                var g = new ImageBrush(bmp) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(g, BitmapScalingMode.NearestNeighbor);
                g.Freeze();
                k.Grain = g;
            }
            _inks = k; _for = key;
            return k;
        }
    }
}
