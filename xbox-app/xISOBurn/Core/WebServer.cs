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
    /// Minimal HTTP/1.1 server inside the app (StreamSocketListener), so a browser on the PC can talk to
    /// the Xbox directly. Milestone 3b spike: it answers whether Developer Mode lets a UWP app accept
    /// incoming connections from the local network at all, on which ports, and how fast a download
    /// from the Xbox to the PC is.
    ///
    ///   GET /          status page (disc, progress, recent log), refreshes itself
    ///   GET /test.bin  64 MiB of generated data, to measure the download speed in the browser
    ///
    /// Every connection is logged with the remote address, so if the page does not load we can tell
    /// whether the connection even reached the app. Needs the privateNetworkClientServer capability.
    /// </summary>
    internal sealed class WebServer : IDisposable
    {
        private const int MaxHeaderBytes = 16 * 1024;
        private const int TestDownloadMiB = 64;

        private readonly Logger _log;
        private readonly Func<string> _pageBody; // HTML fragment built on the UI thread
        private readonly List<StreamSocketListener> _listeners = new List<StreamSocketListener>();
        private readonly HashSet<string> _visitors = new HashSet<string>(); // log each browser once, not every refresh

        public List<int> Ports { get; private set; }

        public WebServer(Logger log, Func<string> pageBody)
        {
            _log = log;
            _pageBody = pageBody;
            Ports = new List<int>();
        }

        public async Task StartAsync(IEnumerable<int> ports)
        {
            foreach (int port in ports)
            {
                var listener = new StreamSocketListener();
                listener.Control.KeepAlive = false;
                listener.ConnectionReceived += OnConnection;
                try
                {
                    await listener.BindServiceNameAsync(port.ToString());
                    _listeners.Add(listener);
                    Ports.Add(port);
                    _log.Info("Web server: listening on port " + port + ".");
                }
                catch (Exception ex)
                {
                    listener.Dispose();
                    _log.Info("Web server: could not listen on port " + port + ": 0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                }
            }
        }

        /// <summary>IPv4 addresses of the console on its network adapters (what to type in the PC browser).</summary>
        public static List<string> LocalAddresses()
        {
            var list = new List<string>();
            foreach (HostName h in NetworkInformation.GetHostNames())
                if (h.Type == HostNameType.Ipv4 && h.IPInformation != null && !h.CanonicalName.StartsWith("127."))
                    list.Add(h.CanonicalName);
            return list;
        }

        private async void OnConnection(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            var socket = args.Socket;
            string remote = socket.Information.RemoteAddress != null ? socket.Information.RemoteAddress.DisplayName : "?";
            string port = socket.Information.LocalPort;
            try
            {
                using (var input = socket.InputStream.AsStreamForRead())
                using (var output = socket.OutputStream.AsStreamForWrite())
                {
                    string request = await ReadRequestLineAsync(input);
                    if (request == null) return;
                    string[] parts = request.Split(' ');
                    string method = parts.Length > 0 ? parts[0] : "";
                    string path = parts.Length > 1 ? parts[1] : "/";
                    int q = path.IndexOf('?');
                    if (q >= 0) path = path.Substring(0, q);

                    if (method != "GET" && method != "HEAD")
                    {
                        await RespondAsync(output, 405, "Method Not Allowed", "text/plain", Encoding.UTF8.GetBytes("GET only"), method == "HEAD");
                    }
                    else if (path == "/" || path == "/index.html")
                    {
                        bool first;
                        lock (_visitors) first = _visitors.Add(remote + ":" + port);
                        if (first) _log.Info("Web: " + remote + " opened the page (port " + port + ") - incoming connections work.");
                        await RespondAsync(output, 200, "OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page()), method == "HEAD");
                    }
                    else if (path == "/test.bin")
                    {
                        await TestDownloadAsync(output, remote, method == "HEAD");
                    }
                    else if (path == "/favicon.ico")
                    {
                        await RespondAsync(output, 404, "Not Found", "text/plain", new byte[0], method == "HEAD");
                    }
                    else
                    {
                        await RespondAsync(output, 404, "Not Found", "text/plain", Encoding.UTF8.GetBytes("not found"), method == "HEAD");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Info("Web: connection from " + remote + " failed: " + ex.Message);
            }
            finally
            {
                socket.Dispose();
            }
        }

        /// <summary>Reads the request head (up to the blank line) and returns its first line.</summary>
        private static async Task<string> ReadRequestLineAsync(Stream input)
        {
            var head = new byte[MaxHeaderBytes];
            int n = 0;
            while (n < head.Length)
            {
                int r = await input.ReadAsync(head, n, head.Length - n);
                if (r <= 0) break;
                n += r;
                if (IndexOf(head, n, "\r\n\r\n") >= 0) break;
            }
            int eol = IndexOf(head, n, "\r\n");
            if (eol <= 0) return null;
            return Encoding.ASCII.GetString(head, 0, eol);
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

        private static async Task RespondAsync(Stream output, int status, string reason, string contentType, byte[] body, bool headOnly)
        {
            string head = "HTTP/1.1 " + status + " " + reason + "\r\n" +
                          "Content-Type: " + contentType + "\r\n" +
                          "Content-Length: " + body.Length + "\r\n" +
                          "Cache-Control: no-store\r\n" +
                          "Connection: close\r\n\r\n";
            var h = Encoding.ASCII.GetBytes(head);
            await output.WriteAsync(h, 0, h.Length);
            if (!headOnly && body.Length > 0) await output.WriteAsync(body, 0, body.Length);
            await output.FlushAsync();
        }

        private async Task TestDownloadAsync(Stream output, string remote, bool headOnly)
        {
            long total = TestDownloadMiB * 1048576L;
            string head = "HTTP/1.1 200 OK\r\n" +
                          "Content-Type: application/octet-stream\r\n" +
                          "Content-Length: " + total + "\r\n" +
                          "Content-Disposition: attachment; filename=\"xisoburn-speed-test.bin\"\r\n" +
                          "Cache-Control: no-store\r\n" +
                          "Connection: close\r\n\r\n";
            var h = Encoding.ASCII.GetBytes(head);
            await output.WriteAsync(h, 0, h.Length);
            if (headOnly) { await output.FlushAsync(); return; }

            _log.Info("Web: " + remote + " started the " + TestDownloadMiB + " MiB test download.");
            var chunk = new byte[1048576];
            for (int i = 0; i < chunk.Length; i++) chunk[i] = (byte)(i * 31);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < TestDownloadMiB; i++)
                await output.WriteAsync(chunk, 0, chunk.Length);
            await output.FlushAsync();
            sw.Stop();
            _log.Info("Web: test download to " + remote + ": " + TestDownloadMiB + " MiB in " + sw.Elapsed.TotalSeconds.ToString("0.0") +
                      " s (" + (TestDownloadMiB / Math.Max(0.001, sw.Elapsed.TotalSeconds)).ToString("0.0") + " MiB/s).");
        }

        private string Page()
        {
            string body;
            try { body = _pageBody(); }
            catch (Exception ex) { body = "<p>" + WebUtility.HtmlEncode(ex.Message) + "</p>"; }
            return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"3\">" +
                   "<meta name=\"viewport\" content=\"width=device-width\"><title>xISOBurn</title><style>" +
                   "body{background:#0b0b0b;color:#e8e8e8;font:15px Segoe UI,sans-serif;margin:24px}" +
                   "h1{color:#4cc417;margin:0 0 4px}h2{font-size:16px;color:#9fe07a;margin:18px 0 6px}" +
                   "pre{background:#161616;border:1px solid #2e7d32;padding:10px;white-space:pre-wrap;font:12px Consolas,monospace}" +
                   "a{color:#4cc417}.box{background:#161616;border:1px solid #2e7d32;padding:10px;white-space:pre-wrap}" +
                   "</style></head><body><h1>xISOBurn</h1><div>Web page served by the Xbox (milestone 3b test). Refreshes every 3 s.</div>" +
                   body +
                   "<h2>Speed test</h2><p><a href=\"/test.bin\">Download " + TestDownloadMiB + " MiB of test data</a> " +
                   "(the browser shows the speed; the app logs it too).</p></body></html>";
        }

        public void Dispose()
        {
            foreach (var l in _listeners) l.Dispose();
            _listeners.Clear();
        }
    }
}
