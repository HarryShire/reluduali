using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace BatchRecorder
{
    /// <summary>
    /// \\.\pipe\bo2batch: the Batch tab driven from outside, whichever program hosts it.
    ///
    /// One line in, the whole reply back, then the connection closes, so a reply
    /// may run to several lines. tools\console\batch.ps1 is the client. "help" lists
    /// the commands.
    ///
    /// Everything that touches the tab runs on the host's UI thread, with a time
    /// limit: a stuck pipe must say so rather than hang. Local connections only;
    /// the network is denied.
    /// </summary>
    public static class BatchPipe
    {
        public const string PipeName = "bo2batch";

        /// <summary>
        /// Commands a host adds (2026-10-14: the Reluduali Console's CAM QUEUE adds "queue ..."): verb -> handler(rest)
        /// -> reply. Called on the pipe's own thread; a handler that touches its window marshals to it itself.
        /// </summary>
        public static readonly System.Collections.Generic.Dictionary<string, Func<string, string>> Extra =
            new System.Collections.Generic.Dictionary<string, Func<string, string>>(StringComparer.OrdinalIgnoreCase);

        private static readonly object Gate = new object();
        private static Thread _thread;

        /// <summary>No pipe at all: a host's off-screen picture run (the Reluduali Console's --gui-tour), beside a real console.</summary>
        public static bool Off;

        public static void Start()
        {
            lock (Gate)
            {
                if (_thread != null || Off) return;
                _thread = new Thread(Serve) { IsBackground = true, Name = "bo2batch pipe" };
                _thread.Start();
            }
        }

        private static NamedPipeServerStream Create()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,
                PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            return new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.None, 4096, 65536, security);
        }

        private static void Serve()
        {
            while (true)
            {
                try
                {
                    using (NamedPipeServerStream pipe = Create())
                    {
                        pipe.WaitForConnection();
                        string reply;
                        try { reply = Handle(ReadLine(pipe)); }
                        catch (Exception ex) { reply = "error: " + ex.GetBaseException().Message; }

                        byte[] bytes = Encoding.UTF8.GetBytes((reply ?? "").TrimEnd() + "\n");
                        pipe.Write(bytes, 0, bytes.Length);
                        pipe.Flush();
                        try { pipe.WaitForPipeDrain(); } catch { }
                    }
                }
                catch
                {
                    // Another host holds the name, or the client left early. Try again.
                    Thread.Sleep(1000);
                }
            }
        }

        private static string ReadLine(Stream s)
        {
            var bytes = new MemoryStream();
            int b;
            while ((b = s.ReadByte()) >= 0 && b != '\n' && bytes.Length < 65536) bytes.WriteByte((byte)b);
            return Encoding.UTF8.GetString(bytes.ToArray()).Trim();
        }

        private const string Help =
            "commands:\n" +
            "  ping                 version, host and process id\n" +
            "  status               idle or running, the tab's status line, takes, key settings\n" +
            "  log [n]              the last n lines of the tab's log (30)\n" +
            "  settings             every saved field, key=value\n" +
            "  set KEY VALUE        change a field (keys as in 'settings'; flags take on/off)\n" +
            "  load                 load the take list (the Load button)\n" +
            "  takes                the loaded takes, numbered from 0\n" +
            "  run all | run N      Run all, or run take N\n" +
            "  run from N           takes N to the end (after a test take, or a stop)\n" +
            "  stop                 Stop after the current step\n" +
            "  state                one read of the game (the Read state button)\n" +
            "  reattach             drop the tether to the game and make it again\n" +
            "  cfg [send | add LINE | remove LINE]   the CFG sent before every recording\n" +
            "  look [send [MAP]]    per-map looks, Looks\\<map>.cfg: sent before every recording on that\n" +
            "                       map; 'look send' applies the loaded map's look now\n" +
            "  send LINE [| LINE]   CFG lines to the game now, by the console's routes (protected dvars too)\n" +
            "  close [force]        close the host the clean way; refused while a batch runs, unless force";

        private static string Handle(string line)
        {
            string cmd = line, rest = "";
            int sp = line.IndexOf(' ');
            if (sp > 0) { cmd = line.Substring(0, sp); rest = line.Substring(sp + 1).Trim(); }

            switch (cmd.ToLowerInvariant())
            {
                case "":
                case "help":
                    return Help + (Extra.Count > 0 ? "\n  " + string.Join(" | ", new System.Collections.Generic.List<string>(Extra.Keys).ToArray())
                                                   + "   the host's own (each answers 'help')" : "");
                case "ping":
                    return "bo2batch Batch Recorder " + BatchInfo.Version + " in " + ConsoleHost.Current.Name
                           + " pid " + System.Diagnostics.Process.GetCurrentProcess().Id;
                case "status":
                    return OnUi(c => c.PipeStatus());
                case "log":
                    {
                        int n;
                        if (!int.TryParse(rest, out n) || n <= 0) n = 30;
                        return OnUi(c => c.PipeLog(n));
                    }
                case "settings":
                    return OnUi(c => c.PipeSettings());
                case "set":
                    {
                        int k = rest.IndexOf(' ');
                        string key = k > 0 ? rest.Substring(0, k) : rest;
                        string value = k > 0 ? rest.Substring(k + 1).Trim() : "";
                        if (key.Length == 0) return "usage: set KEY VALUE";
                        return OnUi(c => c.PipeSet(key, value));
                    }
                case "load":
                    return OnUi(c => c.PipeLoad());
                case "takes":
                    return OnUi(c => c.PipeTakes());
                case "run":
                    return OnUi(c => c.PipeRun(rest));
                case "stop":
                    return OnUi(c => c.PipeStop());
                case "state":
                    return OnUi(c => c.PipeState());
                case "reattach":
                    return OnUi(c => c.Reattach());
                case "cfg":
                    return OnUi(c => c.PipeCfg(rest));
                case "look":
                    return OnUi(c => c.PipeLook(rest));
                case "send":
                    // CFG text straight to the game by UserCfg's routes ('|' between lines); for scripts and remote control
                    // (overgrow night, 2026-10-10). Not while a batch runs: the batch owns the game then.
                    if (rest.Length == 0) return "usage: send CFG-LINE [| CFG-LINE ...]";
                    return UserCfg.Send(rest.Replace("|", "\n"), n => true, l => { });
                case "close":
                    return CloseHost(rest.Equals("force", StringComparison.OrdinalIgnoreCase));
                default:
                    {
                        Func<string, string> extra;
                        if (Extra.TryGetValue(cmd, out extra)) return extra(rest);
                    }
                    return "unknown command '" + cmd + "'\n" + Help;
            }
        }

        private static string OnUi(Func<BatchControl, string> work)
        {
            BatchControl c = BatchControl.Instance;
            if (c == null) return "error: the Batch tab has not been built";

            string result = null;
            DispatcherOperation op = c.Dispatcher.BeginInvoke(new Action(() =>
            {
                try { result = work(c); }
                catch (Exception ex) { result = "error: " + ex.GetBaseException().Message; }
            }));
            if (op.Wait(TimeSpan.FromSeconds(8)) != DispatcherOperationStatus.Completed)
                return "busy: the window did not answer within 8 s";
            return result;
        }

        /// <summary>
        /// Closes the host the way its own button does.
        /// </summary>
        private static string CloseHost(bool force)
        {
            BatchControl c = BatchControl.Instance;
            if (!force && c != null)
            {
                string busy = OnUi(x => x.IsBusy ? "yes" : "no");
                if (busy == "yes") return "refused: a batch is running. 'close force' closes anyway.";
            }

            IConsoleHost host = ConsoleHost.Current;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(500);   // let the reply reach the client first
                try { host.CloseHostWindow(); } catch { }
            });
            return "closing " + host.Name + " the clean way";
        }
    }
}
