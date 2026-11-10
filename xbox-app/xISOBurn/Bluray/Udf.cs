// SPDX-License-Identifier: GPL-2.0-or-later
// Minimal read-only UDF reader (ECMA-167 / OSTA UDF 1.02 to 2.60) for BD-ROM discs: anchor at sector
// 256, partition and logical volume descriptors, type 1 and metadata (type 2) partition maps, file
// entries / extended file entries with short, long or embedded allocation descriptors.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using xISOBurn.Util;

namespace xISOBurn.Bluray
{
    /// <summary>A run of sectors of a file: LBA on the disc, and where it starts in the file.</summary>
    internal struct UdfExtent
    {
        public long Lba;
        public long Sectors;
        public long FileOffset;   // bytes
        public long Length;       // bytes
    }

    internal sealed class UdfFile
    {
        public string Path;       // "BDMV/STREAM/00001.m2ts"
        public long Size;
        public bool IsDirectory;
        public List<UdfExtent> Extents = new List<UdfExtent>();
        public byte[] Embedded;   // data stored inside the file entry, if any
    }

    /// <summary>Reads 'count' sectors at 'lba' into buf[0..]; false on a read error.</summary>
    internal delegate Task<bool> SectorReader(long lba, int count, byte[] buf);

    internal sealed class UdfReader
    {
        private const int Block = 2048;
        private readonly SectorReader _read;

        private sealed class Partition
        {
            public int Number;
            public long Start, Length;
        }

        private sealed class PartitionMap
        {
            public int Type;               // 1 physical, 2 metadata
            public int PartitionNumber;
            public long MetadataFileLbn;
            public List<UdfExtent> MetadataExtents; // metadata partition block -> physical lbn
        }

        private readonly List<Partition> _partitions = new List<Partition>();
        private readonly List<PartitionMap> _maps = new List<PartitionMap>();
        private long _fsdLbn;
        private int _fsdPartition;

        public string VolumeId { get; private set; }
        public readonly List<UdfFile> Files = new List<UdfFile>();

        public UdfReader(SectorReader read) { _read = read; }

        private async Task<byte[]> ReadBlocksAsync(long lba, int count)
        {
            var buf = new byte[count * Block];
            if (!await _read(lba, count, buf)) return null;
            return buf;
        }

        private static int Tag(byte[] b, int o) { return Bytes.LE16(b, o); }

        private static ulong LE64(byte[] b, int o)
        {
            return Bytes.LE32(b, o) | ((ulong)Bytes.LE32(b, o + 4) << 32);
        }

