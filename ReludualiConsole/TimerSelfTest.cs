using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ReludualiConsole
{
    /// <summary>
    /// ReludualiConsole.exe --timer-test SERVER CODE OUTFILE: the contest link end to end against a server (a trial server on
    /// this PC): a request outside the list is refused before it leaves; link; open; check in empty, then with queued time and
    /// with in-game time; one check-in charging above its rate (the server must refuse it, 422); end. Every request sent is
    /// listed at the end and each must be one of TimerLink.Messages. Exit 0 = all as expected.
    /// </summary>
    public static class TimerSelfTest
    {
        public static int Run(string server, string code, string outFile)
        {
            var log = new StringBuilder();
            int failures = 0;
            Action<string> say = s => log.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s);
            Action<bool, string> check = (ok, what) => { say((ok ? "PASS  " : "FAIL  ") + what); if (!ok) failures++; };
            // the test links with this Windows user's own pass store: never over a real link (2026-10-16: a refused Open left
            // the test's pass behind, and a passing run would have deleted a real one)
            if (TimerLink.LoadPass() != null)
            {
                File.WriteAllText(outFile, "NOT RUN: this Windows user has a real contest link saved; unlink it on the Contest page first\r\n");
                return 2;
            }
            try
            {
                using (var link = new TimerLink(server) { Log = s => say("    sent: " + s) })
                {
                    bool refused = false;
                    try { link.Call("GET", "/api/contests", null, null); } catch (InvalidOperationException) { refused = true; }
                    check(refused, "a request that is not one of the Console's messages is refused before it leaves");
                    check(!TimerLink.Allowed(new Uri("http://example.com/")), "plain http to another machine is refused");

                    TimerLink.Pass pass = link.Link(code);
                    check(pass.Token != null && pass.Token.Length == 64, "linked: a 64-character pass for " + pass.Username + " in " + pass.Contest);
                    check(TimerLink.LoadPass() != null && TimerLink.LoadPass().Token == pass.Token, "the pass reads back from this user's protected store");

                    TimerLink.Window w = link.Open(pass);
                    say("    categories: " + string.Join(", ", w.Categories.Select(c => c.Name + " " + c.Kind + " " + c.Multiplier)));
                    TimedCategory queued = w.Categories.FirstOrDefault(c => c.Kind == "queued"), inGame = w.Categories.FirstOrDefault(c => c.Kind == "in-game");
                    check(queued != null && inGame != null, "the window names the Console's two kinds of time, with the contest's rates");

                    var ledger = new Ledger { Categories = w.Categories };
                    bool gap;
                    Thread.Sleep(1000);
                    link.Beat(pass, w, ledger.Next(), out gap);
                    check(true, "an empty check-in is accepted (it keeps the window alive)");

                    Thread.Sleep(6000);
                    ledger.Add(queued, 5000);
                    var segs = ledger.Next();
                    long charged = link.Beat(pass, w, segs, out gap);
                    ledger.Done(segs);
                    check(segs.Count == 1 && segs[0].InFrontMs == 5000 && segs[0].ChargedMs == (long)Math.Floor(5000 * queued.Multiplier),
                          "queued recording: 5000 ms in front, " + segs[0].ChargedMs + " ms charged (server says " + charged + ")");

                    Thread.Sleep(4000);
                    ledger.Add(inGame, 3000);
                    segs = ledger.Next();
                    charged = link.Beat(pass, w, segs, out gap);
                    ledger.Done(segs);
                    check(segs.Count == 1 && segs[0].Category == inGame.Name && segs[0].ChargedMs == (long)Math.Floor(3000 * inGame.Multiplier),
                          "in-game: 3000 ms in front, " + segs[0].ChargedMs + " ms charged (server says " + charged + ")");

                    Thread.Sleep(2000);
                    int status = 0;
                    try { link.Beat(pass, w, new List<Segment> { new Segment { Category = inGame.Name, InFrontMs = 1000, ChargedMs = 1000 } }, out gap); }
                    catch (Refused r) { status = r.Status; say("    refused: " + r.Message); }
                    check(status == 422, "a check-in charging above its rate is refused (" + status + ")");

                    link.End(pass, w);
                    check(true, "the window ended");
                    TimerLink.ForgetPass();

                    say("Every request sent:");
                    foreach (string s in link.Sent) say("    " + s);
                    bool onlyListed = link.Sent.All(s => s.StartsWith("POST /api/console/pair ") || s.StartsWith("POST /api/console/windows ") ||
                                                         System.Text.RegularExpressions.Regex.IsMatch(s, @"^(POST|GET) /api/console/windows/[A-Za-z0-9_-]+(/beat|/end)? "));
                    check(onlyListed, "nothing was sent but the Console's own messages");
                }
            }
            catch (Exception ex) { say("FAIL  " + ex); failures++; }
            finally { try { TimerLink.ForgetPass(); } catch { } }      // the test's own pass, whatever happened
            say(failures == 0 ? "ALL PASSED" : failures + " FAILED");
            File.WriteAllText(outFile, log.ToString());
            return failures == 0 ? 0 : 1;
        }
    }
}
