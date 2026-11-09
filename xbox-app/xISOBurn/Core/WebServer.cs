using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.Sockets;
using xISOBurn.Util;

namespace xISOBurn.Core
{
    /// <summary>
    /// Web page served by the app, so the PC only needs a browser: it shows the copy in progress and the
    /// images stored on the Xbox, and downloads them. Developer Mode lets the app accept connections from the local network on ports 8770, 8080 and 50080.
    ///
    ///   GET  /                 main page (or the PIN page)
    ///   POST /login            pin=NNNN, sets a cookie
    ///   GET  /files/NAME.ext   download (.iso .map .sha256 .log); Range requests supported, so the
    ///                          browser can resume a download that was interrupted
    ///   POST /delete           name=NAME, deletes NAME.iso/.map/.sha256 from the Xbox
    ///   POST /action           do=detect|copy|retry|scan|cancel: the app's buttons (remote control)
    ///   GET  /settings         the settings form; POST /settings saves it (same rules as on the console)
    ///
    /// Download while copying: if NAME.iso is being written by a running rescue, the server waits
    /// before sending sectors that have not been read yet. If the copy stops, the download is
    /// interrupted on purpose, so the browser shows it as failed and can resume it later (Range).
    /// Sectors that failed (pending/bad) are sent as they are in the image: zeros.
    ///
    /// Plain HTTP on the local network; a PIN shown on the Xbox screen keeps others on the network
    /// from downloading or deleting copies. One request per connection (Connection: close).
    /// </summary>
    /// <summary>Result of a remote action: Ok, or why it was refused. For the settings, also the confirmation.</summary>
    internal sealed class RemoteReply
    {
        public bool Ok;
        public string Message;

        public static RemoteReply Done(string message) { return new RemoteReply { Ok = true, Message = message }; }
        public static RemoteReply Refused(string message) { return new RemoteReply { Ok = false, Message = message }; }
    }

    internal sealed class WebServer : IDisposable
    {
        private const int SectorSize = Scsi.OpticalDrive.SectorSize;
        private const int MaxHeaderBytes = 16 * 1024;
        private const int MaxBodyBytes = 4096;
        private const int FileChunk = 1 << 20;
        private const int RequestTimeoutMs = 20000;
        private static readonly TimeSpan DownloadReportInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DownloadStallLimit = TimeSpan.FromSeconds(60);
        private const string PinCookie = "xisoburn_pin";
        private static readonly string[] Downloadable = { ".iso", ".map", ".sha256", ".log" };

        private readonly Logger _log;
        private readonly string _folder;
        private readonly Func<string> _statusHtml;    // fragment built on the UI thread
        private readonly Func<CopyConfig> _settings;  // current PIN settings
        // Remote control: runs a button of the app (or saves the settings) on the UI thread.
        private readonly Func<string, IDictionary<string, string>, string, Task<RemoteReply>> _remote;
        private StreamSocketListener _listener;
        private readonly HashSet<string> _visitors = new HashSet<string>();

        public int Port { get; private set; }

        private int _activeDownloads;
        /// <summary>ISO downloads being sent right now. The UI keeps the screen on while this is above 0, so the
        /// console does not dim, idle-off or suspend the app in the middle of a download.</summary>
        public int ActiveDownloads { get { return System.Threading.Volatile.Read(ref _activeDownloads); } }

        public WebServer(Logger log, string folder, Func<string> statusHtml, Func<CopyConfig> settings,
                         Func<string, IDictionary<string, string>, string, Task<RemoteReply>> remote)
        {
            _log = log;
            _folder = folder;
            _statusHtml = statusHtml;
            _settings = settings;
            _remote = remote;
        }

