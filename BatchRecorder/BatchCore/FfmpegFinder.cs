using System;
using System.IO;
using System.Linq;

namespace BatchRecorder
{
    /// <summary>
    /// Finds ffmpeg.exe on this PC by itself (2026-10-18: a fresh install had an empty ffmpeg box, and every take failed
    /// with "no ffmpeg at ''"). Where people get it: the PATH, winget (its Links folder, or the Gyan / BtbN package folders),
    /// C:\ffmpeg, Program Files, Scoop, Chocolatey, or beside the Designer. The newest winget build wins. Null when none.
    /// </summary>
    public static class FfmpegFinder
    {
        public static string Find()
        {
            try
            {
                string beside = AppDomain.CurrentDomain.BaseDirectory;
                foreach (string f in new[] { Path.Combine(beside, "ffmpeg.exe"), Path.Combine(beside, "ffmpeg", "bin", "ffmpeg.exe") })
                    if (File.Exists(f)) return f;
                foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                {
                    try { string f = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe"); if (dir.Trim().Length > 0 && File.Exists(f)) return f; } catch { }
                }
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string links = Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
                if (File.Exists(links)) return links;
                string packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(packages))
                {
                    string newest = Directory.GetDirectories(packages).Where(d => Path.GetFileName(d).IndexOf("FFmpeg", StringComparison.OrdinalIgnoreCase) >= 0)
                        .SelectMany(d => SafeFiles(d))
                        .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
                    if (newest != null) return newest;
                }
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (string f in new[]
                {
                    @"C:\ffmpeg\bin\ffmpeg.exe", Path.Combine(pf, "ffmpeg", "bin", "ffmpeg.exe"), Path.Combine(pf, "FFmpeg", "bin", "ffmpeg.exe"),
                    Path.Combine(user, "scoop", "shims", "ffmpeg.exe"), @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
                })
                    if (File.Exists(f)) return f;
            }
            catch { }
            return null;
        }

        /// <summary>A winget package's ffmpeg.exe files, at most a few folders deep (bin\ffmpeg.exe inside the build's folder).</summary>
        static string[] SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir, "ffmpeg.exe", SearchOption.AllDirectories); }
            catch { return new string[0]; }
        }

        /// <summary>The path to use: the one given when it exists, else one found here. Null when there is none.</summary>
        public static string Resolve(string given)
        {
            string g = (given ?? "").Trim().Trim('"');
            if (g.Length > 0 && File.Exists(g)) return g;
            return Find();
        }

        public const string Missing = "no ffmpeg on this PC: install it (in PowerShell: winget install Gyan.FFmpeg), or point the Batch page's ffmpeg box at ffmpeg.exe";
    }
}
