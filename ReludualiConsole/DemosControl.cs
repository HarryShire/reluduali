using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>
    /// The DEMOS tab (2026-10-02, first cut; overgrow night 2026-10-10: search, map filter, playback,
    /// go to a time, bookmarks): the films in a folder (default: the game's own theater folder,
    /// Plugins\OpenNetStorage\Cloud\FILM_MP), map and date read from the name, and a safe load into the theater through
    /// RipperBridge. The load keeps the measured rules (2026-10-01): stop the playing film and wait for the theater to read 0
    /// first (loaddemo refuses otherwise), ONE load at a time, and never two loads within 10 s (two ~3 s apart crashed
    /// t6mpv43). Use the theater once per game start first ("theater first"): a film loaded before that plays without jump
    /// points. Seeking is the game's own demo_desiredTime; a time typed as m:ss is the theater clock, tick = ms + 2300 (the
    /// zero most films measured, BatchEngine's assumed zero).
    /// With the game not running, Load in theater starts it and asks for the theater to be opened first (2026-10-17: loaded
    /// straight from the main menu, a film keeps our keyframes but loses the game's own jump points).
    /// </summary>
    public class DemosControl : UserControl
    {
        private sealed class Film { public FileInfo File; public string Map, Label; }
        private sealed class Mark { public string Name; public int Tick; }

        private static readonly Regex Name = new Regex(@"^(?<mode>[a-z]+)_(?<map>mp_[a-z0-9_]+?)_(?<mo>\d+)_(?<d>\d+)_(?<y>\d+)_(?<h>\d+)_(?<mi>\d+)\.demo$", RegexOptions.IgnoreCase);
        private const int Zero = 2300;
        private readonly ListBox _list = new ListBox { Height = 300, Margin = new Thickness(0, 4, 0, 10), FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
        private readonly ListBox _marks = new ListBox { Height = 130, MinWidth = 300, Margin = new Thickness(0, 4, 0, 4) };
        private readonly TextBox _search = new TextBox { Width = 240, Margin = new Thickness(0, 0, 16, 6), ToolTip = "Part of a name, a map, a date" };
        private readonly ComboBox _map = new ComboBox { MinWidth = 170, Margin = new Thickness(0, 0, 8, 6) };
        private readonly TextBox _goto = new TextBox { Width = 90, Margin = new Thickness(0, 0, 8, 6), ToolTip = "1:57 (the theater clock) or a tick, e.g. 120000" };
        private readonly TextBlock _now = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 6), FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 14 };
        private readonly TextBlock _status = Ui.Status();
        private readonly TextBlock _where = Ui.Note("", 6);
        private readonly DispatcherTimer _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly List<Film> _all = new List<Film>();
        private string _folder, _playing;
        private int _loading, _tick = -1;
        private DateTime _lastLoad = DateTime.MinValue;
        /// <summary>The film this tab last loaded into the theater (the CAM QUEUE's fallback for "which demo", 2026-10-14).</summary>
        public static string LastLoaded { get; private set; }
        private static string MarksFile { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Presets", "DEMOS", "bookmarks.json"); } }

        private static string GameFilms() { return Where.InGame(@"Plugins\OpenNetStorage\Cloud\FILM_MP"); }

        public DemosControl()
        {
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Text");
            var filter = new WrapPanel();
            filter.Children.Add(Ui.Label("Search"));
            filter.Children.Add(_search);
            filter.Children.Add(Ui.Label("Map"));
            filter.Children.Add(_map);
            var row = new WrapPanel();
            row.Children.Add(Ui.Primary(Btn("Load in theater", (s, e) => Load())));
            row.Children.Add(Ui.Gap());
            row.Children.Add(Btn("Game's films", (s, e) => { _folder = GameFilms(); Refresh(); }));
            row.Children.Add(Btn("Choose folder...", (s, e) => Choose()));
            row.Children.Add(Btn("Refresh", (s, e) => Refresh()));
            var films = Ui.Card("Films", "Load one into the theater. Go to the theater by its menus and start one film there after the game starts, so films keep their jump points.", filter, _list, row, _where);

            var play = new WrapPanel();
            play.Children.Add(Ui.Primary(Btn("Play / pause", (s, e) => PlayPause())));
            play.Children.Add(Btn("End playback", (s, e) => Cmd("demo_stop", "ended")));
            play.Children.Add(Ui.Gap());
            play.Children.Add(Ui.Label("Go to"));
            play.Children.Add(_goto);
            play.Children.Add(Btn("Go", (s, e) => GoTo(ParseTime(_goto.Text))));
            play.Children.Add(_now);
            var playback = Ui.Card("Playback", null, play);

            var markRow = new WrapPanel();
            markRow.Children.Add(Btn("Mark here...", (s, e) => MarkHere()));
            markRow.Children.Add(Btn("Go to mark", (s, e) => { var m = _marks.SelectedItem as ListBoxItem; if (m != null) GoTo(((Mark)m.Tag).Tick); }));
            markRow.Children.Add(Ui.Gap());
            markRow.Children.Add(Btn("Remove mark", (s, e) => RemoveMark()));
            var marks = Ui.Card("Bookmarks", "Moments in the film loaded here, by name. Double-click one to go back to it.", markRow, _marks);
            Content = Ui.Page(films, playback, marks, _status);

            string remembered = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "demos.txt");
            try { if (File.Exists(remembered)) _folder = File.ReadAllText(remembered).Trim(); } catch { }
            if (string.IsNullOrEmpty(_folder)) _folder = GameFilms();
            _search.TextChanged += (s, e) => Show();
            _map.SelectionChanged += (s, e) => Show();
            _marks.MouseDoubleClick += (s, e) => { var m = _marks.SelectedItem as ListBoxItem; if (m != null) GoTo(((Mark)m.Tag).Tick); };
            _clockTimer.Tick += (s, e) => ReadClock();
            IsVisibleChanged += (s, e) =>
            {
                if ((bool)e.NewValue) { if (_all.Count == 0) Refresh(); _clockTimer.Start(); ShowMarks(); }
                else _clockTimer.Stop();
            };
        }

        private static Button Btn(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6) };
            b.Click += click;
            return b;
        }

        private static string Clock(int tick)
        {
            if (tick < 0) return "-";
            int ms = Math.Max(0, tick - Zero);
            return (ms / 60000) + ":" + (ms / 1000 % 60).ToString("00");
        }

        /// <summary>"1:57" (the theater clock) or "120000" (a tick); -1 if neither.</summary>
        private static int ParseTime(string s)
        {
            s = (s ?? "").Trim();
            var m = Regex.Match(s, @"^(\d+):(\d{1,2})(?:\.(\d+))?$");
            if (m.Success) return int.Parse(m.Groups[1].Value) * 60000 + int.Parse(m.Groups[2].Value) * 1000 + Zero;
            int t;
            return int.TryParse(s, out t) && t >= 0 ? t : -1;
        }

        private void Refresh()
        {
            _all.Clear();
            _where.Text = "Folder: " + _folder;
            try
            {
                if (!Directory.Exists(_folder)) { _status.Text = "No such folder."; Show(); return; }
                foreach (FileInfo f in Directory.GetFiles(_folder, "*.demo").Select(x => new FileInfo(x)).OrderByDescending(x => x.LastWriteTime))
                {
                    Match m = Name.Match(f.Name);
                    string label = m.Success
                        ? string.Format("{0,-16} {1,-5} {2}-{3:00}-{4:00} {5:00}:{6:00}   {7}", m.Groups["map"].Value, m.Groups["mode"].Value.ToUpperInvariant(),
                            m.Groups["y"].Value, int.Parse(m.Groups["mo"].Value), int.Parse(m.Groups["d"].Value),
                            int.Parse(m.Groups["h"].Value), int.Parse(m.Groups["mi"].Value), f.Name)
                        : f.Name;
                    _all.Add(new Film { File = f, Map = m.Success ? m.Groups["map"].Value.ToLowerInvariant() : "", Label = label + "   (" + f.Length / 1024 + " KB)" });
                }
                string keep = _map.SelectedItem as string;
                _map.Items.Clear();
                _map.Items.Add("all maps");
                foreach (string map in _all.Select(x => x.Map).Where(x => x.Length > 0).Distinct().OrderBy(x => x)) _map.Items.Add(map);
                _map.SelectedItem = keep != null && _map.Items.Contains(keep) ? keep : "all maps";
                _status.Text = _all.Count + " film(s).";
            }
            catch (Exception ex) { App.Note("listing films", ex); _status.Text = "Could not list: " + ex.Message; }
            Show();
        }

        private void Show()
        {
            _list.Items.Clear();
            string q = _search.Text.Trim(), map = _map.SelectedItem as string;
            foreach (Film f in _all)
            {
                if (q.Length > 0 && f.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (map != null && map != "all maps" && f.Map != map) continue;
                _list.Items.Add(new ListBoxItem { Tag = f.File.FullName, Content = f.Label, FontFamily = new System.Windows.Media.FontFamily("Consolas") });
            }
        }

        private void Choose()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "BO2 film|*.demo", Title = "Pick any film in the folder" };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            _folder = Path.GetDirectoryName(dlg.FileName);
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "demos.txt"), _folder); } catch { }
            Refresh();
        }

        private void Load()
        {
            var item = _list.SelectedItem as ListBoxItem;
            if (item == null) { _status.Text = "Pick a film first."; return; }
            double since = (DateTime.Now - _lastLoad).TotalSeconds;
            if (since < 10) { _status.Text = "Wait " + Math.Ceiling(10 - since) + " s: two loads close together crash the game."; return; }
            if (Interlocked.Exchange(ref _loading, 1) == 1) { _status.Text = "A load is already running."; return; }
            string path = (string)item.Tag;
            bool start = System.Diagnostics.Process.GetProcessesByName("t6mpv43").Length == 0;
            _status.Text = start ? "starting Redacted..." : "stopping the film that plays...";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string result;
                try
                {
                    // With the game not running it is started, and the film is NOT loaded yet. Mycelium (2026-10-17): "if you jump
                    // straight into demos, you will mess up the native jump tick ability ... An indicator to look out for is
                    // the Downloading: 0.00%". A film loaded from the main menu (sandbox 0512) keeps our keyframes and marker
                    // jumps but loses the game's own jump points; the theater has to be opened once first (theater first).
                    if (start)
                    {
                        string started = Where.StartGame();
                        if (!started.StartsWith("Starting", StringComparison.Ordinal)) throw new Exception(started);
                        DateTime t0 = DateTime.Now; string ping = null;
                        while ((DateTime.Now - t0).TotalSeconds < 180)
                        {
                            Thread.Sleep(2000);
                            ping = BridgeClient.Ask("ping", 1500, 1);
                            if (ping != null && ping.StartsWith("pong", StringComparison.Ordinal)) break;
                            Dispatcher.BeginInvoke(new Action(() => _status.Text = "starting Redacted (" + (int)(DateTime.Now - t0).TotalSeconds + " s)..."));
                        }
                        if (ping == null || !ping.StartsWith("pong", StringComparison.Ordinal)) throw new Exception("Redacted started, but RipperBridge did not answer within 3 minutes");
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _status.Text = "Redacted is up. Open THEATER from its menus (ONLINE, then THEATER) and start one film there, then press Load in theater again: " +
                                           "a film loaded before that has none of the game's own jump points (the HUD then says Downloading: 0.00 %).";
                        }));
                        Interlocked.Exchange(ref _loading, 0);
                        return;
                    }
                    string st = BridgeClient.Ask("stats", 1000, 2) ?? "";
                    if (!st.Contains("theater 0x00000000"))
                    {
                        BridgeClient.Ask("cmd demo_stop", 1000, 2);
                        DateTime t = DateTime.Now;
                        while ((DateTime.Now - t).TotalSeconds < 30)
                        {
                            Thread.Sleep(1000);
                            st = BridgeClient.Ask("stats", 1000, 2) ?? "";
                            if (st.Contains("theater 0x00000000")) break;
                        }
                        if (!st.Contains("theater 0x00000000")) throw new Exception("the film did not stop within 30 s; nothing loaded");
                        Thread.Sleep(3000);
                    }
                    _lastLoad = DateTime.Now;
                    result = BridgeClient.Ask("loaddemo " + path, 15000, 1) ?? ("no answer: " + BridgeClient.LastError);
                    if (result.StartsWith("ok", StringComparison.Ordinal)) { _playing = Path.GetFileName(path); LastLoaded = path; }
                }
                catch (Exception ex) { App.Note("loading a film", ex); result = "failed: " + ex.Message; }
                Dispatcher.BeginInvoke(new Action(() => { _status.Text = Path.GetFileName(path) + ": " + result; ShowMarks(); }));
                Interlocked.Exchange(ref _loading, 0);
            });
        }

        // ---------------------------------------------------------------- playback

        private void ReadClock()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string st = null;
                try { st = BridgeClient.Ask("stats", 400, 1); } catch { }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (st == null) { _now.Text = "(the game is not answering)"; _tick = -1; return; }
                    bool inTheater = !st.Contains("theater 0x00000000");
                    _tick = inTheater ? (int)BridgeClient.Field(st, "tick") : -1;
                    _now.Text = inTheater ? "now " + Clock(_tick) + "  (tick " + _tick + ")" : "no film loaded";
                }));
            });
        }

        private void Cmd(string command, string what)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string r = BridgeClient.Ask("cmd " + command, 1000, 2);
                Dispatcher.BeginInvoke(new Action(() => _status.Text = what + ": " + (r ?? BridgeClient.LastError)));
            });
        }

        private void PlayPause()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string r = BridgeClient.Ask("dvar demo_pause", 600, 1) ?? "";
                bool paused = r.TrimEnd().EndsWith(" 1");
                string sent = BridgeClient.Ask("cmd demo_pause " + (paused ? "0" : "1"), 1000, 2);
                Dispatcher.BeginInvoke(new Action(() => _status.Text = (paused ? "playing" : "paused") + " (" + (sent ?? BridgeClient.LastError) + ")"));
            });
        }

        /// <summary>The game's own seek (demo_desiredTime), watched until the clock lands within 1.5 s or 6 s pass.</summary>
        private void GoTo(int tick)
        {
            if (tick < 0) { _status.Text = "Type a time like 1:57, or a tick like 120000."; return; }
            _status.Text = "going to " + Clock(tick) + "...";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string result;
                BridgeClient.Ask("cmd demo_desiredTime " + tick.ToString(CultureInfo.InvariantCulture), 1000, 2);
                int landed = -1;
                DateTime t0 = DateTime.Now;
                while ((DateTime.Now - t0).TotalSeconds < 6)
                {
                    Thread.Sleep(300);
                    string st = BridgeClient.Ask("stats", 400, 1);
                    landed = (int)BridgeClient.Field(st, "tick");
                    if (Math.Abs(landed - tick) < 1500) break;
                }
                result = Math.Abs(landed - tick) < 1500 ? "at " + Clock(landed) + " (tick " + landed + ")"
                       : "did not land (now tick " + landed + "): start one film from the theater's own menus after the game starts, so films have jump points";
                Dispatcher.BeginInvoke(new Action(() => _status.Text = "go to " + Clock(tick) + ": " + result));
            });
        }

        // ---------------------------------------------------------------- bookmarks

        private static Dictionary<string, List<Mark>> ReadMarks()
        {
            try { if (File.Exists(MarksFile)) return new JavaScriptSerializer().Deserialize<Dictionary<string, List<Mark>>>(File.ReadAllText(MarksFile)) ?? new Dictionary<string, List<Mark>>(); }
            catch (Exception ex) { App.Note("reading bookmarks", ex); }
            return new Dictionary<string, List<Mark>>();
        }

        private static void WriteMarks(Dictionary<string, List<Mark>> all)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarksFile));
            File.WriteAllText(MarksFile, new JavaScriptSerializer().Serialize(all), new UTF8Encoding(false));
        }

        private void ShowMarks()
        {
            _marks.Items.Clear();
            if (_playing == null) { _marks.Items.Add(new ListBoxItem { Content = "(load a film here first: bookmarks belong to a film)", IsEnabled = false }); return; }
            List<Mark> list;
            if (!ReadMarks().TryGetValue(_playing, out list) || list.Count == 0) { _marks.Items.Add(new ListBoxItem { Content = "(no bookmarks for " + _playing + " yet)", IsEnabled = false }); return; }
            foreach (Mark m in list.OrderBy(x => x.Tick))
                _marks.Items.Add(new ListBoxItem { Tag = m, Content = Clock(m.Tick) + "   " + m.Name + "   (tick " + m.Tick + ")", FontFamily = new System.Windows.Media.FontFamily("Consolas") });
        }

        private void MarkHere()
        {
            if (_playing == null) { _status.Text = "Load a film here first: bookmarks belong to a film."; return; }
            if (_tick < 0) { _status.Text = "No clock from the game yet."; return; }
            int at = _tick;
            string name = Ask.Text(Window.GetWindow(this), "Bookmark", "A name for " + Clock(at) + ":", Clock(at));
            if (name == null) return;
            var all = ReadMarks();
            List<Mark> list;
            if (!all.TryGetValue(_playing, out list)) all[_playing] = list = new List<Mark>();
            list.Add(new Mark { Name = name.Length > 0 ? name : Clock(at), Tick = at });
            try { WriteMarks(all); _status.Text = "Marked " + Clock(at) + "."; } catch (Exception ex) { _status.Text = "Could not save the bookmark: " + ex.Message; }
            ShowMarks();
        }

        private void RemoveMark()
        {
            var item = _marks.SelectedItem as ListBoxItem;
            if (item == null || !(item.Tag is Mark) || _playing == null) return;
            var all = ReadMarks();
            List<Mark> list;
            if (all.TryGetValue(_playing, out list)) { list.RemoveAll(m => m.Tick == ((Mark)item.Tag).Tick && m.Name == ((Mark)item.Tag).Name); WriteMarks(all); }
            ShowMarks();
        }
    }
}