        /// <summary>Starts listening; returns false (and logs why) if the port cannot be used.</summary>
        public async Task<bool> StartAsync(int port)
        {
            _listener = new StreamSocketListener();
            _listener.Control.KeepAlive = false;
            _listener.ConnectionReceived += OnConnection;
            try
            {
                await _listener.BindServiceNameAsync(port.ToString());
                Port = port;
                _log.Info("Web server: listening on port " + port + ".");
                return true;
            }
            catch (Exception ex)
            {
                _listener.Dispose();
                _listener = null;
                _log.Info("Web server: could not listen on port " + port + ": 0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                return false;
            }
        }

        /// <summary>IPv4 addresses of the console (what to type in the PC browser).</summary>
        public static List<string> LocalAddresses()
        {
            var list = new List<string>();
            foreach (HostName h in NetworkInformation.GetHostNames())
                if (h.Type == HostNameType.Ipv4 && h.IPInformation != null && !h.CanonicalName.StartsWith("127."))
                    list.Add(h.CanonicalName);
            return list;
        }

        /// <summary>
        /// Closes the listening socket and opens it again on the same port. Needed after the app was
        /// suspended (the system closes the listener then) or the network changed. Connections already
        /// accepted, such as a download in progress, are their own sockets and keep going.
        /// </summary>
        public async Task<bool> RestartAsync()
        {
            int port = Port;
            Dispose();
            return port > 0 && await StartAsync(port);
        }

        public void Dispose()
        {
            if (_listener != null)
            {
                _listener.ConnectionReceived -= OnConnection;
                _listener.Dispose();
            }
            _listener = null;
        }

        // ------------------------------------------------------------ connection handling

        private sealed class Request
        {
            public string Method = "", Path = "/";
            public Dictionary<string, string> Query = new Dictionary<string, string>();
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Form = new Dictionary<string, string>();
            public string Remote = "?";

            public string Header(string name)
            {
                string v;
                return Headers.TryGetValue(name, out v) ? v : null;
            }
        }

        private async void OnConnection(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            var socket = args.Socket;
            string remote = socket.Information.RemoteAddress != null ? socket.Information.RemoteAddress.DisplayName : "?";
            try
            {
                using (var input = socket.InputStream.AsStreamForRead())
                using (var output = socket.OutputStream.AsStreamForWrite())
                {
                    // Browsers open spare connections in advance and may never send anything on them: give
                    // up on a request that is not in after a while instead of holding the socket forever.
                    var reading = ReadRequestAsync(input);
                    if (await Task.WhenAny(reading, Task.Delay(RequestTimeoutMs)) != reading)
                    {
                        var observed = reading.ContinueWith(t => { var ignored = t.Exception; }); // fails once the socket is closed
                        return;
                    }
                    var req = await reading;
                    if (req == null) return;
                    req.Remote = remote;
                    await RouteAsync(req, output);
                }
            }
            catch (DownloadInterruptedException ex)
            {
                _log.Info("Web: " + ex.Message);
            }
            catch (Exception ex)
            {
                // Browsers often drop connections (cancelled downloads, page refresh): not worth more than a line.
                _log.Info("Web: connection from " + remote + " ended: " + ex.Message);
            }
            finally
            {
                socket.Dispose();
            }
        }

        private static byte[] _favicon;
        private static bool _faviconLoaded;

        /// <summary>
        /// The app icon (the unplated 32 px tile from the package), read once.
        /// PNG works as a favicon in every current browser, also when requested as /favicon.ico.
        /// </summary>
        private byte[] Favicon()
        {
            if (_faviconLoaded) return _favicon;
            try
            {
                string path = Path.Combine(Windows.ApplicationModel.Package.Current.InstalledLocation.Path,
                                           "Assets", "Square44x44Logo.targetsize-32_altform-unplated.png");
                _favicon = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                _log.Info("Web: favicon not available: " + ex.Message);
                _favicon = null;
            }
            _faviconLoaded = true;
            return _favicon;
        }

        private async Task RouteAsync(Request req, Stream output)
        {
            bool head = req.Method == "HEAD";
            if (req.Method == "POST" && req.Path == "/login")
            {
                var cfg = _settings();
                string pin;
                req.Form.TryGetValue("pin", out pin);
                if (!cfg.WebPinRequired || (pin ?? "").Trim() == cfg.WebPin)
                {
                    _log.Info("Web: " + req.Remote + " entered the PIN.");
                    await RedirectAsync(output, "/", "Set-Cookie: " + PinCookie + "=" + cfg.WebPin + "; Path=/; HttpOnly; SameSite=Strict\r\n");
                }
                else
                {
                    _log.Info("Web: " + req.Remote + " typed a wrong PIN.");
                    await HtmlAsync(output, 200, LoginPage("Wrong PIN. It is shown at the top of the xISOBurn screen on the Xbox."), false);
                }
                return;
            }

            // The icon is public: the PIN page shows it too.
            if ((req.Method == "GET" || head) && (req.Path == "/favicon.ico" || req.Path == "/favicon.png"))
            {
                var icon = Favicon();
                if (icon != null) await RespondAsync(output, 200, "OK", "image/png", icon, head);
                else await RespondAsync(output, 404, "Not Found", "text/plain", new byte[0], head);
                return;
            }

            if (!Authorized(req))
            {
                if (req.Path.StartsWith("/files/"))
                    await RespondAsync(output, 403, "Forbidden", "text/plain", Encoding.UTF8.GetBytes("PIN required: open the main page first."), head);
                else
                    await HtmlAsync(output, 200, LoginPage(null), head);
                return;
            }

            if ((req.Method == "GET" || head) && (req.Path == "/" || req.Path == "/index.html"))
            {
                bool first;
                lock (_visitors) first = _visitors.Add(req.Remote);
                if (first) _log.Info("Web: " + req.Remote + " opened the page.");
                await HtmlAsync(output, 200, MainPage(), head);
            }
            else if ((req.Method == "GET" || head) && req.Path.StartsWith("/files/"))
            {
                await ServeFileAsync(req, output, req.Path.Substring("/files/".Length), head);
            }
            else if (req.Method == "POST" && req.Path == "/delete")
            {
                string name;
                req.Form.TryGetValue("name", out name);
                string message = Delete(name ?? "", req.Remote);
                if (message == null) await RedirectAsync(output, "/", "");
                else await HtmlAsync(output, 200, Shell("<p>" + Enc(message) + "</p><p><a href=\"/\">Back</a></p>", false), false);
            }
            else if (req.Method == "POST" && req.Path == "/action")
            {
                // The buttons of the app: detect, copy, retry, scan, cancel.
                string action;
                req.Form.TryGetValue("do", out action);
                var reply = await _remote(action ?? "", req.Form, req.Remote);
                if (reply.Ok) await RedirectAsync(output, "/", "");
                else await HtmlAsync(output, 200, Shell("<p class=\"err\">" + Enc(reply.Message) + "</p><p><a href=\"/\">Back</a></p>", false), false);
            }
            else if ((req.Method == "GET" || head) && req.Path == "/settings")
            {
                string msg;
                await HtmlAsync(output, 200, SettingsPage(CopyConfig.Load(_folder, out msg), null), head);
            }
            else if (req.Method == "POST" && req.Path == "/settings")
            {
                var reply = await _remote("settings", req.Form, req.Remote);
                if (reply.Ok)
                {
                    await HtmlAsync(output, 200, Shell("<p class=\"ok\">" + Enc(reply.Message) + "</p><p><a href=\"/\">Back</a></p>", false), false);
                }
                else
                {
                    // Show the form again with what was typed, so the field can be fixed.
                    string msg;
                    var typed = CopyConfig.Load(_folder, out msg);
                    await HtmlAsync(output, 200, SettingsPage(typed, reply.Message, req.Form), false);
                }
            }
            else
            {
                await RespondAsync(output, 404, "Not Found", "text/plain", Encoding.UTF8.GetBytes("not found"), head);
            }
        }

        private bool Authorized(Request req)
        {
            var cfg = _settings();
            if (!cfg.WebPinRequired) return true;
            string q;
            if (req.Query.TryGetValue("pin", out q) && q == cfg.WebPin) return true;
            string cookie = req.Header("Cookie");
            if (cookie == null) return false;
            foreach (var part in cookie.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq).Trim() == PinCookie && part.Substring(eq + 1).Trim() == cfg.WebPin) return true;
            }
            return false;
        }

