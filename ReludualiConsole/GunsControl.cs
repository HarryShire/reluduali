using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>
    /// The GUNS tab (2026-10-02): install a chrome / camo build,
    /// or take ours out. It runs tools\chrome\install.ps1, the installer used on 2026-10-01, so its rules hold:
    /// it never overwrites a file it did not place, the originals stay in the game's .ipak files (and a copy in
    /// the store's originals\), and files taken out go to removed\stamp. Restart the game after either.
    ///
    /// Try live (2026-10-17; Mycelium: "replacing camos without closing the game"): the build's images made into DDS files
    /// by OpenAssetTools' ImageConverter in a cache of the Console's own (never in the build's folder), then each one the
    /// game has loaded drawn from it at once by RipperBridge 0.11.0-dev28+ ("img swap"); "Back to the game's" puts the
    /// game's own back. Nothing on disk in the game changes; a restart (or another map) brings the game's own back too.
    /// </summary>
    public class GunsControl : UserControl
    {
        private readonly ListBox _builds = new ListBox { Height = 260, Margin = new Thickness(0, 0, 0, 10) };
        private readonly TextBlock _status = Ui.Status();
        private readonly TextBlock _where = Ui.Note("", 8);

        /// <summary>The store and the textures folder, from SETUP (guns.txt beside the console still works; the sandbox uses it).</summary>
        private static string[] Places() { return new[] { Where.GunStore, Where.Textures }; }

        private static string Installer()
        {
            string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"tools\install.ps1");
            return File.Exists(beside) ? beside : Path.Combine(Where.Tools, @"chrome\install.ps1");
        }

        private static string Game() { return Where.Game ?? "(the game folder is not known: see SETUP)"; }

        public GunsControl()
        {
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Text");
            var row = new WrapPanel();
            row.Children.Add(Ui.Primary(Btn("Install build", (s, e) => Install())));
            row.Children.Add(Btn("Game's own camos", (s, e) => Run("-Remove", "taking ours out")));
            row.Children.Add(Ui.Gap());
            row.Children.Add(Btn("Try live", (s, e) => TryLive()));
            row.Children.Add(Btn("Back to the game's", (s, e) => Restore()));
            row.Children.Add(Ui.Gap());
            row.Children.Add(Btn("Refresh", (s, e) => Refresh()));
            row.Children.Add(Btn("Open store", (s, e) => { try { Process.Start(Places()[0]); } catch (Exception ex) { _status.Text = ex.Message; } }));
            Content = Ui.Page(Ui.Card("Chrome and camo builds", "Folders with an iwi\\ inside. Install one, or take ours out to get the game's own camos back; restart the game after either. The installer never overwrites what it did not place.",
                                      _builds, row, _status, _where));
            Refresh();
        }

        private static Button Btn(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6) };
            b.Click += click;
            return b;
        }

        private void Refresh(bool keepStatus = false)
        {
            _builds.Items.Clear();
            string[] pl = Places();
            string store = pl[0];
            _where.Text = "Store: " + store + "\nGame: " + Game() + "\nInstaller: " + Installer();
            try
            {
                string installed = null;
                string log = Path.Combine(store, "install-log.txt"), man = Path.Combine(store, "installed.txt");
                if (File.Exists(log) && File.Exists(man) && File.ReadAllLines(man).Any(l => l.Trim().Length > 0))
                {
                    string last = File.ReadAllLines(log).LastOrDefault(l => l.Contains(" installed ") || l.Contains(" moved ")) ?? "";
                    int at = last.IndexOf(" from ", StringComparison.Ordinal);
                    if (last.Contains(" installed ") && at > 0) installed = last.Substring(at + 6).Trim();
                }
                var dirs = new System.Collections.Generic.List<string>();
                if (Directory.Exists(Path.Combine(store, "builds"))) dirs.AddRange(Directory.GetDirectories(Path.Combine(store, "builds")));
                if (pl[1].Length > 0 && Directory.Exists(pl[1])) dirs.AddRange(Directory.GetDirectories(pl[1]));
                foreach (string d in dirs.Where(d => Directory.Exists(Path.Combine(d, "iwi"))).OrderBy(d => d))
                {
                    int n = Directory.GetFiles(Path.Combine(d, "iwi"), "*.iwi").Length;
                    bool on = installed != null && string.Equals(Path.GetFullPath(installed).TrimEnd('\\'), Path.GetFullPath(d).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                    _builds.Items.Add(new ListBoxItem { Tag = d, Content = Path.GetFileName(d) + "   (" + n + " images" + (on ? ", INSTALLED" : "") + ")" });
                }
                if (!keepStatus) _status.Text = _builds.Items.Count + " build(s) found." + (installed == null ? " None of ours is installed." : "");
            }
            catch (Exception ex) { App.Note("listing gun builds", ex); _status.Text = "Could not read the store: " + ex.Message; }
        }

        private void Install()
        {
            var item = _builds.SelectedItem as ListBoxItem;
            if (item == null) { _status.Text = "Pick a build first."; return; }
            // take the installed one out first: the installer refuses to overwrite what it did not place
            Run("-Remove", "taking ours out", () => Run("-Build \"" + (string)item.Tag + "\"", "installing " + Path.GetFileName((string)item.Tag)));
        }

        static string LiveCache { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reluduali", "Console", "live-camo"); } }

        static string ImageConverter() { return Path.Combine(Where.Tools, @"oat\v0.33.0\ImageConverter.exe"); }

        /// <summary>The picked build, in the running game at once: its images converted once into the cache, then swapped.</summary>
        private void TryLive()
        {
            var item = _builds.SelectedItem as ListBoxItem;
            if (item == null) { _status.Text = "Pick a build first."; return; }
            string build = (string)item.Tag, conv = ImageConverter();
            if (!File.Exists(conv)) { _status.Text = "Try live needs OpenAssetTools' ImageConverter at " + conv + "."; return; }
            _status.Text = "Try live: converting " + Path.GetFileName(build) + "'s images...";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                int swapped = 0, absent = 0, failed = 0; string first = null;
                try
                {
                    string cache = Path.Combine(LiveCache, Path.GetFileName(build));
                    Directory.CreateDirectory(cache);
                    foreach (string iwi in Directory.GetFiles(Path.Combine(build, "iwi"), "*.iwi"))
                    {
                        string name = Path.GetFileNameWithoutExtension(iwi);
                        string copy = Path.Combine(cache, Path.GetFileName(iwi)), dds = Path.ChangeExtension(copy, ".dds");
                        if (!File.Exists(dds) || File.GetLastWriteTimeUtc(dds) < File.GetLastWriteTimeUtc(iwi))
                        {
                            File.Copy(iwi, copy, true);
                            var psi = new ProcessStartInfo(conv, "--t6 \"" + copy + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                            using (Process p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(); }
                            try { File.Delete(copy); } catch { }
                        }
                        if (!File.Exists(dds)) { failed++; if (first == null) first = name + ": ImageConverter made no DDS"; continue; }
                        string name1 = name;
                        Dispatcher.BeginInvoke(new Action(() => _status.Text = "Try live: " + name1 + "..."));
                        string r = BridgeClient.Ask("img swap " + name + "|" + dds, 120000, 1) ?? ("no answer: " + BridgeClient.LastError);
                        if (r.StartsWith("ok", StringComparison.Ordinal)) swapped++;
                        else if (r.Contains("no loaded image")) absent++;
                        else { failed++; if (first == null) first = name + ": " + r; }
                    }
                }
                catch (Exception ex) { App.Note("GUNS try live", ex); first = ex.Message; failed++; }
                string said = "Try live: " + swapped + " image(s) swapped in the running game" + (absent > 0 ? ", " + absent + " not in this film or map" : "") +
                              (failed > 0 ? ", " + failed + " failed (" + first + ")" : "") + ". Back to the game's, a restart or another map puts the game's own back.";
                Dispatcher.BeginInvoke(new Action(() => _status.Text = said));
            });
        }

        private void Restore()
        {
            _status.Text = "Putting the game's own images back...";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string r = BridgeClient.Ask("img restore all", 20000, 1) ?? ("no answer: " + BridgeClient.LastError);
                Dispatcher.BeginInvoke(new Action(() => _status.Text = r.StartsWith("ok", StringComparison.Ordinal) ? "The game's own images are back." : "Back to the game's: " + r));
            });
        }

        /// <summary>Runs the installer hidden, off the UI thread; its last line becomes the status.</summary>
        private void Run(string args, string what, Action then = null)
        {
            string store = Places()[0], game = Game(), ps = Installer();
            _status.Text = what + "...";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string result;
                try
                {
                    var psi = new ProcessStartInfo("powershell.exe",
                        "-NoProfile -ExecutionPolicy Bypass -File \"" + ps + "\" " + args + " -Game \"" + game + "\" -Store \"" + store + "\"")
                        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    using (Process p = Process.Start(psi))
                    {
                        string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
                        p.WaitForExit();
                        string last = (o + "\n" + e).Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "(no output)";
                        result = (p.ExitCode == 0 ? "" : "FAILED: ") + last;
                    }
                }
                catch (Exception ex) { App.Note("GUNS " + what, ex); result = "failed: " + ex.Message; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _status.Text = what + ": " + result;
                    Refresh(true);
                    if (then != null && !result.StartsWith("FAILED", StringComparison.Ordinal) && !result.StartsWith("failed", StringComparison.Ordinal)) then();
                }));
            });
        }
    }
}
