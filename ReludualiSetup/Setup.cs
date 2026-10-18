using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

[assembly: AssemblyTitle("The Reluduali Designer Setup")]
[assembly: AssemblyProduct("The Reluduali Designer")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Mycelium")]
[assembly: AssemblyVersion("0.7.2.0")]
[assembly: AssemblyFileVersion("0.7.2.0")]

namespace ReludualiSetup
{
    /// <summary>
    /// ReludualiSetup.exe: the window, or a run without one.
    ///   ReludualiSetup.exe                               the window
    ///   ReludualiSetup.exe /uninstall [/silent]          (as "Uninstall Reluduali.exe" in the install folder)
    ///   ReludualiSetup.exe /silent /redacted DIR [/to DIR] [/lumit beside|into|none] [/no-bridge] [/no-font] [/no-link]
    ///                      [/theater]                    the theater-only launch (its shortcuts; the Console puts the layer in per launch)
    ///                      [/test-root DIR]              a test: everything outside the install folder goes under DIR
    /// A silent run writes what it did to %TEMP%\reluduali-setup.log and exits 0, or 1 with the reason.
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Func<string, bool> has = s => args.Any(a => string.Equals(a, s, StringComparison.OrdinalIgnoreCase));
            Func<string, string> arg = s => { int i = Array.FindIndex(args, a => string.Equals(a, s, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; };
            string here = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            // "Uninstall Reluduali.exe" in the install folder is this program: double-clicked, it uninstalls (the code review's
            // C-7: it opened the installer, and Install there took the install out from under itself)
            bool uninstall = has("/uninstall") || string.Equals(Path.GetFileName(Assembly.GetExecutingAssembly().Location), Installer.UninstallerName, StringComparison.OrdinalIgnoreCase);

            if (has("/silent"))
            {
                string log = Path.Combine(Path.GetTempPath(), "reluduali-setup.log");     // not beside itself: an uninstall removes that folder
                Action<string> say = s => File.AppendAllText(log, DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\r\n");
                try
                {
                    if (uninstall) { Installer.Uninstall(arg("/from") ?? here, say); return 0; }
                    var c = new Choices { Redacted = arg("/redacted") ?? "", Lumit = arg("/lumit") ?? "beside", Bridge = !has("/no-bridge"), Font = !has("/no-font") && Installer.Carries("plugin/RipperBridge.font"), Link = !has("/no-link"), Theater = has("/theater") };
                    if (arg("/to") != null) c.InstallDir = arg("/to");
                    if (arg("/test-root") != null) c.TestRoot(arg("/test-root"));
                    Installer.Install(c, say);
                    return 0;
                }
                catch (Exception ex) { say("FAILED: " + ex.Message); return 1; }
            }

            // ReludualiSetup.exe /shot OUT.png: the setup window drawn to a picture without showing it (for checking its look)
            if (arg("/shot") != null)
            {
                var shotApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var sw = new SetupWindow();
                var body = sw.Content as UIElement;
                sw.Content = null;
                var frame = new Border { Background = sw.Background, Child = body, Width = sw.Width };
                frame.Measure(new Size(sw.Width, double.PositiveInfinity));
                frame.Arrange(new Rect(frame.DesiredSize));
                frame.UpdateLayout();
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(frame.ActualWidth), (int)Math.Ceiling(frame.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bmp.Render(frame);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var f = File.Create(arg("/shot"))) enc.Save(f);
                return 0;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            Window w = uninstall ? (Window)new UninstallWindow(here) : new SetupWindow();
            return app.Run(w);
        }
    }

    /// <summary>The Designer's look, in the few pieces the installer needs: the Ableton theme's colours, reluduali.com's
    /// velvet dither under them and its foil on the edges, corners nearly sharp (2026-10-17; the pictures come from the
    /// Designer's own Velvet.cs, linked into this project).</summary>
    static class Look
    {
        public static readonly Brush Back = Tiled(ReludualiConsole.Velvet.DeepImage), Card = Tiled(ReludualiConsole.Velvet.CardImage),
                                     Rim = Foil(0xA0, false), PrimaryRim = Foil(0xFF, true), PrimaryFace = Primary(),
                                     Text = Make(0xE6, 0xDE, 0xE8), Dim = Make(0xA7, 0x97, 0xAA), Accent = Make(0xBF, 0x90, 0xC6),
                                     OnLight = Make(0x22, 0x18, 0x23), Good = Make(0x3F, 0xB3, 0x9A), Bad = Make(0xE0, 0x33, 0x6F);
        static Brush Make(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
        static Brush Tiled(System.Windows.Media.Imaging.BitmapSource img)
        {
            var b = new ImageBrush(img) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, img.PixelWidth, img.PixelHeight), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(b, BitmapScalingMode.NearestNeighbor);
            b.Freeze();
            return b;
        }
        // the website's holographic foil (#ff4fae #ffd24f #4fffc0 #4fb4ff #b44fff) at 115 degrees over pearl, along an edge
        static Brush Foil(byte a, bool bright)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.5, 0.72), SpreadMethod = GradientSpreadMethod.Reflect };
            uint[] c = bright ? new uint[] { 0xF6F0F7, 0xFF8CC9, 0xFFE39A, 0x9AFFDB, 0x9AD2FF, 0xD59AFF, 0xF6F0F7 }
                              : new uint[] { 0xD9CCDD, 0xFF6FBE, 0xFFD86A, 0x6FFFCB, 0x6FC0FF, 0xBE6AFF, 0xD9CCDD };
            for (int i = 0; i < c.Length; i++)
                g.GradientStops.Add(new GradientStop(Color.FromArgb(a, (byte)(c[i] >> 16), (byte)(c[i] >> 8), (byte)c[i]), i / (double)(c.Length - 1)));
            g.Freeze();
            return g;
        }
        // the main action: the theme's lilac, dark letters
        static Brush Primary()
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0xCD, 0xA3, 0xD3), 0));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0xBF, 0x90, 0xC6), 0.5));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0xB3, 0x83, 0xBA), 1));
            g.Freeze();
            return g;
        }
        // one template for its buttons: the face is the Background, the rim the BorderBrush; hover lifts, a press sinks
        static readonly ControlTemplate ButtonTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
            "<Grid SnapsToDevicePixels='True'><Border CornerRadius='2' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1'/>" +
            "<Border x:Name='V' CornerRadius='2' Margin='1' Background='#00FFFFFF'/>" +
            "<ContentPresenter Margin='{TemplateBinding Padding}' HorizontalAlignment='Center' VerticalAlignment='Center' RecognizesAccessKey='True'/></Grid>" +
            "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='V' Property='Background' Value='#1CFFFFFF'/></Trigger>" +
            "<Trigger Property='IsPressed' Value='True'><Setter TargetName='V' Property='Background' Value='#38000000'/></Trigger>" +
            "<Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");

        public static Button Button(string text, bool primary = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0), Foreground = primary ? OnLight : Text, Template = ButtonTemplate,
                                 Background = primary ? PrimaryFace : Card, BorderBrush = primary ? PrimaryRim : Rim, Cursor = System.Windows.Input.Cursors.Hand };
            return b;
        }

        // a field: the velvet's dark inset with a foil rim (its own template: Windows' blue hover never shows)
        static readonly Brush Inset = Make(0x22, 0x19, 0x24);
        static readonly ControlTemplate FieldTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='TextBox'>" +
            "<Border CornerRadius='2' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1'>" +
            "<ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}'/></Border></ControlTemplate>");
        public static TextBox Field(TextBox t)
        {
            t.Template = FieldTemplate;
            if (t.Background == null || t.Background is SolidColorBrush) t.Background = Inset;
            t.Foreground = Text; t.CaretBrush = Text; t.BorderBrush = Rim; t.SelectionBrush = Accent;
            return t;
        }

        public static TextBlock Line(string text, Brush colour = null, double size = 13) { return new TextBlock { Text = text, Foreground = colour ?? Text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) }; }

        public static void Dress(Window w, string title)
        {
            w.Title = title;
            w.Background = Back;
            w.Foreground = Text;
            w.FontFamily = new FontFamily("Segoe UI");
            w.FontSize = 13;
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.SizeToContent = SizeToContent.Height;
            w.Width = 720;
            w.ResizeMode = ResizeMode.CanMinimize;
            // the Console's dark title bar (Windows 11: DWMWA_USE_IMMERSIVE_DARK_MODE)
            w.SourceInitialized += (s, e) => { try { int on = 1; DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(w).Handle, 20, ref on, 4); } catch { } };
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    }

    public sealed class SetupWindow : Window
    {
        readonly TextBox _redacted = Look.Field(new TextBox { MinWidth = 470, Padding = new Thickness(6, 4, 6, 4) });
        readonly TextBox _to = Look.Field(new TextBox { MinWidth = 470, Padding = new Thickness(6, 4, 6, 4), Text = Choices.DefaultInstallDir });
        readonly TextBlock _check = Look.Line("", Look.Dim);
        readonly CheckBox _bridge = Box("RipperBridge, the game plugin", "Into Redacted's Plugins: the Designer talks to the game through it. One already there is kept aside, never deleted.");
        readonly CheckBox _font = Box("The Polish font pack (for fun)", "The game's lettering, Polish style. One already there is kept aside.");
        readonly CheckBox _link = Box("Lumit can open the Designer", "A reluduali-console: link for you, for a Reluduali panel with an Open button (the panel in this installer has none yet). Lumit asks you first, every time; opened that way, the Designer only comes to the front.");
        readonly CheckBox _desktop = Box("A shortcut on the desktop", null);
        readonly CheckBox _theater = Box("Theater-only launch", "A \"Reluduali Theater\" shortcut (Start menu and desktop): a greeting, then the game docked in the Designer, straight into its theater in Reluduali's look. Redacted started on its own stays as it always was.");
        readonly RadioButton _beside = Radio("Beside the Designer: I add it in Lumit myself (Lumit shows what it can do first)", true);
        readonly RadioButton _into = Radio("Straight into Lumit (Lumit won't show its own list of what the panel can do first)", false);
        readonly RadioButton _none = Radio("Not now", false);
        readonly TextBox _log = Look.Field(new TextBox { IsReadOnly = true, Height = 92, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Look.Card, Foreground = Look.Text, BorderBrush = Look.Rim, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(6, 4, 6, 4) });
        readonly Button _go = Look.Button("Install", true);
        readonly Button _open = Look.Button("Open the Designer");

        static CheckBox Box(string t, string tip) { return new CheckBox { Content = t, ToolTip = tip, IsChecked = true, Foreground = Look.Text, Margin = new Thickness(0, 3, 12, 3) }; }
        static RadioButton Radio(string t, bool on) { return new RadioButton { Content = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, MaxWidth = 620 }, IsChecked = on, GroupName = "lumit", Foreground = Look.Text, Margin = new Thickness(0, 3, 0, 3) }; }

        public SetupWindow()
        {
            Look.Dress(this, "The Reluduali Designer " + Installer.Version + " setup");
            var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
            root.Children.Add(new TextBlock { Text = "The Reluduali Designer", FontSize = 24, FontWeight = FontWeights.Light, Foreground = Look.Text });
            root.Children.Add(Look.Line("Moviemaking for Black Ops II, version " + Installer.Version + ": the Designer, its game plugin and the Reluduali panel for Lumit. For you only: nothing needs an administrator.", Look.Dim));

            root.Children.Add(Section("Redacted's folder (the one with t6mpv43.exe)"));
            root.Children.Add(Row(_redacted, () => { string d = PickFolder("Redacted's folder (the one with t6mpv43.exe)"); if (d != null) _redacted.Text = d; }));
            root.Children.Add(_check);
            _redacted.TextChanged += (s, e) => Recheck();
            _redacted.Text = Guess();

            root.Children.Add(Section("Install the Designer to"));
            root.Children.Add(Row(_to, () => { string d = PickFolder("Where the Designer goes"); if (d != null) _to.Text = Path.Combine(d, "Reluduali"); }));

            root.Children.Add(Section("With it"));
            var with = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
            // the font pack only when this installer carries one (the public installer does not)
            if (!Installer.Carries("plugin/RipperBridge.font")) _font.IsChecked = false;
            foreach (var b in new[] { _bridge, _font, _link, _desktop, _theater }) if (b != _font || _font.IsChecked == true) with.Children.Add(b);
            _theater.IsChecked = false;      // optional: only for those who want it
            _link.IsChecked = false;         // nothing in this installer uses it yet
            root.Children.Add(with);

            root.Children.Add(Section("The Reluduali panel for Lumit"));
            foreach (var r in new[] { _beside, _into, _none }) root.Children.Add(r);
            root.Children.Add(Look.Line(Installer.LumitNeeds, Look.Dim, 12));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(_go);
            buttons.Children.Add(_open);
            _open.Visibility = Visibility.Collapsed;
            _go.Click += (s, e) => Go();
            _open.Click += (s, e) => { try { Process.Start(new ProcessStartInfo(Path.Combine(_to.Text, "ReludualiConsole.exe")) { WorkingDirectory = _to.Text }); } catch (Exception ex) { Say(ex.Message); } Close(); };
            root.Children.Add(buttons);
            root.Children.Add(_log);
            root.Children.Add(Look.Line("Written with an External Cortex, courtesy of Anthropic.", Look.Dim, 11));
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Recheck();
        }

        static TextBlock Section(string t) { return new TextBlock { Text = t, FontSize = 14, Foreground = Look.Text, Margin = new Thickness(0, 10, 0, 4) }; }

        static UIElement Row(TextBox box, Action pick)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            p.Children.Add(box);
            var b = Look.Button("Choose...");
            b.Margin = new Thickness(8, 0, 0, 0);
            b.Click += (s, e) => pick();
            p.Children.Add(b);
            return p;
        }

        /// <summary>The usual places a Redacted folder is, the first one that holds t6mpv43.exe.</summary>
        static string Guess()
        {
            foreach (string d in new[] { @"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Black Ops II", @"D:\SteamLibrary\steamapps\common\Call of Duty Black Ops II",
                                         @"C:\Games\Call of Duty Black Ops II", @"D:\Games\Call of Duty Black Ops II" })
                if (File.Exists(Path.Combine(d, "t6mpv43.exe"))) return d;
            return "";
        }

        static string PickFolder(string what)
        {
            // WPF has no folder picker: the file dialog, on any file in the folder (the Console's SETUP does the same)
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = what + ": pick any file in it", CheckFileExists = false, FileName = "this folder", Filter = "All files|*.*" };
            return dlg.ShowDialog() == true ? Path.GetDirectoryName(dlg.FileName) : null;
        }

        void Recheck()
        {
            bool ok;
            _check.Text = Installer.CheckRedacted(_redacted.Text.Trim(), out ok);
            _check.Foreground = ok ? Look.Good : Look.Bad;
            _go.IsEnabled = ok;
        }

        void Say(string s) { Dispatcher.BeginInvoke(new Action(() => { _log.AppendText(s + "\r\n"); _log.ScrollToEnd(); })); }

        /// <summary>A typed folder made whole ("Reluduali" alone would land wherever this program was started from).</summary>
        static string Full(string d) { try { return d.Length > 0 ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(d)) : d; } catch { return d; } }

        void Go()
        {
            var c = new Choices
            {
                Redacted = _redacted.Text.Trim(), InstallDir = Full(_to.Text.Trim()),
                Bridge = _bridge.IsChecked == true, Font = _font.IsChecked == true, Link = _link.IsChecked == true, Desktop = _desktop.IsChecked == true,
                Theater = _theater.IsChecked == true,
                Lumit = _into.IsChecked == true ? "into" : _none.IsChecked == true ? "none" : "beside",
            };
            _go.IsEnabled = false;
            _log.Clear();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool done = false;
                try { Installer.Install(c, Say); done = true; }
                catch (Exception ex) { Say("Not installed: " + ex.Message); }
                Dispatcher.BeginInvoke(new Action(() => { if (done) { _go.Visibility = Visibility.Collapsed; _open.Visibility = Visibility.Visible; } else _go.IsEnabled = true; }));
            });
        }
    }

    public sealed class UninstallWindow : Window
    {
        public UninstallWindow(string installDir)
        {
            Look.Dress(this, "Uninstall The Reluduali Designer");
            var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
            root.Children.Add(Look.Line("Take The Reluduali Designer back out of " + installDir + "?", Look.Text, 15));
            root.Children.Add(Look.Line("Only what the installer put down goes; what it set aside (an older RipperBridge, an older panel) goes back where it was. Your presets and recordings stay.", Look.Dim));
            var log = Look.Line("", Look.Dim);
            var go = Look.Button("Uninstall", true);
            var no = Look.Button("Keep it");
            no.Click += (s, e) => Close();
            go.Click += (s, e) =>
            {
                go.IsEnabled = false;
                try { Installer.Uninstall(installDir, t => log.Text += t + "\n"); log.Text += "Done."; }
                catch (Exception ex) { log.Text += "Not uninstalled: " + ex.Message; go.IsEnabled = true; }
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 6) };
            buttons.Children.Add(go); buttons.Children.Add(no);
            root.Children.Add(buttons);
            root.Children.Add(log);
            Content = root;
        }
    }
}
