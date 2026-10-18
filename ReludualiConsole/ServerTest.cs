using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ReludualiConsole
{
    /// <summary>
    /// ReludualiConsole.exe --server-test URL OLDURL OUTDIR (2026-10-18; the stand-in started with --latest above this version): the Designer's side of the update feed and sharing
    /// (docs\reluduali-server-contract.md) against tools\release\server_standin.py - URL a stand-in that knows "shares",
    /// OLDURL one that does not (--old-scopes). The sign-in is kept in OUTDIR, never in the person's own account.bin.
    /// Writes OUTDIR\server-test.txt, PASS/FAIL per check; exit 0 when all pass.
    /// </summary>
    public static class ServerTest
    {
        public static int Run(string url, string oldUrl, string outDir)
        {
            Directory.CreateDirectory(outDir);
            var lines = new List<string>();
            int fails = 0;
            Action<string, bool, string> check = (what, ok, detail) =>
            {
                lines.Add((ok ? "PASS  " : "FAIL  ") + what + (string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")"));
                if (!ok) fails++;
            };
            AccountLink.FileOverride = Path.Combine(outDir, "account.bin");
            try { File.Delete(AccountLink.FileOverride); } catch { }
            try
            {
                using (var link = new AccountLink(url))
                {
                    // 1. the update feed
                    var d = link.Updates();
                    string result;
                    var item = UpdateNotifier.Pick(d, new HashSet<string>(), url, out result);
                    // the stand-in offers whatever its --latest says: run it with a version above this one
                    check("a newer version is offered", item != null && item.Key.StartsWith("version ", StringComparison.Ordinal), item == null ? result : item.Text);
                    check("its Download stays on the server's own host", item != null && item.Url != null && new Uri(item.Url).Host == new Uri(url).Host, item == null ? null : item.Url);
                    var seen = new HashSet<string> { item == null ? "" : item.Key };
                    var notice = UpdateNotifier.Pick(d, seen, url, out result);
                    check("after Not now: the live notice, not the old one", notice != null && notice.Key == "notice standin-1", notice == null ? result : notice.Key);
                    seen.Add("notice standin-1");
                    var elsewhere = UpdateNotifier.Pick(d, seen, url, out result);
                    check("a notice linking off the server shows with no button", elsewhere != null && elsewhere.Key == "notice standin-elsewhere" && elsewhere.Url == null, elsewhere == null ? result : elsewhere.Url);
                    seen.Add("notice standin-elsewhere");
                    check("then nothing (the expired notice stays quiet)", UpdateNotifier.Pick(d, seen, url, out result) == null, result);

                    // 2. the browser-code sign-in, with "shares"
                    var dev = link.Start();
                    check("the code asks for shares", dev.Scopes != null && dev.Scopes.Contains("shares"), string.Join(" ", dev.Scopes ?? new string[0]));
                    string why;
                    var me = link.Wait(dev, () => false, out why);
                    check("signed in", me != null, why);
                    if (me == null) throw new Exception("no sign-in: " + why);
                    check("the sign-in may share", me.May("shares"), string.Join(" ", me.Scopes));
                    var back = AccountLink.Load();
                    check("kept in the test's own file, scopes and all", back != null && back.Token == me.Token && back.May("shares") && File.Exists(AccountLink.FileOverride), null);

                    // 3. one share of every kind
                    var small = new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>("Raid at dusk.cfg", Encoding.UTF8.GetBytes("// == FOG ==\nr_fog 1\n")) };
                    foreach (string kind in new[] { "global", "preset", "cfg", "look", "sliders" })
                    {
                        var r = link.Share(me, kind, "Test " + kind, "from --server-test", kind == "preset" ? "FOG" : null, small);
                        check("share a " + kind, AccountLink.S(r, "id").Length > 0 && AccountLink.S(r, "url").Length > 0, AccountLink.S(r, "url"));
                    }
                    var camo = new List<KeyValuePair<string, byte[]>>
                    {
                        new KeyValuePair<string, byte[]>("manifest.json", Encoding.UTF8.GetBytes("{\"format\":\"reluduali-camo/1\"}")),
                        new KeyValuePair<string, byte[]>("layers.psd", new byte[3 * 1024 * 1024]),
                        new KeyValuePair<string, byte[]>("preview.png", new byte[2048]),
                    };
                    var rc = link.Share(me, "camo", "Test camo", "", null, camo);
                    check("share a camo package (3 MB)", AccountLink.S(rc, "id").Length > 0, AccountLink.S(rc, "url"));
                    try { link.Share(me, "camo", "Too big", "", null, new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>("layers.psd", new byte[26 * 1024 * 1024]) }); check("26 MB is refused before sending", false, "it was sent"); }
                    catch (Refused r) { check("26 MB is refused before sending", r.Status == 413, r.Message); }

                    // 4. the person's own shares, and taking one off
                    var mine = link.MyShares(me);
                    check("my shares: 6", mine.Count == 6, mine.Count.ToString());
                    link.Unshare(me, AccountLink.S(rc, "id"));
                    check("removed: 5 left", link.MyShares(me).Count == 5, null);

                    // 5. the one door
                    try { link.Call("GET", "/api/admin/users", null, me.Token); check("a route not listed is never sent", false, "it was sent"); }
                    catch (InvalidOperationException) { check("a route not listed is never sent", true, null); }

                    AccountLink.SignOut(me);
                    check("signed out: the test's file gone", !File.Exists(AccountLink.FileOverride), null);
                }

                // 6. a site that does not know "shares" yet
                using (var old = new AccountLink(oldUrl))
                {
                    var dev = old.Start();
                    check("an older site: the code asks again without shares", dev.Scopes != null && !dev.Scopes.Contains("shares") && dev.Scopes.Contains("contests"), string.Join(" ", dev.Scopes ?? new string[0]));
                    string why;
                    var me = old.Wait(dev, () => false, out why);
                    check("an older site: signed in", me != null && !me.May("shares"), why);
                    if (me != null)
                    {
                        try { old.Share(me, "cfg", "x", "", null, new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>("x.cfg", new byte[4]) }); check("an older sign-in may not share (403)", false, "it was taken"); }
                        catch (Refused r) { check("an older sign-in may not share (403)", r.Status == 403, r.Message); }
                        AccountLink.SignOut(me);
                    }
                }
            }
            catch (Exception ex) { check("ran to the end", false, ex.GetBaseException().Message); }
            finally { AccountLink.FileOverride = null; }
            lines.Add(fails == 0 ? "ALL PASS" : fails + " FAILED");
            File.WriteAllLines(Path.Combine(outDir, "server-test.txt"), lines);
            return fails == 0 ? 0 : 1;
        }
    }
}
