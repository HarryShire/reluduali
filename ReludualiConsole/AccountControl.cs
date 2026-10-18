using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ReludualiConsole
{
    /// <summary>
    /// The CONTEST tab's Account card (2026-10-17): sign in with a browser code, then friends (add, accept, decline) and the
    /// contest (enter alone or with a friend, accept a partner's invite, hand in the YouTube and HQ links): the same things
    /// the site and the Lumit panel do, through the same routes, so a person finds them wherever they reach first.
    /// Every request runs off the window's thread; what comes back is painted.
    /// </summary>
    public sealed class AccountControl : StackPanel
    {
        // the off-screen picture run (--gui-tour) never signs in: nothing it draws may talk to reluduali.com
        AccountLink.SignIn _me = App.Tour ? null : AccountLink.Load();
        AccountLink _link;
        volatile bool _cancel;
        readonly TextBlock _who = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        readonly TextBlock _code = new TextBlock { FontSize = 26, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 4, 0, 4), Visibility = Visibility.Collapsed };
        readonly TextBlock _said = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Opacity = 0.85 };
        readonly WrapPanel _signRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        readonly StackPanel _signedIn = new StackPanel();
        readonly TextBlock _friends = new TextBlock { TextWrapping = TextWrapping.Wrap };
        readonly StackPanel _requests = new StackPanel();
        readonly TextBox _addName = new TextBox { Width = 180, Margin = new Thickness(0, 0, 8, 6), ToolTip = "Their Reluduali username" };
        readonly ComboBox _contest = new ComboBox { MinWidth = 260, Margin = new Thickness(0, 0, 8, 6) };
        readonly TextBlock _entry = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
        readonly TextBox _partner = new TextBox { Width = 180, Margin = new Thickness(0, 0, 8, 6), ToolTip = "A friend's username, or leave it empty to enter alone" };
        readonly StackPanel _invites = new StackPanel();
        readonly TextBox _youtube = new TextBox { Width = 320, Margin = new Thickness(0, 0, 8, 6), ToolTip = "The YouTube link of your entry" };
        readonly TextBox _hq = new TextBox { Width = 320, Margin = new Thickness(0, 0, 8, 6), ToolTip = "A secure (https) download link to the high-quality file" };
        List<Dictionary<string, object>> _contests = new List<Dictionary<string, object>>();

        public AccountControl()
        {
            Children.Add(_who);
            Children.Add(_code);
            Children.Add(_signRow);
            Children.Add(_said);

            var friends = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            friends.Children.Add(Head("Friends"));
            friends.Children.Add(_friends);
            friends.Children.Add(_requests);
            var add = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            add.Children.Add(_addName);
            // what a button sends is read here, on the window's thread: Run works on another one (the code review's C-1)
            add.Children.Add(Btn("Add a friend", (s, e) => { string n = _addName.Text; Run(() => _link.AddFriend(_me, n), true); }, "Sends them a friend request"));
            friends.Children.Add(add);

            var contest = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            contest.Children.Add(Head("Contest"));
            var pick = new WrapPanel();
            pick.Children.Add(_contest);
            pick.Children.Add(Btn("Refresh", (s, e) => Refresh()));
            contest.Children.Add(pick);
            contest.Children.Add(_entry);
            var enter = new WrapPanel();
            enter.Children.Add(new TextBlock { Text = "Partner", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6), Opacity = 0.75 });
            enter.Children.Add(_partner);
            enter.Children.Add(Btn("Enter", (s, e) => { string c = ContestId(), p = _partner.Text; Run(() => { var d = _link.Enter(_me, c, p); return string.IsNullOrWhiteSpace(p) ? "Entered, on your own." : "Entered; " + p.Trim() + " is invited and joins when they accept."; }, true); },
                                   "Enter this contest, alone or with a friend (they accept the invite on the site, in Lumit or in their Designer)"));
            contest.Children.Add(enter);
            contest.Children.Add(_invites);
            var hand = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            var r1 = new WrapPanel(); r1.Children.Add(Label("YouTube")); r1.Children.Add(_youtube); hand.Children.Add(r1);
            var r2 = new WrapPanel(); r2.Children.Add(Label("HQ file")); r2.Children.Add(_hq);
            r2.Children.Add(Ui.Primary(Btn("Hand in", (s, e) => { string c = ContestId(), y = _youtube.Text, h = _hq.Text; Run(() => { _link.HandIn(_me, c, y, h); return "Handed in. You can hand in again until the deadline; the last one counts."; }, true); },
                                           "Hands in your entry's links (again replaces them, until the deadline)")));
            hand.Children.Add(r2);
            contest.Children.Add(hand);
            _signedIn.Children.Add(friends);
            _signedIn.Children.Add(contest);
            Children.Add(_signedIn);
            _contest.SelectionChanged += (s, e) => { if (_me != null && _contest.SelectedIndex >= 0) RefreshEntry(); };
            IsVisibleChanged += (s, e) => { if ((bool)e.NewValue && _me != null && !App.Tour) Refresh(); };
            Paint();
        }

        static TextBlock Head(string s) { return new TextBlock { Text = s, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) }; }
        static TextBlock Label(string s) { return new TextBlock { Text = s, Width = 64, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6), Opacity = 0.75 }; }
        static Button Btn(string text, RoutedEventHandler click, string tip = null) { var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6), ToolTip = tip }; b.Click += click; return b; }

        AccountLink Link()
        {
            string server = _me != null && !string.IsNullOrEmpty(_me.Server) ? _me.Server : TimerLink.ConfiguredServer();
            if (_link == null || _link.Server.GetLeftPart(UriPartial.Authority) != new Uri(server).GetLeftPart(UriPartial.Authority)) _link = new AccountLink(server);
            return _link;
        }

        string ContestId() { int i = _contest.SelectedIndex; return i >= 0 && i < _contests.Count ? AccountLink.S(_contests[i], "id") : ""; }

        void Paint()
        {
            _signRow.Children.Clear();
            if (_me == null)
            {
                _who.Text = "Not signed in. Signing in lets the Designer add friends, enter contests and hand in, as the site and the Lumit panel do. " +
                            "You allow it in your browser; no password is typed here.";
                _signRow.Children.Add(Ui.Primary(Btn("Sign in with the browser...", (s, e) => SignIn(), "Shows a short code and opens reluduali.com/device: press Allow there")));
                _signedIn.Visibility = Visibility.Collapsed;
            }
            else
            {
                _who.Text = "Signed in as " + _me.Username + " (" + new Uri(_me.Server).Host + ").";
                _signRow.Children.Add(Btn("Sign out", (s, e) =>
                {
                    var me = _me; _me = null;
                    _said.Text = "Signing out...";
                    // on the site too (it takes this sign-in back), then here; off the window's thread
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        AccountLink.SignOut(me);
                        Dispatcher.BeginInvoke(new Action(() => { _said.Text = "Signed out, here and on reluduali.com."; Paint(); }));
                    });
                }, "Signs out on reluduali.com and on this PC"));
                _signedIn.Visibility = Visibility.Visible;
            }
        }

        void SignIn()
        {
            _cancel = false;
            _said.Text = "Asking reluduali.com for a code...";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string why = null; AccountLink.SignIn me = null;
                try
                {
                    var link = Link();
                    AccountLink.Device dev = link.Start();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _code.Text = dev.UserCode; _code.Visibility = Visibility.Visible;
                        _said.Text = "Your browser opens " + dev.VerifyUrl + ". Check the code there matches this one, then press Allow. (Not opened? Go there yourself.)";
                        _signRow.Children.Clear();
                        _signRow.Children.Add(Btn("Open the page again", (s, e) => AccountLink.OpenBrowser(dev.VerifyUrl)));
                        _signRow.Children.Add(Btn("Cancel", (s, e) => _cancel = true));
                    }));
                    AccountLink.OpenBrowser(dev.VerifyUrl);
                    me = link.Wait(dev, () => _cancel, out why);
                }
                catch (Refused r) { why = r.Status == 404 ? "this reluduali.com cannot sign in Consoles yet" : r.Message; }
                catch (Exception ex) { why = "reluduali.com did not answer (" + ex.GetBaseException().Message + ")"; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _code.Visibility = Visibility.Collapsed;
                    if (me != null) { _me = me; _said.Text = "Signed in."; Paint(); Refresh(); }
                    else { _said.Text = "Not signed in: " + why + "."; Paint(); }
                }));
            });
        }

        /// <summary>One action, then everything read again. A refusal reads as the server's own words.</summary>
        void Run(Func<string> act, bool refresh)
        {
            if (_me == null) return;
            if (ContestId().Length == 0 && _contests.Count == 0) { }
            _said.Text = "...";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string said;
                try { Link(); said = act(); }
                catch (Refused r) { said = r.Status == 401 ? "reluduali.com no longer takes this sign-in (taken back on the site?): sign in again." : "Not done: " + r.Message + "."; if (r.Status == 401) { AccountLink.Forget(); _me = null; } }
                catch (Exception ex) { said = "Not done: " + ex.GetBaseException().Message; }
                Dispatcher.BeginInvoke(new Action(() => { _said.Text = said; Paint(); if (refresh && _me != null) Refresh(); }));
            });
        }

        public void Refresh()
        {
            var me = _me; if (me == null) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<Dictionary<string, object>> friends = null, contests = null, invites = null; Dictionary<string, object> reqs = null; string err = null;
                try { var l = Link(); friends = l.Friends(me); reqs = l.Requests(me); contests = l.Contests(me); invites = l.Invitations(me); }
                catch (Refused r) { err = r.Status == 401 ? "reluduali.com no longer takes this sign-in: sign in again." : r.Message; if (r.Status == 401) { AccountLink.Forget(); me = null; } }
                catch (Exception ex) { err = "reluduali.com did not answer (" + ex.GetBaseException().Message + ")"; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (me == null) { _me = null; _said.Text = err; Paint(); return; }
                    if (err != null) { _said.Text = err; return; }
                    _friends.Text = friends.Count == 0 ? "No friends yet." : string.Join(", ", friends.Select(f => AccountLink.S(f, "username")));
                    _requests.Children.Clear();
                    foreach (var q in AccountLink.Items(reqs, "incoming"))
                    {
                        string id = AccountLink.S(q, "id"), name = AccountLink.S(q, "username");
                        var row = new WrapPanel();
                        row.Children.Add(new TextBlock { Text = name + " wants to be friends", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) });
                        row.Children.Add(Btn("Accept", (s, e) => Run(() => { _link.Answer(_me, id, true); return "You and " + name + " are friends now."; }, true)));
                        row.Children.Add(Btn("Decline", (s, e) => Run(() => { _link.Answer(_me, id, false); return "Declined."; }, true)));
                        _requests.Children.Add(row);
                    }
                    foreach (var q in AccountLink.Items(reqs, "outgoing"))
                        _requests.Children.Add(new TextBlock { Text = "Asked " + AccountLink.S(q, "username") + ", waiting for them.", Opacity = 0.7, Margin = new Thickness(0, 0, 0, 4) });
                    string keep = ContestId();
                    _contests = contests;
                    _contest.Items.Clear();
                    foreach (var c in contests) _contest.Items.Add(AccountLink.S(c, "name"));
                    int at = contests.FindIndex(c => AccountLink.S(c, "id") == keep);
                    _contest.SelectedIndex = at >= 0 ? at : contests.Count > 0 ? 0 : -1;
                    _invites.Children.Clear();
                    foreach (var iv in invites)
                    {
                        string cid = AccountLink.S(iv, "contestId"), eid = AccountLink.S(iv, "entryId");
                        var from = iv.ContainsKey("from") ? iv["from"] as Dictionary<string, object> : null;
                        string cname = AccountLink.S(contests.FirstOrDefault(c => AccountLink.S(c, "id") == cid), "name");
                        var row = new WrapPanel();
                        row.Children.Add(new TextBlock { Text = AccountLink.S(from, "username") + " invites you to enter " + (cname.Length > 0 ? cname : "a contest") + " with them", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) });
                        row.Children.Add(Btn("Accept", (s, e) => Run(() => { _link.Join(_me, cid, eid); return "You're in, with " + AccountLink.S(from, "username") + "."; }, true)));
                        _invites.Children.Add(row);
                    }
                }));
            });
        }

        void RefreshEntry()
        {
            var me = _me; string cid = ContestId(); if (me == null || cid.Length == 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string text;
                try
                {
                    var e = Link().Entry(me, cid);
                    var members = AccountLink.Items(e, "members");
                    var names = members.Select(m => AccountLink.S(m, "username")).Where(n => n.Length > 0).ToList();
                    text = "Entered" + (names.Count > 1 ? ", with " + string.Join(", ", names.Where(n => n != me.Username)) : names.Count == 1 ? ", on your own" : "") + ".";
                }
                catch (Refused r) { text = r.Status == 404 ? "Not entered yet." : r.Message; }
                catch (Exception ex) { text = ex.GetBaseException().Message; }
                Dispatcher.BeginInvoke(new Action(() => _entry.Text = text));
            });
        }
    }
}
