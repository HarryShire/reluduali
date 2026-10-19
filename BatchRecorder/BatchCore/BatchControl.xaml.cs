using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace BatchRecorder
{
    public partial class BatchControl : UserControl
    {
        private readonly GameClock _clock = new GameClock();
        private List<Take> _takes = new List<Take>();
        private BatchEngine _engine;
        private Thread _worker;

        /// <summary>The tab the host built, for the pipe (a host builds it once).</summary>
        public static BatchControl Instance { get; private set; }

        private readonly DispatcherTimer _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        private bool _restoring;

        public BatchControl()
        {
            InitializeComponent();
            FpsBox.ToolTip = "The film steps 1000/fps WHOLE milliseconds per frame: 600 and 1000 both give 1 ms, i.e. 1000 frames per second of replay. Exact rates: 1000, 500, 250, 200, 125, 100.";
            DepthBox.ToolTip = "A depth pass beside each take: a 16-bit PNG per frame in '<take> - depth', near white, black at the distance. About 230 MB a second of film at 1000 fps.";
            DepthDistBox.ToolTip = "The depth pass's distance in game units: there and past it is black.";
            EnterButton.ToolTip = "Posts ENTER to the game window, without taking focus.";
            Instance = this;
            // First-run defaults (saved settings replace them): empty, and Documents\Reluduali\Recordings - others test on
            // the 18th (overgrow night, 2026-10-10). 2026-10-16 (XF-0095): Mycelium's own folders are no longer written in here;
            // his are in his saved settings.
            CsvBox.Text = "";
            DemoRootBox.Text = "";
            OutBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Reluduali", "Recordings");
            Log("Batch Recorder " + BatchInfo.Version + ", hosted by " + ConsoleHost.Current.Name);
            RestoreSettings();
            _restoring = true;
            CfgBox.Text = UserCfg.Load();
            _restoring = false;
            Log(DryRunBox.IsChecked == true
                ? "Dry run is on: it plays past the take, steps back, and stops - it records nothing."
                : "Dry run is OFF: takes are recorded for real.");
            Log(LoadEachBox.IsChecked == true
                ? "Load each demo is on: each take loads its own demo."
                : "Load each demo is off: it uses whatever demo is on the theater screen.");
            WatchSettings();
            FollowList();
            BatchPipe.Start();
        }

        // ---- the take list followed live (2026-10-18): the CAM QUEUE adds a row, the list here shows it at once, 1:1 ----
        private FileSystemWatcher _listWatch;
        private readonly DispatcherTimer _listTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };

        private void FollowList()
        {
            _listTimer.Tick += (s, e) => { _listTimer.Stop(); if (!IsBusy && File.Exists(CsvBox.Text.Trim().Trim('"'))) LoadList(true); };
            CsvBox.TextChanged += (s, e) => WatchList();
            WatchList();
        }

        private void WatchList()
        {
            if (_listWatch != null) { _listWatch.EnableRaisingEvents = false; _listWatch.Dispose(); _listWatch = null; }
            string p = CsvBox.Text.Trim().Trim('"');
            try
            {
                string dir = p.Length > 0 ? Path.GetDirectoryName(Path.GetFullPath(p)) : null;
                if (dir == null || !Directory.Exists(dir)) return;
                _listWatch = new FileSystemWatcher(dir, Path.GetFileName(p)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                FileSystemEventHandler soon = (s, e) => Dispatcher.BeginInvoke(new Action(() => { _listTimer.Stop(); _listTimer.Start(); }));
                _listWatch.Changed += soon;
                _listWatch.Created += soon;
                _listWatch.Renamed += (s, e) => soon(s, e);      // a queue is saved to a temporary file, then swapped in
                _listWatch.EnableRaisingEvents = true;
                _listTimer.Stop(); _listTimer.Start();
            }
            catch (Exception ex) { Log("could not follow the take list " + p + ": " + ex.Message); }
        }

        /// <summary>The CAM QUEUE's list shown here and followed (it opened or made a queue). Null when it took.</summary>
        public string Follow(string csvPath)
        {
            if (IsBusy) return "a batch is running";
            if (!string.Equals(CsvBox.Text.Trim(), csvPath, StringComparison.OrdinalIgnoreCase)) CsvBox.Text = csvPath;
            else if (File.Exists(csvPath)) LoadList(true);
            return null;
        }

        public bool IsBusy { get { return _worker != null && _worker.IsAlive; } }

        // ---- hosted by the Designer's Batch page (2026-10-18) ----
        // Mycelium: "the batch tab should have these sort of global commands ... for POVs separately and then ... for cinematics
        // separately", "a detailed list inside of the batch queue". The Designer puts its POV / CINE global recording above the
        // Output card, its own take list where TakesBox was, and its night card beside Run; the rows those take over hide here.

        /// <summary>True when the Designer's page hosts this tab: per-take settings come from the queue, and what this tab's own
        /// hidden boxes hold is not used (fps 1000, no passes, 3 s either side: the Designer's global recording says the rest).</summary>
        public bool Hosted { get; private set; }

        /// <summary>The takes as last loaded, and a signal each time they are (the Designer's take list follows it).</summary>
        public IList<Take> Takes { get { return _takes; } }
        public event Action TakesLoaded;

        /// <summary>The list's take picked in the Designer's own take list (Run selected take runs it); -1 = none.</summary>
        public Func<int> PickedTake;

        public string CsvPath { get { return CsvBox.Text.Trim().Trim('"'); } }

        /// <summary>The codec list's names, in order (a take or a kind may name one of its own).</summary>
        public IList<string> CodecNames { get { return CodecBox.Items.OfType<ComboBoxItem>().Select(i => i.Content as string ?? "").ToList(); } }

        /// <summary>The Output card's codec now (the one a take that names none records with).</summary>
        public string CodecName { get { return (CodecBox.SelectedItem as ComboBoxItem)?.Content as string ?? ""; } }

        public void Host(UIElement global, UIElement takes, UIElement night)
        {
            Hosted = true;
            foreach (UIElement e in TimingRow.Children) e.Visibility = Visibility.Collapsed;
            PassesRow.Visibility = Visibility.Collapsed;
            RecordingTitle.Text = "Output: the recorder, ffmpeg, and the codec for takes that do not name their own";
            if (global != null) RecordingSlot.Children.Insert(0, global);
            if (night != null) RunSlot.Children.Add(night);
            if (takes != null)
            {
                TakesSlot.Child = takes;
                TakesColumn.Width = new GridLength(7, GridUnitType.Star);
                LogColumn.Width = new GridLength(3, GridUnitType.Star);
                ((Grid)TakesArea.Parent).RowDefinitions[Grid.GetRow(TakesArea)].MinHeight = 520;     // the page scrolls: the list gets room
            }
            AllButton.Visibility = Visibility.Collapsed;          // "Record all" is the night card's (it retries, then quits if asked)
        }

        /// <summary>The list read again (quietly) - the Designer changed a row or a kind's settings.</summary>
        public void Reload() { if (!IsBusy && File.Exists(CsvPath)) LoadList(true); }

        /// <summary>A line in the tab's log from outside the tab (a host's own fixes).</summary>
        public void Note(string line) { Log(line); }

        // ---- the tab's fields, kept between sessions (BatchSettings) ----

        private static string Flag(ToggleButton b) { return b.IsChecked == true ? "on" : "off"; }

        private static bool? ParseFlag(string v)
        {
            switch ((v ?? "").Trim().ToLowerInvariant())
            {
                case "on": case "1": case "true": case "yes": return true;
                case "off": case "0": case "false": case "no": return false;
                default: return null;
            }
        }

        private Dictionary<string, string> CurrentSettings()
        {
            var inv = CultureInfo.InvariantCulture;
            return new Dictionary<string, string>
            {
                { "csv", CsvBox.Text },
                { "demos", DemoRootBox.Text },
                { "out", OutBox.Text },
                { "fps", FpsBox.Text },
                { "depth", Flag(DepthBox) },
                { "depthdist", DepthDistBox.Text },
                { "plate", Flag(PlateBox) },
                { "noplayers", Flag(NoPlayersBox) },
                { "playersonly", Flag(PlayersOnlyBox) },
                { "nohud", Flag(NoHudBox) },
                { "before", BeforeSlider.Value.ToString("0.0", inv) },
                { "after", AfterSlider.Value.ToString("0.0", inv) },

                { "codec", (CodecBox.SelectedItem as ComboBoxItem)?.Content as string ?? "" },
                { "ffmpeg", FfmpegBox.Text },
                { "dryrun", Flag(DryRunBox) },
                { "loadeach", Flag(LoadEachBox) },
                { "codcast", Flag(CodcastBox) },
            };
        }


        /// <summary>Sets one field by its settings key. Null when it took, else why not.</summary>
        private string ApplySetting(string key, string value)
        {
            ToggleButton box = null;
            switch ((key ?? "").Trim().ToLowerInvariant())
            {
                case "csv": CsvBox.Text = value; return null;
                case "demos": DemoRootBox.Text = value; return null;
                case "out": OutBox.Text = value; return null;
                case "fps": FpsBox.Text = value; return null;
                case "depthdist": DepthDistBox.Text = value; return null;
                case "ffmpeg": FfmpegBox.Text = value; return null;
                case "before":
                case "after":
                    {
                        double d;
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                            return key + " wants seconds, e.g. 3 or 2.5";
                        Slider s = key.Equals("before", StringComparison.OrdinalIgnoreCase) ? BeforeSlider : AfterSlider;
                        s.Value = Math.Max(s.Minimum, Math.Min(s.Maximum, d));
                        return null;
                    }
                case "codec":
                    foreach (object o in CodecBox.Items)
                    {
                        var item = o as ComboBoxItem;
                        string name = item?.Content as string ?? "";
                        if (name.Equals(value, StringComparison.OrdinalIgnoreCase)
                            || (value.Length >= 3 && name.StartsWith(value, StringComparison.OrdinalIgnoreCase)))
                        { CodecBox.SelectedItem = item; return null; }
                    }
                    return "no codec named '" + value + "'";
                case "depth": box = DepthBox; break;
                case "plate": box = PlateBox; break;
                case "noplayers": box = NoPlayersBox; break;
                case "playersonly": box = PlayersOnlyBox; break;
                case "nohud": box = NoHudBox; break;
                case "video":
                    // an older settings file (before 2026-10-18): "video off" meant PNGs, now the codec list's last choice
                    if (ParseFlag(value) == false) foreach (object o in CodecBox.Items) if (((o as ComboBoxItem)?.Tag as string ?? "").StartsWith("png|")) CodecBox.SelectedItem = o;
                    return null;
                case "dryrun": box = DryRunBox; break;
                case "loadeach": box = LoadEachBox; break;
                case "codcast": box = CodcastBox; break;
                default: return "unknown setting '" + key + "'";
            }
            bool? on = ParseFlag(value);
            if (on == null) return key + " takes on or off";
            box.IsChecked = on.Value;
            return null;
        }

        private static int ParseDepthDistance(string s)
        {
            int v;
            return int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v >= 1 && v <= 1000000 ? v : 2000;
        }

        private void RestoreSettings()
        {
            Dictionary<string, string> saved = BatchSettings.Load();
            if (BatchSettings.LastError.Length > 0) Log("could not read the saved settings: " + BatchSettings.LastError);
            if (saved.Count == 0) { Log("no saved settings yet; they are kept from the first change on"); FillFfmpeg(); return; }

            _restoring = true;
            try
            {
                foreach (KeyValuePair<string, string> kv in saved)
                {
                    string why = ApplySetting(kv.Key, kv.Value);
                    if (why != null) Log("saved setting skipped: " + why);
                }
            }
            finally { _restoring = false; }
            Log("settings restored from " + BatchSettings.PathOnDisk);
            FillFfmpeg();
        }

        /// <summary>An empty ffmpeg box, or one naming a file that is gone, gets the ffmpeg found on this PC (2026-10-18).</summary>
        private void FillFfmpeg()
        {
            string now = FfmpegBox.Text.Trim().Trim('"');
            if (now.Length > 0 && File.Exists(now)) return;
            string found = FfmpegFinder.Find();
            if (found == null) { Log("ffmpeg: " + FfmpegFinder.Missing); return; }
            FfmpegBox.Text = found;          // saved like any change
            Log("ffmpeg: found " + found + (now.Length > 0 ? " (" + now + " is not there)" : ""));
        }

        private void WatchSettings()
        {
            _saveTimer.Tick += (s, e) =>
            {
                _saveTimer.Stop();
                if (!BatchSettings.Save(CurrentSettings())) Log("could not save the settings: " + BatchSettings.LastError);
                if (!UserCfg.Save(CfgBox.Text)) Log("could not save the CFG to " + UserCfg.PathOnDisk);
            };

            TextChangedEventHandler text = (s, e) => SettingChanged();
            foreach (TextBox b in new[] { CsvBox, DemoRootBox, OutBox, FpsBox, FfmpegBox, CfgBox, DepthDistBox }) b.TextChanged += text;

            RoutedEventHandler flag = (s, e) => SettingChanged();
            foreach (ToggleButton b in new ToggleButton[] { DepthBox, PlateBox, NoPlayersBox, PlayersOnlyBox, NoHudBox, DryRunBox, LoadEachBox, CodcastBox })
            { b.Checked += flag; b.Unchecked += flag; }

            BeforeSlider.ValueChanged += (s, e) => SettingChanged();
            AfterSlider.ValueChanged += (s, e) => SettingChanged();
            CodecBox.SelectionChanged += (s, e) => SettingChanged();
        }

        private void SettingChanged()
        {
            if (_restoring) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        // ---- what the pipe (BatchPipe) asks for; always called on the UI thread ----

        public string PipeStatus()
        {
            int missing = 0;
            foreach (Take t in _takes) if (t.DemoPath == null) missing++;
            string codec = (CodecBox.SelectedItem as ComboBoxItem)?.Content as string ?? "";
            return "batch: " + (IsBusy ? "running" : "idle") + "\n"
                 + "status: " + StatusText.Text + "\n"
                 + "takes: " + _takes.Count + " loaded" + (missing > 0 ? ", " + missing + " without a demo file" : "") + "\n"
                 + "dry run: " + Flag(DryRunBox) + "   load each demo: " + Flag(LoadEachBox)
                 + "   require CODCAST: " + Flag(CodcastBox) + "\n"
                 + "video: " + codec + "   fps " + FpsBox.Text
                 + "   passes: Depth " + Flag(DepthBox) + ", Clean plate " + Flag(PlateBox)
                 + ", No players " + Flag(NoPlayersBox) + ", Players only " + Flag(PlayersOnlyBox) + "\n"
                 + "out: " + OutBox.Text + "\n"
                 + "zero: " + (_zero >= 0 ? _zero.ToString() : "not measured") + "\n"
                 + "host: " + ConsoleHost.Current.Name
                 + (BatchInfo.HostReport.Length > 0 ? " (" + BatchInfo.HostReport + ")" : "");
        }

        public string PipeLog(int lines)
        {
            string[] all = (LogBox.Text ?? "").Replace("\r", "").Split('\n');
            int end = all.Length;
            while (end > 0 && all[end - 1].Length == 0) end--;
            int start = Math.Max(0, end - lines);
            return string.Join("\n", all, start, end - start);
        }

        public string PipeSettings()
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in CurrentSettings()) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            sb.Append("(saved in ").Append(BatchSettings.PathOnDisk).Append(')');
            return sb.ToString();
        }

        public string PipeSet(string key, string value)
        {
            if (IsBusy) return "refused: a batch is running; settings are read when it starts";
            string why = ApplySetting(key, value);
            if (why != null) return "error: " + why;
            Log("set by pipe: " + key + " = " + value);
            Dictionary<string, string> now = CurrentSettings();
            string shown;
            return key + " = " + (now.TryGetValue(key.ToLowerInvariant(), out shown) ? shown : value);
        }

        public string PipeLoad()
        {
            if (IsBusy) return "refused: a batch is running";
            Load_Click(null, null);
            return PipeTakes();
        }

        public string PipeTakes()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < TakesBox.Items.Count; i++) sb.Append(i).Append("  ").Append(TakesBox.Items[i]).Append('\n');
            sb.Append(_takes.Count).Append(" takes");
            return sb.ToString();
        }

        public string PipeRun(string which)
        {
            if (IsBusy) return "refused: a batch is already running";
            which = (which ?? "").Trim();
            if (which.Equals("all", StringComparison.OrdinalIgnoreCase)) RunAll_Click(null, null);
            else if (which.StartsWith("from ", StringComparison.OrdinalIgnoreCase))
            {
                // the rest of the list after a test take, or after a stop: take N to the end
                int from;
                if (!int.TryParse(which.Substring(5).Trim(), out from) || from < 0 || from >= _takes.Count)
                    return "usage: run from N, with N from 0 to " + (_takes.Count - 1);
                Start(_takes.GetRange(from, _takes.Count - from));
            }
            else
            {
                int i;
                if (!int.TryParse(which, out i) || i < 0 || i >= _takes.Count)
                    return "usage: run all | run N, with N from 0 to " + (_takes.Count - 1) + " ('takes' lists them)";
                TakesBox.SelectedIndex = i;
                Start(new List<Take> { _takes[i] });      // take i itself (hosted, the picked take is the Designer's list's)
            }
            // Started: the status line still shows what it said before (sandbox 2026-10-01: "started:
            // Redacted is not running" while it ran fine) - name the take instead; 'status' follows it.
            return IsBusy ? "started: " + which + " ('status' and 'log' follow it)" : "did not start: " + StatusText.Text;
        }

        public string PipeStop()
        {
            if (!IsBusy) return "nothing is running";
            Stop_Click(null, null);
            return StatusText.Text;
        }

        // ---- the CFG ----

        private string SendCfgNow()
        {
            if (!Attach()) return "cannot attach: " + _clock.Error;
            string summary = UserCfg.Send(CfgBox.Text, _clock.HasDvar, Log);
            Log(summary);
            Status(summary);
            return summary;
        }

        private void CfgSend_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy) { Status("A batch is running; it sends the CFG itself before each recording."); return; }
            SendCfgNow();
        }

        /// <summary>The pipe's "look" commands: list the Looks folder, or send a map's look now.</summary>
        public string PipeLook(string rest)
        {
            rest = (rest ?? "").Trim();
            if (rest.StartsWith("send", StringComparison.OrdinalIgnoreCase))
            {
                if (IsBusy) return "refused: a batch is running; it sends the look itself";
                string map = rest.Length > 4 ? rest.Substring(4).Trim().ToLowerInvariant() : Looks.CurrentMap();
                string s = Looks.Send(map, _clock.HasDvar, Log);
                Log(s);
                return s;
            }
            var names = new List<string>();
            try { if (System.IO.Directory.Exists(Looks.Folder)) foreach (string f in System.IO.Directory.GetFiles(Looks.Folder, "*.cfg")) names.Add(System.IO.Path.GetFileNameWithoutExtension(f)); }
            catch { }
            return "looks in " + Looks.Folder + ": " + (names.Count == 0 ? "none" : string.Join(", ", names.ToArray())) + "\nloaded map: " + (Looks.CurrentMap() ?? "(none)");
        }

        /// <summary>The pipe's "cfg" commands: show, send, add LINE, remove LINE.</summary>
        public string PipeCfg(string rest)
        {
            rest = (rest ?? "").Trim();
            if (rest.Length == 0) return CfgBox.Text.TrimEnd() + "\n(saved in " + UserCfg.PathOnDisk + ")";
            if (rest.Equals("send", StringComparison.OrdinalIgnoreCase))
                return IsBusy ? "refused: a batch is running; it sends the CFG itself" : SendCfgNow();
            if (rest.StartsWith("add ", StringComparison.OrdinalIgnoreCase))
            {
                string line = rest.Substring(4).Trim();
                CfgBox.Text = CfgBox.Text.TrimEnd('\r', '\n') + "\r\n" + line + "\r\n";
                return "added: " + line;
            }
            if (rest.StartsWith("remove ", StringComparison.OrdinalIgnoreCase))
            {
                string line = rest.Substring(7).Trim();
                var kept = new List<string>();
                int removed = 0;
                foreach (string l in CfgBox.Text.Replace("\r", "").Split('\n'))
                    if (l.Trim().Equals(line, StringComparison.OrdinalIgnoreCase)) removed++; else kept.Add(l);
                CfgBox.Text = string.Join("\r\n", kept.ToArray());
                return removed > 0 ? "removed " + removed + ": " + line : "no line '" + line + "'";
            }
            return "usage: cfg | cfg send | cfg add LINE | cfg remove LINE";
        }

        public string PipeState()
        {
            if (!Attach()) return "cannot attach: " + _clock.Error;
            return StateLine();
        }

        private void Log(string line)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LogBox.AppendText(line + Environment.NewLine);
                LogBox.ScrollToEnd();
            }));
        }

        private void Status(string s)
        {
            Dispatcher.BeginInvoke(new Action(() => StatusText.Text = s));
        }

        private bool Attach()
        {
            if (_clock.Attached && _clock.Alive) return true;
            if (_clock.Attached) Log("the game this tab was reading has exited; attaching to the running one");
            if (_clock.Attach()) { Log("attached to Redacted (pid " + _clock.ProcessId + ")"); return true; }
            Log("cannot attach: " + _clock.Error);
            Status(_clock.Error);
            return false;
        }

        /// <summary>
        /// Drops the tether to the game and makes it again, for a host's Re-attach button
        /// (Mycelium: "as simple as a Re Tether, Re Attach Button"). Refused while a batch
        /// runs, because the engine holds the same clock.
        /// </summary>
        public string Reattach()
        {
            if (IsBusy) return "a batch is running; stop it first";
            _clock.Dispose();
            if (!Attach()) return "Redacted: " + _clock.Error;
            string bridge = BridgeClient.Ask("ping", 800, 1);
            return "Redacted pid " + _clock.ProcessId + ", " + _clock.DvarCount + " dvars; RipperBridge "
                   + (bridge != null && bridge.StartsWith("pong") ? bridge.Substring(5) : "not answering");
        }

        private void Load_Click(object sender, RoutedEventArgs e) { LoadList(false); }

        /// <summary>Reads the take list into the list here. Quiet: a reload because the file changed (the picked row stays
        /// picked, and the log says it once, not as if the person had pressed Load).</summary>
        private void LoadList(bool quiet)
        {
            string error;
            int keep = TakesBox.SelectedIndex;
            _takes = TakeList.Load(CsvBox.Text.Trim().Trim('"'), DemoRootBox.Text.Trim(), out error);
            TakesBox.Items.Clear();
            int missing = 0;
            foreach (Take t in _takes)
            {
                TakesBox.Items.Add(t.ToString() + (t.DemoPath == null ? "   [no demo found]" : "") + (t.Fitted ? "   [fitted]" : ""));
                if (t.DemoPath == null) missing++;
            }
            if (quiet && keep >= 0 && keep < TakesBox.Items.Count) TakesBox.SelectedIndex = keep;
            if (error.Length > 0 && !quiet) Log(error);
            Log((quiet ? "the take list changed: " : "loaded ") + _takes.Count + " takes" + (missing > 0 ? ", " + missing + " without a demo file" : ""));
            if (TakesLoaded != null) { try { TakesLoaded(); } catch (Exception ex) { Log("the take list's host: " + ex.Message); } }
            Status(_takes.Count + " takes " + (quiet ? "in the list" : "loaded") + (missing > 0 ? " (" + missing + " missing their demo)" : ""));
        }

        private BatchEngine.Options ReadOptions()
        {
            // "ext|ffmpeg codec args" on the selected codec item
            string codecTag = (CodecBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "";
            int bar = codecTag.IndexOf('|');
            // hosted: the hidden boxes say nothing; a take's own, its preset's, its look's or its kind's settings say the rest,
            // and what none of them says is this (2026-10-18, Mycelium: a POV whose fps was never touched records at 1000)
            if (Hosted)
            {
                BatchEngine.Options h = ReadOptionsShown(codecTag, bar);
                h.Fps = 1000; h.PreRollMs = 3000; h.PostRollMs = 3000; h.HideHud = false;
                h.Depth = false; h.Plate = false; h.NoPlayers = false; h.PlayersOnly = false;
                return h;
            }
            return ReadOptionsShown(codecTag, bar);
        }

        private BatchEngine.Options ReadOptionsShown(string codecTag, int bar)
        {
            return new BatchEngine.Options
            {
                OutputDirectory = OutBox.Text.Trim(),
                Fps = ParseInt(FpsBox.Text, 1000),
                FfmpegPath = FfmpegBox.Text.Trim().Trim('"'),
                VideoExtension = bar > 0 ? codecTag.Substring(0, bar) : "mov",
                VideoCodecArgs = bar > 0 ? codecTag.Substring(bar + 1) : "-c:v dnxhd -profile:v dnxhr_hq -pix_fmt yuv422p",
                PreRollMs = (int)Math.Round(BeforeSlider.Value * 1000),
                PostRollMs = (int)Math.Round(AfterSlider.Value * 1000),
                Depth = DepthBox.IsChecked == true,
                DepthDistance = ParseDepthDistance(DepthDistBox.Text),
                Plate = PlateBox.IsChecked == true,
                NoPlayers = NoPlayersBox.IsChecked == true,
                PlayersOnly = PlayersOnlyBox.IsChecked == true,
                DryRun = DryRunBox.IsChecked == true,
                LoadEachDemo = LoadEachBox.IsChecked == true,
                RequireCodcast = CodcastBox.IsChecked == true,
                HideHud = NoHudBox.IsChecked == true,
                UserCfg = CfgBox.Text,
                Zero = _zero,
                // every codec in the list by its name, for a take that names its own (2026-10-18)
                Codecs = CodecBox.Items.OfType<ComboBoxItem>().Where(i => i.Tag is string).ToDictionary(i => (string)i.Content, i => (string)i.Tag, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > 0 ? v : fallback;
        }

        private void RunOne_Click(object sender, RoutedEventArgs e)
        {
            int i = Hosted && PickedTake != null ? PickedTake() : TakesBox.SelectedIndex;
            if (i < 0 || i >= _takes.Count) { Status("Pick a take in the list first."); return; }
            Start(new List<Take> { _takes[i] });
        }

        private void RunAll_Click(object sender, RoutedEventArgs e)
        {
            if (_takes.Count == 0) { Status("Load a take list first."); return; }
            Start(_takes);
        }

        /// <summary>The demo root this tab finds demos under (the CAM QUEUE looks films up there too).</summary>
        public string DemoRoot { get { return DemoRootBox.Text.Trim(); } }

        /// <summary>The output folder this tab records into.</summary>
        public string OutputFolder { get { return OutBox.Text.Trim(); } }

        /// <summary>The CFG box's text (a new queue's POV and CINE CFGs start as a copy of it).</summary>
        public string CfgText { get { return CfgBox.Text; } }

        /// <summary>
        /// The CAM QUEUE's RUN QUEUE (2026-10-14): the queue's own take list, recorded with this tab's settings (recorder,
        /// passes, codec, ffmpeg, output folder) except the two a night of many demos needs: every take loads its own
        /// demo, and dry run is what the caller says. The tab's own boxes are left as they are. Null when it started,
        /// else why not. <paramref name="finished"/> runs on the UI thread with the results (null if it threw).
        /// </summary>
        public string StartQueue(string csvPath, bool dryRun, Action<List<BatchEngine.Result>> finished)
        {
            if (IsBusy) return "a batch is already running";
            string error;
            List<Take> takes = TakeList.Load(csvPath, DemoRootBox.Text.Trim(), out error);
            if (error.Length > 0) return error;
            if (takes.Count == 0) return "the queue has no rows";
            int missing = takes.Count(t => t.DemoPath == null);
            if (missing > 0) return missing + " row(s) have no demo file - 'Check queue' lists them";

            CsvBox.Text = csvPath;        // the tab shows what is running
            _takes = takes;
            TakesBox.Items.Clear();
            foreach (Take t in _takes) TakesBox.Items.Add(t.ToString());
            if (TakesLoaded != null) { try { TakesLoaded(); } catch (Exception ex) { Log("the take list's host: " + ex.Message); } }
            Log("CAM QUEUE: " + takes.Count + " takes from " + csvPath + (dryRun ? " (dry run)" : "") + "; load each demo: on for this run");
            // the reason it did not start, in its own words (the status line only updates later: the code review's B-2)
            return Start(takes, o => { o.LoadEachDemo = true; o.DryRun = dryRun; }, finished);
        }

        /// <summary>Starts a run; null when it started, else why not.</summary>
        private string Start(List<Take> takes, Action<BatchEngine.Options> adjust = null, Action<List<BatchEngine.Result>> finished = null)
        {
            if (_worker != null && _worker.IsAlive) { Status("Already running."); return "a batch is already running"; }
            if (!Attach()) return "Redacted: " + _clock.Error;

            BatchEngine.Options opt = ReadOptions();
            if (adjust != null) adjust(opt);
            // the default folder is made the first time (a fresh install has none); one typed in must already be there
            string usual = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Reluduali", "Recordings");
            if (!opt.DryRun && !Directory.Exists(opt.OutputDirectory) && string.Equals(opt.OutputDirectory.TrimEnd('\\'), usual, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.CreateDirectory(usual); Log("made the recordings folder " + usual); }
                catch (Exception ex) { Log("could not make " + usual + ": " + ex.Message); }
            }
            if (!opt.DryRun && !Directory.Exists(opt.OutputDirectory))
            {
                Status("Output folder does not exist: " + opt.OutputDirectory);
                Log("refusing to record into a folder that is not there: " + opt.OutputDirectory);
                return "the output folder " + opt.OutputDirectory + " is not there (BATCH's Output folder)";
            }

            _engine = new BatchEngine(_clock, opt, Log);
            OneButton.IsEnabled = AllButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            Status((opt.DryRun ? "Dry run: " : "Recording: ") + takes.Count + " take(s)");

            _worker = new Thread(() =>
            {
                List<BatchEngine.Result> results = null;
                try { results = _engine.Run(takes); }
                catch (Exception ex) { Log("stopped: " + ex.GetBaseException().Message); }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    OneButton.IsEnabled = AllButton.IsEnabled = true;
                    StopButton.IsEnabled = false;
                    if (results != null)
                    {
                        int ok = 0;
                        foreach (var r in results) if (r.Ok) ok++;
                        Status(ok + " of " + results.Count + " finished");
                        WriteReport(results, opt);
                    }
                    if (finished != null)
                    {
                        try { finished(results); }
                        catch (Exception ex) { Log("after the run: " + ex.Message); }
                    }
                }));
            });
            _worker.IsBackground = true;
            _worker.Start();
            return null;
        }

        /// <summary>A run that leaves no record cannot be checked afterwards.</summary>
        private void WriteReport(List<BatchEngine.Result> results, BatchEngine.Options opt)
        {
            try
            {
                string dir = Directory.Exists(opt.OutputDirectory) ? opt.OutputDirectory : Path.GetTempPath();
                string path = Path.Combine(dir, "batch-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
                using (var w = new StreamWriter(path))
                {
                    w.WriteLine("take,folder,ok,zero,start_tick,landed_tick,files_written,message");
                    foreach (var r in results)
                        w.WriteLine(string.Join(",", r.Take.Number, "\"" + r.Take.Folder + "\"", r.Ok, r.Zero,
                                                r.StartTick, r.LandedTick, r.Frames, "\"" + r.Message + "\""));
                }
                Log("report: " + path);
            }
            catch (Exception ex) { Log("could not write the report: " + ex.Message); }
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (_engine != null) _engine.Cancel();
            Status("Stopping after the current step…");
        }

        /// <summary>Lists the host's own demo-loading methods into the log, so we can call the right one.</summary>
        private int _zero = -1;

        /// <summary>
        /// The on-screen clock is the only ground truth for this load's zero.
        /// Type what it reads while paused; zero = tick - clock, less half a
        /// second because the HUD shows whole seconds and we cannot tell where
        /// inside that second we are.
        /// </summary>
        private void SetZero_Click(object sender, RoutedEventArgs e)
        {
            if (!Attach()) return;
            int clock = TakeList.ParseClock(HudBox.Text);
            if (clock < 0) { Status("Type the clock as m:ss, e.g. 1:54"); return; }

            if (!_clock.IsStill()) { Status("Pause the replay first - the clock is still moving."); Log("not still; pause before setting the zero"); return; }
            int tick = _clock.Tick;
            _zero = tick - clock * 1000 - 500;
            ZeroText.Text = "zero: " + _zero;
            Log("clock " + Take.Clock(clock) + " at tick " + tick + "  ->  zero = " + _zero + " (+-500 ms)");
            Status("Zero set to " + _zero + " from the on-screen clock.");
        }

        /// <summary>Jumps back a few times and reports whether the landings sit on fixed points.</summary>
        private void Calibrate_Click(object sender, RoutedEventArgs e)
        {
            if (!Attach()) return;
            if (_worker != null && _worker.IsAlive) { Status("Busy."); return; }

            var engine = new BatchEngine(_clock, ReadOptions(), Log);
            _worker = new Thread(() =>
            {
                int zero;
                bool ok = engine.Calibrate(out zero);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ok) { _zero = zero; ZeroText.Text = "zero: " + zero; Status("Zero measured: " + zero); }
                    else Status("The landings disagree - set the zero from the clock instead.");
                }));
            });
            _worker.IsBackground = true;
            _worker.Start();
        }


        private void State_Click(object sender, RoutedEventArgs e)
        {
            if (!Attach()) return;
            Log(StateLine());
            Status("tick " + _clock.Tick + (_clock.TheaterBase != 0 ? ", in theater" : ", not in theater"));
        }

        private string StateLine()
        {
            int tick = _clock.Tick, end = _clock.EndTick, theater = _clock.TheaterBase, slot = _clock.PlayerIndex;
            string mode = _clock.IsCodcast ? "CODCAST" : "regular";
            return "tick " + tick + "   end " + end + "   theater " + (theater != 0 ? "yes" : "no") + "   slot " + slot
                + " (" + _clock.PlayerName(slot) + ")"
                + "   mode " + mode + " [" + _clock.DemoModeFlags + "]";
        }


        /// <summary>A command typed by hand, so an argument form can be tried without a rebuild.</summary>
        private void Send_Click(object sender, RoutedEventArgs e)
        {
            string command = (SendBox.Text ?? "").Trim();
            if (command.Length == 0) { Status("Nothing to send."); return; }

            // RipperBridge first (in-process, nothing lost); the host's route only if it does not answer.
            string reply = BridgeClient.Ask("cmd " + command, 1000, 2);
            if (reply == "ok") { Log("sent: " + command); Status("Sent: " + command); return; }
            if (ConsoleHost.Current.SendCommand(command)) { Log("sent through " + ConsoleHost.Current.Name + ": " + command); Status("Sent: " + command); }
            else
            {
                Log("could not send '" + command + "': RipperBridge " + (reply ?? BridgeClient.LastError) + "; host: " + ConsoleHost.Current.LastError);
                Status("Could not send it - see the log.");
            }
        }

        /// <summary>Posts ENTER to the game (a panel waiting for the key), without taking focus.</summary>
        private void Enter_Click(object sender, RoutedEventArgs e)
        {
            IntPtr h = GameKeys.GameWindow();
            if (h == IntPtr.Zero) { Log("no t6mpv43 window to post to"); Status("Game window not found."); return; }

            Log("posting ENTER to the game window (0x" + h.ToInt64().ToString("X") + ")");
            GameKeys.PressEnter();
            Status("ENTER posted - did the panel close?");
        }

        /// <summary>The spectate roster, read straight out of the game at a fixed offset.</summary>
        private void Roster_Click(object sender, RoutedEventArgs e)
        {
            if (!Attach()) return;
            int slot = _clock.PlayerIndex;
            string[] names = _clock.Roster();

            Log("--- roster (module+0x" + GameClock.RosterOffset.ToString("X") + ", stride "
                + GameClock.RosterStride + ") ---");
            int filled = 0;
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].Length == 0) continue;
                filled++;
                Log("  " + i.ToString().PadLeft(2) + "  " + names[i] + (i == slot ? "   <== spectating" : ""));
            }
            if (filled == 0) Log("  empty - is a demo actually playing?");
            Status(filled + " players; on slot " + slot
                   + (slot >= 0 && slot < names.Length && names[slot].Length > 0 ? " (" + names[slot] + ")" : ""));
        }

        /// <summary>
        /// Goes to a POV by name: demo_client first, clicking as the fallback, and
        /// the result checked against selectedPlayerIndex either way.
        /// </summary>
        private void Pov_Click(object sender, RoutedEventArgs e)
        {
            if (!Attach()) return;
            if (_worker != null && _worker.IsAlive) { Status("Busy."); return; }

            string pov = (PovBox.Text ?? "").Trim();
            if (pov.Length == 0) { Status("Type a gamertag first."); return; }

            var engine = new BatchEngine(_clock, ReadOptions(), Log);
            _worker = new Thread(() =>
            {
                bool ok = engine.GoToPovPublic(pov);
                int slot = _clock.PlayerIndex;
                Dispatcher.BeginInvoke(new Action(() =>
                    Status(ok ? "On " + pov + " (slot " + slot + ")" : "Could not reach " + pov + " - see the log.")));
            });
            _worker.IsBackground = true;
            _worker.Start();
        }

        /// <summary>
        /// Flips the CODCAST bit by writing to Redacted. Whether the game honours a
        /// bare write is the open question - the menu that normally sets this mode
        /// does other work besides setting a bit, so the test is the screen.
        /// </summary>
        private void Codcast_Click(object sender, RoutedEventArgs e)
        {
            if (!Attach()) return;
            if (!_clock.CanWrite)
            {
                Log("writing refused: this is '" + _clock.ProcessName + "', not Redacted");
                Status("Writing is only allowed against Redacted.");
                return;
            }

            // two buttons, one for each mode, never a toggle (Mycelium, 2026-10-18: "set CODCAST, set regular theater. Two buttons")
            bool want = !string.Equals((sender as FrameworkElement)?.Tag as string, "off", StringComparison.Ordinal);
            bool was = _clock.IsCodcast;
            if (was == want) { Status(want ? "Already in CODCAST." : "Already in the regular theater."); return; }
            Log("demo mode flags " + _clock.DemoModeFlags + ", codcast " + (was ? "on" : "off") + " -> setting " + (want ? "on" : "off"));

            if (!_clock.SetCodcast(want)) { Log("  " + _clock.Error); Status("Write failed - see the log."); return; }

            System.Threading.Thread.Sleep(300);
            Log("  flags now " + _clock.DemoModeFlags + ", codcast " + (_clock.IsCodcast ? "on" : "off"));
            Status(_clock.IsCodcast == want ? (want ? "CODCAST." : "The regular theater.") : "The switch did not take - see the log.");
        }

        /// <summary>A tick only counts once two reads agree; otherwise it was sampled mid-move.</summary>
        private int SettledTick()
        {
            for (int i = 0; i < 40; i++)
            {
                int a = _clock.Tick;
                Thread.Sleep(120);
                if (a == _clock.Tick && a != int.MinValue) return a;
            }
            Log("  the clock never settled");
            return int.MinValue;
        }
    }
}
