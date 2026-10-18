using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ReludualiConsole
{
    /// <summary>
    /// Mycelium, 2026-10-01: "Id want to make ours crash safe ... I dont see why UI's should
    /// ever crash, they're just UIs." So no exception ends the console: on the UI thread it
    /// is logged and shown, on a worker thread it is logged (App.config keeps the process
    /// alive), and the game is never touched by any of it.
    /// </summary>
    public partial class App : Application
    {
        public static string LogPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReludualiConsole.log"); } }

        /// <summary>Set by the main window, so a caught error can be shown as well as logged.</summary>
        public static Action<string> ShowError;

        /// <summary>The off-screen picture run (--gui-tour): no tether, no pipe, no contest timer, no keyboard hook.</summary>
        public static bool Tour;

        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, a) => Note("worker thread", a.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, a) => { Note("task", a.Exception); a.SetObserved(); };
            Note("started, version " + typeof(App).Assembly.GetName().Version + ", contrast " + Velvet.Contrast, null);
            // every window shows the Reluduali mark (Ui.WindowIcon): the windows made in code get it as they load
            try
            {
                EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, args) =>
                {
                    var w = sender as Window;
                    if (w != null && w.Icon == null) try { w.Icon = Ui.WindowIcon; } catch { }
                }));
            }
            catch (Exception ex) { Note("the window icon", ex); }
            // the website's foil on every edge catches the light where the pointer is (Velvet.FollowPointer)
            try { Velvet.FollowPointer(); }
            catch (Exception ex) { Note("the foil's light", ex); }
            // ReludualiConsole.exe --timer-test SERVER CODE OUTFILE: the contest link, headless (overgrow night, 2026-10-10)
            if (e.Args.Length >= 4 && e.Args[0] == "--timer-test")
            {
                int code = TimerSelfTest.Run(e.Args[1], e.Args[2], e.Args[3]);
                Shutdown(code);
                return;
            }
            // ReludualiConsole.exe --gui-tour OUTDIR [WIDTH HEIGHT] [QUEUEDIR ROW]: every page drawn to a picture, no window shown
            if (e.Args.Length >= 2 && e.Args[0] == "--gui-tour")
            {
                int code = 1;
                try { code = GuiTour.Run(e.Args); }
                catch (Exception ex) { Note("the GUI tour", ex); Console.Error.WriteLine(ex); }
                Shutdown(code);
                return;
            }
            // ReludualiConsole.exe --theater-test OUTDIR: the theater launch's checks with no game, no window shown and no sound
            if (e.Args.Length >= 2 && e.Args[0] == "--theater-test")
            {
                int code = 1;
                try { code = TheaterLaunch.SelfTest(e.Args[1]); }
                catch (Exception ex) { Note("the theater test", ex); Console.Error.WriteLine(ex); }
                Shutdown(code);
                return;
            }
            // every window in the console's colours: a dark title bar and a purple rim (Windows 11)
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, a) => Native.Frame((Window)s)));
            // ReludualiConsole.exe --lumit-test TAKEDIR WATCHDIR: hands a take's videos to a Lumit folder, as after a batch take
            if (e.Args.Length >= 3 && e.Args[0] == "--lumit-test")
            {
                string was = LumitFolder.Path_;
                try { LumitFolder.Path_ = e.Args[2]; LumitFolder.Deliver(e.Args[1], DateTime.Now.AddMinutes(-10)); }
                finally { LumitFolder.Path_ = was; }
                Shutdown(0);
                return;
            }
            // ReludualiConsole.exe --link register | remove | status: the reluduali-console: link without a window (the
            // installer, tests); exit 0 when it is registered afterwards (status) or the change was made
            if (e.Args.Length >= 2 && e.Args[0] == "--link")
            {
                int code = 0;
                try
                {
                    if (e.Args[1] == "register") ConsoleLink.Register();
                    else if (e.Args[1] == "remove") ConsoleLink.Unregister();
                    else code = ConsoleLink.Registered ? 0 : 1;
                    Note("--link " + e.Args[1] + ": " + (ConsoleLink.Registered ? "registered" : "not registered"), null);
                }
                catch (Exception ex) { Note("--link " + e.Args[1], ex); code = 2; }
                Shutdown(code);
                return;
            }
            // ReludualiConsole.exe --theater [film.demo]: the splash, the game docked in the Workspace, the DEMOS tab (TheaterLaunch)
            bool theater = TheaterLaunch.Parse(e.Args);
            // one Console at a time (0051): a second launch, by the reluduali-console: link or by hand, brings the first forward
            if (!OneConsole.Claim())
            {
                if (theater)
                {
                    Note("--theater: another Console is running: this launch greets, starts the game and asks that one over its pipe", null);
                    TheaterLaunch.ForRunningConsole(this);
                    return;
                }
                Note("another Console is running: asked it to come to the front" + (ConsoleLink.IsLink(e.Args) ? " (opened by the link)" : ""), null);
                Shutdown(0);
                return;
            }
            // what a theater launch put into the game and a crash left there comes out (once the game is not running)
            TheaterLayer.Tidy();
            if (theater)
            {
                TheaterLaunch.Early();        // the greeting at once, and the game starting while the Designer builds itself
                EventManager.RegisterClassHandler(typeof(MainWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, a) => TheaterLaunch.Begin((MainWindow)s)));
            }
            if (ConsoleLink.IsLink(e.Args)) Note("opened by the reluduali-console: link (nothing in it is read)", null);
            ConsoleLink.Refresh();
            try { string names = Where.Saved("names"); if (names != null) BatchRecorder.Naming.Style = names == "old" ? "" : names; } catch { }
            EventManager.RegisterClassHandler(typeof(MainWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, a) => OneConsole.Listen((Window)s)));
            base.OnStartup(e);
        }

        private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Note("window", e.Exception);
            e.Handled = true;
            try { if (ShowError != null) ShowError(e.Exception.GetBaseException().Message); } catch { }
        }

        public static void Note(string where, Exception ex)
        {
            try
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + where
                    + (ex != null ? ": " + ex : "") + Environment.NewLine);
            }
            catch { }
        }
    }
}