        /// <summary>Reads the volume structure and the whole directory tree. Returns null on success, else the reason.</summary>
        public async Task<string> OpenAsync()
        {
            var avdp = await ReadBlocksAsync(256, 1);
            if (avdp == null) return "cannot read sector 256";
            if (Tag(avdp, 0) != 2) return "no anchor volume descriptor at sector 256";
            uint vdsLen = Bytes.LE32(avdp, 16), vdsLoc = Bytes.LE32(avdp, 20);
            int vdsBlocks = (int)Math.Min(64, (vdsLen + Block - 1) / Block);
            var vds = await ReadBlocksAsync(vdsLoc, vdsBlocks);
            if (vds == null) return "cannot read the volume descriptor sequence";

            byte[] lvd = null;
            int lvdOffset = 0;
            for (int i = 0; i < vdsBlocks; i++)
            {
                int o = i * Block;
                int tag = Tag(vds, o);
                if (tag == 8) break; // terminating descriptor
                if (tag == 5)
                {
                    _partitions.Add(new Partition
                    {
                        Number = Bytes.LE16(vds, o + 22),
                        Start = Bytes.LE32(vds, o + 188),
                        Length = Bytes.LE32(vds, o + 192),
                    });
                }
                else if (tag == 6 && lvd == null)
                {
                    lvd = vds;
                    lvdOffset = o;
                }
                else if (tag == 1)
                {
                    VolumeId = DString(vds, o + 24, 32);
                }
            }
            if (lvd == null) return "no logical volume descriptor";
            if (_partitions.Count == 0) return "no partition descriptor";
            if (Bytes.LE32(lvd, lvdOffset + 212) != Block) return "logical block size is not 2048";
            if (string.IsNullOrEmpty(VolumeId)) VolumeId = DString(lvd, lvdOffset + 84, 128);

            _fsdLbn = Bytes.LE32(lvd, lvdOffset + 248 + 4);
            _fsdPartition = Bytes.LE16(lvd, lvdOffset + 248 + 8);
            int numMaps = (int)Bytes.LE32(lvd, lvdOffset + 268);
            int p = lvdOffset + 440;
            for (int i = 0; i < numMaps && p + 2 <= lvdOffset + Block; i++)
            {
                int type = lvd[p], len = lvd[p + 1];
                if (len == 0) break;
                var map = new PartitionMap { Type = type };
                if (type == 1)
                {
                    map.PartitionNumber = Bytes.LE16(lvd, p + 4);
                }
                else if (type == 2)
                {
                    string ident = Encoding.ASCII.GetString(lvd, p + 5, 23).TrimEnd('\0');
                    map.PartitionNumber = Bytes.LE16(lvd, p + 38);
                    if (ident.StartsWith("UDF Metadata Partition"))
                        map.MetadataFileLbn = Bytes.LE32(lvd, p + 40);
                    else
                        map.Type = 0; // virtual / sparable: not used on BD-ROM
                }
                _maps.Add(map);
                p += len;
            }

            // Metadata partitions: read the metadata file, which maps metadata blocks to the physical partition.
            for (int i = 0; i < _maps.Count; i++)
            {
                var m = _maps[i];
                if (m.Type != 2) continue;
                int phys = PhysicalMapFor(m.PartitionNumber);
                if (phys < 0) return "metadata partition without its physical partition";
                var fe = await ReadBlocksAsync(PhysicalLba(phys, m.MetadataFileLbn), 1);
                if (fe == null) return "cannot read the metadata file";
                var mf = new UdfFile();
                var ads = new List<Ad>();
                if (!ParseFileEntry(fe, phys, mf, ads)) return "bad metadata file entry";
                await ExpandAdsAsync(ads);
                m.MetadataExtents = new List<UdfExtent>();
                long blockOffset = 0;
                foreach (var ad in ads)
                {
                    long blocks = (ad.Length + Block - 1) / Block;
                    if (ad.Kind == 0)
                        m.MetadataExtents.Add(new UdfExtent { Lba = ad.Lbn, Sectors = blocks, FileOffset = blockOffset });
                    blockOffset += blocks;
                }
            }

            var fsd = await ReadBlocksAsync(ToLba(_fsdPartition, _fsdLbn), 1);
            if (fsd == null || Tag(fsd, 0) != 256) return "no file set descriptor";
            long rootLbn = Bytes.LE32(fsd, 400 + 4);
            int rootPart = Bytes.LE16(fsd, 400 + 8);
            await WalkAsync("", rootPart, rootLbn, 0);
            Files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return null;
        }

        private int PhysicalMapFor(int partitionNumber)
        {
            for (int i = 0; i < _maps.Count; i++)
                if (_maps[i].Type == 1 && _maps[i].PartitionNumber == partitionNumber) return i;
            return -1;
        }

        private long PhysicalLba(int mapIndex, long lbn)
        {
            int number = _maps[mapIndex].PartitionNumber;
            foreach (var p in _partitions)
                if (p.Number == number) return p.Start + lbn;
            return _partitions[0].Start + lbn;
        }

        /// <summary>Disc LBA of logical block 'lbn' of partition reference 'part'; -1 if unmapped.</summary>
        private long ToLba(int part, long lbn)
        {
            if (part < 0 || part >= _maps.Count) part = 0;
            var m = _maps[part];
            if (m.Type == 2 && m.MetadataExtents != null)
            {
                int phys = PhysicalMapFor(m.PartitionNumber);
                foreach (var e in m.MetadataExtents)
                    if (lbn >= e.FileOffset && lbn < e.FileOffset + e.Sectors)
                        return PhysicalLba(phys, e.Lba + (lbn - e.FileOffset));
                return -1;
            }
            return PhysicalLba(part, lbn);
        }

        // ------------------------------------------------------------ file entries

        private struct Ad
        {
            public int Kind;         // 0 recorded, 1 allocated, 2 not allocated, 3 continuation
            public long Length;
            public long Lbn;
            public int Partition;
            public int AdType;       // 0 short, 1 long, 2 extended (for continuation blocks)
        }

        /// <summary>Parses a file entry (261) or extended file entry (266). Data descriptors go into 'ads'.</summary>
        private static bool ParseFileEntry(byte[] b, int partition, UdfFile f, List<Ad> ads)
        {
            int tag = Tag(b, 0);
            int eaLen, adLen, adStart;
            if (tag == 261)
            {
                eaLen = (int)Bytes.LE32(b, 168);
                adLen = (int)Bytes.LE32(b, 172);
                adStart = 176 + eaLen;
            }
            else if (tag == 266)
            {
                eaLen = (int)Bytes.LE32(b, 208);
                adLen = (int)Bytes.LE32(b, 212);
                adStart = 216 + eaLen;
            }
            else return false;
            if (adStart < 0 || adLen < 0 || adStart + adLen > b.Length) return false;
            f.Size = (long)LE64(b, 56);
            int fileType = b[16 + 11];
            f.IsDirectory = fileType == 4;
            int adType = Bytes.LE16(b, 16 + 18) & 7;
            ParseAds(b, adStart, adLen, adType, partition, f, ads);
            return true;
        }

