using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>
    /// The live tabs (overgrow night, 2026-10-10): FOG, SUN, SSAO, DOF and LOOK's colour and exposure. Every row is a game
    /// dvar or a field of the game's fog record; ranges and defaults are the game's own (docs\study\bo2-redacted-dvars-
    /// 2026-09-29.tsv).
    /// </summary>
    public static class Panels
    {
        static double[] V(params double[] v) { return v; }

        public static KnobPanel Fog()
        {
            var p = new KnobPanel("FOG", "The map's fog, live: every move goes straight into the game's fog record. A film or map load puts " +
                                         "the map's own fog back; Reset does the same now. " +
                                         "Blend modes: fog mixed in by screen, multiply, overlay and more, seen at once; lock it in place to record it " +
                                         "(into the takes and the world pass, never the players pass).", "fog");
            p.Heading("Distance fog")
             .Fog("Start", "fog_start", 0, V(500), V(0), V(20000), tip: "Distance where the fog begins (game units)")
             .Fog("Fade (to full fog)", "fog_fade", 1, V(8000), V(1), V(100000), tip: "Distance over which it thickens to full")
             .Fog("Opacity", "fog_opacity", 11, V(0.8), V(0), V(1))
             .Fog("Colour (HDR)", "fog_colour", 8, V(1, 1, 1), V(0, 0, 0), V(15, 15, 15), colour: true)
             .Heading("Height fog")
             .Fog("Height start", "fog_height_start", 2, V(0), V(-3000), V(5000), tip: "Height where the fog begins")
             .Fog("Height fade", "fog_height_fade", 3, V(1000), V(0), V(10000))
             .Heading("Sun haze")
             .Fog("Haze colour (HDR)", "haze_colour", 12, V(1, 1, 1), V(0, 0, 0), V(15, 15, 15), colour: true)
             .Fog("Haze opacity", "haze_opacity", 15, V(1), V(0), V(1))
             .Fog("Haze pitch", "haze_pitch", 4, V(0), V(-90), V(90))
             .Fog("Haze yaw", "haze_yaw", 5, V(0), V(-180), V(360))
             .Fog("Haze inner radius", "haze_inner", 6, V(0), V(0), V(180))
             .Fog("Haze outer radius", "haze_outer", 7, V(40), V(0), V(180));
            BlendRows(p);

            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(Btn("Haze = fog colour", () =>
            {
                var fog = p.Find("Colour (HDR)"); p.Set(p.Find("Haze colour (HDR)"), (double[])fog.Values.Clone());
            }, "Sync the sun haze colour to the fog colour"));
            row.Children.Add(Btn("No fog", () => p.Set(p.Find("Opacity"), V(0))));
            row.Children.Add(Btn("Random fog", () =>
            {
                var r = new Random();
                double hue = r.NextDouble() * 360, sat = 0.2 + r.NextDouble() * 0.6, str = 2 + r.NextDouble() * 10;
                var c = ColourWheel.Hsv(hue, sat, 1).Select(x => x * str).ToArray();
                p.Set(p.Find("Start"), V(r.Next(0, 1500)));
                p.Set(p.Find("Fade (to full fog)"), V(r.Next(1500, 20000)));
                p.Set(p.Find("Opacity"), V(0.5 + r.NextDouble() * 0.5));
                p.Set(p.Find("Colour (HDR)"), c);
                p.Set(p.Find("Haze colour (HDR)"), ColourWheel.Hsv((hue + 30) % 360, sat, 1).Select(x => x * str * 1.2).ToArray());
                p.Set(p.Find("Haze outer radius"), V(r.Next(10, 90)));
            }, "A fog of random colour and distance; Reset puts the map's own back"));
            p.Below(row);
            var blend = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            blend.Children.Add(new TextBlock { Text = "Blend", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4), Foreground = Ui.Brush("Dim") });
            string[] modes = { "Normal", "Add", "Screen", "Multiply", "Overlay", "Soft light", "Colour dodge", "Colour burn" };
            for (int i = 0; i < modes.Length; i++)
            {
                int m = i;
                blend.Children.Add(Btn(modes[i], () => { p.Set(p.Find("Blend mode"), V(m)); }, "Fog with the " + modes[i].ToLowerInvariant() + " blend mode (turn on Fog with a blend mode below)"));
            }
            p.Below(blend);
            return p;
        }

        /// <summary>
        /// The blend-mode fog, a part of FOG (2026-10-17). Mycelium: "Lets see what we can do in game with a TEMP FOG pass that
        /// gets recorded to the World Pass when a User locks it in place", then: "The user shouldn't know its 'Temp FOG' -
        /// thats just for us to apply it over the game. To them, its just 'FOG' with blending modes." Not the map's fog
        /// record: RipperBridge (0.11.0-dev37 or newer) draws a fog of its own into the game's frame, by the view distance
        /// from the game's depth, in a blend mode the game never had. Shown live, left out of every recording until it is
        /// locked; locked, it goes into the takes and the world pass, never the players pass. Each row is a "bridge fog ..."
        /// line, so FOG's presets, moves and Copy as CFG carry it too, and Reset turns it off.
        /// </summary>
        private static void BlendRows(KnobPanel p)
        {
            p.Heading("Blend modes")
             .Toggle("Fog with a blend mode", "bridge fog on", "bridge fog off", null, "Mixes a fog in by distance, in the blend mode below; you see it at once").ReadBy(() => BlendFog("on"))
             .Toggle("Lock it in place: record it", "bridge fog lock on", "bridge fog lock off", null,
                     "Locked, it goes into every recording and the world pass (never the players pass); unlocked, it is only shown").ReadBy(() => BlendFog("locked"))
             .Whole("Blend mode", "bridge fog set mode={0}", null, 0, 0, 7, "0 normal, 1 add, 2 screen, 3 multiply, 4 overlay, 5 soft light, 6 colour dodge, 7 colour burn; or the Blend buttons above")
             .ReadBy(() => BlendFog("mode"))
             .Colour("Blend colour", "bridge fog set color={0},{1},{2}", null, V(140, 153, 178), 255).ReadBy(() => BlendFog("color"))
             .Add("Blend opacity", "bridge fog set opacity={0}", null, V(0.6), V(0), V(1), tip: "How much of the colour, where the fog is full").ReadBy(() => BlendFog("opacity"))
             .Whole("Blend curve", "bridge fog set curve={0}", null, 0, 0, 2, "0 straight from start to end; 1 exponential and 2 exponential squared, by density").ReadBy(() => BlendFog("curve"))
             .Add("Blend start", "bridge fog set start={0}", null, V(200), V(0), V(20000), tip: "Where it begins (game units from the camera)", curve: 2).ReadBy(() => BlendFog("start"))
             .Add("Blend end", "bridge fog set end={0}", null, V(3000), V(1), V(60000), tip: "Where the straight curve reaches full", curve: 2).ReadBy(() => BlendFog("end"))
             .Add("Blend density", "bridge fog set density={0}", null, V(0.0008), V(0), V(0.01), tip: "How fast the exponential curves thicken", curve: 2).ReadBy(() => BlendFog("density"))
             .Add("Blend sky", "bridge fog set sky={0}", null, V(0.5), V(0), V(1), tip: "How much of it covers the sky").ReadBy(() => BlendFog("sky"))
             .Toggle("Over the finished frame (after the game's bloom and colour grade)", "bridge fog where present", "bridge fog where scene", null,
                     "Off (the default): into the scene before the game's own bloom and grade, so they run on it too").ReadBy(() => BlendFog("where"));
        }

        // the bridge's "fog status", read once for all the rows of one read (half a second)
        private static string _fogStatus; private static DateTime _fogStatusAt;
        private static readonly string[] FogModeNames = { "normal", "add", "screen", "multiply", "overlay", "softlight", "dodge", "burn" };

        /// <summary>One of the blend-mode fog's values as the bridge holds it, or null (not connected, or a bridge without it).</summary>
        internal static double[] BlendFog(string key)
        {
            string s;
            lock (FogModeNames)
            {
                if (_fogStatus == null || (DateTime.Now - _fogStatusAt).TotalMilliseconds > 500)
                {
                    _fogStatus = BridgeClient.Ask("fog status", 800, 1);
                    _fogStatusAt = DateTime.Now;
                }
                s = _fogStatus;
            }
            if (s == null || !s.StartsWith("fog on", StringComparison.Ordinal)) return null;
            System.Text.RegularExpressions.Match m;
            switch (key)
            {
                case "color":
                    m = System.Text.RegularExpressions.Regex.Match(s, @" color (\d+),(\d+),(\d+)");
                    return m.Success ? new[] { double.Parse(m.Groups[1].Value), double.Parse(m.Groups[2].Value), double.Parse(m.Groups[3].Value) } : null;
                case "where": return new[] { s.Contains(" where present") ? 1.0 : 0.0 };
                case "curve":
                    m = System.Text.RegularExpressions.Regex.Match(s, @" curve (\w+)");
                    return m.Success ? new[] { m.Groups[1].Value == "exp2" ? 2.0 : m.Groups[1].Value == "exp" ? 1.0 : 0.0 } : null;
                case "mode":
                    m = System.Text.RegularExpressions.Regex.Match(s, @" mode (\w+)");
                    return m.Success ? new[] { (double)Math.Max(0, Array.IndexOf(FogModeNames, m.Groups[1].Value)) } : null;
                default:
                    m = System.Text.RegularExpressions.Regex.Match(s, @"(?:^fog | )" + key + @" (-?[\d.]+)");
                    double v;
                    return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? new[] { v } : null;
            }
        }

        public static KnobPanel Sun()
        {
            // the sun's lines go in as console commands: written into memory they black the frame out (measured 2026-10-01)
            var p = new KnobPanel("SUN", "The sun and the sky, live.", "sun");
            p.Heading("Sun")
             .Add("Sun light", "r_lightTweakSunLight {0}", "r_lightTweakSunLight", V(1), V(0), V(32))
             .Colour("Sun colour", "r_lightTweakSunColor {0} {1} {2}", "r_lightTweakSunColor", V(1, 1, 0), 1,
                     "The game keeps the colour's hue and rescales it to 0-1 (sent 3 1.2 0.2, read back 0.96 0.85 0.52); brightness is Sun light")
             .Add("Sun direction", "r_lightTweakSunDirection {0} {1} 0", "r_lightTweakSunDirection", V(0, 0), V(-90, -360), V(90, 360), new[] { "pitch", "yaw" },
                  "Where the light comes from (shadows move with it)")
             .Toggle("Remove the sun flare", "flareDisableEffects 1; r_superFlareDraw 0", "flareDisableEffects 0; r_superFlareDraw 1", "flareDisableEffects")
             .Whole("Sun shadow quality", "sm_sunQuality {0}", "sm_sunQuality", 0, 0, 2)
             .Heading("Sky")
             .Add("Sky colour temperature (K)", "r_skyColorTemp {0}", "r_skyColorTemp", V(6500), V(1650), V(25000))
             .Add("Sky brightness", "r_skyTransition 1; r_sky_intensity_factor0 {0}; r_sky_intensity_factor1 {0}", "r_sky_intensity_factor0", V(1), V(0), V(10))
             .Add("Sky rotation", "r_skyRotation {0}", "r_skyRotation", V(0), V(-360), V(360))
             .Toggle("Sky transition on", "r_skyTransition 1", "r_skyTransition 0", "r_skyTransition");
            return p;
        }

        public static KnobPanel Ssao()
        {
            var p = new KnobPanel("SSAO", "Ambient occlusion: the soft dark in corners and under things.", "ssao");
            p.Toggle("SSAO on", "r_ssao 1", "r_ssao 0", "r_ssao")
             .Add("Scale", "r_ssaoScale {0}", "r_ssaoScale", V(50), V(0), V(1000))
             .Add("Radius", "r_ssaoRadius {0}", "r_ssaoRadius", V(12), V(0), V(200))
             .Add("Bias", "r_ssaoBias {0}", "r_ssaoBias", V(0.55), V(0), V(5))
             .Add("Intensity", "r_ssaoIntensity {0}", "r_ssaoIntensity", V(0.53), V(0), V(1));
            return p;
        }

        public static KnobPanel Dof()
        {
            var p = new KnobPanel("DOF", "Depth of field from these values. The game ignores the values until the tweak is on: the switch " +
                                         "turns on both (r_dof_enable and r_dof_tweak).", "dof");
            // 2026-10-18 (Mycelium: "The doff doesn't work ... Oh, so it has to have depth of field with these values on"): a slider
            // moved turns the switch on by itself; "where's the blur amount?": the bias, first and named for what it does
            p.Toggle("Depth of field with these values", "r_dof_enable 1; r_dof_tweak 1", "r_dof_tweak 0", "r_dof_tweak")
             .SwitchedOnByMoves()
             .Add("Blur amount", "r_dof_bias {0}", "r_dof_bias", V(0.5), V(0.1), V(3), null, "How strongly the blur grows away from the sharp distance (r_dof_bias): higher is blurrier")
             .Heading("Near")
             .Add("Near blur", "r_dof_nearBlur {0}", "r_dof_nearBlur", V(6), V(4), V(10))
             .Add("Near start", "r_dof_nearStart {0}", "r_dof_nearStart", V(10), V(0), V(1000))
             .Add("Near end", "r_dof_nearEnd {0}", "r_dof_nearEnd", V(60), V(0), V(1000))
             .Heading("Far")
             .Add("Far blur", "r_dof_farBlur {0}", "r_dof_farBlur", V(1.8), V(0), V(10))
             .Add("Far start", "r_dof_farStart {0}", "r_dof_farStart", V(1000), V(0), V(20000))
             .Add("Far end", "r_dof_farEnd {0}", "r_dof_farEnd", V(7000), V(0), V(20000))
             .Heading("The gun (view model)")
             .Add("Gun start", "r_dof_viewModelStart {0}", "r_dof_viewModelStart", V(2), V(0), V(128))
             .Add("Gun end", "r_dof_viewModelEnd {0}", "r_dof_viewModelEnd", V(8), V(0), V(128))
             .Heading("Quality")
             .Whole("HDR depth of field", "r_dofHDR {0}", "r_dofHDR", 0, 0, 2);
            return p;
        }

        public static KnobPanel Look()
        {
            var p = new KnobPanel("LOOK", null, null);
            // Mycelium's round 1 (2026-10-15, 18:25): FOV is "the first place I would go to look". The same line as Theater's:
            // cg_fov for first person, and the free camera's own FOV through RipperBridge (dev20 writes it and holds it)
            p.Heading("Field of view")
             .Add("FOV", "cg_fov {0}; bridge dolly fov {0}", "cg_fov", V(65), V(20), V(160), tip: "First person and the free camera; Shift+scroll in the free camera zooms too")
             .Heading("Exposure")
             .Toggle("Exposure tweak on", "r_exposureTweak 1", "r_exposureTweak 0", "r_exposureTweak")
             .Add("Exposure (higher = darker)", "r_exposureTweak 1; r_exposureValue {0}", "r_exposureValue", V(3), V(-6), V(16))
             // NOT here (0271/0272, by picture): r_filmUseTweaks 1 blacks the frame whatever else is set; r_contrast,
             // r_desaturation, r_brightness and r_heroLightScale take the value and change nothing.
             .Heading("Colour control")
             .Toggle("Colour control on", "cg_useColorControl 1", "cg_useColorControl 0", "cg_useColorControl")
             .Add("Saturation", "cg_colorSaturation {0}", "cg_colorSaturation", V(1), V(0), V(1))
             .Colour("Colour scale", "cg_colorScale {0} {1} {2}", "cg_colorScale", V(1, 1, 1), 2)
             .Add("Hue", "cg_colorHue {0}", "cg_colorHue", V(0), V(0), V(360))
             .Add("Colour temperature (K)", "cg_colorTemp {0}", "cg_colorTemp", V(6500), V(1650), V(25000))
             .Heading("Light")
             // NOT here (sandbox 0448, 2026-10-16, by picture): r_lightTweakAmbient 0.1 and 4 drew the same frame on Raid -
             // Mycelium's round 1 (18:02): "Ambient light shows no change". An editor tweak; the light grid's below do act
             .Toggle("Light grid tweaks on", "r_lightGridEnableTweaks 1", "r_lightGridEnableTweaks 0", "r_lightGridEnableTweaks")
             .Add("Light grid intensity", "r_lightGridIntensity {0}", "r_lightGridIntensity", V(1), V(0), V(2))
             .Add("Light grid contrast", "r_lightGridContrast {0}", "r_lightGridContrast", V(0), V(-1), V(1));
            return p;
        }

        /// <summary>
        /// THEATER (overgrow night): only what changed the picture in the sandbox (0270-0274). Tried and NOT here, by picture:
        /// r_zbufferDebug, r_drawLightmapDrawlists, r_drawLitDrawlists, r_lightMapSecondary, fxfrustumCutoff, r_fog, r_drawWater,
        /// r_dobjLimit, cg_thirdPerson in free camera; r_skipPvs hid Carrier's world but not Rush's (unreliable). The depth
        /// pass is the BATCH tab's.
        /// </summary>
        public static KnobPanel Theater()
        {
            var p = new KnobPanel("THEATER", "Passes for the edit, live: switch on, record, switch off. Reset puts the game's own values back " +
                                             "(FOV too: the engine's 65, not your own setup).", null);
            p.Toggle("No HUD (hides the kill feed too)", "cg_draw2d 0", "cg_draw2d 1", null)
             .Toggle("No gun (first person)", "cg_drawGun 0", "cg_drawGun 1", "cg_drawGun", "Proven while the film plays (0274)")
             // the free camera ignores cg_fov (it has its own, theater + 0x8): RipperBridge dev19's "dolly fov" sets that one too
             // (Mycelium's round 1, 2026-10-15: "Why isn't the FOV applying?")
             .Add("FOV", "cg_fov {0}; bridge dolly fov {0}", "cg_fov", V(65), V(20), V(160), tip: "First person and the free camera")
             .Add("Blur (whole picture)", "r_blur {0}", "r_blur", V(0), V(0), V(32))
             .Add("Far clip (0 = off)", "r_zfar {0}", "r_zfar", V(0), V(0), V(16000), tip: "Nothing beyond this distance is drawn; the background colour shows. " +
                  "The slider is curved: most of its travel is the near distances, where the clip does its work", curve: 3)
             .Heading("Green sky")
             .Toggle("Sky off: the background colour shows (props that are models go too)", "r_modelLimit 0", "r_modelLimit 1024", null,
                     "r_modelLimit 0: the sky is a model; so are some props, like Carrier's distant ships (0272)")
             .Colour("Background colour", "r_clearColor {0} {1} {2} 1; r_clearColor2 {0} {1} {2} 1", "r_clearColor", V(0, 1, 0), 1);
            return p;
        }

        static Button Btn(string text, Action click, string tip = null)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 4), ToolTip = tip };
            b.Click += (s, e) => { try { click(); } catch (Exception ex) { App.Note(text, ex); } };
            return b;
        }
    }
}
