using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ReludualiConsole
{
    /// <summary>
    /// The pieces every page is laid out with (2026-10-14, the new look - Theme.xaml). The rule for buttons, so each page
    /// reads the same way (Mycelium: "make the logical ordering/placement of buttons intuitive"):
    ///  - a page's main action is Primary (purple chrome) and comes first in its row;
    ///  - actions on the same thing sit together; groups are parted by a thin line (Gap);
    ///  - stop / take out / reset come last, and only what cannot be undone is Danger (red);
    ///  - a page is cards: a short title, a line on what it does, then its controls.
    /// </summary>
    public static class Ui
    {
        public static Brush Brush(string key) { return (Brush)Application.Current.FindResource(key); }
        public static Style Style(string key) { return (Style)Application.Current.FindResource(key); }

        // ---------------------------------------------------------------- the Reluduali mark
        // Mycelium 2026-10-17, with a picture of it: "Update the Main Reluduali .exe Logo and Icons in the UI with this logo".
        // The mark is Reluduali's own (reluduali.com's brand files, tools\release\brand\): the greeting's dithered chrome.

        private static ImageSource _mark, _icon;

        /// <summary>The mark as an image, 256 px square, transparent (Assets\mark.png).</summary>
        public static ImageSource MarkSource
        {
            get
            {
                if (_mark == null) { var b = new BitmapImage(new Uri("pack://application:,,,/Assets/mark.png")); b.Freeze(); _mark = b; }
                return _mark;
            }
        }

        /// <summary>The mark at a size, pixel for pixel so its dither stays crisp.</summary>
        public static Image Mark(double size)
        {
            var i = new Image { Source = MarkSource, Width = size, Height = size, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.NearestNeighbor);
            return i;
        }

        /// <summary>Every window's icon (Assets\reluduali.ico, 16 to 256 px): Windows picks the size it needs.</summary>
        public static ImageSource WindowIcon
        {
            get { return _icon ?? (_icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/reluduali.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad)); }
        }

        /// <summary>A web page in the browser (https only: the site, the source, Lumit).</summary>
        public static void OpenWeb(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttps) return;
            try { Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true }); }
            catch (Exception ex) { App.Note("opening " + url, ex); }
        }

        /// <summary>Segoe Fluent Icons (Windows 11), else Segoe MDL2 Assets (Windows 10): the same code points.</summary>
        public static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public static Button Button(string text, Action click, string tip = null, string icon = null)
        {
            var b = new Button { Content = icon == null ? (object)text : IconText(icon, text), ToolTip = tip, Margin = new Thickness(0, 0, 8, 6) };
            b.Click += (s, e) => { try { click(); } catch (Exception ex) { App.Note(text, ex); } };
            return b;
        }

        public static Button Primary(Button b) { b.Style = Style("PrimaryButton"); return b; }
        public static Button Danger(Button b) { b.Style = Style("DangerButton"); return b; }
        public static Button Quiet(Button b) { b.Style = Style("QuietButton"); return b; }

        /// <summary>An icon and a word, for a button's face.</summary>
        public static FrameworkElement IconText(string glyph, string text)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            p.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, text.Length > 0 ? 7 : 0, 0) });
            if (text.Length > 0) p.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return p;
        }

        public static TextBlock Icon(string glyph, double size = 14)
        {
            return new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        }

        /// <summary>A thin upright line between groups of buttons in a row.</summary>
        public static FrameworkElement Gap()
        {
            return new Border { Width = 1, Height = 22, Background = Brush("Line"), Opacity = 0.7, Margin = new Thickness(6, 0, 14, 6), VerticalAlignment = VerticalAlignment.Center };
        }

        /// <summary>A small purple heading over a group of controls.</summary>
        public static TextBlock Heading(string text, double top = 0)
        {
            return new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("Accent"), Margin = new Thickness(0, top, 0, 6) };
        }

        /// <summary>Quiet explanatory text.</summary>
        public static TextBlock Note(string text, double top = 0, double bottom = 0)
        {
            return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush("Dim"), Margin = new Thickness(0, top, 0, bottom), LineHeight = 19 };
        }

        /// <summary>A label in front of a field.</summary>
        public static TextBlock Label(string text, double width = double.NaN)
        {
            return new TextBlock { Text = text, Width = width, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6), Foreground = Brush("Text") };
        }

        /// <summary>A row of controls that wraps when the page is narrow.</summary>
        public static WrapPanel Row(params UIElement[] items)
        {
            var w = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (UIElement e in items) if (e != null) w.Children.Add(e);
            return w;
        }

        /// <summary>A card: a title, an optional line on what it does, then its content. By day it is printed like the
        /// website's cards (PrintCard) and its title is set in their Georgia.</summary>
        public static Border Card(string title, string blurb, params UIElement[] content)
        {
            var p = new StackPanel();
            if (title != null) p.Children.Add(Title(new TextBlock { Text = title, FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = Brush("Chrome"), Margin = new Thickness(0, 0, 0, blurb == null ? 10 : 2) }));
            if (blurb != null) p.Children.Add(Note(blurb, 0, 10));
            foreach (UIElement e in content) if (e != null) p.Children.Add(e);
            return new PrintCard { Child = p, Margin = new Thickness(0, 0, 0, 12) };
        }

        /// <summary>A title's face follows the skin: Segoe UI by night, the website cards' bold Georgia by day.</summary>
        public static TextBlock Title(TextBlock t, string weight = "TitleWeight")
        {
            PaintExtension.Bind(t, TextBlock.FontFamilyProperty, "TitleFont");
            PaintExtension.Bind(t, TextBlock.FontWeightProperty, weight);
            return t;
        }

        /// <summary>A page's body: cards stacked, scrolling when they are taller than the window.</summary>
        public static ScrollViewer Page(params UIElement[] cards)
        {
            var p = new StackPanel { Margin = new Thickness(12, 4, 12, 12) };
            foreach (UIElement c in cards) if (c != null) p.Children.Add(c);
            return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }

        /// <summary>A status line under a page's controls.</summary>
        public static TextBlock Status()
        {
            return new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0), Foreground = Brush("AccentPale") };
        }

        /// <summary>Tabs in the Designer's own look (2026-10-18; WPF's TabControl draws Windows' white): a row of toggles, the
        /// picked one's page under it.</summary>
        public static FrameworkElement Tabs(params Tuple<string, UIElement>[] tabs)
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var host = new ContentControl { Focusable = false };
            var toggles = new List<ToggleButton>();
            foreach (var t in tabs)
            {
                var tb = new ToggleButton { Content = t.Item1, Style = Style("QuietToggle"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 4, 0) };
                UIElement page = t.Item2;
                tb.Click += (s, e) => { foreach (ToggleButton o in toggles) o.IsChecked = o == tb; host.Content = page; };
                toggles.Add(tb);
                bar.Children.Add(tb);
            }
            if (toggles.Count > 0) { toggles[0].IsChecked = true; host.Content = tabs[0].Item2; }
            var dock = new DockPanel();
            var line = new Border { Height = 1, Background = Brush("Line"), Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(bar, Dock.Top); DockPanel.SetDock(line, Dock.Top);
            dock.Children.Add(bar); dock.Children.Add(line); dock.Children.Add(host);
            return dock;
        }

        /// <summary>An icon-only quiet button (the 3D views' headers).</summary>
        public static Button IconButton(string glyph, string tip, Action click)
        {
            var b = new Button { Content = Icon(glyph, 13), ToolTip = tip, Style = Style("QuietButton"), Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(1, 0, 1, 0) };
            b.Click += (s, e) => { try { click(); } catch (Exception ex) { App.Note(tip, ex); } };
            return b;
        }

        public static ToggleButton IconToggle(string glyph, string tip)
        {
            return new ToggleButton { Content = Icon(glyph, 13), ToolTip = tip, Style = Style("QuietToggle"), Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(1, 0, 1, 0) };
        }
    }

    /// <summary>Glyphs from Segoe Fluent Icons / Segoe MDL2 Assets, by what they mean here.</summary>
    public static class Glyph
    {
        public const string Home = "\uE80F", Setting = "\uE713", Music = "\uEC4F", Help = "\uE897", Film = "\uE8B2", Video = "\uE714",
            Palette = "\uE790", Sun = "\uE706", Cloud = "\uE753", Camera = "\uE722", Font = "\uE8D2", Command = "\uE756",
            Sliders = "\uE9E9", Record = "\uE7C8", Map = "\uE909", Cube = "\uF158", Person = "\uE77B", Stopwatch = "\uE916",
            Pin = "\uE718", Unpin = "\uE77A", PopOut = "\uE8A7", Close = "\uE711", Add = "\uE710", Refresh = "\uE72C",
            Folder = "\uE838", Save = "\uE74E", Play = "\uE768", Pause = "\uE769", Stop = "\uE71A", Up = "\uE70E", Down = "\uE70D",
            Edit = "\uE70F", Copy = "\uE8C8", Delete = "\uE74D", Check = "\uE73E", Eye = "\uE890", Frame = "\uE740", Link = "\uE71B",
            Theater = "\uE714", Gun = "\uE771", Moon = "\uE708", Contrast = "\uE793", Grid = "\uE80A", List = "\uE8FD",
            DockBottom = "\uE90E", DockRight = "\uE90D";
    }
}