        private static void ParseAds(byte[] b, int start, int len, int adType, int partition, UdfFile f, List<Ad> ads)
        {
            if (adType == 3)
            {
                f.Embedded = new byte[len];
                Buffer.BlockCopy(b, start, f.Embedded, 0, len);
                return;
            }
            int size = adType == 0 ? 8 : adType == 1 ? 16 : 20;
            for (int o = start; o + size <= start + len; o += size)
            {
                uint raw = Bytes.LE32(b, o);
                var ad = new Ad { Kind = (int)(raw >> 30), Length = raw & 0x3FFFFFFF, AdType = adType };
                if (ad.Length == 0) break;
                if (adType == 0)
                {
                    ad.Lbn = Bytes.LE32(b, o + 4);
                    ad.Partition = partition;
                }
                else if (adType == 1)
                {
                    ad.Lbn = Bytes.LE32(b, o + 4);
                    ad.Partition = Bytes.LE16(b, o + 8);
                }
                else
                {
                    // extended_ad: length, recorded length, information length, long_ad location
                    ad.Lbn = Bytes.LE32(b, o + 12);
                    ad.Partition = Bytes.LE16(b, o + 16);
                }
                ads.Add(ad);
            }
        }

        /// <summary>Follows continuation extents (type 3: next block of allocation descriptors).</summary>
        private async Task ExpandAdsAsync(List<Ad> ads)
        {
            for (int guard = 0; guard < 64; guard++)
            {
                int idx = ads.FindIndex(a => a.Kind == 3);
                if (idx < 0) return;
                var cont = ads[idx];
                ads.RemoveAt(idx);
                long lba = ToLba(cont.Partition, cont.Lbn);
                if (lba < 0) return;
                var blk = await ReadBlocksAsync(lba, 1);
                if (blk == null || Tag(blk, 0) != 258) return; // allocation extent descriptor
                int adLen = (int)Bytes.LE32(blk, 20);
                // The AED does not store the AD type: continuation blocks keep the type of the entry.
                var more = new List<Ad>();
                ParseAds(blk, 24, Math.Min(adLen, Block - 24), cont.AdType, cont.Partition, new UdfFile(), more);
                ads.InsertRange(idx, more);
            }
        }

        private async Task<UdfFile> ReadEntryAsync(string path, int part, long lbn, List<Ad> adsOut)
        {
            long lba = ToLba(part, lbn);
            if (lba < 0) return null;
            var fe = await ReadBlocksAsync(lba, 1);
            if (fe == null) return null;
            var f = new UdfFile { Path = path };
            if (!ParseFileEntry(fe, part, f, adsOut)) return null;
            await ExpandAdsAsync(adsOut);
            long offset = 0;
            foreach (var ad in adsOut)
            {
                if (offset >= f.Size) break;
                long len = Math.Min(ad.Length, f.Size - offset);
                if (ad.Kind == 0)
                {
                    // A metadata partition holds only metadata (entries, directories): the data of a
                    // regular file is in the physical partition it is built on.
                    int dataPart = ad.Partition;
                    if (!f.IsDirectory && dataPart >= 0 && dataPart < _maps.Count && _maps[dataPart].Type == 2)
                        dataPart = PhysicalMapFor(_maps[dataPart].PartitionNumber);
                    long start = ToLba(dataPart, ad.Lbn);
                    if (start >= 0)
                        f.Extents.Add(new UdfExtent { Lba = start, Sectors = (len + Block - 1) / Block, FileOffset = offset, Length = len });
                }
                offset += ad.Length;
            }
            return f;
        }

