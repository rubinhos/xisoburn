using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;

namespace xISOBurn.Core
{
    /// <summary>The receiver answered with an error (HTTP status + its message).</summary>
    internal sealed class ReceiverException : Exception
    {
        public int Status { get; private set; }
        public ReceiverException(int status, string message) : base(message) { Status = status; }

        /// <summary>401/403/404/409: retrying the same request cannot help (wrong token, not on the LAN, wrong disc...).</summary>
        public bool IsPermanent { get { return Status == 401 || Status == 403 || Status == 404 || Status == 409; } }
    }

    /// <summary>
    /// Client for pc-receiver/xisoburn_receiver.py (protocol described there and in docs/milestone-3.md).
    /// Every chunk carries the SHA-256 of its bytes, computed right after the disc read; the receiver
    /// rejects it if it arrives different, and the app sends it again.
    /// Needs the privateNetworkClientServer capability (the PC is on the local network).
    /// </summary>
    internal sealed class ReceiverClient : IDisposable
    {
        private const int SectorSize = Scsi.OpticalDrive.SectorSize;
        private static readonly HashAlgorithmProvider Sha256 = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);

        private readonly HttpClient _http;
        private readonly Uri _base;
        private readonly string _token;

        public string Endpoint { get { return _base.ToString(); } }

        public ReceiverClient(string host, int port, string token)
        {
            _base = new Uri("http://" + host.Trim() + ":" + port.ToString(CultureInfo.InvariantCulture) + "/");
            _token = token ?? "";
            _http = new HttpClient();
            _http.Timeout = Timeout.InfiniteTimeSpan; // per-request timeouts below
        }

        public Task<JsonObject> HelloAsync()
        {
            return SendAsync(HttpMethod.Get, "hello", null, TimeSpan.FromSeconds(10), null);
        }

        /// <summary>Sector ranges [start, end) the receiver already has for this disc (empty if none).</summary>
        public async Task<List<long[]>> ReceivedRangesAsync(string name, long totalSectors)
        {
            var o = await SendAsync(HttpMethod.Get, "status?" + Query(name, totalSectors), null, TimeSpan.FromSeconds(30), null);
            var list = new List<long[]>();
            if (!o.ContainsKey("received")) return list;
            foreach (var item in o.GetNamedArray("received"))
            {
                var pair = item.GetArray();
                list.Add(new[] { (long)pair.GetNumberAt(0), (long)pair.GetNumberAt(1) });
            }
            return list;
        }

        public async Task PutChunkAsync(string name, long totalSectors, long lba, byte[] buffer, int sectorCount)
        {
            int bytes = sectorCount * SectorSize;
            string sha = CryptographicBuffer.EncodeToHexString(Sha256.HashData(buffer.AsBuffer(0, bytes)));
            var content = new ByteArrayContent(buffer, 0, bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            long first = lba * SectorSize;
            content.Headers.ContentRange = new ContentRangeHeaderValue(first, first + bytes - 1, totalSectors * SectorSize);
            await SendAsync(HttpMethod.Put, "chunk?" + Query(name, totalSectors), content, TimeSpan.FromSeconds(60),
                            req => req.Headers.Add("X-Content-SHA256", sha));
        }

        /// <summary>Sends the map; with complete=true the receiver also hashes the ISO (can take a minute or two).</summary>
        public Task<JsonObject> FinishAsync(string name, long totalSectors, bool complete, string mapText)
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(mapText ?? ""));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            return SendAsync(HttpMethod.Post, "finish?" + Query(name, totalSectors) + "&complete=" + (complete ? "1" : "0"),
                             content, TimeSpan.FromMinutes(15), null);
        }

        private static string Query(string name, long totalSectors)
        {
            return "name=" + Uri.EscapeDataString(name) + "&total_sectors=" + totalSectors.ToString(CultureInfo.InvariantCulture);
        }

        private async Task<JsonObject> SendAsync(HttpMethod method, string path, HttpContent content, TimeSpan timeout,
                                                 Action<HttpRequestMessage> extra)
        {
            using (var cts = new CancellationTokenSource(timeout))
            using (var req = new HttpRequestMessage(method, new Uri(_base, path)))
            {
                req.Headers.Add("X-Token", _token);
                // The UWP HTTP stack (WinRT underneath) may serve GETs from its cache; /status must be live.
                req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                if (content != null) req.Content = content;
                if (extra != null) extra(req);
                HttpResponseMessage resp;
                try
                {
                    resp = await _http.SendAsync(req, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("no answer from " + _base + " within " + timeout.TotalSeconds + " s");
                }
                using (resp)
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    JsonObject o;
                    if (!JsonObject.TryParse(body, out o)) o = null;
                    if (!resp.IsSuccessStatusCode)
                    {
                        string msg = o != null && o.ContainsKey("error") ? o.GetNamedString("error") : body;
                        throw new ReceiverException((int)resp.StatusCode, "HTTP " + (int)resp.StatusCode + ": " + msg);
                    }
                    return o ?? new JsonObject();
                }
            }
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
