using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ReludualiConsole
{
    /// <summary>
    /// Where things are on THIS PC (overgrow night, 2026-10-10: "others will be testing" on the 18th, so nothing may assume
    /// Mycelium's drives). The game folder: the running game, then the one saved here, then the one the installer wrote, then
    /// the usual Steam places. The stores (skies, gun builds, textures, the tools): the one saved here, then the old *.txt
    /// beside the console, then a folder beside the console. Saved in %LOCALAPPDATA%\Reluduali\Console\places.txt.
    /// 2026-10-16 (XF-0095): no path of Mycelium's is written in here any more, since the build leaves the PC; his own
    /// folders live in his places.txt.
    /// </summary>
    public static class Where
    {
        static string File_ { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reluduali", "Console", "places.txt"); } }
        static string Beside(params string[] parts) { return Path.Combine(new[] { AppDomain.CurrentDomain.BaseDirectory }.Concat(parts).ToArray()); }

        static Dictionary<string, string> Read() { bool whole; return Read(out whole); }

        /// <summary>Every place saved; <paramref name="whole"/> is false when the file was there but could not be read.</summary>
        static Dictionary<string, string> Read(out bool whole)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            whole = true;
            try
            {
                if (File.Exists(File_))
                    foreach (string l in File.ReadAllLines(File_))
                    {
                        int eq = l.IndexOf('=');
                        if (eq > 0) d[l.Substring(0, eq).Trim()] = l.Substring(eq + 1).Trim();
                    }
            }
            catch { whole = false; }
            return d;
        }

        public static string Saved(string key) { string v; return Read().TryGetValue(key, out v) && v.Length > 0 ? v : null; }

        public static void Save(string key, string value)
        {
            bool whole;
            var d = Read(out whole);
            // a file that could not be read is never written over with one place in it (the code review's P-5)
            if (!whole) throw new IOException("places.txt could not be read just now: nothing saved");
            d[key] = value ?? "";
            Directory.CreateDirectory(Path.GetDirectoryName(File_));
            string tmp = File_ + ".tmp";                                  // whole or not at all
            File.WriteAllLines(tmp, d.Select(kv => kv.Key + "=" + kv.Value));
            if (File.Exists(File_)) File.Replace(tmp, File_, null); else File.Move(tmp, File_);
        }

        // Redacted's own exe only: the Designer works with Redacted, never with retail Black Ops II (the code review's C-20)
        static bool IsGame(string dir) { return dir != null && File.Exists(Path.Combine(dir, "t6mpv43.exe")); }

        /// <summary>The game's folder, or null when it cannot be found (start the game once, or choose it on SETUP).</summary>
        public static string Game
        {
            get
            {
                try
                {
                    Process p = Process.GetProcessesByName("t6mpv43").FirstOrDefault();
                    if (p != null)
                    {
                        string dir = Path.GetDirectoryName(p.MainModule.FileName);
                        if (Saved("game") != dir) Save("game", dir);
                        return dir;
                    }
                }
                catch { }
                string s = Saved("game");
                if (IsGame(s)) return s;
                string installed = Installed;
                if (IsGame(installed)) { Save("game", installed); return installed; }
                foreach (string c in SteamCandidates()) if (IsGame(c)) return c;
                return null;
            }
        }

        /// <summary>The Redacted folder the installer asked for (ReludualiSetup writes one line: the folder), or null.</summary>
        static string Installed
        {
            get
            {
                try
                {
                    string f = Path.Combine(Path.GetDirectoryName(File_), "redacted-folder.txt");
                    return File.Exists(f) ? File.ReadAllLines(f).Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) : null;
                }
                catch { return null; }
            }
        }

        /// <summary>Starts Redacted (t6mpv43.exe in the game folder) with the arguments saved on SETUP; the tether finds it
        /// and the workspace docks it as when it is started by hand. Returns a sentence for the person.</summary>
        public static string StartGame()
        {
            try
            {
                if (Process.GetProcessesByName("t6mpv43").Length > 0) return "Redacted is already running.";
                string g = Game;
                if (g == null) return "The game folder is not known: choose t6mpv43.exe on SETUP.";
                string exe = Path.Combine(g, "t6mpv43.exe");
                if (!File.Exists(exe)) return "There is no t6mpv43.exe in " + g + ".";
                // a +wait between every two +commands: Redacted loses every second one (sandbox 0561/0562; GameCommandLine)
                Process.Start(new ProcessStartInfo(exe, GameCommandLine.Join(Saved("gameargs") ?? "")) { WorkingDirectory = g, UseShellExecute = false });
                return "Starting Redacted: the Designer takes it in once its window is up.";
            }
            catch (Exception ex) { App.Note("starting Redacted", ex); return "Could not start Redacted: " + ex.Message; }
        }

        static IEnumerable<string> SteamCandidates()
        {
            string steam = @"C:\Program Files (x86)\Steam";
            yield return Path.Combine(steam, @"steamapps\common\Call of Duty Black Ops II");
            string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            string text = null;
            try { if (File.Exists(vdf)) text = File.ReadAllText(vdf); } catch { }
            if (text != null)
                foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                    yield return Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), @"steamapps\common\Call of Duty Black Ops II");
        }

        /// <summary>A folder inside the game's, or a plain sentence saying the game folder is not known (never Mycelium's path).</summary>
        public static string InGame(string sub)
        {
            string g = Game;
            return g == null ? "(the game folder is not known: see SETUP)" : Path.Combine(g, sub);
        }

        static string Store(string key, string besideTxt, string besideFolder)
        {
            string s = Saved(key);
            if (s != null) return s;
            try
            {
                string f = Beside(besideTxt);
                if (File.Exists(f)) { string t = File.ReadAllLines(f).Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0); if (t != null) return t; }
            }
            catch { }
            string b = Beside("Stores", besideFolder);
            try { Directory.CreateDirectory(b); } catch { }
            return b;
        }

        public static string SkyStore { get { return Store("skies", "skies.txt", "Skies"); } }
        public static string GunStore { get { return Store("guns", "guns.txt", "Guns"); } }

        public static string Textures
        {
            get
            {
                string s = Saved("textures");
                if (s != null) return s;
                try { string f = Beside("guns.txt"); if (File.Exists(f)) { var l = File.ReadAllLines(f); if (l.Length > 1 && l[1].Trim().Length > 0) return l[1].Trim(); } } catch { }
                return Beside("Stores", "Textures");
            }
        }

        /// <summary>The tools folder: packed beside the console, else the project's.</summary>
        public static string Tools
        {
            get
            {
                string s = Saved("tools");
                if (s != null && Directory.Exists(s)) return s;
                return Beside("tools");
            }
        }
    }

    /// <summary>The SETUP tab: where the game and the stores are on this PC, each changeable.</summary>
    public class SetupControl : UserControl
    {
        readonly StackPanel _rows = new StackPanel();

        public SetupControl()
        {
            Foreground = (Brush)Application.Current.FindResource("Text");
            Content = Ui.Page(Ui.Card("This PC", "Where things are, and what this PC has. The game folder is found by itself while the game runs; choose it here if it is not.", _rows),
                              MapShapes.Card(), AppearanceCard.Make(), UpdatesCard());
            IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) Fill(); };
        }

        /// <summary>The update notifier's card (2026-10-18): which Designer this is, what the Reluduali Server last said, Check now.</summary>
        static UIElement UpdatesCard()
        {
            var said = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            Action paint = () => said.Text = "This is the Reluduali Designer " + AccountLink.Version + ", " + Unfinished.Edition + " edition" + (Unfinished.IsTest ? ", Test Build" : "") +
                                             ". Last asked: " + UpdateNotifier.LastResult + ".";
            paint();
            var check = new Button { Content = "Check now", Margin = new Thickness(0, 0, 8, 6), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Ask " + TimerLink.ConfiguredServer() + " now" };
            check.Click += (s, e) =>
            {
                if (UpdateNotifier.Shared == null) return;
                said.Text = "Asking...";
                UpdateNotifier.Shared.Check(true, r => paint());
            };
            var panel = new StackPanel();
            panel.Children.Add(said);
            panel.Children.Add(check);
            panel.IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) paint(); };
            return Ui.Card("Updates", "The Designer asks reluduali.com about new versions and notices when it starts and every few hours, and shows them under the header. Nothing about you is sent.", panel);
        }

        void Fill()
        {
            _rows.Children.Clear();
            Row("Game folder (t6mpv43.exe)", Where.Game ?? "not found: start the game once, or Choose", "game", true);
            Row("Sky sets", Where.SkyStore, "skies", false);
            Row("Gun builds", Where.GunStore, "guns", false);
            Row("Gun textures", Where.Textures, "textures", false);
            Row("Contest pass and these places", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reluduali", "Console"), null, false);
            Checks();
        }

        // ---------------------------------------------------------------- what this PC has (for testers, 2026-10-18)

        static string OnPath(string exe)
        {
            foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { string f = Path.Combine(d.Trim(), exe); if (d.Trim().Length > 0 && File.Exists(f)) return f; } catch { }
            }
            return null;
        }


        static string BridgeShipped { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugin", "RipperBridge.Red32n"); } }

        void Check(string what, bool ok, string said)
        {
            var r = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            r.Children.Add(new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = (Brush)Application.Current.FindResource(ok ? "Good" : "Bad"), Margin = new Thickness(2, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center });
            r.Children.Add(new TextBlock { Text = what, Width = 202 });
            r.Children.Add(new TextBlock { Text = said, TextWrapping = TextWrapping.Wrap, MaxWidth = 700 });
            _rows.Children.Add(r);
        }

        void Checks()
        {
            _rows.Children.Add(Ui.Heading("What this PC has", 16));
            string game = Where.Game;
            string plugins = game == null ? null : Path.Combine(game, "Plugins");
            string bridge = plugins == null ? null : Path.Combine(plugins, "RipperBridge.Red32n");
            bool hasBridge = bridge != null && File.Exists(bridge);
            string ping = null;
            try { if (System.Diagnostics.Process.GetProcessesByName("t6mpv43").Length > 0) ping = BatchRecorder.BridgeClient.Ask("ping", 500, 1); } catch { }
            Check("RipperBridge in the game", hasBridge, hasBridge ? (ping != null ? ping.Replace("pong ", "") + " (answering)" : File.GetLastWriteTime(bridge).ToString("yyyy-MM-dd HH:mm") + ", " + new FileInfo(bridge).Length / 1024 + " KB (the game is not running)")
                                                               : "not installed: the console talks to the game through it. Install it below.");
            string ff = null;
            try { string v; if (BatchRecorder.BatchSettings.Load().TryGetValue("ffmpeg", out v) && File.Exists(v)) ff = v; } catch { }
            ff = ff ?? BatchRecorder.FfmpegFinder.Find();
            Check("ffmpeg", ff != null, ff ?? "not found: the Reluduali recorder writes video through it. Install it (in PowerShell: winget install Gyan.FFmpeg), or point BATCH's ffmpeg box at ffmpeg.exe.");

            var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            var install = new Button { Content = "Install RipperBridge into the game", Margin = new Thickness(0, 0, 12, 0), IsEnabled = File.Exists(BridgeShipped) && plugins != null, Style = Ui.Style("PrimaryButton") };
            install.Click += (s, e) => MessageText(InstallBridge(plugins));
            row.Children.Add(install);
            row.Children.Add(new TextBlock { Text = File.Exists(BridgeShipped) ? "from " + BridgeShipped : "(this console has no plugin\\RipperBridge.Red32n beside it)", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
            _rows.Children.Add(row);

            // Start Redacted from the Console (Mycelium, 2026-10-16): the options it starts with, saved as typed
            _rows.Children.Add(Ui.Heading("Starting Redacted", 16));
            var args = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            args.Children.Add(new TextBlock { Text = "Start options", Width = 220, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("Dim") });
            var argBox = new TextBox { Text = Where.Saved("gameargs") ?? "", MinWidth = 420, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Passed to t6mpv43.exe by Start Redacted, e.g. +set com_maxfps 250" };
            argBox.TextChanged += (s, e) => { try { Where.Save("gameargs", argBox.Text); } catch { } };
            args.Children.Add(argBox);
            var start = new Button { Content = "Start Redacted", IsEnabled = game != null };
            start.Click += (s, e) => MessageText(Where.StartGame());
            args.Children.Add(start);
            _rows.Children.Add(args);

            // 0051: Lumit's Reluduali panel opens the Console through a reluduali-console: link (Lumit asks first, every time)
            _rows.Children.Add(Ui.Heading("Lumit", 16));
            var link = new CheckBox
            {
                Content = "Lumit can open the Designer: a reluduali-console: link, for a Reluduali panel with an Open button (Lumit asks you first, every time)",
                IsChecked = ConsoleLink.Registered, Margin = new Thickness(0, 4, 0, 4),
            };
            link.Click += (s, e) =>
            {
                try
                {
                    if (link.IsChecked == true) { ConsoleLink.Register(); MessageText("Registered reluduali-console: for this Windows user. Opened by it, the Designer only comes to the front."); }
                    else { ConsoleLink.Unregister(); MessageText("Removed the reluduali-console: link."); }
                }
                catch (Exception ex) { App.Note("the reluduali-console: link", ex); MessageText("Could not change the link: " + ex.Message); link.IsChecked = ConsoleLink.Registered; }
            };
            _rows.Children.Add(link);
        }

        void MessageText(string s) { _rows.Children.Add(new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) }); }

        /// <summary>Copies the console's RipperBridge into the game's Plugins; an older one is moved out of Plugins first, never deleted.</summary>
        static string InstallBridge(string plugins)
        {
            try
            {
                if (System.Diagnostics.Process.GetProcessesByName("t6mpv43").Length > 0) return "Close the game first: it holds the plugin while it runs.";
                if (plugins == null || !Directory.Exists(plugins)) return "The game's Plugins folder is not known: choose the game folder above.";
                string target = Path.Combine(plugins, "RipperBridge.Red32n");
                if (File.Exists(target))
                {
                    string keep = Path.Combine(Path.GetDirectoryName(plugins), "reluduali-backup");     // outside Plugins: the game loads every .Red32n there
                    Directory.CreateDirectory(keep);
                    string old = Path.Combine(keep, "RipperBridge.Red32n." + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    File.Move(target, old);
                    File.Copy(BridgeShipped, target);
                    return "Installed. The one that was there is kept as " + old + ". Start the game.";
                }
                File.Copy(BridgeShipped, target);
                return "Installed. Start the game.";
            }
            catch (Exception ex) { App.Note("installing RipperBridge", ex); return "Could not install: " + ex.Message; }
        }

        void Row(string label, string value, string key, bool game)
        {
            var r = new WrapPanel { Margin = new Thickness(0, 3, 0, 3) };
            r.Children.Add(new TextBlock { Text = label, Width = 220, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("Dim") });
            r.Children.Add(new TextBlock { Text = value, MinWidth = 420, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), TextWrapping = TextWrapping.Wrap });
            if (key != null)
            {
                var b = new Button { Content = "Choose...", Margin = new Thickness(0, 0, 8, 0) };
                b.Click += (s, e) =>
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = game ? "Pick t6mpv43.exe in the game folder" : "Pick any file in the folder (or type a name in it)",
                        Filter = game ? "Redacted BO2 (t6mpv43.exe)|t6mpv43.exe|All files|*.*" : "All files|*.*",
                        CheckFileExists = game, FileName = game ? "t6mpv43.exe" : "this folder",
                    };
                    if (dlg.ShowDialog(Window.GetWindow(this)) == true) { try { Where.Save(key, Path.GetDirectoryName(dlg.FileName)); } catch (Exception ex) { MessageText(ex.Message); } Fill(); }
                };
                r.Children.Add(b);
            }
            _rows.Children.Add(r);
        }
    }
}
