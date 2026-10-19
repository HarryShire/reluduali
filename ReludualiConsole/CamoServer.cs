using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>
    /// The Camo Editor inside the Designer (2026-10-18; Mycelium's 10-18 test take, 38:49: he wanted to paint a gun and could not,
    /// "the Camo Editor isn't in the Designer, and what it needs isn't installed"). The editor is the page made on 10-17
    /// (tools\camo-editor\app, MIT); its server was a Python script (serve.py + models.py). This is that server in the
    /// Designer itself, so nothing else has to be installed: the page is served on http://127.0.0.1:&lt;port&gt;/ (this PC
    /// only) and opened in the PC's own browser.
    ///
    /// The same API as serve.py (so the page is unchanged): /api/file, /api/save, /api/autosave, /api/package, /api/guns,
    /// /api/patterns, /api/thumb, /api/models, /api/mesh; and one serve.py never had, /api/swap: a saved .dds into the
    /// running game by RipperBridge's "img swap". A write must carry "x-camo: 1" (a page from another site cannot add it
    /// without asking first, and this server never answers that question), and the Host must be 127.0.0.1 or localhost.
    ///
    /// The gun models come from OpenAssetTools' Unlinker dumps of the game's zones (the MODELS tab's), read only. Without
    /// them the editor still paints and saves the flat picture: no UV layout, no 3D.
    /// </summary>
    public sealed class CamoServer
    {
        static CamoServer _running;
        static readonly object Gate = new object();

        readonly TcpListener _listen;
        public readonly int Port;
        public readonly string App, Out;
        /// <summary>The model folder, read each time (models made while the editor is open show at once).</summary>
        public string Dumps { get { return DumpsFolder; } }
        readonly Dictionary<string, byte[]> _meshes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        readonly object _lock = new object();

        static readonly HashSet<string> Readable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".iwi", ".dds", ".png", ".jpg", ".jpeg", ".webp", ".psd" };
        static readonly HashSet<string> Writable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dds", "iwi", "psd", "png" };
        static readonly Regex Name = new Regex(@"^[A-Za-z0-9~&_.\-]{1,120}$"), Stamp = new Regex(@"^[0-9]{8}-[0-9]{6}$"),
                              PackageFile = new Regex(@"^(manifest\.json|layers\.psd|preview\.png|turn-[1-9]\.png)$"),
                              Material = new Regex(@"^MATERIAL \d+ ""([^""]*)"" ""[^""]*"" ""([^""]*)""");
        static readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".html", "text/html; charset=utf-8" }, { ".js", "text/javascript; charset=utf-8" }, { ".mjs", "text/javascript; charset=utf-8" },
            { ".css", "text/css; charset=utf-8" }, { ".svg", "image/svg+xml" }, { ".png", "image/png" }
        };

        /// <summary>The editor's page: beside the Designer (tools\camo-editor\app), else the repository's.</summary>
        public static string AppFolder { get { return Path.Combine(Where.Tools, @"camo-editor\app"); } }

        /// <summary>Where saves go: Documents\Reluduali\camo (never inside the game's folder).</summary>
        public static string OutFolder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Reluduali", "camo"); } }

        /// <summary>The model dumps: the folder chosen (camo-dumps), else the MODELS tab's.</summary>
        public static string DumpsFolder
        {
            get
            {
                string s = Where.Saved("camodumps");
                if (!string.IsNullOrEmpty(s)) return s;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reluduali", "models", "oat", "v0.33.0");
            }
        }

        CamoServer(int port)
        {
            App = AppFolder; Out = OutFolder;
            _listen = new TcpListener(IPAddress.Loopback, port);
            _listen.Start();
            Port = ((IPEndPoint)_listen.LocalEndpoint).Port;
            new Thread(Accept) { IsBackground = true, Name = "camo editor" }.Start();
        }

        /// <summary>The editor's address, the server started if it was not (8770, or the next free port).</summary>
        public static string Url(out string why)
        {
            why = null;
            lock (Gate)
            {
                if (_running == null)
                {
                    if (!File.Exists(Path.Combine(AppFolder, "index.html"))) { why = "the editor's page is not beside the Designer (" + AppFolder + ")"; return null; }
                    for (int p = 8770; p < 8790 && _running == null; p++)
                    {
                        try { _running = new CamoServer(p); } catch (SocketException) { }
                    }
                    if (_running == null) { why = "no free port for it (8770-8789)"; return null; }
                }
                return "http://127.0.0.1:" + _running.Port + "/";
            }
        }

        /// <summary>The editor opened in the PC's browser, with a picture if one is given (CamoEditorControl.Open shows it in
        /// the Designer instead when WebView2 is on the PC).</summary>
        public static string OpenInBrowser(string picture)
        {
            string why, url = Url(out why);
            if (url == null) return "The Camo Editor could not start: " + why + ".";
            if (!string.IsNullOrEmpty(picture)) url += "?open=" + Uri.EscapeDataString(picture);
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { return "Could not open the browser (" + ex.Message + "): the editor is at " + url; }
            return "The Camo Editor is open in your browser (" + url.Split('?')[0] + ")" + (string.IsNullOrEmpty(picture) ? "" : " with " + Path.GetFileName(picture))
                   + ". Save, then Send to the game puts it in the running game.";
        }

        // ---------------------------------------------------------------- HTTP, the little that a page needs

        void Accept()
        {
            while (true)
            {
                TcpClient c;
                try { c = _listen.AcceptTcpClient(); } catch { return; }
                ThreadPool.QueueUserWorkItem(_ => { try { Serve(c); } catch { } finally { try { c.Close(); } catch { } } });
            }
        }

        sealed class Req { public string Method, Path, Query; public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); public byte[] Body = new byte[0]; public Dictionary<string, string> Q; }

        static string ReadLine(Stream s)
        {
            var b = new List<byte>();
            int x;
            while ((x = s.ReadByte()) >= 0) { if (x == '\n') break; if (x != '\r') b.Add((byte)x); if (b.Count > 16384) break; }
            return x < 0 && b.Count == 0 ? null : Encoding.ASCII.GetString(b.ToArray());
        }

        void Serve(TcpClient c)
        {
            c.ReceiveTimeout = 15000; c.SendTimeout = 60000;
            Stream s = c.GetStream();
            string first = ReadLine(s);
            if (string.IsNullOrEmpty(first)) return;
            string[] p = first.Split(' ');
            if (p.Length < 2) return;
            var r = new Req { Method = p[0].ToUpperInvariant() };
            string target = p[1];
            int qm = target.IndexOf('?');
            r.Path = Uri.UnescapeDataString(qm < 0 ? target : target.Substring(0, qm));
            r.Query = qm < 0 ? "" : target.Substring(qm + 1);
            r.Q = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string kv in r.Query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = kv.IndexOf('=');
                string k = Uri.UnescapeDataString((eq < 0 ? kv : kv.Substring(0, eq)).Replace('+', ' ')), v = eq < 0 ? "" : Uri.UnescapeDataString(kv.Substring(eq + 1).Replace('+', ' '));
                if (!r.Q.ContainsKey(k)) r.Q[k] = v;
            }
            string h;
            while (!string.IsNullOrEmpty(h = ReadLine(s)))
            {
                int colon = h.IndexOf(':');
                if (colon > 0) r.Headers[h.Substring(0, colon).Trim()] = h.Substring(colon + 1).Trim();
            }
            string len;
            long n = 0;
            if (r.Headers.TryGetValue("Content-Length", out len) && long.TryParse(len, out n) && n > 0)
            {
                if (n > 512L * 1024 * 1024) { Json(s, new { error = "more than 512 MB" }, 400); return; }
                r.Body = new byte[n];
                int got = 0;
                while (got < n) { int k = s.Read(r.Body, got, (int)Math.Min(n - got, 1 << 20)); if (k <= 0) break; got += k; }
                if (got < n) return;
            }
            string host;
            r.Headers.TryGetValue("Host", out host);
            host = (host ?? "").Split(':')[0].ToLowerInvariant();
            if (host != "127.0.0.1" && host != "localhost") { Json(s, new { error = "refused: asked for by another name (" + host + ")" }, 403); return; }
            try { Route(s, r); }
            catch (Exception ex) { Json(s, new { error = ex.Message }, 500); }
        }

        static void Send(Stream s, int code, string type, byte[] body)
        {
            string head = "HTTP/1.1 " + code + " " + (code == 200 ? "OK" : code == 404 ? "Not Found" : code == 403 ? "Forbidden" : code == 409 ? "Conflict" : code == 400 ? "Bad Request" : code == 501 ? "Not Implemented" : "Error") + "\r\n"
                          + "Content-Type: " + type + "\r\nContent-Length: " + body.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(head);
            s.Write(hb, 0, hb.Length);
            s.Write(body, 0, body.Length);
            s.Flush();
        }

        static void Json(Stream s, object o, int code = 200)
        {
            Send(s, code, "application/json", Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(o)));
        }

        string Q(Req r, string k) { string v; return r.Q.TryGetValue(k, out v) ? v : ""; }

        static bool Inside(string path, string folder)
        {
            try { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        void Route(Stream s, Req r)
        {
            if (r.Method == "POST")
            {
                string camo;
                if (!r.Headers.TryGetValue("x-camo", out camo) || camo != "1") { Json(s, new { error = "refused: not the editor's own page" }, 403); return; }
                switch (r.Path)
                {
                    case "/api/save": SaveFile(s, r); return;
                    case "/api/autosave": Autosave(s, r); return;
                    case "/api/package": Package(s, r); return;
                    case "/api/swap": Swap(s, r); return;
                    default: Json(s, new { error = "not found" }, 404); return;
                }
            }
            switch (r.Path)
            {
                case "/api/file":
                    {
                        string path = Q(r, "path").Trim().Trim('"');
                        if (!Readable.Contains(Path.GetExtension(path))) { Json(s, new { error = "not a picture this editor opens (.iwi .dds .png .jpg .webp .psd)" }, 400); return; }
                        if (!File.Exists(path)) { Json(s, new { error = "no such file" }, 404); return; }
                        Send(s, 200, "application/octet-stream", File.ReadAllBytes(path));
                        return;
                    }
                case "/api/models":
                    {
                        List<string> found;
                        lock (_lock) { Dictionary<string, List<string>> idx = Index(); idx.TryGetValue(Q(r, "image").ToLowerInvariant(), out found); }
                        Json(s, new { dumps = Dumps, models = (found ?? new List<string>()).Select(f => new { file = f, name = Path.GetFileName(f).Substring(0, Path.GetFileName(f).Length - 14) }).ToList() });
                        return;
                    }
                case "/api/mesh":
                    {
                        string f = Path.GetFullPath(Q(r, "file"));
                        if (!f.EndsWith(".xmodel_export", StringComparison.OrdinalIgnoreCase) || !Inside(f, Dumps) || !File.Exists(f)) { Json(s, new { error = "not a model in the dumps (" + Dumps + ")" }, 400); return; }
                        byte[] b;
                        lock (_lock) { if (!_meshes.TryGetValue(f, out b)) _meshes[f] = b = Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(Mesh(f))); }
                        Send(s, 200, "application/json", b);
                        return;
                    }
                case "/api/guns": Guns(s); return;
                case "/api/patterns": Patterns(s); return;
                case "/api/thumb": Thumb(s, r); return;
                case "/api/autosave":
                    {
                        string image = Q(r, "image");
                        if (!Name.IsMatch(image) || image.Contains("..")) { Json(s, new { error = "not an image name" }, 400); return; }
                        string meta = Path.Combine(Out, image, "autosave", "autosave.json");
                        if (!File.Exists(meta)) { Json(s, new { found = false }); return; }
                        Dictionary<string, object> m;
                        try { m = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(meta)); }
                        catch { Json(s, new { found = false }); return; }
                        m["found"] = true; m["psd"] = Path.Combine(Path.GetDirectoryName(meta), image + ".psd");
                        Json(s, m);
                        return;
                    }
            }
            string rel = r.Path == "/" || r.Path == "" ? "index.html" : r.Path.TrimStart('/').Replace('/', '\\');
            string full = Path.GetFullPath(Path.Combine(App, rel));
            if (!Inside(full, App) || !File.Exists(full)) { Json(s, new { error = "not found" }, 404); return; }
            string type;
            if (!Types.TryGetValue(Path.GetExtension(full), out type)) type = "application/octet-stream";
            Send(s, 200, type, File.ReadAllBytes(full));
        }

        // ---------------------------------------------------------------- writes: never over a file, but the autosave

        void SaveFile(Stream s, Req r)
        {
            string image = Q(r, "image"), stamp = Q(r, "stamp"), ext = Q(r, "ext").ToLowerInvariant();
            if (!Name.IsMatch(image) || image.Trim('.') != image || image.Contains("..")) { Json(s, new { error = "an image name is letters, digits and ~ - _ & . only" }, 400); return; }
            if (!Stamp.IsMatch(stamp) || !Writable.Contains(ext)) { Json(s, new { error = "a save needs a stamp (YYYYMMDD-HHMMSS) and dds, iwi, psd or png" }, 400); return; }
            if (r.Body.Length == 0) { Json(s, new { error = "nothing to write" }, 400); return; }
            string folder = Path.Combine(Out, image, stamp), path = Path.Combine(folder, image + "." + ext);
            WriteNew(s, folder, path, r.Body);
        }

        void Package(Stream s, Req r)
        {
            string image = Q(r, "image"), stamp = Q(r, "stamp"), name = Q(r, "file");
            if (!Name.IsMatch(image) || image.Trim('.') != image || image.Contains("..") || !Stamp.IsMatch(stamp) || !PackageFile.IsMatch(name))
            { Json(s, new { error = "a package file is manifest.json, layers.psd, preview.png or turn-N.png" }, 400); return; }
            if (r.Body.Length == 0 || r.Body.Length > 256 * 1024 * 1024) { Json(s, new { error = "nothing to write, or more than 256 MB" }, 400); return; }
            string folder = Path.Combine(Out, image, stamp + "-package");
            WriteNew(s, folder, Path.Combine(folder, name), r.Body);
        }

        static void WriteNew(Stream s, string folder, string path, byte[] body)
        {
            try
            {
                Directory.CreateDirectory(folder);
                using (var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) f.Write(body, 0, body.Length);     // never over a file that is there
            }
            catch (IOException) when (File.Exists(path)) { Json(s, new { error = path + " is there already: nothing was replaced" }, 409); return; }
            catch (Exception ex) { Json(s, new { error = "it could not be written: " + ex.Message }, 500); return; }
            Json(s, new { ok = true, path = path, bytes = body.Length });
        }

        void Autosave(Stream s, Req r)
        {
            string image = Q(r, "image"), name = Q(r, "file");
            if (!Name.IsMatch(image) || image.Contains("..") || (name != "psd" && name != "json")) { Json(s, new { error = "an autosave is the image's .psd and its autosave.json" }, 400); return; }
            if (r.Body.Length == 0) { Json(s, new { error = "nothing to write" }, 400); return; }
            string folder = Path.Combine(Out, image, "autosave"), path = Path.Combine(folder, name == "psd" ? image + ".psd" : "autosave.json");
            Directory.CreateDirectory(folder);
            string tmp = path + ".part";
            File.WriteAllBytes(tmp, r.Body);
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);      // whole or not at all
            Json(s, new { ok = true, path = path });
        }

        /// <summary>A saved .dds into the running game: RipperBridge's "img swap &lt;image&gt;|&lt;file&gt;" (dev28+).</summary>
        void Swap(Stream s, Req r)
        {
            string image = Q(r, "image"), path = Q(r, "path");
            if (!Name.IsMatch(image) || image.Contains("..")) { Json(s, new { error = "not an image name" }, 400); return; }
            if (!path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) { Json(s, new { error = "no saved .dds at " + path }, 400); return; }
            string reply = BridgeClient.Ask("img swap " + image + "|" + path, 120000, 1);
            if (reply == null) { Json(s, new { error = "the game did not answer (is it running with RipperBridge?): " + BridgeClient.LastError }, 502); return; }
            if (!reply.StartsWith("ok", StringComparison.Ordinal)) { Json(s, new { error = reply }, 409); return; }
            Json(s, new { ok = true, said = "In the game: " + image + " (" + reply + "). Guns > Back to the game's puts the game's own back." });
        }

        // ---------------------------------------------------------------- the library: guns, patterns, pictures

        void Guns(Stream s)
        {
            Dictionary<string, List<string>> idx;
            lock (_lock) idx = Index();
            var guns = new List<Dictionary<string, object>>();
            foreach (var kv in idx)
            {
                string image = kv.Key;
                List<string> view = kv.Value.Where(f => Path.GetFileName(f).StartsWith("t6_wpn_", StringComparison.OrdinalIgnoreCase) && f.IndexOf("_view", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (view.Count == 0 || !image.EndsWith("_col") || !image.Contains("_wpn_") || image.Contains("attach")) continue;
                string model = Path.GetFileName(view[0]); model = model.Substring(0, model.Length - ".xmodel_export".Length);
                string dds = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(view[0])), "images", image + ".dds");
                if (!File.Exists(dds)) continue;
                string name = Regex.Replace(image, "^.*_wpn_|_col$", "").Replace("_", " ");
                guns.Add(new Dictionary<string, object> { { "image", image }, { "name", name }, { "model", model }, { "file", dds } });
            }
            Json(s, new { guns = guns.OrderBy(g => (string)g["name"]).ThenBy(g => (string)g["image"]).ToList() });
        }

        void Patterns(Stream s)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<object>();
            if (Directory.Exists(Dumps))
                foreach (string z in Directory.GetDirectories(Dumps).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    string d = Path.Combine(z, "images");
                    if (!Directory.Exists(d)) continue;
                    foreach (string f in Directory.GetFiles(d, "*.dds").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        string low = Path.GetFileName(f).ToLowerInvariant();
                        if (!low.Contains("camo") || Regex.IsMatch(low, "_(nml|spc|spec|gloss|reveal|heat|glow)")) continue;
                        string n = Path.GetFileNameWithoutExtension(f);
                        if (seen.Add(n)) list.Add(new { name = n, file = f });
                    }
                }
            Json(s, new { patterns = list });
        }

        void Thumb(Stream s, Req r)
        {
            string f = Path.GetFullPath(Q(r, "file"));
            if (!f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) || !Inside(f, Dumps) || !File.Exists(f)) { Json(s, new { error = "not an image in the dumps" }, 400); return; }
            string key;
            using (var sha = SHA1.Create()) key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(f.ToLowerInvariant()))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            string cache = Path.Combine(Out, "thumbs", key + ".png");
            if (!File.Exists(cache))
            {
                byte[] png = Dds.Thumbnail(File.ReadAllBytes(f), 192);
                if (png == null) { Json(s, new { error = "no picture (a DDS kind this reader does not take)" }, 501); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(cache));
                File.WriteAllBytes(cache, png);
            }
            Send(s, 200, "image/png", File.ReadAllBytes(cache));
        }

        // ---------------------------------------------------------------- the models (models.py)

        static string ImageOf(string colourPath)
        {
            string b = colourPath.Replace('\\', '/');
            b = b.Substring(b.LastIndexOf('/') + 1);
            return b.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? b.Substring(0, b.Length - 4) : b;
        }

        /// <summary>A model's (material, colour image) pairs, from its last 64 KB, where they are.</summary>
        static List<Tuple<string, string>> Materials(string path)
        {
            var o = new List<Tuple<string, string>>();
            string tail;
            using (var f = File.OpenRead(path))
            {
                long from = Math.Max(0, f.Length - 64 * 1024);
                f.Seek(from, SeekOrigin.Begin);
                var b = new byte[f.Length - from];
                int got = 0; while (got < b.Length) { int k = f.Read(b, got, b.Length - got); if (k <= 0) break; got += k; }
                tail = Encoding.UTF8.GetString(b, 0, got);
            }
            foreach (string line in tail.Split('\n'))
            {
                Match m = Material.Match(line.Trim());
                if (!m.Success) continue;
                string img = m.Groups[2].Value; int c = img.IndexOf(':');
                o.Add(Tuple.Create(m.Groups[1].Value, ImageOf(c >= 0 ? img.Substring(c + 1) : img)));
            }
            return o;
        }

        /// <summary>{image: [model files]}: view models first, then world models, the gun before its attachments, the finest
        /// LOD first, the shortest name first. Kept in Out\models-index.json, made again for a zone whose dump changed.</summary>
        Dictionary<string, List<string>> Index()
        {
            string path = Path.Combine(Out, "models-index.json");
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> idx = null;
            try { idx = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(path)); } catch { }
            var zonesIdx = idx != null && idx.ContainsKey("zones") ? (Dictionary<string, object>)idx["zones"] : new Dictionary<string, object>();
            bool changed = false;
            var zones = Directory.Exists(Dumps) ? Directory.GetDirectories(Dumps).Where(d => Directory.Exists(Path.Combine(d, "model_export"))).Select(Path.GetFileName).OrderBy(x => x).ToList() : new List<string>();
            foreach (string z in zones)
            {
                string folder = Path.Combine(Dumps, z, "model_export");
                string stamp = Directory.GetLastWriteTimeUtc(folder).Ticks.ToString(CultureInfo.InvariantCulture);
                object zo;
                if (zonesIdx.TryGetValue(z, out zo) && zo is Dictionary<string, object> && (string)((Dictionary<string, object>)zo)["stamp"] == stamp) continue;
                var models = new Dictionary<string, object>();
                foreach (string f in Directory.GetFiles(folder, "*.xmodel_export"))
                    models[Path.GetFileName(f)] = Materials(f).Select(t => t.Item2).Where(i => i.Length > 0).Distinct().OrderBy(i => i).ToArray();
                zonesIdx[z] = new Dictionary<string, object> { { "stamp", stamp }, { "models", models } };
                changed = true;
            }
            if (changed)
            {
                Directory.CreateDirectory(Out);
                File.WriteAllText(path, js.Serialize(new Dictionary<string, object> { { "zones", zonesIdx } }));
            }
            var by = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in zonesIdx)
            {
                if (!zones.Contains(kv.Key)) continue;
                var models = (Dictionary<string, object>)((Dictionary<string, object>)kv.Value)["models"];
                foreach (var m in models)
                    foreach (object img in (System.Collections.IEnumerable)m.Value)
                    {
                        string key = img.ToString().ToLowerInvariant();
                        List<string> l;
                        if (!by.TryGetValue(key, out l)) by[key] = l = new List<string>();
                        l.Add(Path.Combine(Dumps, kv.Key, "model_export", m.Key));
                    }
            }
            Func<string, string> rank = p =>
            {
                string n = Path.GetFileName(p).ToLowerInvariant();
                Match lod = Regex.Match(n, @"_lod(\d+)");
                return (n.Contains("_view") ? 0 : n.Contains("_world") ? 1 : 2) + "|" + (n.Contains("_attach_") ? 1 : 0) + "|" + (lod.Success ? lod.Groups[1].Value.PadLeft(2, '0') : "09") + "|" + n.Length.ToString("000") + "|" + n;
            };
            return by.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(rank, StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);
        }

        static readonly Dictionary<string, string> MapRoles = new Dictionary<string, string> { { "normalMap", "normal" }, { "specularMap", "spec" }, { "occlusionMap", "ao" } };

        static Dictionary<string, string> MapsOf(string material, string zoneDir)
        {
            var o = new Dictionary<string, string>();
            try
            {
                var doc = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(zoneDir, "materials", material.Replace('/', '\\') + ".json")));
                object t;
                if (!doc.TryGetValue("textures", out t)) return o;
                foreach (object x in (System.Collections.ArrayList)t)
                {
                    var d = (Dictionary<string, object>)x;
                    string role; object sem, img;
                    if (!d.TryGetValue("semantic", out sem) || sem == null || !MapRoles.TryGetValue(sem.ToString(), out role)) continue;
                    d.TryGetValue("image", out img);
                    string dds = Path.Combine(zoneDir, "images", (img ?? "") + ".dds");
                    if (File.Exists(dds)) o[role] = dds;
                }
            }
            catch { }
            return o;
        }

        static double[] Floats(string s) { return s.Replace(',', ' ').Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray(); }

        /// <summary>A model's triangles grouped by the colour image their material draws (models.mesh): flat lists, three
        /// corners a triangle; the UVs as the file has them (V down the picture, as its rows).</summary>
        static Dictionary<string, object> Mesh(string path)
        {
            string[] lines = File.ReadAllLines(path).Select(l => l.Trim()).ToArray();
            var verts = new List<double[]>();
            var mats = Materials(path);
            string zoneDir = Path.GetDirectoryName(Path.GetDirectoryName(path)), imagesDir = Path.Combine(zoneDir, "images");
            var parts = new Dictionary<string, Dictionary<string, object>>();
            var order = new List<string>();
            bool tris = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i];
                if (!tris && s.StartsWith("VERT ") && i + 1 < lines.Length && lines[i + 1].StartsWith("OFFSET"))
                {
                    verts.Add(Floats(lines[i + 1].Substring(6)));
                    int nb = int.Parse(lines[i + 2].Split(' ')[1], CultureInfo.InvariantCulture);
                    i += 2 + nb;
                }
                else if (s.StartsWith("TRI "))
                {
                    tris = true;
                    string[] p = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    int mat = int.Parse(p[2], CultureInfo.InvariantCulture);
                    string name = mat < mats.Count ? mats[mat].Item1 : "", img = mat < mats.Count ? mats[mat].Item2 : "";
                    string key = name + "|" + img;
                    Dictionary<string, object> part;
                    if (!parts.TryGetValue(key, out part))
                    {
                        string dds = img.Length > 0 ? Path.Combine(imagesDir, img + ".dds") : "";
                        parts[key] = part = new Dictionary<string, object>
                        {
                            { "image", img }, { "material", name }, { "file", dds.Length > 0 && File.Exists(dds) ? dds : "" }, { "maps", MapsOf(name, zoneDir) },
                            { "position", new List<double>() }, { "normal", new List<double>() }, { "uv", new List<double>() }
                        };
                        order.Add(key);
                    }
                    var pos = (List<double>)part["position"]; var nrm = (List<double>)part["normal"]; var uv = (List<double>)part["uv"];
                    for (int k = 0; k < 3; k++)
                    {
                        int j = i + 1 + 4 * k;
                        int v = int.Parse(lines[j].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
                        double[] n = Floats(lines[j + 1].Substring(6));
                        string[] u = lines[j + 3].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (double c in verts[v]) pos.Add(Math.Round(c, 4));
                        foreach (double c in n) nrm.Add(Math.Round(c, 4));
                        uv.Add(Math.Round(double.Parse(u[2], CultureInfo.InvariantCulture), 6)); uv.Add(Math.Round(double.Parse(u[3], CultureInfo.InvariantCulture), 6));
                    }
                    i += 12;
                }
            }
            string model = Path.GetFileName(path); model = model.Substring(0, model.Length - ".xmodel_export".Length);
            return new Dictionary<string, object> { { "model", model }, { "parts", order.Select(k => parts[k]).ToList() } };
        }
    }

    /// <summary>A DDS read for a small picture (the welcome screen's guns, the pattern library): DXT1/3/5, BC4/5 and plain
    /// 32-bit, the top mip only, scaled down, as a PNG.</summary>
    static class Dds
    {
        public static byte[] Thumbnail(byte[] b, int max)
        {
            if (b.Length < 128 || b[0] != 'D' || b[1] != 'D' || b[2] != 'S' || b[3] != ' ') return null;
            int h = BitConverter.ToInt32(b, 12), w = BitConverter.ToInt32(b, 16);
            int pfFlags = BitConverter.ToInt32(b, 80);
            string four = Encoding.ASCII.GetString(b, 84, 4);
            int bits = BitConverter.ToInt32(b, 88);
            int off = 128;
            if (four == "DX10") { int fmt = BitConverter.ToInt32(b, 128); off = 148; four = fmt == 71 || fmt == 72 ? "DXT1" : fmt == 74 || fmt == 75 ? "DXT3" : fmt == 77 || fmt == 78 ? "DXT5" : fmt == 80 ? "ATI1" : fmt == 83 ? "ATI2" : fmt == 28 ? "RGBA" : fmt == 87 ? "BGRA" : "?"; }
            if (w <= 0 || h <= 0 || w > 16384 || h > 16384) return null;
            var rgba = new byte[w * h * 4];
            try
            {
                if (four == "DXT1" || four == "DXT3" || four == "DXT5" || four == "ATI1" || four == "BC4U" || four == "ATI2" || four == "BC5U") Blocks(b, off, w, h, four, rgba);
                else if ((pfFlags & 0x40) != 0 && bits == 32 || four == "RGBA" || four == "BGRA")
                {
                    bool bgra = four == "BGRA" || (four != "RGBA" && BitConverter.ToUInt32(b, 92) == 0x00FF0000);
                    for (int i = 0; i < w * h; i++)
                    {
                        int s = off + i * 4;
                        rgba[i * 4] = b[s + (bgra ? 2 : 0)]; rgba[i * 4 + 1] = b[s + 1]; rgba[i * 4 + 2] = b[s + (bgra ? 0 : 2)]; rgba[i * 4 + 3] = 255;
                    }
                }
                else return null;
            }
            catch (IndexOutOfRangeException) { return null; }
            double k = Math.Min(1.0, max / (double)Math.Max(w, h));
            int tw = Math.Max(1, (int)(w * k)), th = Math.Max(1, (int)(h * k));
            var px = new byte[tw * th * 4];
            for (int y = 0; y < th; y++)
                for (int x = 0; x < tw; x++)
                {
                    int sx = Math.Min(w - 1, (int)(x / k)), sy = Math.Min(h - 1, (int)(y / k));
                    int si = (sy * w + sx) * 4, di = (y * tw + x) * 4;
                    px[di] = rgba[si + 2]; px[di + 1] = rgba[si + 1]; px[di + 2] = rgba[si]; px[di + 3] = 255;      // BGRA for WPF
                }
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(tw, th, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, tw * 4);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var ms = new MemoryStream()) { enc.Save(ms); return ms.ToArray(); }
        }

        static void Blocks(byte[] b, int off, int w, int h, string four, byte[] rgba)
        {
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            int size = four == "DXT1" || four == "ATI1" || four == "BC4U" ? 8 : 16;
            var c = new byte[16 * 4];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    int p = off + (by * bw + bx) * size;
                    if (four == "ATI1" || four == "BC4U") { var a = Alpha(b, p); for (int i = 0; i < 16; i++) { c[i * 4] = c[i * 4 + 1] = c[i * 4 + 2] = a[i]; c[i * 4 + 3] = 255; } }
                    else if (four == "ATI2" || four == "BC5U") { var r = Alpha(b, p); var g = Alpha(b, p + 8); for (int i = 0; i < 16; i++) { c[i * 4] = r[i]; c[i * 4 + 1] = g[i]; c[i * 4 + 2] = 255; c[i * 4 + 3] = 255; } }
                    else
                    {
                        Colour(b, four == "DXT1" ? p : p + 8, four == "DXT1", c);
                        if (four == "DXT5") { var a = Alpha(b, p); for (int i = 0; i < 16; i++) c[i * 4 + 3] = a[i]; }
                        else if (four == "DXT3") for (int i = 0; i < 16; i++) c[i * 4 + 3] = (byte)(((b[p + i / 2] >> ((i % 2) * 4)) & 15) * 17);
                    }
                    for (int i = 0; i < 16; i++)
                    {
                        int x = bx * 4 + i % 4, y = by * 4 + i / 4;
                        if (x >= w || y >= h) continue;
                        Array.Copy(c, i * 4, rgba, (y * w + x) * 4, 4);
                    }
                }
        }

        static void Colour(byte[] b, int p, bool dxt1, byte[] o)
        {
            int c0 = b[p] | b[p + 1] << 8, c1 = b[p + 2] | b[p + 3] << 8;
            var pal = new int[4, 3];
            Action<int, int> rgb = (i, v) => { pal[i, 0] = (v >> 11 & 31) * 255 / 31; pal[i, 1] = (v >> 5 & 63) * 255 / 63; pal[i, 2] = (v & 31) * 255 / 31; };
            rgb(0, c0); rgb(1, c1);
            for (int k = 0; k < 3; k++)
            {
                if (c0 > c1 || !dxt1) { pal[2, k] = (2 * pal[0, k] + pal[1, k]) / 3; pal[3, k] = (pal[0, k] + 2 * pal[1, k]) / 3; }
                else { pal[2, k] = (pal[0, k] + pal[1, k]) / 2; pal[3, k] = 0; }
            }
            uint idx = BitConverter.ToUInt32(b, p + 4);
            for (int i = 0; i < 16; i++)
            {
                int s = (int)(idx >> (2 * i) & 3);
                o[i * 4] = (byte)pal[s, 0]; o[i * 4 + 1] = (byte)pal[s, 1]; o[i * 4 + 2] = (byte)pal[s, 2]; o[i * 4 + 3] = 255;
            }
        }

        static byte[] Alpha(byte[] b, int p)
        {
            int a0 = b[p], a1 = b[p + 1];
            var a = new int[8]; a[0] = a0; a[1] = a1;
            if (a0 > a1) for (int i = 1; i < 7; i++) a[i + 1] = ((7 - i) * a0 + i * a1) / 7;
            else { for (int i = 1; i < 5; i++) a[i + 1] = ((5 - i) * a0 + i * a1) / 5; a[6] = 0; a[7] = 255; }
            ulong bits = 0; for (int i = 0; i < 6; i++) bits |= (ulong)b[p + 2 + i] << (8 * i);
            var o = new byte[16];
            for (int i = 0; i < 16; i++) o[i] = (byte)a[(int)(bits >> (3 * i) & 7)];
            return o;
        }
    }
}
