using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ReludualiConsole
{
    /// <summary>
    /// 2026-10-16 (reluduali.com): the Reluduali panel inside Lumit opens the Console through a
    /// reluduali-console: link; Lumit asks the person first, every time, naming the program. Registered for this person only
    /// (HKCU\Software\Classes), by the installer or SETUP's switch; the command is the Console's exe itself, since Lumit
    /// refuses shell and script handlers. A launch through the link only brings the Console to the front: any web page can
    /// open the same scheme, so nothing in the link is read.
    /// </summary>
    public static class ConsoleLink
    {
        public const string Scheme = "reluduali-console";
        const string Key = @"Software\Classes\" + Scheme;

        static string Exe { get { return Process.GetCurrentProcess().MainModule.FileName; } }
        static string Command { get { return "\"" + Exe + "\" \"%1\""; } }

        /// <summary>True for a launch through the link: the argument is ignored whatever it says.</summary>
        public static bool IsLink(string[] args)
        {
            return args.Length >= 1 && args[0].StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);
        }

        public static bool Registered
        {
            get { try { using (var k = Registry.CurrentUser.OpenSubKey(Key)) return k != null; } catch { return false; } }
        }

        public static void Register()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key))
            {
                k.SetValue("", "URL:The Reluduali Designer");
                k.SetValue("URL Protocol", "");
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", Command);
            }
        }

        public static void Unregister() { Registry.CurrentUser.DeleteSubKeyTree(Key, false); }

        /// <summary>On each start: a registered link that names another exe (the Console moved, or an older copy) is pointed
        /// at this one. Nothing is registered that was not already.</summary>
        public static void Refresh()
        {
            try
            {
                using (var c = Registry.CurrentUser.OpenSubKey(Key + @"\shell\open\command"))
                {
                    if (c == null) return;
                    if (!string.Equals(c.GetValue("") as string, Command, StringComparison.OrdinalIgnoreCase)) { c.Close(); Register(); }
                }
            }
            catch (Exception ex) { App.Note("refreshing the reluduali-console: link", ex); }
        }
    }

    /// <summary>
    /// One Console at a time (0051): a second launch, by the link or by hand, asks the first to come to the front and exits.
    /// The off-screen and headless runs (--gui-tour, --timer-test, --lumit-test) never get here.
    /// </summary>
    public static class OneConsole
    {
        static Mutex _held;
        static readonly int ShowMessage = RegisterWindowMessage("Reluduali.Console.ComeToTheFront");

        /// <summary>True for the first Console. False when one is already running: it has been asked to come forward.</summary>
        public static bool Claim()
        {
            bool first;
            _held = new Mutex(true, @"Local\Reluduali.Console.One", out first);
            if (first) return true;
            AllowSetForegroundWindow(-1);                           // ASFW_ANY: this launch holds the foreground right; pass it on
            PostMessage((IntPtr)0xFFFF, ShowMessage, IntPtr.Zero, IntPtr.Zero);     // HWND_BROADCAST: only a Console answers it
            // and bring it forward from here too: the launch Windows just started (by Lumit's link, or a shortcut) may set the
            // foreground itself, where the running Console, asked by a message, may be refused (sandbox 0448)
            try
            {
                var self = Process.GetCurrentProcess();
                foreach (Process p in Process.GetProcessesByName(self.ProcessName))
                {
                    if (p.Id == self.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                    if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9);     // SW_RESTORE
                    SetForegroundWindow(p.MainWindowHandle);
                }
            }
            catch { }
            return false;
        }

        /// <summary>The main window listens for the second launch's request.</summary>
        public static void Listen(Window w)
        {
            var src = HwndSource.FromHwnd(new WindowInteropHelper(w).Handle);
            if (src == null) return;
            src.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg != ShowMessage) return IntPtr.Zero;
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                w.Show();
                w.Activate();
                SetForegroundWindow(hwnd);
                handled = true;
                return IntPtr.Zero;
            });
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    }
}