        // ------------------------------------------------------------ files

        private sealed class DownloadInterruptedException : Exception
        {
            public DownloadInterruptedException(string message) : base(message) { }
        }

        private async Task ServeFileAsync(Request req, Stream output, string rawName, bool head)
        {
            string name = Uri.UnescapeDataString(rawName);
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (name.Length == 0 || name != Path.GetFileName(name) || Array.IndexOf(Downloadable, ext) < 0)
            {
                await RespondAsync(output, 404, "Not Found", "text/plain", Encoding.UTF8.GetBytes("not found"), head);
                return;
            }
            string path = Path.Combine(_folder, name);
            if (!File.Exists(path))
            {
                await RespondAsync(output, 404, "Not Found", "text/plain", Encoding.UTF8.GetBytes("not found"), head);
                return;
            }

            bool iso = ext == ".iso";
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            {
                long length = fs.Length;
                // The ISO keeps its full size from the start of a copy, so length identifies it well enough
                // for the browser's resume (If-Range): retries only ever fill in sectors, never move them.
                string etag = "\"xisoburn-" + length + "\"";
                long start = 0, end = length - 1;
                bool partial = false;
                string range = req.Header("Range");
                string ifRange = req.Header("If-Range");
                if (range != null && (ifRange == null || ifRange == etag) && length > 0)
                {
                    if (!ParseRange(range, length, out start, out end))
                    {
                        await RespondAsync(output, 416, "Range Not Satisfiable", "text/plain", new byte[0], head,
                                           "Content-Range: bytes */" + length + "\r\n");
                        return;
                    }
                    partial = true;
                }

                long count = length == 0 ? 0 : end - start + 1;
                string type = iso ? "application/octet-stream" : "text/plain; charset=utf-8";
                string disposition = iso ? "attachment" : "inline";
                // ?dl=TOKEN (added by the page's script when the ISO link is clicked): echo it in a cookie, so
                // the page knows the download has started and can hide its loading and resume refreshing.
                string token;
                string dlCookie = req.Query.TryGetValue("dl", out token) && IsToken(token)
                    ? "Set-Cookie: xbdl=" + token + "; Path=/; SameSite=Strict\r\n" : "";
                string headers = "HTTP/1.1 " + (partial ? "206 Partial Content" : "200 OK") + "\r\n" +
                                 dlCookie +
                                 "Content-Type: " + type + "\r\n" +
                                 "Content-Length: " + count + "\r\n" +
                                 (partial ? "Content-Range: bytes " + start + "-" + end + "/" + length + "\r\n" : "") +
                                 "Accept-Ranges: bytes\r\n" +
                                 "ETag: " + etag + "\r\n" +
                                 "Content-Disposition: " + disposition + "; filename=\"" + name + "\"\r\n" +
                                 "Cache-Control: no-store\r\n" +
                                 "Connection: close\r\n\r\n";
                var h = Encoding.ASCII.GetBytes(headers);
                await output.WriteAsync(h, 0, h.Length);
                if (head || count == 0) { await output.FlushAsync(); return; }

                SectorStateMap live = iso ? LiveImages.Get(name) : null;
                if (iso)
                    _log.Info("Web: " + req.Remote + " is downloading " + name + (start > 0 ? " from " + (start * 100 / length) + "%" : "") +
                              (live != null ? " (following the copy in progress)" : "") + ".");
                var sw = Stopwatch.StartNew();
                var buffer = new byte[FileChunk];
                long pos = start;
                TimeSpan lastReport = TimeSpan.Zero;
                if (iso) System.Threading.Interlocked.Increment(ref _activeDownloads);
                try
                {
                    while (pos <= end)
                    {
                        int n = (int)Math.Min(buffer.Length, end - pos + 1);
                        if (live != null && !await WaitForSectorsAsync(name, live, pos, n))
                            throw new DownloadInterruptedException("download of " + name + " by " + req.Remote + " stopped at " + (pos * 100 / length) +
                                                                   "% because the copy stopped; resume it in the browser after resuming the copy.");
                        fs.Position = pos;
                        int read = 0;
                        while (read < n)
                        {
                            int r = await fs.ReadAsync(buffer, read, n - read);
                            if (r <= 0) throw new IOException("file shorter than expected");
                            read += r;
                        }
                        await WithStallLimitAsync(output.WriteAsync(buffer, 0, n), name, req.Remote, pos, length);
                        pos += n;

                        if (iso && sw.Elapsed - lastReport >= DownloadReportInterval)
                        {
                            // Heartbeat: shows in the log how far a download got and whether it is still moving.
                            lastReport = sw.Elapsed;
                            double mib = (pos - start) / 1048576.0;
                            _log.Info("Web: " + name + " to " + req.Remote + ": " + (pos * 100 / length) + "% (" + mib.ToString("0") + " MiB this request, " +
                                      (mib / Math.Max(0.001, sw.Elapsed.TotalSeconds)).ToString("0.0") + " MiB/s)" +
                                      (live != null && LiveImages.Get(name) == live ? ", following the copy" : "") + ".");
                        }
                    }
                    await WithStallLimitAsync(output.FlushAsync(), name, req.Remote, pos, length);
                }
                finally
                {
                    if (iso) System.Threading.Interlocked.Decrement(ref _activeDownloads);
                }
                if (iso)
                {
                    double secs = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    _log.Info("Web: " + name + " sent to " + req.Remote + ": " + (count / 1048576.0).ToString("0") + " MiB in " +
                              RescueJob.FormatTime(sw.Elapsed) + " (" + (count / 1048576.0 / secs).ToString("0.0") + " MiB/s).");
                }
            }
        }

