// SPDX-License-Identifier: LGPL-2.1-or-later
// BD+ conversion table (fixup table). Port of libbdplus src/libbdplus/bdsvm/segment.c,
// Copyright (C) 2008-2010 Accident, (C) 2013 VideoLAN, LGPL-2.1-or-later.
//
// Not ported: the streaming patcher (segment_patch / bdplus_fixup) and the "SEGK" segment-key tables
// that need it. The copy reads sectors in any order, so instead every active entry of a decrypted table
// becomes a fixed (file offset, 5 bytes) patch, applied to each aligned unit after AACS decryption.

using System;
using System.Collections.Generic;
using System.IO;
using xISOBurn.Util;

namespace xISOBurn.Bluray.Bdplus
{
    internal sealed class ConvEntry
    {
        public uint Index;
        public byte Flags;
        public ushort Patch0AddressAdjust; // 12 bits
        public ushort Patch1AddressAdjust; // 12 bits
        public byte Patch0BufferOffset, Patch1BufferOffset;
        public byte[] Patch0 = new byte[5], Patch1 = new byte[5];
        public bool Active;

        public long Offset0 { get { return ((long)Index + Patch0AddressAdjust) * 0xC0 + Patch0BufferOffset; } }
        public long Offset1 { get { return ((long)Index + Patch0AddressAdjust + Patch1AddressAdjust) * 0xC0 + Patch1BufferOffset; } }
    }

    internal sealed class ConvSegment
    {
        public bool Encrypted;
        public ConvEntry[] Entries = new ConvEntry[0];
        public byte[] Mask = new byte[8];
        public byte[] Key = new byte[16];
    }

    internal sealed class ConvSubtable
    {
        public uint TableId;          // m2ts clip number
        public ConvSegment[] Segments = new ConvSegment[0];
    }

    /// <summary>One BD+ patch: 5 bytes written at a byte offset of a .m2ts file.</summary>
    internal struct BdplusPatch
    {
        public long Offset;
        public byte[] Data;
    }

    internal sealed class ConvTable
    {
        public List<ConvSubtable> Tables = new List<ConvSubtable>();
        public uint CurrentTable = 0xFFFFFFFF, CurrentSegment = 0xFFFFFFFF;

        private static bool IsInvalidEntry(ConvEntry e, ConvEntry prev)
        {
            if ((e.Flags >> 6) == 3) return true;
            bool invalid = false;
            if ((e.Flags >> 6) == 1)
            {
                if (e.Patch0BufferOffset < 8 || e.Patch0BufferOffset > 187 ||
                    e.Patch1BufferOffset < 8 || e.Patch1BufferOffset > 187)
                    invalid = true;
                if (e.Offset0 >= e.Offset1 ||
                    (prev != null && (prev.Flags >> 6) == 1 && e.Offset0 < prev.Offset1))
                    invalid = true;
            }
            return invalid;
        }

        public int FindTableIndex(uint m2ts)
        {
            for (int i = 0; i < Tables.Count; i++) if (Tables[i].TableId == m2ts) return i;
            return -1;
        }

        public int NumEntriesOfCurrent()
        {
            int n = 0;
            if (CurrentTable < Tables.Count)
                foreach (var s in Tables[(int)CurrentTable].Segments) n += s.Entries.Length;
            return n;
        }

        public int ValidationErrors()
        {
            int errors = 0;
            foreach (var t in Tables)
                foreach (var s in t.Segments)
                    for (int i = 0; i < s.Entries.Length; i++)
                        if (IsInvalidEntry(s.Entries[i], i == 0 ? null : s.Entries[i - 1])) errors++;
            return errors;
        }

