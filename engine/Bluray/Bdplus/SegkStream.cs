// SPDX-License-Identifier: LGPL-2.1-or-later
// BD+ stream fixups for segment key ("SEGK") tables. Port of the streaming mode of libbdplus
// src/libbdplus/bdsvm/segment.c (segment_set_m2ts for ct_segment_masks, segment_patchseek,
// ts_parse_desc_0x89 and segment_patch), Copyright (C) 2008-2010 Accident, (C) 2013 VideoLAN,
// LGPL-2.1-or-later. The code follows the C original; the stream is fed in order, one aligned unit
// (6144 bytes, 32 source packets) at a time, as libbluray does during playback.
//
// In this mode the clip itself carries the patches: TS packets on PID 0x100 with a descriptor 0x89
// hold an entry XORed with the segment key. Each entry patches 5 bytes at two places further on in
// the same clip.

using System;
using System.Collections.Generic;

namespace xISOBurn.Bluray.Bdplus
{
    internal sealed class SegkStream
    {
        private const int PacketSize = 192;
        /// <summary>E1_CACHE_SIZE: the queue of pending entries per segment.</summary>
        private const int CacheSize = 50;

        private readonly ConvSubtable _table;
        private readonly List<ConvEntry>[] _entries; // the entries injected from the stream, per segment
        private uint _streamSegment, _streamEntry;
        private ulong _streamOffset, _nextPatchOffset;

        /// <summary>Segments of this clip without a key (it is common for the first one to be empty).</summary>
        public int MissingKeys { get; private set; }
        public long Injected { get; private set; }

        /// <summary>segment_set_m2ts for a segment key table. Null if the table has nothing for this clip.</summary>
        public static SegkStream ForClip(ConvTable ct, uint m2ts)
        {
            if (ct == null || !ct.SegmentMasks) return null;
            int idx = ct.FindTableIndex(m2ts);
            if (idx < 0) return null;
            return new SegkStream(ct.Tables[idx]);
        }

        private SegkStream(ConvSubtable table)
        {
            _table = table;
            _entries = new List<ConvEntry>[table.Segments.Length];
            for (int i = 0; i < _entries.Length; i++)
            {
                _entries[i] = new List<ConvEntry>();
                if (IsEmptyKey(table.Segments[i].Key)) MissingKeys++;
            }
        }

        private static bool IsEmptyKey(byte[] key)
        {
            for (int i = 0; i < 16; i++) if (key[i] != 0) return false;
            return true;
        }

        /// <summary>segment_patchseek: start (again) at a byte offset of the clip, which must be packet-aligned.</summary>
        public bool Seek(ulong offset)
        {
            foreach (var e in _entries) e.Clear();
            _streamSegment = 0;
            _streamEntry = 0;
            _streamOffset = offset;
            _nextPatchOffset = 0;
            return offset % PacketSize == 0;
        }

        /// <summary>ts_parse_desc_0x89: one patch entry from the descriptor at buf[d]. True if it was injected.</summary>
        private bool ParseDescriptor(uint spn, uint spn0, byte[] buf, int d)
        {
            uint spId = (uint)((buf[d + 2] << 8) | buf[d + 3]);
            if (spId >= _table.Segments.Length) return false; // broken table or stream

            var segment = _table.Segments[spId];
            byte[] mask = segment.Key;
            if (IsEmptyKey(mask)) return false;      // libbdplus: "TODO: if mask missing, call VM for computeSP"

            int type = (buf[d + 4] ^ mask[0]) >> 6;
            if (type == 0) return false;             // fuzzing entries (random data)
            if (type == 3) return false;             // invalid flags: incorrect mask?

            // Avoid polluting the tables with incomplete lists.
            if (spId > 0) _entries[spId - 1].Clear();

            // Forensic Mark ID: a type 2 patch only applies if its bit is set in the segment's FM_ID mask.
            if (type == 2)
            {
                int fmIdPos = (buf[d + 4] ^ mask[0]) & 0x3f;
                int fmIdBit = segment.Mask[7 - (fmIdPos >> 3)] & (1 << (fmIdPos & 0x07));
                if (fmIdBit == 0) return false;
            }

            var list = _entries[spId];
            // Drop past entries. Unsigned arithmetic, as in C: at the very start of a clip (spn0 = 0) the
            // limit wraps around and every queued entry of the segment is dropped.
            uint limit = unchecked(spn0 - 1);
            while (list.Count > 0 &&
                   unchecked(list[0].Index + (uint)list[0].Patch0AddressAdjust + (uint)list[0].Patch1AddressAdjust) < limit)
                list.RemoveAt(0);
            if (list.Count >= CacheSize - 1) return false; // stream entry cache overflow

            var entry = new ConvEntry
            {
                Index = spn,
                Flags = (byte)(buf[d + 4] ^ mask[0]),
                Patch0AddressAdjust = (ushort)(((buf[d + 5] ^ mask[1]) << 4) | ((buf[d + 6] ^ mask[2]) >> 4)),
                Patch1AddressAdjust = (ushort)((((buf[d + 6] ^ mask[2]) & 0x0f) << 8) | (buf[d + 7] ^ mask[3])),
                Patch0BufferOffset = (byte)(buf[d + 8] ^ mask[4]),
                Patch1BufferOffset = (byte)(buf[d + 9] ^ mask[5]),
                Active = true,
            };
            for (int i = 0; i < 5; i++)
            {
                entry.Patch0[i] = (byte)(buf[d + 10 + i] ^ mask[6 + i]);
                entry.Patch1[i] = (byte)(buf[d + 15 + i] ^ mask[11 + i]);
            }
            list.Add(entry);
            Injected++;

            _streamSegment = spId;
            _streamEntry = 0;
            _nextPatchOffset = 0;
            return true;
        }

