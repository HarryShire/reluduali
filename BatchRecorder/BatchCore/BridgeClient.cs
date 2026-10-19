using System;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace BatchRecorder
{
    /// <summary>
    /// Talks to RipperBridge.Red32n, our Redacted plugin, over \\.\pipe\bo2ripper: one
    /// text line out, one line back. The batch records through it ("rec start", "rec
    /// status", "rec stop"), and the console's pages talk to the game through it.
    ///
    /// The pipe has one instance, so a connection is made per request and closed at
    /// once; anything else holding it (a sandbox script) only delays a request.
    /// </summary>
    public static class BridgeClient
    {
        public static string LastError { get; private set; }

        /// <summary>The last film loaded through the bridge ("loaddemo PATH"), whoever sent it: the Console's timeline goes back
        /// by loading it again (2026-10-16: the game's demo_back moved nothing in the sandbox, paused or playing).</summary>
        public static string LastDemo;

        // RipperBridge opens files with Windows' ANSI calls, so a line crosses in this PC's ANSI code page: an Ł in a user name
        // works on a Polish Windows, an é on a French one (the code review's C-5: plain ASCII turned both into '?'). A letter
        // the code page cannot hold is refused here in words, never sent on as a '?' in a path.
        static readonly Encoding Ansi = Encoding.GetEncoding(Encoding.Default.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);

        public static string Ask(string line, int connectMs = 3000, int attempts = 3)
        {
            if (line != null && line.StartsWith("loaddemo ", StringComparison.Ordinal)) LastDemo = line.Substring(9).Trim();
            byte[] outBytes;
            try { outBytes = Ansi.GetBytes(line + "\n"); }
            catch (EncoderFallbackException)
            {
                LastError = "a letter Windows' code page here cannot carry to the game";
                return "error a folder or file name has a letter this PC's Windows code page cannot carry to the game: move it, or rename it with plainer letters";
            }
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", "bo2ripper", PipeDirection.InOut))
                    {
                        pipe.Connect(connectMs);
                        pipe.Write(outBytes, 0, outBytes.Length);
                        pipe.Flush();
                        var got = new System.IO.MemoryStream();
                        int b;
                        while ((b = pipe.ReadByte()) >= 0 && b != '\n') got.WriteByte((byte)b);
                        LastError = "";
                        string reply = Ansi.GetString(got.ToArray()).TrimEnd('\r');
                        TheaterFirst.Saw(line, reply);
                        return reply;
                    }
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    System.Threading.Thread.Sleep(300);
                }
            }
            return null;
        }

        /// <summary>
        /// "cam path FILE", asked again when the free camera was not ready. Mycelium's game, 2026-10-18 16:34:39: "FREE CAMERA on
        /// (mode was 0; F3)", then at once "none of 0 candidates turned the free camera" - the take failed; the next cinematic
        /// 40 s later found the angles on its first look ("2 candidates"). Right after F3 the free camera has not taken its
        /// angles yet, so the search finds nothing to match. So: a second and a third look, 0.8 s and 1.6 s later.
        /// </summary>
        public static string CamPath(string file, Action<string> log = null)
        {
            string reply = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                reply = Ask("cam path " + file, 3000, 1) ?? ("no answer: " + LastError);
                if (reply.StartsWith("ok", StringComparison.Ordinal)) return reply;
                bool notReady = reply.Contains("candidates turned the free camera") || reply.Contains("turn the free camera a little");
                if (!notReady || attempt == 3) break;
                if (log != null) log("    the free camera was not ready (" + reply + "); turning it a touch and asking again");
                // the Preview at 16:26 failed the same way with the bar already on FREE CAMERA: its angles had gone stale. A turn
                // with the game's own command makes the free camera take its angles again (as the bridge does for a yaw near 0)
                Ask("cmd +right", 1000, 1); System.Threading.Thread.Sleep(120); Ask("cmd -right", 1000, 1);
                System.Threading.Thread.Sleep(800 * attempt);
            }
            return reply;
        }

        /// <summary>A number after "key " in a status line, or -1.</summary>
        public static long Field(string status, string key)
        {
            if (status == null) return -1;
            string k = " " + key + " ";
            int i = (" " + status).IndexOf(k, StringComparison.Ordinal);
            if (i < 0) return -1;
            int start = i + k.Length - 1, end = start;
            while (end < status.Length && (char.IsDigit(status[end]) || status[end] == '-')) end++;
            long v;
            return long.TryParse(status.Substring(start, end - start), out v) ? v : -1;
        }
    }
}
