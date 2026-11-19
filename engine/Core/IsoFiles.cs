using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn.Core
{
    internal sealed class DiscFile
    {
        public string Path;
        public long Lba;
        public long Size;
        public long Sectors { get { return (Size + 2047) / 2048; } }
        public long End { get { return Lba + Sectors; } } // exclusive
    }

    /// <summary>
    /// Lists the files of the ISO9660 file system (root and one level of subdirectories, which covers
    /// DVD-Video: VIDEO_TS and AUDIO_TS). Used to tell which files, and so which part of the movie,
    /// fall into sectors that could not be read. DVD-Video files are under 1 GiB, so single-extent
    /// ISO9660 records are enough; the UDF file system describes the same extents.
    /// </summary>
    internal static class IsoFiles
    {
        private const int SectorSize = OpticalDrive.SectorSize;
        private const int MaxDirSectors = 32;

        public static async Task<List<DiscFile>> ReadAsync(OpticalDrive drive, byte[] pvd, uint timeout)
        {
            var files = new List<DiscFile>();
            // Root directory record inside the PVD, at offset 156.
            uint rootLba = Bytes.LE32(pvd, 156 + 2);
            uint rootSize = Bytes.LE32(pvd, 156 + 10);
            var root = await ReadDirAsync(drive, rootLba, rootSize, timeout);
            if (root == null) return files;
            foreach (var e in root)
            {
                if (e.IsDir)
                {
                    var sub = await ReadDirAsync(drive, e.Lba, e.Size, timeout);
                    if (sub == null) continue;
                    foreach (var f in sub)
                        if (!f.IsDir) files.Add(new DiscFile { Path = e.Name + "/" + f.Name, Lba = f.Lba, Size = f.Size });
                }
                else
                {
                    files.Add(new DiscFile { Path = e.Name, Lba = e.Lba, Size = e.Size });
                }
            }
            files.Sort((a, b) => a.Lba.CompareTo(b.Lba));
            return files;
        }

        public static DiscFile FileAt(List<DiscFile> files, long lba)
        {
            if (files == null) return null;
            foreach (var f in files)
                if (lba >= f.Lba && lba < f.End) return f;
            return null;
        }

        private sealed class Entry
        {
            public string Name;
            public uint Lba, Size;
            public bool IsDir;
        }

        private static async Task<List<Entry>> ReadDirAsync(OpticalDrive drive, uint lba, uint size, uint timeout)
        {
            int sectors = (int)Math.Min(MaxDirSectors, (size + SectorSize - 1) / SectorSize);
            if (sectors <= 0) return null;
            var data = new byte[sectors * SectorSize];
            var r = await drive.SendAsync(ScsiCommands.Read10(lba, sectors), sectors * SectorSize, data, 0, timeout);
            if (!r.Success) return null;

            var list = new List<Entry>();
            for (int s = 0; s < sectors; s++)
            {
                int o = s * SectorSize;
                int sectorEnd = o + SectorSize;
                while (o < sectorEnd)
                {
                    int len = data[o];
                    if (len == 0 || o + len > sectorEnd) break; // records never cross sectors; rest is padding
                    int nameLen = data[o + 32];
                    if (33 + nameLen <= len)
                    {
                        bool special = nameLen == 1 && (data[o + 33] == 0 || data[o + 33] == 1); // "." and ".."
                        if (!special)
                        {
                            string name = Bytes.Ascii(data, o + 33, nameLen);
                            int semi = name.IndexOf(';');
                            if (semi >= 0) name = name.Substring(0, semi);
                            list.Add(new Entry
                            {
                                Name = name,
                                Lba = Bytes.LE32(data, o + 2),
                                Size = Bytes.LE32(data, o + 10),
                                IsDir = (data[o + 25] & 0x02) != 0,
                            });
                        }
                    }
                    o += len;
                }
            }
            return list;
        }
    }
}
