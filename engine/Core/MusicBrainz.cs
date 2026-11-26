using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using xISOBurn.Util;

namespace xISOBurn.Core
{
    /// <summary>
    /// Audio CD names from MusicBrainz (https://musicbrainz.org), the open music database.
    ///
    /// The disc ID is the one of libdiscid: SHA-1 of the text "%02X" first track, "%02X" last track, then
    /// 100 "%08X" offsets (the lead-out first, then each track; LBA + 150, 0 for missing tracks), in
    /// base64 with '+', '/', '=' replaced by '.', '_', '-'. Only the first session counts, as for a CD-Extra
    /// disc in libdiscid. Only that ID is sent, with the User-Agent MusicBrainz asks applications for.
    /// </summary>
    internal static class MusicBrainz
    {
        private const string Service = "https://musicbrainz.org/ws/2/discid/";
        private const string UserAgent = "xISOBurn/1.0 ( https://github.com/rubinhos/xisoburn )";

        public static string DiscId(CdToc toc)
        {
            if (toc.Tracks.Count == 0) throw new InvalidOperationException("no tracks");
            int first = toc.Tracks[0].Number, last = toc.Tracks[toc.Tracks.Count - 1].Number;
            var offsets = new long[100];
            offsets[0] = toc.FirstSessionEnd + 150;
            foreach (var t in toc.Tracks)
                if (t.Number >= 1 && t.Number <= 99) offsets[t.Number] = t.Lba + 150;

            var sb = new StringBuilder();
            sb.Append(first.ToString("X2")).Append(last.ToString("X2"));
            foreach (long o in offsets) sb.Append(o.ToString("X8"));
            byte[] hash;
            using (var sha1 = SHA1.Create()) hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(sb.ToString()));
            return Convert.ToBase64String(hash).Replace('+', '.').Replace('/', '_').Replace('=', '-');
        }

        private static string Credit(object entity)
        {
            var credits = MiniJson.Arr(entity, "artist-credit");
            if (credits == null) return "";
            var sb = new StringBuilder();
            foreach (var c in credits) sb.Append(MiniJson.Str(c, "name")).Append(MiniJson.Str(c, "joinphrase"));
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Looks the disc ID up. The first matching release is used (the others are listed in the log);
        /// its tracks are matched in order to the audio tracks of the TOC. Null if the disc is unknown.
        /// </summary>
        public static async Task<CdInfo> LookupAsync(string discId, CdToc toc, Logger log)
        {
            string url = Service + Uri.EscapeDataString(discId) + "?inc=recordings+artist-credits&cdstubs=no&fmt=json";
            string body;
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
                http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
                var response = await http.GetAsync(url);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    log.Info("MusicBrainz: disc ID " + discId + " is not in the database. You can add it at " +
                             "https://musicbrainz.org/cdtoc/attach?toc=" + Uri.EscapeDataString(TocString(toc)) + " .");
                    return null;
                }
                if (!response.IsSuccessStatusCode)
                {
                    log.Info("MusicBrainz: HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + ".");
                    return null;
                }
                body = await response.Content.ReadAsStringAsync();
            }

            var root = MiniJson.Parse(body);
            var releases = MiniJson.Arr(root, "releases");
            if (releases == null || releases.Count == 0)
            {
                log.Info("MusicBrainz: no release for disc ID " + discId + ".");
                return null;
            }
            if (releases.Count > 1)
            {
                var others = new List<string>();
                for (int i = 1; i < releases.Count && i < 6; i++)
                    others.Add(Credit(releases[i]) + " - " + MiniJson.Str(releases[i], "title") + " (" + MiniJson.Str(releases[i], "date") + ")");
                log.Info("MusicBrainz: " + releases.Count + " releases match; using the first. Others: " + string.Join("; ", others) + ".");
            }

            var release = releases[0];
            var media = MiniJson.Arr(release, "media") ?? new List<object>();
            object medium = null;
            foreach (var m in media)
            {
                var discs = MiniJson.Arr(m, "discs");
                if (discs == null) continue;
                foreach (var d in discs) if (MiniJson.Str(d, "id") == discId) { medium = m; break; }
                if (medium != null) break;
            }
            if (medium == null && media.Count > 0) medium = media[0];

            var info = new CdInfo { Source = "MusicBrainz", MusicBrainzDiscId = discId };
            info.Album = MiniJson.Str(release, "title") ?? "";
            info.Artist = Credit(release);
            string barcode = MiniJson.Str(release, "barcode") ?? "";
            if (barcode.Length == 13) info.Catalog = barcode;
            if (media.Count > 1 && medium != null)
                info.Album += " (disc " + MiniJson.Str(medium, "position") + ")";

            // The tracks of the medium, in order, to the audio tracks of the TOC.
            var tracks = MiniJson.Arr(medium, "tracks") ?? new List<object>();
            int k = 0;
            foreach (var t in toc.Tracks)
            {
                if (!t.Audio) continue;
                if (k >= tracks.Count) break;
                var mt = tracks[k++];
                var ti = info.Track(t.Number);
                ti.Title = MiniJson.Str(mt, "title") ?? "";
                string artist = Credit(mt);
                if (artist.Length > 0 && artist != info.Artist) ti.Artist = artist;
            }
            log.Info("MusicBrainz: \"" + info.Album + "\" by \"" + info.Artist + "\", " + k + " track title(s) (release " +
                     MiniJson.Str(release, "id") + ").");
            return info;
        }

        /// <summary>The TOC in the MusicBrainz "toc" format: first, last, lead-out, then the track offsets.</summary>
        private static string TocString(CdToc toc)
        {
            var sb = new StringBuilder();
            sb.Append(toc.Tracks[0].Number).Append(' ').Append(toc.Tracks[toc.Tracks.Count - 1].Number).Append(' ')
              .Append(toc.FirstSessionEnd + 150);
            foreach (var t in toc.Tracks) sb.Append(' ').Append(t.Lba + 150);
            return sb.ToString();
        }
    }
}
