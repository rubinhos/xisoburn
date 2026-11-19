using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using xISOBurn.Core;
using xISOBurn.Util;

namespace xISOBurn.Tools
{
    /// <summary>
    /// Serves the Xbox app's web page (the real WebServer) on example copies, for the README screenshots:
    ///   - HOME_VIDEOS: a finished DVD copy (its checksums are computed from the example file);
    ///   - MUSIC_CD: a finished audio CD (.bin + .cue);
    ///   - MY_MOVIE: a CSS DVD-9 being copied (62% read), shown under "Now on the Xbox".
    /// Usage: WebScreenshots [port] [seconds]. The page is at http://127.0.0.1:port/ (no PIN).
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            int port = args.Length > 0 ? int.Parse(args[0]) : 8770;
            int seconds = args.Length > 1 ? int.Parse(args[1]) : 120;
            string folder = Path.Combine(Path.GetTempPath(), "xisoburn-web-example");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);

            var cfg = new CopyConfig { WebPort = port, WebPinRequired = false, WebPin = "4821" };
            cfg.Save(folder);
            var log = new Logger(folder, "xisoburn", null);
            log.Info("xISOBurn started.");

            Finished(folder, "HOME_VIDEOS", ".iso", 2291424, 2048);
            Finished(folder, "MUSIC_CD", ".bin", 316723, 2352);
            File.WriteAllText(Path.Combine(folder, "MUSIC_CD.cue"),
                "FILE \"MUSIC_CD.bin\" BINARY\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n");
            var live = InProgress(folder, "MY_MOVIE", 3867872, 2413552);
            LiveImages.Begin("MY_MOVIE.iso", live);

            Func<string, IDictionary<string, string>, string, Task<RemoteReply>> remote =
                (action, form, who) => Task.FromResult(RemoteReply.Refused("This is an example page."));
            var web = new WebServer(log, folder, StatusHtml, () => CopyConfig.Load(folder, out _), remote);
            if (!web.StartAsync(port).GetAwaiter().GetResult())
            {
                Console.Error.WriteLine("Could not listen on port " + port);
                return 1;
            }
            Console.WriteLine("Serving http://127.0.0.1:" + port + "/ for " + seconds + " s");
            Thread.Sleep(seconds * 1000);
            web.Dispose();
            return 0;
        }

        /// <summary>A finished copy: the image, its map, .sha256 and .hashes (computed from the file).</summary>
        private static void Finished(string folder, string name, string ext, long sectors, int sectorBytes)
        {
            string image = Path.Combine(folder, name + ext);
            using (var f = new FileStream(image, FileMode.Create)) f.SetLength(sectors * sectorBytes);
            var map = new SectorStateMap(sectors, name);
            map.Set(0, sectors, SectorState.Ok, null);

            var h = new ImageHashes { Size = sectors * sectorBytes };
            var crc = new Crc32();
            using (var md5 = MD5.Create())
            using (var sha1 = SHA1.Create())
            using (var sha256 = SHA256.Create())
            using (var f = File.OpenRead(image))
            {
                var chunk = new byte[4 << 20];
                int n;
                while ((n = f.Read(chunk, 0, chunk.Length)) > 0)
                {
                    crc.Append(chunk, 0, n);
                    md5.TransformBlock(chunk, 0, n, null, 0);
                    sha1.TransformBlock(chunk, 0, n, null, 0);
                    sha256.TransformBlock(chunk, 0, n, null, 0);
                }
                md5.TransformFinalBlock(chunk, 0, 0);
                sha1.TransformFinalBlock(chunk, 0, 0);
                sha256.TransformFinalBlock(chunk, 0, 0);
                h.Crc32 = crc.Value.ToString("x8");
                h.Md5 = Bytes.Hex(md5.Hash);
                h.Sha1 = Bytes.Hex(sha1.Hash);
                h.Sha256 = Bytes.Hex(sha256.Hash);
            }
            File.WriteAllText(Path.Combine(folder, name + ".map"), map.ToText(name + ext, h.Sha256));
            File.WriteAllText(Path.Combine(folder, name + ".sha256"), h.Sha256 + " *" + name + ext + "\n");
            File.WriteAllText(Path.Combine(folder, name + ImageHashes.Extension), h.ToText(name + ext));
        }

        /// <summary>A copy in progress: the first 'ok' sectors read, the rest not tried yet.</summary>
        private static SectorStateMap InProgress(string folder, string name, long sectors, long ok)
        {
            using (var f = new FileStream(Path.Combine(folder, name + ".iso"), FileMode.Create)) f.SetLength(sectors * 2048);
            var map = new SectorStateMap(sectors, name);
            map.Set(0, ok, SectorState.Ok, null);
            File.WriteAllText(Path.Combine(folder, name + ".map"), map.ToText(name + ".iso", null));
            return map;
        }

        /// <summary>The "Now on the Xbox" part, as MainPage.UpdateWebBody builds it during a copy.</summary>
        private static string StatusHtml()
        {
            Func<string, string> enc = System.Net.WebUtility.HtmlEncode;
            var probe = new DiscProbe
            {
                Ready = true, Profile = 0x0010, VolumeLabel = "MY_MOVIE", Udf = true, Cpst = 1,
                TotalSectors = 3867872,
            };
            for (int i = 0; i < 42; i++) probe.Files.Add(new DiscFile { Path = "VIDEO_TS/FILE" + i, Lba = i, Size = 2048 });
            string disc = "DVD-ROM, 2 layers (OTP)\n" + string.Join("\n", new List<string>(probe.Describe()).GetRange(1, 3));
            string progress =
                "Pass 1: fast copy: 2,413,552 / 3,867,872 (62.4%) | LBA 2,413,552\n" +
                "Speed: 10.21 MiB/s | Elapsed: 00:07:42 | Phase remaining: 00:04:38\n" +
                "Disc: 62.400% read | bad 0 | pending 0 | not tried 1,454,320";
            string status = "Copying to the Xbox (CSS disc: decrypting the video)... do not leave the app. B pauses " +
                            "(progress is kept). The download can already start on the web page.";
            Func<string, string, bool, string> button = (action, label, enabled) =>
                "<button name=\"do\" value=\"" + action + "\"" + (enabled ? "" : " disabled") + ">" + label + "</button>";
            return "<h2>Now on the Xbox</h2>" +
                   "<form method=\"post\" action=\"/action\" class=\"controls\">" +
                   button("detect", "Detect disc", false) + button("copy", "Copy / resume", false) +
                   button("cancel", "Pause / cancel", true) + button("retry", "Retry failed", false) +
                   button("scan", "Scan disc", false) +
                   "<span class=\"dim\">Settings (unavailable while the app is busy or the dialog is open on the Xbox)</span></form>" +
                   "<div class=\"box\"><b>Disc</b>\n" + enc(disc) + "</div>" +
                   "<div class=\"box\"><b>Progress</b>\n" + enc(progress) + "</div>" +
                   "<div class=\"box\">" + enc(status) + "</div>" +
                   "<div class=\"dim\">71.4 GiB free on the Xbox (app storage)</div>" +
                   "<details id=\"recentlog\"><summary>Recent log</summary><pre></pre></details>";
        }
    }
}