        /// <summary>
        /// segment_patch in stream mode: the next 'len' bytes of the clip are in buf[off..]. Reads the
        /// descriptors they carry, then applies every pending patch that lands in them. Returns the
        /// number of patches applied, or -1 if the data is not packet-aligned.
        /// </summary>
        public int Patch(byte[] buf, int off, int len)
        {
            int patches = 0;
            ulong startOffset = _streamOffset;
            ulong endOffset = _streamOffset + (ulong)len;
            _streamOffset += (ulong)len;

            if (buf[off + 4] != 0x47) return -1; // unaligned data in stream fixup mode

            for (int p = off; p + PacketSize <= off + len; p += PacketSize)
            {
                if (buf[p + 5] == 0x41 && buf[p + 6] == 0x00 && (buf[p + 7] & 0x20) == 0 && buf[p + 4] == 0x47 &&
                    buf[p + 21] == 0x89 && buf[p + 22] == 0x14)
                {
                    ParseDescriptor((uint)((startOffset + (ulong)(p - off)) / PacketSize), (uint)(startOffset / PacketSize), buf, p + 21);
                }
            }

            if (_nextPatchOffset > endOffset) return 0;

            for (uint currseg = _streamSegment; currseg < _entries.Length; currseg++, _streamSegment++)
            {
                var list = _entries[currseg];
                for (uint currentry = _streamEntry; currentry < list.Count; currentry++, _streamEntry++)
                {
                    var entry = list[(int)currentry];
                    if (!entry.Active) continue;
                    ulong offset0 = (ulong)entry.Offset0;

                    // This entry is beyond this buffer: stop here, more data is needed.
                    if (offset0 > endOffset)
                    {
                        _nextPatchOffset = offset0;
                        return patches;
                    }

                    ulong offset1 = (ulong)entry.Offset1;

                    // This entry (patch1) is completely before this buffer: skip to the next.
                    if (offset1 + 5 <= startOffset) continue;

                    patches += Apply(entry.Patch0, offset0, startOffset, endOffset, buf, off, len);

                    if (offset1 > endOffset) return patches;

                    patches += Apply(entry.Patch1, offset1, startOffset, endOffset, buf, off, len);
                }
                _streamEntry = 0;
            }
            // stream_segment stays high, as in libbdplus (resetting it would scan the list forever).
            return patches;
        }

        /// <summary>One 5-byte patch at a clip offset, clipped to the buffer [start, end) as segment_patch does.</summary>
        private static int Apply(byte[] patch, ulong at, ulong start, ulong end, byte[] buf, int off, int len)
        {
            if (at < start)
            {
                // The patch goes over the start of the buffer.
                ulong diff = start - at;
                if (diff < 5)
                {
                    Buffer.BlockCopy(patch, (int)diff, buf, off, 5 - (int)diff);
                    return 1;
                }
                return 0;
            }
            ulong d = end - at;
            if (d < 5)
            {
                // Straddling the end.
                Buffer.BlockCopy(patch, 0, buf, off + len - (int)d, (int)d);
                return 1;
            }
            // Entirely inside.
            Buffer.BlockCopy(patch, 0, buf, off + len - (int)d, 5);
            return 1;
        }
    }
}
