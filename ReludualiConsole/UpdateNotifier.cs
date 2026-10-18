using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ReludualiConsole
{
    /// <summary>
    /// The update notifier (2026-10-18): what the Reluduali Server says about this Designer - a newer version of its edition
    /// and channel, and notices (a test round, a known problem, a contest) - shown in the bar under the header. In every
    /// edition and build. GET /api/designer/updates, no sign-in, nothing about the person sent: the version, the edition
    /// (public / private) and the channel (release / test). The contract: docs\reluduali-server-contract.md.
    ///
    /// Asked 20 s after the start, then every 6 hours. A server that does not answer, or does not know the route yet, says
    /// nothing in the window (the log has it). "Not now" on a version keeps quiet about that version; on a notice, about
    /// that notice: remembered in %LOCALAPPDATA%\Reluduali\Console\updates-seen.txt.
    /// </summary>
    public sealed class UpdateNotifier
    {
        public sealed class Item { public string Key, Text, Button, Url; }

        readonly Action<Item> _show;
        readonly DispatcherTimer _timer = new DispatcherTimer();
        readonly Dispatcher _ui;
        int _busy;

        public static string LastResult = "not asked yet";
        public static UpdateNotifier Shared;

        public UpdateNotifier(Dispatcher ui, Action<Item> show)
        {
            _ui = ui; _show = show;
            _timer.Interval = TimeSpan.FromSeconds(20);
            _timer.Tick += (s, e) => { _timer.Interval = TimeSpan.FromHours(6); Check(false); };
        }

        public void Start() { _timer.Start(); }

        static string SeenFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reluduali", "Console", "updates-seen.txt"); } }

        static HashSet<string> Seen()
        {
            try { return new HashSet<string>(File.Exists(SeenFile) ? File.ReadAllLines(SeenFile).Select(l => l.Trim()).Where(l => l.Length > 0) : new string[0]); }
            catch { return new HashSet<string>(); }
        }

        public static void Dismiss(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            try { Directory.CreateDirectory(Path.GetDirectoryName(SeenFile)); File.AppendAllLines(SeenFile, new[] { key }); } catch { }
        }

        /// <summary>Asks the server now (SETUP's "Check now" says what it found even when it is nothing).</summary>
        public void Check(bool sayNothingNew, Action<string> said = null)
        {
            if (App.Tour || Interlocked.Exchange(ref _busy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Item item = null; string result;
                try
                {
                    Dictionary<string, object> d;
                    using (var link = new AccountLink(TimerLink.ConfiguredServer())) d = link.Updates();
                    item = Pick(d, sayNothingNew ? new HashSet<string>() : Seen(), TimerLink.ConfiguredServer(), out result);
                }
                catch (Refused r) { result = r.Status == 404 ? "the server has no update feed yet" : "the server said: " + r.Message; }
                catch (Exception ex) { result = "the server did not answer (" + ex.GetBaseException().Message + ")"; }
                LastResult = DateTime.Now.ToString("HH:mm") + ": " + result;
                App.Note("updates: " + result, null);
                Interlocked.Exchange(ref _busy, 0);
                _ui.BeginInvoke(new Action(() => { if (item != null) _show(item); if (said != null) said(LastResult); }));
            });
        }

        static string S(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null; }

        /// <summary>A newer version first, then the first notice not yet put away.</summary>
        internal static Item Pick(Dictionary<string, object> d, HashSet<string> seen, string server, out string result)
        {
            string latest = S(d, "latest");
            Version have = typeof(UpdateNotifier).Assembly.GetName().Version, got;
            if (latest != null && Version.TryParse(latest, out got) && got > new Version(have.Major, have.Minor, Math.Max(have.Build, 0)))
            {
                result = "version " + latest + " is out (this is " + AccountLink.Version + ")";
                string key = "version " + latest + " " + Unfinished.Channel;
                if (!seen.Contains(key))
                {
                    string notes = S(d, "notes");
                    return new Item
                    {
                        Key = key,
                        Text = "The Reluduali Designer " + latest + " is out" + (Unfinished.IsTest ? " (Test Build)" : "") + "." + (string.IsNullOrWhiteSpace(notes) ? "" : " " + notes.Trim()),
                        Button = "Download",
                        Url = SameSite(S(d, "download"), server) ?? new Uri(new Uri(server), "designer").ToString(),
                    };
                }
            }
            else result = "this is the newest (" + AccountLink.Version + ")";
            object n;
            if (d != null && d.TryGetValue("notices", out n) && n is IEnumerable)
                foreach (var o in (IEnumerable)n)
                {
                    var x = o as Dictionary<string, object>;
                    string id = S(x, "id"), text = S(x, "text");
                    if (id == null || string.IsNullOrWhiteSpace(text) || seen.Contains("notice " + id)) continue;
                    DateTime until;
                    if (DateTime.TryParse(S(x, "until") ?? "", out until) && until < DateTime.Now) continue;
                    result += "; a notice";
                    string url = SameSite(S(x, "url"), server);
                    return new Item { Key = "notice " + id, Text = text.Trim(), Button = url != null ? (S(x, "button") ?? "Open") : null, Url = url };
                }
            return null;
        }

        /// <summary>Only a link on the Reluduali Server itself opens from the bar (a notice cannot send the person elsewhere).</summary>
        static string SameSite(string url, string serverUrl)
        {
            Uri u, server;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out u) || !Uri.TryCreate(serverUrl, UriKind.Absolute, out server)) return null;
            return u.GetLeftPart(UriPartial.Authority) == server.GetLeftPart(UriPartial.Authority) && TimerLink.Allowed(u) ? u.ToString() : null;
        }
    }
}
