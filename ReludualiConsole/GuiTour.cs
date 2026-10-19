using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ReludualiConsole
{
    /// <summary>
    /// Every page of the console drawn to a picture, with no window on the screen (2026-10-14): for checking the look while
    /// Mycelium works on this PC. ReludualiConsole.exe --gui-tour OUTDIR [WIDTH HEIGHT] [QUEUEDIR ROW]
    /// The window's content is put in a hidden window of its own (so pages see themselves as shown and build), each tool is
    /// brought to the front in turn and drawn. Nothing talks to the game: App.Tour keeps the tether, the batch's pipe, the
    /// contest timer, the keyboard hook and the CAM QUEUE's polling off.
    /// </summary>
    public static class GuiTour
    {
        public static int Run(string[] a)
        {
            string outDir = a[1];
            int w = 1480, h = 940;
            if (a.Length >= 4) { w = int.Parse(a[2], CultureInfo.InvariantCulture); h = int.Parse(a[3], CultureInfo.InvariantCulture); }
            string queue = a.Length >= 5 ? a[4] : null;
            int row = a.Length >= 6 ? int.Parse(a[5], CultureInfo.InvariantCulture) : 0;
            App.Tour = true;
            BatchRecorder.BatchPipe.Off = true;
            Directory.CreateDirectory(outDir);

            var win = new MainWindow();
            var root = (FrameworkElement)win.Content;
            win.Content = null;
            TextElement.SetFontFamily(root, new FontFamily("Segoe UI"));
            TextElement.SetFontSize(root, 13);
            TextElement.SetForeground(root, Ui.Brush("Text"));
            var host = new HwndSource(new HwndSourceParameters("Reluduali tour")
            {
                Width = w, Height = h, PositionX = -32000, PositionY = -32000,
                WindowStyle = unchecked((int)0x80000000),           // WS_POPUP, never WS_VISIBLE: nothing on the screen
            });
            host.RootVisual = root;
            root.Width = w; root.Height = h;
            Pump(500);
            int n = 0;
            win.ShowHomeForTour();
            Shot(root, outDir, (n++).ToString("00") + "-home", w, h);
            foreach (string key in win.ToolKeys.ToList())
            {
                win.ShowTool(key);
                Pump(key == "camqueue" ? 1500 : 450);
                Shot(root, outDir, (n++).ToString("00") + "-" + key, w, h);
            }
            if (queue != null && CamQueueControl.Instance != null)
            {
                win.ShowTool("camqueue");
                string said = CamQueueControl.Instance.TourOpen(queue, row);
                var t0 = DateTime.Now;
                while (!CamQueueControl.Instance.TourMapSettled && (DateTime.Now - t0).TotalSeconds < 30) Pump(250);
                Pump(2500);                                           // the players load on a worker too
                CamQueueControl.Instance.TourSettle();
                Pump(800);
                said += "; map settled after " + (DateTime.Now - t0).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
                Shot(root, outDir, (n++).ToString("00") + "-camqueue-row", w, h);
                File.WriteAllText(Path.Combine(outDir, "camqueue.txt"), said + "\r\n" + CamQueueControl.Instance.TourStatus());
                // the views' looks and layouts, each as a script would set it (the pipe's "queue view ...")
                foreach (string[] step in new[]
                {
                    new[] { "view look 3 off", "side-nomap" },
                    new[] { "view look 2 wire", "top-wire" },
                    new[] { "view add front wire ghost", "four-views" },
                    new[] { "view main 2", "top-as-main" },
                })
                {
                    string r = CamQueueControl.Instance.TourPipe(step[0]);
                    Pump(600);
                    CamQueueControl.Instance.TourSettle();
                    Pump(400);
                    Shot(root, outDir, (n++).ToString("00") + "-camqueue-" + step[1], w, h);
                    File.AppendAllText(Path.Combine(outDir, "camqueue.txt"), "\r\n> " + step[0] + "\r\n" + r + "\r\n");
                }
                // grab a camera in the big (top) view: pick it, drag it, aim it, undo both - as the mouse would
                var log = new System.Text.StringBuilder();
                Func<string, string> pipe = c => { string r = CamQueueControl.Instance.TourPipe(c); log.Append("> ").Append(c).Append("\r\n").Append(r).Append("\r\n"); return r; };
                string screens = pipe("view markers");
                var m2 = System.Text.RegularExpressions.Regex.Match(screens, @"2: (-?\d+),(-?\d+)");
                if (m2.Success)
                {
                    double x = double.Parse(m2.Groups[1].Value, CultureInfo.InvariantCulture), y = double.Parse(m2.Groups[2].Value, CultureInfo.InvariantCulture);
                    pipe("view pick " + (x + 10).ToString(CultureInfo.InvariantCulture) + " " + y.ToString(CultureInfo.InvariantCulture));
                    pipe("view pick " + (x + 20).ToString(CultureInfo.InvariantCulture) + " " + y.ToString(CultureInfo.InvariantCulture));
                    pipe("view drag 2 60 0");
                    Pump(300);
                    Shot(root, outDir, (n++).ToString("00") + "-camqueue-dragged", w, h);
                    pipe("view drag 2 40 0 aim");
                    pipe("view undo");
                    pipe("view undo");
                    pipe("view markers");
                }
                File.AppendAllText(Path.Combine(outDir, "camqueue.txt"), "\r\n" + log);
                CamQueueControl.Instance.TourPipe("view default");
                // the same queue on the POV Queue and on the Batch page (2026-10-18): its POV takes; every take, a take picked,
                // the page scrolled down to the take list
                win.ShowTool("povqueue");
                Pump(600);
                Shot(root, outDir, (n++).ToString("00") + "-povqueue-queue", w, h);
                win.ShowTool("batch");
                Pump(1200);
                Shot(root, outDir, (n++).ToString("00") + "-batch-queue", w, h);
                var grid = Find<DataGrid>(root);
                if (grid != null && grid.Items.Count > 0) { grid.SelectedIndex = Math.Min(1, grid.Items.Count - 1); Pump(500); }
                var sv = Find<ScrollViewer>(win.PageForTour("batch") as DependencyObject);
                if (sv != null) { sv.ScrollToEnd(); Pump(600); }
                Shot(root, outDir, (n++).ToString("00") + "-batch-takes", w, h);
            }
            // the workspace layouts (no game in the tour: the slot shows where it goes)
            win.ShowTool("fog");
            foreach (string[] step in new[] { new[] { "layout workspace", "workspace-right" }, new[] { "layout below", "workspace-below" }, new[] { "layout overlay", "overlay" } })
            {
                win.TourConsole(step[0]);
                Pump(500);
                Shot(root, outDir, (n++).ToString("00") + "-" + step[1], w, h);
            }
            win.TourConsole("layout right");
            win.TourConsole("layout classic");
            Glyphs(outDir);
            host.Dispose();
            return 0;
        }

        /// <summary>The first element of a type in a visual tree (itself included), or null.</summary>
        static T Find<T>(DependencyObject d) where T : DependencyObject
        {
            if (d == null) return null;
            if (d is T) return (T)d;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) { T f = Find<T>(VisualTreeHelper.GetChild(d, i)); if (f != null) return f; }
            return null;
        }

        /// <summary>Lets the dispatcher run for a while: layout, Loaded, work posted back from other threads.</summary>
        public static void Pump(int ms)
        {
            var frame = new DispatcherFrame();
            var t = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(ms) };      // never starved by drawing
            t.Tick += (s, e) => { t.Stop(); frame.Continue = false; };
            t.Start();
            Dispatcher.PushFrame(frame);
        }

        private static void Shot(FrameworkElement root, string dir, string name, int w, int h)
        {
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            root.UpdateLayout();
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(Path.Combine(dir, name + ".png"))) enc.Save(f);
        }

        /// <summary>A sheet of icon glyphs with their code points, to choose the rail's icons by eye.</summary>
        private static void Glyphs(string dir)
        {
            var codes = new[]
            {
                0xE80F, 0xE713, 0xEC4F, 0xE8D6, 0xE897, 0xE8B2, 0xE714, 0xE790, 0xE706, 0xE753, 0xE722, 0xE8D2, 0xE756, 0xE9E9, 0xE7C8, 0xE909,
                0xF158, 0xE77B, 0xE916, 0xE718, 0xE77A, 0xE8A7, 0xE711, 0xE710, 0xE72C, 0xE838, 0xE74E, 0xE768, 0xE769, 0xE71A, 0xE70E, 0xE70D,
                0xE70F, 0xE8C8, 0xE74D, 0xE73E, 0xE890, 0xE740, 0xE71B, 0xE7F4, 0xE7A6, 0xE7C1, 0xE9CA, 0xE771, 0xE81E, 0xE8FD, 0xE7B8, 0xE8A9,
                0xE80A, 0xE945, 0xE9D9, 0xE793, 0xE708, 0xE774, 0xE707, 0xE81D, 0xE7AD, 0xE8B9, 0xE91B, 0xE7EE, 0xE8EC, 0xE9F5, 0xEA86, 0xE7FC,
                0xEB9F, 0xE8E5, 0xE8F1, 0xE9A9, 0xF0E2, 0xE81C, 0xE7E6, 0xE771, 0xE8BA, 0xE7F8, 0xE963, 0xE95E, 0xE7B3, 0xE8AB, 0xE895, 0xE72A,
                0xE9F9, 0xEA80, 0xE754, 0xE787, 0xEC05, 0xED5E, 0xE88E, 0xE7BE, 0xE9D2, 0xE8CB, 0xE946, 0xE734, 0xE735, 0xE8BC, 0xE7AC, 0xE97A,
            };
            const int cols = 16, cell = 74;
            int rows = (codes.Length + cols - 1) / cols;
            var grid = new Canvas { Width = cols * cell, Height = rows * cell, Background = Ui.Brush("Back") };
            for (int i = 0; i < codes.Length; i++)
            {
                var g = new TextBlock { Text = char.ConvertFromUtf32(codes[i]), FontFamily = Ui.IconFont, FontSize = 26, Foreground = Ui.Brush("Chrome") };
                var c = new TextBlock { Text = codes[i].ToString("X4"), FontSize = 11, Foreground = Ui.Brush("Dim") };
                Canvas.SetLeft(g, (i % cols) * cell + 22); Canvas.SetTop(g, (i / cols) * cell + 10);
                Canvas.SetLeft(c, (i % cols) * cell + 20); Canvas.SetTop(c, (i / cols) * cell + 50);
                grid.Children.Add(g); grid.Children.Add(c);
            }
            Shot(grid, dir, "zz-glyphs", cols * cell, rows * cell);
        }
    }
}
