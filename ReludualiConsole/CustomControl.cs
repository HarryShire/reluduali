using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Web.Script.Serialization;

namespace ReludualiConsole
{
    /// <summary>One slider the user made: a dvar, its range, and what drives it besides the mouse.</summary>
    public sealed class CustomSlider
    {
        public string Label, Dvar;
        public double Min, Max, Value;
        public string KeyDown, KeyUp;         // e.g. "OemMinus", "OemPlus": nudge while the game is in front
        public int MidiChannel = -1, MidiCc = -1;
    }

    /// <summary>
    /// The CUSTOM tab (Mycelium, 2026-10-01: "really for creating Sliders to be used for Keybinds, we could probably
    /// do this better with Midi Fader Support"). Any number dvar becomes a live slider; each can have two keys that nudge it
    /// while the game is in front (the keys still reach the game), and a MIDI fader (Learn, then move the fader). Saved in
    /// Presets\CUSTOM\sliders.json beside the console.
    /// </summary>
    public class CustomControl : UserControl
    {
        readonly List<CustomSlider> _sliders = new List<CustomSlider>();
        readonly ContentControl _panelHost = new ContentControl();
        readonly StackPanel _rows = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        readonly TextBlock _status = Ui.Status();
        readonly ComboBox _midi = new ComboBox { MinWidth = 220, Margin = new Thickness(0, 0, 8, 6) };
        KnobPanel _panel;
        readonly Dictionary<CustomSlider, Knob> _knobs = new Dictionary<CustomSlider, Knob>();
        CustomSlider _learning;
        static string FilePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Presets", "CUSTOM", "sliders.json"); } }

        public CustomControl()
        {
            Foreground = (Brush)Application.Current.FindResource("Text");
            var top = new WrapPanel();
            top.Children.Add(Ui.Primary(Btn("New slider...", (s, e) => NewSlider())));
            top.Children.Add(Ui.Gap());
            top.Children.Add(Ui.Label("MIDI in"));
            top.Children.Add(_midi);
            top.Children.Add(Btn("Open", (s, e) => OpenMidi()));
            var setup = Ui.Card("Your sliders", "Any number setting the game has. Each can have two keys that nudge it while the game is in front, and a MIDI fader (open the device, then Learn MIDI on its row).",
                                top, _rows, _status);
            var root = new StackPanel();
            root.Children.Add(new Border { Child = setup, Margin = new Thickness(12, 2, 12, 0) });
            root.Children.Add(_panelHost);
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Load();
            Rebuild();
            if (!App.Tour && _sliders.Any(c => c.KeyDown != null || c.KeyUp != null)) Keys.Start();
            for (int i = 0; i < Midi.Count; i++) _midi.Items.Add(Midi.Name(i));
            if (_midi.Items.Count == 0) _midi.Items.Add("(no MIDI input on this PC)");
            _midi.SelectedIndex = 0;
            Keys.Pressed += OnKey;
            Midi.Control += OnMidi;
        }

        void Load()
        {
            try { if (File.Exists(FilePath)) _sliders.AddRange(new JavaScriptSerializer().Deserialize<List<CustomSlider>>(File.ReadAllText(FilePath))); }
            catch (Exception ex) { App.Note("reading custom sliders", ex); }
        }

        void Save()
        {
            try
            {
                foreach (var kv in _knobs) kv.Key.Value = kv.Value.Values[0];
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(_sliders), new UTF8Encoding(false));
            }
            catch (Exception ex) { App.Note("saving custom sliders", ex); }
        }

        void Rebuild()
        {
            _knobs.Clear();
            if (_panel != null) _panel.Retire();
            _panel = new KnobPanel("CUSTOM", null, null);
            foreach (CustomSlider c in _sliders)
                _panel.Add(c.Label, c.Dvar + " {0}", c.Dvar, new[] { c.Value }, new[] { c.Min }, new[] { c.Max });
            foreach (CustomSlider c in _sliders) _knobs[c] = _panel.Find(c.Label);
            _panelHost.Content = _sliders.Count > 0 ? (object)_panel : new TextBlock { Text = "No sliders yet: New slider... and name a setting (try r_exposureValue).", Opacity = 0.6, Margin = new Thickness(0, 10, 0, 0) };
            _rows.Children.Clear();
            foreach (CustomSlider c in _sliders)
            {
                CustomSlider me = c;
                var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(new TextBlock { Text = c.Label + "  (" + c.Dvar + ")", Width = 260, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = "keys " + (c.KeyDown ?? "-") + " / " + (c.KeyUp ?? "-") + "   MIDI " + (c.MidiCc >= 0 ? "ch " + (c.MidiChannel + 1) + " CC " + c.MidiCc : "-"),
                                                 Width = 260, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 });
                row.Children.Add(Btn("Keys...", (s, e) => SetKeys(me)));
                row.Children.Add(Btn("Learn MIDI", (s, e) => { _learning = me; _status.Text = "Move a fader or knob on the MIDI device for '" + me.Label + "'."; }));
                row.Children.Add(Btn("Remove", (s, e) => { _sliders.Remove(me); Save(); Rebuild(); }));
                _rows.Children.Add(row);
            }
        }

