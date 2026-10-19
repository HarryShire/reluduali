using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ReludualiConsole
{
    /// <summary>
    /// Keyboard shortcuts, on Settings (2026-10-18). Mycelium: "alt-tabbing was really annoying ... Alt-Q gets the person out of the
    /// game into the designer without them having to alt tab ... Alt-Q is my go-to. But we should also make that in a keyboard
    /// shortcuts tab inside of the settings." The key back to the Designer works whoever is in front (Windows hands a hotkey to
    /// the window that registered it), the game in its own window too. Ctrl+Alt+D, the key before, stays as a second way.
    /// </summary>
    public static class Shortcuts
    {
        public const string DefaultBack = "Alt+Q";
        public const string SecondBack = "Ctrl+Alt+D";

        /// <summary>The key back to the Designer, as written ("Alt+Q").</summary>
        public static string Back
        {
            get { string v = null; try { v = Where.Saved("key_back"); } catch { } return string.IsNullOrWhiteSpace(v) ? DefaultBack : v.Trim(); }
        }

        /// <summary>Raised on the window's thread when a shortcut changed (the window registers its keys again).</summary>
        public static event Action Changed;

        /// <summary>"Ctrl+Alt+Q" -> RegisterHotKey's modifiers (Alt 1, Ctrl 2, Shift 4, Win 8) and virtual key. False when it
        /// is not a key Windows can register (no letter, digit or F-key, or no modifier on a letter).</summary>
        public static bool Parse(string text, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (string part in text.Split('+'))
            {
                string p = part.Trim();
                if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods |= 1;
                else if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase)) mods |= 2;
                else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods |= 4;
                else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase)) mods |= 8;
                else
                {
                    Key k;
                    if (p.Length == 1 && char.IsDigit(p[0])) p = "D" + p;
                    if (!Enum.TryParse(p, true, out k)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(k);
                }
            }
            bool fKey = vk >= 0x70 && vk <= 0x87;
            return vk != 0 && (mods != 0 || fKey);
        }

        /// <summary>A pressed key as text ("Alt+Q"), or null for a lone modifier.</summary>
        static string Describe(Key key, ModifierKeys mods)
        {
            if (key == Key.System) return null;
            if (key == Key.LeftAlt || key == Key.RightAlt || key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin) return null;
            string s = "";
            if ((mods & ModifierKeys.Control) != 0) s += "Ctrl+";
            if ((mods & ModifierKeys.Alt) != 0) s += "Alt+";
            if ((mods & ModifierKeys.Shift) != 0) s += "Shift+";
            if ((mods & ModifierKeys.Windows) != 0) s += "Win+";
            string k = key.ToString();
            if (k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1])) k = k.Substring(1);
            return s + k;
        }

        /// <summary>The Settings card: each shortcut, and a box that takes the next key pressed.</summary>
        public static UIElement Card()
        {
            var box = new TextBox { Text = Back, Width = 160, IsReadOnly = true, IsReadOnlyCaretVisible = false, VerticalContentAlignment = VerticalAlignment.Center,
                                    ToolTip = "Click here, then press the keys you want (for example Alt+Q)" };
            var said = new TextBlock { Foreground = Ui.Brush("Dim"), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
            box.PreviewKeyDown += (s, e) =>
            {
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                string t = Describe(key, Keyboard.Modifiers);
                e.Handled = true;
                if (t == null) return;
                uint m, v;
                if (!Parse(t, out m, out v)) { said.Text = t + " cannot be a shortcut: hold Alt, Ctrl or Shift with a letter or number, or use an F key."; return; }
                box.Text = t;
                try { Where.Save("key_back", t); } catch (Exception ex) { said.Text = "Not saved: " + ex.Message; return; }
                if (Changed != null) Changed();
                said.Text = "Saved: " + t + " brings the Designer to the front from the game (and from anywhere).";
            };
            var reset = Ui.Button("Back to " + DefaultBack, () =>
            {
                try { Where.Save("key_back", DefaultBack); } catch { }
                box.Text = DefaultBack;
                if (Changed != null) Changed();
                said.Text = "Saved: " + DefaultBack + ".";
            }, "The usual key");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = "Back to the Designer", Width = 170, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(box);
            row.Children.Add(new Border { Width = 10 });
            row.Children.Add(reset);
            var panel = new StackPanel();
            panel.Children.Add(row);
            panel.Children.Add(said);
            panel.Children.Add(new TextBlock { Text = SecondBack + " does the same, always. F1 opens Help; Ctrl+Home the start screen.", Foreground = Ui.Brush("Dim"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
            return Ui.Card("Keyboard shortcuts", "Keys that work while the game is in front: out of the game and into the Designer without Alt+Tab.", panel);
        }
    }
}
