using System;
using System.Windows;

namespace BatchRecorder
{
    /// <summary>The batch core's version, shown by every host.</summary>
    public static class BatchInfo
    {
        // 0.3.0 (2026-10-01): the core stands alone (Stage 1), hosted by the standalone console.
        // 0.3.1 (2026-10-01, found in the sandbox): a take's demo load counts only when the theater
        // is up with a NEW end tick (or after it closed and came back) - a film already coming up
        // was taken for the take's demo; the pipe's 'run' reply names the run instead of a stale
        // status line.
        // 0.5.0 (2026-10-14, the CAM QUEUE): a take list row may say its kind (pov / cine), name, CFG, fps, exact ticks,
        // before / after, HUD and film mode, with defaults per kind beside the list (queue.txt); a named row records into
        // <out>\<folder>\<name>\; a row with exact ticks needs no zero; TakeFinished for a night's status file.
        public const string Version = "0.7.0";

        /// <summary>What the host's own start-up work did, for the pipe.</summary>
        public static string HostReport = "";
    }

    /// <summary>
    /// What a host program adds on top of RipperBridge. The core never needs one: it
    /// sends commands, reads dvars, loads demos and records through RipperBridge and its
    /// own memory reads. A host's own routes are kept as fallbacks; the standalone
    /// console has none of them.
    /// </summary>
    public interface IConsoleHost
    {
        /// <summary>Who is hosting, for logs and the pipe.</summary>
        string Name { get; }

        /// <summary>Why the last call failed.</summary>
        string LastError { get; }

        /// <summary>A named game address from the host's own table (Tick, EndTick...), or zero.</summary>
        IntPtr GetAddress(string field);

        /// <summary>A dvar_t record by name, through the host's finder, or zero.</summary>
        IntPtr FindDvarStruct(string name);

        bool SendCommand(string command);
        bool LoadDemoFile(string path);
        bool EndDemoPlayback();

        /// <summary>The host's own clean way out.</summary>
        bool CloseHostWindow();
    }

    public static class ConsoleHost
    {
        private static IConsoleHost _current = new StandaloneHost();

        /// <summary>The host in use: the standalone one unless a host sets its own.</summary>
        public static IConsoleHost Current
        {
            get { return _current; }
            set { _current = value ?? new StandaloneHost(); }
        }
    }

    /// <summary>No host routes: everything goes through RipperBridge.</summary>
    public sealed class StandaloneHost : IConsoleHost
    {
        private const string None = "not available in the standalone console; RipperBridge does this instead";

        public string Name { get { return "standalone"; } }
        public string LastError { get { return None; } }

        public IntPtr GetAddress(string field) { return IntPtr.Zero; }
        public IntPtr FindDvarStruct(string name) { return IntPtr.Zero; }
        public bool SendCommand(string command) { return false; }
        public bool LoadDemoFile(string path) { return false; }
        public bool EndDemoPlayback() { return false; }

        public bool CloseHostWindow()
        {
            Application app = Application.Current;
            if (app == null) return false;
            app.Dispatcher.BeginInvoke(new Action(() => app.Shutdown()));
            return true;
        }
    }
}
