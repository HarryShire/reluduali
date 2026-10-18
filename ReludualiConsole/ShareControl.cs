using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace ReludualiConsole
{
    /// <summary>
    /// Share to reluduali.com (2026-10-18): the person's own camos, CFGs, page presets, global looks, map looks and custom
    /// sliders, put on the site from the Designer with the Reluduali sign-in (the "shares" right, allowed in the browser).
    /// The files are read from where the Designer keeps them (Presets\... and Looks\ beside it; a camo is a Camo Editor
    /// package folder) and sent as they are; nothing is changed here. The contract: docs\reluduali-server-contract.md.
    /// </summary>
    public sealed class ShareControl : StackPanel
    {
        sealed class Kind { public string Key, Label, Hint; public Func<IEnumerable<string>> List; public bool Folder; }

        static string Beside(params string[] p) { return Path.Combine(new[] { AppDomain.CurrentDomain.BaseDirectory }.Concat(p).ToArray()); }

        static IEnumerable<string> Files(string dir, string pattern)
        {
            try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase) : Enumerable.Empty<string>(); }
            catch { return Enumerable.Empty<string>(); }
        }

        // the folders under Presets that are not one page's presets
        static readonly string[] NotPages = { "GLOBAL", "CFG", "CUSTOM", "CAMQUEUE", "DEMOS" };

        static readonly Kind[] Kinds =
        {
            new Kind { Key = "global", Label = "Global look", Hint = "Presets\\GLOBAL: one look across every page", List = () => Files(Beside("Presets", "GLOBAL"), "*.cfg") },
            new Kind { Key = "preset", Label = "Page preset", Hint = "Presets\\<page>: one page's preset (Fog, Sun, DOF ...)", List = () =>
                {
                    try
                    {
                        string root = Beside("Presets");
                        return Directory.Exists(root) ? Directory.GetDirectories(root).Where(d => !NotPages.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                                                                .SelectMany(d => Files(d, "*.cfg")) : Enumerable.Empty<string>();
                    }
                    catch { return Enumerable.Empty<string>(); }
                } },
            new Kind { Key = "cfg", Label = "CFG", Hint = "Presets\\CFG: your CFG files", List = () => Files(Beside("Presets", "CFG"), "*.cfg") },
            new Kind { Key = "look", Label = "Map look", Hint = "Looks\\<map>.cfg: a map's look, sent before every recording on it", List = () => Files(SafeLooks(), "*.cfg") },
            new Kind { Key = "sliders", Label = "Custom sliders", Hint = "Presets\\CUSTOM\\sliders.json: your own sliders", List = () => Files(Beside("Presets", "CUSTOM"), "sliders.json") },
            new Kind { Key = "camo", Label = "Camo design", Hint = "A Camo Editor package folder (Publish... makes one: manifest.json, layers.psd, previews)", Folder = true, List = () => Enumerable.Empty<string>() },
        };

        static string SafeLooks() { try { return BatchRecorder.Looks.Folder; } catch { return Beside("Looks"); } }

        readonly ComboBox _kind = new ComboBox { MinWidth = 200, Margin = new Thickness(0, 0, 8, 6) };
        readonly ComboBox _file = new ComboBox { MinWidth = 320, Margin = new Thickness(0, 0, 8, 6) };
        readonly Button _pick;
        readonly TextBox _name = new TextBox { Width = 260, Margin = new Thickness(0, 0, 8, 6), ToolTip = "Its name on reluduali.com" };
        readonly TextBox _about = new TextBox { Width = 420, Margin = new Thickness(0, 0, 8, 6), ToolTip = "A line about it (optional)" };
        readonly Button _share;
        readonly TextBlock _hint = Ui.Note("", 2);
        readonly TextBlock _said = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        readonly StackPanel _mine = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        readonly TextBlock _gate = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85, Margin = new Thickness(0, 0, 0, 6) };
        string _camoFolder;

        public ShareControl()
        {
            foreach (var k in Kinds) _kind.Items.Add(k.Label);
            _pick = Btn("Pick the package folder...", (s, e) => PickCamo(), "The folder the Camo Editor's Publish... made (it holds manifest.json)");
            _share = Ui.Primary(Btn("Share to reluduali.com", (s, e) => Share(), "Puts it on reluduali.com under your name"));

            Children.Add(_gate);
            var r1 = new WrapPanel(); r1.Children.Add(Label("What")); r1.Children.Add(_kind); Children.Add(r1);
            var r2 = new WrapPanel(); r2.Children.Add(Label("Which")); r2.Children.Add(_file); r2.Children.Add(_pick); Children.Add(r2);
            Children.Add(_hint);
            var r3 = new WrapPanel(); r3.Children.Add(Label("Name")); r3.Children.Add(_name); Children.Add(r3);
            var r4 = new WrapPanel(); r4.Children.Add(Label("About")); r4.Children.Add(_about); Children.Add(r4);
            var r5 = new WrapPanel { Margin = new Thickness(64, 4, 0, 0) }; r5.Children.Add(_share); r5.Children.Add(Btn("Refresh my shares", (s, e) => RefreshMine())); Children.Add(r5);
            Children.Add(_said);
            Children.Add(new TextBlock { Text = "Your shares", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) });
            Children.Add(_mine);

            _kind.SelectionChanged += (s, e) => FillFiles();
            _file.SelectionChanged += (s, e) => { var f = _file.SelectedItem as ComboBoxItem; if (f != null) _name.Text = Path.GetFileNameWithoutExtension((string)f.Tag); };
            _kind.SelectedIndex = 0;
            AccountLink.SignedInOrOut += () => Dispatcher.BeginInvoke(new Action(Gate));
            IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) { FillFiles(); Gate(); if (Me() != null && Me().May("shares")) RefreshMine(); } };
            Gate();
        }

        static TextBlock Label(string s) { return new TextBlock { Text = s, Width = 64, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6), Opacity = 0.75 }; }
        static Button Btn(string text, RoutedEventHandler click, string tip = null) { var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6), ToolTip = tip }; b.Click += click; return b; }

        static AccountLink.SignIn Me() { return App.Tour ? null : AccountLink.Load(); }
        Kind Current { get { return _kind.SelectedIndex >= 0 ? Kinds[_kind.SelectedIndex] : Kinds[0]; } }

        /// <summary>Sharing needs a sign-in that allowed it; the card says which step is missing.</summary>
        void Gate()
        {
            var me = Me();
            bool ok = me != null && me.May("shares");
            _share.IsEnabled = ok;
            _gate.Text = me == null ? "Sign in above first: sharing puts things on reluduali.com under your name."
                       : !me.May("shares") ? "Your sign-in does not allow sharing yet (it was made before sharing, or reluduali.com does not offer it yet): sign out, then sign in again and allow \"shares\"."
                       : "Signed in as " + me.Username + ": what you share shows on your page on reluduali.com.";
        }

        void FillFiles()
        {
            var k = Current;
            _hint.Text = k.Hint;
            _file.Items.Clear();
            _file.Visibility = k.Folder ? Visibility.Collapsed : Visibility.Visible;
            _pick.Visibility = k.Folder ? Visibility.Visible : Visibility.Collapsed;
            if (k.Folder) { _camoFolder = null; _name.Text = ""; return; }
            foreach (string f in k.List())
            {
                string shown = k.Key == "preset" ? Path.GetFileName(Path.GetDirectoryName(f)) + " / " + Path.GetFileName(f) : Path.GetFileName(f);
                _file.Items.Add(new ComboBoxItem { Content = shown, Tag = f });
            }
            if (_file.Items.Count == 0) _file.Items.Add(new ComboBoxItem { Content = "(none saved yet)", IsEnabled = false });
            _file.SelectedIndex = 0;
        }

        void PickCamo()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "The package's manifest.json", Filter = "Camo package|manifest.json", CheckFileExists = true };
            if (dlg.ShowDialog() != true) return;
            _camoFolder = Path.GetDirectoryName(dlg.FileName);
            _name.Text = Path.GetFileName(_camoFolder);
            _said.Text = "Package: " + _camoFolder;
        }

        void Share()
        {
            var me = Me(); var k = Current;
            if (me == null || !me.May("shares")) { Gate(); return; }
            string name = _name.Text.Trim(), about = _about.Text, page = null;
            if (name.Length == 0) { _said.Text = "Give it a name first."; return; }
            var files = new List<KeyValuePair<string, byte[]>>();
            try
            {
                if (k.Folder)
                {
                    if (_camoFolder == null || !File.Exists(Path.Combine(_camoFolder, "manifest.json"))) { _said.Text = "Pick the package folder first (it holds manifest.json)."; return; }
                    foreach (string f in Directory.GetFiles(_camoFolder)) files.Add(new KeyValuePair<string, byte[]>(Path.GetFileName(f), File.ReadAllBytes(f)));
                }
                else
                {
                    var item = _file.SelectedItem as ComboBoxItem;
                    string f = item == null ? null : item.Tag as string;
                    if (f == null || !File.Exists(f)) { _said.Text = "Pick one of yours first."; return; }
                    if (k.Key == "preset") page = Path.GetFileName(Path.GetDirectoryName(f));
                    files.Add(new KeyValuePair<string, byte[]>(Path.GetFileName(f), File.ReadAllBytes(f)));
                }
            }
            catch (Exception ex) { _said.Text = "Could not read it: " + ex.Message; return; }
            _said.Text = "Sharing " + name + "...";
            _share.IsEnabled = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string said;
                try
                {
                    Dictionary<string, object> d;
                    using (var link = new AccountLink(me.Server)) d = link.Share(me, k.Key, name, about, page, files);
                    string url = AccountLink.S(d, "url");
                    said = "Shared: " + name + (url.Length > 0 ? " - " + url : "") + ".";
                }
                catch (Refused r) { said = Why(r); }
                catch (Exception ex) { said = "reluduali.com did not answer (" + ex.GetBaseException().Message + ")."; }
                Dispatcher.BeginInvoke(new Action(() => { _said.Text = said; Gate(); RefreshMine(); }));
            });
        }

        static string Why(Refused r)
        {
            if (r.Status == 401) { AccountLink.Forget(); return "reluduali.com no longer takes this sign-in: sign in again."; }
            if (r.Status == 403) return "Your sign-in may not share: sign out and in again, and allow \"shares\".";
            if (r.Status == 404) return "reluduali.com does not take shares yet (the site's side is on its way).";
            if (r.Status == 413) return "Too big: " + r.Message + ".";
            if (r.Status == 429) return "reluduali.com says that is enough for now (" + r.Message + "): share again later, or remove some old ones.";
            if (r.Status == 503) return "reluduali.com is busy taking other shares: try again in a minute.";
            return "Not shared: " + r.Message + ".";
        }

        void RefreshMine()
        {
            var me = Me();
            if (me == null || !me.May("shares")) { _mine.Children.Clear(); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<Dictionary<string, object>> list = null; string err = null;
                try { using (var link = new AccountLink(me.Server)) list = link.MyShares(me); }
                catch (Refused r) { err = Why(r); }
                catch (Exception ex) { err = "reluduali.com did not answer (" + ex.GetBaseException().Message + ")."; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _mine.Children.Clear();
                    if (err != null) { _mine.Children.Add(new TextBlock { Text = err, Opacity = 0.75, TextWrapping = TextWrapping.Wrap }); return; }
                    if (list.Count == 0) { _mine.Children.Add(new TextBlock { Text = "Nothing shared yet.", Opacity = 0.75 }); return; }
                    foreach (var x in list)
                    {
                        string id = AccountLink.S(x, "id"), url = AccountLink.S(x, "url");
                        var row = new WrapPanel();
                        row.Children.Add(new TextBlock { Text = AccountLink.S(x, "name") + "  (" + AccountLink.S(x, "kind") + ")", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 6) });
                        if (url.Length > 0) row.Children.Add(Btn("Open", (s, e) => AccountLink.OpenBrowser(url)));
                        row.Children.Add(Btn("Remove", (s, e) => Unshare(id), "Takes it off reluduali.com"));
                        _mine.Children.Add(row);
                    }
                }));
            });
        }

        void Unshare(string id)
        {
            var me = Me(); if (me == null) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string said;
                try { using (var link = new AccountLink(me.Server)) link.Unshare(me, id); said = "Taken off reluduali.com."; }
                catch (Refused r) { said = Why(r); }
                catch (Exception ex) { said = "reluduali.com did not answer (" + ex.GetBaseException().Message + ")."; }
                Dispatcher.BeginInvoke(new Action(() => { _said.Text = said; RefreshMine(); }));
            });
        }
    }
}
