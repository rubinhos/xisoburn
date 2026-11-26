using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn.Core
{
    /// <summary>
    /// CD-TEXT, read with READ TOC/PMA/ATIP format 5 (MMC). The response is a 4-byte header and 18-byte
    /// packs: type (0x80 title, 0x81 performer, 0x8E UPC/ISRC, 0x8F size information), track number
    /// (bit 7 = extension), sequence number, block number / character position, 12 bytes of text and a
    /// CRC. For each type, the texts of the packs follow each other as NUL-terminated strings, one per
    /// track starting at the track number of the first pack (0 = the album); a single TAB means "the same
    /// as the previous track". Only block 0 (the first language) in a single-byte character set is read.
    /// </summary>
    internal static class CdText
    {
        private const int PackSize = 18;

        public static async Task<CdInfo> ReadAsync(OpticalDrive drive, uint timeout, Logger log)
        {
            // The header first, for the length; then everything (at most 8 blocks of 256 packs).
            var head = new byte[4];
            var r = await drive.SendAsync(ScsiCommands.ReadToc(5, 0, head.Length), head.Length, head, 0, timeout);
            if (!r.Success || r.DataLength < 4) { log.Info("CD-TEXT: none on this disc."); return null; }
            int length = Bytes.BE16(head, 0) + 2;
            if (length <= 4 + PackSize) { log.Info("CD-TEXT: none on this disc."); return null; }
            length = System.Math.Min(length, drive.MaxTransferBytes);
            var buf = new byte[length];
            r = await drive.SendAsync(ScsiCommands.ReadToc(5, 0, length), length, buf, 0, timeout);
            if (!r.Success) { log.Info("CD-TEXT: " + r.Describe()); return null; }
            return Parse(buf, r.DataLength, log);
        }

        public static CdInfo Parse(byte[] buf, int length, Logger log)
        {
            var packs = new List<int>(); // offsets of the block 0 packs
            for (int p = 4; p + PackSize <= length; p += PackSize)
                if (((buf[p + 3] >> 4) & 0x07) == 0) packs.Add(p);
            if (packs.Count == 0) return null;

            // Character set, from the size information pack (0x8F, sequence 0 of the block): byte 4.
            foreach (int p in packs)
            {
                if (buf[p] != 0x8F || buf[p + 1] != 0) continue;
                int charset = buf[p + 4];
                if (charset != 0x00 && charset != 0x01)
                {
                    log.Info("CD-TEXT: character set 0x" + charset.ToString("X2") + " (not ISO 8859-1 / ASCII) is not read.");
                    return null;
                }
            }
            foreach (int p in packs)
                if ((buf[p + 3] & 0x80) != 0) { log.Info("CD-TEXT: double-byte text is not read."); return null; }

            var titles = Strings(buf, packs, 0x80);
            var performers = Strings(buf, packs, 0x81);
            var codes = Strings(buf, packs, 0x8E);

            var info = new CdInfo { Source = "CD-TEXT" };
            string v;
            if (titles.TryGetValue(0, out v)) info.Album = v;
            if (performers.TryGetValue(0, out v)) info.Artist = v;
            if (codes.TryGetValue(0, out v) && v.Length == 13 && IsDigits(v)) info.Catalog = v;
            foreach (var kv in titles) if (kv.Key > 0) info.Track(kv.Key).Title = kv.Value;
            foreach (var kv in performers) if (kv.Key > 0) info.Track(kv.Key).Artist = kv.Value;
            foreach (var kv in codes) if (kv.Key > 0 && kv.Value.Length == 12) info.Track(kv.Key).Isrc = kv.Value;
            if (!info.HasAlbum && info.Tracks.Count == 0) return null;
            log.Info("CD-TEXT: \"" + info.Album + "\" by \"" + info.Artist + "\", " + titles.Count + " title(s).");
            return info;
        }

        private static bool IsDigits(string s)
        {
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        /// <summary>The strings of one pack type, by track number (empty ones left out).</summary>
        private static Dictionary<int, string> Strings(byte[] buf, List<int> packs, byte type)
        {
            var result = new Dictionary<int, string>();
            int track = -1;
            var sb = new StringBuilder();
            string previous = "";
            foreach (int p in packs)
            {
                if (buf[p] != type) continue;
                if (track < 0) track = buf[p + 1] & 0x7F;
                for (int i = 4; i < 16; i++)
                {
                    byte b = buf[p + i];
                    if (b != 0) { sb.Append((char)b); continue; } // ISO 8859-1 maps byte = code point
                    string s = sb.ToString();
                    if (s == "\t") s = previous;                   // "same as the previous track"
                    s = s.Trim();
                    if (s.Length > 0 && track <= 99 && !result.ContainsKey(track)) result[track] = s;
                    previous = s;
                    sb.Clear();
                    track++;
                }
            }
            return result;
        }
    }
}
