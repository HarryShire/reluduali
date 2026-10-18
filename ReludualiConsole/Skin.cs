using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ReludualiConsole
{
    /// <summary>
    /// The Designer's day and night (Mycelium, 2026-10-17): "Can you add a light mode to the Designer, use the exact scheme and
    /// design parameters, textures from the front page of The Reluduali Website. You can change the buttons to better fit
    /// the theme of the Reluduali Website. In fact, make it a dynamic theme. Light during the day, dark during the night. I
    /// really want to urge the dither texture and the printstream reflective design. If you can capture that right, just
    /// like the website front (card screen). It'll look amazing. Translucent sort of crystaline feel that the website has."
    ///
    /// - Light is reluduali.com's card screen (the site's skin: app/skin/skin.css and main.mjs). Behind everything the sky,
    ///   dithered as the site's world is: seven steps a channel through the 8x8 ordered pattern, in two-pixel cells. The
    ///   header and the rail are glass over it. The cards are printed like a polymer note (PrintCard.cs): a pearl face the
    ///   sky shows through, banknote linework, the holographic foil and the window stripe through the dither, a glare where
    ///   the pointer is, a double rule. Buttons are the site's: the pearl face and the plain edge, and the foil glints on
    ///   them under the pointer; the main action is the site's maroon. Titles are the cards' Georgia.
    /// - Dark is the velvet (Velvet.cs and its colours below): the Ableton plum under reluduali.com's velvet dither, the
    ///   foil on the edges, as it was.
    /// - Three settings (Mycelium, later the same evening: "Make three theme options, Dark, Light and Dynamic ... Dark and Light
    ///   are Static, Dynamic obviously does the change with the day"): Dark always the velvet; Light always the site's day
    ///   (light blue); Dynamic, the default: light while the Sun is up and through the evening until it is 6 degrees down (where the site's
    ///   own colours have turned all the way to its evening), dark through the night. The sky is this PC's (TheaterLaunch's
    ///   Sky: the time zone gives the place, the almanac's formulae the Sun), looked at every minute.
    ///   In Dynamic the light's colours follow the sky as the site's do: light blue by day, beige and orange in the evening.
    /// - Setup > Appearance: Dark, Light or Dynamic ("theme" in places.txt; an older "auto" reads as Dynamic) and the
    ///   contrast, both shown at once.
    ///
    /// Every brush in Theme.xaml takes its colour or picture from here by a binding ({local:Paint key}), so one change
    /// repaints every window, the pages built in code included. A turn between light and dark dissolves the old look away
    /// cell by cell in the dither's own order, as the site's white-out does.
    ///
    /// For pictures and tests the environment can set it for one run: RELUDUALI_SKIN (dynamic, light or dark),
    /// RELUDUALI_NIGHT (0 to 1, the sky: 0 day, 1 night), RELUDUALI_CONTRAST (soft, normal or high) and
    /// RELUDUALI_DISSOLVE_MS (how long the turn's dissolve takes).
    /// </summary>
    public sealed class Skin : INotifyPropertyChanged
    {
        /// <summary>The site's foil, in its order: pink, gold, mint, sky, violet (before Now: statics are made in order).</summary>
        internal static readonly Color[] Hues = { H(0xFF4FAE), H(0xFFD24F), H(0x4FFFC0), H(0x4FB4FF), H(0xB44FFF) };

        /// <summary>The one skin every brush reads (made before Theme.xaml's first brush).</summary>
        public static readonly Skin Now = new Skin();

        Dictionary<string, object> _v;
        double? _fixedNight;
        DispatcherTimer _clock;

        /// <summary>A colour, a picture, a font or a value by its key, for the theme's bindings.</summary>
        public object this[string key]
        {
            get
            {
                object v;
                if (_v != null && _v.TryGetValue(key, out v)) return v;
                return Colors.Magenta;           // a key the theme asks for and the skin lacks: plain to see in a picture
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Raised after every change (light or dark, the evening's colours, the contrast).</summary>
        public static event Action Changed;

        /// <summary>The light (the site's card screen) is showing; else the velvet.</summary>
        public bool IsLight { get; private set; }
        /// <summary>How far light's colours have gone toward the site's evening: 0 day (light blue), 1 beige and orange.</summary>
        public double Evening { get; private set; }
        /// <summary>The setting: "dynamic", "light" or "dark".</summary>
        public string Mode { get; private set; }
        /// <summary>"soft", "normal" or "high".</summary>
        public string Contrast { get; private set; }
        /// <summary>The sky over this PC: 0 with the Sun up, 1 once it is 6 degrees down (the site's nightness).</summary>
        public double Night { get; private set; }

        Skin()
        {
            Mode = Pick(Environment.GetEnvironmentVariable("RELUDUALI_SKIN"), null) ?? Pick(Saved("theme"), "dynamic");
            Contrast = Velvet.Contrast;
            string c = Environment.GetEnvironmentVariable("RELUDUALI_CONTRAST");
            if (c == "soft" || c == "normal" || c == "high") Contrast = c;
            double n;
            if (double.TryParse(Environment.GetEnvironmentVariable("RELUDUALI_NIGHT"), NumberStyles.Float, CultureInfo.InvariantCulture, out n))
                _fixedNight = Math.Max(0, Math.Min(1, n));
            Night = SkyNight();
            bool light; double evening;
            Decide(out light, out evening);
            IsLight = light; Evening = evening;
            _v = Build(light, evening, Contrast);
        }

        static string Pick(string v, string fallback)
        {
            v = (v ?? "").Trim().ToLowerInvariant();
            if (v == "auto") v = "dynamic";             // the evening it was first built, Dynamic was called Auto
            return v == "dynamic" || v == "light" || v == "dark" ? v : fallback;
        }

        static string Saved(string key)
        {
            try { return Where.Saved(key); } catch { return null; }
        }

        double SkyNight()
        {
            if (_fixedNight.HasValue) return _fixedNight.Value;
            try { return Sky.Now().Night; }
            catch (Exception ex) { App.Note("the skin's sky", ex); return 0; }
        }

        void Decide(out bool light, out double evening)
        {
            light = Mode == "light" || (Mode == "dynamic" && Night < 1);
            // Light is static: always the site's day. Dynamic: the evening's colours come on with the sky, in 24 steps, so
            // a picture is made at most every few minutes of a twilight
            evening = light && Mode == "dynamic" ? Math.Round(Night * 24) / 24 : 0;
        }

        // ------------------------------------------------------------------ changing it

        /// <summary>The clock: the sky looked at every minute (after the main window is up).</summary>
        public static void Start()
        {
            if (Now._clock != null) return;
            Now._clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(60) };
            Now._clock.Tick += (s, e) => Now.Refresh(true);
            Now._clock.Start();
        }

        /// <summary>Setup > Appearance: "dynamic", "light" or "dark", saved and shown at once.</summary>
        public void SetMode(string mode)
        {
            mode = Pick(mode, "dynamic");
            try { Where.Save("theme", mode); } catch (Exception ex) { App.Note("saving the theme", ex); }
            Mode = mode;
            Refresh(true);
        }

        /// <summary>For the self-test (--skin-test): a setting and a sky, nothing saved.</summary>
        internal void Test(string mode, double night, string contrast, bool animate)
        {
            Mode = Pick(mode, "dynamic");
            _fixedNight = Math.Max(0, Math.Min(1, night));
            Contrast = contrast;
            Refresh(animate, true);
        }

        /// <summary>Setup > Appearance: the contrast, saved and shown at once.</summary>
        public void SetContrast(string contrast)
        {
            if (contrast != "soft" && contrast != "high") contrast = "normal";
            try { Where.Save("contrast", contrast); } catch (Exception ex) { App.Note("saving the contrast", ex); }
            Contrast = contrast;
            Refresh(true, true);
        }

        /// <summary>Looks at the sky again and changes what has changed.</summary>
        public void Refresh(bool animate, bool force = false)
        {
            Night = SkyNight();
            bool light; double evening;
            Decide(out light, out evening);
            if (!force && light == IsLight && Math.Abs(evening - Evening) < 1e-6) { Raise("Night"); return; }
            bool turn = light != IsLight;
            Action apply = () =>
            {
                _v = Build(light, evening, Contrast);
                IsLight = light; Evening = evening;
                Raise("Item[]");
                Raise(string.Empty);
                Fonts();
                PrintCard.All();
                foreach (Window w in Application.Current.Windows) Native.Frame(w);
                var h = Changed; if (h != null) h();
            };
            App.Note("the skin: " + (light ? "light, evening " + evening.ToString("0.00", CultureInfo.InvariantCulture) : "dark") + " (" + Mode + ", night "
                     + Night.ToString("0.00", CultureInfo.InvariantCulture) + ", contrast " + Contrast + ")", null);
            if (turn && animate && !App.Tour) Dissolve.Over(apply);
            else apply();
        }

        void Raise(string name) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(name)); }

        /// <summary>The titles' face as an application resource, for the pages that ask for it with DynamicResource
        /// (BatchCore's BATCH page): Georgia by day, as the site's cards; Segoe UI by night.</summary>
        public static void Fonts()
        {
            var app = Application.Current;
            if (app != null) app.Resources["TitleFont"] = Now["TitleFont"];
        }

        /// <summary>When the Designer turns dark next or light next, for Setup's line: null when it stays as it is.</summary>
        public string NextTurn()
        {
            if (Mode != "dynamic" || _fixedNight.HasValue) return null;
            try
            {
                double lat, lon;
                Sky.Place(out lat, out lon);
                DateTime now = DateTime.UtcNow;
                bool dark = !IsLight;
                for (int m = 1; m <= 36 * 60; m++)
                {
                    DateTime t = now.AddMinutes(m);
                    bool d = Sky.FromAltitude(Sky.SunAltitude(t, lat, lon)).Night >= 1;
                    if (d != dark) return (d ? "dark from " : "light from ") + t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex) { App.Note("the skin's next turn", ex); }
            return null;
        }

        // ------------------------------------------------------------------ the palettes

        static Color H(uint rgb) { return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); }
        static Color A(Color c, byte a) { return Color.FromArgb(a, c.R, c.G, c.B); }
        internal static Color Mix(Color a, Color b, double t)
        {
            Func<byte, byte, byte> m = (x, y) => (byte)Math.Round(x + (y - x) * t);
            return Color.FromArgb(m(a.A, b.A), m(a.R, b.R), m(a.G, b.G), m(a.B, b.B));
        }
        /// <summary>What a colour printed over another looks like (the site's mix-blend-mode multiply).</summary>
        internal static Color Mul(Color a, Color b) { return Color.FromArgb(a.A, (byte)(a.R * b.R / 255), (byte)(a.G * b.G / 255), (byte)(a.B * b.B / 255)); }
        static double Smooth(double a, double b, double x) { double t = Math.Max(0, Math.Min(1, (x - a) / (b - a))); return t * t * (3 - 2 * t); }

        static Dictionary<string, object> Build(bool light, double evening, string contrast)
        {
            return light ? Light(evening, contrast) : Dark(contrast);
        }

        static void Stops(Dictionary<string, object> d, string name, params Color[] c) { for (int i = 0; i < c.Length; i++) d[name + i] = c[i]; }
        static void Stops(Dictionary<string, object> d, string name, params uint[] c) { for (int i = 0; i < c.Length; i++) d[name + i] = Velvet.C(c[i]); }

        /// <summary>The velvet: the theme as it was before the day came (2026-10-17's Ableton look), every value kept.</summary>
        static Dictionary<string, object> Dark(string c)
        {
            var d = new Dictionary<string, object>();
            Action<string, uint> k = (key, argb) => d[key] = Velvet.C(argb);
            d["Back"] = Velvet.BackFor(c);
            k("Panel", 0xFF372938); k("Raised", 0xFF443547);
            d["Inset"] = Velvet.InsetFor(c); d["Line"] = Velvet.BorderFor(c);
            d["Text"] = Velvet.TextFor(c); d["Dim"] = Velvet.DimFor(c); d["Chrome"] = Velvet.ChromeFor(c);
            k("Accent", 0xFFBF90C6); k("AccentPale", 0xFFDCC3E0); k("AccentDeep", 0xFF99869C);
            k("Good", 0xFF3FB39A); k("Bad", 0xFFE0336F); k("Warn", 0xFFE8862E);
            k("Hover", 0x14FFFFFF); k("Selected", 0xFF99869C); k("OnSelected", 0xFF221823); k("OnPrimary", 0xFF221823); k("Focus", 0xFFE07020);
            k("Veil", 0x00FFFFFF); k("VeilOver", 0x1CFFFFFF); k("VeilDown", 0x38000000); k("Sheen", 0x18FFFFFF); k("SheenOver", 0x30FFFFFF);
            k("ComboOver", 0x16FFFFFF); k("RowOver", 0x14FFFFFF); k("RailOver", 0x12FFFFFF);
            k("CheckMark", 0xFFFFFFFF); k("CheckMixed", 0x66544457); k("RadioEdge", 0xFF6E5D71); k("RadioDot", 0xFFF6F0F7);
            k("FieldOver", 0xFF6E5D71); k("ThumbOver", 0xFF7A687D);
            k("QuietOn", 0x40BF90C6); k("QuietOnEdge", 0x99BF90C6);
            k("SliderRest", 0xFF1E1620); k("SliderRestEdge", 0x44544457); k("KnobGlow", 0x55BF90C6); k("KnobEdge", 0xFF6E5A72);
            k("Knob0", 0xFFF7F2FF); k("Knob1", 0xFFE3CCF5); k("Knob2", 0xFFC9A6E6); k("Knob3", 0xFFB98AE0);
            k("TipBack", 0xF2312533); k("Gripper", 0x55544457); k("Splitter", 0x44544457); k("OnDanger", 0xFFFFFFFF);
            k("Face0", 0xFF4A3B4D); k("Face1", 0xFF443547); k("Face2", 0xFF3E3041);
            k("Primary0", 0xFFCDA3D3); k("Primary1", 0xFFBF90C6); k("Primary2", 0xFFB383BA);
            k("Danger0", 0xFFCC3468); k("Danger1", 0xFFBF245D); k("Danger2", 0xFFAA1F52);
            // the selection's marker, the theme's orange from top to foot (five stops: light's holographic one fits the same brush)
            Color o0 = Velvet.C(0xFFF08A3C), o1 = Velvet.C(0xFFDB490E);
            Stops(d, "Bar", o0, Mix(o0, o1, 0.25), Mix(o0, o1, 0.5), Mix(o0, o1, 0.75), o1);
            k("Fill0", 0xFF99869C); k("Fill1", 0xFFBF90C6);
            Stops(d, "Rim", 0xA0D9CCDD, 0x90FF6FBE, 0x80FFD86A, 0x786FFFCB, 0x806FC0FF, 0x90BE6AFF, 0xA0D9CCDD);
            Stops(d, "RimSoft", 0x70C9BBCC, 0x58FF4FAE, 0x48FFD24F, 0x404FFFC0, 0x484FB4FF, 0x58B44FFF, 0x70C9BBCC);
            Stops(d, "RimHi", 0xFFF6F0F7, 0xFFFF4FAE, 0xFFFFD24F, 0xFF4FFFC0, 0xFF4FB4FF, 0xFFB44FFF, 0xFFF6F0F7);
            Stops(d, "PrimaryRim", 0xFFF6F0F7, 0xFFFF8CC9, 0xFFFFE39A, 0xFF9AFFDB, 0xFF9AD2FF, 0xFFD59AFF, 0xFFF6F0F7);
            Stops(d, "RimLine", 0x40544457, 0xA0FF4FAE, 0xC0F6F0F7, 0xA0FFD24F, 0x904FFFC0, 0x904FB4FF, 0xA0B44FFF, 0x40544457);
            Stops(d, "Glint", 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u);       // the light's foil on a button: none at night
            k("TlFill", 0x96BF90C6); k("TlHead", 0xFFFFFFFF); k("TlTarget", 0xFFF08A24); k("TlMark", 0xFF78DCFF); k("TlKey", 0xC8E9D5FF);
            k("MvText", 0xFFDCDCDC); k("MvDot", 0xFFFFFFFF); k("MvNow", 0xFFFF4500);
            k("Plate", 0x00000000); k("PlateEdge", 0x00000000);
            d["PlateMargin"] = new Thickness(26, 18, 24, 6); d["PlatePad"] = new Thickness(0); d["PlateLine"] = new Thickness(0);
            k("Seed", 0xFFBF90C6);
            d["TitleFont"] = new FontFamily("Segoe UI");
            d["KickerFont"] = new FontFamily("Segoe UI");
            d["TitleWeight"] = FontWeights.SemiBold;
            d["BigWeight"] = FontWeights.Light;
            d["Day"] = Visibility.Collapsed; d["Night"] = Visibility.Visible;
            d["Deep"] = Velvet.RailFor(c); d["Rail"] = Velvet.RailFor(c); d["Page"] = Velvet.DeepFor(c);
            d["Card"] = Velvet.CardFor(c); d["Pop"] = Velvet.CardFor(c); d["View"] = Velvet.ViewFor(c); d["Slot"] = Velvet.ViewFor(c);
            d["ViewText"] = Velvet.DimFor(c);
            return d;
        }

        /// <summary>The site's card screen: its colours (main.mjs: CSS_DAY to CSS_NIGHT by the sky), and the Designer's
        /// pieces made from them.</summary>
        static Dictionary<string, object> Light(double e, string c)
        {
            var d = new Dictionary<string, object>();
            Color base_ = Mix(H(0xEEF6FD), H(0xF1E3CC), e), ink = Mix(H(0x0B2A4A), H(0x3A0716), e), edge = Mix(H(0x9FC2E0), H(0xC58A55), e),
                  soft = Mix(H(0x4A6A8A), H(0x8A5A3A), e), seed = Mix(H(0x5C0F2E), H(0xE2873C), e), maroon = H(0x5C0F2E),
                  pink = Mix(H(0xFF4FAE), H(0xFF7FC0), e), white = Colors.White, black = Colors.Black;
            bool high = c == "high", calm = c == "soft";
            Color text = high ? Mix(ink, black, 0.35) : calm ? Mix(ink, soft, 0.18) : ink;
            Color dim = high ? Mix(soft, ink, 0.62) : calm ? soft : Mix(soft, ink, 0.32);
            Color line = high ? Mix(edge, ink, 0.42) : calm ? Mix(edge, base_, 0.12) : edge;
            Color accent = high ? Mix(maroon, black, 0.2) : maroon;

            d["Back"] = base_; d["Panel"] = base_; d["Raised"] = Mix(base_, white, 0.45); d["Inset"] = Mix(base_, white, 0.62);
            d["Line"] = line; d["Text"] = text; d["Dim"] = dim; d["Chrome"] = high ? Mix(ink, black, 0.5) : Mix(ink, black, 0.12);
            d["Accent"] = accent; d["AccentPale"] = Mix(maroon, pink, 0.3); d["AccentDeep"] = edge;
            d["Good"] = H(0x17876A); d["Bad"] = H(0xC0245D); d["Warn"] = H(0xB05A14);
            d["Hover"] = A(ink, 0x10); d["Selected"] = Mix(base_, edge, 0.55); d["OnSelected"] = text; d["OnPrimary"] = H(0xFDF0F5); d["Focus"] = maroon;
            d["Veil"] = A(ink, 0); d["VeilOver"] = A(ink, 0x10); d["VeilDown"] = A(ink, 0x24); d["Sheen"] = A(white, 0xB0); d["SheenOver"] = A(white, 0xE6);
            d["ComboOver"] = A(ink, 0x0C); d["RowOver"] = A(ink, 0x10); d["RailOver"] = A(ink, 0x10);
            d["CheckMark"] = white; d["CheckMixed"] = A(edge, 0x99); d["RadioEdge"] = Mix(edge, ink, 0.3); d["RadioDot"] = H(0xFDF0F5);
            d["FieldOver"] = Mix(edge, ink, 0.35); d["ThumbOver"] = Mix(edge, ink, 0.35);
            d["QuietOn"] = A(edge, 0x70); d["QuietOnEdge"] = Mix(edge, ink, 0.25);
            d["SliderRest"] = Mix(base_, edge, 0.6); d["SliderRestEdge"] = A(white, 0x99); d["KnobGlow"] = A(maroon, 0x40); d["KnobEdge"] = Mix(edge, ink, 0.35);
            d["Knob0"] = white; d["Knob1"] = Mix(base_, white, 0.5); d["Knob2"] = Mix(base_, edge, 0.15); d["Knob3"] = Mix(base_, edge, 0.35);
            d["TipBack"] = A(Mix(base_, white, 0.3), 0xF5); d["Gripper"] = A(edge, 0xAA); d["Splitter"] = A(edge, 0x99); d["OnDanger"] = white;
            // a button: the site's chip, a pearl face and the plain edge (the foil comes on under the pointer: Glint)
            d["Face0"] = Mix(base_, white, 0.8); d["Face1"] = Mix(base_, white, 0.45); d["Face2"] = Mix(base_, edge, 0.12);
            // the main action: the site's maroon (its focus and the cards' foot)
            d["Primary0"] = Mix(maroon, pink, 0.12); d["Primary1"] = maroon; d["Primary2"] = Mix(maroon, black, 0.18);
            d["Danger0"] = Velvet.C(0xFFCC3468); d["Danger1"] = Velvet.C(0xFFBF245D); d["Danger2"] = Velvet.C(0xFFAA1F52);
            // the selection's marker: the polymer note's window, the five hues printed over the edge's blue
            for (int i = 0; i < 5; i++) d["Bar" + i] = Mul(Hues[i], edge);
            d["Fill0"] = maroon; d["Fill1"] = Mix(maroon, pink, 0.55);
            // edges: the site's plain edge on buttons, fields and cards; the foil where something is lit (RimHi) or is the
            // main action (PrimaryRim, on the maroon), and along the header's foot
            for (int i = 0; i < 7; i++) { d["Rim" + i] = line; d["RimSoft" + i] = line; }
            Color[] lit = new Color[5];
            for (int i = 0; i < 5; i++) lit[i] = Mul(Hues[i], edge);
            Stops(d, "RimHi", Mix(edge, ink, 0.2), lit[0], lit[1], lit[2], lit[3], lit[4], Mix(edge, ink, 0.2));
            Stops(d, "PrimaryRim", 0xFFF6F0F7, 0xFFFF8CC9, 0xFFFFE39A, 0xFF9AFFDB, 0xFF9AD2FF, 0xFFD59AFF, 0xFFF6F0F7);
            Stops(d, "RimLine", A(edge, 0x70), A(lit[0], 0xC0), A(white, 0xD0), A(lit[1], 0xC0), A(lit[2], 0xB0), A(lit[3], 0xB0), A(lit[4], 0xC0), A(edge, 0x70));
            // the site's foil (linear-gradient 115deg: transparent 18%, the hues 30-70%, transparent 82%), multiplied over the face
            Color[] g = new Color[5];
            for (int i = 0; i < 5; i++) g[i] = Mul(Hues[i], base_);
            Stops(d, "Glint", A(g[0], 0), A(g[0], 0), g[0], g[1], g[2], g[3], g[4], A(g[4], 0), A(g[4], 0));
            d["TlFill"] = A(Mix(edge, maroon, 0.25), 0xB0); d["TlHead"] = ink; d["TlTarget"] = H(0xD9661F); d["TlMark"] = H(0x1F6FBF); d["TlKey"] = A(maroon, 0xB0);
            d["MvText"] = text; d["MvDot"] = maroon; d["MvNow"] = H(0xD9541F);
            // under a page's title: a pane of glass, as the site's chips and hints sit on
            d["Plate"] = A(base_, 0xA8); d["PlateEdge"] = A(white, 0x90);
            d["PlateMargin"] = new Thickness(16, 12, 14, 0); d["PlatePad"] = new Thickness(10, 6, 10, 6); d["PlateLine"] = new Thickness(1);
            d["Seed"] = seed;
            d["TitleFont"] = new FontFamily("Georgia");
            d["KickerFont"] = new FontFamily("Cascadia Mono, Consolas");      // the cards' kicker: ui-monospace capitals
            d["TitleWeight"] = FontWeights.Bold;
            d["BigWeight"] = FontWeights.Bold;
            d["Day"] = Visibility.Visible; d["Night"] = Visibility.Collapsed;
            // the surfaces: the sky behind it all, glass for the header and the rail, the pearl face
            d["Deep"] = SkyImage(e, c);
            d["Rail"] = GlassImage(base_, high ? 0.9 : calm ? 0.66 : 0.76);
            d["Page"] = Plain(Colors.Transparent);
            d["Card"] = Plain(A(base_, (byte)(high ? 0xF0 : calm ? 0xD8 : 0xE2)));
            d["Pop"] = Plain(Mix(base_, white, 0.2));
            d["View"] = Velvet.ViewFor(c);
            d["Slot"] = Plain(A(base_, (byte)(high ? 0xF0 : 0xD2)));
            d["ViewText"] = Mix(edge, white, 0.25);
            return d;
        }

        // ------------------------------------------------------------------ light's surfaces

        /// <summary>The sky, as the site's world draws it looking ahead (scene.mjs: sky()), from up at the window's top down
        /// to the horizon at its foot: day from #7fb0dd to #eaf6ff, evening from #e7d3b8 to #e2873c; then every channel on
        /// eight levels through the 8x8 ordered pattern, a cell to every two pixels (an old Mac's dithering).</summary>
        static BitmapSource SkyImage(double e, string c)
        {
            const int W = Velvet.Tile, Ht = 2160;
            const double Top = 0.26, Foot = 0.02;            // how far up the window's top and foot look
            int levels = c == "high" ? 15 : 7;               // high contrast: a quieter dither
            Color skyTop = H(0x7FB0DD), skyLow = H(0xEAF6FF), dusk = H(0xE2873C), duskHi = H(0xE7D3B8);
            var px = new uint[W * Ht];
            int ch = Ht / 2, cw = W / 2;
            for (int y = 0; y < ch; y++)
            {
                double el = Top - (Top - Foot) * y / (ch - 1.0);
                Color day = Mix(skyLow, skyTop, Smooth(-0.02, 0.55, el));
                Color night = Mix(dusk, duskHi, Smooth(-0.02, 0.32, el));
                Color col = Mix(day, night, e);
                for (int x = 0; x < cw; x++)
                {
                    double b = (Velvet.B[(y & 7) * 8 + (x & 7)] + 0.5) / 64.0;
                    uint v = 0xFF000000u | ((uint)Q(col.R, levels, b) << 16) | ((uint)Q(col.G, levels, b) << 8) | Q(col.B, levels, b);
                    int o = (y * 2) * W + x * 2;
                    px[o] = v; px[o + 1] = v; px[o + W] = v; px[o + W + 1] = v;
                }
            }
            var bmp = BitmapSource.Create(W, Ht, 96, 96, PixelFormats.Bgra32, null, px, W * 4);
            bmp.Freeze();
            return bmp;
        }

        static byte Q(byte v, int levels, double b)
        {
            double q = Math.Floor(v / 255.0 * levels + b) / levels;
            return (byte)Math.Round(255 * Math.Min(1, q));
        }

        /// <summary>Glass for the header and the rail: the pearl, a little clearer toward the foot, the sky's dither behind.</summary>
        static BitmapSource GlassImage(Color c, double alpha)
        {
            const int W = 8, Ht = 2160;
            var px = new uint[W * Ht];
            for (int y = 0; y < Ht; y++)
            {
                double a = alpha * (1.06 - 0.12 * y / (Ht - 1.0));
                uint v = ((uint)Math.Round(255 * Math.Max(0, Math.Min(1, a))) << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
                for (int x = 0; x < W; x++) px[y * W + x] = v;
            }
            var bmp = BitmapSource.Create(W, Ht, 96, 96, PixelFormats.Bgra32, null, px, W * 4);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>One colour as a picture (the theme's surfaces are pictures, so a colour can stand in for one).</summary>
        static BitmapSource Plain(Color c)
        {
            const int W = 8;
            var px = new uint[W * W];
            uint v = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
            for (int i = 0; i < px.Length; i++) px[i] = v;
            var bmp = BitmapSource.Create(W, W, 96, 96, PixelFormats.Bgra32, null, px, W * 4);
            bmp.Freeze();
            return bmp;
        }
    }

    /// <summary>{local:Paint Key} in Theme.xaml: a binding to the skin's value for Key, so the brush follows day and night.</summary>
    [MarkupExtensionReturnType(typeof(object))]
    public sealed class PaintExtension : MarkupExtension
    {
        public PaintExtension() { }
        public PaintExtension(string key) { Key = key; }
        [ConstructorArgument("key")]
        public string Key { get; set; }

        public override object ProvideValue(IServiceProvider sp)
        {
            var b = new Binding("[" + Key + "]") { Source = Skin.Now, Mode = BindingMode.OneWay };
            return b.ProvideValue(sp);
        }

        /// <summary>The same binding for code: a page's piece that follows the skin.</summary>
        public static void Bind(DependencyObject o, DependencyProperty p, string key)
        {
            BindingOperations.SetBinding(o, p, new Binding("[" + key + "]") { Source = Skin.Now, Mode = BindingMode.OneWay });
        }
    }

    /// <summary>
    /// The turn between light and dark (the site's main.mjs whiteOut: "an old Mac's dissolve, cell by cell in the dither's
    /// order"): each window's look as it was is laid over it, the new look goes on under it, and the old one is taken away
    /// in the 8x8 ordered pattern's order, three pixels a cell, in a little over half a second.
    /// </summary>
    static class Dissolve
    {
        const int Cell = 3;
        // a little over half a second (RELUDUALI_DISSOLVE_MS slows it, for pictures of it)
        static readonly int Ms = Slow();
        static int Slow() { int ms; return int.TryParse(Environment.GetEnvironmentVariable("RELUDUALI_DISSOLVE_MS"), out ms) && ms >= 100 && ms <= 20000 ? ms : 560; }
        static ImageBrush[] _masks;

        static ImageBrush[] Masks()
        {
            if (_masks != null) return _masks;
            var m = new ImageBrush[65];
            for (int k = 0; k <= 64; k++)
            {
                const int S = 8 * Cell;
                var px = new uint[S * S];
                for (int i = 0; i < 64; i++)
                {
                    if (Velvet.B[i] < k) continue;                // gone: the new look shows through here
                    int cx = (i % 8) * Cell, cy = (i / 8) * Cell;
                    for (int y = 0; y < Cell; y++) for (int x = 0; x < Cell; x++) px[(cy + y) * S + cx + x] = 0xFF000000u;
                }
                var bmp = BitmapSource.Create(S, S, 96, 96, PixelFormats.Bgra32, null, px, S * 4);
                bmp.Freeze();
                var b = new ImageBrush(bmp) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, S, S), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(b, BitmapScalingMode.NearestNeighbor);
                b.Freeze();
                m[k] = b;
            }
            return _masks = m;
        }

        sealed class Veil : Adorner
        {
            readonly BitmapSource _was;
            public int Step;
            public Veil(UIElement on, BitmapSource was) : base(on) { _was = was; IsHitTestVisible = false; }
            protected override void OnRender(DrawingContext dc)
            {
                if (Step >= 64) return;
                dc.PushOpacityMask(Masks()[Step]);
                dc.DrawImage(_was, new Rect(0, 0, AdornedElement.RenderSize.Width, AdornedElement.RenderSize.Height));
                dc.Pop();
            }
        }

        /// <summary>Takes a picture of every window, makes the change, then dissolves the pictures away.</summary>
        public static void Over(Action change)
        {
            var veils = new List<KeyValuePair<AdornerLayer, Veil>>();
            try
            {
                foreach (Window w in Application.Current.Windows)
                {
                    var root = w.Content as FrameworkElement;
                    if (root == null || !w.IsVisible || w.WindowState == WindowState.Minimized || root.ActualWidth < 2 || root.ActualHeight < 2) continue;
                    var layer = AdornerLayer.GetAdornerLayer(root);
                    if (layer == null) continue;
                    double sx = 1, sy = 1;
                    var src = PresentationSource.FromVisual(root);
                    if (src != null && src.CompositionTarget != null) { sx = src.CompositionTarget.TransformToDevice.M11; sy = src.CompositionTarget.TransformToDevice.M22; }
                    int pw = (int)Math.Ceiling(root.ActualWidth * sx), ph = (int)Math.Ceiling(root.ActualHeight * sy);
                    if ((long)pw * ph > 40000000) continue;
                    var bmp = new RenderTargetBitmap(pw, ph, 96 * sx, 96 * sy, PixelFormats.Pbgra32);
                    bmp.Render(root);
                    bmp.Freeze();
                    var v = new Veil(root, bmp);
                    layer.Add(v);
                    veils.Add(new KeyValuePair<AdornerLayer, Veil>(layer, v));
                }
            }
            catch (Exception ex) { App.Note("the skin's dissolve (its pictures)", ex); }
            change();
            if (veils.Count == 0) return;
            DateTime t0 = DateTime.Now;
            var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
            timer.Tick += (s, e) =>
            {
                double f = (DateTime.Now - t0).TotalMilliseconds / Ms;
                int k = (int)Math.Round(64 * Math.Min(1, f));
                foreach (var kv in veils) { kv.Value.Step = k; kv.Value.InvalidateVisual(); }
                if (f < 1) return;
                timer.Stop();
                foreach (var kv in veils) try { kv.Key.Remove(kv.Value); } catch { }
            };
            timer.Start();
        }
    }

    /// <summary>
    /// ReludualiConsole.exe --skin-test OUTDIR: day and night's checks, with no game and nothing on the screen (a window
    /// off the screen, never shown in the taskbar): Dynamic's choice at five heights of the Sun and the two static settings,
    /// the brushes repainting in place, the contrast, the cards drawn both ways, and the dissolve between them, its frames
    /// as pictures. checks.txt says what passed; the exit code is 0 when all of it did.
    /// </summary>
    static class SkinTest
    {
        public static int Run(string dir)
        {
            System.IO.Directory.CreateDirectory(dir);
            var log = new System.Text.StringBuilder();
            int bad = 0;
            Action<string, bool> check = (what, ok) => { log.AppendLine((ok ? "ok    " : "FAIL  ") + what); if (!ok) bad++; };
            Skin s = Skin.Now;
            var inv = CultureInfo.InvariantCulture;
            // Dynamic's choice: light while the Sun is up and through the evening, dark from 6 degrees down; Light and Dark
            // static whatever the sky (Light always the day's colours)
            foreach (object[] t in new[] { new object[] { "dynamic", 0.0, true, 0.0 }, new object[] { "dynamic", 0.5, true, 0.5 }, new object[] { "dynamic", 0.99, true, 1.0 },
                                           new object[] { "dynamic", 1.0, false, 0.0 }, new object[] { "light", 1.0, true, 0.0 }, new object[] { "light", 0.5, true, 0.0 },
                                           new object[] { "dark", 0.0, false, 0.0 }, new object[] { "auto", 0.25, true, 0.25 } })
            {
                s.Test((string)t[0], (double)t[1], "normal", false);
                check(string.Format(inv, "{0} with the sky at {1:0.00}: {2} (evening {3:0.00})", t[0], t[1], s.IsLight ? "light" : "dark", s.Evening),
                      s.IsLight == (bool)t[2] && Math.Abs(s.Evening - (double)t[3]) < 1e-9);
            }
            // the brushes repaint in place: the theme's brush objects are the same, their colours follow
            s.Test("dark", 0, "normal", false);
            var text = (SolidColorBrush)Ui.Brush("Text");
            var back = (ImageBrush)Ui.Brush("VelvetDeep");
            Color nightText = text.Color;
            ImageSource nightBack = back.ImageSource;
            s.Test("light", 0, "normal", false);
            check("Text, night " + nightText + " to day " + text.Color + " (the site's ink #FF0B2A4A)", nightText == Velvet.TextFor("normal") && text.Color == Color.FromRgb(0x0B, 0x2A, 0x4A));
            check("the window's ground: night the velvet, day the sky", nightBack == Velvet.RailFor("normal") && back.ImageSource != nightBack);
            check("the theme's brushes are live (not frozen)", !text.IsFrozen && !back.IsFrozen && !((Brush)Ui.Brush("Rim")).IsFrozen);
            check("the titles' face by day: Georgia", ((FontFamily)s["TitleFont"]).Source == "Georgia");
            s.Test("light", 0, "high", false);
            Color high = text.Color;
            check("high contrast by day: darker ink " + high, high.R + high.G + high.B < 0x0B + 0x2A + 0x4A);
            s.Test("dynamic", 0.5, "normal", false);
            check("half way into the evening: the ink toward the site's night maroon " + text.Color, text.Color.R > 0x0B && text.Color.B < 0x4A);
            string next = null;
            s.Test("dynamic", 0, "normal", false);
            try { next = s.NextTurn(); } catch { }
            check("Setup's line with a fixed sky: no next turn (" + (next ?? "none") + ")", next == null);

            // the dissolve, in a window off the screen: night, then day with the dissolve, then night again without it
            s.Test("dark", 0, "normal", false);
            var w = new Window { Width = 560, Height = 380, Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize };
            var page = new StackPanel { Margin = new Thickness(16) };
            page.Children.Add(Ui.Card("A card", "Printed by day, velvet by night.", Ui.Row(Ui.Primary(Ui.Button("The main action", () => { })), Ui.Button("Another", () => { })),
                                      new CheckBox { Content = "A box", IsChecked = true }, new Slider { Width = 200, Value = 4 }));
            page.Children.Add(Ui.Card("Another card", null, Ui.Note("Quiet words under it.")));
            var root = new Grid { Background = Ui.Brush("VelvetDeep") };
            root.Children.Add(page);
            w.Content = root;
            w.Show();
            GuiTour.Pump(400);
            Shot(w, dir, "0-night");
            s.Test("light", 0, "normal", true);
            var layer = AdornerLayer.GetAdornerLayer(root);
            check("the dissolve's veil is laid over the window", layer != null && layer.GetAdorners(root) != null && layer.GetAdorners(root).Length == 1);
            for (int i = 1; i <= 4; i++) { GuiTour.Pump(110); Shot(w, dir, i + "-dissolving"); }
            GuiTour.Pump(700);
            Shot(w, dir, "5-day");
            check("the veil is gone when it has dissolved", layer == null || layer.GetAdorners(root) == null);
            s.Test("dynamic", 0.75, "normal", false);
            GuiTour.Pump(200);
            Shot(w, dir, "6-evening");
            s.Test("dark", 0, "normal", false);
            GuiTour.Pump(200);
            Shot(w, dir, "7-night-again");
            w.Close();
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "checks.txt"), log.ToString() + (bad == 0 ? "ALL PASS" : bad + " FAILED") + Environment.NewLine);
            return bad == 0 ? 0 : 1;
        }

        static void Shot(Window w, string dir, string name)
        {
            var bmp = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(w);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = System.IO.File.Create(System.IO.Path.Combine(dir, name + ".png"))) enc.Save(f);
        }
    }
}

