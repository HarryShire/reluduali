using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ReludualiSetup
{
    /// <summary>What the person picked (or a test passed on the command line).</summary>
    public sealed class Choices
    {
        public string Redacted = "", InstallDir = DefaultInstallDir;
        public bool Bridge = true, Font = false, Link = true, Desktop = true, StartMenu = true;
        public bool Theater = false;         // the theater-only launch: "Reluduali Theater" shortcuts (the Console puts the theater layer in per launch)
        public string Lumit = "beside";      // "beside" (Lumit's own Install from a folder), "into" (straight into Lumit's data), "none"

        // where things outside the install folder go; a test points every one of them into a scratch folder
        public string LumitExtensions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumit", "Lumit", "data", "extensions");
        public string ConsoleConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reluduali", "Console");
        public string StartMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        public string DesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        public string RegRoot = "Software";  // HKCU\<this>\Classes\reluduali-console and ...\Uninstall\Reluduali

        public static string DefaultInstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Reluduali"); } }

        /// <summary>The test switch: everything outside the install folder goes under one scratch folder, the registry under a test key.</summary>
        public void TestRoot(string root)
        {
            LumitExtensions = Path.Combine(root, "lumit-extensions");
            ConsoleConfig = Path.Combine(root, "console-config");
            StartMenuDir = Path.Combine(root, "start-menu");
            DesktopDir = Path.Combine(root, "desktop");
            RegRoot = @"Software\ReludualiSetupTest";
        }
    }

    /// <summary>
    /// The Reluduali installer's work (2026-10-16; Mycelium: "One big install that ships the Lumit Extention, The Console and the
    /// Redacted Plugin (and the polish font stuff for fun" ... "asks for his redacted directory"). Per person, no administrator.
    /// Everything it puts down or moves aside is written to install-record.txt in the install folder, and the uninstaller
    /// takes exactly that back out, in reverse: nothing it did not put there is ever removed, and what it moved aside goes back.
    /// </summary>
    public static class Installer
    {
        public const string Version = "0.7.8";
        public const string RecordName = "install-record.txt";
        public const string UninstallerName = "Uninstall Reluduali.exe";
        public const string TheaterShortcut = "Reluduali Theater.lnk";
        public const string LumitNeeds = "Needs Lumit 0.8.0 or newer (or a nightly from v0.7.1-nightly.20261015.30 on).";

        /// <summary>What a Redacted folder holds: (ok, a sentence for the person).</summary>
        public static string CheckRedacted(string dir, out bool ok)
        {
            ok = false;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return "Choose the folder that holds t6mpv43.exe.";
            if (!File.Exists(Path.Combine(dir, "t6mpv43.exe"))) return "No t6mpv43.exe in this folder: choose Redacted's own folder.";
            ok = true;
            string plugins = Path.Combine(dir, "Plugins");
            bool bridge = File.Exists(Path.Combine(plugins, "RipperBridge.Red32n"));
            return "t6mpv43.exe found." + (bridge ? " A RipperBridge is already there: it is kept aside, never deleted." : "");
        }

        // a zip made by Windows PowerShell names its entries with '\\', one made elsewhere with '/': both are read the same
        static string Norm(ZipArchiveEntry e) { return e.FullName.Replace('\\', '/'); }
        static ZipArchiveEntry Find(ZipArchive z, string name) { return z.Entries.FirstOrDefault(e => Norm(e) == name); }

        static Stream Payload()
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
            if (s == null) throw new InvalidOperationException("this installer has no payload (built without make_installer.ps1)");
            return s;
        }

        /// <summary>Whether this installer carries a piece (the window only offers what is in it: the public one has no font pack).</summary>
        public static bool Carries(string entry)
        {
            try { using (Stream s = Payload()) using (var z = new ZipArchive(s, ZipArchiveMode.Read)) return Find(z, entry) != null; }
            catch { return false; }
        }

        public static void Install(Choices c, Action<string> say)
        {
            bool ok;
            string why = CheckRedacted(c.Redacted, out ok);
            if (!ok) throw new InvalidOperationException(why);
            if ((c.Bridge || c.Font || c.Theater) && Process.GetProcessesByName("t6mpv43").Length > 0)
                throw new InvalidOperationException("Close Redacted first: it holds its plugins and zones while it runs.");
            if (Process.GetProcessesByName("ReludualiConsole").Length > 0)
                throw new InvalidOperationException("Close The Reluduali Designer first.");

            // an earlier install in this folder is taken out first, exactly as its uninstaller would (what it set aside goes back
            // where it was): this install's record is then whole, and nothing it sets aside is an earlier copy of its own
            // (2026-10-17: before this, a second install kept the first one's RipperBridge as "the one that was there", and the
            // person's own went out of the record). After it, anything of ours by name that is still there is the person's.
            if (File.Exists(Path.Combine(c.InstallDir, RecordName)))
            {
                say("An earlier install is in " + c.InstallDir + ": taking it out first (your presets stay).");
                TakeOut(c.InstallDir, say, false);
            }

            var rec = new List<string>();
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Directory.CreateDirectory(c.InstallDir);
            try
            {
            // every folder made is recorded, outermost first, so the uninstaller (last first) takes out the innermost first
            Action<string> wroteDir = d =>
            {
                var missing = new Stack<string>();
                for (string x = d; !string.IsNullOrEmpty(x) && !Directory.Exists(x); x = Path.GetDirectoryName(x)) missing.Push(x);
                foreach (string x in missing) { Directory.CreateDirectory(x); rec.Add("dir\t" + x); }
            };

            using (Stream ps = Payload())
            using (var zip = new ZipArchive(ps, ZipArchiveMode.Read))
            {
                // the Console
                int n = 0;
                foreach (ZipArchiveEntry e in zip.Entries.Where(e => Norm(e).StartsWith("console/") && e.Name.Length > 0))
                {
                    string to = Path.Combine(c.InstallDir, Norm(e).Substring(8).Replace('/', '\\'));
                    wroteDir(Path.GetDirectoryName(to));
                    e.ExtractToFile(to, true);
                    rec.Add("file\t" + to);
                    n++;
                }
                if (n == 0) throw new InvalidOperationException("this installer's payload has no Designer in it");
                say("The Reluduali Designer: " + n + " files in " + c.InstallDir);

                // the uninstaller: this program itself
                string un = Path.Combine(c.InstallDir, UninstallerName);
                File.Copy(Assembly.GetExecutingAssembly().Location, un, true);
                rec.Add("file\t" + un);

                // the game's plugins: what is there already goes to <Redacted>\reluduali-backup (outside Plugins: the game loads
                // every .Red32n in Plugins), never deleted
                string plugins = Path.Combine(c.Redacted, "Plugins");
                foreach (var p in new[] { new { On = c.Bridge, Entry = "plugin/RipperBridge.Red32n", Name = "RipperBridge.Red32n", What = "RipperBridge" },
                                          new { On = c.Font, Entry = "plugin/RipperBridge.font", Name = "RipperBridge.font", What = "The Polish font pack" } })
                {
                    if (!p.On) continue;
                    ZipArchiveEntry e = Find(zip, p.Entry);
                    if (e == null) { say(p.What + ": not in this installer, skipped."); continue; }
                    wroteDir(plugins);
                    string to = Path.Combine(plugins, p.Name);
                    if (File.Exists(to))
                    {
                        string keep = Path.Combine(c.Redacted, "reluduali-backup");
                        wroteDir(keep);
                        string aside = Path.Combine(keep, p.Name + "." + stamp);
                        File.Move(to, aside);
                        rec.Add("moved\t" + to + "\t" + aside);
                        say(p.What + ": the one that was there is kept as " + aside);
                    }
                    e.ExtractToFile(to, false);
                    rec.Add("file\t" + to);
                    say(p.What + ": into " + plugins);
                }

                // nothing of the theater goes into the game here (his design, 2026-10-17: "If the USER starts Redacted on its own,
                // There is no overwrite"): the Console puts the theater layer in for each of its theater launches, from
                // theater\ffotdmp.lua beside itself (shipped with the Console above), and takes it out when that game exits.

                // the Lumit panel (Reluduali's, pinned and unchanged): beside the Console for Lumit's own Install from a folder,
                // or straight into Lumit's extensions (Mycelium: "a, b - both, just make it optional in the Installer")
                if (c.Lumit != "none")
                {
                    ZipArchiveEntry pz = Find(zip, "lumit/reluduali-lumit.zip"), ph = Find(zip, "lumit/sha256.txt");
                    if (pz == null) say("The Lumit panel: not in this installer, skipped.");
                    else
                    {
                        byte[] bytes;
                        using (var ms = new MemoryStream()) { using (Stream s = pz.Open()) s.CopyTo(ms); bytes = ms.ToArray(); }
                        if (ph != null)
                        {
                            string want; using (var r = new StreamReader(ph.Open())) want = r.ReadToEnd().Trim().ToLowerInvariant();
                            string got; using (var sha = SHA256.Create()) got = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                            if (want != got) throw new InvalidOperationException("the Lumit panel in this installer is not the one it should be (SHA-256 " + got + ")");
                        }
                        string target = c.Lumit == "into" ? Path.Combine(c.LumitExtensions, "reluduali") : Path.Combine(c.InstallDir, "Lumit panel");
                        if (Directory.Exists(target))
                        {
                            string aside = target + ".before-" + stamp;      // a folder not named "reluduali": Lumit's scan passes it by
                            Directory.Move(target, aside);
                            rec.Add("moved\t" + target + "\t" + aside);
                        }
                        wroteDir(Path.GetDirectoryName(target));
                        Directory.CreateDirectory(target);
                        rec.Add("tree\t" + target);
                        using (var inner = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
                            foreach (ZipArchiveEntry e in inner.Entries.Where(x => x.Name.Length > 0))
                            {
                                string to = Path.Combine(target, Norm(e).Replace('/', '\\'));
                                Directory.CreateDirectory(Path.GetDirectoryName(to));
                                e.ExtractToFile(to, true);
                            }
                        say(c.Lumit == "into"
                            ? "The Lumit panel: straight into Lumit (" + target + "). Lumit lists it the next time it starts (Lumit won't show its own list of what the panel can do first)."
                            : "The Lumit panel: in " + target + ". In Lumit: Settings > Addons > Extensions > Install from a folder, and choose that folder.");
                        say(LumitNeeds);
                    }
                }
            }

            string exe = Path.Combine(c.InstallDir, "ReludualiConsole.exe");

            // the Redacted folder, where the Console reads it (Where.Installed)
            wroteDir(c.ConsoleConfig);
            string rf = Path.Combine(c.ConsoleConfig, "redacted-folder.txt");
            File.WriteAllText(rf, c.Redacted.TrimEnd('\\') + "\r\n");
            rec.Add("file\t" + rf);

            // Lumit's Reluduali panel can open the Designer (Lumit asks first, every time); the command is the exe itself
            if (c.Link)
            {
                string key = c.RegRoot + @"\Classes\reluduali-console";
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(key))
                {
                    k.SetValue("", "URL:The Reluduali Designer");
                    k.SetValue("URL Protocol", "");
                    using (RegistryKey cmd = k.CreateSubKey(@"shell\open\command")) cmd.SetValue("", "\"" + exe + "\" \"%1\"");
                }
                rec.Add("reg\t" + key);
                say("Lumit can open the Designer (the reluduali-console: link, for you only).");
            }

            // shortcuts
            if (c.StartMenu) { wroteDir(c.StartMenuDir); string l = Path.Combine(c.StartMenuDir, "The Reluduali Designer.lnk"); Shortcut(l, exe, c.InstallDir); rec.Add("file\t" + l); }
            if (c.Desktop) { wroteDir(c.DesktopDir); string l = Path.Combine(c.DesktopDir, "The Reluduali Designer.lnk"); Shortcut(l, exe, c.InstallDir); rec.Add("file\t" + l); }

            // the theater-only launch (2026-10-17): "Reluduali Theater" runs the Console with --theater (its splash, the game docked,
            // the DEMOS tab). An earlier install's is gone by now (above), so one of that name still there is the person's: kept
            if (c.Theater)
            {
                var placed = new List<string>();
                foreach (var w in new[] { new { On = c.StartMenu, Dir = c.StartMenuDir, Where = "in the Start menu" }, new { On = c.Desktop, Dir = c.DesktopDir, Where = "on the desktop" } })
                {
                    if (!w.On) continue;
                    string l = Path.Combine(w.Dir, TheaterShortcut);
                    if (File.Exists(l)) { say("A \"Reluduali Theater\" shortcut that is not this installer's is already in " + w.Dir + ": left as it is."); continue; }
                    wroteDir(w.Dir);
                    Shortcut(l, exe, c.InstallDir, "--theater", "Black Ops II's theater, straight in: the game docked in The Reluduali Designer");
                    rec.Add("file\t" + l);
                    placed.Add(w.Where);
                }
                if (placed.Count > 0) say("The theater-only launch: \"Reluduali Theater\" " + string.Join(" and ", placed) + ".");
            }

            // the entry in Windows' Installed apps
            string ukey = c.RegRoot + @"\Microsoft\Windows\CurrentVersion\Uninstall\Reluduali";
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(ukey))
            {
                k.SetValue("DisplayName", "The Reluduali Designer");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "Reluduali");
                k.SetValue("InstallLocation", c.InstallDir);
                k.SetValue("DisplayIcon", exe);
                k.SetValue("UninstallString", "\"" + Path.Combine(c.InstallDir, UninstallerName) + "\" /uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            rec.Add("reg\t" + ukey);
            }
            catch
            {
                // stopped partway (a file held open, the window closed): what was done is recorded all the same, so the
                // uninstaller - or this installer run again - takes exactly that back out and puts back what it set aside
                // (the code review's C-8: without it, a second run kept our own RipperBridge as "the one that was there")
                try
                {
                    rec.Insert(0, "# Reluduali " + Version + ", an install that stopped partway " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ": what it did, taken back out in reverse");
                    File.WriteAllLines(Path.Combine(c.InstallDir, RecordName), rec, new UTF8Encoding(false));
                }
                catch { }
                throw;
            }

            rec.Insert(0, "# Reluduali " + Version + ", installed " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ": what the uninstaller takes back out, in reverse");
            File.WriteAllLines(Path.Combine(c.InstallDir, RecordName), rec, new UTF8Encoding(false));
            say("Done.");
        }

        static void Shortcut(string lnk, string target, string workDir, string args = "", string what = "The Reluduali Designer for Black Ops II")
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic sh = Activator.CreateInstance(t);
            dynamic s = sh.CreateShortcut(lnk);
            s.TargetPath = target;
            s.Arguments = args;
            s.WorkingDirectory = workDir;
            s.Description = what;
            s.Save();
        }

        /// <summary>Takes back out exactly what install-record.txt lists, last first; puts back what was moved aside.</summary>
        public static void Uninstall(string installDir, Action<string> say)
        {
            if (Process.GetProcessesByName("ReludualiConsole").Length > 0) throw new InvalidOperationException("Close The Reluduali Designer first.");
            TakeOut(installDir, say, true);
        }

        /// <summary>
        /// The uninstaller's work, also an install's first step over an earlier one (then nothing removes this program or the
        /// folder). Throws, having changed nothing, while the game holds files it would take out.
        /// </summary>
        static void TakeOut(string installDir, Action<string> say, bool removeSelf)
        {
            string recPath = Path.Combine(installDir, RecordName);
            if (!File.Exists(recPath)) throw new InvalidOperationException("No " + RecordName + " in " + installDir + ": nothing to take out.");
            string self = Assembly.GetExecutingAssembly().Location;
            var lines = File.ReadAllLines(recPath).Where(l => l.Length > 0 && !l.StartsWith("#")).Reverse().ToList();
            if (lines.Any(l => l.StartsWith("file\t") && (l.EndsWith(".Red32n", StringComparison.OrdinalIgnoreCase) || l.EndsWith(".ff", StringComparison.OrdinalIgnoreCase)
                                                        || l.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)))
                && Process.GetProcessesByName("t6mpv43").Length > 0)
                throw new InvalidOperationException("Close Redacted first: it holds its plugins and zones while it runs.");
            int files = 0, back = 0;
            foreach (string l in lines)
            {
                string[] f = l.Split('\t');
                try
                {
                    switch (f[0])
                    {
                        case "file":
                            if (string.Equals(f[1], self, StringComparison.OrdinalIgnoreCase)) break;     // this program: last, below
                            if (File.Exists(f[1])) { File.Delete(f[1]); files++; }
                            break;
                        case "tree":
                            if (Directory.Exists(f[1])) Directory.Delete(f[1], true);
                            break;
                        case "moved":
                            if (!File.Exists(f[1]) && File.Exists(f[2])) { File.Move(f[2], f[1]); back++; }
                            else if (!Directory.Exists(f[1]) && Directory.Exists(f[2])) { Directory.Move(f[2], f[1]); back++; }
                            break;
                        case "dir":
                            if (Directory.Exists(f[1]) && !Directory.EnumerateFileSystemEntries(f[1]).Any()) Directory.Delete(f[1]);
                            break;
                        case "reg":
                            Registry.CurrentUser.DeleteSubKeyTree(f[1], false);
                            break;
                    }
                }
                catch (Exception ex) { say("Could not take back " + l.Replace("\t", " ") + ": " + ex.Message); }
            }
            File.Delete(recPath);
            // a reluduali-console: link turned on later from the Designer's Setup is not in the record: it goes too when it
            // opens this install's Designer (the code review's C-22)
            try
            {
                string exe = Path.Combine(installDir, "ReludualiConsole.exe");
                string cmd;
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\reluduali-console\shell\open\command")) cmd = k == null ? null : k.GetValue("") as string;
                if (cmd != null && cmd.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0) Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\reluduali-console", false);
            }
            catch { }
            say("Removed " + files + " files; put back " + back + " that were set aside.");
            // this program and the folder go once it has exited (its window may stay open a while: the code review's C-10);
            // the folder only if it is empty by then
            if (removeSelf && string.Equals(Path.GetDirectoryName(self), installDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                string q = "'" + self.Replace("'", "''") + "'", d = "'" + installDir.TrimEnd('\\').Replace("'", "''") + "'";
                Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"Wait-Process -Id " + Process.GetCurrentProcess().Id
                                                   + " -Timeout 3600 -ErrorAction SilentlyContinue; Remove-Item -LiteralPath " + q + " -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath " + d + " -ErrorAction SilentlyContinue\"")
                              { CreateNoWindow = true, UseShellExecute = false });
            }
        }
    }
}
