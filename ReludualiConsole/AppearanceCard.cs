using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace ReludualiConsole
{
    /// <summary>
    /// Setup > Appearance: the UI's contrast (Mycelium, 2026-10-17: "Add a Contrast Feature for the UI Appearance in the
    /// Settings."). Soft: calmer letters and lines, the velvet a little stronger. Normal: as designed. High: white letters,
    /// clear lines, the velvet quiet - for a bright room or tired eyes. The theme reads it as it loads (Velvet.Contrast), so
    /// the choice is saved at once and shows after a restart; Restart now does that, the clean way (a docked game is let
    /// go first, as when the Designer closes).
    /// </summary>
    public static class AppearanceCard
    {
        public static Border Make()
        {
            string now = Velvet.Contrast;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
            var note = Ui.Note("", 4, 0);
            var restart = Ui.Button("Restart now", Restart, "Close the Designer the clean way and open it again with this contrast");
            restart.Visibility = Visibility.Collapsed;
            foreach (string[] c in new[] { new[] { "soft", "Soft", "Calmer letters and lines" }, new[] { "normal", "Normal", "As designed" },
                                           new[] { "high", "High", "White letters, clear lines, a quiet texture" } })
            {
                string key = c[0];
                var rb = new RadioButton { Content = c[1], ToolTip = c[2], GroupName = "contrast", IsChecked = key == now, Margin = new Thickness(0, 0, 18, 0) };
                System.Windows.Automation.AutomationProperties.SetAutomationId(rb, "Contrast_" + key);
                rb.Checked += (s, e) =>
                {
                    try { Where.Save("contrast", key); }
                    catch (Exception ex) { App.Note("saving the contrast", ex); }
                    bool changed = key != now;
                    note.Text = changed ? "Saved: the Designer shows it after a restart." : "";
                    restart.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
                };
                row.Children.Add(rb);
            }
            row.Children.Add(restart);
            return Ui.Card("Appearance", "Contrast: how strong the letters, the lines and the texture are.", row, note);
        }

        static void Restart()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                int pid = Process.GetCurrentProcess().Id;
                // the new one starts once this one has really gone (one Designer at a time; closing can take a few seconds,
                // the contest timer's last word to reluduali.com among them: the code review's C-18)
                string q = "'" + exe.Replace("'", "''") + "'";
                Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"Wait-Process -Id " + pid
                                                   + " -Timeout 120 -ErrorAction SilentlyContinue; Start-Process -FilePath " + q + "\"")
                              { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                if (Application.Current.MainWindow != null) Application.Current.MainWindow.Close();
                else Application.Current.Shutdown();
            }
            catch (Exception ex) { App.Note("restarting for the contrast", ex); }
        }
    }
}
