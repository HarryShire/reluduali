using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace ReludualiConsole
{
    /// <summary>
    /// The SKY tab (2026-10-02). A sky set is a folder of
    /// IWIs named like the game's sky images; a loose file in data\images overrides the map's own sky,
    /// and no loose file means the original sky. Same convention as the 2026-10-01 install: the record
    /// is "Installed in Redacted (set)\installed.txt" in the store, and files taken out go to
    /// "removed\yyyyMMdd-HHmmss" - nothing is deleted. The game reads its images at start: restart it.
    /// </summary>
    public class SkyControl : UserControl
    {
        private readonly ListBox _sets = new ListBox { Height = 260, Margin = new Thickness(0, 0, 0, 10) };
        private readonly TextBlock _status = Ui.Status();
        private readonly TextBlock _where = Ui.Note("", 8);

        /// <summary>The store, from SETUP ("skies.txt" beside the console still works: the sandbox points at its own copy).</summary>
        private static string Store { get { return Where.SkyStore; } }

        /// <summary>data\images of the game, wherever SETUP finds it.</summary>
        private static string Images() { return Where.InGame(@"data\images"); }

        public SkyControl()
        {
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Text");
            var row = new WrapPanel();
            row.Children.Add(Ui.Primary(Btn("Install set", (s, e) => Install())));
            row.Children.Add(Btn("Original skies", (s, e) => Original()));
            row.Children.Add(Ui.Gap());
            row.Children.Add(Btn("Refresh", (s, e) => Refresh()));
            row.Children.Add(Btn("Open store", (s, e) => { try { Process.Start(Store); } catch (Exception ex) { _status.Text = ex.Message; } }));
            Content = Ui.Page(Ui.Card("Sky sets", "Folders of sky IWIs in the store. Install one, or go back to the maps' own skies; restart the game after either. Nothing is deleted: what was there is moved aside.",
                                      _sets, row, _status, _where));
            Refresh();
        }

        private static Button Btn(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6) };
            b.Click += click;
            return b;
        }

        private static string[] SkyFiles(string dir)
        {
            return Directory.GetFiles(dir, "*.iwi").Where(f =>
            {
                string n = Path.GetFileName(f).ToLowerInvariant();
                return n.Contains("sky") || n.EndsWith("_ft.iwi");
            }).ToArray();
        }

        private static string Hash(string f)
        {
            using (var md5 = MD5.Create()) using (var s = File.OpenRead(f)) return BitConverter.ToString(md5.ComputeHash(s));
        }

        private void Refresh(bool keepStatus = false)
        {
            _sets.Items.Clear();
            string images = Images();
            _where.Text = "Store: " + Store + "\nGame images: " + images;
            try
            {
                foreach (string d in Directory.GetDirectories(Store).OrderBy(x => x))
                {
                    string name = Path.GetFileName(d);
                    if (name.StartsWith("Installed in", StringComparison.OrdinalIgnoreCase) || name.Equals("removed", StringComparison.OrdinalIgnoreCase)) continue;
                    string[] files = SkyFiles(d);
                    if (files.Length == 0) continue;
                    int live = files.Count(f =>
                    {
                        string g = Path.Combine(images, Path.GetFileName(f));
                        return File.Exists(g) && new FileInfo(g).Length == new FileInfo(f).Length && Hash(g) == Hash(f);
                    });
                    _sets.Items.Add(new ListBoxItem
                    {
                        Tag = d,
                        Content = name + "   (" + files.Length + " skies" + (live == files.Length ? ", INSTALLED" : live > 0 ? ", " + live + " installed" : "") + ")"
                    });
                }
                if (!keepStatus) _status.Text = _sets.Items.Count + " set(s) found.";
            }
            catch (Exception ex) { App.Note("listing sky sets", ex); _status.Text = "Could not read the store: " + ex.Message; }
        }

        /// <summary>Moves every loose sky file the records say we placed out of data\images, into removed\stamp.</summary>
        private int TakeOut(string images, string stamp)
        {
            int moved = 0;
            string dest = Path.Combine(Store, "removed", stamp);
            foreach (string rec in Directory.GetDirectories(Store, "Installed in Redacted*"))
            {
                string list = Path.Combine(rec, "installed.txt");
                if (!File.Exists(list)) continue;
                foreach (string n in File.ReadAllLines(list).Select(l => l.Trim().TrimStart('\uFEFF')).Where(l => l.EndsWith(".iwi", StringComparison.OrdinalIgnoreCase)))
                {
                    string f = Path.Combine(images, n);
                    if (!File.Exists(f)) continue;
                    Directory.CreateDirectory(dest);
                    string to = Path.Combine(dest, n);
                    if (File.Exists(to)) to = Path.Combine(dest, Path.GetFileNameWithoutExtension(n) + "." + moved + ".iwi");
                    File.Move(f, to);
                    moved++;
                }
                File.AppendAllText(Path.Combine(rec, "install-log.txt"), stamp + " taken out (files in removed\\" + stamp + ")\r\n");
                File.Move(list, list + "." + stamp + ".old");
            }
            return moved;
        }

        private void Install()
        {
            var item = _sets.SelectedItem as ListBoxItem;
            if (item == null) { _status.Text = "Pick a set first."; return; }
            string set = (string)item.Tag, images = Images(), stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                if (!Directory.Exists(images)) { _status.Text = "No data\\images at " + images; return; }
                int outN = TakeOut(images, stamp);
                string[] files = SkyFiles(set);
                // a loose file we did not place (no record) is moved aside too, never overwritten
                string dest = Path.Combine(Store, "removed", stamp);
                foreach (string f in files)
                {
                    string g = Path.Combine(images, Path.GetFileName(f));
                    if (File.Exists(g)) { Directory.CreateDirectory(dest); File.Move(g, Path.Combine(dest, Path.GetFileName(f) + ".unrecorded")); }
                    File.Copy(f, g);
                }
                string rec = Path.Combine(Store, "Installed in Redacted (" + Path.GetFileName(set) + ")");
                Directory.CreateDirectory(rec);
                File.WriteAllLines(Path.Combine(rec, "installed.txt"), files.Select(Path.GetFileName));
                File.AppendAllText(Path.Combine(rec, "install-log.txt"), stamp + " installed " + files.Length + " from " + set + "\r\n");
                _status.Text = "Installed " + files.Length + " skies from " + Path.GetFileName(set) + (outN > 0 ? "; " + outN + " earlier ones moved to removed\\" + stamp : "") + ". Restart the game.";
            }
            catch (Exception ex) { App.Note("installing a sky set", ex); _status.Text = "Install failed (nothing deleted): " + ex.Message; }
            Refresh(true);
        }

        private void Original()
        {
            string images = Images(), stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                int n = TakeOut(images, stamp);
                _status.Text = n > 0 ? n + " sky files moved to removed\\" + stamp + ": the maps' own skies come back. Restart the game." : "No installed skies on record.";
            }
            catch (Exception ex) { App.Note("restoring original skies", ex); _status.Text = "Failed (nothing deleted): " + ex.Message; }
            Refresh(true);
        }
    }
}
