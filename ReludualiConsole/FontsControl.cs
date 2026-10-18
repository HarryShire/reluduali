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
    /// The FONTS tab (2026-10-02). RipperBridge reads a font pack, Plugins\RipperBridge.font,
    /// when the game starts (tools\zones\make-fontpack.py makes one: another language's 9 fonts on the English
    /// sheet's size - the Polish pack, 2026-09-30) and repaints the letter sheet on command: "font apply",
    /// "font restore", "font" for the status. Choosing another pack copies it into place (the old one is kept
    /// beside it as RipperBridge.font.before-stamp) and needs a game restart.
    /// </summary>
    public class FontsControl : UserControl
    {
        private readonly TextBlock _status = Ui.Status();
        private readonly TextBlock _pack = Ui.Note("", 0, 10);

        private static string Plugins()
        {
            return Where.InGame("Plugins");
        }

        public FontsControl()
        {
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Text");
            var row = new WrapPanel();
            row.Children.Add(Ui.Primary(Btn("Apply the pack", (s, e) => Ask("font apply"))));
            row.Children.Add(Btn("Game's own letters", (s, e) => Ask("font restore")));
            row.Children.Add(Ui.Gap());
            row.Children.Add(Btn("Use another pack...", (s, e) => Choose()));
            row.Children.Add(Btn("Status", (s, e) => Ask("font")));
            Content = Ui.Page(Ui.Card("Font pack", "The game's letters, swapped for a font pack's (RipperBridge). Apply and the game's own letters work live; another pack needs a game restart.",
                                      _pack, row, _status));
            ShowPack();
            IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) { ShowPack(); Ask("font"); } };
        }

        private static Button Btn(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6) };
            b.Click += click;
            return b;
        }

        private void ShowPack()
        {
            string f = Path.Combine(Plugins(), "RipperBridge.font");
            _pack.Text = File.Exists(f)
                ? "Pack: " + f + "  (" + new FileInfo(f).Length / 1024 + " KB, " + File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm") + ")"
                : "No pack at " + f + " - the game keeps its own letters.";
        }

        private void Ask(string line)
        {
            _status.Text = line + "...";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string r;
                try { r = BridgeClient.Ask(line, 1000, 2) ?? ("no answer: " + BridgeClient.LastError); }
                catch (Exception ex) { App.Note("FONTS " + line, ex); r = "failed: " + ex.Message; }
                Dispatcher.BeginInvoke(new Action(() => _status.Text = line + ": " + r));
            });
        }

        private void Choose()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "RipperBridge font pack|*.font" };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                string dest = Path.Combine(Plugins(), "RipperBridge.font");
                if (string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) { _status.Text = "That pack is already the one in place."; return; }
                byte[] head = new byte[8];
                using (var s = File.OpenRead(dlg.FileName)) s.Read(head, 0, 8);
                if (System.Text.Encoding.ASCII.GetString(head) != "RBFONT01") { _status.Text = "Not a RipperBridge font pack (no RBFONT01 header)."; return; }
                if (File.Exists(dest)) File.Move(dest, dest + ".before-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                File.Copy(dlg.FileName, dest);
                _status.Text = "Pack in place (the old one kept beside it). Restart the game, then Apply.";
            }
            catch (Exception ex) { App.Note("choosing a font pack", ex); _status.Text = "Failed (nothing deleted): " + ex.Message; }
            ShowPack();
        }
    }
}
