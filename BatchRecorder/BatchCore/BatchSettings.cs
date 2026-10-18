using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BatchRecorder
{
    /// <summary>
    /// The batch tab's fields, kept between sessions (Mycelium, 2026-10-01). Before
    /// this, every start brought back the built-in paths, dry run on and the first
    /// codec.
    ///
    /// One "key=value" line per field, beside this DLL. Written to a temporary file
    /// and then swapped in, so a crash mid-write leaves the previous file whole: a
    /// file written in place can come back cut short.
    /// </summary>
    public static class BatchSettings
    {
        public const string FileName = "BatchRecorder.settings.txt";

        public static string LastError = "";

        public static string PathOnDisk
        {
            get
            {
                string dir = null;
                try { dir = Path.GetDirectoryName(typeof(BatchSettings).Assembly.Location); }
                catch { }
                if (string.IsNullOrEmpty(dir)) dir = AppDomain.CurrentDomain.BaseDirectory;
                return Path.Combine(dir, FileName);
            }
        }

        public static Dictionary<string, string> Load()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path = PathOnDisk;
            try
            {
                if (!File.Exists(path)) return values;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1);
                }
                LastError = "";
            }
            catch (Exception ex) { LastError = ex.Message; }
            return values;
        }

        public static bool Save(IDictionary<string, string> values)
        {
            string path = PathOnDisk, tmp = path + ".tmp";
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Batch Recorder settings, saved " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                foreach (KeyValuePair<string, string> kv in values)
                    sb.Append(kv.Key).Append('=').AppendLine((kv.Value ?? "").Replace("\r", " ").Replace("\n", " "));

                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                LastError = "";
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }
    }
}