        private async Task WalkAsync(string dirPath, int part, long lbn, int depth)
        {
            if (depth > 16) return;
            var ads = new List<Ad>();
            var dir = await ReadEntryAsync(dirPath, part, lbn, ads);
            if (dir == null) return;
            byte[] data = await ReadAllAsync(dir, 4 * 1024 * 1024);
            if (data == null) return;

            int o = 0;
            while (o + 38 <= data.Length)
            {
                if (Tag(data, o) != 257) break;
                int characteristics = data[o + 18];
                int lfi = data[o + 19];
                long childLbn = Bytes.LE32(data, o + 20 + 4);
                int childPart = Bytes.LE16(data, o + 20 + 8);
                int liu = Bytes.LE16(data, o + 36);
                int nameStart = o + 38 + liu;
                bool parent = (characteristics & 0x08) != 0;
                bool deleted = (characteristics & 0x04) != 0;
                if (!parent && !deleted && lfi > 0 && nameStart + lfi <= data.Length)
                {
                    string name = DName(data, nameStart, lfi);
                    string childPath = dirPath.Length == 0 ? name : dirPath + "/" + name;
                    if ((characteristics & 0x02) != 0)
                    {
                        await WalkAsync(childPath, childPart, childLbn, depth + 1);
                    }
                    else
                    {
                        var child = await ReadEntryAsync(childPath, childPart, childLbn, new List<Ad>());
                        if (child != null) Files.Add(child);
                    }
                }
                int total = 38 + liu + lfi;
                o += (total + 3) & ~3;
            }
        }

        // ------------------------------------------------------------ data

        public UdfFile Find(string path)
        {
            foreach (var f in Files)
                if (string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        /// <summary>The whole file, or null if it is larger than maxBytes or cannot be read.</summary>
        public async Task<byte[]> ReadAllAsync(UdfFile f, long maxBytes)
        {
            if (f.Size > maxBytes || f.Size < 0) return null;
            var data = new byte[f.Size];
            if (f.Embedded != null)
            {
                Buffer.BlockCopy(f.Embedded, 0, data, 0, (int)Math.Min(f.Size, f.Embedded.Length));
                return data;
            }
            int got = await ReadAsync(f, 0, data, 0, (int)f.Size);
            return got == f.Size ? data : null;
        }

        /// <summary>Reads 'count' bytes of the file at 'offset' into buf; returns the bytes read.</summary>
        public async Task<int> ReadAsync(UdfFile f, long offset, byte[] buf, int bufOffset, int count)
        {
            if (offset >= f.Size) return 0;
            count = (int)Math.Min(count, f.Size - offset);
            if (f.Embedded != null)
            {
                int n = (int)Math.Max(0, Math.Min(count, f.Embedded.Length - offset));
                Buffer.BlockCopy(f.Embedded, (int)offset, buf, bufOffset, n);
                return n;
            }
            int done = 0;
            while (done < count)
            {
                long pos = offset + done;
                UdfExtent ext = default(UdfExtent);
                bool found = false;
                foreach (var e in f.Extents)
                    if (pos >= e.FileOffset && pos < e.FileOffset + e.Length) { ext = e; found = true; break; }
                if (!found)
                {
                    // sparse (not recorded) part of the file: zeros
                    long next = f.Size;
                    foreach (var e in f.Extents) if (e.FileOffset > pos && e.FileOffset < next) next = e.FileOffset;
                    int z = (int)Math.Min(count - done, next - pos);
                    Array.Clear(buf, bufOffset + done, z);
                    done += z;
                    continue;
                }
                long inExt = pos - ext.FileOffset;
                long firstSector = ext.Lba + inExt / Block;
                int skip = (int)(inExt % Block);
                int want = (int)Math.Min(count - done, ext.Length - inExt);
                int sectors = Math.Min(32, (skip + want + Block - 1) / Block);
                var tmp = await ReadBlocksAsync(firstSector, sectors);
                if (tmp == null) return done;
                int n2 = Math.Min(want, sectors * Block - skip);
                Buffer.BlockCopy(tmp, skip, buf, bufOffset + done, n2);
                done += n2;
            }
            return done;
        }

        // ------------------------------------------------------------ strings

        /// <summary>OSTA compressed unicode (d-characters): first byte 8 = 8-bit, 16 = UTF-16BE.</summary>
        private static string DName(byte[] b, int o, int len)
        {
            if (len < 1) return "";
            int comp = b[o];
            if (comp == 16)
            {
                var sb = new StringBuilder();
                for (int i = 1; i + 1 < len; i += 2) sb.Append((char)((b[o + i] << 8) | b[o + i + 1]));
                return sb.ToString();
            }
            var s = new StringBuilder();
            for (int i = 1; i < len; i++) s.Append((char)b[o + i]);
            return s.ToString();
        }

        /// <summary>dstring: the last byte holds the used length.</summary>
        private static string DString(byte[] b, int o, int fieldLen)
        {
            int used = b[o + fieldLen - 1];
            if (used <= 0 || used > fieldLen - 1) return "";
            return DName(b, o, used).TrimEnd('\0', ' ');
        }
    }
}
