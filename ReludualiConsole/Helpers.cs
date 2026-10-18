using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Web.Script.Serialization;

namespace ReludualiConsole
{
    /// <summary>The colour wheel behind every colour swatch: hue and saturation on the disc, strength on the slider. Live.</summary>
    public static class ColourWheel
    {
        public static void Show(Window owner, string title, double[] start, double max, Action<double[]> changed)
        {
            const int size = 220;
            var w = new Window { Title = title, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                                 WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)Application.Current.FindResource("Back") };
            var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
            var px = new byte[size * size * 4];
            double c = (size - 1) / 2.0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    double dx = x - c, dy = y - c, r = Math.Sqrt(dx * dx + dy * dy) / c;
                    int o = (y * size + x) * 4;
                    if (r > 1) continue;
                    double h = (Math.Atan2(-dy, dx) * 180 / Math.PI + 360) % 360;
                    double[] rgb = Hsv(h, r, 1);
                    px[o] = (byte)(255 * rgb[2]); px[o + 1] = (byte)(255 * rgb[1]); px[o + 2] = (byte)(255 * rgb[0]); px[o + 3] = 255;
                }
            bmp.WritePixels(new Int32Rect(0, 0, size, size), px, size * 4, 0);

            double top = Math.Max(start[0], Math.Max(start[1], start[2]));
            double hue, sat; ToHs(start, out hue, out sat);
            var img = new Image { Source = bmp, Width = size, Height = size, Cursor = Cursors.Cross };
            var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
            var canvas = new Canvas { Width = size, Height = size };
            canvas.Children.Add(img); canvas.Children.Add(dot);
            var strength = new Slider { Minimum = 0, Maximum = Math.Max(max, top), Value = top, Width = size, Margin = new Thickness(0, 8, 0, 0) };
            var readout = new TextBlock { Foreground = (Brush)Application.Current.FindResource("Text"), Margin = new Thickness(0, 6, 0, 0) };
            Action emit = () =>
            {
                double[] rgb = Hsv(hue, sat, 1);
                var v = new[] { rgb[0] * strength.Value, rgb[1] * strength.Value, rgb[2] * strength.Value };
                double a = hue * Math.PI / 180;
                Canvas.SetLeft(dot, c + Math.Cos(a) * sat * c - 5); Canvas.SetTop(dot, c - Math.Sin(a) * sat * c - 5);
                readout.Text = string.Format(CultureInfo.InvariantCulture, "R {0:0.###}  G {1:0.###}  B {2:0.###}", v[0], v[1], v[2]);
                changed(v);
            };
            MouseEventHandler pick = (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                Point p = e.GetPosition(img);
                double dx = p.X - c, dy = p.Y - c;
                sat = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / c);
                hue = (Math.Atan2(-dy, dx) * 180 / Math.PI + 360) % 360;
                emit();
            };
            img.MouseMove += pick;
            img.MouseLeftButtonDown += (s, e) => { img.CaptureMouse(); pick(s, e); };
            img.MouseLeftButtonUp += (s, e) => img.ReleaseMouseCapture();
            strength.ValueChanged += (s, e) => emit();
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(canvas);
            panel.Children.Add(new TextBlock { Text = "Strength (the brightest channel; fog colours are HDR)", Foreground = (Brush)Application.Current.FindResource("Dim"), Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(strength);
            panel.Children.Add(readout);
            w.Content = panel;
            double a0 = hue * Math.PI / 180;
            Canvas.SetLeft(dot, c + Math.Cos(a0) * sat * c - 5); Canvas.SetTop(dot, c - Math.Sin(a0) * sat * c - 5);
            w.Show();
        }

        private static void ToHs(double[] rgb, out double h, out double s)
        {
            double mx = Math.Max(rgb[0], Math.Max(rgb[1], rgb[2])), mn = Math.Min(rgb[0], Math.Min(rgb[1], rgb[2]));
            s = mx <= 0 ? 0 : (mx - mn) / mx;
            if (mx == mn) { h = 0; return; }
            double d = mx - mn;
            if (mx == rgb[0]) h = 60 * (((rgb[1] - rgb[2]) / d) % 6);
            else if (mx == rgb[1]) h = 60 * ((rgb[2] - rgb[0]) / d + 2);
            else h = 60 * ((rgb[0] - rgb[1]) / d + 4);
            if (h < 0) h += 360;
        }

        public static double[] Hsv(double h, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; } else if (h < 120) { r = x; g = c; b = 0; } else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; } else if (h < 300) { r = x; g = 0; b = c; } else { r = c; g = 0; b = x; }
            return new[] { r + m, g + m, b + m };
        }
    }

    /// <summary>One line of text from the user.</summary>
    public static class Ask
    {
        public static string Text(Window owner, string title, string prompt, string start)
        {
            var w = new Window { Title = title, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                                 WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)Application.Current.FindResource("Back") };
            var box = new TextBox { Text = start ?? "", Width = 320, Margin = new Thickness(0, 6, 0, 10) };
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 70, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 70 };
            ok.Click += (s, e) => { w.DialogResult = true; };
            var p = new StackPanel { Margin = new Thickness(14) };
            p.Children.Add(new TextBlock { Text = prompt, Foreground = (Brush)Application.Current.FindResource("Text") });
            p.Children.Add(box);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            row.Children.Add(ok); row.Children.Add(cancel);
            p.Children.Add(row);
            w.Content = p;
            w.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            return w.ShowDialog() == true ? box.Text.Trim() : null;
        }
    }

    /// <summary>A settings file as CFG text: a .cfg is read as it is.</summary>
    public static class SettingsFiles
    {
        public static string ToCfg(string path, string section) { return File.ReadAllText(path, Encoding.UTF8); }
    }
}
