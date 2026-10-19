using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ReludualiConsole
{
    /// <summary>
    /// The Camo Editor as a page of the Designer (2026-10-18; Mycelium: "Go ahead with the webview2 and open asset tool
    /// suggestions"; his rule for other people's PCs: "Its always better our app tells them what they need, or downloads
    /// itself then them having to dig thru documentation").
    ///
    /// The page the Designer serves (CamoServer) shown in Microsoft's WebView2 inside the Designer. Everything it needs is
    /// checked here and fetched with one click when missing:
    ///   the WebView2 runtime  part of Windows 11 and of any PC running Lumit; when missing, Microsoft's own installer is
    ///                         offered (its licence is the person's to accept); meanwhile the editor opens in the browser.
    ///   the gun models        made once from the game's own files by OpenAssetTools (GPLv3, fetched from its authors'
    ///                         release on the person's click, checked against the pinned hash: OatTools); without them the
    ///                         editor paints the flat picture only.
    ///   the C++ runtime       Microsoft's Visual C++ runtime (32-bit, 14.51 or newer), which OpenAssetTools runs on and a
    ///                         clean Windows lacks; when missing or older, Microsoft's own installer is offered as WebView2's.
    /// </summary>
    public sealed class CamoEditorControl : UserControl
    {
        public static CamoEditorControl Instance { get; private set; }
        readonly Grid _body = new Grid();
        readonly TextBlock _models = Ui.Note("", 0), _engine = Ui.Note("", 0);
        readonly Button _make, _browser, _install;
        WebView2 _web;
        string _pending;

        public CamoEditorControl()
        {
            Instance = this;
            Foreground = Ui.Brush("Text");
            _make = Ui.Button("Make the gun models", MakeModels, "Once: OpenAssetTools reads your game's own files and makes the guns' 3D models and pictures (about a minute, about 900 MB)", Glyph.Gun);
            _browser = Ui.Button("Open in the browser", () => CamoServer.OpenInBrowser(null), "The same editor in your browser", Glyph.PopOut);
            _install = Ui.Button("Install WebView2", InstallRuntime, "Microsoft's own installer of its web engine (about 2 MB; you accept its licence); the editor then opens here", Glyph.Add);
            foreach (Button b in new[] { _make, _browser, _install }) b.Margin = new Thickness(0, 0, 8, 0);
            var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 8), LastChildFill = true };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(_install); right.Children.Add(_make); right.Children.Add(_browser);
            DockPanel.SetDock(right, Dock.Right);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(_models); words.Children.Add(_engine);
            bar.Children.Add(right); bar.Children.Add(words);
            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            root.Children.Add(_body);
            Content = root;
            Loaded += (s, e) => Start();
        }

        /// <summary>A picture opened in the editor (the Guns and Sky pages): here when WebView2 is on this PC, else in the
        /// browser. The words say which.</summary>
        public static string Open(string picture)
        {
            var w = Application.Current.MainWindow as MainWindow;
            if (EngineVersion() == null || w == null) return CamoServer.OpenInBrowser(picture);
            w.ShowTool("camo");
            CamoEditorControl c = Instance;
            if (c == null) return CamoServer.OpenInBrowser(picture);
            c.Go(picture);
            return "The Camo Editor is open" + (string.IsNullOrEmpty(picture) ? "" : " with " + Path.GetFileName(picture)) + ". Save, then Send to the game puts it in the running game.";
        }

        /// <summary>The WebView2 runtime's version on this PC, or null (none, or its loader is not beside the Designer).</summary>
        public static string EngineVersion()
        {
            // a runtime of its own (WEBVIEW2_BROWSER_EXECUTABLE_FOLDER, as Windows Sandbox needs) counts only when passed:
            // the call does not read the variable itself (sandbox 0754)
            try { return CoreWebView2Environment.GetAvailableBrowserVersionString(RuntimeFolder()); }
            catch (WebView2RuntimeNotFoundException) { return null; }
            catch (DllNotFoundException) { return null; }
            catch (Exception) { return null; }
        }

        /// <summary>A WebView2 runtime folder named by WEBVIEW2_BROWSER_EXECUTABLE_FOLDER, or null for Windows' own.</summary>
        static string RuntimeFolder()
        {
            string f = Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER");
            return string.IsNullOrWhiteSpace(f) || !Directory.Exists(f) ? null : f;
        }

        void Start()
        {
            ShowModels();
            string v = EngineVersion();
            _install.Visibility = v == null ? Visibility.Visible : Visibility.Collapsed;
            if (v == null)
            {
                _engine.Text = "Microsoft's WebView2 is not on this PC, so the editor opens in your browser for now. Install WebView2 (Microsoft's own installer, about 2 MB) and it opens here.";
                if (_body.Children.Count == 0) _body.Children.Add(Ui.Page(Ui.Card("The Camo Editor", "It runs in your browser until WebView2 is installed: Open in the browser.")));
                return;
            }
            _engine.Text = "";
            Go(_pending);
        }

        void Go(string picture)
        {
            string why, url = CamoServer.Url(out why);
            if (url == null) { _engine.Text = "The editor could not start: " + why + "."; return; }
            if (!string.IsNullOrEmpty(picture)) url += "?open=" + Uri.EscapeDataString(picture);
            if (!IsLoaded) { _pending = picture; return; }
            _pending = null;
            if (_web == null)
            {
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reluduali", "WebView2");
                _web = new WebView2 { CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = data, BrowserExecutableFolder = RuntimeFolder() } };
                _web.CoreWebView2InitializationCompleted += (s, e) =>
                {
                    if (!e.IsSuccess)
                    {
                        _engine.Text = "WebView2 did not start (" + (e.InitializationException != null ? e.InitializationException.Message : "no reason given") + "): Open in the browser works meanwhile.";
                        return;
                    }
                    // links to other sites go to the browser, never inside the Designer
                    _web.CoreWebView2.NewWindowRequested += (s2, e2) => { e2.Handled = true; Browse(e2.Uri); };
                    _web.CoreWebView2.NavigationStarting += (s2, e2) => { if (!e2.Uri.StartsWith(url.Split('?')[0], StringComparison.OrdinalIgnoreCase)) { e2.Cancel = true; Browse(e2.Uri); } };
                };
                _body.Children.Clear();
                _body.Children.Add(_web);
            }
            // the same address again (the page shown again, the models just made): a reload, which setting it does not do
            var to = new Uri(url);
            if (_web.Source != null && _web.Source.AbsoluteUri == to.AbsoluteUri && _web.CoreWebView2 != null) _web.CoreWebView2.Reload();
            else _web.Source = to;
        }

        static void Browse(string uri)
        {
            try { if (uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
        }

        void ShowModels()
        {
            int guns = OatTools.GunModels();
            _make.Visibility = guns > 0 ? Visibility.Collapsed : Visibility.Visible;
            _models.Text = guns > 0 ? "The guns' 3D models: " + guns + " (" + CamoServer.DumpsFolder + ")."
                                    : "No gun models yet: Make the gun models (once) and every gun opens with its 3D model and UV layout. Until then the editor paints the flat picture.";
        }

        // ---------------------------------------------------------------- the gun models: OpenAssetTools, once

        int _making;

        void MakeModels()
        {
            if (Interlocked.CompareExchange(ref _making, 1, 0) != 0) return;
            string game = Where.Game;
            if (game == null) { _models.Text = "The game's folder is not known: start Redacted once (or set it in Settings), then Make the gun models."; _making = 0; return; }
            if (OatTools.Unlinker() == null)
            {
                var ok = MessageBox.Show(Window.GetWindow(this),
                    "Making the gun models uses OpenAssetTools, a free tool by its own authors (GPLv3).\n\n" +
                    "The Designer downloads it now (3.6 MB) from their release on GitHub:\n" + OatTools.Url + "\n" +
                    "checks it against the version it was tested with, and keeps it in " + OatTools.Home + ".\n\nDownload it?",
                    "OpenAssetTools", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (ok != MessageBoxResult.OK) { _making = 0; return; }
            }
            // OpenAssetTools runs on Microsoft's Visual C++ runtime, which a clean Windows lacks (sandbox 0744, 10-18: the
            // Unlinker stopped on "MSVCP140.dll was not found" and nothing came out)
            string tool = OatTools.Unlinker();
            string cpp = OatTools.CppRuntimeMissing(tool == null ? null : Path.GetDirectoryName(tool));
            if (cpp != null)
            {
                var ok = MessageBox.Show(Window.GetWindow(this),
                    cpp + ", and OpenAssetTools needs it.\n\n" +
                    "The Designer downloads Microsoft's own installer of it (about 7 MB) from Microsoft:\n" + OatTools.CppRedist + "\n" +
                    "checks that Microsoft signed it, and starts it. You accept or decline its licence yourself.\n\nInstall it?",
                    "Microsoft Visual C++ runtime", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (ok != MessageBoxResult.OK) { _models.Text = cpp + ": the gun models need it."; _making = 0; return; }
            }
            _make.IsEnabled = false;
            Action<string> say = t => Dispatcher.BeginInvoke(new Action(() => _models.Text = t));
            Task.Run(() =>
            {
                string r;
                try
                {
                    string installed = null;
                    if (cpp != null) { say("Microsoft's Visual C++ runtime installer is open: finish it there."); installed = OatTools.RunCppInstaller(); }
                    r = cpp == null || OatTools.CppRuntimeMissing() == null ? OatTools.MakeGunModels(game, say) : installed;
                }
                catch (Exception ex) { App.Note("making the gun models", ex); r = "Could not make the gun models: " + ex.Message; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _making = 0; _make.IsEnabled = true;
                    ShowModels();
                    if (!r.StartsWith("ok", StringComparison.Ordinal)) _models.Text = r;
                    else if (_web != null && _web.CoreWebView2 != null) _web.CoreWebView2.Reload();
                }));
            });
        }

        // ---------------------------------------------------------------- the WebView2 runtime, when it is missing

        void InstallRuntime()
        {
            _install.IsEnabled = false;
            _engine.Text = "Downloading Microsoft's WebView2 installer...";
            Task.Run(() =>
            {
                string r;
                try { r = OatTools.RunWebView2Installer(); }
                catch (Exception ex) { r = "Could not install WebView2: " + ex.Message; }
                Dispatcher.BeginInvoke(new Action(() => { _install.IsEnabled = true; _engine.Text = r; if (EngineVersion() != null) Start(); }));
            });
        }
    }

    /// <summary>
    /// OpenAssetTools (GPLv3, by its own authors), fetched on first use rather than shipped (2026-10-18): the pinned release
    /// tools\oat\PIN.md names - v0.33.0, its SHA-256 checked before anything is unpacked. Run as a separate program, never
    /// linked. Its Unlinker makes the guns' models, pictures and materials from the game's common_mp.ff, as the MODELS tab
    /// does (tools\mapimport\bodies_cli.py), into the Camo Editor's model folder.
    /// </summary>
    public static class OatTools
    {
        public const string Version = "v0.33.0";
        public const string Url = "https://github.com/Laupetin/OpenAssetTools/releases/download/v0.33.0/oat-windows.zip";
        public const string Sha256 = "4bf366cec03558a4b0f20bc8f197a51f4b9dad0aa3edc52b0e40fef2dd95a4f5";
        const string WebView2Bootstrapper = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        /// <summary>Where a fetched copy lives: %LOCALAPPDATA%\Reluduali\tools\oat\v0.33.0.</summary>
        public static string Home { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reluduali", "tools", "oat", Version); } }

        /// <summary>The Unlinker: a copy chosen in Settings (oat), the one beside the Designer (tools\oat), or the fetched one; null if none.</summary>
        public static string Unlinker()
        {
            foreach (string d in new[] { Where.Saved("oat"), Path.Combine(Where.Tools, "oat", Version), Home })
            {
                if (string.IsNullOrEmpty(d)) continue;
                string exe = Path.Combine(d, "Unlinker.exe");
                if (File.Exists(exe)) return exe;
            }
            return null;
        }

        /// <summary>How many guns' view models the editor's model folder holds (0: none made yet).</summary>
        public static int GunModels()
        {
            try
            {
                string d = Path.Combine(CamoServer.DumpsFolder, "common_mp", "model_export");
                return Directory.Exists(d) ? Directory.GetFiles(d, "t6_wpn_*_view*.xmodel_export").Length : 0;
            }
            catch { return 0; }
        }

        static void Fetch(Action<string> say)
        {
            say("Downloading OpenAssetTools " + Version + " (3.6 MB) from its authors' release...");
            byte[] zip;
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ReludualiDesigner");
                zip = http.GetByteArrayAsync(Url).Result;
            }
            string got;
            using (var sha = SHA256.Create()) got = BitConverter.ToString(sha.ComputeHash(zip)).Replace("-", "").ToLowerInvariant();
            if (got != Sha256) throw new InvalidDataException("the download is not the version the Designer was tested with (its SHA-256 is " + got.Substring(0, 16) + "...): nothing was unpacked");
            string tmp = Home + ".part";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            using (var ms = new MemoryStream(zip)) using (var z = new ZipArchive(ms))
                foreach (ZipArchiveEntry e in z.Entries)
                {
                    string to = Path.GetFullPath(Path.Combine(tmp, e.FullName));
                    if (!to.StartsWith(Path.GetFullPath(tmp) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;    // nothing outside its folder
                    if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(to); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    e.ExtractToFile(to, true);
                }
            File.WriteAllText(Path.Combine(tmp, "SOURCE.txt"),
                "OpenAssetTools " + Version + ", by its own authors, GPLv3 (its licence and source: https://github.com/Laupetin/OpenAssetTools/tree/" + Version + ").\r\n" +
                "Downloaded at your click by the Reluduali Designer on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " from\r\n" + Url + "\r\nSHA-256 " + Sha256 + "\r\n");
            if (Directory.Exists(Home)) Directory.Delete(Home, true);
            Directory.CreateDirectory(Path.GetDirectoryName(Home));
            Directory.Move(tmp, Home);
        }

        /// <summary>The guns' models from the game's common_mp.ff (OpenAssetTools fetched first if it is not here). "ok ..."
        /// or why not; progress through say.</summary>
        public static string MakeGunModels(string game, Action<string> say)
        {
            string zone = Path.Combine(game, "zone", "all", "common_mp.ff");
            if (!File.Exists(zone)) return "The game's guns file is not where it should be (" + zone + "): is the game folder right in Settings?";
            string dumps = CamoServer.DumpsFolder;
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dumps)));
                if (drive.AvailableFreeSpace < 1500L * 1024 * 1024) return "Not enough room on " + drive.Name + " (about 1.5 GB is needed; " + drive.AvailableFreeSpace / (1024 * 1024) + " MB free).";
            }
            catch { }
            if (Unlinker() == null) Fetch(say);
            string exe = Unlinker();
            if (exe == null) return "OpenAssetTools is still not here.";
            string cpp = CppRuntimeMissing(Path.GetDirectoryName(exe));
            if (cpp != null) return cpp + ", and OpenAssetTools needs it: Make the gun models offers Microsoft's installer.";
            say("Making the gun models from your game's files (about a minute)...");
            Directory.CreateDirectory(dumps);
            var psi = new ProcessStartInfo(exe, "--no-color --model-format XMODEL_EXPORT --image-format DDS --include-assets xmodel,material,image -o \"" + Path.Combine(dumps, "?zone?") + "\" \"" + zone + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var tail = new System.Collections.Generic.Queue<string>();
            DataReceivedEventHandler keep = (s, e) => { if (e.Data == null) return; lock (tail) { tail.Enqueue(e.Data); while (tail.Count > 12) tail.Dequeue(); } };
            var sw = Stopwatch.StartNew();
            // a DLL it cannot load ends it with a code here, never a Windows box nobody may see while the Designer waits
            uint was = SetErrorMode(SemFailCriticalErrors);
            Process started;
            try { started = Process.Start(psi); }
            finally { SetErrorMode(was); }
            using (Process p = started)
            {
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }      // never in the way of the person's own work
                p.OutputDataReceived += keep; p.ErrorDataReceived += keep;
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                while (!p.WaitForExit(2000))
                {
                    if (sw.Elapsed > MakeLimit)
                    {
                        try { p.Kill(); } catch { }
                        return "OpenAssetTools did not finish in " + (int)MakeLimit.TotalMinutes + " minutes (it takes about half a minute): stopped. Make the gun models tries again.";
                    }
                    say("Making the gun models from your game's files... " + (int)sw.Elapsed.TotalSeconds + " s");
                }
                p.WaitForExit();
                if (p.ExitCode == DllNotFound || p.ExitCode == EntryPointNotFound || p.ExitCode == BadImage)
                    return "OpenAssetTools could not start: " + (CppRuntimeMissing(Path.GetDirectoryName(exe)) ?? "a part of Windows it needs could not be loaded (0x" + p.ExitCode.ToString("X8") + ")") + ".";
                if (p.ExitCode != 0) { string t; lock (tail) t = string.Join(" | ", tail.ToArray()); return "OpenAssetTools stopped (" + p.ExitCode + "): " + t; }
            }
            int n = GunModels();
            if (n == 0) return "OpenAssetTools ran, but no gun models came out (" + dumps + ").";
            File.WriteAllText(Path.Combine(dumps, "common_mp", ".dumped"), "OpenAssetTools " + Version + ", " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n");
            return "ok " + n + " guns in " + (int)sw.Elapsed.TotalSeconds + " s";
        }

        // ---------------------------------------------------------------- Microsoft's Visual C++ runtime, which it runs on

        /// <summary>Microsoft's permanent link to its latest Visual C++ v14 runtime installer, 32-bit as OpenAssetTools is
        /// (it goes to Visual Studio 2026's; the vs/17 link stays at 14.44, older than OpenAssetTools needs: sandbox 0750).</summary>
        public const string CppRedist = "https://aka.ms/vc14/vc_redist.x86.exe";
        /// <summary>The toolset OpenAssetTools v0.33.0 was linked with (14.51, read from its own header): the runtime must be
        /// at least that new.</summary>
        const int CppToolset = 51;
        static readonly TimeSpan MakeLimit = TimeSpan.FromMinutes(20);
        const int DllNotFound = unchecked((int)0xC0000135), EntryPointNotFound = unchecked((int)0xC0000139), BadImage = unchecked((int)0xC000007B);
        const uint SemFailCriticalErrors = 0x0001;
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);

        /// <summary>The 32-bit Visual C++ runtime OpenAssetTools loads (beside it, else Windows' own): null when it is there and
        /// new enough, else what is wrong, in words.</summary>
        public static string CppRuntimeMissing(string besideTool = null)
        {
            string[] dlls = { "msvcp140.dll", "vcruntime140.dll" };
            if (besideTool != null && dlls.All(d => File.Exists(Path.Combine(besideTool, d)))) return null;
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
            foreach (string dll in dlls)
            {
                string p = Path.Combine(dir, dll);
                if (!File.Exists(p)) return "Microsoft's Visual C++ runtime (32-bit) is not on this PC";
                FileVersionInfo v = FileVersionInfo.GetVersionInfo(p);
                if (v.FileMajorPart < 14 || (v.FileMajorPart == 14 && v.FileMinorPart < CppToolset))
                    return "Microsoft's Visual C++ runtime on this PC (" + v.FileMajorPart + "." + v.FileMinorPart + ") is older than OpenAssetTools needs (14." + CppToolset + ")";
            }
            return null;
        }

        /// <summary>The runtime's version for the Designer's own checks ("14.51"), or "missing" / "old 14.38".</summary>
        public static string CppRuntimeVersion()
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "msvcp140.dll");
            if (!File.Exists(p)) return "missing";
            FileVersionInfo v = FileVersionInfo.GetVersionInfo(p);
            return (CppRuntimeMissing() == null ? "" : "old ") + v.FileMajorPart + "." + v.FileMinorPart;
        }

        /// <summary>Microsoft's Visual C++ runtime installer, downloaded from Microsoft and run as WebView2's is.</summary>
        public static string RunCppInstaller()
        {
            string why = RunMicrosoftInstaller(CppRedist, "vc_redist.x86.exe");
            if (why != null) return why;
            string still = CppRuntimeMissing();
            return still == null ? "Microsoft's Visual C++ runtime is installed." : "The Visual C++ runtime did not install (the installer closed without it): " + still + ".";
        }

        /// <summary>Microsoft's WebView2 bootstrapper, downloaded from Microsoft and run (its own window, its licence the
        /// person's to accept). Its signature must be Microsoft's before it runs.</summary>
        public static string RunWebView2Installer()
        {
            string why = RunMicrosoftInstaller(WebView2Bootstrapper, "MicrosoftEdgeWebview2Setup.exe");
            if (why != null) return why;
            return CamoEditorControl.EngineVersion() != null ? "WebView2 is installed: the editor opens here." : "WebView2 did not install (the installer closed without it): the editor stays in the browser.";
        }

        /// <summary>One of Microsoft's installers: downloaded from Microsoft, run only when Microsoft signed it, in its own
        /// window (its licence the person's to accept), waited for. Null once it ran, else why not.</summary>
        static string RunMicrosoftInstaller(string url, string name)
        {
            string file = Path.Combine(Path.GetTempPath(), name);
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                File.WriteAllBytes(file, http.GetByteArrayAsync(url).Result);
            string signer;
            try { signer = X509Certificate.CreateFromSignedFile(file).Subject; }
            catch { signer = ""; }
            if (signer.IndexOf("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase) < 0)
            {
                try { File.Delete(file); } catch { }
                return "The downloaded installer is not signed by Microsoft: not run.";
            }
            using (Process p = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }))
                p.WaitForExit();
            return null;
        }
    }
}
