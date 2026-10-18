using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ReludualiConsole
{
    /// <summary>
    /// Windows' own frame in the console's colours (2026-10-14): a dark title bar the colour of the header, and a purple
    /// rim round every window - the "shiny on its edges" (Mycelium, 2026-10-01) at the window's own edge. Windows draws it, so
    /// snapping, resizing and maximising stay Windows' own. Windows 10 takes only the dark title bar; the colours need
    /// Windows 11 (asked and ignored elsewhere).
    /// </summary>
    internal static class Native
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private const int DarkMode = 20, BorderColour = 34, CaptionColour = 35, TextColour = 36;

        /// <summary>COLORREF: 0x00BBGGRR.</summary>
        private static int Ref(byte r, byte g, byte b) { return r | (g << 8) | (b << 16); }

        /// <summary>The frame in the skin's colours: by night as above; by day (Skin.cs) a light title bar the pearl of the
        /// site's cards, ink letters and the site's edge blue round it. Called again for every window when the skin turns.</summary>
        public static void Frame(Window w)
        {
            try
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                int on = 1, caption = Ref(0x1F, 0x16, 0x22), text = Ref(0xDD, 0xD3, 0xEC), border = Ref(0xB1, 0x60, 0xF5);
                if (Skin.Now.IsLight)
                {
                    System.Windows.Media.Color c = (System.Windows.Media.Color)Skin.Now["Back"], t = (System.Windows.Media.Color)Skin.Now["Text"], e = (System.Windows.Media.Color)Skin.Now["Line"];
                    on = 0; caption = Ref(c.R, c.G, c.B); text = Ref(t.R, t.G, t.B); border = Ref(e.R, e.G, e.B);
                }
                DwmSetWindowAttribute(h, DarkMode, ref on, 4);
                DwmSetWindowAttribute(h, CaptionColour, ref caption, 4);
                DwmSetWindowAttribute(h, TextColour, ref text, 4);
                DwmSetWindowAttribute(h, BorderColour, ref border, 4);
            }
            catch (Exception ex) { App.Note("colouring the window frame", ex); }
        }
    }
}
