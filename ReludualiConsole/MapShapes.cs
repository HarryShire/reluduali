using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>
    /// The 3D views' map shapes, made from the user's own game (Mycelium, 2026-10-17: "Since we're leaving the maps out, make
    /// sure we make a streamlined easy process for users to get them themselves or to have the Reluduali Designer Create
    /// them itself when installed."). No game content ships; instead, while a film plays on a map, RipperBridge reads that
    /// map's world surfaces from the game's own memory and writes them as RVM2 (docs\map-shapes.md), and this gzips the file
    /// into Documents\Reluduali\maps, where the 3D views look (CamView.MeshFolders). With "each map by itself" on, the
    /// shape is made the first time a film plays on a map that has none.
    /// </summary>
    public static class MapShapes
    {
        public static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Reluduali", "maps"); } }

        /// <summary>A map's shape was written (on the thread that made it): the 3D views show it if they are waiting for it.</summary>
        public static event Action<string> Made;

        /// <summary>Whether each map's shape is made by itself, the first time a film plays on it ("mapshapes=auto").</summary>
        public static bool Auto
        {
            get { try { return Where.Saved("mapshapes") != "off"; } catch { return true; } }
            set { try { Where.Save("mapshapes", value ? "auto" : "off"); } catch { } }
        }

        /// <summary>The map the game has loaded now ("mp_raid"), or null.</summary>
        public static string LoadedMap()
        {
            // a map is loaded only in a film or a match: at the main menu, mapname still names the last one (0573)
            string ing = BridgeClient.Ask("dvar cl_ingame", 2000, 1);
            if (ing == null || ing.Trim() != "cl_ingame bool 1") return null;
            string r = BridgeClient.Ask("dvar mapname", 2000, 1);
            Match m = r == null ? Match.Empty : Regex.Match(r, "\"(mp_[A-Za-z0-9_]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Makes the shape of the map a film is playing on now (blocks: call it off the window's thread). A sentence
        /// for the person.</summary>
        public static string MakeLoaded()
        {
            try
            {
                string map = LoadedMap();
                if (map == null) return "No map is loaded: play a film first (Demos > Load in theater), then make its shape.";
                string raw = Path.Combine(AsciiTemp(), "reluduali-" + map + ".rvmesh");
                if (File.Exists(raw)) File.Delete(raw);
                string reply = BridgeClient.Ask("map shape " + raw, 3000, 1) ?? "";
                if (reply.StartsWith("error unknown command", StringComparison.Ordinal))
                    return "This RipperBridge cannot make map shapes yet: install the one beside the Designer (Setup > Install RipperBridge into the game, with the game closed).";
                if (!reply.StartsWith("ok", StringComparison.Ordinal) || !File.Exists(raw))
                    return "RipperBridge could not make " + map + "'s shape: " + (reply.Length > 0 ? reply.Replace("error ", "") : "no answer (is the game running, with RipperBridge in it?)");
                Directory.CreateDirectory(Folder);
                string to = Path.Combine(Folder, map + ".rvmesh.gz");
                string part = to + ".part";                  // whole or not at all: a reader never meets half a file
                using (var src = File.OpenRead(raw))
                using (var dst = File.Create(part))
                using (var gz = new GZipStream(dst, CompressionLevel.Optimal)) src.CopyTo(gz);
                if (File.Exists(to)) File.Delete(to);
                File.Move(part, to);
                File.Delete(raw);
                var made = Made; if (made != null) made(map);
                App.Note("map shape: " + reply + " -> " + to, null);
                string said = reply.Substring(2).Trim();                      // "mp_raid: 180412 vertices, ..."
                int colon = said.IndexOf(':');
                return "The 3D views have " + map + " now: " + (colon > 0 ? said.Substring(colon + 1).Trim() : said) + ".";
            }
            catch (Exception ex) { App.Note("making a map's shape", ex); return "Could not make the shape: " + ex.Message; }
        }

        /// <summary>A folder for the game to write into whose name the pipe can carry (it sends plain ASCII): the temp folder,
        /// or its short name, or the shared app data folder.</summary>
        static string AsciiTemp()
        {
            string dir = Path.GetTempPath();
            if (dir.All(c => c < 127)) return dir;
            var sb = new StringBuilder(520);
            if (GetShortPathName(dir, sb, sb.Capacity) > 0 && sb.ToString().All(c => c < 127)) return sb.ToString();
            string common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Reluduali");
            Directory.CreateDirectory(common);
            return common;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int GetShortPathName(string longPath, StringBuilder shortPath, int size);

        private static readonly HashSet<string> Tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The 3D views want a map there is no shape for. With "each map by itself" on, and that map loaded in the game now,
        /// make it (once a session per map; quietly, logged), then run done on the window's thread.
        /// </summary>
        public static void WhenMissing(string map, Action done)
        {
            if (!Auto || string.IsNullOrEmpty(map) || App.Tour) return;
            lock (Tried) { if (!Tried.Add(map)) return; }
            Dispatcher ui = Dispatcher.CurrentDispatcher;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (System.Diagnostics.Process.GetProcessesByName("t6mpv43").Length == 0 || !string.Equals(LoadedMap(), map, StringComparison.OrdinalIgnoreCase))
                    {
                        lock (Tried) Tried.Remove(map);             // not on this map now: the next time it is
                        return;
                    }
                    App.Note("map shape, by itself: " + MakeLoaded(), null);
                    if (CamView.MeshFile(map) != null && done != null) ui.BeginInvoke(done);
                }
                catch (Exception ex) { App.Note("making a map's shape by itself", ex); }
            });
        }

        /// <summary>The maps the 3D views have, wherever they are.</summary>
        public static List<string> Have()
        {
            var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string d in CamView.MeshFolders())
                try { if (Directory.Exists(d)) foreach (string f in Directory.GetFiles(d, "*.rvmesh.gz")) found.Add(Path.GetFileName(f).Replace(".rvmesh.gz", "")); }
                catch { }
            return found.ToList();
        }

        /// <summary>Setup's card: make the loaded map's shape, the by-itself switch, and the maps there are.</summary>
        public static Border Card()
        {
            var list = Ui.Note("", 4, 0);
            Action refresh = () =>
            {
                List<string> have = Have();
                list.Text = have.Count == 0 ? "No map shapes yet." : "Maps the 3D views have (" + have.Count + "): " + string.Join(", ", have);
            };
            var status = Ui.Note("", 2, 0);
            Button make = null;
            make = Ui.Primary(Ui.Button("Make this map's shape", () =>
            {
                make.IsEnabled = false;
                status.Text = "Reading the map from the game...";
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    string said = MakeLoaded();
                    make.Dispatcher.BeginInvoke(new Action(() => { status.Text = said; make.IsEnabled = true; refresh(); }));
                });
            }, "While a film plays: read this map's shape from the game, for the 3D views"));
            System.Windows.Automation.AutomationProperties.SetAutomationId(make, "MapShape_Make");
            var auto = new CheckBox { Content = "Each map by itself: when a 3D view needs a map the game has loaded", IsChecked = Auto, Margin = new Thickness(0, 6, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(auto, "MapShape_Auto");
            auto.Checked += (s, e) => Auto = true;
            auto.Unchecked += (s, e) => Auto = false;
            refresh();
            return Ui.Card("3D view maps", "The Cam Queue's 3D views draw the map around your camera path. The Designer makes each map's shape " +
                           "from your own game (nothing is downloaded): play a film on the map, then make its shape. They are kept in " +
                           "Documents\\Reluduali\\maps.",
                           Ui.Row(make), auto, status, list);
        }
    }
}
