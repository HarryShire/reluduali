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
using System.Windows.Input;
using System.Windows.Media;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>One entry of the game's own lists: a dvar (with type, default and range) or a command.</summary>
    public sealed class GameName
    {
        public string Name, Type, Flags, Current, Default, Domain, Description;
        public bool IsCommand;
        public bool Protected { get { return !IsCommand && ProtectedDvars.Contains(Name); } }
        public override string ToString() { return Name; }
        public string Info
        {
            get
            {
                if (IsCommand) return Name + "  (command)";
                return Name + "  " + Type + "   default " + Default + (Domain.Length > 0 ? "   range " + Domain : "") +
                       (Protected ? "   cheat-protected: the console clears the flag, then sets it" : "") + (Description.Length > 0 ? "   " + Description : "");
            }
        }

        static List<GameName> _all;

        /// <summary>The dvars and commands read off the running game on 2026-09-29 (GameNames\), embedded.</summary>
        public static List<GameName> All
        {
            get
            {
                if (_all != null) return _all;
                var list = new List<GameName>();
                var asm = typeof(GameName).Assembly;
                using (var s = asm.GetManifestResourceStream("dvars.tsv"))
                    if (s != null)
                        using (var r = new StreamReader(s))
                        {
                            r.ReadLine();
                            string line;
                            while ((line = r.ReadLine()) != null)
                            {
                                string[] f = line.Split('\t');
                                if (f.Length < 6) continue;
                                list.Add(new GameName { Name = f[0], Type = f[1], Flags = f[2], Current = f[3], Default = f[4], Domain = f[5], Description = f.Length > 6 ? f[6] : "" });
                            }
                        }
                using (var s = asm.GetManifestResourceStream("commands.txt"))
                    if (s != null)
                        using (var r = new StreamReader(s))
                        {
                            string line;
                            while ((line = r.ReadLine()) != null)
                                if (line.Trim().Length > 0) list.Add(new GameName { Name = line.Trim(), IsCommand = true, Type = "", Flags = "", Current = "", Default = "", Domain = "", Description = "" });
                        }
                _all = list.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
                return _all;
            }
        }

        public static IEnumerable<GameName> Matching(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return Enumerable.Empty<GameName>();
            var starts = All.Where(n => n.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase));
            var contains = All.Where(n => !n.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) && n.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
            return starts.Concat(contains).Take(max);
        }

        public static GameName Find(string name) { return All.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); }
    }

    /// <summary>
    /// The CFG tab (overgrow night 2026-10-10): one command at a time with completion over every dvar and command the
    /// game has, the timescale, a CFG box, and a library of CFG files.
    /// Everything goes through UserCfg.Send, so protected dvars and fog lines work here as everywhere else.
    /// </summary>
    public class CfgControl : UserControl
    {
        private readonly TextBox _cmd = new TextBox { MinWidth = 420, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 0, 8, 0) };
        private readonly Popup _pop = new Popup { Placement = PlacementMode.Bottom, StaysOpen = false };
        private readonly ListBox _list = new ListBox { MaxHeight = 260, MinWidth = 420, FontFamily = new FontFamily("Consolas") };
        private readonly TextBlock _info = new TextBlock { Opacity = 0.75, Margin = new Thickness(0, 4, 0, 10), TextWrapping = TextWrapping.Wrap };
        private readonly TextBox _box = new TextBox { AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"), MinHeight = 220,
                                                      VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap };
        private readonly TextBox _ts = new TextBox { Width = 60, Text = "1", Margin = new Thickness(0, 0, 8, 0) };
        private readonly ListBox _lib = new ListBox { MinHeight = 220, MinWidth = 260 };
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        private readonly List<string> _history = new List<string>();
        private int _historyAt = -1;
        private static string Folders { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cfg-folders.txt"); } }
        private static string Mine { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Presets", "CFG"); } }

        public CfgControl()
        {
            Foreground = (Brush)Application.Current.FindResource("Text");
            var root = new DockPanel { Margin = new Thickness(12) };

            var head = new StackPanel();
            var row = new WrapPanel();
            row.Children.Add(new TextBlock { Text = "Command", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(_cmd);
            row.Children.Add(Ui.Primary(Btn("Send", (s, e) => SendCommand())));
            row.Children.Add(Btn("Read", (s, e) => ReadNow(), "What the game holds for this dvar now"));
            row.Children.Add(Btn("Add to CFG", (s, e) => { if (_cmd.Text.Trim().Length > 0) _box.AppendText((_box.Text.Length > 0 && !_box.Text.EndsWith("\n") ? "\r\n" : "") + _cmd.Text.Trim() + "\r\n"); }));
            head.Children.Add(row);
            head.Children.Add(_info);
            var tsRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            tsRow.Children.Add(new TextBlock { Text = "Timescale", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            tsRow.Children.Add(_ts);
            tsRow.Children.Add(Btn("Send", (s, e) => Timescale(_ts.Text)));
            foreach (string t in new[] { "0.05", "0.1", "0.25", "0.5", "1", "2" })
            {
                string v = t;
                tsRow.Children.Add(Btn(t, (s, e) => { _ts.Text = v; Timescale(v); }));
            }
            head.Children.Add(tsRow);
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);
            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(_status);

            var lib = new DockPanel { Margin = new Thickness(12, 0, 0, 0) };
            var libHead = new StackPanel();
            libHead.Children.Add(new TextBlock { Text = "CFG library (double-click to open)", Margin = new Thickness(0, 0, 0, 4) });
            var libButtons = new WrapPanel();
            libButtons.Children.Add(Btn("Add folder...", (s, e) => AddFolder(), "A folder of .cfg files"));
            libButtons.Children.Add(Btn("Refresh", (s, e) => ListLibrary()));
            libHead.Children.Add(libButtons);
            DockPanel.SetDock(libHead, Dock.Top);
            lib.Children.Add(libHead);
            lib.Children.Add(_lib);
            DockPanel.SetDock(lib, Dock.Right);
            root.Children.Add(lib);

            var boxPanel = new DockPanel();
            var boxButtons = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            boxButtons.Children.Add(Ui.Primary(Btn("Send CFG", (s, e) => Send(_box.Text, "CFG"))));
            boxButtons.Children.Add(Btn("Send selection", (s, e) => Send(_box.SelectedText, "selection")));
            boxButtons.Children.Add(Ui.Gap());
            boxButtons.Children.Add(Btn("Open...", (s, e) => OpenFile()));
            boxButtons.Children.Add(Btn("Save as...", (s, e) => SaveFile()));
            boxButtons.Children.Add(Ui.Gap());
            boxButtons.Children.Add(Btn("Clear", (s, e) => _box.Clear()));
            DockPanel.SetDock(boxButtons, Dock.Top);
            boxPanel.Children.Add(boxButtons);
            boxPanel.Children.Add(_box);
            root.Children.Add(boxPanel);
            Content = root;

            _pop.Child = _list;
            _pop.PlacementTarget = _cmd;
            _cmd.TextChanged += (s, e) => Complete();
            _cmd.PreviewKeyDown += CommandKeys;
            _list.MouseDoubleClick += (s, e) => Accept();
            _lib.MouseDoubleClick += (s, e) => OpenLibraryItem();
            ListLibrary();
        }

        private void Complete()
        {
            string t = _cmd.Text;
            int sp = t.IndexOf(' ');
            string word = sp >= 0 ? t.Substring(0, sp) : t;
            var g = GameName.Find(word);
            _info.Text = g != null ? g.Info : "";
            if (sp >= 0 || word.Length < 2) { _pop.IsOpen = false; return; }
            var m = GameName.Matching(word, 40).ToList();
            _list.ItemsSource = m;
            _pop.IsOpen = m.Count > 0 && !(m.Count == 1 && m[0].Name.Equals(word, StringComparison.OrdinalIgnoreCase));
            if (_pop.IsOpen) _list.SelectedIndex = 0;
        }

        private void Accept()
        {
            var g = _list.SelectedItem as GameName;
            if (g == null) return;
            _cmd.Text = g.Name + " ";
            _cmd.CaretIndex = _cmd.Text.Length;
            _pop.IsOpen = false;
            _info.Text = g.Info;
            if (!g.IsCommand) ReadNow();
        }

        private void CommandKeys(object sender, KeyEventArgs e)
        {
            if (_pop.IsOpen)
            {
                if (e.Key == Key.Down) { _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1); _list.ScrollIntoView(_list.SelectedItem); e.Handled = true; }
                else if (e.Key == Key.Up) { _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1); _list.ScrollIntoView(_list.SelectedItem); e.Handled = true; }
                else if (e.Key == Key.Tab || e.Key == Key.Enter) { Accept(); e.Handled = true; }
                else if (e.Key == Key.Escape) { _pop.IsOpen = false; e.Handled = true; }
                return;
            }
            if (e.Key == Key.Enter) { SendCommand(); e.Handled = true; }
            else if (e.Key == Key.Up && _history.Count > 0)
            {
                _historyAt = _historyAt < 0 ? _history.Count - 1 : Math.Max(0, _historyAt - 1);
                _cmd.Text = _history[_historyAt]; _cmd.CaretIndex = _cmd.Text.Length; e.Handled = true;
            }
            else if (e.Key == Key.Down && _historyAt >= 0)
            {
                _historyAt++;
                _cmd.Text = _historyAt < _history.Count ? _history[_historyAt] : "";
                if (_historyAt >= _history.Count) _historyAt = -1;
                _cmd.CaretIndex = _cmd.Text.Length; e.Handled = true;
            }
        }

        private void SendCommand()
        {
            string c = _cmd.Text.Trim();
            if (c.Length == 0) return;
            _history.Remove(c); _history.Add(c); _historyAt = -1;
            Send(c, c);
            _cmd.SelectAll();
        }

        private void ReadNow()
        {
            string name = _cmd.Text.Trim().Split(' ')[0];
            if (name.Length == 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string r = null;
                try { r = BridgeClient.Ask("dvar " + name, 600, 1); } catch { }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var g = GameName.Find(name);
                    _info.Text = (g != null ? g.Info + "\n" : "") + "now in the game: " + (r ?? "(no answer: is the game running?)");
                }));
            });
        }

        private void Timescale(string v)
        {
            double d;
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) || d <= 0) { _status.Text = "Timescale: a number above 0."; return; }
            Send("timescale " + d.ToString(CultureInfo.InvariantCulture), "timescale");
        }

        private void Send(string text, string what)
        {
            if (string.IsNullOrWhiteSpace(text)) { _status.Text = "Nothing to send."; return; }
            _status.Text = "sending " + what + "...";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string r;
                try { r = UserCfg.Send(text, n => true, l => App.Note("CFG: " + l.Trim(), null)); }
                catch (Exception ex) { App.Note("CFG send", ex); r = "failed: " + ex.Message; }
                Dispatcher.BeginInvoke(new Action(() => _status.Text = what + "  ->  " + r));
            });
        }

        private void OpenFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "CFG (*.cfg)|*.cfg|All files|*.*" };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) Open(dlg.FileName);
        }

        private void Open(string path)
        {
            try
            {
                _box.Text = SettingsFiles.ToCfg(path, null);
                _status.Text = "Opened " + Path.GetFileName(path) + " (not sent yet: Send CFG).";
            }
            catch (Exception ex) { _status.Text = "Could not open " + Path.GetFileName(path) + ": " + ex.Message; }
        }

        private void SaveFile()
        {
            Directory.CreateDirectory(Mine);
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CFG (*.cfg)|*.cfg", InitialDirectory = Mine, DefaultExt = ".cfg" };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            try { File.WriteAllText(dlg.FileName, _box.Text, new UTF8Encoding(false)); _status.Text = "Saved " + dlg.FileName; ListLibrary(); }
            catch (Exception ex) { _status.Text = "Could not save: " + ex.Message; }
        }

        private IEnumerable<string> LibraryFolders()
        {
            yield return Mine;
            if (File.Exists(Folders))
                foreach (string l in File.ReadAllLines(Folders)) if (l.Trim().Length > 0 && !l.TrimStart().StartsWith("#")) yield return l.Trim();
        }

        private void ListLibrary()
        {
            var items = new List<ListBoxItem>();
            foreach (string folder in LibraryFolders())
            {
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (string f in Directory.GetFiles(folder).Where(f => f.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f))
                        items.Add(new ListBoxItem { Content = Path.GetFileName(f), Tag = f, ToolTip = f });
                }
                catch (Exception ex) { App.Note("listing " + folder, ex); }
            }
            _lib.ItemsSource = items;
        }

        private void OpenLibraryItem()
        {
            var it = _lib.SelectedItem as ListBoxItem;
            if (it != null) Open((string)it.Tag);
        }

        private void AddFolder()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Pick any file in the folder to add", Filter = "CFG (*.cfg)|*.cfg|All files|*.*" };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            string folder = Path.GetDirectoryName(dlg.FileName);
            try { File.AppendAllText(Folders, folder + Environment.NewLine); ListLibrary(); _status.Text = "Added " + folder; }
            catch (Exception ex) { _status.Text = "Could not add: " + ex.Message; }
        }

        private static Button Btn(string text, RoutedEventHandler click, string tip = null)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 4), ToolTip = tip, MinWidth = 36 };
            b.Click += click;
            return b;
        }
    }
}
