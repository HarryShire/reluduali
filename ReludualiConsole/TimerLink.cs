using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ReludualiConsole
{
    /// <summary>One of the Console's own kinds of time, as the contest sets it when the window opens (never written in here).</summary>
    public sealed class TimedCategory
    {
        public string Name, Kind;       // Kind: "in-game" or "queued"
        public double Multiplier;
        public string[] Processes;
    }

    /// <summary>What one check-in says about one category, in whole milliseconds.</summary>
    public sealed class Segment
    {
        public string Category;
        public long InFrontMs, ChargedMs;
    }

    /// <summary>The server said no, with its status and reason.</summary>
    public sealed class Refused : Exception
    {
        public readonly int Status;
        public Refused(int status, string reason) : base(reason) { Status = status; }
    }

    /// <summary>
    /// The Console's side of reluduali.com's contest timer: reluduali.com's contract "How the Reluduali Console reports its
    /// time" (2026-10-10, how-the-console-reports.md; Mycelium: "When++"). Linked once to a person with
    /// the code the Lumit panel shows; after that the pass opens a window and checks in about once a minute with what is new
    /// in each of the Console's two kinds of time. The signature and the arithmetic are the Reluduali Timer's
    /// (the Reluduali Timer's BeatSigner, MIT), written again here for .NET Framework.
    ///
    /// Every request it can make is in <see cref="Messages"/>; <see cref="Call"/> refuses anything else, follows no
    /// redirect, and talks only https (or plain http to this PC itself, for a trial server). The pass and the window's key
    /// are never logged or shown.
    /// </summary>
    public sealed class TimerLink : IDisposable
    {
        /// <summary>Everything this sends, in plain words. Nothing about files, paths, the screen or other programs.</summary>
        public static readonly string[] Messages =
        {
            "POST /api/console/pair - once, when you link: the code you typed, nothing else.",
            "POST /api/console/windows - when timing starts (the Designer opens, linked): only the pass.",
            "POST /api/console/windows/<id>/beat - about once a minute: how many milliseconds of in-game work and of queued " +
                "recording are new since the last accepted check-in, what they charge at the contest's rates, and a signature.",
            "GET /api/console/windows/<id> - after an error, to read where the window stands.",
            "POST /api/console/windows/<id>/end - when the Designer closes.",
        };

        static readonly Regex[] AllowedPaths =
        {
            new Regex(@"^POST /api/console/pair$"),
            new Regex(@"^POST /api/console/windows$"),
            new Regex(@"^POST /api/console/windows/[A-Za-z0-9_-]{1,64}/beat$"),
            new Regex(@"^GET /api/console/windows/[A-Za-z0-9_-]{1,64}$"),
            new Regex(@"^POST /api/console/windows/[A-Za-z0-9_-]{1,64}/end$"),
        };

        public const string DefaultServer = "https://reluduali.com";
        readonly HttpClient _http;
        readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        public Uri Server { get; private set; }
        public readonly List<string> Sent = new List<string>();     // "POST /api/console/windows -> 200", for the tab and the test
        public Action<string> Log = s => { };

        public TimerLink(string server)
        {
            Uri u;
            if (!Uri.TryCreate(server, UriKind.Absolute, out u) || !Allowed(u)) throw new ArgumentException("refusing to talk to " + server + ": it must be https, or this PC itself");
            Server = new Uri(u.GetLeftPart(UriPartial.Authority) + "/");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            _http = new HttpClient(handler, true) { BaseAddress = Server, Timeout = TimeSpan.FromSeconds(20) };
        }

        /// <summary>https anywhere, or plain http only to this PC.</summary>
        public static bool Allowed(Uri u)
        {
            if (u.Scheme == Uri.UriSchemeHttps) return true;
            return u.Scheme == Uri.UriSchemeHttp && (u.Host == "127.0.0.1" || u.Host == "localhost" || u.Host == "[::1]" || u.Host == "::1");
        }

        /// <summary>The server: reluduali.com, unless timer-server.txt beside the Console names a trial server.</summary>
        public static string ConfiguredServer()
        {
            try
            {
                string f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "timer-server.txt");
                if (File.Exists(f))
                    foreach (string l in File.ReadAllLines(f))
                        if (l.Trim().Length > 0 && !l.TrimStart().StartsWith("#")) return l.Trim();
            }
            catch { }
            return DefaultServer;
        }

        /// <summary>The one door every request goes through.</summary>
        public Dictionary<string, object> Call(string method, string path, object body, string pass)
        {
            string what = method + " " + path;
            if (!AllowedPaths.Any(r => r.IsMatch(what))) throw new InvalidOperationException("not one of the Console's messages: " + what);
            var req = new HttpRequestMessage(new HttpMethod(method), path.TrimStart('/'));
            if (pass != null) req.Headers.TryAddWithoutValidation("Authorization", "Console " + pass);
            if (body != null) req.Content = new StringContent(_json.Serialize(body), Encoding.UTF8, "application/json");
            HttpResponseMessage res;
            try { res = _http.SendAsync(req).ConfigureAwait(false).GetAwaiter().GetResult(); }   // no return to a window thread: that deadlocks
            catch (Exception ex) { lock (Sent) Sent.Add(what + " -> no answer"); Log(what + ": no answer (" + ex.GetBaseException().Message + ")"); throw; }
            int status = (int)res.StatusCode;
            string text = res.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            lock (Sent) Sent.Add(what + " -> " + status);
            Log(what + " -> " + status);
            Dictionary<string, object> data = null;
            try { data = _json.Deserialize<Dictionary<string, object>>(text); } catch { }
            if (status != 200)
            {
                object e = null;
                if (data != null) { if (!data.TryGetValue("error", out e)) data.TryGetValue("message", out e); }
                throw new Refused(status, e as string ?? text);
            }
            return data ?? new Dictionary<string, object>();
        }

        public void Dispose() { _http.Dispose(); }

        // -------------------------------------------------------------- the pass, kept for this Windows user only

        public sealed class Pass
        {
            public string Token, Username, Contest, Server;
            public long ExpiresAt;
            public bool Expired { get { return ExpiresAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > ExpiresAt; } }
        }

        static string PassFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reluduali", "Console", "pass.bin"); } }
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Reluduali Console pass");

        public static Pass LoadPass()
        {
            try
            {
                if (!File.Exists(PassFile)) return null;
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(PassFile), Entropy, DataProtectionScope.CurrentUser);
                return new JavaScriptSerializer().Deserialize<Pass>(Encoding.UTF8.GetString(plain));
            }
            catch { return null; }
        }

        static void SavePass(Pass p)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PassFile));
            byte[] plain = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(p));
            File.WriteAllBytes(PassFile, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }

        public static void ForgetPass() { try { if (File.Exists(PassFile)) File.Delete(PassFile); } catch { } }

        /// <summary>Links with the code from the Lumit panel (any case, dash or spaces). The pass is kept; returns it.</summary>
        public Pass Link(string code)
        {
            string c = new string((code ?? "").Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
            var d = Call("POST", "/api/console/pair", new Dictionary<string, object> { { "code", c } }, null);
            var user = d.ContainsKey("user") ? d["user"] as Dictionary<string, object> : null;
            var contest = d.ContainsKey("contest") ? d["contest"] as Dictionary<string, object> : null;
            var p = new Pass
            {
                Token = d["token"] as string,
                ExpiresAt = d.ContainsKey("expiresAt") ? Convert.ToInt64(d["expiresAt"], CultureInfo.InvariantCulture) : 0,
                Username = user != null ? user["username"] as string : "",
                Contest = contest != null && contest.ContainsKey("name") ? contest["name"] as string : "",
                Server = Server.ToString(),
            };
            if (string.IsNullOrEmpty(p.Token)) throw new Refused(500, "the server gave no pass");
            SavePass(p);
            return p;
        }

        // -------------------------------------------------------------- one window

        public sealed class Window
        {
            public string Id, Key, Nonce, Previous = "";
            public long Seq;
            public int IdleMinutes = 3;
            public List<TimedCategory> Categories = new List<TimedCategory>();
        }

        public Window Open(Pass pass)
        {
            var d = Call("POST", "/api/console/windows", null, pass.Token);
            var w = new Window
            {
                Id = d["id"] as string, Key = d["key"] as string, Nonce = d["nonce"] as string,
                Seq = d.ContainsKey("seq") ? Convert.ToInt64(d["seq"], CultureInfo.InvariantCulture) : 0,
            };
            var contest = d.ContainsKey("contest") ? d["contest"] as Dictionary<string, object> : null;
            if (contest != null && contest.ContainsKey("idleMinutes")) w.IdleMinutes = Convert.ToInt32(contest["idleMinutes"], CultureInfo.InvariantCulture);
            var cats = d.ContainsKey("categories") ? d["categories"] as System.Collections.ArrayList : null;
            if (cats != null)
                foreach (Dictionary<string, object> c in cats)
                {
                    object kind; c.TryGetValue("console", out kind);
                    if (!(kind is string)) continue;
                    var procs = c.ContainsKey("processes") ? c["processes"] as System.Collections.ArrayList : null;
                    w.Categories.Add(new TimedCategory
                    {
                        Name = c["name"] as string, Kind = (string)kind,
                        Multiplier = Convert.ToDouble(c["multiplier"], CultureInfo.InvariantCulture),
                        Processes = procs != null ? procs.Cast<object>().Select(o => o as string).Where(s => s != null).ToArray() : new string[0],
                    });
                }
            return w;
        }

        /// <summary>The check-in's signature: HMAC-SHA256(key, previous + "\n" + "seq|nonce|cat:in:charged,..."), lowercase hex.</summary>
        public static string Text(long seq, string nonce, IList<Segment> segments)
        {
            return seq.ToString(CultureInfo.InvariantCulture) + "|" + nonce + "|" +
                   string.Join(",", segments.Select(s => s.Category + ":" + s.InFrontMs.ToString(CultureInfo.InvariantCulture) + ":" + s.ChargedMs.ToString(CultureInfo.InvariantCulture)));
        }

        public static string Sign(string keyHex, string previous, long seq, string nonce, IList<Segment> segments)
        {
            using (var h = new HMACSHA256(FromHex(keyHex)))
                return ToHex(h.ComputeHash(Encoding.UTF8.GetBytes((previous ?? "") + "\n" + Text(seq, nonce, segments))));
        }

        /// <summary>One check-in. On 200 the window moves on (nonce, seq, chain); a refusal throws.</summary>
        public long Beat(Pass pass, Window w, IList<Segment> segments, out bool gap)
        {
            long seq = w.Seq + 1;
            string sig = Sign(w.Key, w.Previous, seq, w.Nonce, segments);
            var body = new Dictionary<string, object>
            {
                { "seq", seq }, { "nonce", w.Nonce }, { "signature", sig },
                { "segments", segments.Select(s => new Dictionary<string, object> { { "category", s.Category }, { "inFrontMs", s.InFrontMs }, { "chargedMs", s.ChargedMs } }).ToList() },
            };
            var d = Call("POST", "/api/console/windows/" + w.Id + "/beat", body, pass.Token);
            w.Nonce = d["nonce"] as string;
            w.Seq = d.ContainsKey("seq") ? Convert.ToInt64(d["seq"], CultureInfo.InvariantCulture) : seq;
            w.Previous = sig;
            gap = d.ContainsKey("gap") && d["gap"] is bool && (bool)d["gap"];
            return d.ContainsKey("chargedMs") ? Convert.ToInt64(d["chargedMs"], CultureInfo.InvariantCulture) : 0;
        }

        public void End(Pass pass, Window w) { Call("POST", "/api/console/windows/" + w.Id + "/end", null, pass.Token); }

        static byte[] FromHex(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        static string ToHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>
    /// The Reluduali Timer's BeatBuilder, for the Console: running totals per category in whole milliseconds, so rounding
    /// never loses or invents time; each check-in reports what is new since the last ACCEPTED one, and never charges a
    /// category above its own rate.
    /// </summary>
    public sealed class Ledger
    {
        readonly Dictionary<string, double> _inFront = new Dictionary<string, double>(), _charged = new Dictionary<string, double>();
        readonly Dictionary<string, long[]> _sent = new Dictionary<string, long[]>();
        public List<TimedCategory> Categories = new List<TimedCategory>();

        public void Add(TimedCategory c, double ms)
        {
            double v;
            _inFront.TryGetValue(c.Name, out v); _inFront[c.Name] = v + ms;
            _charged.TryGetValue(c.Name, out v); _charged[c.Name] = v + ms * c.Multiplier;
        }

        public double InFront(string name) { double v; return _inFront.TryGetValue(name, out v) ? v : 0; }
        public double Charged(string name) { double v; return _charged.TryGetValue(name, out v) ? v : 0; }

        public List<Segment> Next()
        {
            var list = new List<Segment>();
            foreach (TimedCategory c in Categories)
            {
                long[] already; if (!_sent.TryGetValue(c.Name, out already)) already = new long[2];
                long inFront = (long)Math.Floor(InFront(c.Name)) - already[0];
                long owed = (long)Math.Floor(Charged(c.Name)) - already[1];
                long charged = Math.Min(owed, (long)Math.Floor(inFront * c.Multiplier));
                if (inFront > 0 || charged > 0) list.Add(new Segment { Category = c.Name, InFrontMs = inFront, ChargedMs = Math.Max(0, charged) });
            }
            return list;
        }

        /// <summary>A new window: what the old one did not accept goes uncharged, never into the new one (the contract's 409 row).</summary>
        public void DropUnsent()
        {
            foreach (TimedCategory c in Categories)
                _sent[c.Name] = new[] { (long)Math.Floor(InFront(c.Name)), (long)Math.Floor(Charged(c.Name)) };
        }

        /// <summary>Accepted by the server, or given up (a refusal that says to count it as sent): never sent again.</summary>
        public void Done(IEnumerable<Segment> segments)
        {
            foreach (Segment s in segments)
            {
                long[] already; if (!_sent.TryGetValue(s.Category, out already)) already = new long[2];
                _sent[s.Category] = new[] { already[0] + s.InFrontMs, already[1] + s.ChargedMs };
            }
        }
    }
}
