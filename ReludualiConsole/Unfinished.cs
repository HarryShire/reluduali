using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ReludualiConsole
{
    /// <summary>
    /// Which build this is, and what it keeps quiet (2026-10-18): every button whose feature is not started or not working
    /// yet is muted in "the Test Build".
    ///
    /// The Test Build is the installer's -TestBuild: it puts build.txt ("test") beside the Designer. There, every feature
    /// named in Muted is greyed, and says why on hover. A feature whose own files an install does not carry (a tool folder,
    /// a converter) is greyed in every build, by Needs: a button that cannot work never looks as if it could.
    /// </summary>
    public static class Unfinished
    {
        static readonly Lazy<string> _channel = new Lazy<string>(() =>
        {
            try
            {
                string f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "build.txt");
                if (File.Exists(f)) { string t = File.ReadAllText(f).Trim().ToLowerInvariant(); if (t.Length > 0) return t; }
            }
            catch { }
            return "release";
        });

        /// <summary>"test" in the Test Build, "release" otherwise.</summary>
        public static string Channel { get { return _channel.Value; } }
        public static bool IsTest { get { return Channel == "test"; } }

        public const string Edition = "public";

        /// <summary>What the Test Build keeps quiet, and why, by a key the page passes to Mute.</summary>
        static string Why(string key)
        {
            switch (key)
            {
                case "camqueue.players": return "The players' 3D pass has not run in a real game yet.";
                case "share.camo": return "Sharing camo designs waits for the Camo Editor inside the Designer.";
                default: return null;
            }
        }

        /// <summary>
        /// A whole page that cannot work on this PC: the file its tool runs is not here (an install carries bo2map only, not
        /// the project's other tools). Null when the page can work. Every build, not only the Test Build.
        /// </summary>
        public static string ToolWhy(string toolKey)
        {
            string need = null, what = null;
            switch (toolKey)
            {
                case "tap": need = @"tapviewer\tap-viewer.ps1"; what = "Tap Markers"; break;
                case "scene": need = @"mapimport\scene_cli.py"; what = "Scene"; break;
                case "models": need = @"mapimport\bodies_cli.py"; what = "Models"; break;
            }
            if (need == null) return null;
            string f = Path.Combine(Where.Tools, need);
            return File.Exists(f) ? null : what + " needs the project's " + need + ", which this install does not carry yet.";
        }

        /// <summary>Greys an element in the Test Build when its key is one of the unfinished ones. Answers whether it did.</summary>
        public static bool Mute(UIElement e, string key)
        {
            string why = IsTest ? Why(key) : null;
            if (why == null || e == null) return false;
            Grey(e, "Not in the Test Build: " + why);
            return true;
        }

        /// <summary>Greys an element in every build when a file it runs is not on this PC. Answers whether it did.</summary>
        public static bool Needs(UIElement e, string file, string what)
        {
            if (e == null || string.IsNullOrEmpty(file) || File.Exists(file)) return false;
            Grey(e, what + " needs " + Path.GetFileName(file) + ", which this install does not carry.");
            return true;
        }

        public static void Grey(UIElement e, string why)
        {
            e.IsEnabled = false;
            var fe = e as FrameworkElement;
            if (fe != null)
            {
                fe.ToolTip = why;
                ToolTipService.SetShowOnDisabled(fe, true);
            }
        }
    }
}
