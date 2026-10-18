using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace ReludualiConsole
{
    /// <summary>
    /// The person's own Reluduali sign-in, for what the contest pass deliberately cannot do (2026-10-17). Mycelium, via Demo
    /// Database: "make sure theres multiple ways of doing everying. Adding friends on the console, adding friends on the
    /// website, submitting entry on the console, submitting entry on the website"; then "Browser Code, do it now" (the
    /// panel handing over a sign-in waits for the After Effects work: "b will only be once we integrate after effects and
    /// no longer force lumit to be used").
    ///
    /// A browser code, not a password: the Console asks reluduali.com for a short code, opens reluduali.com/device, the
    /// person (signed in there) sees what the Console wants and presses Allow, and the Console gets a token with only those
    /// rights, revocable from the account page. The token is kept for this Windows user only (DPAPI) and sent only to the
    /// server, as "Authorization: Bearer". The contest pass (TimerLink) stays what it was, and the two never mix.
    /// Every request goes through Call, which allows only the messages listed in Allowed.
    /// </summary>
    public sealed class AccountLink : IDisposable
    {
        // "shares" (2026-10-18): putting the person's camos, CFGs, presets and looks on reluduali.com. A site that does not know
        // it yet refuses the code; then the Designer asks again without it (Start), and sharing says to sign in again later
        public static readonly string[] Scopes = { "friends", "contests", "hand-in", "shares" };
        public static readonly string[] OlderScopes = { "friends", "contests", "hand-in" };

        static readonly Regex[] Allowed =
        {
            new Regex(@"^POST /api/device/(start|poll)$"),
            new Regex(@"^GET /api/auth/(me|state)$"),
            new Regex(@"^POST /api/auth/logout$"),
            new Regex(@"^GET /api/(friends|friends/requests|contests|contests/invitations)$"),
            new Regex(@"^POST /api/friends$"),
            new Regex(@"^POST /api/friends/requests/[\w-]{1,64}/(accept|decline)$"),
            new Regex(@"^GET /api/contests/[\w-]{1,32}/(entry|submission)$"),
            new Regex(@"^POST /api/contests/[\w-]{1,32}/(enter|submission|join/[\w-]{1,32})$"),
            // 2026-10-18: the update notifier (no sign-in) and sharing (the "shares" scope): docs\reluduali-server-contract.md
            new Regex(@"^GET /api/designer/updates$"),
            new Regex(@"^GET /api/shares$"),
            new Regex(@"^POST /api/shares$"),
            new Regex(@"^DELETE /api/shares/[\w-]{1,64}$"),
        };

        readonly HttpClient _http;
        readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };    // a shared camo is megabytes
        public Uri Server { get; private set; }
        public Action<string> Log = s => { };

        public AccountLink(string server)
        {
            Uri u;
            if (!Uri.TryCreate(server, UriKind.Absolute, out u) || !TimerLink.Allowed(u)) throw new ArgumentException("refusing to talk to " + server + ": it must be https, or this PC itself");
            Server = new Uri(u.GetLeftPart(UriPartial.Authority) + "/");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, true) { BaseAddress = Server, Timeout = TimeSpan.FromSeconds(20) };
        }

        public void Dispose() { _http.Dispose(); }

        /// <summary>The one door. Answers the JSON (a list comes back as "items"); a refusal throws Refused.</summary>
        public Dictionary<string, object> Call(string method, string path, object body, string token, IDictionary<string, string> query = null)
        {
            string what = method + " " + path;
            if (!Allowed.Any(r => r.IsMatch(what))) throw new InvalidOperationException("not one of the Console's messages: " + what);
            string q = query == null || query.Count == 0 ? "" : "?" + string.Join("&", query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? "")));
            var req = new HttpRequestMessage(new HttpMethod(method), path.TrimStart('/') + q);
            if (token != null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            // a POST always carries a JSON object: the site answers a body of "null" with 400 (reluduali.com, 2026-10-17)
            if (body == null && method == "POST") body = new Dictionary<string, object>();
            if (body != null) req.Content = new StringContent(_json.Serialize(body), Encoding.UTF8, "application/json");
            HttpResponseMessage res;
            try { res = _http.SendAsync(req).ConfigureAwait(false).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log(what + ": no answer (" + ex.GetBaseException().Message + ")"); throw; }
            int status = (int)res.StatusCode;
            string text = res.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            Log(what + " -> " + status);
            object parsed = null;
            try { parsed = _json.DeserializeObject(text); } catch { }
            var data = parsed as Dictionary<string, object>;
            if (data == null) { data = new Dictionary<string, object>(); if (parsed is IEnumerable && !(parsed is string)) data["items"] = parsed; }
            if (status < 200 || status > 299)          // a 201 Created is a success too (the code review's C-26)
            {
                object e; if (!data.TryGetValue("error", out e)) data.TryGetValue("message", out e);
                throw new Refused(status, e as string ?? text);
            }
            return data;
        }

        // ---- the sign-in, kept for this Windows user ------------------------------------------------------------------
        public sealed class SignIn
        {
            public string Token, Username, Server;
            public string[] Scopes = new string[0];          // what the person allowed (older sign-ins: unknown, so none of the new ones)
            public bool May(string scope) { return Scopes.Contains(scope); }
        }

        /// <summary>--server-test keeps its sign-in apart from the person's own.</summary>
        public static string FileOverride;
        static string File_ { get { return FileOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reluduali", "Console", "account.bin"); } }
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Reluduali Console account");

        public static SignIn Load()
        {
            try
            {
                if (!File.Exists(File_)) return null;
                string[] l = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(File_), Entropy, DataProtectionScope.CurrentUser)).Split('\n');
                if (l.Length < 3) return null;
                return new SignIn { Token = l[0], Username = l[1], Server = l[2], Scopes = l.Length >= 4 ? l[3].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries) : new string[0] };
            }
            catch { return null; }
        }

        static void Save(SignIn s)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_));
            File.WriteAllBytes(File_, ProtectedData.Protect(Encoding.UTF8.GetBytes(s.Token + "\n" + s.Username + "\n" + s.Server + "\n" + string.Join(" ", s.Scopes)), Entropy, DataProtectionScope.CurrentUser));
            Changed();
        }

        /// <summary>Signed in or out, anywhere in the Designer: the header's button and every Account card repaint.</summary>
        public static event Action SignedInOrOut;
        static void Changed() { var h = SignedInOrOut; if (h != null) try { h(); } catch { } }

        public static void Forget() { try { if (File.Exists(File_)) File.Delete(File_); } catch { } Changed(); }

        public sealed class Device { public string DeviceCode, UserCode, VerifyUrl; public int IntervalS = 5; public DateTime Until; public string[] Scopes; }

        static string Str(Dictionary<string, object> d, params string[] keys)
        {
            foreach (string k in keys) { object v; if (d != null && d.TryGetValue(k, out v) && v != null) return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture); }
            return null;
        }

        /// <summary>Asks for a browser code (the Console's name and the rights it wants are shown to the person).</summary>
        public Device Start()
        {
            // the name the site's Allow page shows the person (/device); a site that does not know "shares" yet refuses the
            // whole list, so the Designer asks again for the rights it had before
            string[] asked = Scopes;
            Dictionary<string, object> d;
            try { d = Call("POST", "/api/device/start", new Dictionary<string, object> { { "client", "Reluduali Designer" }, { "scopes", asked } }, null); }
            catch (Refused r) when (r.Status == 400 || r.Status == 422)
            {
                Log("the site does not take every right asked for (" + r.Message + "): asking for the older ones");
                asked = OlderScopes;
                d = Call("POST", "/api/device/start", new Dictionary<string, object> { { "client", "Reluduali Designer" }, { "scopes", asked } }, null);
            }
            var dev = new Device
            {
                Scopes = asked,
                DeviceCode = Str(d, "deviceCode", "device_code"),
                UserCode = Str(d, "userCode", "user_code"),
                VerifyUrl = Str(d, "verificationUriComplete", "verification_uri_complete", "verificationUri", "verification_uri") ?? new Uri(Server, "device").ToString(),
            };
            int i; if (int.TryParse(Str(d, "interval") ?? "", out i) && i > 0) dev.IntervalS = i;
            int e; dev.Until = DateTime.Now.AddSeconds(int.TryParse(Str(d, "expiresIn", "expires_in") ?? "", out e) && e > 0 ? e : 600);
            if (string.IsNullOrEmpty(dev.DeviceCode) || string.IsNullOrEmpty(dev.UserCode)) throw new Refused(500, "the server's answer had no code");
            Uri vu; if (!Uri.TryCreate(dev.VerifyUrl, UriKind.Absolute, out vu) || vu.GetLeftPart(UriPartial.Authority) != Server.GetLeftPart(UriPartial.Authority)) dev.VerifyUrl = new Uri(Server, "device").ToString();
            return dev;
        }

        /// <summary>Waits for the person to allow (or not) in the browser. Null with why when they did not, or it ran out.</summary>
        public SignIn Wait(Device dev, Func<bool> cancelled, out string why)
        {
            why = "";
            int every = dev.IntervalS;
            while (DateTime.Now < dev.Until)
            {
                for (int i = 0; i < every * 10; i++) { if (cancelled()) { why = "cancelled"; return null; } Thread.Sleep(100); }
                try
                {
                    var d = Call("POST", "/api/device/poll", new Dictionary<string, object> { { "deviceCode", dev.DeviceCode } }, null);
                    string tok = Str(d, "token", "access_token");
                    if (string.IsNullOrEmpty(tok)) continue;            // still waiting, as a 200 with no token
                    var user = d.ContainsKey("user") ? d["user"] as Dictionary<string, object> : null;
                    var s = new SignIn { Token = tok, Username = Str(user, "username") ?? Str(d, "username") ?? "?", Server = Server.ToString(), Scopes = dev.Scopes ?? new string[0] };
                    // the rights the site says it gave, when it says (else the ones asked for)
                    object sc; if (d.TryGetValue("scopes", out sc) && sc is IEnumerable && !(sc is string)) s.Scopes = ((IEnumerable)sc).Cast<object>().Select(o => Convert.ToString(o)).ToArray();
                    Save(s);
                    return s;
                }
                catch (Refused r)
                {
                    string m = (r.Message ?? "").ToLowerInvariant();
                    if (m.Contains("slow")) { every += 5; continue; }
                    if (m.Contains("pending") || r.Status == 428 || r.Status == 202) continue;
                    if (r.Status == 403 || m.Contains("denied")) { why = "you pressed Deny in the browser"; return null; }
                    if (r.Status == 410 || m.Contains("expired")) { why = "the code ran out (ten minutes): sign in again"; return null; }
                    why = r.Message; return null;
                }
                catch (Exception ex) { why = "reluduali.com did not answer (" + ex.GetBaseException().Message + ")"; return null; }
            }
            why = "the code ran out (ten minutes): sign in again";
            return null;
        }

        /// <summary>Signs out on the site as well (POST /api/auth/logout takes the sign-in back there), then here. Never throws.</summary>
        public static void SignOut(SignIn s)
        {
            if (s != null && !string.IsNullOrEmpty(s.Token) && !string.IsNullOrEmpty(s.Server))
                try { using (var l = new AccountLink(s.Server)) l.Call("POST", "/api/auth/logout", null, s.Token); } catch { }
            Forget();
        }

        public static void OpenBrowser(string url)
        {
            Uri u;
            if (Uri.TryCreate(url, UriKind.Absolute, out u) && TimerLink.Allowed(u)) try { Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true }); } catch { }
        }

        // ---- what a signed-in Console can do --------------------------------------------------------------------------
        public static List<Dictionary<string, object>> Items(Dictionary<string, object> d, string key = "items")
        {
            object v; var list = new List<Dictionary<string, object>>();
            if (d != null && d.TryGetValue(key, out v) && v is IEnumerable)
                foreach (object o in (IEnumerable)v) { var x = o as Dictionary<string, object>; if (x != null) list.Add(x); }
            return list;
        }
        public static string S(Dictionary<string, object> d, string key) { return Str(d, key) ?? ""; }

        public List<Dictionary<string, object>> Friends(SignIn s) { return Items(Call("GET", "/api/friends", null, s.Token)); }
        public Dictionary<string, object> Requests(SignIn s) { return Call("GET", "/api/friends/requests", null, s.Token); }
        public string AddFriend(SignIn s, string username)
        {
            var d = Call("POST", "/api/friends", new Dictionary<string, object> { { "username", username.Trim() } }, s.Token);
            string st = S(d, "status");
            return st == "friends" || st == "accepted" ? "You and " + username.Trim() + " are friends now." : "Asked " + username.Trim() + " to be friends: they accept on the site, in Lumit's panel or in their Console.";
        }
        public void Answer(SignIn s, string userId, bool accept) { Call("POST", "/api/friends/requests/" + userId + (accept ? "/accept" : "/decline"), null, s.Token); }
        public List<Dictionary<string, object>> Contests(SignIn s) { return Items(Call("GET", "/api/contests", null, s.Token)); }
        public List<Dictionary<string, object>> Invitations(SignIn s) { return Items(Call("GET", "/api/contests/invitations", null, s.Token)); }
        public Dictionary<string, object> Entry(SignIn s, string contestId) { return Call("GET", "/api/contests/" + contestId + "/entry", null, s.Token); }
        public Dictionary<string, object> Enter(SignIn s, string contestId, string partner)
        {
            var b = new Dictionary<string, object>(); if (!string.IsNullOrWhiteSpace(partner)) b["partner"] = partner.Trim();
            return Call("POST", "/api/contests/" + contestId + "/enter", b, s.Token);
        }
        public Dictionary<string, object> Join(SignIn s, string contestId, string entryId) { return Call("POST", "/api/contests/" + contestId + "/join/" + entryId, null, s.Token); }
        public Dictionary<string, object> HandIn(SignIn s, string contestId, string youtube, string hq)
        {
            return Call("POST", "/api/contests/" + contestId + "/submission", new Dictionary<string, object> { { "youtube", youtube.Trim() }, { "hq", hq.Trim() } }, s.Token);
        }

        // ---- sharing and updates (2026-10-18; the contract: docs\reluduali-server-contract.md) ------------------------
        /// <summary>The most a share may carry, all its files together, before base64 (the contract's limit).</summary>
        public const long ShareLimit = 25L * 1024 * 1024;

        /// <summary>Puts one share on the site: its kind, a name, a line about it, and its files (name, bytes).</summary>
        public Dictionary<string, object> Share(SignIn s, string kind, string name, string about, string page, IList<KeyValuePair<string, byte[]>> files)
        {
            long total = files.Sum(f => (long)f.Value.Length);
            if (total > ShareLimit) throw new Refused(413, "it is " + (total / 1048576.0).ToString("0.0") + " MB; a share may be 25 MB");
            var body = new Dictionary<string, object>
            {
                { "kind", kind }, { "name", name.Trim() }, { "about", (about ?? "").Trim() },
                { "designer", new Dictionary<string, object> { { "version", Version }, { "edition", Unfinished.Edition } } },
                { "files", files.Select(f => new Dictionary<string, object> { { "name", f.Key }, { "data", Convert.ToBase64String(f.Value) } }).ToArray() },
            };
            if (!string.IsNullOrEmpty(page)) body["page"] = page;
            return Call("POST", "/api/shares", body, s.Token);
        }

        public List<Dictionary<string, object>> MyShares(SignIn s) { return Items(Call("GET", "/api/shares", null, s.Token, new Dictionary<string, string> { { "mine", "1" } })); }
        public void Unshare(SignIn s, string id) { Call("DELETE", "/api/shares/" + id, null, s.Token); }

        /// <summary>What the Reluduali Server says about this Designer: a newer version, notices. No sign-in.</summary>
        public Dictionary<string, object> Updates()
        {
            return Call("GET", "/api/designer/updates", null, null, new Dictionary<string, string>
            {
                { "version", Version }, { "edition", Unfinished.Edition }, { "channel", Unfinished.Channel },
            });
        }

        public static string Version
        {
            get { var v = typeof(AccountLink).Assembly.GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; }
        }
    }
}
