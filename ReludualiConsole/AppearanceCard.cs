using System;
using System.Windows;
using System.Windows.Controls;

namespace ReludualiConsole
{
    /// <summary>
    /// Setup > Appearance. Day and night (Mycelium, 2026-10-17: "make it a dynamic theme. Light during the day, dark during the
    /// night"; then "Make three theme options, Dark, Light and Dynamic ... Dark and Light are Static"): Dark keeps the velvet,
    /// Light keeps the site's card screen by day, Dynamic follows the sky over this PC (Skin.cs). And
    /// the UI's contrast ("Add a Contrast Feature for the UI Appearance in the Settings"): Soft, calmer letters and lines,
    /// the texture a little stronger; Normal, as designed; High, the clearest letters and lines, the texture quiet - for a
    /// bright room or tired eyes. Both are saved at once and shown at once.
    /// </summary>
    public static class AppearanceCard
    {
        public static Border Make()
        {
            Skin s = Skin.Now;
            var when = Ui.Note("", 2, 4);
            Action said = () =>
            {
                string next = s.NextTurn();
                when.Text = s.Mode == "dynamic"
                    ? "Now " + (s.IsLight ? (s.Evening > 0.04 ? "light, in the evening's colours" : "light") : "dark") + (next != null ? "; " + next + "." : ".")
                      + " The sky is this PC's: its clock gives the place, the Sun its height; dark once the Sun is 6 degrees down."
                    : s.Mode == "light" ? "Light all the time: the site's card screen as it is by day."
                    : "Dark all the time: the velvet.";
            };

            var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            foreach (string[] m in new[] { new[] { "dark", "Dark", "The velvet, always" },
                                           new[] { "light", "Light", "reluduali.com's card screen by day, always: the sky, glass and printed cards" },
                                           new[] { "dynamic", "Dynamic", "Light by day, its evening colours as the Sun goes down, dark by night: the sky over this PC" } })
            {
                string key = m[0];
                var rb = new RadioButton { Content = m[1], ToolTip = m[2], GroupName = "theme", IsChecked = key == s.Mode, Margin = new Thickness(0, 0, 18, 0) };
                System.Windows.Automation.AutomationProperties.SetAutomationId(rb, "Theme_" + key);
                rb.Checked += (o, e) => { if (s.Mode != key) s.SetMode(key); said(); };
                modes.Children.Add(rb);
            }

            var contrast = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
            foreach (string[] c in new[] { new[] { "soft", "Soft", "Calmer letters and lines" }, new[] { "normal", "Normal", "As designed" },
                                           new[] { "high", "High", "The clearest letters and lines, a quiet texture" } })
            {
                string key = c[0];
                var rb = new RadioButton { Content = c[1], ToolTip = c[2], GroupName = "contrast", IsChecked = key == s.Contrast, Margin = new Thickness(0, 0, 18, 0) };
                System.Windows.Automation.AutomationProperties.SetAutomationId(rb, "Contrast_" + key);
                rb.Checked += (o, e) => { if (s.Contrast != key) s.SetContrast(key); };
                contrast.Children.Add(rb);
            }

            said();
            Skin.Changed += said;                         // the evening coming on while Setup is open
            return Ui.Card("Appearance", "Day and night, and the contrast: how strong the letters, the lines and the texture are.",
                           Ui.Heading("Day and night"), modes, when, Ui.Heading("Contrast", 6), contrast);
        }
    }
}