        void NewSlider()
        {
            string name = Ask.Text(Window.GetWindow(this), "New slider", "The setting (dvar) it drives, e.g. r_exposureValue:", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            GameName g = GameName.Find(name.Trim());
            if (g == null || g.IsCommand) { _status.Text = "The game has no number setting called '" + name.Trim() + "'. The CFG tab completes names."; return; }
            if (_sliders.Any(c => c.Dvar.Equals(g.Name, StringComparison.OrdinalIgnoreCase))) { _status.Text = g.Name + " has a slider already."; return; }
            if (!(g.Type == "float" || g.Type == "int" || g.Type == "bool")) { _status.Text = g.Name + " is a " + g.Type + ", not one number: a slider drives one number."; return; }
            double min = 0, max = g.Type == "bool" ? 1 : 1, def;
            string[] dom = (g.Domain ?? "").Split(new[] { " .. " }, StringSplitOptions.None);
            if (dom.Length == 2) { double a, b; if (double.TryParse(dom[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a)) min = a; if (double.TryParse(dom[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b)) max = b; }
            if (max > 100000) max = Math.Max(1000, Math.Abs(min) + 1000);      // "0 .. 3.4e38": a usable slider instead
            if (min < -100000) min = -max;
            double.TryParse(g.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out def);
            _sliders.Add(new CustomSlider { Label = g.Name, Dvar = g.Name, Min = min, Max = max, Value = def });
            Save(); Rebuild();
            _status.Text = "Added " + g.Name + " (" + min.ToString(CultureInfo.InvariantCulture) + " to " + max.ToString(CultureInfo.InvariantCulture) + ", the game's range). Typed values beyond it work too.";
        }

        void SetKeys(CustomSlider c)
        {
            string down = Ask.Text(Window.GetWindow(this), "Keys for " + c.Label, "Key that nudges it DOWN (a WPF key name, e.g. OemMinus, NumPad1, F5; empty = none):", c.KeyDown ?? "");
            if (down == null) return;
            string up = Ask.Text(Window.GetWindow(this), "Keys for " + c.Label, "Key that nudges it UP (e.g. OemPlus, NumPad2, F6; empty = none):", c.KeyUp ?? "");
            if (up == null) return;
            Key k;
            if (down.Length > 0 && !Enum.TryParse(down, true, out k)) { _status.Text = "No key called '" + down + "'."; return; }
            if (up.Length > 0 && !Enum.TryParse(up, true, out k)) { _status.Text = "No key called '" + up + "'."; return; }
            c.KeyDown = down.Length > 0 ? down : null; c.KeyUp = up.Length > 0 ? up : null;
            Save(); Rebuild();
            Keys.Start();
        }

        /// <summary>A key went down while the game was in front: nudge every slider bound to it by 1/50 of its range.</summary>
        void OnKey(Key key)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (CustomSlider c in _sliders)
                {
                    int dir = (c.KeyUp != null && c.KeyUp.Equals(key.ToString(), StringComparison.OrdinalIgnoreCase)) ? 1
                            : (c.KeyDown != null && c.KeyDown.Equals(key.ToString(), StringComparison.OrdinalIgnoreCase)) ? -1 : 0;
                    Knob k;
                    if (dir == 0 || !_knobs.TryGetValue(c, out k) || k == null) continue;
                    double v = Math.Max(c.Min, Math.Min(c.Max, k.Values[0] + dir * (c.Max - c.Min) / 50));
                    _panel.Set(k, new[] { v });
                    _status.Text = c.Label + " = " + v.ToString("0.###", CultureInfo.InvariantCulture) + " (key " + key + ")";
                }
            }));
        }

        void OpenMidi()
        {
            if (Midi.Count == 0) { _status.Text = "No MIDI input device on this PC."; return; }
            string r = Midi.Open(_midi.SelectedIndex);
            _status.Text = r;
        }

        void OnMidi(int channel, int cc, int value)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_learning != null)
                {
                    _learning.MidiChannel = channel; _learning.MidiCc = cc;
                    _status.Text = "'" + _learning.Label + "' follows channel " + (channel + 1) + " CC " + cc + ".";
                    _learning = null; Save(); Rebuild();
                    return;
                }
                foreach (CustomSlider c in _sliders)
                {
                    Knob k;
                    if (c.MidiChannel != channel || c.MidiCc != cc || !_knobs.TryGetValue(c, out k) || k == null) continue;
                    _panel.Set(k, new[] { c.Min + (c.Max - c.Min) * value / 127.0 });
                }
            }));
        }

        static Button Btn(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 2) };
            b.Click += click;
            return b;
        }
    }

    /// <summary>
    /// Keys pressed while the game is the window in front, seen by a low-level keyboard hook. Nothing is swallowed: the
    /// game gets every key as before. The hook only runs once a slider has a key.
    /// </summary>
    public static class Keys
    {
        public static event Action<Key> Pressed = k => { };
        delegate IntPtr Proc(int code, IntPtr wParam, IntPtr lParam);
        static Proc _proc;
        static IntPtr _hook = IntPtr.Zero;
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, Proc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        static int _gamePid;

        public static void Start()
        {
            if (_hook != IntPtr.Zero) return;
            _proc = Hook;
            using (var m = Process.GetCurrentProcess().MainModule) _hook = SetWindowsHookEx(13, _proc, GetModuleHandle(m.ModuleName), 0);   // WH_KEYBOARD_LL
            if (_hook == IntPtr.Zero) App.Note("keyboard hook not set: error " + Marshal.GetLastWin32Error(), null);
        }

        static bool GameInFront()
        {
            uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            if (pid == 0) return false;
            if (pid == _gamePid) return true;
            try { if (Process.GetProcessById((int)pid).ProcessName.Equals("t6mpv43", StringComparison.OrdinalIgnoreCase)) { _gamePid = (int)pid; return true; } } catch { }
            return false;
        }

        static IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0 && (wParam.ToInt32() == 0x100 || wParam.ToInt32() == 0x104) && GameInFront())   // WM_KEYDOWN, WM_SYSKEYDOWN
                    Pressed(KeyInterop.KeyFromVirtualKey(Marshal.ReadInt32(lParam)));
            }
            catch { }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }
    }

    /// <summary>MIDI in through Windows' own winmm: control changes (faders, knobs) only. Opened on demand.</summary>
    public static class Midi
    {
        public static event Action<int, int, int> Control = (ch, cc, v) => { };
        delegate void InProc(IntPtr h, int msg, IntPtr instance, IntPtr p1, IntPtr p2);
        static InProc _proc;
        static IntPtr _h = IntPtr.Zero;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct MIDIINCAPS { public ushort wMid, wPid; public uint vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public uint dwSupport; }
        [DllImport("winmm.dll")] static extern int midiInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Auto)] static extern int midiInGetDevCaps(IntPtr id, ref MIDIINCAPS caps, int size);
        [DllImport("winmm.dll")] static extern int midiInOpen(out IntPtr h, int id, InProc cb, IntPtr instance, int flags);
        [DllImport("winmm.dll")] static extern int midiInStart(IntPtr h);
        [DllImport("winmm.dll")] static extern int midiInClose(IntPtr h);

        public static int Count { get { try { return midiInGetNumDevs(); } catch { return 0; } } }

        public static string Name(int i)
        {
            var c = new MIDIINCAPS();
            return midiInGetDevCaps((IntPtr)i, ref c, Marshal.SizeOf(c)) == 0 ? c.szPname : "MIDI in " + i;
        }

        public static string Open(int i)
        {
            if (_h != IntPtr.Zero) { midiInClose(_h); _h = IntPtr.Zero; }
            _proc = Callback;
            int r = midiInOpen(out _h, i, _proc, IntPtr.Zero, 0x30000);     // CALLBACK_FUNCTION
            if (r != 0) return "Could not open " + Name(i) + " (error " + r + "; is another program holding it? Live must be closed).";
            midiInStart(_h);
            return "Listening to " + Name(i) + ".";
        }

        static void Callback(IntPtr h, int msg, IntPtr instance, IntPtr p1, IntPtr p2)
        {
            if (msg != 0x3C3) return;                 // MIM_DATA
            int m = p1.ToInt32(), status = m & 0xF0;
            if (status != 0xB0) return;               // control change only
            Control(m & 0x0F, (m >> 8) & 0x7F, (m >> 16) & 0x7F);
        }
    }
}