        /// <summary>
        /// Waits for a write to the browser, but not forever. Downloads were seen to stop with the write
        /// never completing and never failing, which left the browser hanging until the app was closed.
        /// Past the limit the connection is dropped (the caller's finally closes the socket), so the
        /// browser reports a network error and its Resume continues from where it stopped (Range).
        /// </summary>
        private static async Task WithStallLimitAsync(Task write, string name, string remote, long pos, long length)
        {
            if (await Task.WhenAny(write, Task.Delay(DownloadStallLimit)) == write)
            {
                await write; // rethrows a failed write
                return;
            }
            var observed = write.ContinueWith(t => { var ignored = t.Exception; }); // fails once the socket is closed
            throw new DownloadInterruptedException("download of " + name + " by " + remote + " stalled at " + (pos * 100 / Math.Max(1, length)) +
                                                   "%: the PC took no data for " + (int)DownloadStallLimit.TotalSeconds +
                                                   " s. Connection closed; choose Resume in the browser's downloads.");
        }

        /// <summary>
        /// Waits until every sector overlapping [pos, pos+n) has been tried by the running copy. Returns
        /// false if the copy stops while some of them are still untried.
        /// </summary>
        private static async Task<bool> WaitForSectorsAsync(string name, SectorStateMap map, long pos, int n)
        {
            long first = pos / SectorSize, last = Math.Min(map.TotalSectors - 1, (pos + n - 1) / SectorSize);
            for (long s = first; s <= last; s++)
            {
                while (map.Get(s) == SectorState.Untried)
                {
                    if (LiveImages.Get(name) != map) return false;
                    await Task.Delay(500);
                }
            }
            return true;
        }

        private static bool ParseRange(string header, long length, out long start, out long end)
        {
            start = 0;
            end = length - 1;
            header = header.Trim();
            if (!header.StartsWith("bytes=") || header.IndexOf(',') >= 0) return false; // one range only
            string spec = header.Substring(6);
            int dash = spec.IndexOf('-');
            if (dash < 0) return false;
            string a = spec.Substring(0, dash).Trim(), b = spec.Substring(dash + 1).Trim();
            long v;
            if (a.Length == 0)
            {
                if (!long.TryParse(b, out v) || v <= 0) return false; // "bytes=-N": the last N bytes
                start = Math.Max(0, length - v);
                return true;
            }
            if (!long.TryParse(a, out start) || start >= length) return false;
            if (b.Length > 0)
            {
                if (!long.TryParse(b, out v) || v < start) return false;
                end = Math.Min(v, length - 1);
            }
            return true;
        }