        /// <summary>segment_setTable: decodes a table as sent by TRAP_ConversionTable (or a cached file). Null on error.</summary>
        public static ConvTable Parse(byte[] t, int len)
        {
            if (t == null || len < 2 || len > 0x400000) return null;
            var ct = new ConvTable();
            uint ptr = 0;
            int numTables = Bytes.BE16(t, 0);
            ptr += 2;
            try
            {
                for (int table = 0; table < numTables; table++)
                {
                    if (len < 6 || ptr > len - 6) return null;
                    var st = new ConvSubtable();
                    st.TableId = Bytes.BE32(t, (int)ptr);
                    ptr += 4;
                    int numSegments = Bytes.BE16(t, (int)ptr);
                    ptr += 2;
                    st.Segments = new ConvSegment[numSegments];
                    ct.Tables.Add(st);
                    if (numSegments == 0) continue;
                    uint offset = 0;
                    for (int seg = 0; seg < numSegments; seg++)
                    {
                        var s = new ConvSegment();
                        st.Segments[seg] = s;
                        if (ptr + seg * 4 > len - 4) return null;
                        offset = Bytes.BE32(t, (int)(ptr + seg * 4));
                        if (offset > len - 4) return null;
                        uint numEntries = Bytes.BE32(t, (int)offset);
                        offset += 4;
                        if (numEntries == 0) continue;
                        if (numEntries > (uint)len / 4) return null;
                        s.Entries = new ConvEntry[numEntries];
                        s.Encrypted = true;
                        for (uint e = 0; e < numEntries; e++)
                        {
                            if (offset > len - 4) return null;
                            s.Entries[e] = new ConvEntry { Index = Bytes.BE32(t, (int)offset) };
                            offset += 4;
                        }
                        for (uint e = 0; e < numEntries; e++)
                        {
                            var en = s.Entries[e];
                            if (len < 16 || offset > len - 16) return null;
                            en.Flags = t[offset];
                            offset += 1;
                            uint tmp = Bytes.BE32(t, (int)offset) & 0xFFFFFF00;
                            offset += 3;
                            en.Patch0AddressAdjust = (ushort)((tmp & 0xFFF00000) >> 20);
                            en.Patch1AddressAdjust = (ushort)((tmp & 0x000FFF00) >> 8);
                            en.Patch0BufferOffset = t[offset++];
                            en.Patch1BufferOffset = t[offset++];
                            Buffer.BlockCopy(t, (int)offset, en.Patch0, 0, 5);
                            offset += 5;
                            Buffer.BlockCopy(t, (int)offset, en.Patch1, 0, 5);
                            offset += 5;
                        }
                    }
                    ptr = offset;
                }
            }
            catch (IndexOutOfRangeException) { return null; }
            catch (ArgumentException) { return null; }
            return ct;
        }

        /// <summary>segment_mergeTables: adds the tables of 'other' whose IDs are new.</summary>
        public int Merge(ConvTable other)
        {
            int added = 0;
            foreach (var t2 in other.Tables)
            {
                if (FindTableIndex(t2.TableId) >= 0) continue;
                Tables.Add(t2);
                added++;
            }
            return added;
        }

        /// <summary>segment_activateTable (cached tables): everything decrypted and active.</summary>
        public void Activate()
        {
            foreach (var t in Tables)
                foreach (var s in t.Segments)
                {
                    s.Encrypted = false;
                    foreach (var e in s.Entries) e.Active = true;
                }
        }

        /// <summary>segment_setSegment.</summary>
        public void SetSegment(uint tableId, uint segment)
        {
            int table = FindTableIndex(tableId);
            if (table < 0) table = 0;
            CurrentTable = (uint)table;
            CurrentSegment = segment;
        }

