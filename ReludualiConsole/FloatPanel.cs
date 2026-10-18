using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ReludualiConsole
{
    /// <summary>
    /// The Overlay layout's page (2026-10-14; Mycelium: "adjust the sliders perhaps overtop of BO2, or move them aside so the
    /// BO2 is fully visible"): the open tool in a window of its own that floats over the game - see-through as much as he
    /// likes, dragged anywhere by its title, resized from its corner, hidden with one click. It is owned by the game's
    /// window, so it always stays above the game. Windows' own window alpha makes it see-through, so the page keeps
    /// hardware drawing (a WPF AllowsTransparency window would not).
    /// </summary>
    public sealed class FloatPanel : Window
    {
        [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern int SetWindowLongW(IntPtr h, int i, int v);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);

        private readonly ContentControl _body = new ContentControl { Focusable = false };
        private readonly TextBlock _title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Chrome"), VerticalAlignment = VerticalAlignment.Center };
        private readonly Slider _alpha = new Slider { Minimum = 0.6, Maximum = 1, Value = 0.9, Width = 90, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0),
                                                      ToolTip = "How solid the panel is: drag left to see more of the game through it" };
        private double _applied = -1;

        /// <summary>The panel's "beside the game" button: the host puts the page back in the Workspace layout.</summary>
        public event Action BesideRequested;
        public event Action Changed;

        public FloatPanel()
        {
            Title = "Reluduali - panel";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;
            ShowActivated = false;
            Width = 540; Height = 780;
            // Mycelium 2026-10-17: "Make sure visibility is prioritized and doesn't interfere on the Overlay Setting": over the
            // game the panel is plain, its cards too - no velvet to blend with the picture behind it when it is see-through
            Resources["CardBack"] = Ui.Brush("Panel");
            Background = Ui.Brush("Back");
            Foreground = Ui.Brush("Text");
            FontFamily = new FontFamily("Segoe UI"); FontSize = 13;

            var head = new DockPanel { LastChildFill = true, Margin = new Thickness(12, 6, 6, 6), Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
            var hide = Ui.IconButton(Glyph.Close, "Hide the panel (the Panel button in the game's bar brings it back)", () => Hide());
            var beside = Ui.IconButton(Glyph.Frame, "Put the page beside the game instead (Workspace)", () => { var h = BesideRequested; if (h != null) h(); });
            DockPanel.SetDock(hide, Dock.Right); DockPanel.SetDock(beside, Dock.Right);
            head.Children.Add(hide); head.Children.Add(beside);
            var see = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Arrow };
            see.Children.Add(Ui.Icon(Glyph.Eye, 12));
            see.Children.Add(_alpha);
            DockPanel.SetDock(see, Dock.Right);
            head.Children.Add(see);
            head.Children.Add(_title);
            head.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } var h = Changed; if (h != null) h(); } };

            var bar = new Border { Child = head, Background = Ui.Brush("Panel"), BorderBrush = Ui.Brush("Line"), BorderThickness = new Thickness(0, 0, 0, 1) };
            var dock = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            dock.Children.Add(bar);
            dock.Children.Add(new Border { Child = _body, Padding = new Thickness(4, 6, 4, 4) });
            Content = new Border { Child = dock, BorderBrush = Ui.Brush("RimHi"), BorderThickness = new Thickness(1) };

            _alpha.ValueChanged += (s, e) => { ApplyAlpha(); var h = Changed; if (h != null) h(); };
            SourceInitialized += (s, e) =>
            {
                var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                if (src != null) src.AddHook(KeepLayered);
                ApplyAlpha();
            };
            IsVisibleChanged += (s, e) => { if (IsVisible) { _applied = -1; ApplyAlpha(); } };
            SizeChanged += (s, e) => { var h = Changed; if (h != null) h(); };
        }

        [StructLayout(LayoutKind.Sequential)] private struct STYLESTRUCT { public int Old, New; }
        private bool _wantLayered;

        /// <summary>
        /// WPF takes WS_EX_LAYERED straight back off a window it did not make transparent itself (0435/0436: set from in
        /// here or from outside, the style never changed), so the panel was never see-through. While it should be, the
        /// change of its extended style keeps the bit (WM_STYLECHANGING, answered here before WPF's own filter).
        /// </summary>
        private IntPtr KeepLayered(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled)
        {
            if (msg == 0x007C && wp.ToInt32() == -20 && _wantLayered)          // WM_STYLECHANGING, GWL_EXSTYLE
            {
                var s = (STYLESTRUCT)Marshal.PtrToStructure(lp, typeof(STYLESTRUCT));
                s.New |= 0x80000;
                Marshal.StructureToPtr(s, lp, false);
                handled = true;
            }
            return IntPtr.Zero;
        }

        public object Page { get { return _body.Content; } set { _body.Content = value; } }
        public string Heading { set { _title.Text = value; } }
        // the window fades as a whole, its letters too: under 0.6 they stop being readable over a bright or a dark frame
        // (the fake-mouse test's B5, Mycelium 2026-10-17: "Make sure visibility is prioritized")
        public double Alpha { get { return _alpha.Value; } set { _alpha.Value = Math.Max(_alpha.Minimum, Math.Min(1, value)); } }
        /// <summary>Is Windows really drawing it see-through (for the pipe: 0435 found it never was).</summary>
        public bool Layered { get { IntPtr h = new WindowInteropHelper(this).Handle; return h != IntPtr.Zero && (GetWindowLongW(h, -20) & 0x80000) != 0; } }

        /// <summary>The window above which the panel stays: the game when it is docked, else the Console.</summary>
        public void SetOwnerWindow(IntPtr owner)
        {
            var me = new WindowInteropHelper(this);
            if (me.Handle == IntPtr.Zero) me.Owner = owner;          // before it is shown
            else SetWindowLongW(me.Handle, -8, owner.ToInt32());     // our own window, a 32-bit process: GWL_HWNDPARENT
        }

        private void ApplyAlpha()
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero || Math.Abs(_applied - _alpha.Value) < 0.004) return;
            _applied = _alpha.Value;
            int ex = GetWindowLongW(h, -20);
            _wantLayered = _alpha.Value < 0.995;
            if (!_wantLayered) { SetWindowLongW(h, -20, ex & ~0x80000); return; }               // solid: not layered at all
            SetWindowLongW(h, -20, ex | 0x80000);                                              // WS_EX_LAYERED
            SetLayeredWindowAttributes(h, 0, (byte)Math.Round(_alpha.Value * 255), 2);         // LWA_ALPHA
        }
    }
}
