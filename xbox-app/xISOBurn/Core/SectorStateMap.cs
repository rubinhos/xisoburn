using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace xISOBurn.Core
{
    internal enum SectorState : byte
    {
        Untried = 0, // '?' not read yet (or skipped by pass 1)
        Ok = 1,      // '+' read successfully; its data is in the image
        Pending = 2, // '*' failed in the fast pass; waiting for the scrape pass
        Bad = 3,     // '-' failed in the scrape/retry passes; zeros in the image
    }

    /// <summary>
    /// State of every sector of the disc, one byte per sector (3.3 MB for a DVD-9, 25 MB for a BD-50).
    /// It is the rescue's memory between runs: saved as LocalState\VOLUME.map (run-length encoded text,
    /// similar to a GNU ddrescue mapfile) and loaded again to resume where the last run stopped.
    /// The image file only ever receives data for sectors marked Ok; everything else stays zero.
    /// </summary>
    internal sealed class SectorStateMap
    {
        public const string Magic = "# xISOBurn rescue map v1";
        private static readonly char[] Codes = { '?', '+', '*', '-' };

        private readonly byte[] _state;
        private readonly Dictionary<long, string> _errors = new Dictionary<long, string>();
        private readonly long[] _counts = new long[4];

        public long TotalSectors { get; private set; }
        public string Volume { get; private set; }
        public int RetryPassesDone;
        /// <summary>Where the data of the Ok sectors lives: "local" (LocalState image); "pc" in maps from the old PC receiver.</summary>
        public string Destination = "";

        public SectorStateMap(long totalSectors, string volume)
        {
            TotalSectors = totalSectors;
            Volume = volume ?? "";
            _state = new byte[totalSectors];
            _counts[(int)SectorState.Untried] = totalSectors;
        }

        public long Count(SectorState s) { return _counts[(int)s]; }
        public SectorState Get(long lba) { return (SectorState)_state[lba]; }

        public string ErrorOf(long lba)
        {
            string e;
            return _errors.TryGetValue(lba, out e) ? e : "";
        }

        public void Set(long lba, long count, SectorState s, string error)
        {
            for (long i = lba; i < lba + count; i++)
            {
                _counts[_state[i]]--;
                _state[i] = (byte)s;
                _counts[(int)s]++;
                if (s == SectorState.Pending || s == SectorState.Bad)
                {
                    if (!string.IsNullOrEmpty(error)) _errors[i] = error;
                }
                else
                {
                    _errors.Remove(i);
                }
            }
        }

        /// <summary>First LBA in [from, end) with state s, or end if none.</summary>
        public long NextWith(SectorState s, long from, long end)
        {
            byte b = (byte)s;
            for (long i = from; i < end; i++)
                if (_state[i] == b) return i;
            return end;
        }

        /// <summary>End (exclusive) of the run of identical states that starts at 'from', limited to 'end'.</summary>
        public long RunEnd(long from, long end)
        {
            byte b = _state[from];
            long i = from + 1;
            while (i < end && _state[i] == b) i++;
            return i;
        }

        public long CountIn(SectorState s, long from, long end)
        {
            if (from == 0 && end == TotalSectors) return Count(s);
            byte b = (byte)s;
            long n = 0;
            for (long i = from; i < end; i++) if (_state[i] == b) n++;
            return n;
        }

        public string Summary()
        {
            return "ok " + Count(SectorState.Ok).ToString("N0") + " (" + Percent(Count(SectorState.Ok)) + ")" +
                   " | not tried " + Count(SectorState.Untried).ToString("N0") +
                   " | pending " + Count(SectorState.Pending).ToString("N0") +
                   " | bad " + Count(SectorState.Bad).ToString("N0");
        }

        public string Percent(long n)
        {
            return (TotalSectors > 0 ? n * 100.0 / TotalSectors : 0).ToString("0.000") + "%";
        }

        // ------------------------------------------------------------ persistence

        /// <summary>
        /// Text format. Lines starting with '#' are comments or "# key: value" headers; each data line is
        /// "start count status [last error]", status being ? + * - (see SectorState). Ranges cover the
        /// whole disc in order. Sector = 2048 bytes; LBAs are absolute on the disc.
        /// </summary>
        public string ToText(string imageName, string sha256)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(Magic).Append('\n');
            sb.Append("# volume: ").Append(Volume).Append('\n');
            sb.Append("# total_sectors: ").Append(TotalSectors.ToString(inv)).Append('\n');
            sb.Append("# image: ").Append(imageName ?? "(not written)").Append('\n');
            sb.Append("# destination: ").Append(Destination).Append('\n');
            sb.Append("# retry_passes_done: ").Append(RetryPassesDone.ToString(inv)).Append('\n');
            sb.Append("# written: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", inv)).Append('\n');
            sb.Append("# summary: ").Append(Summary()).Append('\n');
            if (!string.IsNullOrEmpty(sha256))
                sb.Append("# image_sha256 (zeros included): ").Append(sha256).Append('\n');
            sb.Append("# status: + read OK | ? not tried yet | * failed once, pending retry | - unreadable so far (zeros in the image)\n");
            sb.Append("# start count status [last error]\n");

            long pos = 0;
            while (pos < TotalSectors)
            {
                long end = RunEnd(pos, TotalSectors);
                var s = (SectorState)_state[pos];
                sb.Append(pos.ToString(inv)).Append(' ').Append((end - pos).ToString(inv)).Append(' ').Append(Codes[(int)s]);
                if (s == SectorState.Pending || s == SectorState.Bad)
                {
                    string e = ErrorOf(pos);
                    if (e.Length > 0) sb.Append(' ').Append(e);
                }
                sb.Append('\n');
                pos = end;
            }
            return sb.ToString();
        }

        /// <summary>Writes to a temporary file first, then replaces the map, so a crash never leaves a half-written map.</summary>
        public void Save(string path, string imageName, string sha256)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, ToText(imageName, sha256), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>Loads a map written by ToText. Returns null (with the reason) if it cannot be used.</summary>
        public static SectorStateMap Load(string path, long expectedTotal, out string reason)
        {
            reason = "";
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0].Trim() != Magic)
            {
                reason = "not a rescue map (older format?)";
                return null;
            }
            long total = -1;
            string volume = "";
            string destination = "";
            int passes = 0;
            foreach (var line in lines)
            {
                if (!line.StartsWith("#")) continue;
                string v;
                if (Header(line, "total_sectors", out v)) long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out total);
                else if (Header(line, "volume", out v)) volume = v;
                else if (Header(line, "retry_passes_done", out v)) int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out passes);
                else if (Header(line, "destination", out v)) destination = v;
            }
            if (total != expectedTotal)
            {
                reason = "the map is for " + total + " sectors, the disc has " + expectedTotal;
                return null;
            }

            // Maps written before milestone 3 have no destination line: their data is in LocalState.
            var map = new SectorStateMap(total, volume) { RetryPassesDone = passes, Destination = destination.Length > 0 ? destination : "local" };
            long covered = 0;
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var parts = line.Split(new[] { ' ' }, 4, StringSplitOptions.RemoveEmptyEntries);
                long start, count;
                if (parts.Length < 3 ||
                    !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out start) ||
                    !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) ||
                    parts[2].Length != 1 || start != covered || count <= 0 || start + count > total)
                {
                    reason = "invalid line: " + line;
                    return null;
                }
                int code = Array.IndexOf(Codes, parts[2][0]);
                if (code < 0) { reason = "invalid status in: " + line; return null; }
                if (code != (int)SectorState.Untried)
                    map.Set(start, count, (SectorState)code, parts.Length > 3 ? parts[3] : "");
                covered = start + count;
            }
            if (covered != total)
            {
                reason = "the map covers " + covered + " of " + total + " sectors";
                return null;
            }
            return map;
        }

        private static bool Header(string line, string key, out string value)
        {
            string prefix = "# " + key + ":";
            if (line.StartsWith(prefix))
            {
                value = line.Substring(prefix.Length).Trim();
                return true;
            }
            value = null;
            return false;
        }
    }
}