        /// <summary>segment_nextSegment: the next encrypted segment, scanning all tables. False when none is left.</summary>
        public bool NextSegment(out uint tableId, out uint segment)
        {
            tableId = 0;
            segment = 0;
            if (CurrentTable == 0xFFFFFFFF) CurrentTable = 0;
            if (CurrentSegment == 0xFFFFFFFF) CurrentSegment = 0;
            for (uint table = CurrentTable; table < Tables.Count; table++)
            {
                for (uint seg = CurrentSegment; seg < Tables[(int)table].Segments.Length; seg++)
                {
                    if (Tables[(int)table].Segments[seg].Encrypted)
                    {
                        CurrentTable = table;
                        CurrentSegment = seg;
                        tableId = Tables[(int)table].TableId;
                        segment = seg;
                        return true;
                    }
                }
                CurrentSegment = 0;
            }
            CurrentTable = 0;
            return false;
        }

        /// <summary>
        /// segment_decrypt: the VM computed the key (16 bytes) and forensic mask (8 bytes) of the current
        /// segment. Decrypts its entries and marks which ones apply. Returns invalid entries deactivated, or -1.
        /// </summary>
        public int DecryptSegment(byte[] mem, int keyOffset, int maskOffset)
        {
            if (CurrentTable == 0xFFFFFFFF || CurrentSegment == 0xFFFFFFFF) return -1;
            if (CurrentTable >= Tables.Count) return -1;
            var t = Tables[(int)CurrentTable];
            if (CurrentSegment >= t.Segments.Length) return -1;
            var segment = t.Segments[CurrentSegment];
            var key = new byte[16];
            var mask = new byte[8];
            Buffer.BlockCopy(mem, keyOffset, key, 0, 16);
            Buffer.BlockCopy(mem, maskOffset, mask, 0, 8);

            if (!segment.Encrypted)
            {
                if (Bytes.Equal(segment.Key, 0, key, 0, 16)) return -1; // key not changed
                if (!Bytes.AllZero(segment.Key, 0, 16)) return -1;      // old key was not empty
            }
            Buffer.BlockCopy(key, 0, segment.Key, 0, 16);
            Buffer.BlockCopy(mask, 0, segment.Mask, 0, 8);
            segment.Encrypted = false;

            uint tmp = Bytes.BE32(key, 1) & 0xFFFFFF00;
            foreach (var e in segment.Entries)
            {
                e.Flags ^= key[0];
                e.Patch0AddressAdjust ^= (ushort)((tmp & 0xFFF00000) >> 20);
                e.Patch1AddressAdjust ^= (ushort)((tmp & 0x000FFF00) >> 8);
                e.Patch0BufferOffset ^= key[4];
                e.Patch1BufferOffset ^= key[5];
                for (int i = 0; i < 5; i++)
                {
                    e.Patch0[i] ^= key[6 + i];
                    e.Patch1[i] ^= key[11 + i];
                }
            }

            int invalid = 0;
            for (int i = 0; i < segment.Entries.Length; i++)
            {
                var e = segment.Entries[i];
                switch ((e.Flags >> 6) & 3)
                {
                    case 0: e.Active = false; break;          // no transform (fuzzing)
                    case 1: e.Active = true; break;           // transform
                    case 2:                                   // forensic: active if its bit is set in the mask
                        int bits = e.Flags & 0x3f;
                        if ((mask[7 - (bits >> 3)] & (1 << (bits & 7))) != 0)
                        {
                            e.Active = true;
                            continue;
                        }
                        e.Active = false;
                        break;
                    default: e.Active = false; break;         // reserved
                }
                if (IsInvalidEntry(e, i == 0 ? null : segment.Entries[i - 1]))
                {
                    e.Active = false;
                    invalid++;
                }
            }
            return invalid;
        }

