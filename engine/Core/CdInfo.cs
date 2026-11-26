using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn.Core
{
    /// <summary>Names of one track of an audio CD.</summary>
    internal sealed class CdTrackInfo
    {
        public string Title = "", Artist = "", Isrc = "";
    }

    /// <summary>How DiscProbe looks up the names of an audio CD (set by the app from its settings).</summary>
    internal sealed class CdLookup
    {
        /// <summary>Folder for the cache of names, one file per MusicBrainz disc ID (e.g. data\cdinfo).</summary>
        public string CacheFolder;
        /// <summary>Ask MusicBrainz when the disc's CD-TEXT does not have everything.</summary>
        public bool Online;
    }

    /// <summary>
    /// Album, artist and track names of an audio CD: from the disc's CD-TEXT, then from MusicBrainz for
    /// what is missing, kept in a small cache per disc ID. Used for the image name and the cue sheet.
    /// </summary>
    internal sealed class CdInfo
    {
        public string Album = "", Artist = "", Catalog = "";
        /// <summary>Track names by track number.</summary>
        public readonly Dictionary<int, CdTrackInfo> Tracks = new Dictionary<int, CdTrackInfo>();
        /// <summary>Where the names came from, for the log ("CD-TEXT", "MusicBrainz", "cache").</summary>
        public string Source = "";
        public string MusicBrainzDiscId = "";

        public bool HasAlbum { get { return Album.Length > 0; } }

        public CdTrackInfo Track(int number)
        {
            CdTrackInfo t;
            if (!Tracks.TryGetValue(number, out t)) { t = new CdTrackInfo(); Tracks[number] = t; }
            return t;
        }

        /// <summary>Every track of the TOC has a title.</summary>
        public bool CompleteFor(CdToc toc)
        {
            if (!HasAlbum) return false;
            foreach (var t in toc.Tracks)
            {
                CdTrackInfo i;
                if (t.Audio && (!Tracks.TryGetValue(t.Number, out i) || i.Title.Length == 0)) return false;
            }
            return true;
        }

        /// <summary>Fills what is empty here from 'other' (this one wins).</summary>
        public void FillFrom(CdInfo other)
        {
            if (Album.Length == 0) Album = other.Album;
            if (Artist.Length == 0) Artist = other.Artist;
            if (Catalog.Length == 0) Catalog = other.Catalog;
            foreach (var kv in other.Tracks)
            {
                var t = Track(kv.Key);
                if (t.Title.Length == 0) t.Title = kv.Value.Title;
                if (t.Artist.Length == 0) t.Artist = kv.Value.Artist;
                if (t.Isrc.Length == 0) t.Isrc = kv.Value.Isrc;
            }
            if (other.Source.Length > 0) Source = Source.Length == 0 ? other.Source : Source + " + " + other.Source;
        }

        /// <summary>The image name: "Artist - Album", or the album alone.</summary>
        public string DisplayName
        {
            get { return Artist.Length > 0 && Artist != Album ? Artist + " - " + Album : Album; }
        }

        public string Describe()
        {
            int titled = 0;
            foreach (var kv in Tracks) if (kv.Value.Title.Length > 0) titled++;
            return DisplayName + " (" + titled + " track title(s), from " + Source + ")";
        }

        // ------------------------------------------------------------------ cache

        private static string Clean(string s)
        {
            return (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.Append("# xISOBurn CD names (album, artist, tracks). Edit it to correct the names; delete it to look them up again.\n");
            sb.Append("source=").Append(Clean(Source)).Append('\n');
            sb.Append("album=").Append(Clean(Album)).Append('\n');
            sb.Append("artist=").Append(Clean(Artist)).Append('\n');
            if (Catalog.Length > 0) sb.Append("catalog=").Append(Clean(Catalog)).Append('\n');
            var numbers = new List<int>(Tracks.Keys);
            numbers.Sort();
            foreach (int n in numbers)
            {
                var t = Tracks[n];
                if (t.Title.Length > 0) sb.Append("track.").Append(n).Append(".title=").Append(Clean(t.Title)).Append('\n');
                if (t.Artist.Length > 0) sb.Append("track.").Append(n).Append(".artist=").Append(Clean(t.Artist)).Append('\n');
                if (t.Isrc.Length > 0) sb.Append("track.").Append(n).Append(".isrc=").Append(Clean(t.Isrc)).Append('\n');
            }
            return sb.ToString();
        }

        public static CdInfo FromText(string text)
        {
            var info = new CdInfo();
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq), value = line.Substring(eq + 1).Trim();
                if (key == "source") info.Source = value;
                else if (key == "album") info.Album = value;
                else if (key == "artist") info.Artist = value;
                else if (key == "catalog") info.Catalog = value;
                else if (key.StartsWith("track."))
                {
                    var parts = key.Split('.');
                    int n;
                    if (parts.Length != 3 || !int.TryParse(parts[1], out n)) continue;
                    var t = info.Track(n);
                    if (parts[2] == "title") t.Title = value;
                    else if (parts[2] == "artist") t.Artist = value;
                    else if (parts[2] == "isrc") t.Isrc = value;
                }
            }
            return info;
        }

        // ------------------------------------------------------------------ lookup

        /// <summary>
        /// The names of an audio CD: the cache (by MusicBrainz disc ID), else the CD-TEXT, completed by
        /// MusicBrainz when allowed. Returns null when nothing names the album. Never throws.
        /// </summary>
        public static async Task<CdInfo> LookupAsync(OpticalDrive drive, CdToc toc, uint timeout, CdLookup lookup, Logger log)
        {
            string discId = null;
            try { discId = MusicBrainz.DiscId(toc); }
            catch (Exception ex) { log.Info("CD names: no disc ID (" + ex.Message + ")."); }

            string cachePath = discId != null && !string.IsNullOrEmpty(lookup.CacheFolder)
                ? Path.Combine(lookup.CacheFolder, discId + ".txt") : null;
            if (cachePath != null && File.Exists(cachePath))
            {
                try
                {
                    var cached = FromText(File.ReadAllText(cachePath, Encoding.UTF8));
                    cached.MusicBrainzDiscId = discId;
                    if (cached.HasAlbum)
                    {
                        cached.Source = (cached.Source.Length > 0 ? cached.Source : "?") + ", cached";
                        log.Info("CD names: " + cached.Describe() + ". Cache: " + cachePath);
                        return cached;
                    }
                }
                catch (Exception ex)
                {
                    log.Info("CD names: the cache file could not be read (" + ex.Message + ").");
                }
            }

            var info = new CdInfo { MusicBrainzDiscId = discId ?? "" };
            try
            {
                var text = await CdText.ReadAsync(drive, timeout, log);
                if (text != null) info.FillFrom(text);
            }
            catch (Exception ex)
            {
                log.Info("CD-TEXT: " + ex.Message);
            }

            if (!info.CompleteFor(toc) && discId != null)
            {
                if (lookup.Online)
                {
                    try
                    {
                        var mb = await MusicBrainz.LookupAsync(discId, toc, log);
                        if (mb != null) info.FillFrom(mb);
                    }
                    catch (Exception ex)
                    {
                        log.Info("MusicBrainz: lookup failed (" + ex.Message + ").");
                    }
                }
                else
                {
                    log.Info("CD names: the online lookup (MusicBrainz) is off in Settings.");
                }
            }

            if (!info.HasAlbum)
            {
                log.Info("CD names: not found" + (discId != null ? " (MusicBrainz disc ID " + discId + ")" : "") +
                         ". The image keeps the default name; set \"File name\" in Settings to choose one.");
                return null;
            }
            log.Info("CD names: " + info.Describe() + ".");
            if (cachePath != null)
            {
                try
                {
                    Directory.CreateDirectory(lookup.CacheFolder);
                    File.WriteAllText(cachePath, info.ToText(), new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    log.Info("CD names: could not save the cache (" + ex.Message + ").");
                }
            }
            return info;
        }
    }
}