        /// <summary>Deletes NAME.iso and its companion files. Returns null when done, or why it was refused.</summary>
        private string Delete(string name, string remote)
        {
            if (name.Length == 0 || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return "Invalid name.";
            if (LiveImages.Get(name + ".iso") != null)
                return name + " is being copied right now. Pause the copy first (Pause / cancel).";
            int deleted = 0;
            foreach (var ext in new[] { ".iso", ".map", ".sha256", ".map.old", ".map.tmp" })
            {
                string path = Path.Combine(_folder, name + ext);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            _log.Info("Web: " + remote + " deleted " + name + " (" + deleted + " file(s)) from the Xbox.");
            return null;
        }

        // ------------------------------------------------------------ pages

        private string MainPage()
        {
            string status;
            try { status = _statusHtml(); }
            catch (Exception ex) { status = "<p>" + Enc(ex.Message) + "</p>"; }

            var sb = new StringBuilder();
            sb.Append(status);
            sb.Append("<h2>Copies on the Xbox</h2>");
            var isos = new List<string>(Directory.GetFiles(_folder, "*.iso"));
            isos.Sort(StringComparer.OrdinalIgnoreCase);
            if (isos.Count == 0)
            {
                sb.Append("<p class=\"dim\">None yet. Insert a disc on the Xbox and choose <b>Copy / resume</b>.</p>");
            }
            else
            {
                sb.Append("<table><tr><th>Disc</th><th>Size</th><th>State</th><th>Download</th><th></th></tr>");
                foreach (var path in isos)
                    sb.Append(ImageRow(path));
                sb.Append("</table>");
                sb.Append("<p class=\"dim\">Downloads can be resumed in the browser if the network drops. ISO links of a copy in " +
                          "progress follow the copy: the download waits for the sectors still being read. Unreadable sectors are zeros " +
                          "in the ISO and listed in the map.</p>");
            }

            var logs = new List<string>(Directory.GetFiles(_folder, "*.log"));
            logs.Sort(StringComparer.OrdinalIgnoreCase);
            logs.Reverse();
            if (logs.Count > 0)
            {
                sb.Append("<details id=\"logfiles\"><summary>Log files (").Append(logs.Count).Append(")</summary><ul>");
                for (int i = 0; i < logs.Count && i < 30; i++)
                {
                    string n = Path.GetFileName(logs[i]);
                    sb.Append("<li><a href=\"/files/").Append(Uri.EscapeDataString(n)).Append("\">").Append(Enc(n)).Append("</a></li>");
                }
                sb.Append("</ul></details>");
            }

            return Shell(sb.ToString(), true);
        }

        private string ImageRow(string isoPath)
        {
            string file = Path.GetFileName(isoPath);
            string name = Path.GetFileNameWithoutExtension(isoPath);
            long size = new FileInfo(isoPath).Length;
            var live = LiveImages.Get(file);
            string sha = ReadSha(Path.Combine(_folder, name + ".sha256"));
            string state;
            if (live != null)
            {
                state = "<b class=\"busy\">Copying now</b> - " + live.Percent(live.Count(SectorState.Ok)) + " read" +
                        (live.Count(SectorState.Bad) + live.Count(SectorState.Pending) > 0
                            ? ", " + (live.Count(SectorState.Bad) + live.Count(SectorState.Pending)).ToString("N0") + " failed" : "");
            }
            else
            {
                string summary = ReadMapSummary(Path.Combine(_folder, name + ".map"));
                state = sha != null ? "<b class=\"ok\">Complete</b>" : "<b>Incomplete</b> (resume the copy on the Xbox)";
                if (summary != null) state += "<br><span class=\"dim\">" + Enc(summary) + "</span>";
            }
            if (sha != null) state += "<br><span class=\"dim\">SHA-256 " + Enc(sha) + "</span>";

            string enc = Uri.EscapeDataString(name);
            var links = new StringBuilder();
            // data-dl: the page's script follows this download (see BusyOverlay).
            links.Append("<a data-dl=\"1\" href=\"/files/").Append(Uri.EscapeDataString(file)).Append("\">ISO</a>");
            if (File.Exists(Path.Combine(_folder, name + ".map")))
                links.Append(" &middot; <a href=\"/files/").Append(enc).Append(".map\">map</a>");
            if (sha != null)
                links.Append(" &middot; <a href=\"/files/").Append(enc).Append(".sha256\">sha256</a>");

            string delete = live != null ? "" :
                "<form method=\"post\" action=\"/delete\" data-wait=\"Deleting...\" onsubmit=\"return confirm('Delete " + Enc(name).Replace("'", "\\'") +
                " from the Xbox? Download it first.')\"><input type=\"hidden\" name=\"name\" value=\"" + Enc(name) +
                "\"><button>Delete</button></form>";

            return "<tr><td><b>" + Enc(name) + "</b></td><td>" + (size / 1073741824.0).ToString("0.00") + " GiB</td><td>" + state +
                   "</td><td>" + links + "</td><td>" + delete + "</td></tr>";
        }

        private static string ReadSha(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path).Trim();
                int sp = text.IndexOf(' ');
                return sp > 0 ? text.Substring(0, sp) : text;
            }
            catch (IOException) { return null; }
        }