        /// <summary>
        /// Patches for one clip, sorted by offset: the active entries of a table whose first segment is
        /// decrypted (segment_set_m2ts refuses the others). Null if there is nothing to patch.
        /// </summary>
        public List<BdplusPatch> PatchesFor(uint m2ts)
        {
            int idx = FindTableIndex(m2ts);
            if (idx < 0) return null;
            var t = Tables[idx];
            int entries = 0;
            foreach (var s in t.Segments) entries += s.Entries.Length;
            if (entries < 1) return null;
            if (t.Segments[0].Encrypted) return null;
            var list = new List<BdplusPatch>();
            foreach (var s in t.Segments)
                foreach (var e in s.Entries)
                {
                    if (!e.Active) continue;
                    list.Add(new BdplusPatch { Offset = e.Offset0, Data = (byte[])e.Patch0.Clone() });
                    list.Add(new BdplusPatch { Offset = e.Offset1, Data = (byte[])e.Patch1.Clone() });
                }
            // Stable sort by offset, so patches at the same offset keep the table order.
            var indexed = new List<KeyValuePair<int, BdplusPatch>>();
            for (int i = 0; i < list.Count; i++) indexed.Add(new KeyValuePair<int, BdplusPatch>(i, list[i]));
            indexed.Sort((a, b) =>
            {
                int c = a.Value.Offset.CompareTo(b.Value.Offset);
                return c != 0 ? c : a.Key.CompareTo(b.Key);
            });
            list.Clear();
            foreach (var kv in indexed) list.Add(kv.Value);
            return list;
        }

        /// <summary>segment_load: a cached full table (convtab file). Null if not usable.</summary>
        public static ConvTable Load(byte[] data, out string error)
        {
            error = null;
            if (data.Length < 2 || data.Length > 0x400000) { error = "invalid size"; return null; }
            if (data.Length > 8 && data[0] == 'S' && data[1] == 'E' && data[2] == 'G' && data[3] == 'K' && data[4] == '0')
            {
                error = "segment key (SEGK) tables are not supported by this port";
                return null;
            }
            var ct = Parse(data, data.Length);
            if (ct == null) { error = "cannot be parsed"; return null; }
            int errors = ct.ValidationErrors();
            if (errors != 0) { error = errors + " invalid entries"; return null; }
            ct.Activate();
            return ct;
        }

        /// <summary>
        /// segment_save, writing only the active entries of decrypted segments, so the file can be
        /// loaded back with Load (which activates every entry).
        /// </summary>
        public byte[] SaveActive()
        {
            var tables = new List<ConvSubtable>(Tables);
            tables.Sort((a, b) => a.TableId.CompareTo(b.TableId));
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Action<uint> be32 = v => { w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v); };
            Action<int> be16 = v => { w.Write((byte)(v >> 8)); w.Write((byte)v); };

            var kept = new List<List<ConvEntry>[]>();
            foreach (var t in tables)
            {
                var segs = new List<ConvEntry>[t.Segments.Length];
                for (int i = 0; i < t.Segments.Length; i++)
                {
                    segs[i] = new List<ConvEntry>();
                    if (t.Segments[i].Encrypted) continue;
                    foreach (var e in t.Segments[i].Entries) if (e.Active) segs[i].Add(e);
                }
                kept.Add(segs);
            }

            be16(tables.Count);
            uint offset = 2;
            for (int ti = 0; ti < tables.Count; ti++)
            {
                var t = tables[ti];
                be32(t.TableId);
                be16(t.Segments.Length);
                offset += 6 + (uint)(4 * t.Segments.Length);
                foreach (var seg in kept[ti])
                {
                    be32(offset);
                    offset += 4 + (uint)(seg.Count * (4 + 16));
                }
                foreach (var seg in kept[ti])
                {
                    be32((uint)seg.Count);
                    foreach (var e in seg) be32(e.Index);
                    foreach (var e in seg)
                    {
                        w.Write(e.Flags);
                        uint u = ((uint)e.Patch0AddressAdjust << 20) | ((uint)e.Patch1AddressAdjust << 8);
                        w.Write((byte)(u >> 24)); w.Write((byte)(u >> 16)); w.Write((byte)(u >> 8));
                        w.Write(e.Patch0BufferOffset);
                        w.Write(e.Patch1BufferOffset);
                        w.Write(e.Patch0);
                        w.Write(e.Patch1);
                    }
                }
            }
            w.Flush();
            return ms.ToArray();
        }
    }
}
