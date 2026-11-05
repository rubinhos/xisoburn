using System.Collections.Generic;
using System.Text;

namespace xISOBurn.Core
{
    /// <summary>
    /// Ranges of sectors that could not be read and were filled with zeros.
    /// Sectors arrive in ascending order, so contiguous ranges are merged on the fly.
    /// </summary>
    internal sealed class BadSectorMap
    {
        public sealed class Range
        {
            public long Start;
            public long End; // inclusive
            public string LastError;
            public long Count { get { return End - Start + 1; } }
        }

        private readonly List<Range> _ranges = new List<Range>();

        public long TotalSectors { get; private set; }
        public IReadOnlyList<Range> Ranges { get { return _ranges; } }

        public void Add(long lba, string error)
        {
            TotalSectors++;
            if (_ranges.Count > 0)
            {
                var last = _ranges[_ranges.Count - 1];
                if (last.End + 1 == lba)
                {
                    last.End = lba;
                    last.LastError = error;
                    return;
                }
            }
            _ranges.Add(new Range { Start = lba, End = lba, LastError = error });
        }

        /// <summary>
        /// Text .map file. Lines starting with '#' are comments; each data line is
        /// "start-end (count) last error", in 2048-byte LBAs, inclusive range.
        /// </summary>
        public string ToText(string volume, long firstSector, long endSector, long sectorsDone, bool complete, string sha256)
        {
            var sb = new StringBuilder();
            sb.Append("# xISOBurn sector map\n");
            sb.Append("# volume: ").Append(volume).Append('\n');
            sb.Append("# sector = 2048 bytes; inclusive ranges; absolute LBA on the disc\n");
            sb.Append("# requested range: ").Append(firstSector).Append('-').Append(endSector - 1).Append('\n');
            sb.Append("# processed range: ").Append(firstSector).Append('-').Append(firstSector + sectorsDone - 1)
              .Append(complete ? " (complete)" : " (INTERRUPTED)").Append('\n');
            sb.Append("# sectors read successfully: ").Append(sectorsDone - TotalSectors).Append('\n');
            sb.Append("# sectors NOT read (zero-filled in the image): ").Append(TotalSectors)
              .Append(" in ").Append(_ranges.Count).Append(" range(s)\n");
            if (!string.IsNullOrEmpty(sha256))
                sb.Append("# image sha256 (including the zeros): ").Append(sha256).Append('\n');
            sb.Append("# start-end (count) last error\n");
            foreach (var r in _ranges)
                sb.Append(r.Start).Append('-').Append(r.End).Append(" (").Append(r.Count).Append(") ").Append(r.LastError).Append('\n');
            return sb.ToString();
        }
    }
}