        /// <summary>The "# summary:" header line of a map (written by SectorStateMap.ToText), without parsing the whole map.</summary>
        private static string ReadMapSummary(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs))
                {
                    for (int i = 0; i < 15; i++)
                    {
                        string line = reader.ReadLine();
                        if (line == null) break;
                        if (line.StartsWith("# summary:")) return line.Substring(10).Trim();
                    }
                }
            }
            catch (IOException) { }
            return null;
        }

        private static string LoginPage(string error)
        {
            return Shell("<h2>PIN</h2><p>Type the PIN shown at the top of the xISOBurn screen on the Xbox.</p>" +
                         (error != null ? "<p class=\"err\">" + Enc(error) + "</p>" : "") +
                         "<form method=\"post\" action=\"/login\" data-wait=\"Checking the PIN...\"><input name=\"pin\" type=\"password\" inputmode=\"numeric\" " +
                         "autocomplete=\"off\" autofocus size=\"8\"> <button>Enter</button></form>", false);
        }

        /// <summary>
        /// The same settings as the console's Settings dialog. 'typed' (after a rejected save) refills
        /// the fields with what was sent instead of the saved values.
        /// </summary>
        private static string SettingsPage(CopyConfig cfg, string error, IDictionary<string, string> typed = null)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Func<string, string, string> val = (key, saved) =>
            {
                string v;
                return typed != null ? (typed.TryGetValue(key, out v) ? v : "") : saved;
            };
            Func<string, bool, string> check = (key, saved) =>
            {
                bool on = typed != null ? val(key, "") == "true" : saved;
                return "<label><input type=\"checkbox\" name=\"" + key + "\" value=\"true\"" + (on ? " checked" : "") + "> ";
            };
            Func<string, string, string[][], string> select = (key, current, options) =>
            {
                var s = new StringBuilder("<select name=\"" + key + "\">");
                bool found = false;
                foreach (var o in options)
                {
                    bool sel = o[0] == current;
                    found |= sel;
                    s.Append("<option value=\"").Append(o[0]).Append('"').Append(sel ? " selected" : "").Append('>').Append(Enc(o[1])).Append("</option>");
                }
                if (!found) s.Append("<option value=\"").Append(Enc(current)).Append("\" selected>").Append(Enc(current)).Append(" (from config.json)</option>");
                return s.Append("</select>").ToString();
            };
            Func<string, string, string, string> text = (key, saved, attrs) =>
                "<input name=\"" + key + "\" value=\"" + Enc(val(key, saved)) + "\" " + attrs + ">";

            var b = new StringBuilder();
            b.Append("<h2>Settings</h2>");
            if (error != null) b.Append("<p class=\"err\">").Append(Enc(error)).Append("</p>");
            b.Append("<form method=\"post\" action=\"/settings\" class=\"settings\" data-wait=\"Saving the settings...\">");

            b.Append("<h3>Web page</h3>");
            b.Append("<p>Port (1024-65535; changing it needs an app restart)<br>")
             .Append(text("WebPort", cfg.WebPort.ToString(inv), "type=\"number\" min=\"1024\" max=\"65535\"")).Append("</p>");
            b.Append("<p>").Append(check("WebPinRequired", cfg.WebPinRequired)).Append("Ask for the PIN on the web page</label></p>");
            b.Append("<p>PIN (4 to 8 digits)<br>").Append(text("WebPin", cfg.WebPin, "inputmode=\"numeric\" size=\"8\"")).Append("</p>");
            b.Append("<p>").Append(check("KeepAwake", cfg.KeepAwake)).Append("Keep the console awake while xISOBurn is open (otherwise the Xbox suspends the app when idle and this page stops answering)</label></p>");

            b.Append("<h3>Copy</h3>");
            b.Append("<p>File name (empty = the disc's volume label)<br>").Append(text("OutputName", cfg.OutputName, "size=\"30\"")).Append("</p>");
            b.Append("<p>").Append(check("WriteImage", cfg.WriteImage)).Append("Write the image (off = only test the drive and build the map)</label></p>");
            b.Append("<p>").Append(check("DecryptCss", cfg.DecryptCss)).Append("Decrypt CSS-protected DVD-Video while copying (off = copy it scrambled)</label></p>");

            b.Append("<h3>Reading</h3>");
            b.Append("<p>Sectors per read<br>").Append(select("SectorsPerRead", val("SectorsPerRead", cfg.SectorsPerRead.ToString(inv)), new[]
            {
                new[] { "16", "16 (32 KiB)" }, new[] { "32", "32 (64 KiB, default)" }, new[] { "48", "48 (96 KiB)" }, new[] { "64", "64 (128 KiB)" },
            })).Append("</p>");
            b.Append("<p>Timeout per command (seconds, 5-120)<br>")
             .Append(text("TimeoutSeconds", cfg.TimeoutSeconds.ToString(inv), "type=\"number\" min=\"5\" max=\"120\"")).Append("</p>");
            b.Append("<p>").Append(check("SkipOnErrors", cfg.SkipOnErrors)).Append("Skip ahead over areas with many errors (filled in later)</label></p>");
            b.Append("<p>Retry passes over bad sectors (0-100)<br>")
             .Append(text("RetryPasses", cfg.RetryPasses.ToString(inv), "type=\"number\" min=\"0\" max=\"100\"")).Append("</p>");
            b.Append("<p>Drive speed for retries<br>").Append(select("RetryReadSpeedKBps", val("RetryReadSpeedKBps", cfg.RetryReadSpeedKBps.ToString(inv)), new[]
            {
                new[] { "0", "Do not change" }, new[] { "1385", "DVD 1x (1385 kB/s, default)" },
                new[] { "2770", "DVD 2x (2770 kB/s)" }, new[] { "5540", "DVD 4x (5540 kB/s)" },
            })).Append("</p>");
            b.Append("<p>Drive's own retries per failing read (experimental)<br>").Append(select("DriveReadRetryCount", val("DriveReadRetryCount", cfg.DriveReadRetryCount.ToString(inv)), new[]
            {
                new[] { "-1", "Do not change (default)" }, new[] { "0", "0 (fastest failures)" },
                new[] { "1", "1" }, new[] { "4", "4" }, new[] { "8", "8" },
            })).Append("</p>");

            b.Append("<h3>Range (for tests)</h3>");
            b.Append("<p>First sector<br>").Append(text("StartSector", cfg.StartSector.ToString(inv), "type=\"number\" min=\"0\"")).Append("</p>");
            b.Append("<p>Sectors (0 = to the end)<br>").Append(text("SectorLimit", cfg.SectorLimit.ToString(inv), "type=\"number\" min=\"0\"")).Append("</p>");

            b.Append("<p><button>Save</button> &nbsp; <a href=\"/\">Cancel</a></p>");
            b.Append("<p class=\"dim\">Changes apply from the next run. Only a new web page port needs an app restart. " +
                     "If you change the PIN, the page asks for the new one.</p>");
            b.Append("</form>");
            return Shell(b.ToString(), false);
        }

        // The page reloads every 5 s. Across reloads it keeps which <details> are open and the scroll
        // position (sessionStorage), and it skips a reload while text is selected, so the log can be read
        // and copied.
        private const string RefreshScript =
            "<script>(function(){" +
            "var S=window.sessionStorage;" +
            "document.querySelectorAll('details[id]').forEach(function(d){" +
            "if(S.getItem('open_'+d.id)==='1')d.open=true;" +
            "d.addEventListener('toggle',function(){S.setItem('open_'+d.id,d.open?'1':'0')});});" +
            "var y=S.getItem('scrollY');if(y!==null)window.scrollTo(0,parseInt(y,10));" +
            "function tick(){" +
            "if(window.xbBusy){setTimeout(tick,5000);return;}" + // a form or a download is on its way
            "var sel=window.getSelection?String(window.getSelection()):'';" +
            "if(sel.length>0){setTimeout(tick,5000);return;}" +
            "S.setItem('scrollY',String(window.scrollY));location.reload();}" +
            "setTimeout(tick,5000);})();</script>";

        // Full-screen "loading" while a form is sent (the app's buttons, Settings, Delete, PIN): the Xbox
        // can take a moment to answer, and without it the click looked ignored. The listener is on the
        // document, so it runs after a form's own onsubmit: a cancelled Delete confirmation
        // (defaultPrevented) shows nothing. The overlay also blocks a second click. The submit button is
        // not disabled, because a disabled button's name/value would not be sent. 'pageshow' hides the
        // overlay when the browser's Back button brings a cached page back.
        private const string BusyOverlay =
            "<div id=\"busy\" role=\"status\" aria-live=\"polite\"><div class=\"spin\"></div><div id=\"busytext\"></div></div>" +
            "<script>(function(){" +
            "var o=document.getElementById('busy'),t=document.getElementById('busytext'),n=0;" +
            "function hide(){n++;o.className='';o.onclick=null;window.xbBusy=false;}" +
            // Shows the overlay; with no answer after 'wait' ms (app closed, network down) it says so and
            // closes on a click. 'n' tells a stale timer from the current one.
            "function show(text,wait){var my=++n;t.textContent=text;o.className='on';window.xbBusy=true;" +
            "setTimeout(function(){if(my!==n)return;" +
            "t.textContent='No answer from the Xbox. Is xISOBurn still open on the screen? Click to close.';" +
            "o.onclick=hide;},wait);return my;}" +
            "document.addEventListener('submit',function(e){" +
            "if(e.defaultPrevented)return;" +
            "var b=e.submitter||e.target.querySelector('button');" +
            "show((b&&b.getAttribute('data-wait'))||e.target.getAttribute('data-wait')||'Sending to the Xbox...',30000);});" +
            // ISO links: a download does not replace the page, so the 5 s reload could cancel the request
            // before the Xbox answers (the click then seemed ignored). Pause the reload and follow the
            // download: the server echoes ?dl=TOKEN in the xbdl cookie once the download has started.
            "document.addEventListener('click',function(e){" +
            "var a=e.target.closest?e.target.closest('a[data-dl]'):null;" +
            "if(!a||e.button!==0||e.ctrlKey||e.metaKey||e.shiftKey||e.altKey)return;" +
            "e.preventDefault();" +
            "var k=Date.now().toString(36)+Math.random().toString(36).slice(2,8);" +
            "var my=show('Starting the download...',60000),iv=setInterval(function(){" +
            "if(my!==n){clearInterval(iv);return;}" +
            "if(document.cookie.indexOf('xbdl='+k)>=0){clearInterval(iv);hide();}},250);" +
            "location.href=a.href+(a.href.indexOf('?')<0?'?':'&')+'dl='+k;});" +
            "window.addEventListener('pageshow',hide);" +
            "})();</script>";

        private static string Shell(string body, bool autoRefresh)
        {
            // Refresh with JavaScript, not <meta refresh>: timers pause while the delete confirmation is open.
            return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">" +
                   "<title>xISOBurn</title><link rel=\"icon\" type=\"image/png\" href=\"/favicon.png\"><style>" +
                   "body{background:#0b0b0b;color:#e8e8e8;font:15px Segoe UI,sans-serif;margin:24px;max-width:1100px}" +
                   "h1{color:#4cc417;margin:0}h2{font-size:17px;color:#9fe07a;margin:22px 0 8px}" +
                   "a{color:#4cc417}.box{background:#161616;border:1px solid #2e7d32;padding:10px;white-space:pre-wrap;margin-bottom:6px}" +
                   "pre{background:#161616;border:1px solid #333;padding:10px;white-space:pre-wrap;font:12px Consolas,monospace}" +
                   "table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid #2a2a2a;padding:8px;text-align:left;vertical-align:top}" +
                   "th{color:#9fe07a;font-weight:600}.dim{color:#9a9a9a;font-size:13px}.ok{color:#4cc417}.busy{color:#f0c040}.err{color:#ff6b6b}" +
                   "button{background:#2e7d32;color:#fff;border:0;padding:6px 12px;border-radius:3px;cursor:pointer}" +
                   "input{padding:6px;font-size:16px}details{margin-top:18px}" +
                   "button[disabled]{background:#333;color:#777;cursor:default}.controls button{margin:0 6px 6px 0;font-size:15px}" +
                   "h3{font-size:15px;color:#9fe07a;margin:16px 0 4px}select{padding:6px;font-size:15px}.settings p{margin:6px 0}" +
                   "#busy{position:fixed;top:0;left:0;right:0;bottom:0;background:rgba(0,0,0,.65);display:none;z-index:10;" +
                   "flex-direction:column;align-items:center;justify-content:center;font-size:17px;color:#e8e8e8;cursor:wait}" +
                   "#busy.on{display:flex}.spin{width:48px;height:48px;margin-bottom:16px;border:5px solid #2e7d32;" +
                   "border-top-color:#4cc417;border-radius:50%;animation:spin .8s linear infinite}" +
                   "@keyframes spin{to{transform:rotate(360deg)}}" +
                   "</style></head><body><h1>xISOBurn</h1><div class=\"dim\">Served by the Xbox" +
                   (autoRefresh ? " &middot; updates every 5 s" : "") + "</div>" + body +
                   BusyOverlay +
                   (autoRefresh ? RefreshScript : "") +
                   "</body></html>";
        }

        /// <summary>The download token the page sends (?dl=): letters and digits only, so it is safe in a header.</summary>
        private static bool IsToken(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 32) return false;
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) return false;
            return true;
        }

        private static string Enc(string s)
        {
            return WebUtility.HtmlEncode(s ?? "");
        }

        // ------------------------------------------------------------ HTTP plumbing

        private static async Task<Request> ReadRequestAsync(Stream input)
        {
            var buf = new byte[MaxHeaderBytes];
            int n = 0, headEnd = -1;
            while (n < buf.Length)
            {
                int r = await input.ReadAsync(buf, n, buf.Length - n);
                if (r <= 0) break;
                n += r;
                headEnd = IndexOf(buf, n, "\r\n\r\n");
                if (headEnd >= 0) break;
            }
            if (headEnd < 0) return null;

            var req = new Request();
            string[] lines = Encoding.ASCII.GetString(buf, 0, headEnd).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            req.Method = first[0].ToUpperInvariant();
            string target = first[1];
            int q = target.IndexOf('?');
            req.Path = q >= 0 ? target.Substring(0, q) : target;
            if (q >= 0) ParseForm(target.Substring(q + 1), req.Query);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0) req.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            int length;
            string cl = req.Header("Content-Length");
            if (cl != null && int.TryParse(cl, out length) && length > 0 && length <= MaxBodyBytes)
            {
                var body = new byte[length];
                int have = Math.Min(length, n - (headEnd + 4));
                Buffer.BlockCopy(buf, headEnd + 4, body, 0, have);
                while (have < length)
                {
                    int r = await input.ReadAsync(body, have, length - have);
                    if (r <= 0) break;
                    have += r;
                }
                ParseForm(Encoding.UTF8.GetString(body, 0, have), req.Form);
            }
            return req;
        }

        private static void ParseForm(string text, Dictionary<string, string> into)
        {
            foreach (var pair in text.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string k = eq >= 0 ? pair.Substring(0, eq) : pair;
                string v = eq >= 0 ? pair.Substring(eq + 1) : "";
                into[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' '));
            }
        }

        private static int IndexOf(byte[] b, int length, string ascii)
        {
            for (int i = 0; i + ascii.Length <= length; i++)
            {
                bool match = true;
                for (int j = 0; j < ascii.Length && match; j++) match = b[i + j] == ascii[j];
                if (match) return i;
            }
            return -1;
        }

        private static Task HtmlAsync(Stream output, int status, string html, bool headOnly)
        {
            return RespondAsync(output, status, status == 200 ? "OK" : "Error", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), headOnly);
        }

        private static Task RedirectAsync(Stream output, string location, string extraHeaders)
        {
            return RespondAsync(output, 303, "See Other", "text/plain", new byte[0], false, "Location: " + location + "\r\n" + extraHeaders);
        }

        private static async Task RespondAsync(Stream output, int status, string reason, string contentType, byte[] body, bool headOnly,
                                               string extraHeaders = "")
        {
            string head = "HTTP/1.1 " + status + " " + reason + "\r\n" +
                          "Content-Type: " + contentType + "\r\n" +
                          "Content-Length: " + body.Length + "\r\n" +
                          extraHeaders +
                          "Cache-Control: no-store\r\n" +
                          "Connection: close\r\n\r\n";
            var h = Encoding.ASCII.GetBytes(head);
            await output.WriteAsync(h, 0, h.Length);
            if (!headOnly && body.Length > 0) await output.WriteAsync(body, 0, body.Length);
            await output.FlushAsync();
        }
    }
}
