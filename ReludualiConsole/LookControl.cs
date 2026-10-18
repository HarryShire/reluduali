using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>
    /// The LOOK tab: the per-map looks (Looks\&lt;mp_name&gt;.cfg beside BatchCore, sent before every recording on that
    /// map), and the live colour and exposure panel. The fog moved to FOG and the sun and sky to SUN (overgrow night,
    /// 2026-10-10); their old LOOK sliders waited 150 ms for the hand to stop, these send on every move.
    /// </summary>
    public class LookControl : UserControl
    {
        private readonly ComboBox _maps = new ComboBox { MinWidth = 240, Margin = new Thickness(0, 0, 8, 6) };
        private readonly TextBlock _status = Ui.Status();

        public LookControl()
        {
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Text");
            var root = new DockPanel();
            var row = new WrapPanel();
            row.Children.Add(Btn("The loaded map", (s, e) => Run("map", () =>
            {
                string m = Looks.CurrentMap();
                Dispatcher.Invoke(new Action(() => { if (m != null) _maps.SelectedItem = m; }));
                return m == null ? "no map loaded (or the bridge is not answering)" : "loaded: " + m;
            }), "Pick the look of the map the game has loaded"));
            row.Children.Add(_maps);
            row.Children.Add(Ui.Primary(Btn("Send look", (s, e) => Run("look", () =>
            {
                string map = (string)Dispatcher.Invoke(new Func<object>(() => _maps.SelectedItem));
                return map == null ? "pick a map" : Looks.Send(map, n => true, Log);
            }), "Send this map's look to the game now (it is also sent before every recording on that map)")));
            var head = Ui.Card("Map look", "Your look for each map (kept in the Looks folder beside the Designer): sent before every recording on that map, or now.", row, _status);
            head.Margin = new Thickness(12, 2, 12, 2);
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);
            root.Children.Add(Panels.Look());
            Content = root;
            LoadMaps();
        }

        private void LoadMaps()
        {
            try
            {
                if (Directory.Exists(Looks.Folder))
                    foreach (string f in Directory.GetFiles(Looks.Folder, "*.cfg").OrderBy(x => x))
                        _maps.Items.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch (Exception ex) { App.Note("listing the looks", ex); }
            if (_maps.Items.Count == 0) _maps.Items.Add("(no Looks folder beside the Designer yet)");
        }

        private void Run(string what, Func<string> work)
        {
            _status.Text = what + "...";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string result;
                try { result = work(); }
                catch (Exception ex) { App.Note("LOOK " + what, ex); result = "failed: " + ex.Message; }
                Dispatcher.BeginInvoke(new Action(() => { _status.Text = what + ": " + result; }));
            });
        }

        private void Log(string line) { App.Note("LOOK: " + line.Trim(), null); }

        private static Button Btn(string text, RoutedEventHandler click, string tip = null)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6), ToolTip = tip };
            b.Click += click;
            return b;
        }
    }
}
